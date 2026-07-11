namespace FsVoice.OpenSource

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open SherpaOnnx

type private PocketTtsReference =
    { Key: string
      SampleRate: int
      Samples: float32 array }

type PocketTtsOnnxRuntime(options: TtsRuntimeOptions, pathBase: string) =
    let modelDir = RuntimePaths.resolveAgainst pathBase options.ModelDir

    let executionProvider =
        if String.IsNullOrWhiteSpace options.ExecutionProvider then
            "cpu"
        else
            options.ExecutionProvider.Trim().ToLowerInvariant()

    let outputSampleRate = 24000
    let numThreads = max 1 options.NumThreads
    let numSteps = max 1 options.NumSteps
    let cacheCapacity = max 0 options.VoiceEmbeddingCacheCapacity
    let maxReferenceAudioSeconds = max 1.0 options.MaxReferenceAudioSeconds
    let synthesisGate = new SemaphoreSlim(1, 1)
    let syncRoot = obj ()
    let mutable engine: OfflineTts option = None
    let mutable cachedReference: PocketTtsReference option = None

    let modelPath name = Path.Combine(modelDir, name)

    let requiredFiles () =
        [| modelPath "lm_flow.int8.onnx"
           modelPath "lm_main.int8.onnx"
           modelPath "encoder.onnx"
           modelPath "decoder.int8.onnx"
           modelPath "text_conditioner.onnx"
           modelPath "vocab.json"
           modelPath "token_scores.json" |]

    let modelDefaultVoice () =
        Path.Combine(modelDir, "test_wavs", "bria.wav")

    let configuredVoiceSample () =
        if String.IsNullOrWhiteSpace options.VoiceSamplePath then
            modelDefaultVoice ()
        else
            RuntimePaths.resolveAgainst pathBase options.VoiceSamplePath

    let requestVoiceSample (request: TtsSynthesisRequest) =
        request.VoiceSamplePath
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (RuntimePaths.resolveAgainst pathBase)
        |> Option.defaultValue (configuredVoiceSample ())

    let missingFiles () =
        [| yield! requiredFiles (); yield configuredVoiceSample () |]
        |> Array.distinct
        |> Array.filter (File.Exists >> not)

    let createEngine () =
        if executionProvider <> "cpu" then
            invalidArg
                (nameof options.ExecutionProvider)
                $"Pocket TTS ONNX is CPU-optimized. Use ExecutionProvider=cpu, not '{executionProvider}'."

        let mutable config = OfflineTtsConfig()
        config.Model.Pocket.LmFlow <- modelPath "lm_flow.int8.onnx"
        config.Model.Pocket.LmMain <- modelPath "lm_main.int8.onnx"
        config.Model.Pocket.Encoder <- modelPath "encoder.onnx"
        config.Model.Pocket.Decoder <- modelPath "decoder.int8.onnx"
        config.Model.Pocket.TextConditioner <- modelPath "text_conditioner.onnx"
        config.Model.Pocket.VocabJson <- modelPath "vocab.json"
        config.Model.Pocket.TokenScoresJson <- modelPath "token_scores.json"
        config.Model.Pocket.VoiceEmbeddingCacheCapacity <- cacheCapacity
        config.Model.NumThreads <- numThreads
        config.Model.Debug <- if options.Debug then 1 else 0
        config.Model.Provider <- "cpu"
        config.MaxNumSentences <- 1
        config.SilenceScale <- 0.2f
        new OfflineTts(config)

    let loadEngine () =
        match engine with
        | Some value -> value
        | None ->
            lock syncRoot (fun () ->
                match engine with
                | Some value -> value
                | None ->
                    let missing = missingFiles ()

                    if missing.Length > 0 then
                        let missingText = String.Join(", ", missing)
                        invalidOp $"Pocket TTS ONNX is not ready. Missing: {missingText}"

                    let value = createEngine ()
                    engine <- Some value
                    value)

    let loadReference path =
        let info = FileInfo path
        let key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}"

        match cachedReference with
        | Some value when value.Key = key -> value
        | _ ->
            let sampleRate, samples = Wave.readMono info.FullName
            let maxSamples = int (Math.Ceiling(maxReferenceAudioSeconds * float sampleRate))

            let samples =
                if samples.Length <= maxSamples then
                    samples
                else
                    samples[0 .. maxSamples - 1]

            let value =
                { Key = key
                  SampleRate = sampleRate
                  Samples = samples }

            cachedReference <- Some value
            value

    interface ITtsRuntime with
        member _.Status() =
            let missing = missingFiles ()
            let voiceSample = configuredVoiceSample ()

            { Ready = missing.Length = 0
              SupportsVoiceCloning = true
              SupportsStreaming = true
              Runtime = "pocket-tts-onnx"
              ModelDir = modelDir
              ExecutionProvider = executionProvider
              OutputSampleRate = outputSampleRate
              VoiceSamplePath = voiceSample
              MissingFiles = missing
              Message =
                if missing.Length = 0 then
                    $"Pocket TTS ONNX is ready. threads={numThreads}; steps={numSteps}; voiceSample={voiceSample}."
                else
                    let missingText = String.Join(", ", missing)
                    $"Pocket TTS ONNX is not ready. Missing: {missingText}" }

        member this.SynthesizeAsync(request, emitChunk, cancellationToken) =
            task {
                let status = (this :> ITtsRuntime).Status()

                if not status.Ready then
                    invalidOp status.Message

                let text =
                    if String.IsNullOrWhiteSpace request.Text then
                        "."
                    else
                        request.Text.Trim()

                let referencePath = requestVoiceSample request

                if not (File.Exists referencePath) then
                    invalidOp $"Pocket TTS reference voice was not found: {referencePath}"

                Directory.CreateDirectory request.OutputDirectory |> ignore
                let outputPath = Path.Combine(request.OutputDirectory, request.OutputFileName)
                do! synthesisGate.WaitAsync cancellationToken

                let stopwatch = Stopwatch.StartNew()
                let mutable firstChunkMs: float option = None
                let mutable callbackError: exn option = None

                try
                    let active = loadEngine ()
                    let reference = loadReference referencePath
                    let mutable generation = OfflineTtsGenerationConfig()
                    generation.ReferenceAudio <- reference.Samples
                    generation.ReferenceSampleRate <- reference.SampleRate
                    generation.ReferenceText <- ""
                    generation.NumSteps <- numSteps
                    generation.SilenceScale <- 0.2f
                    generation.Speed <- 1.0f
                    generation.Extra["max_reference_audio_len"] <- int maxReferenceAudioSeconds

                    let callback =
                        OfflineTtsCallbackProgressWithArg(fun samples count _progress _argument ->
                            if cancellationToken.IsCancellationRequested then
                                0
                            else
                                try
                                    let chunk = Array.zeroCreate<float32> count
                                    Marshal.Copy(samples, chunk, 0, count)

                                    if firstChunkMs.IsNone then
                                        firstChunkMs <- Some stopwatch.Elapsed.TotalMilliseconds

                                    let pending = emitChunk chunk
                                    pending.GetAwaiter().GetResult()
                                    1
                                with ex ->
                                    callbackError <- Some ex
                                    0)

                    let! generated =
                        Task.Run((fun () -> active.GenerateWithConfig(text, generation, callback)), cancellationToken)

                    try
                        callbackError |> Option.iter raise
                        cancellationToken.ThrowIfCancellationRequested()
                        let sampleRate = generated.SampleRate
                        let samples = generated.Samples |> Array.map AudioPcm.clamp
                        Wave.writeMono16 outputPath sampleRate samples
                        stopwatch.Stop()

                        let firstChunk =
                            firstChunkMs |> Option.defaultValue stopwatch.Elapsed.TotalMilliseconds

                        let firstChunkText =
                            firstChunk.ToString("0.0", Globalization.CultureInfo.InvariantCulture)

                        let rtf =
                            if samples.Length = 0 then
                                0.0
                            else
                                stopwatch.Elapsed.TotalSeconds / (float samples.Length / float sampleRate)

                        let rtfText = rtf.ToString("0.000", Globalization.CultureInfo.InvariantCulture)

                        return
                            { Phase = request.Phase
                              Text = text
                              OutputPath = Some outputPath
                              SampleRate = sampleRate
                              Samples = samples.Length
                              DurationMs = float samples.Length / float sampleRate * 1000.0
                              InferenceTimeMs = stopwatch.Elapsed.TotalMilliseconds
                              Message =
                                $"Pocket TTS ONNX synthesis completed. firstChunkMs={firstChunkText}; rtf={rtfText}; threads={numThreads}; steps={numSteps}." }
                    finally
                        generated.Dispose()
                finally
                    stopwatch.Stop()
                    synthesisGate.Release() |> ignore
            }

    interface IDisposable with
        member _.Dispose() =
            lock syncRoot (fun () ->
                engine |> Option.iter _.Dispose()
                engine <- None
                cachedReference <- None)

            synthesisGate.Dispose()
