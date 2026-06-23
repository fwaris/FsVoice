namespace Speak2Docs.WorkFlow

open System
open System.Diagnostics
open System.IO
open System.Threading
open Speak2Docs
open Microsoft.Extensions.AI
open OpenAI.Chat
open RTFlow
open RTFlow.Functions

module OracleAgent =
    type State =
        { bus: WBus<FlowMsg, AgentMsg>
          storageRoot: string
          apiKey: string
          plugIn: FsVoice.Ctx.PlugInDefinition
          qaPlugIn: FsVoice.Ctx.IQaPlugIn
          plugInSettings: Map<string, string>
          session: FsVoice.Ctx.IQaOrchestrator option
          retrievalMode: Speak2Docs.RetrievalMode
          sources: KnowledgeSource list
          flags: SourceFlags }

    let private createClient (key: string) (modelId: string) : IChatClient =
        let client = OpenAI.OpenAIClient(key)
        client.GetResponsesClient().AsIChatClient(modelId)

    let private toQaMode (mode: Speak2Docs.RetrievalMode) : FsVoice.Ctx.RetrievalMode =
        match mode with
        | InternalDocumentIndex -> FsVoice.Ctx.RetrievalMode.InternalDocumentIndex
        | FsColbertWithFallback -> FsVoice.Ctx.RetrievalMode.FsColbertWithFallback

    let private pdfParsingMode flags =
        if flags.useHybridPdfParsing && flags.useLayoutAnalysis then
            FsVoice.Retrieval.KnowledgeSources.PdfParsingMode.Hybrid
        elif
            flags.useHybridPdfParsing
            && (flags.useOpticalParsing || flags.useAutoOcrFallback)
        then
            FsVoice.Retrieval.KnowledgeSources.PdfParsingMode.HybridWithoutLayout
        else
            FsVoice.Retrieval.KnowledgeSources.PdfParsingMode.Legacy

    let private modelConfig role (plugIn: FsVoice.Ctx.PlugInDefinition) =
        FsVoice.Ctx.PlugInDefinition.model role plugIn

    let private visualDescriptionOptions apiKey flags (plugIn: FsVoice.Ctx.PlugInDefinition) =
        if
            not flags.describePdfVisuals
            || not flags.useHybridPdfParsing
            || not flags.useLayoutAnalysis
        then
            FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled
        else
            let visualModel = modelConfig FsVoice.Ctx.VisualDescription plugIn

            { FsVoice.Retrieval.PdfVisualDescriptionOptions.defaults with
                enabled = true
                client = Some(createClient apiKey visualModel.modelId)
                modelId = visualModel.modelId }

    let private createSession (st: State) flags : FsVoice.Ctx.IQaOrchestrator =
        if String.IsNullOrWhiteSpace st.apiKey then
            invalidOp "OpenAI API key is required for oracle QA Responses WebSocket answering."

        let clients: FsVoice.Ctx.QaModelClients =
            let queryExpansion =
                createClient st.apiKey (modelConfig FsVoice.Ctx.QueryExpansion st.plugIn).modelId

            { queryExpansion = Some queryExpansion
              visualDescription =
                if flags.describePdfVisuals && flags.useHybridPdfParsing && flags.useLayoutAnalysis then
                    Some(createClient st.apiKey (modelConfig FsVoice.Ctx.VisualDescription st.plugIn).modelId)
                else
                    None }

        let storageRoot = st.storageRoot
        let answerModel = modelConfig FsVoice.Ctx.Answer st.plugIn
        let keywordModel = modelConfig FsVoice.Ctx.Keyword st.plugIn
        let answerWebSocketConfig = FsResponses.ResponseWebSocketConfig.create st.apiKey

        let options =
            { FsVoice.Ctx.QaSessionOptions.create storageRoot answerWebSocketConfig with
                toolProviderDirectory = Some(Path.Combine(storageRoot, "tool-providers"))
                clients = clients
                toolProviders = st.qaPlugIn.GetToolProviders()
                plugInProfile = st.plugIn.profile
                prompts = st.plugIn.prompts
                modelRoles = st.plugIn.models
                answerModelId = answerModel.modelId
                answerTransportMode = Speak2Docs.RuntimeSettings.DefaultOracleAnswerTransportMode
                keywordModelId = keywordModel.modelId
                elaborateIndexKeywords = flags.elaborateIndexKeywords
                pdfParsingMode = pdfParsingMode flags
                enableOpticalParsing = flags.useOpticalParsing
                enableAutoOpticalParsing = flags.useAutoOcrFallback
                pdfVisualDescriptionOptions = visualDescriptionOptions st.apiKey flags st.plugIn
                enableQueryExpansion = st.plugIn.runtime.enableQueryExpansion
                memoryCandidateChunks = st.plugIn.runtime.memoryCandidateChunks
                maxContextChunks = st.plugIn.runtime.maxContextChunks
                autoWriteback = st.plugIn.runtime.autoWriteback
                enableDurableMemory = false
                answerToolCallLoopLimit = flags.answerToolCallLoopLimit
                logTimings = true
                logExpansions = flags.logExpansions
                logChunks = flags.logChunks
                useLexicalFilter = flags.useLexicalFilter
                report = fun msg -> st.bus.PostToAgent(Ag_Log msg) }

        new FsVoice.Ctx.QaSession(options) :> FsVoice.Ctx.IQaOrchestrator

    let private createContextProvider st flags mode sources : FsVoice.Ctx.IQaContextProvider =
        let qaMode = toQaMode mode

        let keywordModel = modelConfig FsVoice.Ctx.Keyword st.plugIn

        let options =
            { FsVoice.Retrieval.FsColbertContextProviderOptions.create st.storageRoot qaMode sources with
                queryExpansionClient = None
                keywordGenerationClient = None
                disposeKeywordGenerationClient = false
                plugInProfile = st.plugIn.profile
                plugInFingerprint = FsVoice.Ctx.PlugInDefinition.fingerprint st.plugIn
                keywordModelId = keywordModel.modelId
                elaborateIndexKeywords = flags.elaborateIndexKeywords
                pdfParsingMode = pdfParsingMode flags
                enableOpticalParsing = flags.useOpticalParsing
                enableAutoOpticalParsing = flags.useAutoOcrFallback
                pdfVisualDescriptionOptions = visualDescriptionOptions st.apiKey flags st.plugIn
                buildMissingIndexes = false
                logExpansions = flags.logExpansions
                logChunks = flags.logChunks
                useLexicalFilter = flags.useLexicalFilter
                report = fun msg -> st.bus.PostToAgent(Ag_Log msg) }

        new FsVoice.Retrieval.FsColbertContextProvider(options) :> FsVoice.Ctx.IQaContextProvider

    let private createPlugInContextProviders st =
        try
            let hostContext: FsVoice.Ctx.PlugInHostContext =
                { storageRoot = st.storageRoot
                  packageRoot = None
                  settings = st.plugInSettings
                  report = fun msg -> st.bus.PostToAgent(Ag_Log msg) }

            st.qaPlugIn.GetContextProviders hostContext
        with ex ->
            st.bus.PostToAgent(Ag_Log $"PlugIn context providers failed to load: {ex.Message}")
            []

    let private startAnswerTransportPreparation st (session: FsVoice.Ctx.IQaOrchestrator) =
        match session with
        | :? FsVoice.Ctx.IQaAnswerTransportPreparer as preparer ->
            async {
                use preparationTimeout = new CancellationTokenSource()
                preparationTimeout.CancelAfter(TimeSpan.FromMilliseconds(float st.plugIn.runtime.functionCallTimeoutMs))

                try
                    do!
                        preparer.PrepareAnswerTransportAsync(preparationTimeout.Token)
                        |> Async.AwaitTask

                    st.bus.PostToAgent(Ag_Log "Answer Responses WebSocket prepared.")
                with
                | :? OperationCanceledException ->
                    st.bus.PostToAgent(
                        Ag_Log
                            $"Answer Responses WebSocket preparation timed out after {st.plugIn.runtime.functionCallTimeoutMs} ms; the next oracle request will connect on demand."
                    )
                | ex ->
                    st.bus.PostToAgent(
                        Ag_Log $"Answer Responses WebSocket preparation failed: {ex.GetType().Name}: {ex.Message}"
                    )
            }
            |> Async.Start
        | _ -> ()

    let private configureSession st flags mode sources (session: FsVoice.Ctx.IQaOrchestrator) =
        async {
            KnowledgeSources.configurePdfParser flags.useLayoutAnalysis flags.useOpticalParsing flags.useAutoOcrFallback

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
                    $"QA session configured: mode={Speak2Docs.RetrievalModes.displayName mode}; sources={sources.Length}; retrievalFlags=lexical:{flags.useLexicalFilter} indexKeywords:{flags.elaborateIndexKeywords} pdfParser:{parserName} pdfOptical:{flags.useOpticalParsing} pdfAutoOcr:{flags.useAutoOcrFallback} pdfVisuals:{flags.describePdfVisuals}."
            )

            startAnswerTransportPreparation st session

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
            FsVoice.Ctx.DurableMemory.createSupervisorDecision request.snapshot request.realtimeJudgement

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

    let private answerRequest (st: State) (session: FsVoice.Ctx.IQaOrchestrator) (request: MemoryRequest) =
        async {
            try
                let qaRequest: FsVoice.Ctx.QaTurnRequest =
                    { turnId = request.snapshot.turnId
                      question = request.snapshot.text
                      realtimeJudgement = request.realtimeJudgement
                      deadline = Some request.deadline }

                let sw = Stopwatch.StartNew()

                st.bus.PostToAgent(
                    Ag_Log
                        $"QA request started: turn={request.snapshot.turnId}; question_chars={request.snapshot.text.Length}; timeoutMs={st.plugIn.runtime.functionCallTimeoutMs}; deadline={request.deadline:O}."
                )

                let! answer = session.AnswerAsync(qaRequest, request.cancellationToken) |> Async.AwaitTask
                sw.Stop()

                st.bus.PostToAgent(
                    Ag_Log
                        $"QA request returned: turn={request.snapshot.turnId}; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; answer_chars={answer.answer.Length}."
                )

                let chunks: SourceChunk list = answer.context
                let inventory: KnowledgeSource list = answer.inventory
                let context = createMemoryContext request chunks inventory

                st.bus.PostToAgent(Ag_Log $"Context ready: {chunks.Length} chunk(s), {inventory.Length} source(s).")

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
                st.bus.PostToAgent(Ag_Log $"QA request canceled for turn {request.snapshot.turnId}.")
                request.completion.TrySetCanceled request.cancellationToken |> ignore
                st.bus.PostToAgent(Ag_ResponseReady(request.snapshot, None))
            | ex ->
                st.bus.PostToAgent(Ag_Log $"QA request failed: {ex.GetType().Name}: {ex.Message}")
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
              plugIn = FsVoice.Ctx.PlugInDefinition.sanitize plugIn
              qaPlugIn = qaPlugIn
              plugInSettings = plugInSettings
              session = None
              retrievalMode = retrievalMode
              sources = sources
              flags = flags }

        bus.AgentBus.RunAsync("qa", st0, update) |> FlowUtils.catch bus.PostToFlow
