namespace FsVoice.OpenSource

open System
open System.IO

type GemmaRuntimeOptions() =
    member val ModelDir = "models/gemma-4-e2b-it-onnx-mobius/Q4_K_M/cuda" with get, set
    member val Variant = "Q4_K_M/cuda" with get, set
    member val Runtime = "raw-onnx" with get, set
    member val ExecutionProvider = "cuda" with get, set
    member val MaxAudioSeconds = 30.0 with get, set
    member val AsrMaxNewTokens = 128 with get, set
    member val ReasoningMaxNewTokens = 512 with get, set
    member val ToolMaxRounds = 3 with get, set
    member val MaxHistoryTurns = 8 with get, set

type TtsRuntimeOptions() =
    member val ModelDir = "models/chatterbox-onnx" with get, set
    member val Runtime = "chatterbox-onnx" with get, set
    member val ExecutionProvider = "cuda" with get, set
    member val Variant = "q4f16" with get, set
    member val HuggingFaceRepoId = "onnx-community/chatterbox-ONNX" with get, set
    member val VoiceSamplePath = "voices/default_voice.wav" with get, set
    member val VoiceSampleTranscript = "" with get, set
    member val Instruction = "" with get, set
    member val OutputSampleRate = 24000 with get, set
    member val MaxSteps = 256 with get, set
    member val Seed = 12345 with get, set
    member val Exaggeration = 0.5 with get, set
    member val RepetitionPenalty = 1.2 with get, set
    member val StreamingChunkSeconds = 0.5 with get, set
    member val RequireGpu = true with get, set
    member val RequireFullGpu = false with get, set
    member val CudaDeviceId = 0 with get, set
    member val GpuMemoryLimitGb = 0.0 with get, set

type OpenSourceVoiceOptions() =
    member val WorkDir = "served_runs" with get, set
    member val MaxHistoryTurns = 8 with get, set
    member val MaxTurnAudioSeconds = 30.0 with get, set
    member val Gemma = GemmaRuntimeOptions() with get, set
    member val Tts = TtsRuntimeOptions() with get, set

module RuntimePaths =
    let resolveAgainst (basePath: string) (path: string) =
        if String.IsNullOrWhiteSpace path then
            path
        elif Path.IsPathRooted path then
            Path.GetFullPath path
        else
            Path.GetFullPath(Path.Combine(basePath, path))

    let private allExist basePath (relativePaths: string array) =
        relativePaths
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.forall (fun path ->
            let resolved = resolveAgainst basePath path
            Directory.Exists resolved || File.Exists resolved)

    let resolveBaseFromCandidates (candidates: string array) (relativePaths: string array) =
        candidates
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.map Path.GetFullPath
        |> Array.tryFind (fun candidate -> allExist candidate relativePaths)
        |> Option.defaultWith (fun () ->
            candidates
            |> Array.tryHead
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map Path.GetFullPath
            |> Option.defaultValue (Directory.GetCurrentDirectory()))
