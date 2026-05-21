namespace Speak2Docs.WorkFlow

open System
open System.IO
open System.Threading
open Speak2Docs
open Microsoft.Extensions.AI
open OpenAI.Chat
open RTFlow
open RTFlow.Functions

module QaAgent =
    type State =
        { bus: WBus<FlowMsg, AgentMsg>
          storageRoot: string
          apiKey: string
          plugIn: FsVoice.QA.PlugInDefinition
          qaPlugIn: FsVoice.QA.IQaPlugIn
          plugInSettings: Map<string, string>
          session: FsVoice.QA.IQaOrchestrator option
          retrievalMode: Speak2Docs.RetrievalMode
          sources: KnowledgeSource list
          flags: SourceFlags }

    let private createClient (key: string) (modelId: string) : IChatClient =
        let client = OpenAI.OpenAIClient(key)
        client.GetResponsesClient().AsIChatClient(modelId)

    let private toQaMode (mode: Speak2Docs.RetrievalMode) : FsVoice.QA.RetrievalMode =
        match mode with
        | InternalDocumentIndex -> FsVoice.QA.RetrievalMode.InternalDocumentIndex
        | FsColbertWithFallback -> FsVoice.QA.RetrievalMode.FsColbertWithFallback

    let private toQaSource (source: KnowledgeSource) : FsVoice.QA.KnowledgeSource =
        let kind =
            match source.kind with
            | Pdf -> FsVoice.QA.KnowledgeSourceKind.Pdf
            | Markdown -> FsVoice.QA.KnowledgeSourceKind.Markdown
            | Json -> FsVoice.QA.KnowledgeSourceKind.Json

        { FsVoice.QA.KnowledgeSource.kind = kind
          location = source.location
          enabled = source.enabled }

    let private pdfParsingMode flags =
        if flags.useHybridPdfParsing && flags.useLayoutAnalysis then
            FsVoice.QA.KnowledgeSources.PdfParsingMode.Hybrid
        elif flags.useHybridPdfParsing then
            FsVoice.QA.KnowledgeSources.PdfParsingMode.HybridWithoutLayout
        else
            FsVoice.QA.KnowledgeSources.PdfParsingMode.Legacy

    let private toWorkflowSource (source: FsVoice.QA.KnowledgeSource) : KnowledgeSource =
        let kind =
            match source.kind with
            | FsVoice.QA.KnowledgeSourceKind.Pdf -> Pdf
            | FsVoice.QA.KnowledgeSourceKind.Markdown -> Markdown
            | FsVoice.QA.KnowledgeSourceKind.Json -> Json

        { kind = kind
          location = source.location
          enabled = source.enabled }

    let private toWorkflowChunk (chunk: FsVoice.QA.SourceChunk) : SourceChunk =
        { source = toWorkflowSource chunk.source
          index = chunk.index
          text = chunk.text
          score = chunk.score }

    let private toQaJudgement (judgement: RealtimeJudgement) : FsVoice.QA.RealtimeJudgement =
        { FsVoice.QA.RealtimeJudgement.turnKind = judgement.turnKind
          topicContinuity = judgement.topicContinuity
          memoryAction = judgement.memoryAction
          needsExternalContext = judgement.needsExternalContext
          confidence = judgement.confidence
          riskFlags =
            { FsVoice.QA.RiskFlags.memoryMutation = judgement.riskFlags.memoryMutation
              sensitive = judgement.riskFlags.sensitive
              conflictLikely = judgement.riskFlags.conflictLikely } }

    let private modelConfig role (plugIn: FsVoice.QA.PlugInDefinition) =
        FsVoice.QA.PlugInDefinition.model role plugIn

    let private createSession (st: State) flags : FsVoice.QA.IQaOrchestrator =
        let clients: FsVoice.QA.QaModelClients =
            if String.IsNullOrWhiteSpace st.apiKey then
                FsVoice.QA.QaModelClients.none
            else
                let queryExpansion =
                    createClient st.apiKey (modelConfig FsVoice.QA.QueryExpansion st.plugIn).modelId

                let planner =
                    createClient st.apiKey (modelConfig FsVoice.QA.Planner st.plugIn).modelId

                let answer =
                    createClient st.apiKey (modelConfig FsVoice.QA.Answer st.plugIn).modelId

                { queryExpansion = Some queryExpansion
                  toolPlanner = Some planner
                  answerGenerator = Some answer }

        let storageRoot = st.storageRoot
        let answerModel = modelConfig FsVoice.QA.Answer st.plugIn
        let keywordModel = modelConfig FsVoice.QA.Keyword st.plugIn

        let options =
            { FsVoice.QA.QaSessionOptions.create storageRoot with
                toolProviderDirectory = Some(Path.Combine(storageRoot, "tool-providers"))
                clients = clients
                toolProviders = st.qaPlugIn.GetToolProviders()
                plugInProfile = st.plugIn.profile
                prompts = st.plugIn.prompts
                modelRoles = st.plugIn.models
                answerModelId = answerModel.modelId
                keywordModelId = keywordModel.modelId
                elaborateIndexKeywords = flags.elaborateIndexKeywords
                pdfParsingMode = pdfParsingMode flags
                enableToolPlanner = st.plugIn.runtime.enableToolPlanner
                enableQueryExpansion = st.plugIn.runtime.enableQueryExpansion
                memoryCandidateChunks = st.plugIn.runtime.memoryCandidateChunks
                maxContextChunks = st.plugIn.runtime.maxContextChunks
                autoWriteback = st.plugIn.runtime.autoWriteback
                logTimings = true
                logExpansions = flags.logExpansions
                logChunks = flags.logChunks
                useLexicalFilter = flags.useLexicalFilter
                report = fun msg -> st.bus.PostToAgent(Ag_Log msg) }

        new FsVoice.QA.QaSession(options) :> FsVoice.QA.IQaOrchestrator

    let private createContextProvider st flags mode sources : FsVoice.QA.IQaContextProvider =
        let qaMode = toQaMode mode
        let qaSources = sources |> List.map toQaSource

        let keywordModel = modelConfig FsVoice.QA.Keyword st.plugIn

        let options =
            { FsVoice.QA.FsColbertContextProviderOptions.create st.storageRoot qaMode qaSources with
                queryExpansionClient = None
                keywordGenerationClient = None
                disposeKeywordGenerationClient = false
                plugInProfile = st.plugIn.profile
                plugInFingerprint = FsVoice.QA.PlugInDefinition.fingerprint st.plugIn
                keywordModelId = keywordModel.modelId
                elaborateIndexKeywords = flags.elaborateIndexKeywords
                pdfParsingMode = pdfParsingMode flags
                buildMissingIndexes = false
                logExpansions = flags.logExpansions
                logChunks = flags.logChunks
                useLexicalFilter = flags.useLexicalFilter
                report = fun msg -> st.bus.PostToAgent(Ag_Log msg) }

        new FsVoice.QA.FsColbertContextProvider(options) :> FsVoice.QA.IQaContextProvider

    let private createPlugInContextProviders st =
        try
            let hostContext: FsVoice.QA.PlugInHostContext =
                { storageRoot = st.storageRoot
                  packageRoot = None
                  settings = st.plugInSettings
                  report = fun msg -> st.bus.PostToAgent(Ag_Log msg) }

            st.qaPlugIn.GetContextProviders hostContext
        with ex ->
            st.bus.PostToAgent(Ag_Log $"PlugIn context providers failed to load: {ex.Message}")
            []

    let private configureSession st flags mode sources (session: FsVoice.QA.IQaOrchestrator) =
        async {
            KnowledgeSources.configurePdfParser flags.useLayoutAnalysis

            let provider = createContextProvider st flags mode sources
            let providers = createPlugInContextProviders st @ [ provider ]
            let! errors = session.ConfigureAsync(providers, CancellationToken.None) |> Async.AwaitTask

            for err in errors do
                st.bus.PostToAgent(Ag_Log err)

            let parserName =
                if flags.useHybridPdfParsing then
                    if flags.useLayoutAnalysis then
                        "hybrid+layout"
                    else
                        "hybrid"
                else
                    "legacy"

            st.bus.PostToAgent(
                Ag_Log
                    $"QA session configured: mode={Speak2Docs.RetrievalModes.displayName mode}; sources={sources.Length}; retrievalFlags=lexical:{flags.useLexicalFilter} indexKeywords:{flags.elaborateIndexKeywords} pdfParser:{parserName}."
            )

            return session
        }

    let private ensureSession (st: State) =
        async {
            match st.session with
            | Some session -> return st, session
            | None ->
                let session = createSession st st.flags
                let! session = configureSession st st.flags st.retrievalMode st.sources session
                return { st with session = Some session }, session
        }

    let private sameSourceConfiguration st mode sources flags =
        st.retrievalMode = mode && st.sources = sources && st.flags = flags

    let private createMemoryContext
        (request: MemoryRequest)
        (chunks: SourceChunk list)
        (inventory: KnowledgeSource list)
        =
        let decision =
            DurableMemory.createSupervisorDecision request.snapshot request.realtimeJudgement

        { requestId = request.requestId
          snapshot = request.snapshot
          supervisorDecision = decision
          context = chunks
          inventory = inventory
          memories = []
          conflicts = []
          cards = []
          timedOut = false
          createdAt = DateTimeOffset.UtcNow }

    let private answerRequest (st: State) (session: FsVoice.QA.IQaOrchestrator) (request: MemoryRequest) =
        async {
            try
                let qaRequest: FsVoice.QA.QaTurnRequest =
                    { turnId = request.snapshot.turnId
                      question = request.snapshot.text
                      realtimeJudgement = request.realtimeJudgement |> Option.map toQaJudgement
                      deadline = Some request.deadline }

                let! answer = session.AnswerAsync(qaRequest, request.cancellationToken) |> Async.AwaitTask

                let chunks: SourceChunk list = answer.context |> List.map toWorkflowChunk
                let inventory: KnowledgeSource list = answer.inventory |> List.map toWorkflowSource
                let context = createMemoryContext request chunks inventory

                let firstChunk =
                    chunks
                    |> List.tryHead
                    |> Option.map (fun chunk -> Speak2Docs.Text.truncate 180 chunk.text)
                    |> Option.defaultValue "none"

                st.bus.PostToAgent(
                    Ag_Log
                        $"QA answer trace turn={request.snapshot.turnId} chunks={chunks.Length} inventory={inventory.Length} answer_chars={answer.answer.Length} first_chunk='{firstChunk}'"
                )

                request.completion.TrySetResult context |> ignore
                st.bus.PostToAgent(Ag_ContextReady(request.snapshot, chunks, inventory))

                let candidate =
                    { turnId = request.snapshot.turnId
                      revision = request.snapshot.revision
                      source = answer.model
                      answer = answer.answer
                      context = chunks
                      isFinal = true
                      createdAt = answer.createdAt }

                st.bus.PostToAgent(Ag_ResponseReady(request.snapshot, Some candidate))
            with
            | :? OperationCanceledException ->
                request.completion.TrySetCanceled request.cancellationToken |> ignore
                st.bus.PostToAgent(Ag_ResponseReady(request.snapshot, None))
            | ex ->
                st.bus.PostToAgent(Ag_Log $"QA request failed: {ex.Message}")
                request.completion.TrySetException ex |> ignore
                st.bus.PostToAgent(Ag_ResponseReady(request.snapshot, None))
        }

    let private update (st: State) (msg: AgentMsg) =
        async {
            match msg with
            | Ag_SourcesUpdated(mode, sources, flags) ->
                if st.session.IsSome && sameSourceConfiguration st mode sources flags then
                    st.bus.PostToAgent(
                        Ag_Log
                            $"QA source update skipped; configuration is already active with {sources.Length} source(s)."
                    )

                    return st
                else
                    match st.session with
                    | Some session -> do! (session :> IAsyncDisposable).DisposeAsync().AsTask() |> Async.AwaitTask
                    | None -> ()

                    let session = createSession st flags
                    let! session = configureSession st flags mode sources session

                    return
                        { st with
                            session = Some session
                            retrievalMode = mode
                            sources = sources
                            flags = flags }
            | Ag_MemoryRequested request ->
                let! st, session = ensureSession st
                answerRequest st session request |> Async.Start
                return st
            | Ag_FlowDone _ ->
                match st.session with
                | Some session -> do! (session :> IAsyncDisposable).DisposeAsync().AsTask() |> Async.AwaitTask
                | None -> ()

                return { st with session = None }
            | _ -> return st
        }

    let start storageRoot apiKey plugIn qaPlugIn plugInSettings retrievalMode sources flags bus =
        let st0 =
            { bus = bus
              storageRoot = storageRoot
              apiKey = apiKey
              plugIn = FsVoice.QA.PlugInDefinition.sanitize plugIn
              qaPlugIn = qaPlugIn
              plugInSettings = plugInSettings
              session = None
              retrievalMode = retrievalMode
              sources = sources
              flags = flags }

        bus.AgentBus.RunAsync("qa", st0, update) |> FlowUtils.catch bus.PostToFlow
