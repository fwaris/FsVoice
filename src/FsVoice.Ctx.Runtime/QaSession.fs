namespace FsVoice.Ctx

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open FsVoice.Core
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

type private AnswerPrompt =
    { instructions: string
      userPrompt: string }

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

type private BlackboardPruningCheckpoint =
    { version: int
      selection: BlackboardPruneSelection }

type private AnswerModelResult =
    { answer: string
      observations: QaToolObservation list }

type private ResponseToolCatalog =
    { tools: FsResponses.Tool list
      byName: Map<string, IQaTool> }

type private DisabledMemoryService() =
    interface IMemoryService with
        member _.StartupLogs = []
        member _.DefaultNamespace = DurableMemory.defaultNamespace

        member _.CreateSupervisorDecision(snapshot, judgement) =
            DurableMemory.createSupervisorDecision snapshot judgement

        member _.RecallAsync(_, _) = Task.FromResult []

        member _.SearchAsync(_, _, _) =
            Task.FromResult "Durable memory is disabled for this QA session."

        member _.ProposalsFromExchange(_, _) = []

        member _.CommitProposals _ = [], []

        member _.RetractFromTurn _ = []

        member _.ClearAll() =
            [ "Durable memory is disabled for this QA session." ]

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

    let blackboardGate = obj ()
    let mutable blackboard = Blackboard.empty 120
    let mutable blackboardVersion = 0
    let mutable blackboardPruningInProgress = false
    let mutable blackboardSummarized = false
    let mutable blackboardPruningUnavailableLogged = false
    let sessionCancellation = new CancellationTokenSource()

    let report message = options.report message

    let getBlackboard () =
        lock blackboardGate (fun () -> blackboard)

    let updateBlackboard update =
        lock blackboardGate (fun () ->
            blackboard <- update blackboard
            blackboardVersion <- blackboardVersion + 1
            blackboard)

    let addBlackboardRecord record =
        updateBlackboard (Blackboard.add record) |> ignore

    let addBlackboardRecords records =
        if not (List.isEmpty records) then
            updateBlackboard (Blackboard.addMany records) |> ignore

    let blackboardHasSummary () =
        lock blackboardGate (fun () -> blackboardSummarized)

    let clamp (maxValue: int) (value: int) = Math.Max(1, Math.Min(maxValue, value))

    let renderObservations (observations: QaToolObservation list) =
        if List.isEmpty observations then
            "No tool observations were recorded."
        else
            observations
            |> List.truncate 12
            |> List.mapi (fun index observation ->
                $"[{index + 1}] {observation.pluginName}.{observation.toolName}\n{Text.truncate 900 observation.content}")
            |> String.concat "\n\n"

    let modelConfig role =
        options.modelRoles
        |> Option.ofObj
        |> Option.bind (Map.tryFind role)
        |> Option.orElse (PlugInDefinition.defaultModels |> Map.tryFind role)
        |> Option.defaultValue (ModelRoleConfig.create options.answerModelId)

    let roleMaxTokens role fallback =
        (modelConfig role).maxOutputTokens |> Option.defaultValue fallback

    let emptyAnswerFallback =
        "I could not produce an answer from the selected context. Please try again."

    let maxAnswerTokensSettingsGuidance =
        "Disconnect, open Settings, increase Max Answer Tokens, then reconnect and try again."

    let tokenLimitFallback maxOutputTokens =
        $"I was unable to obtain a complete answer. The answer model appears to have exceeded the current max answer token limit of {maxOutputTokens}. {maxAnswerTokensSettingsGuidance}"

    let emptyAnswerFallbackWithLimit maxOutputTokens =
        $"I was unable to obtain an answer from the oracle. The answer model returned empty text with the current max answer token limit of {maxOutputTokens}. {maxAnswerTokensSettingsGuidance} You can also ask a narrower question."

    let isFallbackAnswer (answer: string) =
        answer = emptyAnswerFallback
        || answer.StartsWith("I was unable to obtain", StringComparison.OrdinalIgnoreCase)

    let nullableValue (value: Nullable<'T>) =
        if value.HasValue then string value.Value else "n/a"

    let responseDiagnostics (response: ChatResponse) =
        let finishReason = response.FinishReason |> nullableValue

        let usage =
            if isNull response.Usage then
                "usage=n/a"
            else
                $"usage=input:{nullableValue response.Usage.InputTokenCount} output:{nullableValue response.Usage.OutputTokenCount} reasoning:{nullableValue response.Usage.ReasoningTokenCount} total:{nullableValue response.Usage.TotalTokenCount}"

        let messageCount =
            if isNull response.Messages then
                0
            else
                response.Messages.Count

        $"finish={finishReason}; {usage}; messages={messageCount}"

    let responseUsageDiagnostics (usage: FsResponses.Usage option) =
        usage
        |> Option.map (fun usage ->
            $"usage=input:{usage.input_tokens} output:{usage.output_tokens} total:{usage.total_tokens}")
        |> Option.defaultValue "usage=n/a"

    let responseEventName event =
        match event with
        | FsResponses.ResponseStreamEvent.ResponseCreated _ -> "response.created"
        | FsResponses.ResponseStreamEvent.ResponseInProgress _ -> "response.in_progress"
        | FsResponses.ResponseStreamEvent.ResponseCompleted _ -> "response.completed"
        | FsResponses.ResponseStreamEvent.ResponseFailed _ -> "response.failed"
        | FsResponses.ResponseStreamEvent.ResponseIncomplete _ -> "response.incomplete"
        | FsResponses.ResponseStreamEvent.OutputItemAdded _ -> "response.output_item.added"
        | FsResponses.ResponseStreamEvent.OutputItemDone _ -> "response.output_item.done"
        | FsResponses.ResponseStreamEvent.ContentPartAdded _ -> "response.content_part.added"
        | FsResponses.ResponseStreamEvent.ContentPartDone _ -> "response.content_part.done"
        | FsResponses.ResponseStreamEvent.OutputTextDelta _ -> "response.output_text.delta"
        | FsResponses.ResponseStreamEvent.OutputTextDone _ -> "response.output_text.done"
        | FsResponses.ResponseStreamEvent.FunctionCallArgumentsDelta _ -> "response.function_call_arguments.delta"
        | FsResponses.ResponseStreamEvent.FunctionCallArgumentsDone _ -> "response.function_call_arguments.done"
        | FsResponses.ResponseStreamEvent.Error _ -> "error"
        | FsResponses.ResponseStreamEvent.Unknown event -> event.eventType

    let responseTerminalResponse events =
        events
        |> List.tryPick (function
            | FsResponses.ResponseStreamEvent.ResponseCompleted event
            | FsResponses.ResponseStreamEvent.ResponseFailed event
            | FsResponses.ResponseStreamEvent.ResponseIncomplete event -> Some event.response
            | _ -> None)

    let responseLifecycleResponse event =
        match event with
        | FsResponses.ResponseStreamEvent.ResponseCreated lifecycle
        | FsResponses.ResponseStreamEvent.ResponseInProgress lifecycle
        | FsResponses.ResponseStreamEvent.ResponseCompleted lifecycle
        | FsResponses.ResponseStreamEvent.ResponseFailed lifecycle
        | FsResponses.ResponseStreamEvent.ResponseIncomplete lifecycle -> Some lifecycle.response
        | _ -> None

    let responseAnyResponse events =
        events |> List.rev |> List.tryPick responseLifecycleResponse

    let responseIdFromEvents events =
        responseAnyResponse events |> Option.map _.id

    let responseError events =
        events
        |> List.tryPick (function
            | FsResponses.ResponseStreamEvent.Error event -> Some event.error
            | _ -> None)

    let responsesDiagnostics events =
        let eventNames =
            events
            |> List.countBy responseEventName
            |> List.map (fun (name, count) -> if count = 1 then name else $"{name}x{count}")
            |> String.concat ","

        let response = responseTerminalResponse events

        let status = response |> Option.map _.status |> Option.defaultValue "n/a"
        let responseId = response |> Option.map _.id |> Option.defaultValue "n/a"
        let usage = response |> Option.bind _.usage |> responseUsageDiagnostics

        let errorCode =
            responseError events |> Option.map _.code |> Option.defaultValue "n/a"

        let errorMessage =
            responseError events
            |> Option.map _.message
            |> Option.map (Text.truncate 240)
            |> Option.defaultValue "n/a"

        $"responseId={responseId}; status={status}; {usage}; error={errorCode}; errorMessage={errorMessage}; events={eventNames}"

    let isResponsesTokenLimit events =
        let reason =
            responseTerminalResponse events
            |> Option.bind _.incomplete_details
            |> Option.map _.reason
            |> Option.defaultValue ""

        let status =
            responseTerminalResponse events |> Option.map _.status |> Option.defaultValue ""

        status.Contains("incomplete", StringComparison.OrdinalIgnoreCase)
        && (reason.Contains("max_output", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("token", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("length", StringComparison.OrdinalIgnoreCase))

    let isPreviousResponseNotFound events =
        responseError events
        |> Option.exists (fun err ->
            err.code.Contains("previous_response_not_found", StringComparison.OrdinalIgnoreCase)
            || err.message.Contains("previous response", StringComparison.OrdinalIgnoreCase))

    let isTokenLimitFinish (response: ChatResponse) =
        if isNull response then
            false
        else
            let finishReason = response.FinishReason |> nullableValue

            finishReason.Contains("length", StringComparison.OrdinalIgnoreCase)
            || finishReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
            || finishReason.Contains("max output", StringComparison.OrdinalIgnoreCase)

    let answerPromptMessages prompt =
        [ ChatMessage(ChatRole.System, prompt.instructions)
          ChatMessage(ChatRole.User, prompt.userPrompt) ]

    let answerReasoning (answerConfig: ModelRoleConfig) =
        answerConfig.reasoningEffort
        |> Option.map (fun effort ->
            { FsResponses.Reasoning.Default with
                effort = Some effort })

    let answerTemperature (answerConfig: ModelRoleConfig) =
        if ModelCapabilities.supportsTemperature answerConfig.modelId then
            answerConfig.temperature |> Option.defaultValue 0.2f |> Some
        else
            None

    let answerUserItem (prompt: AnswerPrompt) =
        FsResponses.IOitem.Message
            { FsResponses.Message.Default with
                role = "user"
                content = [ FsResponses.Content.Input_text {| text = prompt.userPrompt |} ] }

    let answerAssistantItem answer =
        FsResponses.IOitem.Message
            { FsResponses.Message.Default with
                status = Some "completed"
                role = "assistant"
                content = [ FsResponses.Content.Output_text { text = answer; annotations = None } ] }

    let contentText =
        function
        | FsResponses.Content.Input_text text -> text.text
        | FsResponses.Content.Output_text output -> output.text
        | FsResponses.Content.Refusal refusal -> refusal.refusal
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
                              $"Compacted conversation checkpoint. Use this as prior conversation memory, and call blackboard_search if more detail from pre-compaction tool observations is needed.\n\n{summary}" |} ] }

    let sanitizeResponseToolName (value: string) =
        let chars =
            value.Trim()
            |> Seq.map (fun ch ->
                if Char.IsLetterOrDigit ch || ch = '_' || ch = '-' then
                    ch
                else
                    '_')
            |> Seq.toArray

        let name = String(chars).Trim('_')

        if String.IsNullOrWhiteSpace name then "tool"
        elif name.Length <= 64 then name
        else name.Substring(0, 64).TrimEnd('_', '-')

    let responseToolBaseName (tool: IQaTool) =
        if String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase) then
            sanitizeResponseToolName tool.Name
        else
            sanitizeResponseToolName $"{tool.PluginName}_{tool.Name}"

    let responseToolName usedNames (tool: IQaTool) =
        let baseName = responseToolBaseName tool

        let rec choose index =
            let suffix = if index = 0 then "" else $"_{index}"

            let prefix =
                if baseName.Length + suffix.Length <= 64 then
                    baseName
                else
                    baseName.Substring(0, 64 - suffix.Length).TrimEnd('_', '-')

            let candidate = prefix + suffix

            if Set.contains candidate usedNames then
                choose (index + 1)
            else
                candidate

        choose 0

    let responseToolParameterSchema (parameter: QaToolParameter) =
        let description = Some parameter.description

        if
            String.Equals(parameter.name, "max_results", StringComparison.OrdinalIgnoreCase)
            || parameter.name.EndsWith("_count", StringComparison.OrdinalIgnoreCase)
        then
            FsResponses.JsProperty.Integer { description = description }
        else
            FsResponses.JsProperty.String
                { description = description
                  enum = None }

    let responseWarmupRequest (answerConfig: ModelRoleConfig) (prompt: AnswerPrompt) responseTools =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            instructions = Some prompt.instructions
            generate = Some false
            reasoning = answerReasoning answerConfig
            store = Some false
            temperature = answerTemperature answerConfig
            tools = Some responseTools
            tool_choice = Some FsResponses.ToolChoice.Auto }

    let responseCreateRequest
        (answerConfig: ModelRoleConfig)
        maxOutputTokens
        (prompt: AnswerPrompt)
        previousResponseId
        input
        includeStableContext
        responseTools
        =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = input
            instructions =
                if includeStableContext then
                    Some prompt.instructions
                else
                    None
            max_output_tokens = Some maxOutputTokens
            previous_response_id = previousResponseId
            generate = Some true
            reasoning = answerReasoning answerConfig
            store = Some false
            temperature = answerTemperature answerConfig
            tools = if includeStableContext then Some responseTools else None
            tool_choice =
                if includeStableContext then
                    Some FsResponses.ToolChoice.Auto
                else
                    None }

    let responseCompactionRequest (answerConfig: ModelRoleConfig) items =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = [ compactionUserItem items ]
            instructions = Some compactionInstructions
            max_output_tokens = Some options.answerCompactionMaxOutputTokens
            generate = Some true
            reasoning = answerReasoning answerConfig
            store = Some false
            temperature = answerTemperature answerConfig
            tools = Some []
            tool_choice = None }

    let responseConversationRefreshRequest answerConfig (prompt: AnswerPrompt) responseTools items =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = items
            instructions = Some prompt.instructions
            generate = Some false
            reasoning = answerReasoning answerConfig
            store = Some false
            temperature = answerTemperature answerConfig
            tools = Some responseTools
            tool_choice = Some FsResponses.ToolChoice.Auto }

    let renderTemplate replacements (template: string) =
        replacements
        |> List.fold (fun (text: string) (name, value) -> text.Replace("{{" + name + "}}", value)) template

    let isBuiltInContextTool (tool: IQaTool) =
        String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
        && ([ "selected_source_search"
              "source_inventory"
              "durable_memory_search"
              "blackboard_search" ]
            |> List.exists (fun name -> String.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase)))

    let isBlackboardSearchTool (tool: IQaTool) =
        String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
        && String.Equals(tool.Name, "blackboard_search", StringComparison.OrdinalIgnoreCase)

    let isDurableMemorySearchTool (tool: IQaTool) =
        String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
        && String.Equals(tool.Name, "durable_memory_search", StringComparison.OrdinalIgnoreCase)

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

    let renderTypedMemory decision hits =
        let memories = DurableMemory.renderRecall hits
        let policy = DurableMemory.renderRecallSpec decision
        $"Recall policy:\n{policy}\n\nTyped memory evidence:\n{memories}"

    let answerInstructions () =
        options.prompts.answerSystem
        |> Option.orElse options.plugInProfile.answerSystemInstruction
        |> Option.defaultValue DefaultPlugInPrompts.answerSystem

    let answerPrompt
        (snapshot: TranscriptSnapshot)
        (decision: SupervisorDecision)
        (memoryHits: MemoryRecallHit list)
        (chunks: SourceChunk list)
        (observations: QaToolObservation list)
        =
        let sourceContext =
            KnowledgeSources.renderContextWithLimit options.maxContextChunks chunks

        let inventory = KnowledgeSources.renderInventory (contextSources ())
        let typedMemory = renderTypedMemory decision memoryHits
        let toolObservations = renderObservations observations

        let userPrompt =
            options.prompts.answerUserTemplate
            |> Option.defaultValue DefaultPlugInPrompts.answerUserTemplate
            |> renderTemplate
                [ "question", snapshot.text
                  "typedMemory", typedMemory
                  "toolObservations", toolObservations
                  "sourceInventory", inventory
                  "sourceContext", sourceContext ]

        { instructions = answerInstructions ()
          userPrompt = userPrompt }

    let createSnapshot (request: QaTurnRequest) =
        { turnId = request.turnId
          itemId = request.turnId
          revision = 1
          text = Text.normalizeWhitespace request.question
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow }

    let host =
        { new IQaToolHost with
            member _.Report message = report message

            member _.SearchKnowledgeAsync(question, maxResults, cancellationToken) =
                retrieveContext question maxResults cancellationToken
                |> Async.StartAsTask
                |> fun task ->
                    task.ContinueWith(
                        (fun (t: Task<SourceChunk list>) -> KnowledgeSources.renderContext t.Result),
                        cancellationToken
                    )

            member _.SourceInventoryAsync cancellationToken = contextInventory cancellationToken

            member _.SearchMemoryAsync(query, maxResults, cancellationToken) =
                memoryService.SearchAsync(query, maxResults, cancellationToken)

            member _.SearchBlackboardAsync(query, cancellationToken) =
                task {
                    let query = Text.normalizeWhitespace query
                    let board = getBlackboard ()

                    let options =
                        { BlackboardSearchOptions.defaults with
                            maxResults = 8
                            includeKinds =
                                [ ToolObservation
                                  MemoryEvidence
                                  SourceEvidence
                                  Conflict
                                  FinalAnswer
                                  CompactedSummary ] }

                    let lexicalHits = Blackboard.search options query board

                    let! semanticHits =
                        match currentMemoryEncoder () with
                        | Some encoder when not (String.IsNullOrWhiteSpace query) ->
                            BlackboardSemantic.search encoder options query board
                            |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
                        | _ -> Task.FromResult []

                    return
                        lexicalHits @ semanticHits
                        |> Blackboard.mergeHits options.maxResults
                        |> Blackboard.renderHits
                } }

    let loadToolCatalog () =
        let loaded =
            QaToolLoader.loadWithProviders host options.toolProviderDirectory options.toolProviders

        if options.enableDurableMemory then
            loaded
        else
            { loaded with
                tools = loaded.tools |> List.filter (isDurableMemorySearchTool >> not) }

    let mutable catalog = loadToolCatalog ()

    let responseToolCatalog includeBlackboard =
        let folder (usedNames, byName, tools) (tool: IQaTool) =
            let name = responseToolName usedNames tool

            let parameters =
                { FsResponses.Parameters.Default with
                    properties =
                        tool.Parameters
                        |> List.map (fun parameter -> parameter.name, responseToolParameterSchema parameter)
                        |> Map.ofList
                    required = tool.Parameters |> List.map _.name
                    additionalProperties = false }

            let responseTool =
                FsResponses.Tool.Function
                    { FsResponses.Function.Default with
                        name = name
                        description = tool.Description
                        parameters = parameters
                        strict = true }

            Set.add name usedNames, Map.add name tool byName, responseTool :: tools

        let _, byName, tools =
            catalog.tools
            |> List.filter (fun tool -> includeBlackboard || not (isBlackboardSearchTool tool))
            |> List.sortBy (fun tool -> tool.PluginName, tool.Name)
            |> List.fold folder (Set.empty, Map.empty, [])

        { tools = List.rev tools
          byName = byName }

    let answerConnectionGate = obj ()
    let mutable answerConnection: FsResponses.ResponseWebSocket option = None
    let mutable answerConnectionTask: Task<FsResponses.ResponseWebSocket> option = None
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

    do
        for log in memoryService.StartupLogs @ catalog.logs do
            report log

    let answerConnectionIsOpen (connection: FsResponses.ResponseWebSocket) =
        connection.socket.State = Net.WebSockets.WebSocketState.Open

    let startAnswerConnection config cancellationToken =
        let task = FsResponses.ResponsesWebSocket.connect config cancellationToken
        answerConnectionTask <- Some task
        task

    let clearCompletedAnswerConnectionTask (task: Task<FsResponses.ResponseWebSocket>) =
        if task.IsCompleted then
            lock answerConnectionGate (fun () ->
                match answerConnectionTask with
                | Some current when Object.ReferenceEquals(current, task) -> answerConnectionTask <- None
                | _ -> ())

    let liveAnswerConnection config cancellationToken =
        task {
            let connectionOrTask =
                lock answerConnectionGate (fun () ->
                    match answerConnection with
                    | Some connection when answerConnectionIsOpen connection -> Choice1Of2 connection
                    | Some connection ->
                        FsResponses.ResponsesWebSocket.dispose connection
                        answerConnection <- None

                        match answerConnectionTask with
                        | Some task -> Choice2Of2 task
                        | None -> startAnswerConnection config cancellationToken |> Choice2Of2
                    | None ->
                        match answerConnectionTask with
                        | Some task -> Choice2Of2 task
                        | None -> startAnswerConnection config cancellationToken |> Choice2Of2)

            match connectionOrTask with
            | Choice1Of2 connection -> return connection
            | Choice2Of2 connectionTask ->
                try
                    let! connection = connectionTask.WaitAsync(cancellationToken)

                    lock answerConnectionGate (fun () ->
                        answerConnection <- Some connection

                        match answerConnectionTask with
                        | Some current when Object.ReferenceEquals(current, connectionTask) ->
                            answerConnectionTask <- None
                        | _ -> ())

                    return connection
                with ex ->
                    clearCompletedAnswerConnectionTask connectionTask
                    return raise ex
        }

    let runResponsesRequest request cancellationToken =
        task {
            match options.answerTransport with
            | Some(QaAnswerTransport.CustomResponsesWebSocket createAndCollect) ->
                return! createAndCollect request cancellationToken
            | Some(QaAnswerTransport.OpenAIResponsesWebSocket config) ->
                let! connection = liveAnswerConnection config cancellationToken
                return! FsResponses.ResponsesWebSocket.createAndCollect connection request cancellationToken
            | None -> return []
        }

    let runResponsesWarmupRequest request cancellationToken =
        task {
            match options.answerTransport with
            | Some(QaAnswerTransport.CustomResponsesWebSocket createAndCollect) ->
                return! createAndCollect request cancellationToken
            | Some(QaAnswerTransport.OpenAIResponsesWebSocket config) ->
                let! connection = liveAnswerConnection config cancellationToken
                return! FsResponses.ResponsesWebSocket.createAndCollect connection request cancellationToken
            | None -> return []
        }

    let runResponsesOfflineRequest request cancellationToken =
        task {
            match options.answerTransport with
            | Some(QaAnswerTransport.CustomResponsesWebSocket createAndCollect) ->
                return! createAndCollect request cancellationToken
            | Some(QaAnswerTransport.OpenAIResponsesWebSocket config) ->
                let! connection = FsResponses.ResponsesWebSocket.connect config cancellationToken

                try
                    return! FsResponses.ResponsesWebSocket.createAndCollect connection request cancellationToken
                finally
                    FsResponses.ResponsesWebSocket.dispose connection
            | None -> return []
        }

    let jsonElementArgument (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String -> element.GetString() |> Option.ofObj |> Option.defaultValue ""
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> ""
        | _ -> element.GetRawText()

    let functionArgumentsToDictionary (arguments: string) =
        let dict = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        if String.IsNullOrWhiteSpace arguments then
            Ok(dict :> IReadOnlyDictionary<string, string>)
        else
            try
                use document = JsonDocument.Parse(arguments)

                if document.RootElement.ValueKind <> JsonValueKind.Object then
                    Error "Function call arguments must be a JSON object."
                else
                    for property in document.RootElement.EnumerateObject() do
                        dict[property.Name] <- jsonElementArgument property.Value

                    Ok(dict :> IReadOnlyDictionary<string, string>)
            with ex ->
                Error $"Function call arguments were not valid JSON: {ex.Message}"

    let responseFunctionCalls events =
        let fromItem =
            function
            | FsResponses.IOitem.Function_call call -> Some call
            | _ -> None

        [ for event in events do
              match event with
              | FsResponses.ResponseStreamEvent.OutputItemDone itemEvent ->
                  match fromItem itemEvent.item with
                  | Some call -> yield call
                  | None -> ()
              | FsResponses.ResponseStreamEvent.ResponseCompleted lifecycle ->
                  for item in lifecycle.response.output do
                      match fromItem item with
                      | Some call -> yield call
                      | None -> ()
              | _ -> () ]
        |> List.distinctBy _.call_id

    let responseFunctionOutput callId output =
        FsResponses.IOitem.Function_call_output { call_id = callId; output = output }

    let responseFunctionQuery (call: FsResponses.FunctionCall) (args: IReadOnlyDictionary<string, string>) =
        ToolArguments.tryString "query" args
        |> Option.orElse (ToolArguments.tryString "question" args)
        |> Option.defaultValue call.arguments
        |> Text.truncate 240

    let addResponseToolObservation turnId (tool: IQaTool) query content =
        let observation =
            { pluginName = tool.PluginName
              toolName = tool.Name
              query = query
              content = content
              createdAt = DateTimeOffset.UtcNow }

        addBlackboardRecord (BlackboardRecords.toolObservation turnId observation)

        observation

    let invokeResponseFunctionCall
        turnId
        (responseTools: ResponseToolCatalog)
        (call: FsResponses.FunctionCall)
        cancellationToken
        =
        task {
            match responseTools.byName |> Map.tryFind call.name with
            | None ->
                let output = $"Tool '{call.name}' is not available in this QA session."
                report output
                return responseFunctionOutput call.call_id output, None
            | Some tool ->
                match functionArgumentsToDictionary call.arguments with
                | Error error ->
                    let content = $"Tool {tool.PluginName}.{tool.Name} could not run: {error}"
                    let observation = addResponseToolObservation turnId tool call.arguments content
                    report content
                    return responseFunctionOutput call.call_id content, Some observation
                | Ok args ->
                    let query = responseFunctionQuery call args

                    try
                        let! result = tool.InvokeAsync(args, cancellationToken)
                        let observation = addResponseToolObservation turnId tool query result.content
                        return responseFunctionOutput call.call_id result.content, Some observation
                    with ex ->
                        let content = $"Tool {tool.PluginName}.{tool.Name} failed: {ex.Message}"
                        let observation = addResponseToolObservation turnId tool query content
                        report content
                        return responseFunctionOutput call.call_id content, Some observation
        }

    let invokeResponseFunctionCalls turnId responseTools calls cancellationToken =
        task {
            let boundedCalls = calls |> List.truncate 8

            let! results =
                boundedCalls
                |> List.map (fun call -> invokeResponseFunctionCall turnId responseTools call cancellationToken)
                |> Task.WhenAll

            let outputs, observations = results |> Array.toList |> List.unzip
            return outputs, observations |> List.choose id
        }

    let clearCompletedAnswerBootstrapTask (task: Task<string option>) =
        if task.IsCompleted then
            lock answerBootstrapGate (fun () ->
                match answerBootstrapTask with
                | Some current when Object.ReferenceEquals(current, task) -> answerBootstrapTask <- None
                | _ -> ())

    let runAnswerBootstrap answerConfig prompt responseTools cancellationToken =
        task {
            let request = responseWarmupRequest answerConfig prompt responseTools.tools
            let! events = runResponsesWarmupRequest request cancellationToken

            match responseIdFromEvents events with
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
                    $"Answer Responses WebSocket warmup did not return a response id; sending stable instructions and tools with the next turn. {responsesDiagnostics events}."

                return None
        }

    let ensureAnswerBootstrap answerConfig prompt responseTools cancellationToken =
        async {
            match (getAnswerConversation ()).previousResponseId with
            | Some previousResponseId -> return Some previousResponseId
            | None ->
                let bootstrapTask =
                    lock answerBootstrapGate (fun () ->
                        match (getAnswerConversation ()).previousResponseId with
                        | Some previousResponseId -> Task.FromResult(Some previousResponseId)
                        | None ->
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
                    clearCompletedAnswerBootstrapTask bootstrapTask
                    return raise ex
        }

    let updateAnswerConversation userItem answer events =
        responseIdFromEvents events
        |> Option.iter (fun responseId ->
            updateAnswerConversationState (fun state ->
                { state with
                    previousResponseId = Some responseId
                    items = state.items @ [ userItem; answerAssistantItem answer ]
                    version = state.version + 1 })
            |> ignore)

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

    let tryCreateCompactionCheckpoint threshold =
        lock answerConversationGate (fun () ->
            let size = conversationSize answerConversation.items

            if
                answerConversation.compactionInProgress
                || List.isEmpty answerConversation.items
                || size <= threshold
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
                let! compactionEvents = runResponsesOfflineRequest compactionRequest token |> Async.AwaitTask

                let summary =
                    FsResponses.ResponseStream.outputText compactionEvents
                    |> Text.normalizeWhitespace

                if String.IsNullOrWhiteSpace summary then
                    report
                        $"Answer conversation compaction returned empty text; keeping existing response history. {responsesDiagnostics compactionEvents}."

                    markCompactionFinished checkpoint.generation
                else
                    match tryCreateCompactionRefreshInput checkpoint summary with
                    | None ->
                        report "Answer conversation compaction was discarded because the QA session was reconfigured."
                        markCompactionFinished checkpoint.generation
                    | Some(refreshedItems, refreshVersion) ->
                        let responseTools = responseToolCatalog true

                        let refreshRequest =
                            responseConversationRefreshRequest answerConfig prompt responseTools.tools refreshedItems

                        let! refreshEvents = runResponsesOfflineRequest refreshRequest token |> Async.AwaitTask

                        match responseIdFromEvents refreshEvents with
                        | Some responseId ->
                            if tryApplyCompaction checkpoint refreshVersion refreshedItems responseId then
                                report
                                    $"Answer conversation compaction applied: compacted_items={checkpoint.itemCount}; retained_tail={refreshedItems.Length - 1}; summary_chars={summary.Length}; responseId={responseId}."
                            else
                                report
                                    "Answer conversation compaction finished, but new turns arrived before the compacted response root could be applied; a later turn will retry if needed."

                                scheduleAnswerCompactionIfNeeded answerConfig prompt
                        | None ->
                            report
                                $"Answer conversation compaction could not refresh the response root; keeping existing response history. {responsesDiagnostics refreshEvents}."

                            markCompactionFinished checkpoint.generation
            with
            | :? OperationCanceledException -> markCompactionFinished checkpoint.generation
            | ex ->
                report $"Answer conversation compaction failed: {ex.Message}"
                markCompactionFinished checkpoint.generation
        }

    and scheduleAnswerCompactionIfNeeded answerConfig prompt =
        match options.answerTransport, options.answerCompactionThresholdChars with
        | Some _, Some threshold when threshold > 0 ->
            match tryCreateCompactionCheckpoint threshold with
            | Some checkpoint ->
                Async.Start(runAnswerCompaction answerConfig prompt checkpoint, sessionCancellation.Token)
            | None -> ()
        | _ -> ()

    let blackboardPrunePolicy () =
        { triggerChars = max 1 options.blackboardPruning.triggerChars
          targetChars = max 1 options.blackboardPruning.targetChars
          preserveRecentTurns = max 0 options.blackboardPruning.preserveRecentTurns }

    let blackboardSummarizerAvailable () =
        options.answerTransport.IsSome || options.clients.answerGenerator.IsSome

    let tryReportBlackboardPruningUnavailable () =
        let shouldReport =
            lock blackboardGate (fun () ->
                let totalChars = Blackboard.totalTextChars blackboard

                if
                    options.blackboardPruning.enabled
                    && not blackboardPruningUnavailableLogged
                    && totalChars > options.blackboardPruning.triggerChars
                then
                    blackboardPruningUnavailableLogged <- true
                    Some totalChars
                else
                    None)

        shouldReport
        |> Option.iter (fun totalChars ->
            report
                $"Blackboard pruning skipped because no model summarizer is configured: chars={totalChars}; trigger={options.blackboardPruning.triggerChars}.")

    let tryCreateBlackboardPruningCheckpoint () =
        if not options.blackboardPruning.enabled then
            None
        elif not (blackboardSummarizerAvailable ()) then
            tryReportBlackboardPruningUnavailable ()
            None
        else
            lock blackboardGate (fun () ->
                if blackboardPruningInProgress then
                    None
                else
                    match Blackboard.tryCreatePruneSelection (blackboardPrunePolicy ()) blackboard with
                    | None -> None
                    | Some selection ->
                        blackboardPruningInProgress <- true

                        Some
                            { version = blackboardVersion
                              selection = selection })

    let markBlackboardPruningFinished () =
        lock blackboardGate (fun () -> blackboardPruningInProgress <- false)

    let blackboardSummaryInstructions =
        "Summarize pruned QA blackboard records for future blackboard_search use. Preserve user goals and corrections, prior final answers, source-grounded findings with source names or chunk hints, tool results, durable-memory forget/retract observations, unresolved follow-ups, and conflicts. Do not invent facts. Write compact bullets grouped by topic."

    let renderBlackboardSummaryRecord index (record: BlackboardRecord) =
        let kind = BlackboardEntryKind.displayName record.kind
        let score = record.score |> Option.map (sprintf "%.2f") |> Option.defaultValue "n/a"

        $"[{index + 1}] id={record.id}; turn={record.turnId}; kind={kind}; created={record.createdAt:O}; score={score}\n{Text.truncate 3000 record.text}"

    let blackboardSummaryUserText (selection: BlackboardPruneSelection) =
        let records =
            selection.recordsToSummarize
            |> List.sortBy _.createdAt
            |> List.mapi renderBlackboardSummaryRecord
            |> String.concat "\n\n"

        let dropOnly =
            selection.recordsToDrop
            |> List.map (fun record -> $"{record.id}:{BlackboardEntryKind.displayName record.kind}")
            |> String.concat ", "

        let preservedTurnIds = selection.preservedTurnIds |> String.concat ", "

        $"Create one compact in-session blackboard summary for these records. Covered records will be removed after the summary is accepted; the summary must be useful for later search.\n\nTotal blackboard chars before pruning: {selection.totalChars}\nTarget chars after pruning: {selection.targetChars}\nPreserved recent turn ids: {preservedTurnIds}\nDrop-only operational records not included in the summary: {dropOnly}\n\nRecords to summarize:\n\n{records}"

    let blackboardSummaryRequest answerConfig selection =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = [ FsResponses.IOitem.Message(FsResponses.Message.OfText(blackboardSummaryUserText selection)) ]
            instructions = Some blackboardSummaryInstructions
            max_output_tokens = Some(max 1 options.blackboardPruning.summaryMaxOutputTokens)
            generate = Some true
            reasoning = answerReasoning answerConfig
            store = Some false
            temperature = answerTemperature answerConfig
            tools = Some []
            tool_choice = None }

    let summarizeBlackboardSelection selection cancellationToken =
        async {
            let answerConfig = modelConfig Answer

            match options.answerTransport with
            | Some _ ->
                let request = blackboardSummaryRequest answerConfig selection
                let! events = runResponsesOfflineRequest request cancellationToken |> Async.AwaitTask

                let summary =
                    FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace

                if String.IsNullOrWhiteSpace summary then
                    report
                        $"Blackboard pruning summary returned empty text; keeping existing blackboard records. {responsesDiagnostics events}."

                    return None
                elif responseError events |> Option.isSome then
                    report
                        $"Blackboard pruning summary returned error; keeping existing blackboard records. {responsesDiagnostics events}."

                    return None
                else
                    return Some summary
            | None ->
                match options.clients.answerGenerator with
                | None -> return None
                | Some client ->
                    let opts = ChatOptions()
                    opts.MaxOutputTokens <- Nullable(max 1 options.blackboardPruning.summaryMaxOutputTokens)

                    if ModelCapabilities.supportsTemperature answerConfig.modelId then
                        opts.Temperature <- Nullable(answerConfig.temperature |> Option.defaultValue 0.2f)

                    let messages =
                        [ ChatMessage(ChatRole.System, blackboardSummaryInstructions)
                          ChatMessage(ChatRole.User, blackboardSummaryUserText selection) ]

                    let! response = client.GetResponseAsync(messages, opts, cancellationToken) |> Async.AwaitTask
                    let summary = response.Text |> Text.normalizeWhitespace

                    if String.IsNullOrWhiteSpace summary then
                        report
                            $"Blackboard pruning summary returned empty text; keeping existing blackboard records. {responseDiagnostics response}."

                        return None
                    else
                        return Some summary
        }

    let tryApplyBlackboardPruning checkpoint summaryText =
        let selectedIds =
            checkpoint.selection.recordsToSummarize @ checkpoint.selection.recordsToDrop
            |> List.map _.id
            |> Set.ofList

        let summaryRecord =
            summaryText
            |> Blackboard.summaryFromSelection checkpoint.selection
            |> BlackboardRecords.compactedSummary

        lock blackboardGate (fun () ->
            let currentIds = blackboard.records |> List.map _.id |> Set.ofList

            let selectedStillPresent =
                selectedIds |> Set.forall (fun id -> currentIds.Contains id)

            let protectedTurnIds =
                Blackboard.recentTurnIds options.blackboardPruning.preserveRecentTurns blackboard
                |> Set.ofList

            let selectedNowProtected =
                blackboard.records
                |> List.exists (fun record ->
                    selectedIds.Contains record.id
                    && record.kind <> CompactedSummary
                    && protectedTurnIds.Contains record.turnId)

            if selectedStillPresent && not selectedNowProtected then
                blackboard <- Blackboard.applyPruneSelection checkpoint.selection summaryRecord blackboard
                blackboardVersion <- blackboardVersion + 1
                blackboardPruningInProgress <- false
                blackboardSummarized <- true
                true
            else
                blackboardPruningInProgress <- false
                false)

    let runBlackboardPruning checkpoint =
        async {
            try
                let token = sessionCancellation.Token

                report
                    $"Blackboard pruning started: records={checkpoint.selection.recordsToSummarize.Length}; drop_only={checkpoint.selection.recordsToDrop.Length}; chars={checkpoint.selection.totalChars}; version={checkpoint.version}."

                let! summary = summarizeBlackboardSelection checkpoint.selection token

                match summary with
                | None -> markBlackboardPruningFinished ()
                | Some summaryText ->
                    if tryApplyBlackboardPruning checkpoint summaryText then
                        report
                            $"Blackboard pruning applied: summarized_records={checkpoint.selection.recordsToSummarize.Length}; dropped_records={checkpoint.selection.recordsToDrop.Length}; summary_chars={summaryText.Length}."
                    else
                        report
                            "Blackboard pruning summary was discarded because the selected records are no longer safe to replace."
            with
            | :? OperationCanceledException -> markBlackboardPruningFinished ()
            | ex ->
                report $"Blackboard pruning failed: {ex.Message}"
                markBlackboardPruningFinished ()
        }

    let scheduleBlackboardPruningIfNeeded () =
        match tryCreateBlackboardPruningCheckpoint () with
        | Some checkpoint -> Async.Start(runBlackboardPruning checkpoint, sessionCancellation.Token)
        | None -> ()

    let responsesCreateAndComplete
        turnId
        answerConfig
        maxOutputTokens
        prompt
        responseTools
        previousResponseId
        input
        includeStableContext
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
                    includeStableContext
                    responseTools.tools

            let! initialEvents = runResponsesRequest request cancellationToken |> Async.AwaitTask

            let rec complete remainingToolTurns events observations =
                async {
                    let calls = responseFunctionCalls events

                    if List.isEmpty calls then
                        return
                            events,
                            FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace,
                            observations
                    elif remainingToolTurns <= 0 then
                        report
                            $"Answer Responses WebSocket stopped after reaching the tool-call iteration limit; pendingToolCalls={calls.Length}; {responsesDiagnostics events}."

                        return
                            events,
                            FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace,
                            observations
                    else
                        match responseIdFromEvents events with
                        | None ->
                            report
                                $"Answer Responses WebSocket produced tool calls without a response id; pendingToolCalls={calls.Length}; {responsesDiagnostics events}."

                            return
                                events,
                                FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace,
                                observations
                        | Some responseId ->
                            let! outputs, newObservations =
                                invokeResponseFunctionCalls turnId responseTools calls cancellationToken
                                |> Async.AwaitTask

                            let followUpRequest =
                                responseCreateRequest
                                    answerConfig
                                    maxOutputTokens
                                    prompt
                                    (Some responseId)
                                    outputs
                                    false
                                    responseTools.tools

                            let! nextEvents = runResponsesRequest followUpRequest cancellationToken |> Async.AwaitTask
                            return! complete (remainingToolTurns - 1) nextEvents (observations @ newObservations)
                }

            return! complete 6 initialEvents []
        }

    let answerWithResponsesWebSocket
        (snapshot: TranscriptSnapshot)
        (decision: SupervisorDecision)
        (memoryHits: MemoryRecallHit list)
        (chunks: SourceChunk list)
        (observations: QaToolObservation list)
        cancellationToken
        =
        async {
            let answerConfig = modelConfig Answer
            let prompt = answerPrompt snapshot decision memoryHits chunks observations
            let userItem = answerUserItem prompt

            let answerAttempt maxOutputTokens replayHistory =
                async {
                    let responseTools =
                        responseToolCatalog (answerConversationHasCompacted () || blackboardHasSummary ())

                    let! previousResponseId = ensureAnswerBootstrap answerConfig prompt responseTools cancellationToken

                    let! events, answer, toolObservations =
                        responsesCreateAndComplete
                            snapshot.turnId
                            answerConfig
                            maxOutputTokens
                            prompt
                            responseTools
                            previousResponseId
                            (responsesInputItems userItem replayHistory)
                            previousResponseId.IsNone
                            cancellationToken

                    if isPreviousResponseNotFound events && previousResponseId.IsSome then
                        report
                            $"Answer Responses WebSocket previous_response_id was not found; retrying from local append-only history: previousResponseId={previousResponseId.Value}; historyItems={(getAnswerConversation ()).items.Length}; {responsesDiagnostics events}."

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
                                replayPreviousResponseId.IsNone
                                cancellationToken
                    else
                        return events, answer, toolObservations
                }

            let maxOutputTokens = roleMaxTokens Answer 2500
            let! events, answer, toolObservations = answerAttempt maxOutputTokens false

            if isResponsesTokenLimit events then
                report
                    $"Answer Responses WebSocket hit output token limit: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; answer_chars={answer.Length}; contextChunks={chunks.Length}; {responsesDiagnostics events}."

                return
                    { answer = tokenLimitFallback maxOutputTokens
                      observations = toolObservations }
            elif responseError events |> Option.isSome then
                report
                    $"Answer Responses WebSocket returned error: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; contextChunks={chunks.Length}; {responsesDiagnostics events}."

                return
                    { answer = emptyAnswerFallbackWithLimit maxOutputTokens
                      observations = toolObservations }
            elif not (String.IsNullOrWhiteSpace answer) then
                updateAnswerConversation userItem answer events
                scheduleAnswerCompactionIfNeeded answerConfig prompt

                return
                    { answer = answer
                      observations = toolObservations }
            else
                let retryMaxOutputTokens = max 1200 (maxOutputTokens * 2)
                let! retryEvents, retryAnswer, retryToolObservations = answerAttempt retryMaxOutputTokens false

                if isResponsesTokenLimit retryEvents then
                    report
                        $"Answer Responses WebSocket retry hit output token limit: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; answer_chars={retryAnswer.Length}; contextChunks={chunks.Length}; {responsesDiagnostics retryEvents}."

                    return
                        { answer = tokenLimitFallback retryMaxOutputTokens
                          observations = retryToolObservations }
                elif responseError retryEvents |> Option.isSome then
                    report
                        $"Answer Responses WebSocket retry returned error: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; contextChunks={chunks.Length}; {responsesDiagnostics retryEvents}."

                    return
                        { answer = emptyAnswerFallbackWithLimit retryMaxOutputTokens
                          observations = retryToolObservations }
                elif not (String.IsNullOrWhiteSpace retryAnswer) then
                    updateAnswerConversation userItem retryAnswer retryEvents
                    scheduleAnswerCompactionIfNeeded answerConfig prompt

                    return
                        { answer = retryAnswer
                          observations = retryToolObservations }
                else
                    report
                        $"Answer Responses WebSocket returned empty text after retry: model={answerConfig.modelId}; initialMaxOutputTokens={maxOutputTokens}; retryMaxOutputTokens={retryMaxOutputTokens}; contextChunks={chunks.Length}; initial=({responsesDiagnostics events}); retry=({responsesDiagnostics retryEvents})."

                    return
                        { answer = emptyAnswerFallbackWithLimit retryMaxOutputTokens
                          observations = retryToolObservations }
        }

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
                return! answerWithResponsesWebSocket snapshot decision memoryHits chunks observations cancellationToken
            | None ->
                match options.clients.answerGenerator with
                | None ->
                    return
                        { answer = "No answer model is configured for this QA session."
                          observations = [] }
                | Some client ->
                    let answerConfig = modelConfig Answer

                    let prompt = answerPrompt snapshot decision memoryHits chunks observations
                    let messages = answerPromptMessages prompt

                    let answerAttempt maxOutputTokens =
                        async {
                            let opts = ChatOptions()

                            if ModelCapabilities.supportsTemperature answerConfig.modelId then
                                opts.Temperature <- Nullable(answerConfig.temperature |> Option.defaultValue 0.2f)

                            opts.MaxOutputTokens <- Nullable(maxOutputTokens)

                            let! response =
                                client.GetResponseAsync(messages, opts, cancellationToken) |> Async.AwaitTask

                            return response, response.Text |> Text.normalizeWhitespace
                        }

                    let maxOutputTokens = roleMaxTokens Answer 2500
                    let! response, answer = answerAttempt maxOutputTokens

                    if isTokenLimitFinish response then
                        report
                            $"Answer model hit output token limit: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; answer_chars={answer.Length}; contextChunks={chunks.Length}; {responseDiagnostics response}."

                        return
                            { answer = tokenLimitFallback maxOutputTokens
                              observations = [] }
                    elif not (String.IsNullOrWhiteSpace answer) then
                        return { answer = answer; observations = [] }
                    else
                        report
                            $"Answer model returned empty text: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; contextChunks={chunks.Length}; {responseDiagnostics response}."

                        let retryMaxOutputTokens = max 1200 (maxOutputTokens * 2)
                        let! retryResponse, retryAnswer = answerAttempt retryMaxOutputTokens

                        if isTokenLimitFinish retryResponse then
                            report
                                $"Answer model retry hit output token limit: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; answer_chars={retryAnswer.Length}; contextChunks={chunks.Length}; {responseDiagnostics retryResponse}."

                            return
                                { answer = tokenLimitFallback retryMaxOutputTokens
                                  observations = [] }
                        elif not (String.IsNullOrWhiteSpace retryAnswer) then
                            report
                                $"Answer model retry succeeded after empty response: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; answer_chars={retryAnswer.Length}; {responseDiagnostics retryResponse}."

                            return
                                { answer = retryAnswer
                                  observations = [] }
                        else
                            report
                                $"Answer model retry also returned empty text: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; contextChunks={chunks.Length}; {responseDiagnostics retryResponse}."

                            return
                                { answer = emptyAnswerFallbackWithLimit retryMaxOutputTokens
                                  observations = [] }
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
            && not (isFallbackAnswer answer)
        then
            let proposals = memoryService.ProposalsFromExchange(snapshot, answer)
            let updates, logs = memoryService.CommitProposals proposals

            for update in updates do
                report update.message

            for log in logs do
                report log

    member _.ToolCatalog = catalog

    member _.PrepareAnswerTransportAsync(cancellationToken) =
        task {
            match options.answerTransport with
            | Some _ ->
                use linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionCancellation.Token)

                let token = linkedCts.Token
                let answerConfig = modelConfig Answer

                let prompt =
                    { instructions = answerInstructions ()
                      userPrompt = "" }

                let responseTools = responseToolCatalog false

                let! _ =
                    ensureAnswerBootstrap answerConfig prompt responseTools token
                    |> Async.StartAsTask

                return ()
            | None -> return ()
        }

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

            updateAnswerConversationState (fun state -> emptyAnswerConversation (state.generation + 1))
            |> ignore

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
            let snapshot = createSnapshot request

            addBlackboardRecord (BlackboardRecords.transcript snapshot)

            request.realtimeJudgement
            |> Option.iter (fun judgement ->
                addBlackboardRecord (BlackboardRecords.realtimeJudgement snapshot.turnId judgement))

            let decision =
                memoryService.CreateSupervisorDecision(snapshot, request.realtimeJudgement)

            addBlackboardRecord (BlackboardRecords.recallDecision snapshot.turnId decision)

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

            addBlackboardRecords (
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

            addBlackboardRecords
                [ BlackboardRecords.answerCandidate snapshot.turnId answer
                  BlackboardRecords.finalAnswer qaAnswer ]

            scheduleBlackboardPruningIfNeeded ()

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

            answerConnection |> Option.iter FsResponses.ResponsesWebSocket.dispose
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
