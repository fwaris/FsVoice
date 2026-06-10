module Tests

open FsResponses
open System
open System.Buffers
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit

let dmp (s: string) = System.Diagnostics.Debug.WriteLine s

let runT (t: Task<'t>) = t.Result

let image =
    lazy (JsonSerializer.Deserialize<string>(File.ReadAllText "TestImage.txt"))

let private hasProperty (name: string) (element: JsonElement) : bool =
    let mutable property = Unchecked.defaultof<JsonElement>
    element.TryGetProperty(name, &property)

let private firstArrayItem (element: JsonElement) : JsonElement = element.EnumerateArray() |> Seq.head

let private parseObject (json: string) : JsonElement =
    use document = JsonDocument.Parse json
    document.RootElement.Clone()

let private freeLoopbackPort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let endpoint = listener.LocalEndpoint :?> IPEndPoint
    endpoint.Port

let private websocketConfig endpoint =
    { ResponseWebSocketConfig.create "test-api-key" with
        endpoint = endpoint }

let private withWebSocketServer handler client =
    task {
        let port = freeLoopbackPort ()
        use listener = new HttpListener()
        listener.Prefixes.Add $"http://127.0.0.1:{port}/ws/"
        listener.Start()

        let server =
            task {
                let! context = listener.GetContextAsync()

                if not context.Request.IsWebSocketRequest then
                    context.Response.StatusCode <- 400
                    context.Response.Close()
                else
                    let! webSocketContext = context.AcceptWebSocketAsync null

                    try
                        do! handler webSocketContext.WebSocket
                    finally
                        webSocketContext.WebSocket.Dispose()
            }

        try
            let config = Uri $"ws://127.0.0.1:{port}/ws/" |> websocketConfig
            let! result = client config
            do! server.WaitAsync(TimeSpan.FromSeconds 5.0)
            return result
        finally
            listener.Stop()
    }

let private withWebSocketServerForConnections connectionCount handler client =
    task {
        let port = freeLoopbackPort ()
        use listener = new HttpListener()
        listener.Prefixes.Add $"http://127.0.0.1:{port}/ws/"
        listener.Start()

        let server =
            task {
                for connectionIndex in 1..connectionCount do
                    let! context = listener.GetContextAsync()

                    if not context.Request.IsWebSocketRequest then
                        context.Response.StatusCode <- 400
                        context.Response.Close()
                    else
                        let! webSocketContext = context.AcceptWebSocketAsync null

                        try
                            do! handler connectionIndex webSocketContext.WebSocket
                        finally
                            webSocketContext.WebSocket.Dispose()
            }

        try
            let config = Uri $"ws://127.0.0.1:{port}/ws/" |> websocketConfig
            let! result = client config
            do! server.WaitAsync(TimeSpan.FromSeconds 5.0)
            return result
        finally
            listener.Stop()
    }

let private receiveUtf8 (socket: WebSocket) cancellationToken =
    task {
        let buffer = ArrayPool<byte>.Shared.Rent 1024
        use stream = new MemoryStream()
        let mutable finished = false
        let mutable closed = false

        try
            while not finished do
                let! result = socket.ReceiveAsync(ArraySegment(buffer, 0, buffer.Length), cancellationToken)

                if result.MessageType = WebSocketMessageType.Close then
                    closed <- true
                    finished <- true
                else
                    stream.Write(buffer, 0, result.Count)
                    finished <- result.EndOfMessage

            if closed then
                return None
            else
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, int stream.Length) |> Some
        finally
            ArrayPool<byte>.Shared.Return buffer
    }

let private sendUtf8 (socket: WebSocket) (text: string) cancellationToken =
    task {
        let bytes = Encoding.UTF8.GetBytes text
        do! socket.SendAsync(ArraySegment(bytes, 0, bytes.Length), WebSocketMessageType.Text, true, cancellationToken)
    }

let private sendFragmentedText (socket: WebSocket) (text: string) cancellationToken =
    task {
        let byteCount = Encoding.UTF8.GetByteCount text
        let bytes = ArrayPool<byte>.Shared.Rent byteCount

        try
            let written = Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0)
            let splitAt = max 1 (written / 2)
            do! socket.SendAsync(ArraySegment(bytes, 0, splitAt), WebSocketMessageType.Text, false, cancellationToken)

            do!
                socket.SendAsync(
                    ArraySegment(bytes, splitAt, written - splitAt),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken
                )
        finally
            ArrayPool<byte>.Shared.Return bytes
    }

let private responseCompletedJson responseId text =
    let encodedText = JsonSerializer.Serialize text

    sprintf
        """{"type":"response.completed","sequence_number":1,"response":{"id":"%s","object":"response","created_at":1710000000,"status":"completed","error":null,"incomplete_details":null,"instructions":null,"max_output_tokens":null,"model":"gpt-5","metadata":null,"output":[{"type":"message","id":"msg_%s","status":"completed","role":"assistant","content":[{"type":"output_text","text":%s,"annotations":null}]}],"parallel_tool_calls":true,"previous_response_id":null,"reasoning":null,"store":false,"temperature":1,"text":null,"tool_choice":"auto","tools":[],"top_p":1,"truncation":"auto","usage":null,"user":null}}"""
        responseId
        responseId
        encodedText

let createRequest () =
    { Request.Default with
        input =
            [ IOitem.Message
                  { Message.Default with
                      content =
                          [ Content.Input_text {| text = "Describe the image" |}
                            Content.Input_image {| image_url = image.Value |} ] } ]
        store = false
        model = Models.gpt_41_nano
        metadata = [ "CORR_ID", "1" ] |> Map.ofList |> Some }

[<Fact>]
let ``response create websocket request omits http-only fields`` () =
    let req =
        WebSocketCreateRequest.ofText Models.gpt_5 "Say hello from WebSocket mode."

    let root: JsonElement = req |> ResponsesWebSocket.serializeCreate |> parseObject

    Assert.Equal("response.create", root.GetProperty("type").GetString())
    Assert.Equal(Models.gpt_5, root.GetProperty("model").GetString())
    Assert.False(hasProperty "stream" root)
    Assert.False(hasProperty "background" root)
    Assert.False(hasProperty "previous_response_id" root)
    Assert.False(hasProperty "instructions" root)
    Assert.True(root.GetProperty("store").GetBoolean())

    let firstInput = root.GetProperty("input") |> firstArrayItem
    Assert.Equal("message", firstInput.GetProperty("type").GetString())

    let firstContent = firstInput.GetProperty("content") |> firstArrayItem
    Assert.Equal("input_text", firstContent.GetProperty("type").GetString())
    Assert.Equal("Say hello from WebSocket mode.", firstContent.GetProperty("text").GetString())

[<Fact>]
let ``websocket continuation request includes previous response and function output`` () =
    let request =
        WebSocketCreateRequest.Default
        |> WebSocketCreateRequest.continueWith
            "resp_123"
            [ IOitem.Function_call_output
                  { call_id = "call_123"
                    output = """{"ok":true}""" } ]

    let root: JsonElement = request |> ResponsesWebSocket.serializeCreate |> parseObject
    let firstInput = root.GetProperty("input") |> firstArrayItem

    Assert.Equal("response.create", root.GetProperty("type").GetString())
    Assert.Equal("resp_123", root.GetProperty("previous_response_id").GetString())
    Assert.True(root.GetProperty("store").GetBoolean())
    Assert.Equal("function_call_output", firstInput.GetProperty("type").GetString())
    Assert.Equal("call_123", firstInput.GetProperty("call_id").GetString())

[<Fact>]
let ``websocket request includes prompt cache controls when configured`` () =
    let request =
        { WebSocketCreateRequest.ofText Models.gpt_5 "Say hello from WebSocket mode." with
            prompt_cache_key = Some "core611-oracle:test"
            prompt_cache_retention = Some "24h" }

    let root: JsonElement = request |> ResponsesWebSocket.serializeCreate |> parseObject

    Assert.Equal("core611-oracle:test", root.GetProperty("prompt_cache_key").GetString())
    Assert.Equal("24h", root.GetProperty("prompt_cache_retention").GetString())

[<Fact>]
let ``websocket request serializes context management compaction`` () =
    let request =
        { WebSocketCreateRequest.ofText Models.gpt_5 "Say hello from WebSocket mode." with
            context_management = Some [ ResponseContextManagement.Compaction {| compact_threshold = 200000 |} ] }

    let root: JsonElement = request |> ResponsesWebSocket.serializeCreate |> parseObject
    let firstContext = root.GetProperty("context_management") |> firstArrayItem

    Assert.Equal("compaction", firstContext.GetProperty("type").GetString())
    Assert.Equal(200000, firstContext.GetProperty("compact_threshold").GetInt32())

[<Fact>]
let ``warmup request sets generate false`` () =
    let request =
        WebSocketCreateRequest.warmup
            Models.gpt_5
            (Some "You are a careful assistant.")
            (Some
                [ Tool.Function
                      { Function.Default with
                          name = "lookup" } ])

    let root: JsonElement = request |> ResponsesWebSocket.serializeCreate |> parseObject

    Assert.False(root.GetProperty("generate").GetBoolean())
    Assert.True(root.GetProperty("store").GetBoolean())
    Assert.Equal("You are a careful assistant.", root.GetProperty("instructions").GetString())

    let firstTool = root.GetProperty("tools") |> firstArrayItem
    Assert.Equal("function", firstTool.GetProperty("type").GetString())
    Assert.Equal("lookup", firstTool.GetProperty("name").GetString())

[<Fact>]
let ``client event wrapper serializes response create`` () =
    let event =
        WebSocketCreateRequest.ofText Models.gpt_5 "Say hello from typed client event."
        |> ResponsesClientEvent.create

    let root: JsonElement = event |> ResponsesWebSocket.serializeEvent |> parseObject

    Assert.Equal("response.create", root.GetProperty("type").GetString())
    Assert.Equal(Models.gpt_5, root.GetProperty("model").GetString())

    let firstContent =
        root.GetProperty("input")
        |> firstArrayItem
        |> fun input -> input.GetProperty("content") |> firstArrayItem

    Assert.Equal("input_text", firstContent.GetProperty("type").GetString())
    Assert.Equal("Say hello from typed client event.", firstContent.GetProperty("text").GetString())

[<Fact>]
let ``websocket readText reads fragmented text as one message`` () =
    task {
        let message =
            """{"type":"response.output_text.delta","sequence_number":1,"response_id":"resp_123","item_id":"msg_123","output_index":0,"content_index":0,"delta":"hello"}"""

        let! read =
            withWebSocketServer
                (fun socket ->
                    task {
                        do! sendFragmentedText socket message CancellationToken.None
                        let! _ = receiveUtf8 socket CancellationToken.None

                        if socket.State = WebSocketState.CloseReceived then
                            do!
                                socket.CloseOutputAsync(
                                    WebSocketCloseStatus.NormalClosure,
                                    "done",
                                    CancellationToken.None
                                )
                    })
                (fun config ->
                    task {
                        let! connection = ResponsesWebSocket.connect config CancellationToken.None

                        try
                            let! read = ResponsesWebSocket.readText connection CancellationToken.None
                            do! ResponsesWebSocket.close connection CancellationToken.None
                            return read
                        finally
                            ResponsesWebSocket.dispose connection
                    })

        match read with
        | TextMessage text -> Assert.Equal(message, text)
        | Closed close -> failwith $"Expected text message, got close: {close}"
    }

[<Fact>]
let ``websocket readText returns close frame`` () =
    task {
        let! read =
            withWebSocketServer
                (fun socket ->
                    task {
                        do!
                            socket.CloseOutputAsync(
                                WebSocketCloseStatus.EndpointUnavailable,
                                "server closed",
                                CancellationToken.None
                            )

                        let! _ = receiveUtf8 socket CancellationToken.None
                        ()
                    })
                (fun config ->
                    task {
                        let! connection = ResponsesWebSocket.connect config CancellationToken.None

                        try
                            let! read = ResponsesWebSocket.readText connection CancellationToken.None
                            do! ResponsesWebSocket.close connection CancellationToken.None
                            return read
                        finally
                            ResponsesWebSocket.dispose connection
                    })

        match read with
        | Closed close ->
            Assert.Equal(Some WebSocketCloseStatus.EndpointUnavailable, close.status)
            Assert.Equal(Some "server closed", close.description)
        | TextMessage text -> failwith $"Expected close frame, got text: {text}"
    }

[<Fact>]
let ``websocket createAndCollect fails when connection closes before terminal event`` () =
    task {
        let request = WebSocketCreateRequest.ofText Models.gpt_5 "Say hello."

        let! message =
            withWebSocketServer
                (fun socket ->
                    task {
                        let! _ = receiveUtf8 socket CancellationToken.None

                        do!
                            socket.CloseOutputAsync(
                                WebSocketCloseStatus.EndpointUnavailable,
                                "server closed before terminal",
                                CancellationToken.None
                            )
                    })
                (fun config ->
                    task {
                        let! connection = ResponsesWebSocket.connect config CancellationToken.None

                        try
                            let! ex =
                                Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                                    ResponsesWebSocket.createAndCollect connection request CancellationToken.None
                                    :> Task)

                            return ex.Message
                        finally
                            ResponsesWebSocket.dispose connection
                    })

        Assert.Contains("closed before a terminal response event", message)
    }

[<Fact>]
let ``websocket sendEvent serializes concurrent sends`` () =
    task {
        let messageCount = 20
        let received = ResizeArray<string>()

        do!
            withWebSocketServer
                (fun socket ->
                    task {
                        while received.Count < messageCount do
                            let! message = receiveUtf8 socket CancellationToken.None

                            match message with
                            | Some text -> received.Add text
                            | None -> ()

                        do!
                            socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "received",
                                CancellationToken.None
                            )
                    })
                (fun config ->
                    task {
                        let! connection = ResponsesWebSocket.connect config CancellationToken.None

                        try
                            let sends =
                                [| for index in 1..messageCount ->
                                       ResponsesWebSocket.sendEvent
                                           connection
                                           (ResponsesClientEvent.ofText Models.gpt_5 $"message {index}")
                                           CancellationToken.None |]

                            let! _ = Task.WhenAll sends
                            do! ResponsesWebSocket.close connection CancellationToken.None
                            ()
                        finally
                            ResponsesWebSocket.dispose connection
                    })

        let messages = received |> Seq.toList

        Assert.Equal(messageCount, messages.Length)

        messages
        |> List.iter (fun json ->
            let root = parseObject json
            Assert.Equal("response.create", root.GetProperty("type").GetString()))
    }

[<Fact>]
let ``responses transport prepare reuses persistent websocket`` () =
    task {
        let received = ResizeArray<string>()

        let! events =
            withWebSocketServerForConnections
                1
                (fun _ socket ->
                    task {
                        let! message = receiveUtf8 socket CancellationToken.None

                        match message with
                        | Some text -> received.Add text
                        | None -> ()

                        do!
                            sendUtf8
                                socket
                                (responseCompletedJson "resp_prepared" "prepared persistent answer")
                                CancellationToken.None
                    })
                (fun config ->
                    task {
                        use transport =
                            new ResponsesTransport(
                                { ResponsesTransportOptions.create config with
                                    mode = PersistentWebSocket }
                            )

                        do! transport.PrepareAsync CancellationToken.None

                        return!
                            transport.CreateAndCollectAsync(
                                WebSocketCreateRequest.ofText Models.gpt_5 "prepared request",
                                CancellationToken.None
                            )
                    })

        Assert.Single(received) |> ignore
        Assert.Equal("prepared persistent answer", ResponseStream.outputText events)
    }

[<Fact>]
let ``responses transport persistent retries request on fresh websocket`` () =
    task {
        let received = ResizeArray<int * string>()
        let logs = ResizeArray<string>()

        let! events =
            withWebSocketServerForConnections
                2
                (fun connectionIndex socket ->
                    task {
                        let! message = receiveUtf8 socket CancellationToken.None

                        match message with
                        | Some text -> received.Add(connectionIndex, text)
                        | None -> ()

                        if connectionIndex = 1 then
                            do!
                                socket.CloseOutputAsync(
                                    WebSocketCloseStatus.EndpointUnavailable,
                                    "stale socket",
                                    CancellationToken.None
                                )
                        else
                            do!
                                sendUtf8
                                    socket
                                    (responseCompletedJson "resp_persistent_retry" "persistent retry answer")
                                    CancellationToken.None
                    })
                (fun config ->
                    task {
                        use transport =
                            new ResponsesTransport(
                                { ResponsesTransportOptions.create config with
                                    mode = PersistentWebSocket
                                    report = fun message -> logs.Add message }
                            )

                        return!
                            transport.CreateAndCollectAsync(
                                WebSocketCreateRequest.ofText Models.gpt_5 "retry persistent",
                                CancellationToken.None
                            )
                    })

        Assert.Equal<int list>([ 1; 2 ], received |> Seq.map fst |> Seq.toList)
        Assert.Equal("persistent retry answer", ResponseStream.outputText events)
        Assert.Contains(logs, fun log -> log.Contains("retrying", StringComparison.OrdinalIgnoreCase))
    }

[<Fact>]
let ``responses transport per request retries on fresh websocket`` () =
    task {
        let received = ResizeArray<int * string>()

        let! events =
            withWebSocketServerForConnections
                2
                (fun connectionIndex socket ->
                    task {
                        let! message = receiveUtf8 socket CancellationToken.None

                        match message with
                        | Some text -> received.Add(connectionIndex, text)
                        | None -> ()

                        if connectionIndex = 1 then
                            do!
                                socket.CloseOutputAsync(
                                    WebSocketCloseStatus.EndpointUnavailable,
                                    "per request attempt failed",
                                    CancellationToken.None
                                )
                        else
                            do!
                                sendUtf8
                                    socket
                                    (responseCompletedJson "resp_per_request_retry" "per request retry answer")
                                    CancellationToken.None
                    })
                (fun config ->
                    task {
                        use transport =
                            new ResponsesTransport(
                                { ResponsesTransportOptions.create config with
                                    mode = NewWebSocketPerRequest }
                            )

                        do! transport.PrepareAsync CancellationToken.None

                        return!
                            transport.CreateAndCollectAsync(
                                WebSocketCreateRequest.ofText Models.gpt_5 "retry per request",
                                CancellationToken.None
                            )
                    })

        Assert.Equal<int list>([ 1; 2 ], received |> Seq.map fst |> Seq.toList)
        Assert.Equal("per request retry answer", ResponseStream.outputText events)
    }

[<Fact>]
let ``responses transport persistent does not retry cancellation`` () =
    task {
        let received = ResizeArray<string>()

        do!
            withWebSocketServerForConnections
                1
                (fun _ socket ->
                    task {
                        let! message = receiveUtf8 socket CancellationToken.None

                        match message with
                        | Some text -> received.Add text
                        | None -> ()

                        do! Task.Delay(TimeSpan.FromMilliseconds 500.0)
                    })
                (fun config ->
                    task {
                        use transport =
                            new ResponsesTransport(
                                { ResponsesTransportOptions.create config with
                                    mode = PersistentWebSocket }
                            )

                        use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)

                        do!
                            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                                transport.CreateAndCollectAsync(
                                    WebSocketCreateRequest.ofText Models.gpt_5 "cancel persistent",
                                    cancellation.Token
                                ))
                            :> Task
                    })

        Assert.Single(received) |> ignore
    }

[<Fact>]
let ``responses transport per request does not retry cancellation`` () =
    task {
        let received = ResizeArray<string>()

        do!
            withWebSocketServerForConnections
                1
                (fun _ socket ->
                    task {
                        let! message = receiveUtf8 socket CancellationToken.None

                        match message with
                        | Some text -> received.Add text
                        | None -> ()

                        do! Task.Delay(TimeSpan.FromMilliseconds 500.0)
                    })
                (fun config ->
                    task {
                        use transport =
                            new ResponsesTransport(
                                { ResponsesTransportOptions.create config with
                                    mode = NewWebSocketPerRequest }
                            )

                        use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)

                        do!
                            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                                transport.CreateAndCollectAsync(
                                    WebSocketCreateRequest.ofText Models.gpt_5 "cancel per request",
                                    cancellation.Token
                                ))
                            :> Task
                    })

        Assert.Single(received) |> ignore
    }

[<Fact>]
let ``responses transport persistent serializes complete request cycles`` () =
    task {
        let received = ResizeArray<string>()

        let firstRequestReceived =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let releaseFirstResponse =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let! firstText, secondText =
            withWebSocketServerForConnections
                1
                (fun _ socket ->
                    task {
                        let! first = receiveUtf8 socket CancellationToken.None

                        match first with
                        | Some text -> received.Add text
                        | None -> ()

                        firstRequestReceived.TrySetResult() |> ignore
                        do! releaseFirstResponse.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                        do! sendUtf8 socket (responseCompletedJson "resp_first" "first answer") CancellationToken.None

                        let! second = receiveUtf8 socket CancellationToken.None

                        match second with
                        | Some text -> received.Add text
                        | None -> ()

                        do!
                            sendUtf8
                                socket
                                (responseCompletedJson "resp_second" "second answer")
                                CancellationToken.None
                    })
                (fun config ->
                    task {
                        use transport =
                            new ResponsesTransport(
                                { ResponsesTransportOptions.create config with
                                    mode = PersistentWebSocket }
                            )

                        let firstTask =
                            transport.CreateAndCollectAsync(
                                WebSocketCreateRequest.ofText Models.gpt_5 "first request",
                                CancellationToken.None
                            )

                        do! firstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

                        let secondTask =
                            transport.CreateAndCollectAsync(
                                WebSocketCreateRequest.ofText Models.gpt_5 "second request",
                                CancellationToken.None
                            )

                        do! Task.Delay 100
                        Assert.Single(received) |> ignore

                        releaseFirstResponse.TrySetResult() |> ignore

                        let! firstEvents = firstTask
                        let! secondEvents = secondTask

                        return ResponseStream.outputText firstEvents, ResponseStream.outputText secondEvents
                    })

        Assert.Equal("first answer", firstText)
        Assert.Equal("second answer", secondText)
        Assert.Equal(2, received.Count)
    }

[<Fact>]
let ``text delta event deserializes to typed event`` () =
    let json =
        @"{""type"":""response.output_text.delta"",""sequence_number"":7,""response_id"":""resp_123"",""item_id"":""msg_123"",""output_index"":0,""content_index"":0,""delta"":""hel""}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.OutputTextDelta event ->
        Assert.Equal(7, event.sequence_number.Value)
        Assert.Equal("resp_123", event.response_id.Value)
        Assert.Equal("hel", event.delta)
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``function call output item event deserializes to typed function call`` () =
    let json =
        @"{""type"":""response.output_item.added"",""sequence_number"":2,""response_id"":""resp_123"",""output_index"":0,""item"":{""type"":""function_call"",""id"":""fc_123"",""call_id"":""call_123"",""name"":""lookup"",""arguments"":""""}}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.OutputItemAdded event ->
        match event.item with
        | IOitem.Function_call call ->
            Assert.Equal("fc_123", call.id)
            Assert.Equal("call_123", call.call_id)
            Assert.Equal("lookup", call.name)
        | other -> failwith $"Unexpected item: {other}"
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``compaction output item event deserializes to typed compaction`` () =
    let json =
        @"{""type"":""response.output_item.done"",""sequence_number"":2,""response_id"":""resp_123"",""output_index"":0,""item"":{""type"":""compaction"",""id"":""cmp_123"",""encrypted_content"":""encrypted-state""}}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.OutputItemDone event ->
        match event.item with
        | IOitem.Compaction item ->
            Assert.Equal(Some "cmp_123", item.id)
            Assert.Equal("encrypted-state", item.encrypted_content)
        | other -> failwith $"Unexpected item: {other}"
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``reasoning content part event deserializes to typed content`` () =
    let json =
        @"{""type"":""response.content_part.added"",""sequence_number"":3,""response_id"":""resp_123"",""item_id"":""rs_123"",""output_index"":0,""content_index"":0,""part"":{""type"":""reasoning_text"",""text"":""thinking""}}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.ContentPartAdded event ->
        match event.part with
        | Content.Reasoning_text part -> Assert.Equal("thinking", part.text)
        | other -> failwith $"Unexpected content part: {other}"
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``file search searching event deserializes to typed indexed event`` () =
    let json =
        @"{""type"":""response.file_search_call.searching"",""sequence_number"":4,""item_id"":""fs_123"",""output_index"":1}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.FileSearchCallSearching event ->
        Assert.Equal("fs_123", event.item_id)
        Assert.Equal(1, event.output_index)
        Assert.Equal(4, event.sequence_number.Value)
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``code interpreter code delta event deserializes to typed event`` () =
    let json =
        @"{""type"":""response.code_interpreter_call_code.delta"",""sequence_number"":5,""item_id"":""ci_123"",""output_index"":0,""delta"":""print(1)""}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.CodeInterpreterCallCodeDelta event ->
        Assert.Equal("ci_123", event.item_id)
        Assert.Equal("print(1)", event.delta)
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``refusal done event deserializes to typed event`` () =
    let json =
        @"{""type"":""response.refusal.done"",""sequence_number"":6,""item_id"":""msg_123"",""output_index"":0,""content_index"":0,""refusal"":""I cannot help with that.""}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.RefusalDone event ->
        Assert.Equal("msg_123", event.item_id)
        Assert.Equal("I cannot help with that.", event.refusal)
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``output text annotation event preserves typed annotation payload`` () =
    let json =
        @"{""type"":""response.output_text.annotation.added"",""sequence_number"":7,""item_id"":""msg_123"",""output_index"":0,""content_index"":0,""annotation_index"":0,""annotation"":{""type"":""url_citation"",""url"":""https://example.com""}}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.OutputTextAnnotationAdded event ->
        Assert.Equal(0, event.annotation_index)
        Assert.Equal("url_citation", event.annotation.GetProperty("type").GetString())
        Assert.Equal("https://example.com", event.annotation.GetProperty("url").GetString())
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``image generation partial image event deserializes to typed event`` () =
    let json =
        @"{""type"":""response.image_generation_call.partial_image"",""sequence_number"":8,""item_id"":""ig_123"",""output_index"":0,""partial_image_index"":2,""partial_image_b64"":""abc123""}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.ImageGenerationCallPartialImage event ->
        Assert.Equal("ig_123", event.item_id)
        Assert.Equal(2, event.partial_image_index)
        Assert.Equal("abc123", event.partial_image_b64)
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``completed response event keeps typed response output`` () =
    let json =
        @"{""type"":""response.completed"",""sequence_number"":12,""response"":{""id"":""resp_123"",""object"":""response"",""created_at"":1710000000,""status"":""completed"",""error"":null,""incomplete_details"":null,""instructions"":null,""max_output_tokens"":null,""model"":""gpt-5"",""metadata"":{""CORR_ID"":""42""},""output"":[{""type"":""message"",""id"":""msg_123"",""status"":""completed"",""role"":""assistant"",""content"":[{""type"":""output_text"",""text"":""Hello world"",""annotations"":null}]}],""parallel_tool_calls"":true,""previous_response_id"":null,""reasoning"":null,""store"":false,""temperature"":1,""text"":null,""tool_choice"":""auto"",""tools"":[],""top_p"":1,""truncation"":""auto"",""usage"":{""input_tokens"":10,""output_tokens"":2,""total_tokens"":12,""input_tokens_details"":null,""output_tokens_details"":null},""user"":null}}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.ResponseCompleted event ->
        Assert.Equal("resp_123", event.response.id)
        Assert.Equal(Some "42", event.response.metadata |> Option.bind (Map.tryFind "CORR_ID"))
        Assert.Equal("Hello world", ResponseStream.outputText [ ResponseStreamEvent.ResponseCompleted event ])
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``error event deserializes to typed error`` () =
    let json =
        @"{""type"":""error"",""sequence_number"":4,""status"":404,""error"":{""code"":""previous_response_not_found"",""message"":""Previous response not found."",""type"":""invalid_request_error"",""param"":""previous_response_id""}}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.Error event ->
        Assert.Equal(404, event.status.Value)
        Assert.Equal("previous_response_not_found", event.error.code)
        Assert.Equal(Some "previous_response_id", event.error.param)
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``error event without code still deserializes to typed error`` () =
    let json =
        @"{""type"":""error"",""status"":400,""error"":{""type"":""invalid_request_error"",""message"":""Missing required parameter: input.""}}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.Error event ->
        Assert.Equal(400, event.status.Value)
        Assert.Equal("invalid_request_error", event.error.code)
        Assert.Equal("Missing required parameter: input.", event.error.message)
        Assert.Equal(Some "invalid_request_error", event.error.``type``)
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``unknown stream event is preserved with raw payload`` () =
    let json = @"{""type"":""response.new_future_event"",""value"":123}"

    match ResponseStreamEvent.deserialize json with
    | ResponseStreamEvent.Unknown event ->
        Assert.Equal("response.new_future_event", event.eventType)
        Assert.Equal(123, event.raw.GetProperty("value").GetInt32())
    | other -> failwith $"Unexpected event: {other}"

[<Fact>]
let ``legacy request message serialization still uses Responses item tags`` () =
    let root: JsonElement =
        createRequest ()
        |> fun req -> JsonSerializer.Serialize(req, Api.serOpts) |> parseObject

    let firstInput = root.GetProperty("input") |> firstArrayItem

    let content: JsonElement list =
        firstInput.GetProperty("content").EnumerateArray() |> Seq.toList

    Assert.Equal("message", firstInput.GetProperty("type").GetString())
    Assert.Contains(content, fun item -> item.GetProperty("type").GetString() = "input_text")
    Assert.Contains(content, fun item -> item.GetProperty("type").GetString() = "input_image")

[<Fact>]
let ``http request serializes context management compaction`` () =
    let request =
        { createRequest () with
            context_management = Some [ ResponseContextManagement.Compaction {| compact_threshold = 200000 |} ] }

    let root: JsonElement =
        request
        |> fun req -> JsonSerializer.Serialize(req, Api.serOpts)
        |> parseObject

    let firstContext = root.GetProperty("context_management") |> firstArrayItem

    Assert.Equal("compaction", firstContext.GetProperty("type").GetString())
    Assert.Equal(200000, firstContext.GetProperty("compact_threshold").GetInt32())

[<Fact(Skip = "Live OpenAI HTTP smoke test; enable manually when OPENAI_API_KEY is available.")>]
let ``Create a response`` () =
    task {
        let req = createRequest ()
        let! resp = Api.create req (Api.defaultClient ())
        let text = FsResponses.RUtils.outputText resp
        dmp text
        let corrId = resp.metadata |> Option.bind (Map.tryFind "CORR_ID")
        Assert.Equal(Some "1", corrId)
    }

[<Fact(Skip = "Live OpenAI HTTP smoke test; enable manually when OPENAI_API_KEY is available.")>]
let ``List stored items`` () =
    let req = { createRequest () with store = true }
    let resp = Api.create req (Api.defaultClient ()) |> runT

    let msgIds1 =
        resp.output
        |> List.choose (function
            | IOitem.Message m -> m.id
            | _ -> None)

    let req2 =
        { Request.Default with
            previous_response_id = Some resp.id
            input =
                [ IOitem.Message
                      { Message.Default with
                          content = [ Content.Input_text {| text = "Is there a search box visible in the image" |} ] } ]
            store = true
            metadata = [ "CORR_ID", "2" ] |> Map.ofList |> Some }

    let resp2 = Api.create req2 (Api.defaultClient ()) |> runT
    let text = FsResponses.RUtils.outputText resp2
    dmp text
    let expectedMsgIds = set msgIds1
    let corrId = resp2.metadata |> Option.bind (Map.tryFind "CORR_ID")

    let listResp =
        Api.list
            { ListRequest.Create resp2.id with
                order = Some Asc }
            (Api.defaultClient ())
        |> runT

    dmp (sprintf "List response: %A" listResp)

    let listIds =
        listResp.data
        |> List.choose (function
            | IOitem.Message m -> m.id
            | _ -> None)
        |> set

    let accountedFor = Set.intersect expectedMsgIds listIds
    Assert.Equal(Some "2", corrId)
    Assert.Equal<Set<string>>(expectedMsgIds, accountedFor)

[<Fact(Skip = "Live OpenAI HTTP smoke test; enable manually when OPENAI_API_KEY is available.")>]
let ``Create and delete a response`` () =
    task {
        let req = { createRequest () with store = true }
        let! resp = Api.create req (Api.defaultClient ())
        let text = FsResponses.RUtils.outputText resp
        dmp text
        let corrId = resp.metadata |> Option.bind (Map.tryFind "CORR_ID")
        let! deleteResult = Api.delete resp.id (Api.defaultClient ())
        Assert.Equal(Some "1", corrId)
        Assert.True(deleteResult.deleted, "The stored response should be deleted.")
    }
