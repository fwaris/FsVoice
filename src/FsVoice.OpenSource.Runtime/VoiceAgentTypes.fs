namespace FsVoice.OpenSource

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type ArtifactInfo =
    { Path: string
      ContentType: string }

type SttRuntimeStatus =
    { Ready: bool
      Runtime: string
      InputSampleRate: int
      OutputLanguage: string
      Message: string }

type SttTranscriptionResult =
    { Transcript: string
      InputSampleRate: int
      InputSamples: int
      DurationMs: float
      Message: string }

type ISttRuntime =
    abstract Status: unit -> SttRuntimeStatus

    abstract TranscribeAsync:
        samples24k: float32 array *
        outputDirectory: string *
        cancellationToken: CancellationToken -> Task<SttTranscriptionResult>

type TtsRuntimeStatus =
    { Ready: bool
      SupportsVoiceCloning: bool
      SupportsStreaming: bool
      Runtime: string
      ModelDir: string
      ExecutionProvider: string
      OutputSampleRate: int
      VoiceSamplePath: string
      MissingFiles: string array
      Message: string }

type TtsSynthesisRequest =
    { Phase: string
      Text: string
      OutputDirectory: string
      OutputFileName: string
      VoiceSamplePath: string option
      VoiceSampleTranscript: string option }

type TtsSynthesisResult =
    { Phase: string
      Text: string
      OutputPath: string option
      SampleRate: int
      Samples: int
      DurationMs: float
      InferenceTimeMs: float
      Message: string }

type ITtsRuntime =
    abstract Status: unit -> TtsRuntimeStatus

    abstract SynthesizeAsync:
        request: TtsSynthesisRequest *
        emitChunk: (float32 array -> Task) *
        cancellationToken: CancellationToken -> Task<TtsSynthesisResult>

type AgentToolCallInfo =
    { Round: int
      Name: string
      Arguments: Map<string, string>
      RawText: string }

type AgentToolResultInfo =
    { Round: int
      Name: string
      Success: bool
      Result: string
      Error: string option }

type VoiceAgentSessionRequest =
    { SystemPrompt: string
      Mode: string }

type VoiceAgentSessionInfo =
    { Id: string
      ServiceName: string
      Mode: string
      SystemPrompt: string
      WebRtcOfferUrl: string
      CreatedUtc: DateTimeOffset }

type VoiceAgentTurnRequest =
    { SessionId: string
      UserAudio24k: float32 array
      RequestId: string option }

type VoiceAgentTurnResult =
    { Id: string
      RequestId: string
      TurnIndex: int
      Transcript: string
      FinalText: string
      ToolCalls: AgentToolCallInfo array
      ToolResults: AgentToolResultInfo array
      AudioUrl: string option
      DetailsUrl: string
      Details: JsonElement }

type VoiceAgentStreamingEvent =
    | VoiceAgentTranscription of sessionId: string * requestId: string * turnIndex: int * transcript: string
    | VoiceAgentToolCall of sessionId: string * requestId: string * turnIndex: int * call: AgentToolCallInfo
    | VoiceAgentToolResult of sessionId: string * requestId: string * turnIndex: int * result: AgentToolResultInfo
    | VoiceAgentFillerText of sessionId: string * requestId: string * turnIndex: int * text: string
    | VoiceAgentFinalText of sessionId: string * requestId: string * turnIndex: int * text: string
    | TtsSynthesisStarted of sessionId: string * requestId: string * turnIndex: int * phase: string * text: string
    | TtsAudioChunk of sessionId: string * requestId: string * turnIndex: int * phase: string * sampleRate: int * samples: float32 array
    | TtsSynthesisDone of sessionId: string * requestId: string * turnIndex: int * result: TtsSynthesisResult
    | TtsSynthesisCanceled of sessionId: string * requestId: string * turnIndex: int * phase: string
    | TtsUnavailable of sessionId: string * requestId: string * turnIndex: int * phase: string * message: string
    | VoiceAgentDone of VoiceAgentTurnResult
    | VoiceAgentCanceled of sessionId: string * requestId: string option

type VoiceAgentRuntimeStatus =
    { Ready: bool
      ServiceName: string
      Mode: string
      WorkDir: string
      MaxHistoryTurns: int
      MaxTurnAudioSeconds: float
      MaxTurnAudioSamples24k: int
      Gemma: GemmaRuntimeStatus
      Stt: SttRuntimeStatus
      Tts: TtsRuntimeStatus
      Message: string }

type IVoiceAgentRuntime =
    abstract MaxTurnAudioSamples24k: int
    abstract Status: unit -> VoiceAgentRuntimeStatus
    abstract CreateSession: request: VoiceAgentSessionRequest -> VoiceAgentSessionInfo
    abstract TryGetSession: id: string -> VoiceAgentSessionInfo option

    abstract RunTurnAsync:
        request: VoiceAgentTurnRequest *
        emit: (VoiceAgentStreamingEvent -> Task) *
        cancellationToken: CancellationToken -> Task<VoiceAgentTurnResult>

    abstract TryGetTurnArtifact: sessionId: string * turnIndex: int * fileName: string -> ArtifactInfo option

