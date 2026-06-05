namespace Speak2Docs

open System.Threading
open System.Threading.Channels
open System.Text.Json
open FsVoice.Platform
open Microsoft.Maui.ApplicationModel
open Speak2Docs.WorkFlow
open RTOpenAI.Api

type AppPage =
    | Terms
    | Main
    | Settings
    | Info
    | IndexPreview of documentId: string

type OpenAiDisclosureMode =
    | ConnectAfterAcknowledgement
    | ReviewOnly

type ConnectionBundle =
    { id: string
      session: IVoiceSession<ToHost, FromHost>
      connection: Connection
      serverEvents: Channel<JsonElement>
      clientEvents: Channel<JsonElement>
      cancellation: CancellationTokenSource }

type AppLink =
    | TermsOfUse
    | PrivacyPolicy
    | ThirdPartyNotices
    | SettingsHelp

type StartParams =
    { connectionId: string
      apiKey: string
      openAiDataSharingAcknowledged: bool
      orchestration: IVoiceOrchestration<ToHost, FromHost>
      context: VoiceOrchestrationContext
      mailbox: Channel<Msg>
      runtimeSettings: RuntimeSettings }

and IndexPreviewState =
    | PreviewLoading of documentId: string
    | PreviewReady of KnowledgeSources.IndexPreview
    | PreviewFailed of documentId: string * error: string

and PickedSourceImportResult =
    { documents: PdfDocumentSource list
      newDocuments: PdfDocumentSource list
      logs: string list }

and TransientNotification = { id: int; message: string }

and Model =
    { currentPage: AppPage
      mailbox: Channel<Msg>
      bundle: ConnectionBundle option
      pendingConnectionId: string option
      disconnectedConnectionIds: Set<string>
      sessionState: RTOpenAI.WebRTC.State
      openAiKey: string
      activePlugIn: FsVoice.Ctx.PlugInDefinition
      qaPlugIn: FsVoice.Ctx.IQaPlugIn
      runtimeSettings: RuntimeSettings
      plugInSettings: Map<string, string>
      modelRoleOverrides: Map<FsVoice.Ctx.ModelRole, string>
      retrievalMode: RetrievalMode
      pdfDocuments: PdfDocumentSource list
      log: string list
      logFontSize: float
      activityLogVerbosity: ActivityLogVerbosity
      hideSecrets: bool
      openAiDisclosureSuppressed: bool
      openAiDisclosure: OpenAiDisclosureMode option
      openAiDisclosureDoNotShowAgain: bool
      isBusy: bool
      documentProcessingCancellation: CancellationTokenSource option
      logExpansions: bool
      logChunks: bool
      answerMaxOutputTokens: string
      answerReasoningEffort: string
      answerToolCallLoopLimit: string
      useLexicalFilter: bool
      elaborateIndexKeywords: bool
      useHybridPdfParsing: bool
      useLayoutAnalysis: bool
      notification: TransientNotification option
      nextNotificationId: int
      appTheme: AppTheme
      indexPreview: IndexPreviewState option }

and Msg =
    | TermsAccepted
    | TermsDeclined
    | OpenAiDisclosure_Show of OpenAiDisclosureMode
    | OpenAiDisclosureDoNotShowAgainChanged of bool
    | OpenAiDisclosureAcknowledged
    | OpenAiDisclosureDismissed
    | OpenAiKeyChanged of string
    | ModelRoleModelChanged of FsVoice.Ctx.ModelRole * string
    | PlugInSettingChanged of string * string
    | RetrievalModeChanged of RetrievalMode
    | Settings_Show
    | Settings_Close
    | Info_Show
    | Info_Close
    | OpenAppLink of AppLink
    | AppLinkOpened of Result<unit, exn>
    | ToggleSecretVisibility
    | PickSources
    | PickSourcesCompleted of Result<PickedSourceImportResult, exn>
    | PdfProcessingCompleted of PdfDocumentSource list * Result<PdfProcessingOutcome, exn>
    | CancelPdfProcessing
    | PdfSelectionChanged of string * bool
    | RetryPdfProcessing of string
    | DeletePdf of string
    | DeletePdfCompleted of Result<PdfDeleteResult, exn>
    | RestoreBuiltInIndexes
    | RestoreBuiltInIndexesCompleted of Result<PdfDocumentSource list * string list * int, exn>
    | PreviewIndex of string
    | RefreshIndexPreview
    | IndexPreviewBack
    | IndexPreviewLoaded of string * Result<KnowledgeSources.IndexPreview, exn>
    | ApplySources
    | StartStop
    | StartCompleted of string * Result<ConnectionBundle, exn>
    | StopCompleted of string * Result<unit, exn>
    | WebRTC_StateChanged of string * RTOpenAI.WebRTC.State
    | RealtimeConnectFailed of string * string
    | Log_Append of string
    | Log_Clear
    | LogFont_Increase
    | LogFont_Decrease
    | ActivityLogVerbosityChanged of ActivityLogVerbosity
    | NotificationExpired of int
    | ThemeChanged of AppTheme
    | EventError of exn
    | AnswerMaxOutputTokensChanged of string
    | AnswerReasoningEffortChanged of string
    | AnswerToolCallLoopLimitChanged of string
    | LogExpansionsToggled of bool
    | LogChunksToggled of bool
    | UseLexicalFilterToggled of bool
    | ElaborateIndexKeywordsToggled of bool
    | UseHybridPdfParsingToggled of bool
    | UseLayoutAnalysisToggled of bool
    | PrebuiltDocumentsInstalled of Result<PdfDocumentSource list * string list, exn>
