namespace FsVoice.Ctx

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsVoice.Core

type QaSession private (options: QaSessionOptions, injectedTransport: FsResponses.IResponsesTransport option) =
    let mutable contextProviders = options.contextProviders

    let memoryPath =
        options.memoryStorePath
        |> Option.defaultValue (DurableMemory.defaultPath options.storageRoot)

    let currentMemoryEncoder () =
        contextProviders
        |> List.tryPick (fun provider ->
            match provider with
            | :? ISemanticIndexResourceProvider as semanticProvider ->
                semanticProvider.SemanticIndexResource
                |> Option.bind (function
                    | :? FsColbert.OnnxColbertEncoder as encoder -> Some encoder
                    | _ -> None)
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

    let transport =
        injectedTransport
        |> Option.defaultWith (fun () ->
            let transportOptions =
                { FsResponses.ResponsesTransportOptions.create options.answerResponseWebSocketConfig with
                    mode = options.answerTransportMode
                    report = report }

            options.answerTransportFactory.Create transportOptions)

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

                    return SourceRendering.renderContextWithLimit options.maxContextChunks chunks
                }

            member _.SourceInventoryAsync cancellationToken = contextInventory cancellationToken

            member _.SearchMemoryAsync(query, maxResults, cancellationToken) =
                memoryService.SearchAsync(query, maxResults, cancellationToken)

            member _.SearchBlackboardAsync(query, cancellationToken) =
                Task.FromResult "No QA blackboard is configured for this session." }

    let loadToolCatalog () =
        let loaded =
            QaToolLoader.loadWithProvidersAndLimit
                host
                options.maxContextChunks
                options.toolProviderDirectory
                options.toolProviders

        if options.enableDurableMemory then
            loaded
        else
            { loaded with
                tools = loaded.tools |> List.filter (QaResponseTools.isDurableMemorySearchTool >> not) }

    let mutable catalog = loadToolCatalog ()

    let responseToolCatalog () = QaResponseTools.createCatalog catalog

    let recordResponseToolObservation turnId (tool: IQaTool) query content =
        let observation =
            { pluginName = tool.PluginName
              toolName = tool.Name
              query = query
              content = content
              createdAt = DateTimeOffset.UtcNow }

        observation

    let responsesAnswerer =
        QaResponsesAnswerer(
            options,
            transport,
            sessionCancellation,
            report,
            contextSources,
            responseToolCatalog,
            recordResponseToolObservation
        )

    do
        for log in memoryService.StartupLogs @ catalog.logs do
            report log

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

    new(options: QaSessionOptions) = QaSession(options, None)

    internal new(options: QaSessionOptions, transport: FsResponses.IResponsesTransport) =
        QaSession(options, Some transport)

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
            match options.sourceIndexService with
            | None ->
                return
                    [ "No source index service is configured for LoadSourcesAsync. Create an IQaContextProvider through the host source-index service and call ConfigureAsync instead." ]
            | Some service ->
                let provider =
                    service.CreateContextProvider(options.sourceIngestionProfile, mode, sources)

                return! this.ConfigureAsync([ provider ], cancellationToken)
        }

    member _.AnswerAsync(request: QaTurnRequest, cancellationToken) =
        task {
            let totalSw = Stopwatch.StartNew()
            let snapshot = QaAnswerModel.createSnapshot request
            let transportMode = QaAnswerTransportMode.storageName options.answerTransportMode

            let reportTiming message =
                if options.logTimings then
                    report message

            reportTiming
                $"QA request started: turn={snapshot.turnId}; question_chars={snapshot.text.Length}; answerModel={options.answerModelId}; transportMode={transportMode}."

            let decision =
                memoryService.CreateSupervisorDecision(snapshot, request.realtimeJudgement)

            let forgetLogs =
                if options.enableDurableMemory then
                    memoryService.RetractFromTurn snapshot
                else
                    []

            let forgetObservations = durableMemoryForgetObservation snapshot forgetLogs

            let memoryTask =
                task {
                    let sw = Stopwatch.StartNew()
                    reportTiming $"QA memory started: turn={snapshot.turnId}."
                    let! hits = memoryService.RecallAsync(decision, cancellationToken)
                    sw.Stop()

                    reportTiming
                        $"QA memory completed: turn={snapshot.turnId}; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; hits={hits.Length}."

                    return hits, sw.Elapsed.TotalMilliseconds
                }

            let sourceTask =
                task {
                    let sw = Stopwatch.StartNew()

                    reportTiming
                        $"QA retrieval started: turn={snapshot.turnId}; candidateChunks={options.memoryCandidateChunks}; providers={contextProviders.Length}."

                    let! chunks =
                        retrieveContext snapshot.text options.memoryCandidateChunks cancellationToken
                        |> Async.StartAsTask

                    sw.Stop()

                    reportTiming
                        $"QA retrieval completed: turn={snapshot.turnId}; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; chunks={chunks.Length}."

                    return chunks, sw.Elapsed.TotalMilliseconds
                }

            let! memoryHits, memoryElapsedMs = memoryTask
            let! chunks, sourceRetrievalElapsedMs = sourceTask
            let toolObservations = forgetObservations

            let answerSw = Stopwatch.StartNew()

            reportTiming
                $"QA answer started: turn={snapshot.turnId}; model={options.answerModelId}; contextChunks={chunks.Length}; memoryHits={memoryHits.Length}; transportMode={transportMode}."

            let! answerResult =
                task {
                    try
                        let! result =
                            responsesAnswerer.AnswerAsync(
                                snapshot,
                                decision,
                                memoryHits,
                                chunks,
                                toolObservations,
                                cancellationToken
                            )
                            |> Async.StartAsTask

                        return result
                    with
                    | :? OperationCanceledException as ex ->
                        answerSw.Stop()

                        reportTiming
                            $"QA answer canceled: turn={snapshot.turnId}; elapsed={answerSw.Elapsed.TotalMilliseconds:F0}ms; transportMode={transportMode}; error={ex.Message}."

                        return raise ex
                    | ex ->
                        answerSw.Stop()

                        reportTiming
                            $"QA answer failed: turn={snapshot.turnId}; elapsed={answerSw.Elapsed.TotalMilliseconds:F0}ms; transportMode={transportMode}; error={ex.GetType().Name}: {ex.Message}."

                        return raise ex
                }

            answerSw.Stop()

            reportTiming
                $"QA answer completed: turn={snapshot.turnId}; elapsed={answerSw.Elapsed.TotalMilliseconds:F0}ms; answer_chars={answerResult.answer.Length}; observations={answerResult.observations.Length}; transportMode={transportMode}."

            let answer = answerResult.answer
            let allObservations = toolObservations @ answerResult.observations

            let writebackSw = Stopwatch.StartNew()

            if List.isEmpty forgetObservations then
                applyWriteback snapshot answer

            writebackSw.Stop()
            totalSw.Stop()

            if options.logTimings then
                report
                    $"QA timing: turn={snapshot.turnId}; total={totalSw.Elapsed.TotalMilliseconds:F0}ms; source={sourceRetrievalElapsedMs:F0}ms; memory={memoryElapsedMs:F0}ms; answer={answerSw.Elapsed.TotalMilliseconds:F0}ms; writeback={writebackSw.Elapsed.TotalMilliseconds:F0}ms; toolObservations={allObservations.Length}; transportMode={transportMode}."

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

            return qaAnswer
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            sessionCancellation.Cancel()

            for provider in contextProviders do
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult()

            for client in [ options.clients.queryExpansion ] |> List.choose id do
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
