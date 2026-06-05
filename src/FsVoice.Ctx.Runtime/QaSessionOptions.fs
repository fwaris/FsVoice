namespace FsVoice.Ctx

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open FsVoice.Retrieval

type QaModelClients =
    { queryExpansion: IChatClient option
      answerGenerator: IChatClient option }

module QaModelClients =
    let none =
        { queryExpansion = None
          answerGenerator = None }

type QaAnswerTransport =
    | OpenAIResponsesWebSocket of FsResponses.ResponseWebSocketConfig
    | CustomResponsesWebSocket of
        (FsResponses.WebSocketCreateRequest -> CancellationToken -> Task<FsResponses.ResponseStreamEvent list>)

module QaAnswerTransport =
    let openAIResponsesWebSocket apiKey =
        FsResponses.ResponseWebSocketConfig.create apiKey |> OpenAIResponsesWebSocket

    let customResponsesWebSocket createAndCollect =
        CustomResponsesWebSocket createAndCollect

type BlackboardPruningOptions =
    { enabled: bool
      triggerChars: int
      targetChars: int
      preserveRecentTurns: int
      summaryMaxOutputTokens: int }

module BlackboardPruningOptions =
    let defaults =
        { enabled = true
          triggerChars = 60000
          targetChars = 40000
          preserveRecentTurns = 6
          summaryMaxOutputTokens = 900 }

type QaSessionOptions =
    { storageRoot: string
      memoryStorePath: string option
      toolProviderDirectory: string option
      retrievalMode: RetrievalMode
      clients: QaModelClients
      answerTransport: QaAnswerTransport option
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
      blackboardPruning: BlackboardPruningOptions
      report: string -> unit }

module QaSessionOptions =
    let create storageRoot =
        { storageRoot = storageRoot
          memoryStorePath = None
          toolProviderDirectory = None
          retrievalMode = FsColbertWithFallback
          clients = QaModelClients.none
          answerTransport = None
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
          blackboardPruning = BlackboardPruningOptions.defaults
          report = ignore }
