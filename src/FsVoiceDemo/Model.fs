namespace FsVoiceDemo

open System.Threading
open System.Threading.Channels
open System.Text.Json
open FsVoice.Types
open FsVoiceDemo.WorkFlow
open RTOpenAI.Api

type AppPage =
    | Main
    | Settings
    | IndexPreview of documentId: string

type ConnectionBundle =
    { session: IVoiceSession<ToHost, FromHost>
      connection: Connection
      serverEvents: Channel<JsonElement>
      clientEvents: Channel<JsonElement>
      cancellation: CancellationTokenSource }

type AppLink =
    | PrivacyPolicy
    | ThirdPartyNotices
    | SettingsHelp

type StartParams =
    { apiKey: string
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

and Model =
    { currentPage: AppPage
      mailbox: Channel<Msg>
      bundle: ConnectionBundle option
      sessionState: RTOpenAI.WebRTC.State
      openAiKey: string
      activePlugIn: FsVoice.QA.PlugInDefinition
      qaPlugIn: FsVoice.QA.IQaPlugIn
      runtimeSettings: RuntimeSettings
      plugInSettings: Map<string, string>
      modelRoleOverrides: Map<FsVoice.QA.ModelRole, string>
      retrievalMode: RetrievalMode
      pdfDocuments: PdfDocumentSource list
      log: string list
      logFontSize: float
      hideSecrets: bool
      isBusy: bool
      documentProcessingCancellation: CancellationTokenSource option
      logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      elaborateIndexKeywords: bool
      useHybridPdfParsing: bool
      useLayoutAnalysis: bool
      indexPreview: IndexPreviewState option }

and Msg =
    | OpenAiKeyChanged of string
    | ModelRoleModelChanged of FsVoice.QA.ModelRole * string
    | PlugInSettingChanged of string * string
    | RetrievalModeChanged of RetrievalMode
    | Settings_Show
    | Settings_Close
    | OpenAppLink of AppLink
    | AppLinkOpened of Result<unit, exn>
    | ToggleSecretVisibility
    | PickSources
    | PickSourcesCompleted of Result<PickedSourceImportResult, exn>
    | PdfProcessingCompleted of Result<PdfProcessingOutcome, exn>
    | CancelPdfProcessing
    | PdfSelectionChanged of string * bool
    | RetryPdfProcessing of string
    | DeletePdf of string
    | DeletePdfCompleted of Result<PdfDeleteResult, exn>
    | PreviewIndex of string
    | RefreshIndexPreview
    | IndexPreviewBack
    | IndexPreviewLoaded of string * Result<KnowledgeSources.IndexPreview, exn>
    | ApplySources
    | StartStop
    | StartCompleted of Result<ConnectionBundle, exn>
    | StopCompleted of Result<unit, exn>
    | WebRTC_StateChanged of RTOpenAI.WebRTC.State
    | Log_Append of string
    | Log_Clear
    | LogFont_Increase
    | LogFont_Decrease
    | EventError of exn
    | LogExpansionsToggled of bool
    | LogChunksToggled of bool
    | UseLexicalFilterToggled of bool
    | ElaborateIndexKeywordsToggled of bool
    | UseHybridPdfParsingToggled of bool
    | UseLayoutAnalysisToggled of bool
    | PrebuiltDocumentsInstalled of Result<PdfDocumentSource list * string list, exn>
