namespace Speak2Docs.WorkFlow

open System
open System.Threading
open System.Threading.Tasks
open FsVoice.Ctx
open Speak2Docs
open RTOpenAI.Events
open RTFlow

type SourceKind = KnowledgeSourceKind
type KnowledgeSource = FsVoice.Ctx.KnowledgeSource
type SourceChunk = FsVoice.Ctx.SourceChunk
type TranscriptSnapshot = FsVoice.Ctx.TranscriptSnapshot

type OracleCandidate =
    { turnId: string
      revision: int
      source: string
      answer: string
      context: SourceChunk list
      isFinal: bool
      createdAt: DateTimeOffset }

type MemoryCardKind =
    | UserIntent
    | ToolPlan
    | ToolObservation
    | RecallPlan
    | DurableMemoryRecord
    | ConflictNote
    | CommittedMemory
    | Evidence
    | OpenQuestion
    | ResolvedAnswer
    | CurrentFocus

type MemoryCard =
    { kind: MemoryCardKind
      title: string
      content: string
      source: string option
      createdAt: DateTimeOffset }

type MemoryKind = FsVoice.Ctx.MemoryKind
type MemoryScope = FsVoice.Ctx.MemoryScope
type MemoryStatus = FsVoice.Ctx.MemoryStatus
type MemorySensitivity = FsVoice.Ctx.MemorySensitivity
type TemporalMode = FsVoice.Ctx.TemporalMode
type RecallBudget = FsVoice.Ctx.RecallBudget
type ControllerPath = FsVoice.Ctx.ControllerPath
type MemoryTemporal = FsVoice.Ctx.MemoryTemporal
type MemoryProvenance = FsVoice.Ctx.MemoryProvenance
type MemoryRelations = FsVoice.Ctx.MemoryRelations
type MemoryRetrieval = FsVoice.Ctx.MemoryRetrieval
type MemoryRecord = FsVoice.Ctx.MemoryRecord
type MemoryRecallHit = FsVoice.Ctx.MemoryRecallHit
type RiskFlags = FsVoice.Ctx.RiskFlags
type RealtimeJudgement = FsVoice.Ctx.RealtimeJudgement
type RecallSpec = FsVoice.Ctx.RecallSpec
type SupervisorDecision = FsVoice.Ctx.SupervisorDecision
type MemoryWriteProposal = FsVoice.Ctx.MemoryWriteProposal
type MemoryConflictKind = FsVoice.Ctx.MemoryConflictKind
type CommittedMemoryUpdate = FsVoice.Ctx.CommittedMemoryUpdate

type MemoryContext =
    { requestId: string
      snapshot: TranscriptSnapshot
      supervisorDecision: SupervisorDecision
      context: SourceChunk list
      inventory: KnowledgeSource list
      memories: MemoryRecallHit list
      conflicts: string list
      cards: MemoryCard list
      timedOut: bool
      createdAt: DateTimeOffset }

type MemoryRequest =
    { requestId: string
      snapshot: TranscriptSnapshot
      realtimeJudgement: RealtimeJudgement option
      deadline: DateTimeOffset
      cancellationToken: CancellationToken
      completion: TaskCompletionSource<MemoryContext> }

type VoiceToolCall =
    { name: string
      callId: string
      content: string
      snapshot: TranscriptSnapshot
      answerMaxOutputTokens: int
      cancellation: CancellationTokenSource
      timeout: TimeSpan
      task: TaskCompletionSource<ContentFunctionCallOutput> }

type SourceFlags =
    { logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      elaborateIndexKeywords: bool
      useHybridPdfParsing: bool
      useLayoutAnalysis: bool
      useOpticalParsing: bool
      useAutoOcrFallback: bool
      describePdfVisuals: bool
      answerToolCallLoopLimit: int }

module SourceFlags =
    let ingestionProfile flags =
        SourceIngestionProfile.fromLegacyFlags
            flags.useHybridPdfParsing
            flags.useLayoutAnalysis
            flags.useOpticalParsing
            flags.useAutoOcrFallback
            flags.describePdfVisuals

type RealtimeConnectionState =
    | RealtimeDisconnected
    | RealtimeConnecting
    | RealtimeConnected

type FromHost =
    | SourcesChanged of RetrievalMode * KnowledgeSource list
    | RuntimeSettingsChanged
    | RealtimeStateChanged of RealtimeConnectionState
    | RealtimeConnectionFailed of string

type ToHost =
    | Log of string
    | RequestRealtimeConnection of Session
    | TranscriptFinalized of TranscriptSnapshot
    | OracleResponseReady of TranscriptSnapshot * OracleCandidate option
    | FlowEnded of abnormal: bool

type FlowMsg =
    | Fl_Start
    | Fl_Terminate of {| abnormal: bool |}

type AgentMsg =
    | Ag_FlowError of WErrorType
    | Ag_FlowDone of {| abnormal: bool |}
    | Ag_SourcesUpdated of RetrievalMode * KnowledgeSource list * SourceFlags
    | Ag_TranscriptUpdated of TranscriptSnapshot
    | Ag_MemoryRequested of MemoryRequest
    | Ag_MemoryReady of MemoryRequest * MemoryContext
    | Ag_MemoryRequestCanceled of string
    | Ag_MemoryRequestFailed of string * string
    | Ag_MemoryJobStarted of string
    | Ag_MemoryJobFinished of string * Result<unit, string>
    | Ag_OracleRequested of MemoryRequest * MemoryContext
    | Ag_ResponseReady of TranscriptSnapshot * OracleCandidate option
    | Ag_RequestRealtimeConnection of Session
    | Ag_VoiceServerEvent of ServerEvent
    | Ag_ToolCallOutputReady of string * string
    | Ag_Log of string

    override this.ToString() =
        match this with
        | Ag_FlowError _ -> "Ag_FlowError"
        | Ag_FlowDone _ -> "Ag_FlowDone"
        | Ag_SourcesUpdated _ -> "Ag_SourcesUpdated"
        | Ag_TranscriptUpdated _ -> "Ag_TranscriptUpdated"
        | Ag_MemoryRequested _ -> "Ag_MemoryRequested"
        | Ag_MemoryReady _ -> "Ag_MemoryReady"
        | Ag_MemoryRequestCanceled _ -> "Ag_MemoryRequestCanceled"
        | Ag_MemoryRequestFailed _ -> "Ag_MemoryRequestFailed"
        | Ag_MemoryJobStarted _ -> "Ag_MemoryJobStarted"
        | Ag_MemoryJobFinished _ -> "Ag_MemoryJobFinished"
        | Ag_OracleRequested _ -> "Ag_OracleRequested"
        | Ag_ResponseReady _ -> "Ag_ResponseReady"
        | Ag_RequestRealtimeConnection _ -> "Ag_RequestRealtimeConnection"
        | Ag_VoiceServerEvent _ -> "Ag_VoiceServerEvent"
        | Ag_ToolCallOutputReady _ -> "Ag_ToolCallOutputReady"
        | Ag_Log _ -> "Ag_Log"
