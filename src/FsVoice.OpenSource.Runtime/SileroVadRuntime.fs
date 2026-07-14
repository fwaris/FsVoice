namespace FsVoice.OpenSource

open System
open System.IO
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors

type private SileroVadSession(session: InferenceSession) =
    let syncRoot = obj ()
    let mutable state = Array.zeroCreate<float32> (2 * 1 * 128)
    let mutable context = Array.zeroCreate<float32> 64

    let run (samples16k: float32 array) =
        if samples16k.Length <> 512 then
            invalidArg (nameof samples16k) "Silero VAD requires exactly 512 samples at 16 kHz."

        let input = Array.append context samples16k

        let feeds =
            [| NamedOnnxValue.CreateFromTensor("input", DenseTensor<float32>(input, [| 1; input.Length |]))
               NamedOnnxValue.CreateFromTensor("state", DenseTensor<float32>(state, [| 2; 1; 128 |]))
               NamedOnnxValue.CreateFromTensor("sr", DenseTensor<int64>([| 16000L |], Array.empty<int>)) |]

        use outputs = session.Run feeds
        let probability = outputs |> Seq.find (fun value -> value.Name = "output")
        let nextState = outputs |> Seq.find (fun value -> value.Name = "stateN")
        let probabilityValues = probability.AsTensor<float32>() |> Seq.toArray
        let stateValues = nextState.AsTensor<float32>() |> Seq.toArray

        if probabilityValues.Length <> 1 || stateValues.Length <> state.Length then
            invalidOp "Silero VAD returned incompatible output tensor shapes."

        state <- stateValues
        context <- input[input.Length - 64 ..]
        probabilityValues[0]

    interface IVadSession with
        member _.Reset() =
            lock syncRoot (fun () ->
                Array.Clear(state, 0, state.Length)
                Array.Clear(context, 0, context.Length))

        member _.SpeechProbability(samples16k) =
            lock syncRoot (fun () -> run samples16k)

type SileroVadRuntime(options: VadRuntimeOptions, pathBase: string) =
    let modelVersion = "6.2.1"
    let modelPath = RuntimePaths.resolveAgainst pathBase options.ModelPath

    let validateOptions () =
        if options.Threshold <= 0.0 || options.Threshold > 1.0 then
            invalidArg (nameof options.Threshold) "OpenSourceVoice:Vad:Threshold must be greater than 0 and at most 1."

        if
            options.NegativeThreshold < 0.0
            || options.NegativeThreshold >= options.Threshold
        then
            invalidArg
                (nameof options.NegativeThreshold)
                "OpenSourceVoice:Vad:NegativeThreshold must be non-negative and below Threshold."

        for name, value in
            [ nameof options.MinSpeechDurationMs, options.MinSpeechDurationMs
              nameof options.MinSilenceDurationMs, options.MinSilenceDurationMs
              nameof options.PreRollMs, options.PreRollMs
              nameof options.SpeechPadMs, options.SpeechPadMs ] do
            if value < 0 then
                invalidArg name $"OpenSourceVoice:Vad:{name} must be non-negative."

        if options.MinSpeechDurationMs = 0 then
            invalidArg (nameof options.MinSpeechDurationMs) "OpenSourceVoice:Vad:MinSpeechDurationMs must be positive."

        if options.MinSilenceDurationMs = 0 then
            invalidArg
                (nameof options.MinSilenceDurationMs)
                "OpenSourceVoice:Vad:MinSilenceDurationMs must be positive."

        if options.NumThreads <= 0 then
            invalidArg (nameof options.NumThreads) "OpenSourceVoice:Vad:NumThreads must be positive."

    do validateOptions ()

    do
        if not (File.Exists modelPath) then
            invalidOp
                $"Silero VAD model was not found: {modelPath}. Run scripts/download-silero-vad-onnx-assets.ps1 for the shared VoiceAgent_assets directory."

    let sessionOptions = new SessionOptions()

    do
        sessionOptions.GraphOptimizationLevel <- GraphOptimizationLevel.ORT_ENABLE_ALL
        sessionOptions.IntraOpNumThreads <- options.NumThreads
        sessionOptions.InterOpNumThreads <- 1

    let session =
        try
            new InferenceSession(modelPath, sessionOptions)
        with ex ->
            sessionOptions.Dispose()

            raise (
                InvalidOperationException($"Silero VAD model could not be loaded from {modelPath}: {ex.Message}", ex)
            )

    let requireNames (kind: string) (expected: string array) (actual: System.Collections.Generic.IEnumerable<string>) =
        let missing =
            expected |> Array.filter (fun name -> not (actual |> Seq.contains name))

        if missing.Length > 0 then
            let missingNames = String.Join(", ", missing)
            invalidOp $"Silero VAD {kind} is missing required tensor(s): {missingNames}. Model: {modelPath}"

    do
        try
            requireNames "input metadata" [| "input"; "state"; "sr" |] session.InputMetadata.Keys
            requireNames "output metadata" [| "output"; "stateN" |] session.OutputMetadata.Keys
            let validationSession = SileroVadSession(session) :> IVadSession
            let probability = validationSession.SpeechProbability(Array.zeroCreate 512)

            if not (Single.IsFinite probability) || probability < 0.0f || probability > 1.0f then
                invalidOp $"Silero VAD startup inference returned invalid probability {probability}. Model: {modelPath}"
        with ex ->
            session.Dispose()
            sessionOptions.Dispose()
            raise (InvalidOperationException($"Silero VAD startup validation failed for {modelPath}: {ex.Message}", ex))

    interface IVadRuntime with
        member _.Status() =
            { Ready = true
              Runtime = "silero-vad-onnx"
              ModelPath = modelPath
              ModelVersion = modelVersion
              ExecutionProvider = "cpu"
              InputSampleRate = 16000
              FrameSamples = 512
              AllowBargeIn = options.AllowBargeIn
              Threshold = options.Threshold
              NegativeThreshold = options.NegativeThreshold
              MinSpeechDurationMs = options.MinSpeechDurationMs
              MinSilenceDurationMs = options.MinSilenceDurationMs
              PreRollMs = options.PreRollMs
              SpeechPadMs = options.SpeechPadMs
              Message = $"Silero VAD {modelVersion} is ready on CPU." }

        member _.CreateSession() =
            SileroVadSession(session) :> IVadSession

    interface IDisposable with
        member _.Dispose() =
            session.Dispose()
            sessionOptions.Dispose()
