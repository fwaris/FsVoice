namespace FsVoice.OpenSource

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Linq
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors

module private ParakeetErrors =
    let invalidData (message: string) : 'T = raise (InvalidDataException message)

[<RequireQualifiedAccess>]
type private ParakeetPrecision =
    | Fp32
    | Int8

module private ParakeetPrecision =
    let parse (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "fp32"
        | "float32" -> ParakeetPrecision.Fp32
        | "int8" -> ParakeetPrecision.Int8
        | other -> invalidArg (nameof value) $"Parakeet precision must be fp32 or int8, not '{other}'."

    let name =
        function
        | ParakeetPrecision.Fp32 -> "fp32"
        | ParakeetPrecision.Int8 -> "int8"

[<RequireQualifiedAccess>]
type private ParakeetExecutionProvider =
    | Cpu
    | Cuda

module private ParakeetExecutionProvider =
    let parse (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "cpu" -> ParakeetExecutionProvider.Cpu
        | "cuda" -> ParakeetExecutionProvider.Cuda
        | other -> invalidArg (nameof value) $"Parakeet execution provider must be cpu or cuda, not '{other}'."

    let name =
        function
        | ParakeetExecutionProvider.Cpu -> "cpu"
        | ParakeetExecutionProvider.Cuda -> "cuda"

type private ParakeetModelFiles =
    { Config: string
      Preprocessor: string
      Encoder: string
      EncoderData: string option
      DecoderJoint: string
      Vocabulary: string }

module private ParakeetModelFiles =
    let create precision modelDir =
        let encoderName, encoderData, decoderName =
            match precision with
            | ParakeetPrecision.Fp32 ->
                "encoder-model.onnx",
                Some(Path.Combine(modelDir, "encoder-model.onnx.data")),
                "decoder_joint-model.onnx"
            | ParakeetPrecision.Int8 -> "encoder-model.int8.onnx", None, "decoder_joint-model.int8.onnx"

        { Config = Path.Combine(modelDir, "config.json")
          Preprocessor = Path.Combine(modelDir, "nemo128.onnx")
          Encoder = Path.Combine(modelDir, encoderName)
          EncoderData = encoderData
          DecoderJoint = Path.Combine(modelDir, decoderName)
          Vocabulary = Path.Combine(modelDir, "vocab.txt") }

    let required files =
        [| files.Config
           files.Preprocessor
           files.Encoder
           yield! files.EncoderData |> Option.toArray
           files.DecoderJoint
           files.Vocabulary |]

type private ParakeetVocabulary = { Tokens: string array; BlankId: int }

module private ParakeetVocabulary =
    let private decodeSpacePattern =
        Regex(@"\A\s|\s\B|(\s)\b", RegexOptions.CultureInvariant)

    let private parseLine (line: string) =
        let separator = line.LastIndexOf(' ')

        if separator <= 0 || separator >= line.Length - 1 then
            ParakeetErrors.invalidData $"Invalid Parakeet vocabulary line: '{line}'."

        let token = line.Substring(0, separator).Replace("\u2581", " ")

        match Int32.TryParse(line.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, tokenId -> tokenId, token
        | false, _ -> ParakeetErrors.invalidData $"Invalid Parakeet vocabulary id: '{line}'."

    let load path =
        let entries = File.ReadAllLines path |> Array.map parseLine

        if entries.Length = 0 then
            ParakeetErrors.invalidData $"Parakeet vocabulary is empty: {path}"

        let maxTokenId = entries |> Array.maxBy fst |> fst
        let tokens = Array.create (maxTokenId + 1) ""

        for tokenId, token in entries do
            if tokenId < 0 || tokenId >= tokens.Length then
                ParakeetErrors.invalidData $"Parakeet vocabulary id is out of range: {tokenId}."

            tokens[tokenId] <- token

        let blankId =
            entries
            |> Array.tryFind (snd >> (=) "<blk>")
            |> Option.map fst
            |> Option.defaultWith (fun () ->
                ParakeetErrors.invalidData $"Parakeet vocabulary has no <blk> token: {path}")

        { Tokens = tokens; BlankId = blankId }

    let decode vocabulary (tokenIds: ResizeArray<int>) =
        let text =
            tokenIds
            |> Seq.map (fun tokenId ->
                if tokenId < 0 || tokenId >= vocabulary.Tokens.Length then
                    ParakeetErrors.invalidData
                        $"Parakeet emitted vocabulary id {tokenId}, outside 0-{vocabulary.Tokens.Length - 1}."

                vocabulary.Tokens[tokenId])
            |> String.concat ""

        decodeSpacePattern.Replace(text, MatchEvaluator(fun value -> if value.Groups[1].Success then " " else ""))
        |> _.Trim()

type private ParakeetDecoderState =
    { First: float32 array
      FirstShape: int array
      Second: float32 array
      SecondShape: int array }

type private ParakeetLoadedSessions =
    { Preprocessor: InferenceSession
      Encoder: InferenceSession
      DecoderJoint: InferenceSession
      SessionOptions: SessionOptions array
      Vocabulary: ParakeetVocabulary }

    interface IDisposable with
        member this.Dispose() =
            this.Preprocessor.Dispose()
            this.Encoder.Dispose()
            this.DecoderJoint.Dispose()
            this.SessionOptions |> Array.iter _.Dispose()

type ParakeetSttRuntime(options: SttRuntimeOptions, pathBase: string) =
    let modelDir = RuntimePaths.resolveAgainst pathBase options.ModelDir
    let precision = ParakeetPrecision.parse options.Precision
    let executionProvider = ParakeetExecutionProvider.parse options.ExecutionProvider
    let files = ParakeetModelFiles.create precision modelDir
    let maxAudioSeconds = max 0.1 options.MaxAudioSeconds
    let numThreads = max 1 options.NumThreads
    let maxTokensPerStep = max 1 options.MaxTokensPerStep
    let syncRoot = obj ()
    let mutable loaded: ParakeetLoadedSessions option = None

    let missingFiles () =
        files |> ParakeetModelFiles.required |> Array.filter (File.Exists >> not)

    let createSessionOptions provider =
        let sessionOptions = new SessionOptions()
        sessionOptions.GraphOptimizationLevel <- GraphOptimizationLevel.ORT_ENABLE_ALL
        sessionOptions.LogSeverityLevel <- OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        sessionOptions.IntraOpNumThreads <- numThreads
        sessionOptions.InterOpNumThreads <- 1

        match provider with
        | ParakeetExecutionProvider.Cpu -> sessionOptions.AppendExecutionProvider_CPU(0)
        | ParakeetExecutionProvider.Cuda ->
            use cudaOptions = new OrtCUDAProviderOptions()

            cudaOptions.UpdateOptions(
                Dictionary<string, string>(
                    dict
                        [ "device_id", "0"
                          "do_copy_in_default_stream", "1"
                          "enable_cuda_graph", "0"
                          "use_tf32", "1" ]
                )
            )

            sessionOptions.AppendExecutionProvider_CUDA(cudaOptions)

        sessionOptions

    let loadSessions () =
        match loaded with
        | Some sessions -> sessions
        | None ->
            lock syncRoot (fun () ->
                match loaded with
                | Some sessions -> sessions
                | None ->
                    let missing = missingFiles ()

                    if missing.Length > 0 then
                        let missingText = String.Join(", ", missing)
                        invalidOp $"Parakeet ONNX model is not ready. Missing files: {missingText}"

                    let preprocessorOptions = createSessionOptions ParakeetExecutionProvider.Cpu
                    let encoderOptions = createSessionOptions executionProvider
                    let decoderOptions = createSessionOptions executionProvider

                    try
                        let sessions =
                            { Preprocessor = new InferenceSession(files.Preprocessor, preprocessorOptions)
                              Encoder = new InferenceSession(files.Encoder, encoderOptions)
                              DecoderJoint = new InferenceSession(files.DecoderJoint, decoderOptions)
                              SessionOptions = [| preprocessorOptions; encoderOptions; decoderOptions |]
                              Vocabulary = ParakeetVocabulary.load files.Vocabulary }

                        loaded <- Some sessions
                        sessions
                    with _ ->
                        preprocessorOptions.Dispose()
                        encoderOptions.Dispose()
                        decoderOptions.Dispose()
                        reraise ())

    let result name (results: IDisposableReadOnlyCollection<DisposableNamedOnnxValue>) =
        results
        |> Seq.tryFind (fun value -> String.Equals(value.Name, name, StringComparison.Ordinal))
        |> Option.defaultWith (fun () -> ParakeetErrors.invalidData $"Parakeet ONNX output '{name}' was not returned.")

    let cloneFloatTensor name (results: IDisposableReadOnlyCollection<DisposableNamedOnnxValue>) =
        let tensor = (result name results).AsTensor<float32>()
        DenseTensor<float32>(tensor.ToArray(), tensor.Dimensions.ToArray())

    let cloneInt64Tensor name (results: IDisposableReadOnlyCollection<DisposableNamedOnnxValue>) =
        let tensor = (result name results).AsTensor<int64>()
        DenseTensor<int64>(tensor.ToArray(), tensor.Dimensions.ToArray())

    let preprocess (sessions: ParakeetLoadedSessions) (waveform16k: float32 array) =
        let waveform =
            DenseTensor<float32>(Memory<float32>(waveform16k), [| 1; waveform16k.Length |])

        let waveformLength = DenseTensor<int64>([| int64 waveform16k.Length |], [| 1 |])

        use outputs =
            sessions.Preprocessor.Run(
                [| NamedOnnxValue.CreateFromTensor("waveforms", waveform)
                   NamedOnnxValue.CreateFromTensor("waveforms_lens", waveformLength) |]
            )

        cloneFloatTensor "features" outputs, cloneInt64Tensor "features_lens" outputs

    let encode (sessions: ParakeetLoadedSessions) features featureLengths =
        use outputs =
            sessions.Encoder.Run(
                [| NamedOnnxValue.CreateFromTensor("audio_signal", features)
                   NamedOnnxValue.CreateFromTensor("length", featureLengths) |]
            )

        cloneFloatTensor "outputs" outputs, cloneInt64Tensor "encoded_lengths" outputs

    let stateShape (session: InferenceSession) name =
        session.InputMetadata[name].Dimensions
        |> Array.map (fun dimension -> if dimension > 0 then dimension else 1)

    let emptyDecoderState (session: InferenceSession) =
        let firstShape = stateShape session "input_states_1"
        let secondShape = stateShape session "input_states_2"

        { First = Array.zeroCreate (firstShape |> Array.reduce (*))
          FirstShape = firstShape
          Second = Array.zeroCreate (secondShape |> Array.reduce (*))
          SecondShape = secondShape }

    let decodeStep
        (sessions: ParakeetLoadedSessions)
        (state: ParakeetDecoderState)
        previousToken
        (encoderFrame: float32 array)
        =
        let encoder = DenseTensor<float32>(encoderFrame, [| 1; encoderFrame.Length; 1 |])
        let target = DenseTensor<int32>([| int32 previousToken |], [| 1; 1 |])
        let targetLength = DenseTensor<int32>([| 1 |], [| 1 |])
        let firstState = DenseTensor<float32>(state.First, state.FirstShape)
        let secondState = DenseTensor<float32>(state.Second, state.SecondShape)

        use outputs =
            sessions.DecoderJoint.Run(
                [| NamedOnnxValue.CreateFromTensor("encoder_outputs", encoder)
                   NamedOnnxValue.CreateFromTensor("targets", target)
                   NamedOnnxValue.CreateFromTensor("target_length", targetLength)
                   NamedOnnxValue.CreateFromTensor("input_states_1", firstState)
                   NamedOnnxValue.CreateFromTensor("input_states_2", secondState) |]
            )

        let logits = (result "outputs" outputs).AsTensor<float32>().ToArray()
        let nextFirst = cloneFloatTensor "output_states_1" outputs
        let nextSecond = cloneFloatTensor "output_states_2" outputs

        logits,
        { First = nextFirst.ToArray()
          FirstShape = nextFirst.Dimensions.ToArray()
          Second = nextSecond.ToArray()
          SecondShape = nextSecond.Dimensions.ToArray() }

    let argMax (values: float32 array) offset count =
        if count <= 0 then
            invalidArg (nameof count) "Parakeet argmax input must not be empty."

        let mutable selectedIndex = 0
        let mutable selectedValue = values[offset]

        for index in 1 .. count - 1 do
            let value = values[offset + index]

            if value > selectedValue then
                selectedIndex <- index
                selectedValue <- value

        selectedIndex

    let decode
        (sessions: ParakeetLoadedSessions)
        (encoded: DenseTensor<float32>)
        encodedLength
        (cancellationToken: CancellationToken)
        =
        let dimensions = encoded.Dimensions.ToArray()

        if dimensions.Length <> 3 || dimensions[0] <> 1 then
            let shapeText = String.Join(", ", dimensions)

            ParakeetErrors.invalidData
                $"Parakeet encoder output must have shape [1, hidden, time], found [{shapeText}]."

        let hiddenSize = dimensions[1]
        let availableFrames = dimensions[2]
        let frameCount = min availableFrames encodedLength
        let encodedValues = encoded.ToArray()
        let vocabulary = sessions.Vocabulary
        let tokenIds = ResizeArray<int>()
        let mutable state = emptyDecoderState sessions.DecoderJoint
        let mutable frameIndex = 0
        let mutable emittedTokens = 0

        while frameIndex < frameCount do
            cancellationToken.ThrowIfCancellationRequested()

            let frame =
                Array.init hiddenSize (fun hiddenIndex -> encodedValues[hiddenIndex * availableFrames + frameIndex])

            let previousToken =
                if tokenIds.Count = 0 then
                    vocabulary.BlankId
                else
                    tokenIds[tokenIds.Count - 1]

            let logits, nextState = decodeStep sessions state previousToken frame

            if logits.Length <= vocabulary.Tokens.Length then
                ParakeetErrors.invalidData
                    $"Parakeet decoder returned {logits.Length} values for a {vocabulary.Tokens.Length}-token vocabulary without duration logits."

            let tokenId = argMax logits 0 vocabulary.Tokens.Length

            let duration =
                argMax logits vocabulary.Tokens.Length (logits.Length - vocabulary.Tokens.Length)

            if tokenId <> vocabulary.BlankId then
                state <- nextState
                tokenIds.Add tokenId
                emittedTokens <- emittedTokens + 1

            if duration > 0 then
                frameIndex <- frameIndex + duration
                emittedTokens <- 0
            elif tokenId = vocabulary.BlankId || emittedTokens >= maxTokensPerStep then
                frameIndex <- frameIndex + 1
                emittedTokens <- 0

        ParakeetVocabulary.decode vocabulary tokenIds

    let transcribe (waveform16k: float32 array) (cancellationToken: CancellationToken) =
        let sessions = loadSessions ()
        let preprocessStopwatch = Stopwatch.StartNew()
        let features, featureLengths = preprocess sessions waveform16k
        preprocessStopwatch.Stop()

        let encoderStopwatch = Stopwatch.StartNew()
        let encoded, encodedLengths = encode sessions features featureLengths
        encoderStopwatch.Stop()

        let encodedLength =
            let values = encodedLengths.ToArray()

            if values.Length = 0 then
                ParakeetErrors.invalidData "Parakeet encoder returned no encoded length."
            else
                int values[0]

        let decoderStopwatch = Stopwatch.StartNew()
        let transcript = decode sessions encoded encodedLength cancellationToken
        decoderStopwatch.Stop()

        transcript,
        $"Parakeet TDT completed; precision={ParakeetPrecision.name precision}; provider={ParakeetExecutionProvider.name executionProvider}; preprocess_ms={preprocessStopwatch.Elapsed.TotalMilliseconds:F1}; encoder_ms={encoderStopwatch.Elapsed.TotalMilliseconds:F1}; decoder_ms={decoderStopwatch.Elapsed.TotalMilliseconds:F1}."

    interface ISttRuntime with
        member _.Status() =
            let missing = missingFiles ()

            { Ready = missing.Length = 0
              Runtime = "parakeet-tdt-onnx"
              InputSampleRate = 24000
              OutputLanguage = "auto"
              Message =
                if missing.Length = 0 then
                    let loadState = if loaded.IsSome then "loaded" else "load-on-first-use"

                    $"Parakeet TDT ONNX is ready; model={modelDir}; precision={ParakeetPrecision.name precision}; provider={ParakeetExecutionProvider.name executionProvider}; sessions={loadState}."
                else
                    let missingText = String.Join(", ", missing)
                    $"Parakeet TDT ONNX is not ready. Missing: {missingText}" }

        member this.TranscribeAsync(samples24k, _outputDirectory, cancellationToken) =
            task {
                let status = (this :> ISttRuntime).Status()

                if not status.Ready then
                    invalidOp status.Message

                let stopwatch = Stopwatch.StartNew()
                let maxSamples = int (Math.Ceiling(maxAudioSeconds * 24000.0))

                let truncated =
                    if samples24k.Length > maxSamples then
                        samples24k[0 .. maxSamples - 1]
                    else
                        samples24k

                let waveform16k = AudioPcm.resampleBandLimited 24000 16000 truncated

                let! transcript, message =
                    Task.Run(
                        Func<string * string>(fun () -> transcribe waveform16k cancellationToken),
                        cancellationToken
                    )

                stopwatch.Stop()

                return
                    { Transcript = transcript
                      InputSampleRate = 24000
                      InputSamples = truncated.Length
                      DurationMs = stopwatch.Elapsed.TotalMilliseconds
                      Message = message }
            }

    interface IDisposable with
        member _.Dispose() =
            lock syncRoot (fun () ->
                loaded |> Option.iter (fun sessions -> (sessions :> IDisposable).Dispose())
                loaded <- None)
