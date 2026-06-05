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
        transport: QaResponsesTransport,
        sessionCancellation: CancellationTokenSource,
        report: string -> unit,
        contextSources: unit -> KnowledgeSource list,
        responseToolCatalog: unit -> ResponseToolCatalog,
        recordObservation: string -> IQaTool -> string -> string -> QaToolObservation
    ) =
    let maxResponseToolRounds = max 1 options.answerToolCallLoopLimit

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

    let responseFinalAnswerRequest
        (answerConfig: ModelRoleConfig)
        maxOutputTokens
        (prompt: AnswerPrompt)
        previousResponseId
        input
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
            let request =
                responseCreateRequest
                    answerConfig
                    maxOutputTokens
                    prompt
                    previousResponseId
                    input
                    requireInitialToolCall
                    responseTools.tools

            let initialSw = Stopwatch.StartNew()
            let! initialEvents = transport.RunAnswerRequest request cancellationToken |> Async.AwaitTask
            initialSw.Stop()
            reportOpenAiCompaction "initial" initialEvents

            let previousResponseIdText = previousResponseId |> Option.defaultValue "<none>"

            reportAnswerTiming
                $"Answer Responses timing: phase=initial; elapsed={initialSw.Elapsed.TotalMilliseconds:F0}ms; previousResponseId={previousResponseIdText}; inputItems={input.Length}; {QaResponses.diagnostics initialEvents}."

            let rec complete remainingToolTurns events observations =
                async {
                    let calls = QaResponseTools.functionCalls events

                    if List.isEmpty calls then
                        return
                            events,
                            FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace,
                            observations,
                            true
                    elif remainingToolTurns <= 0 then
                        report
                            $"Answer Responses WebSocket stopped after reaching the tool-call iteration limit; pendingToolCalls={calls.Length}; {QaResponses.diagnostics events}."

                        return
                            events,
                            FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace,
                            observations,
                            false
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
                                    $"Answer Responses WebSocket reached the final tool-call round; sending a no-tool answer synthesis request. pendingToolCalls={calls.Length}; observations={allObservations.Length}; {QaResponses.diagnostics events}."

                                let finalRequest =
                                    responseFinalAnswerRequest
                                        answerConfig
                                        maxOutputTokens
                                        prompt
                                        (Some responseId)
                                        outputs

                                let finalSw = Stopwatch.StartNew()

                                let! finalEvents =
                                    transport.RunAnswerRequest finalRequest cancellationToken |> Async.AwaitTask

                                finalSw.Stop()
                                reportOpenAiCompaction "final_synthesis" finalEvents

                                reportAnswerTiming
                                    $"Answer Responses timing: phase=final_synthesis; elapsed={finalSw.Elapsed.TotalMilliseconds:F0}ms; previousResponseId={responseId}; outputItems={outputs.Length}; {QaResponses.diagnostics finalEvents}."

                                let finalCalls = QaResponseTools.functionCalls finalEvents

                                if List.isEmpty finalCalls then
                                    return
                                        finalEvents,
                                        FsResponses.ResponseStream.outputText finalEvents |> Text.normalizeWhitespace,
                                        allObservations,
                                        true
                                else
                                    report
                                        $"Answer Responses WebSocket no-tool synthesis still produced tool calls; returning latest model text. pendingToolCalls={finalCalls.Length}; {QaResponses.diagnostics finalEvents}."

                                    return
                                        finalEvents,
                                        FsResponses.ResponseStream.outputText finalEvents |> Text.normalizeWhitespace,
                                        allObservations,
                                        false
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

                                let followUpSw = Stopwatch.StartNew()

                                let! nextEvents =
                                    transport.RunAnswerRequest followUpRequest cancellationToken |> Async.AwaitTask

                                followUpSw.Stop()
                                reportOpenAiCompaction "tool_followup" nextEvents

                                reportAnswerTiming
                                    $"Answer Responses timing: phase=tool_followup; elapsed={followUpSw.Elapsed.TotalMilliseconds:F0}ms; previousResponseId={responseId}; outputItems={outputs.Length}; {QaResponses.diagnostics nextEvents}."

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
            do! transport.PrepareAnswerConnection token
            sw.Stop()

            reportAnswerTiming
                $"Answer Responses timing: phase=prepare_connection; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms."

            return ()
        }

    member _.AnswerAsync
        (
            snapshot: TranscriptSnapshot,
            decision: SupervisorDecision,
            memoryHits: MemoryRecallHit list,
            chunks: SourceChunk list,
            observations: QaToolObservation list,
            cancellationToken
        ) =
        async {
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
                            cancellationToken

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
                                cancellationToken
                    else
                        return events, answer, toolObservations, responseChainReusable
                }

            let maxOutputTokens = QaAnswerModel.roleMaxTokens options Answer 2500
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
        }
