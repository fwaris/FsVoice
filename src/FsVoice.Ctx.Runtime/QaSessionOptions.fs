namespace FsVoice.Ctx

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open FsVoice.Retrieval

type QaModelClients = { queryExpansion: IChatClient option }

module QaModelClients =
    let none = { queryExpansion = None }

type QaSessionOptions =
    { storageRoot: string
      memoryStorePath: string option
      toolProviderDirectory: string option
      retrievalMode: RetrievalMode
      clients: QaModelClients
      answerResponseWebSocketConfig: FsResponses.ResponseWebSocketConfig
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
      answerCompactionThresholdChars: int option
      answerCompactionMaxOutputTokens: int
      report: string -> unit }

module QaSessionOptions =
    let create storageRoot answerResponseWebSocketConfig =
        { storageRoot = storageRoot
          memoryStorePath = None
          toolProviderDirectory = None
          retrievalMode = FsColbertWithFallback
          clients = QaModelClients.none
          answerResponseWebSocketConfig = answerResponseWebSocketConfig
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
          answerCompactionThresholdChars = Some 80000
          answerCompactionMaxOutputTokens = 1200
          report = ignore }
