namespace FsVoice.Ctx

open System
open System.Threading
open System.Threading.Tasks
open FsVoice.Core

type private AnswerConversationState =
    { previousResponseId: string option
      items: FsResponses.IOitem list
      compacted: bool
      version: int
      generation: int
      compactionInProgress: bool }

type private AnswerCompactionCheckpoint =
    { generation: int
      version: int
      itemCount: int
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
    let maxResponseToolRounds = 3
    let responseChainCompactionInputTokenThreshold = 16000

    let withAnswerPromptCache (request: FsResponses.WebSocketCreateRequest) =
        { request with
            prompt_cache_key = options.answerPromptCacheKey
            prompt_cache_retention = options.answerPromptCacheRetention }

    let responseWarmupRequest (answerConfig: ModelRoleConfig) (prompt: AnswerPrompt) responseTools =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input =
                [ FsResponses.IOitem.Message(
                      FsResponses.Message.OfText "Warm up the answer conversation with stable instructions and tools."
                  ) ]
            instructions = Some prompt.instructions
            generate = Some false
            reasoning = QaAnswerModel.answerReasoning answerConfig
            store = Some true
            temperature = QaAnswerModel.answerTemperature answerConfig
            tools = Some responseTools
            tool_choice = Some FsResponses.ToolChoice.Auto }
        |> withAnswerPromptCache

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

    let contentText =
        function
        | FsResponses.Content.Input_text text -> text.text
        | FsResponses.Content.Output_text output -> output.text
        | FsResponses.Content.Refusal refusal -> refusal.refusal
        | FsResponses.Content.Reasoning_text reasoning -> reasoning.text
        | FsResponses.Content.Input_image image -> $"[image: {image.image_url}]"

    let itemText =
        function
        | FsResponses.IOitem.Message message ->
            let content = message.content |> List.map contentText |> String.concat "\n"
            $"{message.role}: {content}"
        | FsResponses.IOitem.Function_call call -> $"tool call {call.name}: {call.arguments}"
        | FsResponses.IOitem.Function_call_output output -> $"tool output {output.call_id}: {output.output}"
        | FsResponses.IOitem.Web_search search -> $"web search: {search.search_context_size}"
        | FsResponses.IOitem.Web_search_call _ -> "web search call"
        | FsResponses.IOitem.File_search_call call ->
            call.queries
            |> Option.defaultValue []
            |> String.concat "; "
            |> sprintf "file search: %s"
        | FsResponses.IOitem.Code_interpreter_call call ->
            call.code |> Option.defaultValue "" |> sprintf "code interpreter: %s"
        | FsResponses.IOitem.Mcp_call call
        | FsResponses.IOitem.Mcp_approval_request call
        | FsResponses.IOitem.Mcp_approval_response call ->
            let name = call.name |> Option.defaultValue "mcp"
            let output = call.output |> Option.orElse call.error |> Option.defaultValue ""
            $"{name}: {output}"
        | FsResponses.IOitem.Reasoning reasoning ->
            reasoning.summary
            |> List.map _.text
            |> String.concat "\n"
            |> sprintf "reasoning: %s"
        | FsResponses.IOitem.Image _ -> "[image]"
        | FsResponses.IOitem.File _ -> "[file]"
        | FsResponses.IOitem.Local_shell_call _ -> "local shell call"
        | FsResponses.IOitem.Image_generation_call _ -> "image generation call"
        | FsResponses.IOitem.Computer_use _ -> "computer use"
        | FsResponses.IOitem.Computer_call call -> $"computer call {call.call_id}"
        | FsResponses.IOitem.Computer_call_output output -> $"computer output {output.call_id}"

    let itemSize item = (itemText item).Length

    let conversationSize items = items |> List.sumBy itemSize

    let renderConversationItems items =
        items
        |> List.mapi (fun index item -> $"[{index + 1}]\n{itemText item |> Text.truncate 6000}")
        |> String.concat "\n\n"

    let compactionInstructions =
        "Compact Speak2Docs QA conversation state. Preserve facts, user goals, prior answers, source-grounded findings, cited document details, tool observations, memory decisions, corrections, and unresolved follow-ups. Prefer concise bullets grouped by topic. Do not invent facts."

    let compactionUserItem items =
        let text =
            $"Create a compact checkpoint for this conversation. The next answer model will receive this checkpoint plus any turns that happened after it.\n\nConversation to compact:\n\n{renderConversationItems items}"

        FsResponses.IOitem.Message(FsResponses.Message.OfText text)

    let compactionSummaryItem summary =
        FsResponses.IOitem.Message
            { FsResponses.Message.Default with
                role = "user"
                content =
                    [ FsResponses.Content.Input_text
                          {| text =
                              $"Compacted conversation checkpoint. Use this as prior conversation memory. If more source context is needed, call the available source or memory tools again.\n\n{summary}" |} ] }

    let responseCompactionRequest (answerConfig: ModelRoleConfig) items =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = [ compactionUserItem items ]
            instructions = Some compactionInstructions
            max_output_tokens = Some options.answerCompactionMaxOutputTokens
            generate = Some true
            reasoning = QaAnswerModel.answerReasoning answerConfig
            store = Some true
            temperature = QaAnswerModel.answerTemperature answerConfig
            tools = Some []
            tool_choice = None }
        |> withAnswerPromptCache

    let responseConversationRefreshRequest answerConfig (prompt: AnswerPrompt) responseTools items =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = items
            instructions = Some prompt.instructions
            generate = Some false
            reasoning = QaAnswerModel.answerReasoning answerConfig
            store = Some true
            temperature = QaAnswerModel.answerTemperature answerConfig
            tools = Some responseTools
            tool_choice = Some FsResponses.ToolChoice.Auto }
        |> withAnswerPromptCache

    let answerBootstrapGate = obj ()
    let mutable answerBootstrapTask: Task<string option> option = None

    let emptyAnswerConversation generation =
        { previousResponseId = None
          items = []
          compacted = false
          version = 0
          generation = generation
          compactionInProgress = false }

    let answerConversationGate = obj ()
    let mutable answerConversation = emptyAnswerConversation 0

    let getAnswerConversation () =
        lock answerConversationGate (fun () -> answerConversation)

    let updateAnswerConversationState update =
        lock answerConversationGate (fun () ->
            answerConversation <- update answerConversation
            answerConversation)

    let answerConversationHasCompacted () = (getAnswerConversation ()).compacted

    let clearCompletedAnswerBootstrapTask (task: Task<string option>) =
        if task.IsCompleted then
            lock answerBootstrapGate (fun () ->
                match answerBootstrapTask with
                | Some current when Object.ReferenceEquals(current, task) -> answerBootstrapTask <- None
                | _ -> ())

    let abandonAnswerBootstrapTask (task: Task<string option>) =
        lock answerBootstrapGate (fun () ->
            match answerBootstrapTask with
            | Some current when Object.ReferenceEquals(current, task) -> answerBootstrapTask <- None
            | _ -> ())

    let runAnswerBootstrap answerConfig prompt responseTools cancellationToken =
        task {
            let request = responseWarmupRequest answerConfig prompt responseTools.tools
            let! events = transport.RunAnswerRequest request cancellationToken

            match QaResponses.responseIdFromEvents events with
            | Some responseId ->
                let state =
                    updateAnswerConversationState (fun state ->
                        match state.previousResponseId with
                        | Some _ -> state
                        | None ->
                            { state with
                                previousResponseId = Some responseId
                                version = state.version + 1 })

                return state.previousResponseId
            | None ->
                report
                    $"Answer Responses WebSocket warmup did not return a response id; sending stable instructions and tools with the next turn. {QaResponses.diagnostics events}."

                return None
        }

    let ensureAnswerBootstrap answerConfig prompt responseTools cancellationToken =
        async {
            match getAnswerConversation () with
            | { previousResponseId = Some previousResponseId } -> return Some previousResponseId
            | { previousResponseId = None
                items = _ :: _ } -> return None
            | { previousResponseId = None } ->
                let bootstrapTask =
                    lock answerBootstrapGate (fun () ->
                        match getAnswerConversation () with
                        | { previousResponseId = Some previousResponseId } -> Task.FromResult(Some previousResponseId)
                        | { previousResponseId = None
                            items = _ :: _ } -> Task.FromResult(None)
                        | { previousResponseId = None } ->
                            match answerBootstrapTask with
                            | Some task -> task
                            | None ->
                                let task = runAnswerBootstrap answerConfig prompt responseTools cancellationToken
                                answerBootstrapTask <- Some task
                                task)

                try
                    let! responseId = bootstrapTask.WaitAsync(cancellationToken) |> Async.AwaitTask
                    clearCompletedAnswerBootstrapTask bootstrapTask
                    return responseId
                with ex ->
                    abandonAnswerBootstrapTask bootstrapTask
                    return raise ex
        }

    let appendAnswerConversation userItem answer previousResponseId =
        updateAnswerConversationState (fun state ->
            { state with
                previousResponseId = previousResponseId
                items = state.items @ [ userItem; QaAnswerModel.answerAssistantItem answer ]
                version = state.version + 1 })
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

    let markCompactionFinished generation =
        updateAnswerConversationState (fun state ->
            if state.generation = generation then
                { state with
                    compactionInProgress = false }
            else
                state)
        |> ignore

    let tryCreateCompactionCheckpoint threshold force =
        lock answerConversationGate (fun () ->
            let size = conversationSize answerConversation.items

            if
                answerConversation.compactionInProgress
                || List.isEmpty answerConversation.items
                || ((not force) && size <= threshold)
            then
                None
            else
                answerConversation <-
                    { answerConversation with
                        compactionInProgress = true }

                Some
                    { generation = answerConversation.generation
                      version = answerConversation.version
                      itemCount = answerConversation.items.Length
                      items = answerConversation.items })

    let tryCreateCompactionRefreshInput checkpoint summary =
        let summaryItem = compactionSummaryItem summary

        lock answerConversationGate (fun () ->
            if
                answerConversation.generation <> checkpoint.generation
                || answerConversation.items.Length < checkpoint.itemCount
            then
                None
            else
                let tail = answerConversation.items |> List.skip checkpoint.itemCount
                let items = summaryItem :: tail
                Some(items, answerConversation.version))

    let tryApplyCompaction checkpoint refreshVersion refreshedItems responseId =
        lock answerConversationGate (fun () ->
            if
                answerConversation.generation = checkpoint.generation
                && answerConversation.version = refreshVersion
            then
                answerConversation <-
                    { answerConversation with
                        previousResponseId = Some responseId
                        items = refreshedItems
                        compacted = true
                        version = answerConversation.version + 1
                        compactionInProgress = false }

                true
            else
                answerConversation <-
                    { answerConversation with
                        compactionInProgress = false }

                false)

    let rec runAnswerCompaction answerConfig prompt checkpoint =
        async {
            try
                let token = sessionCancellation.Token

                report
                    $"Answer conversation compaction started: items={checkpoint.itemCount}; chars={conversationSize checkpoint.items}; version={checkpoint.version}."

                let compactionRequest = responseCompactionRequest answerConfig checkpoint.items
                let! compactionEvents = transport.RunStatelessRequest compactionRequest token |> Async.AwaitTask

                let summary =
                    FsResponses.ResponseStream.outputText compactionEvents
                    |> Text.normalizeWhitespace

                if String.IsNullOrWhiteSpace summary then
                    report
                        $"Answer conversation compaction returned empty text; keeping existing response history. {QaResponses.diagnostics compactionEvents}."

                    markCompactionFinished checkpoint.generation
                else
                    match tryCreateCompactionRefreshInput checkpoint summary with
                    | None ->
                        report "Answer conversation compaction was discarded because the QA session was reconfigured."
                        markCompactionFinished checkpoint.generation
                    | Some(refreshedItems, refreshVersion) ->
                        let responseTools = responseToolCatalog ()

                        let refreshRequest =
                            responseConversationRefreshRequest answerConfig prompt responseTools.tools refreshedItems

                        let! refreshEvents = transport.RunStatelessRequest refreshRequest token |> Async.AwaitTask

                        match QaResponses.responseIdFromEvents refreshEvents with
                        | Some responseId ->
                            if tryApplyCompaction checkpoint refreshVersion refreshedItems responseId then
                                report
                                    $"Answer conversation compaction applied: compacted_items={checkpoint.itemCount}; retained_tail={refreshedItems.Length - 1}; summary_chars={summary.Length}; responseId={responseId}."
                            else
                                report
                                    "Answer conversation compaction finished, but new turns arrived before the compacted response root could be applied; a later turn will retry if needed."

                                scheduleAnswerCompactionIfNeeded answerConfig prompt None
                        | None ->
                            report
                                $"Answer conversation compaction could not refresh the response root; keeping existing response history. {QaResponses.diagnostics refreshEvents}."

                            markCompactionFinished checkpoint.generation
            with
            | :? OperationCanceledException -> markCompactionFinished checkpoint.generation
            | ex ->
                report $"Answer conversation compaction failed: {ex.Message}"
                markCompactionFinished checkpoint.generation
        }

    and scheduleAnswerCompactionIfNeeded answerConfig prompt (forceReason: string option) =
        match options.answerCompactionThresholdChars with
        | Some threshold when threshold > 0 ->
            match tryCreateCompactionCheckpoint threshold forceReason.IsSome with
            | Some checkpoint ->
                forceReason
                |> Option.iter (fun reason ->
                    report
                        $"Answer conversation compaction scheduled early: {reason}; items={checkpoint.itemCount}; chars={conversationSize checkpoint.items}; version={checkpoint.version}.")

                Async.Start(runAnswerCompaction answerConfig prompt checkpoint, sessionCancellation.Token)
            | None -> ()
        | _ -> ()

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

            let! initialEvents = transport.RunAnswerRequest request cancellationToken |> Async.AwaitTask

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
                            let! outputs, newObservations =
                                QaResponseTools.invokeFunctionCalls
                                    report
                                    recordObservation
                                    turnId
                                    responseTools
                                    calls
                                    cancellationToken
                                |> Async.AwaitTask

                            let allObservations = observations @ newObservations

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

                                let! finalEvents =
                                    transport.RunAnswerRequest finalRequest cancellationToken |> Async.AwaitTask

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

                                let! nextEvents =
                                    transport.RunAnswerRequest followUpRequest cancellationToken |> Async.AwaitTask

                                return! complete (remainingToolTurns - 1) nextEvents allObservations
                }

            return! complete maxResponseToolRounds initialEvents []
        }

    member _.ResetConversation() =
        updateAnswerConversationState (fun state -> emptyAnswerConversation (state.generation + 1))
        |> ignore

    member _.PrepareAsync(cancellationToken) =
        task {
            use linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionCancellation.Token)

            let token = linkedCts.Token
            let answerConfig = QaAnswerModel.modelConfig options Answer

            let prompt =
                { instructions = QaAnswerModel.answerInstructions options
                  userPrompt = "" }

            let responseTools = responseToolCatalog ()

            let! _ =
                ensureAnswerBootstrap answerConfig prompt responseTools token
                |> Async.StartAsTask

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

                    let! previousResponseId = ensureAnswerBootstrap answerConfig prompt responseTools cancellationToken

                    let replayLocalHistory =
                        replayHistory
                        || (previousResponseId.IsNone && not (List.isEmpty (getAnswerConversation ()).items))

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

                        updateAnswerConversationState (fun state ->
                            { state with
                                previousResponseId = None
                                version = state.version + 1 })
                        |> ignore

                        let! replayPreviousResponseId =
                            ensureAnswerBootstrap answerConfig prompt responseTools cancellationToken

                        return!
                            responsesCreateAndComplete
                                snapshot.turnId
                                answerConfig
                                maxOutputTokens
                                prompt
                                responseTools
                                replayPreviousResponseId
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

                    QaResponses.responseChainCompactionReason responseChainCompactionInputTokenThreshold events
                    |> scheduleAnswerCompactionIfNeeded answerConfig prompt
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

                        QaResponses.responseChainCompactionReason responseChainCompactionInputTokenThreshold retryEvents
                        |> scheduleAnswerCompactionIfNeeded answerConfig prompt
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
