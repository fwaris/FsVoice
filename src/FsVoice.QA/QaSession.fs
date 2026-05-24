namespace FsVoice.QA

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

type QaModelClients =
    { queryExpansion: IChatClient option
      toolPlanner: IChatClient option
      answerGenerator: IChatClient option }

module QaModelClients =
    let none =
        { queryExpansion = None
          toolPlanner = None
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
      enableToolPlanner: bool
      enableQueryExpansion: bool
      logTimings: bool
      logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      autoWriteback: bool
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
          enableToolPlanner = true
          enableQueryExpansion = false
          logTimings = false
          logExpansions = false
          logChunks = false
          useLexicalFilter = true
          autoWriteback = true
          report = ignore }

type private PlannedToolCall =
    { tool: IQaTool
      query: string
      maxResults: int
      arguments: IReadOnlyDictionary<string, string> }

type private PlannedToolCallDto =
    { plugin: string option
      tool: string option
      ``function``: string option
      query: string option
      max_results: int option
      arguments: Map<string, string> option }

type private ToolPlanDto =
    { calls: PlannedToolCallDto list option }

type private AnswerPrompt =
    { instructions: string
      userPrompt: string }

type private AnswerConversationState =
    { previousResponseId: string option
      items: FsResponses.IOitem list }

type private AnswerModelResult =
    { answer: string
      observations: QaToolObservation list }

type private ResponseToolCatalog =
    { tools: FsResponses.Tool list
      byName: Map<string, IQaTool> }

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
        options.memoryService
        |> Option.defaultValue (DurableMemoryService(memoryPath, currentMemoryEncoder) :> IMemoryService)

    let mutable blackboard = Blackboard.empty 120

    let report message = options.report message

    let findTool pluginName toolName (catalog: QaToolCatalog) =
        catalog.tools
        |> List.tryFind (fun tool ->
            String.Equals(tool.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
            && String.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))

    let clamp (maxValue: int) (value: int) = Math.Max(1, Math.Min(maxValue, value))

    let lower (value: string) =
        (defaultArg (Option.ofObj value) "").ToLowerInvariant()

    let containsAny (needles: string list) (haystack: string) =
        let haystack = lower haystack
        needles |> List.exists haystack.Contains

    let makeArgs (values: (string * string) list) =
        let dict = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        for key, value in values do
            dict[key] <- value

        dict :> IReadOnlyDictionary<string, string>

    let argsToMap (args: IReadOnlyDictionary<string, string>) =
        args |> Seq.map (fun (KeyValue(name, value)) -> name, value) |> Map.ofSeq

    let plannedToolSummary (call: PlannedToolCall) =
        { pluginName = call.tool.PluginName
          toolName = call.tool.Name
          query = call.query
          maxResults = call.maxResults
          arguments = argsToMap call.arguments }

    let renderToolInventory (catalog: QaToolCatalog) =
        catalog.tools
        |> List.sortBy (fun tool -> tool.PluginName, tool.Name)
        |> List.map (fun tool ->
            let parameters =
                tool.Parameters
                |> List.map (fun p -> if p.required then $"{p.name}*" else p.name)
                |> String.concat ", "

            $"{tool.PluginName}.{tool.Name}({parameters}): {tool.Description}")
        |> String.concat "\n"

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

    let hasPlannerCandidateTools (catalog: QaToolCatalog) =
        catalog.tools |> List.exists (isBuiltInContextTool >> not)

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

        let systemPrompt =
            options.prompts.answerSystem
            |> Option.orElse options.plugInProfile.answerSystemInstruction
            |> Option.defaultValue DefaultPlugInPrompts.answerSystem

        let userPrompt =
            options.prompts.answerUserTemplate
            |> Option.defaultValue DefaultPlugInPrompts.answerUserTemplate
            |> renderTemplate
                [ "question", snapshot.text
                  "typedMemory", typedMemory
                  "toolObservations", toolObservations
                  "sourceInventory", inventory
                  "sourceContext", sourceContext ]

        { instructions = systemPrompt
          userPrompt = userPrompt }

    let createSnapshot (request: QaTurnRequest) =
        { turnId = request.turnId
          itemId = request.turnId
          revision = 1
          text = Text.normalizeWhitespace request.question
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow }

    let deterministicPlan (catalog: QaToolCatalog) question =
        [ if
              containsAny
                  [ "what time"
                    "current time"
                    "time is it"
                    "today"
                    "current date"
                    "what date" ]
                  question
          then
              match catalog |> findTool "FsVoiceTools" "current_time" with
              | Some tool ->
                  yield
                      { tool = tool
                        query = question
                        maxResults = 1
                        arguments = makeArgs [ "query", question ] }
              | None -> ()

          if
              containsAny
                  [ "what documents"
                    "which documents"
                    "selected documents"
                    "sources"
                    "inventory"
                    "files" ]
                  question
          then
              match catalog |> findTool "FsVoiceTools" "source_inventory" with
              | Some tool ->
                  yield
                      { tool = tool
                        query = question
                        maxResults = options.memoryCandidateChunks
                        arguments = makeArgs [] }
              | None -> ()

          if
              containsAny
                  [ "last result"
                    "previous result"
                    "earlier result"
                    "last tool"
                    "previous tool"
                    "tool result"
                    "what did the tool"
                    "what did you find"
                    "found earlier"
                    "that lookup"
                    "previous lookup"
                    "earlier lookup"
                    "looked up"
                    "searched earlier"
                    "blackboard" ]
                  question
          then
              match catalog |> findTool "FsVoiceTools" "blackboard_search" with
              | Some tool ->
                  yield
                      { tool = tool
                        query = question
                        maxResults = options.memoryCandidateChunks
                        arguments = makeArgs [ "query", question ] }
              | None -> ()

          if
              containsAny
                  [ "remember"
                    "memory"
                    "preference"
                    "decided"
                    "decision"
                    "earlier"
                    "previously" ]
                  question
          then
              match catalog |> findTool "FsVoiceTools" "durable_memory_search" with
              | Some tool ->
                  yield
                      { tool = tool
                        query = question
                        maxResults = options.memoryCandidateChunks
                        arguments = makeArgs [ "query", question; "max_results", string options.memoryCandidateChunks ] }
              | None -> () ]

    let jsonOptions =
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        options.NumberHandling <- JsonNumberHandling.AllowReadingFromString
        options.Converters.Add(JsonFSharpConverter())
        options

    let tryDeserializeToolPlan (text: string) =
        try
            JsonSerializer.Deserialize<ToolPlanDto>(text, jsonOptions) |> Some
        with _ ->
            None

    let toolPlanPrompt question (catalog: QaToolCatalog) =
        let systemPrompt =
            options.prompts.toolPlannerSystem
            |> Option.defaultValue DefaultPlugInPrompts.toolPlannerSystem

        let userPrompt =
            options.prompts.toolPlannerUserTemplate
            |> Option.defaultValue DefaultPlugInPrompts.toolPlannerUserTemplate
            |> renderTemplate [ "toolInventory", renderToolInventory catalog; "question", question ]

        [ ChatMessage(ChatRole.System, systemPrompt)
          ChatMessage(ChatRole.User, userPrompt) ]

    let parseToolPlan (catalog: QaToolCatalog) (text: string) : PlannedToolCall list =
        try
            let first = text.IndexOf('{')
            let last = text.LastIndexOf('}')

            let json =
                if first >= 0 && last >= first then
                    text.Substring(first, last - first + 1)
                else
                    text

            match tryDeserializeToolPlan json |> Option.bind _.calls with
            | Some calls ->
                calls
                |> Seq.choose (fun item ->
                    let plugin = item.plugin
                    let toolName = item.tool |> Option.orElse item.``function``

                    match plugin, toolName with
                    | Some plugin, Some toolName ->
                        match
                            catalog.tools
                            |> List.tryFind (fun t -> t.PluginName = plugin && t.Name = toolName)
                        with
                        | None -> None
                        | Some tool ->
                            let query = item.query |> Option.defaultValue ""
                            let maxResults = item.max_results |> Option.defaultValue 6 |> clamp 30
                            let args = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

                            for KeyValue(name, value) in item.arguments |> Option.defaultValue Map.empty do
                                args[name] <- value

                            if not (args.ContainsKey "query") && not (String.IsNullOrWhiteSpace query) then
                                args["query"] <- query

                            if not (args.ContainsKey "question") && tool.Name = "selected_source_search" then
                                args["question"] <- query

                            if not (args.ContainsKey "max_results") then
                                args["max_results"] <- string maxResults

                            Some(
                                { tool = tool
                                  query = query
                                  maxResults = maxResults
                                  arguments = args :> IReadOnlyDictionary<string, string> }
                                : PlannedToolCall
                            )
                    | _ -> None)
                |> Seq.truncate 4
                |> Seq.toList
            | None -> []
        with _ ->
            []

    let runToolPlanner (catalog: QaToolCatalog) question cancellationToken : Async<PlannedToolCall list> =
        async {
            match options.enableToolPlanner, hasPlannerCandidateTools catalog, options.clients.toolPlanner with
            | false, _, _
            | _, false, _
            | _, _, None -> return []
            | true, true, Some client ->
                try
                    let opts = ChatOptions()
                    opts.MaxOutputTokens <- Nullable(roleMaxTokens Planner 500)

                    let! response =
                        client.GetResponseAsync(toolPlanPrompt question catalog, opts, cancellationToken)
                        |> Async.AwaitTask

                    return parseToolPlan catalog response.Text
                with ex ->
                    report $"Tool planning failed: {ex.Message}"
                    return []
        }

    let invokeTool (call: PlannedToolCall) cancellationToken =
        task {
            let! result = call.tool.InvokeAsync(call.arguments, cancellationToken)

            return
                { pluginName = call.tool.PluginName
                  toolName = call.tool.Name
                  query = call.query
                  content = result.content
                  createdAt = DateTimeOffset.UtcNow }
        }

    let invokeTools (calls: PlannedToolCall list) cancellationToken =
        async {
            let deduped =
                calls
                |> List.distinctBy (fun call -> call.tool.PluginName, call.tool.Name, call.query)
                |> List.truncate 6

            let! observations =
                deduped
                |> List.map (fun call ->
                    async {
                        try
                            let! observation = invokeTool call cancellationToken |> Async.AwaitTask
                            return Some observation
                        with ex ->
                            report $"Tool {call.tool.PluginName}.{call.tool.Name} failed: {ex.Message}"
                            return None
                    })
                |> Async.Parallel

            return observations |> Array.choose id |> Array.toList
        }

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

                    let options =
                        { BlackboardSearchOptions.defaults with
                            maxResults = 8
                            includeKinds = [ ToolObservation; MemoryEvidence; SourceEvidence; Conflict; FinalAnswer ] }

                    let lexicalHits = Blackboard.search options query blackboard

                    let! semanticHits =
                        match currentMemoryEncoder () with
                        | Some encoder when not (String.IsNullOrWhiteSpace query) ->
                            BlackboardSemantic.search encoder options query blackboard
                            |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
                        | _ -> Task.FromResult []

                    return
                        lexicalHits @ semanticHits
                        |> Blackboard.mergeHits options.maxResults
                        |> Blackboard.renderHits
                } }

    let mutable catalog =
        QaToolLoader.loadWithProviders host options.toolProviderDirectory options.toolProviders

    let responseToolCatalog () =
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
            |> List.sortBy (fun tool -> tool.PluginName, tool.Name)
            |> List.fold folder (Set.empty, Map.empty, [])

        { tools = List.rev tools
          byName = byName }

    let mutable answerConnection: FsResponses.ResponseWebSocket option = None

    let mutable answerConversation =
        { previousResponseId = None
          items = [] }

    do
        for log in memoryService.StartupLogs @ catalog.logs do
            report log

    let answerConnectionIsOpen (connection: FsResponses.ResponseWebSocket) =
        connection.socket.State = Net.WebSockets.WebSocketState.Open

    let liveAnswerConnection config cancellationToken =
        task {
            match answerConnection with
            | Some connection when answerConnectionIsOpen connection -> return connection
            | Some connection ->
                FsResponses.ResponsesWebSocket.dispose connection
                let! connection = FsResponses.ResponsesWebSocket.connect config cancellationToken
                answerConnection <- Some connection
                return connection
            | None ->
                let! connection = FsResponses.ResponsesWebSocket.connect config cancellationToken
                answerConnection <- Some connection
                return connection
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

        blackboard <-
            blackboard
            |> Blackboard.add (BlackboardRecords.toolObservation turnId observation)

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

    let ensureAnswerBootstrap answerConfig prompt responseTools cancellationToken =
        async {
            match answerConversation.previousResponseId with
            | Some previousResponseId -> return Some previousResponseId
            | None ->
                let request = responseWarmupRequest answerConfig prompt responseTools.tools
                let! events = runResponsesWarmupRequest request cancellationToken |> Async.AwaitTask

                match responseIdFromEvents events with
                | Some responseId ->
                    answerConversation <-
                        { answerConversation with
                            previousResponseId = Some responseId }

                    return Some responseId
                | None ->
                    report
                        $"Answer Responses WebSocket warmup did not return a response id; sending stable instructions and tools with the next turn. {responsesDiagnostics events}."

                    return None
        }

    let updateAnswerConversation userItem answer events =
        responseIdFromEvents events
        |> Option.iter (fun responseId ->
            answerConversation <-
                { previousResponseId = Some responseId
                  items = answerConversation.items @ [ userItem; answerAssistantItem answer ] })

    let responsesInputItems userItem replayHistory =
        if replayHistory then
            answerConversation.items @ [ userItem ]
        else
            [ userItem ]

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
                    let responseTools = responseToolCatalog ()
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
                            $"Answer Responses WebSocket previous_response_id was not found; retrying from local append-only history: previousResponseId={previousResponseId.Value}; historyItems={answerConversation.items.Length}; {responsesDiagnostics events}."

                        answerConversation <-
                            { answerConversation with
                                previousResponseId = None }

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

    let applyWriteback (snapshot: TranscriptSnapshot) (answer: string) =
        if
            options.autoWriteback
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

            catalog <- QaToolLoader.loadWithProviders host options.toolProviderDirectory options.toolProviders

            answerConversation <-
                { previousResponseId = None
                  items = [] }

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
                                    enableToolPlanner = options.enableToolPlanner
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

            blackboard <- blackboard |> Blackboard.add (BlackboardRecords.transcript snapshot)

            request.realtimeJudgement
            |> Option.iter (fun judgement ->
                blackboard <-
                    blackboard
                    |> Blackboard.add (BlackboardRecords.realtimeJudgement snapshot.turnId judgement))

            let decision =
                memoryService.CreateSupervisorDecision(snapshot, request.realtimeJudgement)

            blackboard <-
                blackboard
                |> Blackboard.add (BlackboardRecords.recallDecision snapshot.turnId decision)

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

            let plannerSw = Stopwatch.StartNew()

            let! plannedTools, llmTools =
                task {
                    match options.answerTransport with
                    | Some _ -> return [], []
                    | None ->
                        let plannedTools = deterministicPlan catalog snapshot.text
                        let! llmTools = runToolPlanner catalog snapshot.text cancellationToken |> Async.StartAsTask
                        return plannedTools, llmTools
                }

            plannerSw.Stop()

            let allPlannedTools = plannedTools @ llmTools

            blackboard <-
                blackboard
                |> Blackboard.addMany (
                    allPlannedTools
                    |> List.map (plannedToolSummary >> BlackboardRecords.plannedTool snapshot.turnId)
                )

            let toolSw = Stopwatch.StartNew()

            let! observations =
                match options.answerTransport with
                | Some _ -> Task.FromResult []
                | None -> invokeTools allPlannedTools cancellationToken |> Async.StartAsTask

            toolSw.Stop()

            let! memoryHits, memoryElapsedMs = memoryTask
            let! chunks, sourceRetrievalElapsedMs = sourceTask

            blackboard <-
                blackboard
                |> Blackboard.addMany (memoryHits |> List.map (BlackboardRecords.memoryEvidence snapshot.turnId))
                |> Blackboard.addMany (chunks |> List.map (BlackboardRecords.sourceEvidence snapshot.turnId))
                |> Blackboard.addMany (observations |> List.map (BlackboardRecords.toolObservation snapshot.turnId))

            let answerSw = Stopwatch.StartNew()

            let! answerResult =
                answerWithModel snapshot decision memoryHits chunks observations cancellationToken
                |> Async.StartAsTask

            answerSw.Stop()

            let answer = answerResult.answer
            let allObservations = observations @ answerResult.observations

            let writebackSw = Stopwatch.StartNew()
            applyWriteback snapshot answer
            writebackSw.Stop()
            totalSw.Stop()

            if options.logTimings then
                report
                    $"QA timing: total={totalSw.Elapsed.TotalMilliseconds:F0}ms; source={sourceRetrievalElapsedMs:F0}ms; memory={memoryElapsedMs:F0}ms; planner={plannerSw.Elapsed.TotalMilliseconds:F0}ms; tools={toolSw.Elapsed.TotalMilliseconds:F0}ms; answer={answerSw.Elapsed.TotalMilliseconds:F0}ms; writeback={writebackSw.Elapsed.TotalMilliseconds:F0}ms; toolObservations={allObservations.Length}."

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

            blackboard <-
                blackboard
                |> Blackboard.add (BlackboardRecords.answerCandidate snapshot.turnId answer)
                |> Blackboard.add (BlackboardRecords.finalAnswer qaAnswer)

            return qaAnswer
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            for provider in contextProviders do
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult()

            for client in
                [ options.clients.queryExpansion
                  options.clients.toolPlanner
                  options.clients.answerGenerator ]
                |> List.choose id do
                client.Dispose()

            answerConnection |> Option.iter FsResponses.ResponsesWebSocket.dispose

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
