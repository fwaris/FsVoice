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
type ResponseErrorEvent =
    { event_id: string option
      sequence_number: int option
      status: int option
      response_id: string option
      error: ResponseError }

type UnknownResponseStreamEvent = { eventType: string; raw: JsonElement }

type ResponseStreamEvent =
    | ResponseCreated of ResponseLifecycleEvent
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
    | FunctionCallArgumentsDelta of ResponseFunctionCallArgumentsDeltaEvent
    | FunctionCallArgumentsDone of ResponseFunctionCallArgumentsDoneEvent
    | Error of ResponseErrorEvent
    | Unknown of UnknownResponseStreamEvent

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
                    tryStringProperty "message" error
                    |> Option.defaultValue (error.GetRawText())

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
        | "response.function_call_arguments.delta" ->
            deserializeKnown<ResponseFunctionCallArgumentsDeltaEvent> FunctionCallArgumentsDelta root
        | "response.function_call_arguments.done" ->
            deserializeKnown<ResponseFunctionCallArgumentsDoneEvent> FunctionCallArgumentsDone root
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
        JsonSerializer.Serialize(request, Api.serOpts)

    let sendCreate connection request (cancellationToken: CancellationToken) =
        task {
            let json = serializeCreate request
            let bytes = Encoding.UTF8.GetBytes json
            do! connection.socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken)
        }

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
