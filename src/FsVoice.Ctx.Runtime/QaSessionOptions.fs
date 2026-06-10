namespace FsVoice.Ctx

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open FsVoice.Retrieval

type QaModelClients =
    { queryExpansion: IChatClient option
      visualDescription: IChatClient option }

module QaModelClients =
    let none =
        { queryExpansion = None
          visualDescription = None }

type QaAnswerTransportMode = FsResponses.ResponsesTransportMode

module QaAnswerTransportMode =
    let PersistentWebSocket = FsResponses.ResponsesTransportMode.PersistentWebSocket

    let NewWebSocketPerRequest =
        FsResponses.ResponsesTransportMode.NewWebSocketPerRequest

    let storageName = FsResponses.ResponsesTransportMode.storageName

type QaSessionOptions =
    { storageRoot: string
      memoryStorePath: string option
      toolProviderDirectory: string option
      retrievalMode: RetrievalMode
      clients: QaModelClients
      answerResponseWebSocketConfig: FsResponses.ResponseWebSocketConfig
      answerTransportMode: FsResponses.ResponsesTransportMode
      answerRequireToolCall: bool
      answerPromptCacheKey: string option
      answerPromptCacheRetention: string option
      plugInProfile: QaPlugInProfile
      prompts: PromptSet
      modelRoles: Map<ModelRole, ModelRoleConfig>
      answerModelId: string
      keywordModelId: string
      elaborateIndexKeywords: bool
      pdfParsingMode: KnowledgeSources.PdfParsingMode
      pdfVisualDescriptionOptions: PdfVisualDescriptionOptions
      memoryCandidateChunks: int
      maxContextChunks: int
      memoryService: IMemoryService option
      contextProviders: IQaContextProvider list
      toolProviders: IQaToolProvider list
      enableQueryExpansion: bool
      logTimings: bool
      logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      autoWriteback: bool
      enableDurableMemory: bool
      answerOpenAiCompactionThresholdTokens: int option
      answerToolCallLoopLimit: int
      report: string -> unit }

module QaSessionOptions =
    let create storageRoot answerResponseWebSocketConfig =
        { storageRoot = storageRoot
          memoryStorePath = None
          toolProviderDirectory = None
          retrievalMode = FsColbertWithFallback
          clients = QaModelClients.none
          answerResponseWebSocketConfig = answerResponseWebSocketConfig
          answerTransportMode = FsResponses.ResponsesTransportMode.PersistentWebSocket
          answerRequireToolCall = false
          answerPromptCacheKey = None
          answerPromptCacheRetention = None
          plugInProfile = QaPlugInProfile.generic
          prompts = PromptSet.empty
          modelRoles = PlugInDefinition.defaultModels
          answerModelId = QaDefaults.answerModel
          keywordModelId = QaDefaults.nanoModel
          elaborateIndexKeywords = true
          pdfParsingMode = KnowledgeSources.PdfParsingMode.Hybrid
          pdfVisualDescriptionOptions = PdfVisualDescriptionOptions.disabled
          memoryCandidateChunks = QaDefaults.memoryCandidateChunks
          maxContextChunks = QaDefaults.maxContextChunks
          memoryService = None
          contextProviders = []
          toolProviders = []
          enableQueryExpansion = false
          logTimings = false
          logExpansions = false
          logChunks = false
          useLexicalFilter = true
          autoWriteback = true
          enableDurableMemory = true
          answerOpenAiCompactionThresholdTokens = Some 200000
          answerToolCallLoopLimit = 3
          report = ignore }
