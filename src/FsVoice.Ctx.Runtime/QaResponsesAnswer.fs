namespace FsVoice.Ctx

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsVoice.Core

type private AnswerConversationState =
    { previousResponseId: string option
      items: FsResponses.IOitem list }

type internal QaResponsesAnswerer
    (
        options: QaSessionOptions,
        transport: FsResponses.IResponsesTransport,
        sessionCancellation: CancellationTokenSource,
        report: string -> unit,
        contextSources: unit -> KnowledgeSource list,
        responseToolCatalog: unit -> ResponseToolCatalog,
        recordObservation: string -> IQaTool -> string -> string -> QaToolObservation
    ) =
    let maxResponseToolRounds = max 1 options.answerToolCallLoopLimit
    let answerTurnGate = new SemaphoreSlim(1, 1)

    let answerContextManagement () =
        match options.answerOpenAiCompactionThresholdTokens with
        | Some threshold when threshold > 0 ->
            Some [ FsResponses.ResponseContextManagement.Compaction {| compact_threshold = threshold |} ]
        | _ -> None

    let withAnswerPromptCache (request: FsResponses.WebSocketCreateRequest) =
        { request with
            prompt_cache_key = options.answerPromptCacheKey
            prompt_cache_retention = options.answerPromptCacheRetention }

    let responseCreateRequest
        (answerConfig: ModelRoleConfig)
        maxOutputTokens
        (prompt: AnswerPrompt)
        previousResponseId
        input
        requireToolCall
        responseTools
        =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = input
            context_management = answerContextManagement ()
            instructions = Some prompt.instructions
            max_output_tokens = Some maxOutputTokens
            previous_response_id = previousResponseId
            generate = Some true
            reasoning = QaAnswerModel.answerReasoning answerConfig
            store = Some true
            temperature = QaAnswerModel.answerTemperature answerConfig
            tools = Some responseTools
            tool_choice =
                if requireToolCall && not (List.isEmpty responseTools) then
                    Some FsResponses.ToolChoice.Required
                else
                    Some FsResponses.ToolChoice.Auto }
        |> withAnswerPromptCache

    let responseEvidenceFinalizerRequest
        (answerConfig: ModelRoleConfig)
        maxOutputTokens
        (prompt: AnswerPrompt)
        observations
        =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = [ QaAnswerModel.finalAnswerSynthesisUserItem prompt observations ]
            context_management = answerContextManagement ()
            instructions = Some QaAnswerModel.finalAnswerSynthesisInstructions
            max_output_tokens = Some maxOutputTokens
            previous_response_id = None
            generate = Some true
            reasoning = QaAnswerModel.answerReasoning answerConfig
            store = Some true
            temperature = QaAnswerModel.answerTemperature answerConfig
            tools = Some []
            tool_choice = Some FsResponses.ToolChoice.None }
        |> withAnswerPromptCache

    let emptyAnswerConversation =
        { previousResponseId = None
          items = [] }

    let answerConversationGate = obj ()
    let mutable answerConversation = emptyAnswerConversation

    let getAnswerConversation () =
        lock answerConversationGate (fun () -> answerConversation)

    let updateAnswerConversationState update =
        lock answerConversationGate (fun () ->
            answerConversation <- update answerConversation
            answerConversation)

    let appendAnswerConversation userItem answer previousResponseId =
        updateAnswerConversationState (fun state ->
            { state with
                previousResponseId = previousResponseId
                items = state.items @ [ userItem; QaAnswerModel.answerAssistantItem answer ] })
        |> ignore

    let updateAnswerConversation userItem answer events =
        QaResponses.responseIdFromEvents events
        |> Option.iter (Some >> appendAnswerConversation userItem answer)

    let resetAnswerConversationChain userItem answer =
        appendAnswerConversation userItem answer None

    let responsesInputItems userItem replayHistory =
        if replayHistory then
            (getAnswerConversation ()).items @ [ userItem ]
        else
            [ userItem ]

    let reportAnswerTiming message =
        if options.logTimings then
            report message

    let answerTransportModeText () =
        QaAnswerTransportMode.storageName options.answerTransportMode

    let compactionItemsFromEvents events =
        events
        |> List.collect (function
            | FsResponses.ResponseStreamEvent.OutputItemAdded event
            | FsResponses.ResponseStreamEvent.OutputItemDone event -> [ event.item ]
            | FsResponses.ResponseStreamEvent.ResponseCompleted event
            | FsResponses.ResponseStreamEvent.ResponseFailed event
            | FsResponses.ResponseStreamEvent.ResponseIncomplete event -> event.response.output
            | _ -> [])
        |> List.choose (function
            | FsResponses.IOitem.Compaction item -> Some item
            | _ -> None)
        |> List.distinctBy (fun item -> item.id, item.encrypted_content)

    let reportOpenAiCompaction phase events =
        match compactionItemsFromEvents events with
        | [] -> ()
        | items ->
            let ids = items |> List.choose _.id |> String.concat ","

            report
                $"OpenAI server-side answer compaction applied: phase={phase}; compactionItems={items.Length}; ids={ids}; {QaResponses.diagnostics events}."

    let finalizerResponseIsClean events answer =
        let hasToolCalls = QaResponseTools.functionCalls events |> List.isEmpty |> not

        let completed =
            QaResponses.terminalResponse events
            |> Option.exists (fun response -> response.status.Contains("completed", StringComparison.OrdinalIgnoreCase))

        completed
        && not hasToolCalls
        && not (QaResponses.isTokenLimit events)
        && (QaResponses.responseError events |> Option.isNone)
        && not (String.IsNullOrWhiteSpace answer)
        && not (QaAnswerModel.containsReasoningLeakage answer)

    let responsesCreateAndComplete
        turnId
        answerConfig
        maxOutputTokens
        prompt
        responseTools
        previousResponseId
        input
        requireInitialToolCall
        cancellationToken
        =
        async {
            let transportMode = answerTransportModeText ()

            let runAnswerRequest phase previousResponseId inputItemLabel inputItems request =
                async {
                    let previousResponseIdText = previousResponseId |> Option.defaultValue "<none>"

                    reportAnswerTiming
                        $"Answer Responses request started: turn={turnId}; phase={phase}; transportMode={transportMode}; model={answerConfig.modelId}; previousResponseId={previousResponseIdText}; {inputItemLabel}={inputItems}."

                    let sw = Stopwatch.StartNew()

                    try
                        let! events = transport.CreateAndCollectAsync(request, cancellationToken) |> Async.AwaitTask
                        sw.Stop()
                        reportOpenAiCompaction phase events

                        reportAnswerTiming
                            $"Answer Responses timing: turn={turnId}; phase={phase}; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; transportMode={transportMode}; previousResponseId={previousResponseIdText}; {inputItemLabel}={inputItems}; {QaResponses.diagnostics events}."

                        return events
                    with
                    | :? OperationCanceledException as ex ->
                        sw.Stop()

                        reportAnswerTiming
                            $"Answer Responses request canceled: turn={turnId}; phase={phase}; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; transportMode={transportMode}; previousResponseId={previousResponseIdText}; {inputItemLabel}={inputItems}; error={ex.Message}."

                        return raise ex
                    | ex ->
                        sw.Stop()

                        reportAnswerTiming
                            $"Answer Responses request failed: turn={turnId}; phase={phase}; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; transportMode={transportMode}; previousResponseId={previousResponseIdText}; {inputItemLabel}={inputItems}; error={ex.GetType().Name}: {ex.Message}."

                        return raise ex
                }

            let runEvidenceFinalizer
                (reason: string)
                (sourceEvents: FsResponses.ResponseStreamEvent list)
                (observations: QaToolObservation list)
                =
                async {
                    report
                        $"Answer Responses WebSocket reached final-answer synthesis boundary; reason={reason}; observations={observations.Length}; sending evidence-only finalizer."

                    let finalizerRequest =
                        responseEvidenceFinalizerRequest answerConfig maxOutputTokens prompt observations

                    try
                        let! finalizerEvents =
                            runAnswerRequest
                                "evidence_finalizer"
                                None
                                "inputItems"
                                finalizerRequest.input.Length
                                finalizerRequest

                        let finalizerAnswer =
                            FsResponses.ResponseStream.outputText finalizerEvents
                            |> Text.normalizeWhitespace

                        if finalizerResponseIsClean finalizerEvents finalizerAnswer then
                            return finalizerEvents, finalizerAnswer, observations, true
                        else
                            report
                                $"Answer Responses WebSocket evidence-only finalizer did not produce a clean answer; reason={reason}; answer_chars={finalizerAnswer.Length}; finalizer=({QaResponses.diagnostics finalizerEvents}); source=({QaResponses.diagnostics sourceEvents})."

                            return sourceEvents, QaAnswerModel.reliableAnswerFallback, observations, false
                    with
                    | :? OperationCanceledException as ex -> return raise ex
                    | ex ->
                        report
                            $"Answer Responses WebSocket evidence-only finalizer failed; reason={reason}; error={ex.GetType().Name}: {ex.Message}; source=({QaResponses.diagnostics sourceEvents})."

                        return sourceEvents, QaAnswerModel.reliableAnswerFallback, observations, false
                }

            let request =
                responseCreateRequest
                    answerConfig
                    maxOutputTokens
                    prompt
                    previousResponseId
                    input
                    requireInitialToolCall
                    responseTools.tools

            let! initialEvents = runAnswerRequest "initial" previousResponseId "inputItems" input.Length request

            let rec complete
                remainingToolTurns
                (events: FsResponses.ResponseStreamEvent list)
                (observations: QaToolObservation list)
                =
                async {
                    let calls = QaResponseTools.functionCalls events

                    if List.isEmpty calls then
                        let answer =
                            FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace

                        if QaAnswerModel.containsReasoningLeakage answer then
                            report
                                $"Answer Responses WebSocket rejected output_text containing process leakage; reason=reasoning_leakage; answer_chars={answer.Length}; observations={observations.Length}; {QaResponses.diagnostics events}."

                            return! runEvidenceFinalizer "reasoning_leakage" events observations
                        else
                            return events, answer, observations, true
                    elif remainingToolTurns <= 0 then
                        report
                            $"Answer Responses WebSocket stopped after reaching the tool-call iteration limit; pendingToolCalls={calls.Length}; {QaResponses.diagnostics events}."

                        return! runEvidenceFinalizer "tool_call_limit" events observations
                    else
                        match QaResponses.responseIdFromEvents events with
                        | None ->
                            report
                                $"Answer Responses WebSocket produced tool calls without a response id; pendingToolCalls={calls.Length}; {QaResponses.diagnostics events}."

                            return
                                events,
                                FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace,
                                observations,
                                false
                        | Some responseId ->
                            let toolSw = Stopwatch.StartNew()

                            let! outputs, newObservations =
                                QaResponseTools.invokeFunctionCalls
                                    report
                                    recordObservation
                                    turnId
                                    responseTools
                                    calls
                                    cancellationToken
                                |> Async.AwaitTask

                            toolSw.Stop()

                            let allObservations = observations @ newObservations

                            reportAnswerTiming
                                $"Answer Responses timing: phase=tool_calls; elapsed={toolSw.Elapsed.TotalMilliseconds:F0}ms; calls={calls.Length}; observations={allObservations.Length}; remainingToolTurns={remainingToolTurns}."

                            if remainingToolTurns <= 1 then
                                report
                                    $"Answer Responses WebSocket reached the final tool-call round; sending an evidence-only finalizer. pendingToolCalls={calls.Length}; observations={allObservations.Length}; {QaResponses.diagnostics events}."

                                return! runEvidenceFinalizer "final_tool_round" events allObservations
                            else
                                let followUpRequest =
                                    responseCreateRequest
                                        answerConfig
                                        maxOutputTokens
                                        prompt
                                        (Some responseId)
                                        outputs
                                        false
                                        responseTools.tools

                                let! nextEvents =
                                    runAnswerRequest
                                        "tool_followup"
                                        (Some responseId)
                                        "outputItems"
                                        outputs.Length
                                        followUpRequest

                                return! complete (remainingToolTurns - 1) nextEvents allObservations
                }

            return! complete maxResponseToolRounds initialEvents []
        }

    member _.ResetConversation() =
        updateAnswerConversationState (fun _ -> emptyAnswerConversation) |> ignore

    member _.PrepareAsync(cancellationToken) =
        task {
            use linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionCancellation.Token)

            let token = linkedCts.Token
            let sw = Stopwatch.StartNew()
            do! transport.PrepareAsync token
            sw.Stop()

            reportAnswerTiming
                $"Answer Responses timing: phase=prepare_connection; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; transportMode={answerTransportModeText ()}."

            return ()
        }

    member _.AnswerAsync
        (
            snapshot: TranscriptSnapshot,
            decision: SupervisorDecision,
            memoryHits: MemoryRecallHit list,
            chunks: SourceChunk list,
            observations: QaToolObservation list,
            cancellationToken: CancellationToken
        ) =
        async {
            use linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionCancellation.Token)

            let token = linkedCts.Token
            let gateWaitSw = Stopwatch.StartNew()
            do! answerTurnGate.WaitAsync(token) |> Async.AwaitTask
            gateWaitSw.Stop()

            if gateWaitSw.Elapsed.TotalMilliseconds >= 1.0 then
                reportAnswerTiming
                    $"Answer Responses timing: phase=serialized_wait; elapsed={gateWaitSw.Elapsed.TotalMilliseconds:F0}ms."

            try
                let answerConfig = QaAnswerModel.modelConfig options Answer

                let prompt =
                    QaAnswerModel.answerPrompt options contextSources snapshot decision memoryHits chunks observations

                let userItem = QaAnswerModel.answerUserItem prompt

                let answerAttempt maxOutputTokens replayHistory =
                    async {
                        let responseTools = responseToolCatalog ()

                        let conversation = getAnswerConversation ()
                        let previousResponseId = conversation.previousResponseId

                        let replayLocalHistory =
                            replayHistory
                            || (previousResponseId.IsNone && not (List.isEmpty conversation.items))

                        let! events, answer, toolObservations, responseChainReusable =
                            responsesCreateAndComplete
                                snapshot.turnId
                                answerConfig
                                maxOutputTokens
                                prompt
                                responseTools
                                previousResponseId
                                (responsesInputItems userItem replayLocalHistory)
                                options.answerRequireToolCall
                                token

                        if QaResponses.isPreviousResponseNotFound events && previousResponseId.IsSome then
                            report
                                $"Answer Responses WebSocket previous_response_id was not found; retrying from local append-only history: previousResponseId={previousResponseId.Value}; historyItems={(getAnswerConversation ()).items.Length}; {QaResponses.diagnostics events}."

                            updateAnswerConversationState (fun state -> { state with previousResponseId = None })
                            |> ignore

                            return!
                                responsesCreateAndComplete
                                    snapshot.turnId
                                    answerConfig
                                    maxOutputTokens
                                    prompt
                                    responseTools
                                    None
                                    (responsesInputItems userItem true)
                                    options.answerRequireToolCall
                                    token
                        else
                            return events, answer, toolObservations, responseChainReusable
                    }

                let maxOutputTokens =
                    QaAnswerModel.roleMaxTokens options Answer QaDefaults.answerMaxOutputTokens

                let! events, answer, toolObservations, responseChainReusable = answerAttempt maxOutputTokens false

                if QaResponses.isTokenLimit events then
                    report
                        $"Answer Responses WebSocket hit output token limit: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; answer_chars={answer.Length}; contextChunks={chunks.Length}; {QaResponses.diagnostics events}."

                    return
                        { answer = QaAnswerModel.tokenLimitFallback maxOutputTokens
                          observations = toolObservations }
                elif QaResponses.responseError events |> Option.isSome then
                    report
                        $"Answer Responses WebSocket returned error: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; contextChunks={chunks.Length}; {QaResponses.diagnostics events}."

                    return
                        { answer = QaAnswerModel.emptyAnswerFallbackWithLimit maxOutputTokens
                          observations = toolObservations }
                elif not (String.IsNullOrWhiteSpace answer) then
                    if responseChainReusable then
                        updateAnswerConversation userItem answer events
                    else
                        resetAnswerConversationChain userItem answer

                    return
                        { answer = answer
                          observations = toolObservations }
                else
                    let retryMaxOutputTokens = max 1200 (maxOutputTokens * 2)

                    let! retryEvents, retryAnswer, retryToolObservations, retryResponseChainReusable =
                        answerAttempt retryMaxOutputTokens false

                    if QaResponses.isTokenLimit retryEvents then
                        report
                            $"Answer Responses WebSocket retry hit output token limit: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; answer_chars={retryAnswer.Length}; contextChunks={chunks.Length}; {QaResponses.diagnostics retryEvents}."

                        return
                            { answer = QaAnswerModel.tokenLimitFallback retryMaxOutputTokens
                              observations = retryToolObservations }
                    elif QaResponses.responseError retryEvents |> Option.isSome then
                        report
                            $"Answer Responses WebSocket retry returned error: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; contextChunks={chunks.Length}; {QaResponses.diagnostics retryEvents}."

                        return
                            { answer = QaAnswerModel.emptyAnswerFallbackWithLimit retryMaxOutputTokens
                              observations = retryToolObservations }
                    elif not (String.IsNullOrWhiteSpace retryAnswer) then
                        if retryResponseChainReusable then
                            updateAnswerConversation userItem retryAnswer retryEvents
                        else
                            resetAnswerConversationChain userItem retryAnswer

                        return
                            { answer = retryAnswer
                              observations = retryToolObservations }
                    else
                        report
                            $"Answer Responses WebSocket returned empty text after retry: model={answerConfig.modelId}; initialMaxOutputTokens={maxOutputTokens}; retryMaxOutputTokens={retryMaxOutputTokens}; contextChunks={chunks.Length}; initial=({QaResponses.diagnostics events}); retry=({QaResponses.diagnostics retryEvents})."

                        return
                            { answer = QaAnswerModel.emptyAnswerFallbackWithLimit retryMaxOutputTokens
                              observations = retryToolObservations }
            finally
                answerTurnGate.Release() |> ignore
        }
