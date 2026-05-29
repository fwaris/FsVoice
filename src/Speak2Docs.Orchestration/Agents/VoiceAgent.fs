namespace Speak2Docs.WorkFlow

open System
open System.Text.RegularExpressions
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open FsVoice.Core
open FsVoice.Platform
open RTOpenAI.Events
open RTFlow
open RTFlow.Functions

module VoiceAgent =
    type private ToolQuestionArgs = { question: string option }

    type private RealtimeJudgementDto =
        { turn_kind: string option
          topic_continuity: string option
          memory_action: string option
          needs_external_context: bool option
          confidence: float option
          sensitive: bool option
          memory_mutation: bool option
          conflict_likely: bool option
          memory_need: string option
          tool_need: string option }

    type private RealtimeJudgementEnvelope =
        { realtime_judgement: RealtimeJudgementDto option }

    let private jsonOptions =
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        options.NumberHandling <- JsonNumberHandling.AllowReadingFromString
        options.Converters.Add(JsonFSharpConverter())
        options

    let private tryDeserialize<'T> (text: string) =
        try
            JsonSerializer.Deserialize<'T>(text, jsonOptions) |> Some
        with _ ->
            None

    module private ToolNames =
        [<Literal>]
        let QUERY_ORACLE = "QUERY_ORACLE"

    type private Q =
        | SendClientEvent of ClientEvent
        | AwaitToolCall of VoiceToolCall

    type private UserSpeechState =
        | Silent
        | Speaking

    type private ResponseCreatedState =
        | NoActiveResponse
        | ActiveResponse of {| id: string option |}

    type private VoicePlugInConfig =
        { realtimeModel: string
          transcriberModel: string
          transcriberPrompt: string
          instructions: string
          speechResultInstructions: string
          initialSourceCount: int
          answerMaxOutputTokens: int
          memoryRequestTimeout: TimeSpan
          functionCallTimeout: TimeSpan }

    type private VoiceState =
        { initialized: bool
          config: VoicePlugInConfig
          realtimeSession: Session
          transcriptByItem: Map<string, string>
          lastFinalUserTranscript: TranscriptSnapshot option
          revision: int
          pendingToolCalls: Map<string, VoiceToolCall>
          completedToolTurns: Set<string>
          pendingSpeakByCallId: Map<string, string>
          userSpeechState: UserSpeechState
          responseCreatedState: ResponseCreatedState
          pendingGreeting: string option
          pendingSpeakTexts: string list
          outputQueue: AsyncPriorityQueue<Q>
          bus: WBus<FlowMsg, AgentMsg> }

    let private TOOL_CALL_PRIORITY = 0
    let private CONTROL_EVENT_PRIORITY = 10
    let private SPEAK_PRIORITY = 20

    let private plugInConfig initialSourceCount (plugIn: FsVoice.Ctx.PlugInDefinition) =
        let plugIn = FsVoice.Ctx.PlugInDefinition.sanitize plugIn
        let realtime = FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.Realtime plugIn
        let transcriber = FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.Transcriber plugIn
        let answer = FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.Answer plugIn

        { realtimeModel = realtime.modelId
          transcriberModel = transcriber.modelId
          transcriberPrompt =
            plugIn.prompts.transcriberPrompt
            |> Option.defaultValue FsVoice.Ctx.DefaultPlugInPrompts.transcriberPrompt
          instructions =
            plugIn.prompts.realtimeInstructions
            |> Option.defaultValue FsVoice.Ctx.DefaultPlugInPrompts.realtimeInstructions
          speechResultInstructions =
            plugIn.prompts.speechResultInstruction
            |> Option.defaultValue FsVoice.Ctx.DefaultPlugInPrompts.speechResultInstruction
          initialSourceCount = max 0 initialSourceCount
          answerMaxOutputTokens =
            answer.maxOutputTokens
            |> Option.defaultValue Speak2Docs.RuntimeSettings.DefaultAnswerMaxOutputTokens
          memoryRequestTimeout = TimeSpan.FromMilliseconds(float plugIn.runtime.realtimeMemoryTimeoutMs)
          functionCallTimeout = TimeSpan.FromMilliseconds(float plugIn.runtime.functionCallTimeoutMs) }

    let private speakerphoneTurnDetection =
        VAD.Server_Vad
            {| create_response = true
               idle_timeout_ms = Skip
               interrupt_response = true
               prefix_padding_ms = 300
               silence_duration_ms = 350
               threshold = 0.7 |}

    let private sessionAudio config =
        { Audio.Default with
            input =
                Include(
                    Some
                        { AudioInput.Default with
                            noise_reduction = Include(Some { ``type`` = "far_field" })
                            transcription =
                                { language = "en"
                                  model = config.transcriberModel
                                  prompt = Some config.transcriberPrompt }
                                |> Some
                                |> Include
                            turn_detection = Include(Some speakerphoneTurnDetection) }
                ) }

    let private oracleTool =
        { Tool.Default with
            name = ToolNames.QUERY_ORACLE
            description =
                "Use this for every substantive user question, especially selected-source or document requests such as summarizing the abstract, explaining a section, or comparing PDFs. It can answer with the backend oracle using available tools, app context, selected sources, and general reasoning. The tool returns the exact wording to speak."
            parameters =
                { Parameters.Default with
                    properties =
                        [ "question",
                          JsProperty.String
                              { description =
                                  Some "The user's question or follow-up, rewritten as a concise standalone request."
                                enum = None }
                          "turn_kind",
                          JsProperty.String
                              { description =
                                  Some
                                      "Advisory turn kind from the utterance: acknowledgement, question, followup, topic_shift, correction, summary, comparison, or memory_request."
                                enum = None }
                          "topic_continuity",
                          JsProperty.String
                              { description =
                                  Some
                                      "Advisory topic continuity from recent conversation only: same_topic, topic_shift, older_topic, or unknown."
                                enum = None }
                          "memory_action",
                          JsProperty.String
                              { description =
                                  Some
                                      "Advisory explicit memory action requested by the user: none, remember, forget, correct, or unknown."
                                enum = None }
                          "needs_external_context",
                          JsProperty.Boolean
                              { description =
                                  Some
                                      "True when the user appears to ask for documents, tools, current facts, app state, memory, comparison, or summary beyond casual chat." }
                          "confidence",
                          JsProperty.String
                              { description = Some "Advisory confidence from 0.0 to 1.0."
                                enum = None }
                          "sensitive",
                          JsProperty.Boolean
                              { description =
                                  Some
                                      "True when the utterance includes secrets, credentials, private identifiers, or sensitive personal data." } ]
                        |> Map.ofList
                    required = [ "question" ] } }

    let private updateSession config (session: Session) =
        { session with
            id = Skip
            ``object`` = Skip
            model = Some config.realtimeModel
            audio = Include(Some(sessionAudio config))
            instructions = Some config.instructions
            tool_choice = Include(Some "auto")
            tools = Include(Some [ oracleTool ])
            expires_at = Skip }

    let private enqueueOutbound (outputQueue: AsyncPriorityQueue<Q>) priority work = outputQueue.Enqueue(work, priority)

    let private enqueueClientEvent state event =
        enqueueOutbound state.outputQueue CONTROL_EVENT_PRIORITY (SendClientEvent event)

    let private writeClientEvent (connection: VoiceConnection) event =
        task {
            let json = SerDe.toJson event
            use document = JsonDocument.Parse(json)
            do! connection.sender.WriteAsync(document.RootElement.Clone(), CancellationToken.None).AsTask()
        }

    let private toServerEvent (json: JsonElement) =
        let document = JsonDocument.Parse(json.GetRawText())
        SerDe.toEvent document

    let private sessionUpdateEvent config (session: Session) =
        { SessionUpdate.Default with
            event_id = Utils.newId ()
            session = updateSession config session }
        |> ClientEvent.SessionUpdate

    let private responseCreateEvent (responseInstructions: string) =
        let response =
            { Response.Default with
                instructions = Include(Some responseInstructions)
                output_modalities = Include(Some [ "audio" ])
                tool_choice = Include(Some "none")
                tools = Include(Some []) }

        { ResponseCreate.Default with
            event_id = Utils.newId ()
            response = Include(Some response) }
        |> ClientEvent.ResponseCreate

    let private responseInstructionsForOracleAnswer baseInstructions answer =
        $"{baseInstructions}\n\nOracle answer to speak exactly, without adding facts:\n\n{answer}"

    let private responseInstructionsForGreeting baseInstructions greeting =
        $"{baseInstructions}\n\nGreeting to speak exactly, without adding facts or calling tools:\n\n{greeting}"

    let private greetingForSourceCount sourceCount =
        match sourceCount with
        | count when count <= 0 ->
            "Hello. I do not see any selected documents yet. Select or add a document, then I can answer questions about it."
        | 1 -> "Hello. I am seeing 1 selected document that I can answer questions about."
        | count -> $"Hello. I am seeing {count} selected documents that I can answer questions about."

    let private createFunctionOutputEvent (result: ContentFunctionCallOutput) =
        { ConversationItemCreate.Default with
            event_id = Utils.newId ()
            item = ConversationItem.Function_call_output result }
        |> ClientEvent.ConversationItemCreate

    let private maxAnswerTokensSettingsGuidance =
        "Disconnect, open Settings, increase Max Answer Tokens, then reconnect and try again."

    let private fallbackToolOutput maxOutputTokens (_snapshot: TranscriptSnapshot) =
        $"I was unable to obtain an answer from the oracle. The request may have exceeded the current max answer token limit of {maxOutputTokens}, or the backend may have timed out. {maxAnswerTokensSettingsGuidance} You can also ask a narrower question."

    let private speechFriendlyMarkdown (text: string) =
        let replace (pattern: string) (replacement: string) (value: string) =
            Regex.Replace(value, pattern, replacement)

        text
        |> replace @"(?m)^\s{0,3}#{1,6}\s*" ""
        |> replace @"(?m)^\s{0,3}>\s?" ""
        |> replace @"(?m)^\s*[-*+]\s+" ""
        |> replace @"(?m)^\s*\d+[.)]\s+" ""
        |> replace @"(?m)^\s*(-{3,}|\*{3,}|_{3,})\s*$" ""
        |> replace @"!\[([^\]]*)\]\([^)]+\)" "$1"
        |> replace @"\[([^\]]+)\]\([^)]+\)" "$1"
        |> replace @"`([^`]+)`" "$1"
        |> replace @"(\*\*|__)(.*?)\1" "$2"
        |> replace @"(\*|_)(.*?)\1" "$2"
        |> replace @"(?<!\S)#{1,6}(?!\S)" ""
        |> Speak2Docs.Text.normalizeWhitespace

    let private oracleToolOutput (_snapshot: TranscriptSnapshot) (candidate: OracleCandidate) =
        let answer = candidate.answer |> speechFriendlyMarkdown

        if String.IsNullOrWhiteSpace answer then
            "Unfortunately, I received an empty answer. Please try again."
        else
            answer

    let private makeTranscriptSnapshot st itemId text isFinal =
        let revision = st.revision + 1

        revision,
        ({ turnId = itemId
           itemId = itemId
           revision = revision
           text = text
           isFinal = isFinal
           receivedAt = DateTimeOffset.UtcNow }
        : TranscriptSnapshot)

    let private userSnapshotForToolCall (toolQuestion: string) (snapshot: TranscriptSnapshot) =
        let text =
            if String.IsNullOrWhiteSpace toolQuestion then
                snapshot.text
            else
                toolQuestion

        { snapshot with
            text = text
            isFinal = true }

    let private acknowledgeToolCallWithoutSpeech (st: VoiceState) callId output =
        ContentFunctionCallOutput.Create callId output
        |> createFunctionOutputEvent
        |> SendClientEvent
        |> enqueueOutbound st.outputQueue TOOL_CALL_PRIORITY

        st

    let private createMemoryRequest config snapshot realtimeJudgement cancellationToken =
        { requestId = Utils.newId ()
          snapshot = snapshot
          realtimeJudgement = realtimeJudgement
          deadline = DateTimeOffset.UtcNow + config.memoryRequestTimeout
          cancellationToken = cancellationToken
          completion = TaskCompletionSource<MemoryContext>(TaskCreationOptions.RunContinuationsAsynchronously) }

    let private tryScheduleSpeak (st: VoiceState) =
        match st.userSpeechState, st.responseCreatedState, st.pendingSpeakTexts with
        | Silent, NoActiveResponse, text :: remaining ->
            responseCreateEvent (responseInstructionsForOracleAnswer st.config.speechResultInstructions text)
            |> SendClientEvent
            |> enqueueOutbound st.outputQueue SPEAK_PRIORITY

            st.bus.PostToAgent(
                Ag_Log $"Realtime response requested from oracle tool output; output_chars={text.Length}."
            )

            { st with
                responseCreatedState = ActiveResponse {| id = None |}
                pendingSpeakTexts = remaining }
        | _ -> st

    let private tryScheduleGreeting (st: VoiceState) =
        match st.userSpeechState, st.responseCreatedState, st.pendingGreeting with
        | Silent, NoActiveResponse, Some greeting ->
            responseCreateEvent (responseInstructionsForGreeting st.config.speechResultInstructions greeting)
            |> SendClientEvent
            |> enqueueOutbound st.outputQueue SPEAK_PRIORITY

            st.bus.PostToAgent(Ag_Log $"Realtime greeting requested: sources={st.config.initialSourceCount}.")

            { st with
                pendingGreeting = None
                responseCreatedState = ActiveResponse {| id = None |} }
        | _ -> st

    let private tryScheduleAudio (st: VoiceState) =
        st |> tryScheduleGreeting |> tryScheduleSpeak

    let private toolQuestion (content: string) =
        let content =
            content
            |> Option.ofObj
            |> Option.map (fun value -> value.Trim())
            |> Option.defaultValue ""

        if String.IsNullOrWhiteSpace content then
            ""
        else
            tryDeserialize<ToolQuestionArgs> content
            |> Option.bind _.question
            |> Option.map Speak2Docs.Text.normalizeWhitespace
            |> Option.defaultValue content

    let private stringIsAny values value =
        values
        |> List.exists (fun candidate -> String.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))

    let private containsIgnoreCase (needle: string) (value: string) =
        value.Contains(needle, StringComparison.OrdinalIgnoreCase)

    let private isActiveResponseInProgressError message =
        containsIgnoreCase "active response in progress" message

    let private memoryActionFromLegacy (dto: RealtimeJudgementDto) =
        match dto.memory_need with
        | Some value when stringIsAny [ "writeback"; "remember"; "typed_recall" ] value -> Some "remember"
        | Some value when stringIsAny [ "forget"; "delete" ] value -> Some "forget"
        | Some value when stringIsAny [ "correct"; "correction" ] value -> Some "correct"
        | _ -> None

    let private needsExternalContextFromLegacy (dto: RealtimeJudgementDto) =
        match dto.tool_need with
        | Some value when stringIsAny [ "none"; "unknown" ] value -> Some false
        | Some _ -> Some true
        | None -> None

    let private isMemoryMutationAction =
        function
        | Some value when stringIsAny [ "remember"; "forget"; "correct" ] value -> true
        | _ -> false

    let private isCorrectionTurn turnKind memoryAction =
        match turnKind, memoryAction with
        | Some value, _ when stringIsAny [ "correction"; "correct" ] value -> true
        | _, Some value when stringIsAny [ "correct"; "forget" ] value -> true
        | _ -> false

    let private tryRealtimeJudgement (content: string) : RealtimeJudgement option =
        let parsed =
            tryDeserialize<RealtimeJudgementEnvelope> content
            |> Option.bind _.realtime_judgement
            |> Option.orElseWith (fun () -> tryDeserialize<RealtimeJudgementDto> content)

        match parsed with
        | None -> None
        | Some dto ->
            let hasAnyHint =
                [ dto.turn_kind
                  dto.topic_continuity
                  dto.memory_action
                  dto.needs_external_context |> Option.map string
                  dto.confidence |> Option.map string
                  dto.sensitive |> Option.map string
                  dto.memory_need
                  dto.tool_need
                  dto.memory_mutation |> Option.map string
                  dto.conflict_likely |> Option.map string ]
                |> List.exists Option.isSome

            if not hasAnyHint then
                None
            else
                let turnKind = dto.turn_kind

                let memoryAction = dto.memory_action |> Option.orElse (memoryActionFromLegacy dto)

                let needsExternalContext =
                    dto.needs_external_context |> Option.orElse (needsExternalContextFromLegacy dto)

                let sensitive = dto.sensitive |> Option.defaultValue false

                let memoryMutation =
                    (dto.memory_mutation |> Option.defaultValue false)
                    || isMemoryMutationAction memoryAction

                let conflictLikely =
                    (dto.conflict_likely |> Option.defaultValue false)
                    || isCorrectionTurn turnKind memoryAction

                Some(
                    { turnKind = turnKind
                      topicContinuity = dto.topic_continuity
                      memoryAction = memoryAction
                      needsExternalContext = needsExternalContext
                      confidence = dto.confidence |> Option.defaultValue 0.5
                      riskFlags =
                        ({ memoryMutation = memoryMutation
                           sensitive = sensitive
                           conflictLikely = conflictLikely }
                        : RiskFlags) }
                    : RealtimeJudgement
                )

    let private dispatchToolCall (st: VoiceState) (fc: ContentFunctionCall) =
        let question = toolQuestion fc.arguments
        let realtimeJudgement = tryRealtimeJudgement fc.arguments

        let question =
            if String.IsNullOrWhiteSpace question then
                "The user asked a question, but the tool call did not include the question text."
            else
                question

        match fc.name with
        | ToolNames.QUERY_ORACLE ->
            match st.lastFinalUserTranscript with
            | None -> acknowledgeToolCallWithoutSpeech st fc.call_id "No completed user question has been heard yet."
            | Some userSnapshot when
                (st.pendingToolCalls |> Map.containsKey userSnapshot.turnId)
                || st.completedToolTurns.Contains userSnapshot.turnId
                ->
                acknowledgeToolCallWithoutSpeech st fc.call_id "That user question is already being handled."
            | Some userSnapshot ->
                let snapshot = userSnapshotForToolCall question userSnapshot
                let cancellation = new CancellationTokenSource()
                cancellation.CancelAfter st.config.functionCallTimeout

                let call =
                    { name = fc.name
                      callId = fc.call_id
                      content = fc.arguments
                      snapshot = snapshot
                      answerMaxOutputTokens = st.config.answerMaxOutputTokens
                      cancellation = cancellation
                      timeout = st.config.functionCallTimeout
                      task =
                        TaskCompletionSource<ContentFunctionCallOutput>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        ) }

                let memoryRequest =
                    createMemoryRequest st.config snapshot realtimeJudgement cancellation.Token

                enqueueOutbound st.outputQueue TOOL_CALL_PRIORITY (AwaitToolCall call)

                st.bus.PostToAgent(
                    Ag_Log
                        $"Oracle tool call started: question_chars={snapshot.text.Length} args_chars={fc.arguments.Length} question='{Speak2Docs.Text.truncate 120 snapshot.text}'"
                )

                st.bus.PostToAgent(Ag_MemoryRequested memoryRequest)

                { st with
                    pendingToolCalls = st.pendingToolCalls |> Map.add snapshot.turnId call }
        | _ ->
            let reply = $"The tool '{fc.name}' is not available in this FsVoice session."
            let result = ContentFunctionCallOutput.Create fc.call_id reply

            createFunctionOutputEvent result
            |> SendClientEvent
            |> enqueueOutbound st.outputQueue TOOL_CALL_PRIORITY

            { st with
                pendingSpeakTexts = st.pendingSpeakTexts @ [ reply ] }
            |> tryScheduleSpeak

    let private completeToolCall (st: VoiceState) (snapshot: TranscriptSnapshot) (candidate: OracleCandidate option) =
        if st.completedToolTurns.Contains snapshot.turnId then
            st
        else
            match st.pendingToolCalls |> Map.tryFind snapshot.turnId with
            | None ->
                st.bus.PostToAgent(
                    Ag_Log $"Ignoring oracle response for turn without pending tool call: {snapshot.turnId}."
                )

                st
            | Some call ->
                let output =
                    match candidate with
                    | Some candidate -> oracleToolOutput snapshot candidate
                    | None -> fallbackToolOutput st.config.answerMaxOutputTokens snapshot

                ContentFunctionCallOutput.Create call.callId output
                |> call.task.TrySetResult
                |> ignore

                st.bus.PostToAgent(Ag_Log $"Oracle tool call completed for turn {snapshot.turnId}.")

                { st with
                    pendingToolCalls = st.pendingToolCalls |> Map.remove snapshot.turnId
                    completedToolTurns = st.completedToolTurns.Add snapshot.turnId }

    let private removePendingToolCall callId (st: VoiceState) =
        match
            st.pendingToolCalls
            |> Map.tryPick (fun turnId call -> if call.callId = callId then Some turnId else None)
        with
        | Some turnId ->
            { st with
                pendingToolCalls = st.pendingToolCalls |> Map.remove turnId
                completedToolTurns = st.completedToolTurns.Add turnId }
        | None -> st

    let private handleToolOutputReady (st: VoiceState) callId output =
        let st = removePendingToolCall callId st
        let outputWasBlank = String.IsNullOrWhiteSpace output

        let output =
            if outputWasBlank then
                $"I was unable to obtain an answer from the oracle. The oracle returned empty text with the current max answer token limit of {st.config.answerMaxOutputTokens}. {maxAnswerTokensSettingsGuidance} You can also ask a narrower question."
            else
                output

        if outputWasBlank then
            st.bus.PostToAgent(Ag_Log $"Oracle tool output was blank for call {callId}; using fallback text.")

        if String.IsNullOrWhiteSpace output then
            st
        else
            ContentFunctionCallOutput.Create callId output
            |> createFunctionOutputEvent
            |> SendClientEvent
            |> enqueueOutbound st.outputQueue TOOL_CALL_PRIORITY

            st.bus.PostToAgent(
                Ag_Log
                    $"Oracle tool output ready for call {callId}; output_chars={output.Length}; waiting for realtime acknowledgement before speech."
            )

            { st with
                pendingSpeakByCallId = st.pendingSpeakByCallId |> Map.add callId output }

    let private acknowledgeFunctionOutput (st: VoiceState) (eventName: string) (item: ConversationItem) =
        match item with
        | Function_call_output output ->
            match st.pendingSpeakByCallId |> Map.tryFind output.call_id with
            | None -> st
            | Some text ->
                st.bus.PostToAgent(
                    Ag_Log
                        $"Realtime acknowledged oracle tool output for call {output.call_id} via {eventName}; scheduling speech."
                )

                { st with
                    pendingSpeakByCallId = st.pendingSpeakByCallId |> Map.remove output.call_id
                    pendingSpeakTexts = st.pendingSpeakTexts @ [ text ] }
                |> tryScheduleSpeak
        | _ -> st

    let private runAwaitedToolCall (bus: WBus<FlowMsg, AgentMsg>) (toolCall: VoiceToolCall) =
        async {
            try
                let! result =
                    toolCall.task.Task.WaitAsync(toolCall.timeout, toolCall.cancellation.Token)
                    |> Async.AwaitTask

                bus.PostToAgent(Ag_ToolCallOutputReady(toolCall.callId, result.output))
            with ex ->
                toolCall.cancellation.Cancel()

                let output =
                    $"I was unable to obtain an answer from the oracle before the timeout. The request may have exceeded the current max answer token limit of {toolCall.answerMaxOutputTokens}, or the backend may still be working. {maxAnswerTokensSettingsGuidance} You can also ask a narrower question."

                bus.PostToAgent(Ag_Log $"Oracle tool call timed out for {toolCall.callId}: {ex.Message}")
                bus.PostToAgent(Ag_ToolCallOutputReady(toolCall.callId, output))
        }

    let private updateVoice (st: VoiceState) (ev: ServerEvent) =
        async {
            match ev with
            | ServerEvent.SessionCreated s when not st.initialized ->
                sessionUpdateEvent st.config s.session |> enqueueClientEvent st
                st.bus.PostToAgent(Ag_Log "Realtime session created.")

                return
                    { st with
                        initialized = true
                        realtimeSession = s.session }
            | ServerEvent.SessionUpdated ev ->
                st.bus.PostToAgent(Ag_Log "Realtime session updated.")
                return { st with realtimeSession = ev.session } |> tryScheduleGreeting
            | ServerEvent.ConversationItemAdded ev ->
                return acknowledgeFunctionOutput st "conversation.item.added" ev.item
            | ServerEvent.ConversationItemDone ev ->
                return acknowledgeFunctionOutput st "conversation.item.done" ev.item
            | ServerEvent.ResponseCreated ev ->
                let responseId =
                    match ev.response.id with
                    | Include(Some value) when not (String.IsNullOrWhiteSpace value) -> Some value
                    | _ -> None

                return
                    { st with
                        responseCreatedState = ActiveResponse {| id = responseId |} }
            | ServerEvent.ResponseOutputItemDone responseItem ->
                match responseItem.item with
                | Function_call fc -> return dispatchToolCall st fc
                | _ -> return st
            | ServerEvent.ResponseDone _ ->
                return
                    { st with
                        responseCreatedState = NoActiveResponse }
                    |> tryScheduleAudio
            | ServerEvent.InputAudioBufferSpeechStarted _ ->
                return
                    { st with
                        userSpeechState = Speaking
                        lastFinalUserTranscript = None
                        pendingGreeting = None }
            | ServerEvent.InputAudioBufferSpeechStopped _ ->
                return { st with userSpeechState = Silent } |> tryScheduleAudio
            | ServerEvent.ConversationItemInputAudioTranscriptionDelta ev ->
                let previous =
                    st.transcriptByItem |> Map.tryFind ev.item_id |> Option.defaultValue ""

                let text = previous + ev.delta
                let revision, _ = makeTranscriptSnapshot st ev.item_id text false

                return
                    { st with
                        revision = revision
                        transcriptByItem = st.transcriptByItem |> Map.add ev.item_id text }
            | ServerEvent.ConversationItemInputAudioTranscriptionCompleted ev ->
                let text = Speak2Docs.Text.normalizeWhitespace ev.transcript
                st.bus.PostToAgent(Ag_Log $"User: {text}")
                let revision, snapshot = makeTranscriptSnapshot st ev.item_id text true
                st.bus.PostToAgent(Ag_TranscriptUpdated snapshot)

                return
                    { st with
                        revision = revision
                        lastFinalUserTranscript = Some snapshot
                        transcriptByItem = st.transcriptByItem |> Map.remove ev.item_id }
            | ServerEvent.Error e ->
                if not (isActiveResponseInProgressError e.error.message) then
                    st.bus.PostToAgent(Ag_Log $"Realtime API error: {e.error.message}")

                return st
            | ServerEvent.EventHandlingError(t, msg, _) ->
                st.bus.PostToAgent(Ag_Log $"Realtime event handling error for {t}: {msg}")
                return st
            | _ -> return st
        }

    let private update (st: VoiceState) (msg: AgentMsg) =
        async {
            match msg with
            | Ag_VoiceServerEvent ev -> return! updateVoice st ev
            | Ag_ResponseReady(snapshot, candidate) -> return completeToolCall st snapshot candidate
            | Ag_ToolCallOutputReady(callId, output) -> return handleToolOutputReady st callId output
            | Ag_FlowDone _ ->
                st.outputQueue.Complete()
                return st
            | _ -> return st
        }

    let private startRealtime config (connection: VoiceConnection) (bus: WBus<FlowMsg, AgentMsg>) =
        async {
            updateSession config Session.Default
            |> Ag_RequestRealtimeConnection
            |> bus.PostToAgent

            do!
                connection.receiver.ReadAllAsync()
                |> AsyncSeq.map toServerEvent
                |> AsyncSeq.iter (Ag_VoiceServerEvent >> bus.PostToAgent)
        }

    let private startOutputPump connection (bus: WBus<FlowMsg, AgentMsg>) (outputQueue: AsyncPriorityQueue<Q>) =
        outputQueue.ToAsyncSeq()
        |> AsyncSeq.iterAsync (fun work ->
            async {
                match work with
                | SendClientEvent event ->
                    try
                        do! writeClientEvent connection event |> Async.AwaitTask
                    with ex ->
                        bus.PostToAgent(Ag_Log $"Failed to send realtime client event: {ex.Message}")
                | AwaitToolCall toolCall -> runAwaitedToolCall bus toolCall |> Async.Start
            })

    let private startAgent config outputQueue (bus: WBus<FlowMsg, AgentMsg>) =
        let st =
            { initialized = false
              config = config
              realtimeSession = Session.Default
              transcriptByItem = Map.empty
              lastFinalUserTranscript = None
              revision = 0
              pendingToolCalls = Map.empty
              completedToolTurns = Set.empty
              pendingSpeakByCallId = Map.empty
              userSpeechState = Silent
              responseCreatedState = NoActiveResponse
              pendingGreeting = Some(greetingForSourceCount config.initialSourceCount)
              pendingSpeakTexts = []
              outputQueue = outputQueue
              bus = bus }

        bus.AgentBus.RunAsync("voice", st, update)

    let start plugIn initialSourceCount voiceConnection bus =
        let outputQueue = AsyncPriorityQueue<Q>()
        let config = plugInConfig initialSourceCount plugIn

        async {
            let! outputPump = Async.StartChild(startOutputPump voiceConnection bus outputQueue)
            let! voiceAgent = Async.StartChild(startAgent config outputQueue bus)

            try
                do! startRealtime config voiceConnection bus
            finally
                outputQueue.Complete()

            do! voiceAgent
            do! outputPump
        }
        |> FlowUtils.catch bus.PostToFlow
