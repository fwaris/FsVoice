namespace FsVoice.Ctx

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

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

type IAnswerTransportFactory =
    abstract Create: FsResponses.ResponsesTransportOptions -> FsResponses.IResponsesTransport

type DefaultAnswerTransportFactory() =
    interface IAnswerTransportFactory with
        member _.Create options =
            new FsResponses.ResponsesTransport(options) :> FsResponses.IResponsesTransport

type QaSessionOptions =
    { storageRoot: string
      memoryStorePath: string option
      toolProviderDirectory: string option
      retrievalMode: RetrievalMode
      sourceIngestionProfile: SourceIngestionProfile
      sourceIndexService: ISourceIndexService option
      clients: QaModelClients
      answerResponseWebSocketConfig: FsResponses.ResponseWebSocketConfig
      answerTransportMode: FsResponses.ResponsesTransportMode
      answerTransportFactory: IAnswerTransportFactory
      answerRequireToolCall: bool
      answerPromptCacheKey: string option
      answerPromptCacheRetention: string option
      plugInProfile: QaPlugInProfile
      prompts: PromptSet
      modelRoles: Map<ModelRole, ModelRoleConfig>
      answerModelId: string
      keywordModelId: string
      elaborateIndexKeywords: bool
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
          sourceIngestionProfile = SourceIngestionProfile.defaults
          sourceIndexService = None
          clients = QaModelClients.none
          answerResponseWebSocketConfig = answerResponseWebSocketConfig
          answerTransportMode = FsResponses.ResponsesTransportMode.PersistentWebSocket
          answerTransportFactory = DefaultAnswerTransportFactory() :> IAnswerTransportFactory
          answerRequireToolCall = false
          answerPromptCacheKey = None
          answerPromptCacheRetention = None
          plugInProfile = QaPlugInProfile.generic
          prompts = PromptSet.empty
          modelRoles = PlugInDefinition.defaultModels
          answerModelId = QaDefaults.answerModel
          keywordModelId = QaDefaults.keywordModel
          elaborateIndexKeywords = true
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
