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

module Wave =
    let private invalidData message = raise (InvalidDataException(message))

    let readMono (path: string) =
        use stream = File.OpenRead path
        use reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen = false)
        let ascii count = Encoding.ASCII.GetString(reader.ReadBytes count)
        if ascii 4 <> "RIFF" then invalidData "WAV file is missing RIFF header."
        reader.ReadUInt32() |> ignore
        if ascii 4 <> "WAVE" then invalidData "WAV file is missing WAVE header."

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
                if channels = 0us then invalidData "WAV fmt chunk must appear before data."
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
                | _ ->
                    invalidData $"Unsupported WAV format {audioFormat} with {bitsPerSample} bits per sample."
                foundData <- true
            | _ ->
                stream.Position <- chunkStart + int64 chunkSize
            if chunkSize % 2u = 1u && stream.Position < stream.Length then
                stream.Position <- stream.Position + 1L

        if not foundData then invalidData "WAV file has no data chunk."
        sampleRate, samples

    let writeMono16 (path: string) (sampleRate: int) (samples: float32 array) =
        match Path.GetDirectoryName path with
        | null | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        use stream = File.Create path
        use writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen = false)
        let dataBytes = samples.Length * sizeof<int16>
        let writeAscii (text: string) = writer.Write(Encoding.ASCII.GetBytes text)
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
