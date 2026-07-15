namespace FsVoice.OpenSource

open System
open System.IO
open System.Text

module AudioPcm =
    let clamp value =
        if value < -1.0f then -1.0f
        elif value > 1.0f then 1.0f
        else value

    let float32ToLittleEndianBytes (samples: float32 array) =
        let bytes = Array.zeroCreate<byte> (samples.Length * sizeof<float32>)
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length)
        bytes

    let float32FromLittleEndianBytes (bytes: byte array) =
        if bytes.Length % sizeof<float32> <> 0 then
            invalidArg (nameof bytes) "Float32 PCM payload length must be a multiple of 4 bytes."

        let samples = Array.zeroCreate<float32> (bytes.Length / sizeof<float32>)
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length)
        samples

    let float32ToPcm16 (samples: float32 array) =
        samples
        |> Array.map (fun sample ->
            let value = clamp sample |> float
            int16 (Math.Round(value * 32767.0)))

    let pcm16ToFloat32 (samples: int16 array) =
        samples |> Array.map (fun sample -> float32 sample / 32768.0f)

    let resampleLinear sourceRate targetRate (samples: float32 array) =
        if sourceRate = targetRate then
            Array.copy samples
        elif samples.Length = 0 then
            Array.empty
        else
            let outputLength =
                max 1 (int (Math.Round(float samples.Length * float targetRate / float sourceRate)))

            let output = Array.zeroCreate<float32> outputLength
            let scale = float sourceRate / float targetRate

            for index in 0 .. outputLength - 1 do
                let source = float index * scale
                let left = int (Math.Floor source)
                let right = min (samples.Length - 1) (left + 1)
                let mix = source - float left
                output[index] <- samples[left] * float32 (1.0 - mix) + samples[right] * float32 mix

            output

    /// Stateful linear resampling for live audio. The final source sample is
    /// retained until the following chunk so interpolation remains continuous
    /// across arbitrary transport packet boundaries.
    type StreamingLinearResampler(sourceRate: int, targetRate: int) =
        do
            if sourceRate <= 0 then
                invalidArg (nameof sourceRate) "The source sample rate must be positive."

            if targetRate <= 0 then
                invalidArg (nameof targetRate) "The target sample rate must be positive."

        let sourcePerOutput = float sourceRate / float targetRate
        let mutable sourceSamplesSeen = 0L
        let mutable nextOutputPosition = 0.0
        let mutable previousSample = 0.0f
        let mutable hasPreviousSample = false

        member _.SourceRate = sourceRate
        member _.TargetRate = targetRate

        member _.Reset() =
            sourceSamplesSeen <- 0L
            nextOutputPosition <- 0.0
            previousSample <- 0.0f
            hasPreviousSample <- false

        member _.Append(samples: float32 array) =
            if samples.Length = 0 then
                Array.empty
            elif sourceRate = targetRate then
                sourceSamplesSeen <- sourceSamplesSeen + int64 samples.Length
                previousSample <- samples[samples.Length - 1]
                hasPreviousSample <- true
                Array.copy samples
            else
                let chunkStart = sourceSamplesSeen
                let chunkEndExclusive = chunkStart + int64 samples.Length
                let output = ResizeArray<float32>()

                let sampleAt globalIndex =
                    if globalIndex = chunkStart - 1L && hasPreviousSample then
                        previousSample
                    else
                        samples[int (globalIndex - chunkStart)]

                let mutable keepGoing = true

                while keepGoing do
                    let leftIndex = int64 (Math.Floor nextOutputPosition)
                    let rightIndex = leftIndex + 1L

                    if rightIndex >= chunkEndExclusive || leftIndex < chunkStart - 1L then
                        keepGoing <- false
                    else
                        let fraction = float32 (nextOutputPosition - float leftIndex)
                        let left = sampleAt leftIndex
                        let right = sampleAt rightIndex
                        output.Add(left + (right - left) * fraction)
                        nextOutputPosition <- nextOutputPosition + sourcePerOutput

                sourceSamplesSeen <- chunkEndExclusive
                previousSample <- samples[samples.Length - 1]
                hasPreviousSample <- true
                output.ToArray()

    /// Band-limited windowed-sinc resampling for reference audio where
    /// preserving speaker characteristics matters more than minimal latency.
    let resampleBandLimited sourceRate targetRate (samples: float32 array) =
        if sourceRate = targetRate then
            Array.copy samples
        elif sourceRate <= 0 || targetRate <= 0 then
            invalidArg (nameof sourceRate) "Audio sample rates must be positive."
        elif samples.Length = 0 then
            Array.empty
        else
            let outputLength =
                max 1 (int (Math.Round(float samples.Length * float targetRate / float sourceRate)))

            let output = Array.zeroCreate<float32> outputLength
            let sourcePerOutput = float sourceRate / float targetRate
            let radius = 32
            let cutoff = 0.475 * min 1.0 (float targetRate / float sourceRate)

            let sinc (value: float) =
                if Math.Abs value < 1e-12 then
                    1.0
                else
                    Math.Sin(Math.PI * value) / (Math.PI * value)

            for outputIndex in 0 .. outputLength - 1 do
                let sourcePosition = float outputIndex * sourcePerOutput
                let center = int (Math.Floor sourcePosition)
                let mutable weighted = 0.0
                let mutable weightSum = 0.0

                for sourceIndex in center - radius + 1 .. center + radius do
                    if sourceIndex >= 0 && sourceIndex < samples.Length then
                        let offset = sourcePosition - float sourceIndex
                        let normalizedOffset = offset / float radius

                        if Math.Abs normalizedOffset < 1.0 then
                            let window = 0.5 + 0.5 * Math.Cos(Math.PI * normalizedOffset)
                            let weight = 2.0 * cutoff * sinc (2.0 * cutoff * offset) * window
                            weighted <- weighted + float samples[sourceIndex] * weight
                            weightSum <- weightSum + weight

                output[outputIndex] <-
                    if Math.Abs weightSum < 1e-12 then
                        samples[min (samples.Length - 1) center]
                    else
                        float32 (weighted / weightSum) |> clamp

            output

    let trimEdgeSilence sampleRate thresholdDb paddingSeconds (samples: float32 array) =
        if samples.Length = 0 then
            Array.empty
        elif sampleRate <= 0 then
            invalidArg (nameof sampleRate) "Audio sample rate must be positive."
        else
            let frameSize = max 1 (sampleRate / 50)
            let frameCount = int (Math.Ceiling(float samples.Length / float frameSize))
            let thresholdSquared = Math.Pow(10.0, thresholdDb / 10.0)

            let isSpeech frame =
                let startIndex = frame * frameSize
                let endIndex = min samples.Length (startIndex + frameSize)
                let mutable sumSquares = 0.0

                for index in startIndex .. endIndex - 1 do
                    let value = float samples[index]
                    sumSquares <- sumSquares + value * value

                endIndex > startIndex
                && sumSquares / float (endIndex - startIndex) >= thresholdSquared

            let firstSpeech = [| 0 .. frameCount - 1 |] |> Array.tryFind isSpeech
            let lastSpeech = [| frameCount - 1 .. -1 .. 0 |] |> Array.tryFind isSpeech

            match firstSpeech, lastSpeech with
            | Some first, Some last ->
                let padding = max 0 (int (Math.Round(max 0.0 paddingSeconds * float sampleRate)))
                let startIndex = max 0 (first * frameSize - padding)
                let endIndex = min samples.Length ((last + 1) * frameSize + padding)
                samples[startIndex .. endIndex - 1]
            | _ -> Array.empty

module Wave =
    let private invalidData message = raise (InvalidDataException(message))

    let readMono (path: string) =
        use stream = File.OpenRead path
        use reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen = false)

        let ascii count =
            Encoding.ASCII.GetString(reader.ReadBytes count)

        if ascii 4 <> "RIFF" then
            invalidData "WAV file is missing RIFF header."

        reader.ReadUInt32() |> ignore

        if ascii 4 <> "WAVE" then
            invalidData "WAV file is missing WAVE header."

        let mutable audioFormat = 0us
        let mutable channels = 0us
        let mutable bitsPerSample = 0us
        let mutable sampleRate = 0
        let mutable foundData = false
        let mutable samples = Array.empty<float32>

        while stream.Position + 8L <= stream.Length && not foundData do
            let chunkId = ascii 4
            let chunkSize = reader.ReadUInt32()
            let chunkStart = stream.Position

            match chunkId with
            | "fmt " ->
                audioFormat <- reader.ReadUInt16()
                channels <- reader.ReadUInt16()
                sampleRate <- reader.ReadInt32()
                reader.ReadUInt32() |> ignore
                reader.ReadUInt16() |> ignore
                bitsPerSample <- reader.ReadUInt16()
                stream.Position <- chunkStart + int64 chunkSize
            | "data" ->
                if channels = 0us then
                    invalidData "WAV fmt chunk must appear before data."

                let raw = reader.ReadBytes(int chunkSize)
                let bytesPerSample = int bitsPerSample / 8
                let frameCount = raw.Length / (bytesPerSample * int channels)
                samples <- Array.zeroCreate<float32> frameCount

                match audioFormat, bitsPerSample with
                | 1us, 16us ->
                    let values = Array.zeroCreate<int16> (raw.Length / sizeof<int16>)
                    Buffer.BlockCopy(raw, 0, values, 0, raw.Length)

                    for frame in 0 .. frameCount - 1 do
                        let mutable sum = 0.0f

                        for channel in 0 .. int channels - 1 do
                            sum <- sum + float32 values[frame * int channels + channel] / 32768.0f

                        samples[frame] <- sum / float32 channels
                | 3us, 32us ->
                    let values = Array.zeroCreate<float32> (raw.Length / sizeof<float32>)
                    Buffer.BlockCopy(raw, 0, values, 0, raw.Length)

                    for frame in 0 .. frameCount - 1 do
                        let mutable sum = 0.0f

                        for channel in 0 .. int channels - 1 do
                            sum <- sum + values[frame * int channels + channel]

                        samples[frame] <- sum / float32 channels
                | _ -> invalidData $"Unsupported WAV format {audioFormat} with {bitsPerSample} bits per sample."

                foundData <- true
            | _ -> stream.Position <- chunkStart + int64 chunkSize

            if chunkSize % 2u = 1u && stream.Position < stream.Length then
                stream.Position <- stream.Position + 1L

        if not foundData then
            invalidData "WAV file has no data chunk."

        sampleRate, samples

    let writeMono16 (path: string) (sampleRate: int) (samples: float32 array) =
        match Path.GetDirectoryName path with
        | null
        | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        use stream = File.Create path
        use writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen = false)
        let dataBytes = samples.Length * sizeof<int16>

        let writeAscii (text: string) =
            writer.Write(Encoding.ASCII.GetBytes text)

        writeAscii "RIFF"
        writer.Write(36 + dataBytes)
        writeAscii "WAVE"
        writeAscii "fmt "
        writer.Write 16
        writer.Write(int16 1)
        writer.Write(int16 1)
        writer.Write(sampleRate: int)
        writer.Write(sampleRate * sizeof<int16>)
        writer.Write(int16 sizeof<int16>)
        writer.Write(int16 16)
        writeAscii "data"
        writer.Write dataBytes

        for sample in samples do
            let value = AudioPcm.clamp sample |> float
            writer.Write(int16 (Math.Round(value * 32767.0)))

    let stats name sampleRate (samples: float32 array) =
        if samples.Length = 0 then
            $"{name}=0.00s peak=0.0000 rms=0.0000 meanAbs=0.0000"
        else
            let mutable peak = 0.0
            let mutable sumAbs = 0.0
            let mutable sumSq = 0.0

            for sample in samples do
                let value = float sample
                let absValue = Math.Abs value
                peak <- max peak absValue
                sumAbs <- sumAbs + absValue
                sumSq <- sumSq + value * value

            let duration = float samples.Length / float sampleRate
            let rms = Math.Sqrt(sumSq / float samples.Length)
            let meanAbs = sumAbs / float samples.Length
            sprintf "%s=%0.2fs peak=%0.4f rms=%0.4f meanAbs=%0.4f" name duration peak rms meanAbs
