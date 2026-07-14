namespace FsVoice.OpenSource

open System
open System.IO

type GemmaRuntimeOptions() =
    member val LlamaCppEndpoint = "http://127.0.0.1:8081" with get, set
    member val LlamaCppModel = "gemma-4-E2B_q4_0-it.gguf" with get, set
    member val LlamaCppRequestTimeoutSeconds = 180 with get, set
    member val LlamaCppHealthTimeoutSeconds = 3 with get, set
    member val ReasoningMaxNewTokens = 512 with get, set
    member val AdaptiveReasoning = true with get, set
    member val FastReasoningMaxNewTokens = 96 with get, set
    member val BalancedReasoningMaxNewTokens = 384 with get, set
    member val ToolMaxRounds = 3 with get, set
    member val FastToolMaxRounds = 1 with get, set
    member val BalancedToolMaxRounds = 2 with get, set
    member val EnableDeterministicToolRouting = true with get, set
    member val EnableRagFirst = false with get, set
    member val RagFirstMaxResults = 8 with get, set
    member val UseStructuredToolFiller = true with get, set
    member val EnableThinking = true with get, set
    member val LogThoughtText = false with get, set

type SttRuntimeOptions() =
    member val Runtime = "parakeet-tdt-onnx" with get, set
    member val ModelDir = "models/parakeet-tdt-0.6b-v3-onnx" with get, set
    member val ExecutionProvider = "cuda" with get, set
    member val Precision = "fp32" with get, set
    member val MaxAudioSeconds = 30.0 with get, set
    member val NumThreads = 4 with get, set
    member val MaxTokensPerStep = 10 with get, set

type VadRuntimeOptions() =
    member val ModelPath = "models/silero-vad-onnx/silero_vad.onnx" with get, set
    member val AllowBargeIn = true with get, set
    member val Threshold = 0.50 with get, set
    member val NegativeThreshold = 0.35 with get, set
    member val MinSpeechDurationMs = 250 with get, set
    member val MinSilenceDurationMs = 700 with get, set
    member val PreRollMs = 300 with get, set
    member val SpeechPadMs = 100 with get, set
    member val NumThreads = 1 with get, set

type TtsRuntimeOptions() =
    member val ModelDir = "models/pocket-tts-onnx-english-2026-04" with get, set
    member val ExecutionProvider = "cpu" with get, set
    member val Precision = "int8" with get, set
    member val VoiceSamplePath = "voices/default_voice.wav" with get, set
    member val Seed = 12345 with get, set
    member val Temperature = 0.7 with get, set
    member val NumThreads = 2 with get, set
    member val NumSteps = 4 with get, set
    member val VoiceEmbeddingCacheCapacity = 4 with get, set
    member val DecoderChunkFrames = 3 with get, set
    member val FirstChunkFrames = 2 with get, set
    member val MaxReferenceAudioSeconds = 12.0 with get, set
    member val TrimReferenceSilence = true with get, set
    member val ReferenceSilenceThresholdDb = -40.0 with get, set
    member val ReferenceSilencePaddingSeconds = 0.05 with get, set

type WebRtcRuntimeOptions() =
    member val BindAddress = "" with get, set
    member val IcePortStart = 0 with get, set
    member val IcePortEnd = 0 with get, set
    member val IncludeAllInterfaceAddresses = false with get, set
    member val GatherTimeoutMs = 1500 with get, set

type IndexRuntimeOptions() =
    member val BundleDirectory = "indexes" with get, set

type AssetRuntimeOptions() =
    member val StatusFile = "" with get, set

type OpenSourceVoiceOptions() =
    member val WorkDir = "served_runs" with get, set
    member val MaxHistoryTurns = 10 with get, set
    member val MaxTurnAudioSeconds = 30.0 with get, set
    member val Gemma = GemmaRuntimeOptions() with get, set
    member val Stt = SttRuntimeOptions() with get, set
    member val Vad = VadRuntimeOptions() with get, set
    member val Tts = TtsRuntimeOptions() with get, set
    member val WebRtc = WebRtcRuntimeOptions() with get, set
    member val Index = IndexRuntimeOptions() with get, set
    member val Assets = AssetRuntimeOptions() with get, set

module RuntimePaths =
    let resolveAgainst (basePath: string) (path: string) =
        if String.IsNullOrWhiteSpace path then path
        elif Path.IsPathRooted path then Path.GetFullPath path
        else Path.GetFullPath(Path.Combine(basePath, path))

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
