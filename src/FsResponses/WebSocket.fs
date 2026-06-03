namespace FsResponses

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type WebSocketCreateRequest =
    { ``type``: string
      model: string
      input: IOitem list
      instructions: string option
      max_output_tokens: int option
      metadata: Map<string, string> option
      parallel_tool_calls: bool option
      previous_response_id: string option
      prompt_cache_key: string option
      prompt_cache_retention: string option
      reasoning: Reasoning option
      service_tier: string option
      store: bool option
      temperature: float32 option
      text: TextOutput option
      tool_choice: ToolChoice option
      tools: Tool list option
      top_p: float32 option
      truncation: string option
      user: string option
      generate: bool option }

    static member Default =
        { ``type`` = "response.create"
          model = Models.gpt_5
          input = []
          instructions = None
          max_output_tokens = None
          metadata = None
          parallel_tool_calls = None
          previous_response_id = None
          prompt_cache_key = None
          prompt_cache_retention = None
          reasoning = None
          service_tier = None
          store = Some false
          temperature = None
          text = None
          tool_choice = Some ToolChoice.Auto
          tools = None
          top_p = None
          truncation = Some Truncation.auto
          user = None
          generate = None }

    static member OfRequest(req: Request) =
        { WebSocketCreateRequest.Default with
            model = req.model
            input = req.input
            instructions = req.instructions
            max_output_tokens = req.max_output_tokens
            metadata = req.metadata
            parallel_tool_calls = Some req.parallel_tool_calls
            previous_response_id = req.previous_response_id
            reasoning = req.reasoning
            service_tier = Some req.service_tier
            store = Some req.store
            temperature = Some req.temperature
            text = req.text
            tool_choice = Some req.tool_choice
            tools = Some req.tools
            top_p = Some req.top_p
            truncation = req.truncation
            user = req.user }

module WebSocketCreateRequest =
    let ofText model text =
        { WebSocketCreateRequest.Default with
            model = model
            input = [ IOitem.Message(Message.OfText text) ] }

    let warmup model instructions tools =
        { WebSocketCreateRequest.Default with
            model = model
            instructions = instructions
            tools = tools
            generate = Some false }

    let continueWith previousResponseId input (request: WebSocketCreateRequest) =
        { request with
            previous_response_id = Some previousResponseId
            input = input }

type ResponsesClientEvent = ResponseCreate of WebSocketCreateRequest

module ResponsesClientEvent =
    let create request = ResponseCreate request

    let ofText model text =
        WebSocketCreateRequest.ofText model text |> ResponseCreate

    let serialize event =
        match event with
        | ResponseCreate request -> JsonSerializer.Serialize(request, Api.serOpts)

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseLifecycleEvent =
    { event_id: string option
      sequence_number: int option
      response: Response }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseOutputItemEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      output_index: int
      item: IOitem }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseContentPartEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string option
      output_index: int
      content_index: int
      part: Content }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseOutputTextDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string option
      output_index: int
      content_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseOutputTextDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string option
      output_index: int
      content_index: int
      text: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseFunctionCallArgumentsDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string option
      output_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseFunctionCallArgumentsDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string option
      output_index: int
      arguments: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseSequenceEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseAudioDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseAudioTranscriptDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseIndexedItemEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseRefusalDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      content_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseRefusalDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      content_index: int
      refusal: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseReasoningTextDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      content_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseReasoningTextDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      content_index: int
      text: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseReasoningSummaryPart = { ``type``: string; text: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseReasoningSummaryPartEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      summary_index: int
      part: ResponseReasoningSummaryPart }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseReasoningSummaryTextDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      summary_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseReasoningSummaryTextDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      summary_index: int
      text: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseOutputTextAnnotationAddedEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      content_index: int
      annotation_index: int
      annotation: JsonElement }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseMcpCallArgumentsDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseMcpCallArgumentsDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      arguments: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseCodeInterpreterCallCodeDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseCodeInterpreterCallCodeDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      code: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseImageGenerationCallPartialImageEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      partial_image_index: int
      partial_image_b64: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseCustomToolCallInputDeltaEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      delta: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseCustomToolCallInputDoneEvent =
    { event_id: string option
      sequence_number: int option
      response_id: string option
      item_id: string
      output_index: int
      input: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ResponseErrorEvent =
    { event_id: string option
      sequence_number: int option
      status: int option
      response_id: string option
      error: ResponseError }

type UnknownResponseStreamEvent = { eventType: string; raw: JsonElement }

type ResponseStreamEvent =
    | ResponseCreated of ResponseLifecycleEvent
    | ResponseQueued of ResponseLifecycleEvent
    | ResponseInProgress of ResponseLifecycleEvent
    | ResponseCompleted of ResponseLifecycleEvent
    | ResponseFailed of ResponseLifecycleEvent
    | ResponseIncomplete of ResponseLifecycleEvent
    | OutputItemAdded of ResponseOutputItemEvent
    | OutputItemDone of ResponseOutputItemEvent
    | ContentPartAdded of ResponseContentPartEvent
    | ContentPartDone of ResponseContentPartEvent
    | OutputTextDelta of ResponseOutputTextDeltaEvent
    | OutputTextDone of ResponseOutputTextDoneEvent
    | OutputTextAnnotationAdded of ResponseOutputTextAnnotationAddedEvent
    | RefusalDelta of ResponseRefusalDeltaEvent
    | RefusalDone of ResponseRefusalDoneEvent
    | FunctionCallArgumentsDelta of ResponseFunctionCallArgumentsDeltaEvent
    | FunctionCallArgumentsDone of ResponseFunctionCallArgumentsDoneEvent
    | FileSearchCallInProgress of ResponseIndexedItemEvent
    | FileSearchCallSearching of ResponseIndexedItemEvent
    | FileSearchCallCompleted of ResponseIndexedItemEvent
    | WebSearchCallInProgress of ResponseIndexedItemEvent
    | WebSearchCallSearching of ResponseIndexedItemEvent
    | WebSearchCallCompleted of ResponseIndexedItemEvent
    | CodeInterpreterCallInProgress of ResponseIndexedItemEvent
    | CodeInterpreterCallInterpreting of ResponseIndexedItemEvent
    | CodeInterpreterCallCompleted of ResponseIndexedItemEvent
    | CodeInterpreterCallCodeDelta of ResponseCodeInterpreterCallCodeDeltaEvent
    | CodeInterpreterCallCodeDone of ResponseCodeInterpreterCallCodeDoneEvent
    | ImageGenerationCallInProgress of ResponseIndexedItemEvent
    | ImageGenerationCallGenerating of ResponseIndexedItemEvent
    | ImageGenerationCallCompleted of ResponseIndexedItemEvent
    | ImageGenerationCallPartialImage of ResponseImageGenerationCallPartialImageEvent
    | McpCallArgumentsDelta of ResponseMcpCallArgumentsDeltaEvent
    | McpCallArgumentsDone of ResponseMcpCallArgumentsDoneEvent
    | McpCallInProgress of ResponseIndexedItemEvent
    | McpCallCompleted of ResponseIndexedItemEvent
    | McpCallFailed of ResponseIndexedItemEvent
    | McpListToolsInProgress of ResponseIndexedItemEvent
    | McpListToolsCompleted of ResponseIndexedItemEvent
    | McpListToolsFailed of ResponseIndexedItemEvent
    | AudioDelta of ResponseAudioDeltaEvent
    | AudioDone of ResponseSequenceEvent
    | AudioTranscriptDelta of ResponseAudioTranscriptDeltaEvent
    | AudioTranscriptDone of ResponseSequenceEvent
    | ReasoningTextDelta of ResponseReasoningTextDeltaEvent
    | ReasoningTextDone of ResponseReasoningTextDoneEvent
    | ReasoningSummaryPartAdded of ResponseReasoningSummaryPartEvent
    | ReasoningSummaryPartDone of ResponseReasoningSummaryPartEvent
    | ReasoningSummaryTextDelta of ResponseReasoningSummaryTextDeltaEvent
    | ReasoningSummaryTextDone of ResponseReasoningSummaryTextDoneEvent
    | CustomToolCallInputDelta of ResponseCustomToolCallInputDeltaEvent
    | CustomToolCallInputDone of ResponseCustomToolCallInputDoneEvent
    | Error of ResponseErrorEvent
    | Unknown of UnknownResponseStreamEvent

type ResponsesServerEvent = ResponseStreamEvent

module ResponseStreamEvent =
    let private tryGetProperty (name: string) (element: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            Some property
        else
            None

    let private tryStringProperty name element =
        element
        |> tryGetProperty name
        |> Option.bind (fun property ->
            if property.ValueKind = JsonValueKind.String then
                property.GetString() |> Option.ofObj
            else
                None)

    let private tryIntProperty name element =
        element
        |> tryGetProperty name
        |> Option.bind (fun property ->
            if property.ValueKind = JsonValueKind.Number then
                match property.TryGetInt32() with
                | true, value -> Some value
                | _ -> None
            else
                None)

    let private eventType (element: JsonElement) =
        element
        |> tryGetProperty "type"
        |> Option.bind (fun property ->
            if property.ValueKind = JsonValueKind.String then
                property.GetString() |> Option.ofObj
            else
                None)
        |> Option.defaultValue "unknown"

    let private unknown (root: JsonElement) =
        Unknown
            { eventType = eventType root
              raw = root.Clone() }

    let private deserializeKnown<'T> (construct: 'T -> ResponseStreamEvent) (root: JsonElement) =
        try
            root.GetRawText()
            |> fun text -> JsonSerializer.Deserialize<'T>(text, Api.serOpts)
            |> construct
        with _ ->
            unknown root

    let private deserializeError (root: JsonElement) =
        try
            root.GetRawText()
            |> fun text -> JsonSerializer.Deserialize<ResponseErrorEvent>(text, Api.serOpts)
            |> Error
        with _ ->
            match tryGetProperty "error" root with
            | Some error when error.ValueKind = JsonValueKind.Object ->
                let errorType = tryStringProperty "type" error

                let message =
                    tryStringProperty "message" error |> Option.defaultValue (error.GetRawText())

                let code =
                    tryStringProperty "code" error
                    |> Option.orElse errorType
                    |> Option.defaultValue "error"

                Error
                    { event_id = tryStringProperty "event_id" root
                      sequence_number = tryIntProperty "sequence_number" root
                      status = tryIntProperty "status" root
                      response_id = tryStringProperty "response_id" root
                      error =
                        { code = code
                          message = message
                          ``type`` = errorType
                          param = tryStringProperty "param" error } }
            | _ -> unknown root

    let fromJsonElement (root: JsonElement) =
        match eventType root with
        | "response.created" -> deserializeKnown<ResponseLifecycleEvent> ResponseCreated root
        | "response.queued" -> deserializeKnown<ResponseLifecycleEvent> ResponseQueued root
        | "response.in_progress" -> deserializeKnown<ResponseLifecycleEvent> ResponseInProgress root
        | "response.completed" -> deserializeKnown<ResponseLifecycleEvent> ResponseCompleted root
        | "response.failed" -> deserializeKnown<ResponseLifecycleEvent> ResponseFailed root
        | "response.incomplete" -> deserializeKnown<ResponseLifecycleEvent> ResponseIncomplete root
        | "response.output_item.added" -> deserializeKnown<ResponseOutputItemEvent> OutputItemAdded root
        | "response.output_item.done" -> deserializeKnown<ResponseOutputItemEvent> OutputItemDone root
        | "response.content_part.added" -> deserializeKnown<ResponseContentPartEvent> ContentPartAdded root
        | "response.content_part.done" -> deserializeKnown<ResponseContentPartEvent> ContentPartDone root
        | "response.output_text.delta" -> deserializeKnown<ResponseOutputTextDeltaEvent> OutputTextDelta root
        | "response.output_text.done" -> deserializeKnown<ResponseOutputTextDoneEvent> OutputTextDone root
        | "response.output_text.annotation.added" ->
            deserializeKnown<ResponseOutputTextAnnotationAddedEvent> OutputTextAnnotationAdded root
        | "response.refusal.delta" -> deserializeKnown<ResponseRefusalDeltaEvent> RefusalDelta root
        | "response.refusal.done" -> deserializeKnown<ResponseRefusalDoneEvent> RefusalDone root
        | "response.function_call_arguments.delta" ->
            deserializeKnown<ResponseFunctionCallArgumentsDeltaEvent> FunctionCallArgumentsDelta root
        | "response.function_call_arguments.done" ->
            deserializeKnown<ResponseFunctionCallArgumentsDoneEvent> FunctionCallArgumentsDone root
        | "response.file_search_call.in_progress" ->
            deserializeKnown<ResponseIndexedItemEvent> FileSearchCallInProgress root
        | "response.file_search_call.searching" ->
            deserializeKnown<ResponseIndexedItemEvent> FileSearchCallSearching root
        | "response.file_search_call.completed" ->
            deserializeKnown<ResponseIndexedItemEvent> FileSearchCallCompleted root
        | "response.web_search_call.in_progress" ->
            deserializeKnown<ResponseIndexedItemEvent> WebSearchCallInProgress root
        | "response.web_search_call.searching" -> deserializeKnown<ResponseIndexedItemEvent> WebSearchCallSearching root
        | "response.web_search_call.completed" -> deserializeKnown<ResponseIndexedItemEvent> WebSearchCallCompleted root
        | "response.code_interpreter_call.in_progress" ->
            deserializeKnown<ResponseIndexedItemEvent> CodeInterpreterCallInProgress root
        | "response.code_interpreter_call.interpreting" ->
            deserializeKnown<ResponseIndexedItemEvent> CodeInterpreterCallInterpreting root
        | "response.code_interpreter_call.completed" ->
            deserializeKnown<ResponseIndexedItemEvent> CodeInterpreterCallCompleted root
        | "response.code_interpreter_call_code.delta" ->
            deserializeKnown<ResponseCodeInterpreterCallCodeDeltaEvent> CodeInterpreterCallCodeDelta root
        | "response.code_interpreter_call_code.done" ->
            deserializeKnown<ResponseCodeInterpreterCallCodeDoneEvent> CodeInterpreterCallCodeDone root
        | "response.image_generation_call.in_progress" ->
            deserializeKnown<ResponseIndexedItemEvent> ImageGenerationCallInProgress root
        | "response.image_generation_call.generating" ->
            deserializeKnown<ResponseIndexedItemEvent> ImageGenerationCallGenerating root
        | "response.image_generation_call.completed" ->
            deserializeKnown<ResponseIndexedItemEvent> ImageGenerationCallCompleted root
        | "response.image_generation_call.partial_image" ->
            deserializeKnown<ResponseImageGenerationCallPartialImageEvent> ImageGenerationCallPartialImage root
        | "response.mcp_call_arguments.delta" ->
            deserializeKnown<ResponseMcpCallArgumentsDeltaEvent> McpCallArgumentsDelta root
        | "response.mcp_call_arguments.done" ->
            deserializeKnown<ResponseMcpCallArgumentsDoneEvent> McpCallArgumentsDone root
        | "response.mcp_call.in_progress" -> deserializeKnown<ResponseIndexedItemEvent> McpCallInProgress root
        | "response.mcp_call.completed" -> deserializeKnown<ResponseIndexedItemEvent> McpCallCompleted root
        | "response.mcp_call.failed" -> deserializeKnown<ResponseIndexedItemEvent> McpCallFailed root
        | "response.mcp_list_tools.in_progress" ->
            deserializeKnown<ResponseIndexedItemEvent> McpListToolsInProgress root
        | "response.mcp_list_tools.completed" -> deserializeKnown<ResponseIndexedItemEvent> McpListToolsCompleted root
        | "response.mcp_list_tools.failed" -> deserializeKnown<ResponseIndexedItemEvent> McpListToolsFailed root
        | "response.audio.delta" -> deserializeKnown<ResponseAudioDeltaEvent> AudioDelta root
        | "response.audio.done" -> deserializeKnown<ResponseSequenceEvent> AudioDone root
        | "response.audio.transcript.delta" ->
            deserializeKnown<ResponseAudioTranscriptDeltaEvent> AudioTranscriptDelta root
        | "response.audio.transcript.done" -> deserializeKnown<ResponseSequenceEvent> AudioTranscriptDone root
        | "response.reasoning_text.delta" -> deserializeKnown<ResponseReasoningTextDeltaEvent> ReasoningTextDelta root
        | "response.reasoning_text.done" -> deserializeKnown<ResponseReasoningTextDoneEvent> ReasoningTextDone root
        | "response.reasoning_summary_part.added" ->
            deserializeKnown<ResponseReasoningSummaryPartEvent> ReasoningSummaryPartAdded root
        | "response.reasoning_summary_part.done" ->
            deserializeKnown<ResponseReasoningSummaryPartEvent> ReasoningSummaryPartDone root
        | "response.reasoning_summary_text.delta" ->
            deserializeKnown<ResponseReasoningSummaryTextDeltaEvent> ReasoningSummaryTextDelta root
        | "response.reasoning_summary_text.done" ->
            deserializeKnown<ResponseReasoningSummaryTextDoneEvent> ReasoningSummaryTextDone root
        | "response.custom_tool_call_input.delta" ->
            deserializeKnown<ResponseCustomToolCallInputDeltaEvent> CustomToolCallInputDelta root
        | "response.custom_tool_call_input.done" ->
            deserializeKnown<ResponseCustomToolCallInputDoneEvent> CustomToolCallInputDone root
        | "error" -> deserializeError root
        | _ -> unknown root

    let deserialize (text: string) =
        use document = JsonDocument.Parse(text)
        document.RootElement.Clone() |> fromJsonElement

    let tryDeserialize (text: string) : Result<ResponseStreamEvent, exn> =
        try
            deserialize text |> Ok
        with ex ->
            Result.Error ex

    let isTerminal event =
        match event with
        | ResponseCompleted _
        | ResponseFailed _
        | ResponseIncomplete _
        | Error _ -> true
        | _ -> false

    let typeName event =
        match event with
        | ResponseCreated _ -> "response.created"
        | ResponseQueued _ -> "response.queued"
        | ResponseInProgress _ -> "response.in_progress"
        | ResponseCompleted _ -> "response.completed"
        | ResponseFailed _ -> "response.failed"
        | ResponseIncomplete _ -> "response.incomplete"
        | OutputItemAdded _ -> "response.output_item.added"
        | OutputItemDone _ -> "response.output_item.done"
        | ContentPartAdded _ -> "response.content_part.added"
        | ContentPartDone _ -> "response.content_part.done"
        | OutputTextDelta _ -> "response.output_text.delta"
        | OutputTextDone _ -> "response.output_text.done"
        | OutputTextAnnotationAdded _ -> "response.output_text.annotation.added"
        | RefusalDelta _ -> "response.refusal.delta"
        | RefusalDone _ -> "response.refusal.done"
        | FunctionCallArgumentsDelta _ -> "response.function_call_arguments.delta"
        | FunctionCallArgumentsDone _ -> "response.function_call_arguments.done"
        | FileSearchCallInProgress _ -> "response.file_search_call.in_progress"
        | FileSearchCallSearching _ -> "response.file_search_call.searching"
        | FileSearchCallCompleted _ -> "response.file_search_call.completed"
        | WebSearchCallInProgress _ -> "response.web_search_call.in_progress"
        | WebSearchCallSearching _ -> "response.web_search_call.searching"
        | WebSearchCallCompleted _ -> "response.web_search_call.completed"
        | CodeInterpreterCallInProgress _ -> "response.code_interpreter_call.in_progress"
        | CodeInterpreterCallInterpreting _ -> "response.code_interpreter_call.interpreting"
        | CodeInterpreterCallCompleted _ -> "response.code_interpreter_call.completed"
        | CodeInterpreterCallCodeDelta _ -> "response.code_interpreter_call_code.delta"
        | CodeInterpreterCallCodeDone _ -> "response.code_interpreter_call_code.done"
        | ImageGenerationCallInProgress _ -> "response.image_generation_call.in_progress"
        | ImageGenerationCallGenerating _ -> "response.image_generation_call.generating"
        | ImageGenerationCallCompleted _ -> "response.image_generation_call.completed"
        | ImageGenerationCallPartialImage _ -> "response.image_generation_call.partial_image"
        | McpCallArgumentsDelta _ -> "response.mcp_call_arguments.delta"
        | McpCallArgumentsDone _ -> "response.mcp_call_arguments.done"
        | McpCallInProgress _ -> "response.mcp_call.in_progress"
        | McpCallCompleted _ -> "response.mcp_call.completed"
        | McpCallFailed _ -> "response.mcp_call.failed"
        | McpListToolsInProgress _ -> "response.mcp_list_tools.in_progress"
        | McpListToolsCompleted _ -> "response.mcp_list_tools.completed"
        | McpListToolsFailed _ -> "response.mcp_list_tools.failed"
        | AudioDelta _ -> "response.audio.delta"
        | AudioDone _ -> "response.audio.done"
        | AudioTranscriptDelta _ -> "response.audio.transcript.delta"
        | AudioTranscriptDone _ -> "response.audio.transcript.done"
        | ReasoningTextDelta _ -> "response.reasoning_text.delta"
        | ReasoningTextDone _ -> "response.reasoning_text.done"
        | ReasoningSummaryPartAdded _ -> "response.reasoning_summary_part.added"
        | ReasoningSummaryPartDone _ -> "response.reasoning_summary_part.done"
        | ReasoningSummaryTextDelta _ -> "response.reasoning_summary_text.delta"
        | ReasoningSummaryTextDone _ -> "response.reasoning_summary_text.done"
        | CustomToolCallInputDelta _ -> "response.custom_tool_call_input.delta"
        | CustomToolCallInputDone _ -> "response.custom_tool_call_input.done"
        | Error _ -> "error"
        | Unknown event -> event.eventType

    let outputTextDelta event =
        match event with
        | OutputTextDelta delta -> Some delta.delta
        | _ -> None

    let outputTextDone event =
        match event with
        | OutputTextDone doneEvent -> Some doneEvent.text
        | ResponseCompleted lifecycle ->
            lifecycle.response.output
            |> List.choose (function
                | IOitem.Message message ->
                    message.content
                    |> List.choose (function
                        | Content.Output_text text -> Some text.text
                        | _ -> None)
                    |> String.concat ""
                    |> Some
                | _ -> None)
            |> String.concat ""
            |> function
                | "" -> None
                | text -> Some text
        | _ -> None

    let completedResponse event =
        match event with
        | ResponseCompleted lifecycle -> Some lifecycle.response
        | _ -> None

module ResponseStream =
    let outputText events =
        let deltas =
            events |> List.choose ResponseStreamEvent.outputTextDelta |> String.concat ""

        if String.IsNullOrEmpty deltas then
            events |> List.choose ResponseStreamEvent.outputTextDone |> String.concat ""
        else
            deltas

    let completedResponse events =
        events |> List.tryPick ResponseStreamEvent.completedResponse

type ResponseWebSocketConfig =
    { endpoint: Uri
      apiKey: string
      organization: string option
      project: string option
      receiveBufferSize: int }

module ResponseWebSocketConfig =
    [<Literal>]
    let ApiKeyEnvironmentVariable = "OPENAI_API_KEY"

    let defaultEndpoint = Uri "wss://api.openai.com/v1/responses"

    let create apiKey =
        { endpoint = defaultEndpoint
          apiKey = apiKey
          organization = None
          project = None
          receiveBufferSize = 64 * 1024 }

    let fromEnvironment () =
        let apiKey = Environment.GetEnvironmentVariable ApiKeyEnvironmentVariable

        if String.IsNullOrWhiteSpace apiKey then
            invalidOp $"Environment variable {ApiKeyEnvironmentVariable} is not set."

        create apiKey

type ResponseWebSocket =
    { socket: ClientWebSocket
      config: ResponseWebSocketConfig }

type WebSocketClosed =
    { status: WebSocketCloseStatus option
      description: string option }

type WebSocketRead =
    | TextMessage of string
    | Closed of WebSocketClosed

module ResponsesWebSocket =
    let private applyHeaders config (socket: ClientWebSocket) =
        socket.Options.SetRequestHeader("Authorization", $"Bearer {config.apiKey}")

        config.organization
        |> Option.iter (fun organization -> socket.Options.SetRequestHeader("OpenAI-Organization", organization))

        config.project
        |> Option.iter (fun project -> socket.Options.SetRequestHeader("OpenAI-Project", project))

    let connect config (cancellationToken: CancellationToken) =
        task {
            let socket = new ClientWebSocket()
            applyHeaders config socket
            do! socket.ConnectAsync(config.endpoint, cancellationToken)
            return { socket = socket; config = config }
        }

    let dispose connection = connection.socket.Dispose()

    let close connection (cancellationToken: CancellationToken) =
        task {
            if connection.socket.State = WebSocketState.Open then
                do!
                    connection.socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "closed by client",
                        cancellationToken
                    )
        }

    let serializeCreate request =
        request |> ResponsesClientEvent.create |> ResponsesClientEvent.serialize

    let serializeEvent event = ResponsesClientEvent.serialize event

    let sendEvent connection event (cancellationToken: CancellationToken) =
        task {
            let json = serializeEvent event
            let bytes = Encoding.UTF8.GetBytes json
            do! connection.socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken)
        }

    let sendCreate connection request (cancellationToken: CancellationToken) =
        task { do! sendEvent connection (ResponsesClientEvent.create request) cancellationToken }

    let readText connection (cancellationToken: CancellationToken) =
        task {
            let buffer = Array.zeroCreate<byte> connection.config.receiveBufferSize
            use stream = new MemoryStream()
            let mutable finished = false
            let mutable closed = None

            while not finished do
                let! result = connection.socket.ReceiveAsync(ArraySegment<byte> buffer, cancellationToken)

                if result.MessageType = WebSocketMessageType.Close then
                    closed <-
                        Some
                            { status = result.CloseStatus |> Option.ofNullable
                              description = result.CloseStatusDescription |> Option.ofObj }

                    finished <- true
                else
                    stream.Write(buffer, 0, result.Count)
                    finished <- result.EndOfMessage

            match closed with
            | Some close -> return Closed close
            | None -> return TextMessage(Encoding.UTF8.GetString(stream.ToArray()))
        }

    let readEvent connection cancellationToken =
        task {
            let! message = readText connection cancellationToken

            match message with
            | TextMessage text -> return ResponseStreamEvent.deserialize text |> Some
            | Closed _ -> return None
        }

    let readUntilTerminal connection cancellationToken =
        task {
            let events = ResizeArray<ResponseStreamEvent>()
            let mutable finished = false

            while not finished do
                let! event = readEvent connection cancellationToken

                match event with
                | Some event ->
                    events.Add event
                    finished <- ResponseStreamEvent.isTerminal event
                | None -> finished <- true

            return events |> Seq.toList
        }

    let createAndCollect connection request cancellationToken =
        task {
            do! sendCreate connection request cancellationToken
            return! readUntilTerminal connection cancellationToken
        }

    let createWithNewConnection config request cancellationToken =
        task {
            let! connection = connect config cancellationToken

            try
                return! createAndCollect connection request cancellationToken
            finally
                dispose connection
        }
