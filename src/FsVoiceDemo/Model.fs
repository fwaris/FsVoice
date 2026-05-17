namespace FsVoiceDemo

open System.Threading
open System.Threading.Channels
open FsVoiceDemo.WorkFlow
open RTFlow
open RTOpenAI.Api

type AppPage =
    | Main
    | Settings

type ConnectionBundle =
    { flow: IFlow<FlowMsg, AgentMsg>
      connection: Connection }

type AppLink =
    | PrivacyPolicy
    | ThirdPartyNotices

type StartParams =
    { apiKey: string
      useCase: FsVoice.QA.UseCaseDefinition
      useCasePlugin: FsVoice.QA.IUseCasePlugin
      useCaseSettings: Map<string, string>
      retrievalMode: RetrievalMode
      sources: KnowledgeSource list
      mailbox: Channel<Msg>
      logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      elaborateIndexKeywords: bool
      useHybridPdfParsing: bool
      useLayoutAnalysis: bool }

and Model =
    { currentPage: AppPage
      mailbox: Channel<Msg>
      bundle: ConnectionBundle option
      sessionState: RTOpenAI.WebRTC.State
      openAiKey: string
      activeUseCase: FsVoice.QA.UseCaseDefinition
      useCasePlugin: FsVoice.QA.IUseCasePlugin
      useCaseSettings: Map<string, string>
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
      useLayoutAnalysis: bool }

and Msg =
    | OpenAiKeyChanged of string
    | ModelRoleModelChanged of FsVoice.QA.ModelRole * string
    | UseCaseSettingChanged of string * string
    | RetrievalModeChanged of RetrievalMode
    | Settings_Show
    | Settings_Close
    | OpenAppLink of AppLink
    | AppLinkOpened of Result<unit, exn>
    | ToggleSecretVisibility
    | PickPdfs
    | PickPdfsCompleted of Result<PdfDocumentSource list, exn>
    | PickIndexBundle
    | IndexBundleImportCompleted of Result<PdfDocumentSource list * string list, exn>
    | PdfProcessingCompleted of Result<PdfProcessingOutcome, exn>
    | CancelPdfProcessing
    | PdfSelectionChanged of string * bool
    | RetryPdfProcessing of string
    | DeletePdf of string
    | DeletePdfCompleted of Result<PdfDeleteResult, exn>
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
