namespace FsVoice.Ctx

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsVoice.Core
open FsVoice.Retrieval

type QaSession(options: QaSessionOptions) =
    let mutable contextProviders = options.contextProviders

    let memoryPath =
        options.memoryStorePath
        |> Option.defaultValue (DurableMemory.defaultPath options.storageRoot)

    let currentMemoryEncoder () =
        contextProviders
        |> List.tryPick (fun provider ->
            match provider with
            | :? FsColbertContextProvider as fsColbert -> fsColbert.Retrieval.encoder
            | _ -> None)

    let memoryService =
        if options.enableDurableMemory then
            options.memoryService
            |> Option.defaultValue (DurableMemoryService(memoryPath, currentMemoryEncoder) :> IMemoryService)
        else
            DisabledMemoryService() :> IMemoryService

    let sessionCancellation = new CancellationTokenSource()
    let report message = options.report message
    let clamp (maxValue: int) (value: int) = Math.Max(1, Math.Min(maxValue, value))

    let transport = QaResponsesTransport(options, sessionCancellation, report)

    let blackboard =
        QaBlackboardRuntime(options, sessionCancellation, report, currentMemoryEncoder, transport)

    let retrieveContext question maxResults cancellationToken =
        async {
            let request =
                { query = question
                  maxResults = clamp options.memoryCandidateChunks maxResults }

            let! results =
                contextProviders
                |> List.map (fun provider ->
                    async {
                        try
                            let! chunks = provider.RetrieveAsync(request, cancellationToken) |> Async.AwaitTask
                            return chunks
                        with ex ->
                            report $"Context provider {provider.DisplayName} failed: {ex.Message}"
                            return []
                    })
                |> Async.Parallel

            return
                results
                |> Array.toList
                |> List.collect id
                |> List.sortByDescending _.score
                |> List.truncate request.maxResults
        }

    let contextInventory cancellationToken =
        task {
            if List.isEmpty contextProviders then
                return "No selected source context providers are loaded."
            else
                let! inventories =
                    contextProviders
                    |> List.map (fun provider ->
                        task {
                            try
                                return! provider.InventoryAsync cancellationToken
                            with ex ->
                                report $"Context provider {provider.DisplayName} inventory failed: {ex.Message}"
                                return $"No inventory was available for {provider.DisplayName}."
                        })
                    |> Task.WhenAll

                return
                    inventories
                    |> Array.toList
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> String.concat "\n\n"
        }

    let contextSources () =
        contextProviders
        |> List.collect _.Sources
        |> List.distinctBy (fun source -> source.kind, source.location)

    let host =
        { new IQaToolHost with
            member _.Report message = report message

            member _.SearchKnowledgeAsync(question, maxResults, cancellationToken) =
                task {
                    let! chunks = retrieveContext question maxResults cancellationToken |> Async.StartAsTask

                    return KnowledgeSources.renderContext chunks
                }

            member _.SourceInventoryAsync cancellationToken = contextInventory cancellationToken

            member _.SearchMemoryAsync(query, maxResults, cancellationToken) =
                memoryService.SearchAsync(query, maxResults, cancellationToken)

            member _.SearchBlackboardAsync(query, cancellationToken) =
                blackboard.SearchAsync(query, cancellationToken) }

    let loadToolCatalog () =
        let loaded =
            QaToolLoader.loadWithProviders host options.toolProviderDirectory options.toolProviders

        if options.enableDurableMemory then
            loaded
        else
            { loaded with
                tools = loaded.tools |> List.filter (QaResponseTools.isDurableMemorySearchTool >> not) }

    let mutable catalog = loadToolCatalog ()

    let responseToolCatalog includeBlackboard =
        QaResponseTools.createCatalog includeBlackboard catalog

    let recordResponseToolObservation turnId (tool: IQaTool) query content =
        let observation =
            { pluginName = tool.PluginName
              toolName = tool.Name
              query = query
              content = content
              createdAt = DateTimeOffset.UtcNow }

        blackboard.AddRecord(BlackboardRecords.toolObservation turnId observation)
        observation

    let responsesAnswerer =
        QaResponsesAnswerer(
            options,
            transport,
            sessionCancellation,
            report,
            contextSources,
            responseToolCatalog,
            recordResponseToolObservation,
            blackboard.HasSummary
        )

    do
        for log in memoryService.StartupLogs @ catalog.logs do
            report log

    let answerWithModel
        (snapshot: TranscriptSnapshot)
        (decision: SupervisorDecision)
        (memoryHits: MemoryRecallHit list)
        (chunks: SourceChunk list)
        (observations: QaToolObservation list)
        cancellationToken
        =
        async {
            match options.answerTransport with
            | Some _ ->
                return!
                    responsesAnswerer.AnswerAsync(
                        snapshot,
                        decision,
                        memoryHits,
                        chunks,
                        observations,
                        cancellationToken
                    )
            | None ->
                return!
                    QaAnswerModel.answerWithChatClient
                        options
                        report
                        contextSources
                        snapshot
                        decision
                        memoryHits
                        chunks
                        observations
                        cancellationToken
        }

    let durableMemoryForgetObservation (snapshot: TranscriptSnapshot) logs =
        if List.isEmpty logs then
            []
        else
            [ { pluginName = "FsVoiceTools"
                toolName = "durable_memory_forget"
                query = snapshot.text
                content = logs |> String.concat "\n"
                createdAt = DateTimeOffset.UtcNow } ]

    let applyWriteback (snapshot: TranscriptSnapshot) (answer: string) =
        if
            options.enableDurableMemory
            && options.autoWriteback
            && not (String.IsNullOrWhiteSpace answer)
            && not (QaAnswerModel.isFallbackAnswer answer)
        then
            let proposals = memoryService.ProposalsFromExchange(snapshot, answer)
            let updates, logs = memoryService.CommitProposals proposals

            for update in updates do
                report update.message

            for log in logs do
                report log

    member _.ToolCatalog = catalog

    member _.PrepareAnswerTransportAsync(cancellationToken) =
        responsesAnswerer.PrepareAsync(cancellationToken)

    member _.ConfigureAsync(providers: IQaContextProvider list, cancellationToken) =
        task {
            for provider in contextProviders do
                do! provider.DisposeAsync().AsTask()

            contextProviders <- providers

            let! results =
                contextProviders
                |> List.map (fun provider ->
                    task {
                        try
                            return! provider.LoadAsync cancellationToken
                        with ex ->
                            return [ $"Context provider {provider.DisplayName} failed to load: {ex.Message}" ]
                    })
                |> Task.WhenAll

            catalog <- loadToolCatalog ()
            responsesAnswerer.ResetConversation()

            for log in catalog.logs do
                report log

            return results |> Array.toList |> List.collect id
        }

    member this.LoadSourcesAsync(mode, sources, cancellationToken) =
        task {
            let providerOptions =
                { FsColbertContextProviderOptions.create options.storageRoot mode sources with
                    queryExpansionClient =
                        if options.enableQueryExpansion then
                            options.clients.queryExpansion
                        else
                            None
                    keywordGenerationClient = options.clients.queryExpansion
                    plugInProfile = options.plugInProfile
                    plugInFingerprint =
                        { PlugInDefinition.generic with
                            id = options.plugInProfile.id
                            displayName = options.plugInProfile.displayName
                            description = options.plugInProfile.description
                            profile = options.plugInProfile
                            prompts = options.prompts
                            models = options.modelRoles
                            runtime =
                                { PlugInRuntimeOptions.defaults with
                                    retrievalMode = mode
                                    enableQueryExpansion = options.enableQueryExpansion
                                    elaborateIndexKeywords = options.elaborateIndexKeywords
                                    useLexicalFilter = options.useLexicalFilter
                                    autoWriteback = options.autoWriteback } }
                        |> PlugInDefinition.fingerprint
                    keywordModelId = options.keywordModelId
                    elaborateIndexKeywords = options.elaborateIndexKeywords
                    pdfParsingMode = options.pdfParsingMode
                    logExpansions = options.logExpansions
                    logChunks = options.logChunks
                    useLexicalFilter = options.useLexicalFilter
                    report = report }

            let provider = FsColbertContextProvider providerOptions :> IQaContextProvider
            return! this.ConfigureAsync([ provider ], cancellationToken)
        }

    member _.AnswerAsync(request: QaTurnRequest, cancellationToken) =
        task {
            let totalSw = Stopwatch.StartNew()
            let snapshot = QaAnswerModel.createSnapshot request

            blackboard.AddRecord(BlackboardRecords.transcript snapshot)

            request.realtimeJudgement
            |> Option.iter (fun judgement ->
                blackboard.AddRecord(BlackboardRecords.realtimeJudgement snapshot.turnId judgement))

            let decision =
                memoryService.CreateSupervisorDecision(snapshot, request.realtimeJudgement)

            blackboard.AddRecord(BlackboardRecords.recallDecision snapshot.turnId decision)

            let forgetLogs =
                if options.enableDurableMemory then
                    memoryService.RetractFromTurn snapshot
                else
                    []

            let forgetObservations = durableMemoryForgetObservation snapshot forgetLogs

            let memoryTask =
                task {
                    let sw = Stopwatch.StartNew()
                    let! hits = memoryService.RecallAsync(decision, cancellationToken)
                    sw.Stop()
                    return hits, sw.Elapsed.TotalMilliseconds
                }

            let sourceTask =
                task {
                    let sw = Stopwatch.StartNew()

                    let! chunks =
                        retrieveContext snapshot.text options.memoryCandidateChunks cancellationToken
                        |> Async.StartAsTask

                    sw.Stop()
                    return chunks, sw.Elapsed.TotalMilliseconds
                }

            let! memoryHits, memoryElapsedMs = memoryTask
            let! chunks, sourceRetrievalElapsedMs = sourceTask
            let toolObservations = forgetObservations

            blackboard.AddRecords(
                (memoryHits |> List.map (BlackboardRecords.memoryEvidence snapshot.turnId))
                @ (chunks |> List.map (BlackboardRecords.sourceEvidence snapshot.turnId))
                @ (toolObservations |> List.map (BlackboardRecords.toolObservation snapshot.turnId))
            )

            let answerSw = Stopwatch.StartNew()

            let! answerResult =
                answerWithModel snapshot decision memoryHits chunks toolObservations cancellationToken
                |> Async.StartAsTask

            answerSw.Stop()

            let answer = answerResult.answer
            let allObservations = toolObservations @ answerResult.observations

            let writebackSw = Stopwatch.StartNew()

            if List.isEmpty forgetObservations then
                applyWriteback snapshot answer

            writebackSw.Stop()
            totalSw.Stop()

            if options.logTimings then
                report
                    $"QA timing: total={totalSw.Elapsed.TotalMilliseconds:F0}ms; source={sourceRetrievalElapsedMs:F0}ms; memory={memoryElapsedMs:F0}ms; answer={answerSw.Elapsed.TotalMilliseconds:F0}ms; writeback={writebackSw.Elapsed.TotalMilliseconds:F0}ms; toolObservations={allObservations.Length}."

            let qaAnswer =
                { turnId = request.turnId
                  answer = answer
                  model = options.answerModelId
                  context = chunks
                  sourceRetrievalElapsedMs = sourceRetrievalElapsedMs
                  inventory = contextSources ()
                  toolObservations = allObservations
                  timedOut = false
                  createdAt = DateTimeOffset.UtcNow }

            blackboard.AddRecords
                [ BlackboardRecords.answerCandidate snapshot.turnId answer
                  BlackboardRecords.finalAnswer qaAnswer ]

            blackboard.SchedulePruningIfNeeded()

            return qaAnswer
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            sessionCancellation.Cancel()

            for provider in contextProviders do
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult()

            for client in
                [ options.clients.queryExpansion; options.clients.answerGenerator ]
                |> List.choose id do
                client.Dispose()

            transport.Dispose()
            sessionCancellation.Dispose()

            ValueTask()

    interface IQaSession with
        member this.LoadSourcesAsync(mode, sources, cancellationToken) =
            this.LoadSourcesAsync(mode, sources, cancellationToken)

        member this.AnswerAsync(request, cancellationToken) =
            this.AnswerAsync(request, cancellationToken)

    interface IQaOrchestrator with
        member this.ConfigureAsync(providers, cancellationToken) =
            this.ConfigureAsync(providers, cancellationToken)

        member this.AnswerAsync(request, cancellationToken) =
            this.AnswerAsync(request, cancellationToken)

    interface IQaAnswerTransportPreparer with
        member this.PrepareAnswerTransportAsync cancellationToken =
            this.PrepareAnswerTransportAsync cancellationToken
