namespace FsResponses

open System
open System.Diagnostics
open System.Net.WebSockets
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

type ResponsesTransportMode =
    | PersistentWebSocket
    | NewWebSocketPerRequest

module ResponsesTransportMode =
    let storageName =
        function
        | PersistentWebSocket -> "persistent_websocket"
        | NewWebSocketPerRequest -> "new_websocket_per_request"

type ResponsesTransportOptions =
    { webSocketConfig: ResponseWebSocketConfig
      mode: ResponsesTransportMode
      maxRequestRetries: int
      responseEventIdleTimeout: TimeSpan option
      report: string -> unit }

module ResponsesTransportOptions =
    let create webSocketConfig =
        { webSocketConfig = webSocketConfig
          mode = PersistentWebSocket
          maxRequestRetries = 1
          responseEventIdleTimeout = Some(TimeSpan.FromSeconds 15.0)
          report = ignore }

type IResponsesTransport =
    inherit IDisposable

    abstract PrepareAsync: CancellationToken -> Task
    abstract CreateAndCollectAsync: WebSocketCreateRequest * CancellationToken -> Task<ResponseStreamEvent list>

type ResponsesTransport(options: ResponsesTransportOptions) =
    let connectionGate = obj ()
    let requestGate = new SemaphoreSlim(1, 1)
    let config = options.webSocketConfig
    let mutable connection: ResponseWebSocket option = None
    let mutable connectionTask: Task<ResponseWebSocket> option = None
    let mutable nextRequestId = 0L

    let report message =
        try
            options.report $"Answer Responses transport: {message}"
        with _ ->
            ()

    let connectionId (connection: ResponseWebSocket) =
        RuntimeHelpers.GetHashCode(connection.socket)

    let socketState (connection: ResponseWebSocket) = string connection.socket.State

    let requestSummary (request: WebSocketCreateRequest) =
        let previousResponseId =
            request.previous_response_id |> Option.defaultValue "<none>"

        let toolCount = request.tools |> Option.map List.length |> Option.defaultValue 0

        let maxOutputTokens =
            request.max_output_tokens |> Option.map string |> Option.defaultValue "n/a"

        $"requestId={Interlocked.Increment(&nextRequestId)}; model={request.model}; previousResponseId={previousResponseId}; inputItems={request.input.Length}; tools={toolCount}; maxOutputTokens={maxOutputTokens}"

    let connectionIsOpen (connection: ResponseWebSocket) =
        connection.socket.State = WebSocketState.Open

    let startConnection cancellationToken =
        report $"connection-open-start; endpoint={config.endpoint}."
        let task = ResponsesWebSocket.connect config cancellationToken
        connectionTask <- Some task
        task

    let abandonConnectionTask (task: Task<ResponseWebSocket>) =
        lock connectionGate (fun () ->
            match connectionTask with
            | Some current when Object.ReferenceEquals(current, task) -> connectionTask <- None
            | _ -> ())

    let clearConnection (staleConnection: ResponseWebSocket) =
        lock connectionGate (fun () ->
            match connection with
            | Some current when Object.ReferenceEquals(current, staleConnection) ->
                ResponsesWebSocket.dispose current
                connection <- None
            | _ -> ())

    let liveConnection cancellationToken =
        task {
            let connectionOrTask =
                lock connectionGate (fun () ->
                    match connection with
                    | Some current when connectionIsOpen current ->
                        report
                            $"connection-reuse; connectionId={connectionId current}; socketState={socketState current}."

                        Choice1Of2 current
                    | Some current ->
                        report
                            $"connection-stale-dispose; connectionId={connectionId current}; socketState={socketState current}."

                        ResponsesWebSocket.dispose current
                        connection <- None

                        match connectionTask with
                        | Some task ->
                            report "connection-await-pending."
                            Choice2Of2 task
                        | None -> startConnection cancellationToken |> Choice2Of2
                    | None ->
                        match connectionTask with
                        | Some task ->
                            report "connection-await-pending."
                            Choice2Of2 task
                        | None -> startConnection cancellationToken |> Choice2Of2)

            match connectionOrTask with
            | Choice1Of2 current -> return current
            | Choice2Of2 pendingConnection ->
                try
                    let! openedConnection = pendingConnection.WaitAsync(cancellationToken)

                    lock connectionGate (fun () ->
                        connection <- Some openedConnection

                        match connectionTask with
                        | Some current when Object.ReferenceEquals(current, pendingConnection) ->
                            connectionTask <- None
                        | _ -> ())

                    report
                        $"connection-open-completed; connectionId={connectionId openedConnection}; socketState={socketState openedConnection}."

                    return openedConnection
                with ex ->
                    abandonConnectionTask pendingConnection
                    report $"connection-open-failed; error={ex.GetType().Name}: {ex.Message}."
                    return raise ex
        }

    let runPersistentAttempt request cancellationToken =
        task {
            let summary = requestSummary request
            let sw = Stopwatch.StartNew()
            report $"request-live-connection-start; mode=persistent_websocket; {summary}."
            let! current = liveConnection cancellationToken

            try
                report
                    $"request-live-connection-ready; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; connectionId={connectionId current}; socketState={socketState current}; {summary}."

                let trace message =
                    report $"request-trace; connectionId={connectionId current}; {summary}; {message}"

                return!
                    ResponsesWebSocket.createAndCollectWithTrace
                        options.responseEventIdleTimeout
                        trace
                        current
                        request
                        cancellationToken
            with ex ->
                report
                    $"request-abandoning-connection; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; connectionId={connectionId current}; socketState={socketState current}; {summary}; error={ex.GetType().Name}: {ex.Message}."

                clearConnection current
                return raise ex
        }

    let runNewConnectionAttempt request cancellationToken =
        task {
            let summary = requestSummary request
            let sw = Stopwatch.StartNew()
            report $"request-new-connection-start; mode=new_websocket_per_request; {summary}."
            let! current = ResponsesWebSocket.connect config cancellationToken

            try
                report
                    $"request-new-connection-ready; elapsed={sw.Elapsed.TotalMilliseconds:F0}ms; connectionId={connectionId current}; socketState={socketState current}; {summary}."

                let trace message =
                    report $"request-trace; connectionId={connectionId current}; {summary}; {message}"

                return!
                    ResponsesWebSocket.createAndCollectWithTrace
                        options.responseEventIdleTimeout
                        trace
                        current
                        request
                        cancellationToken
            finally
                ResponsesWebSocket.dispose current
        }

    let runAttempt request cancellationToken =
        match options.mode with
        | PersistentWebSocket -> runPersistentAttempt request cancellationToken
        | NewWebSocketPerRequest -> runNewConnectionAttempt request cancellationToken

    let runWithRetry request cancellationToken =
        let maxRequestRetries = max 0 options.maxRequestRetries
        let modeName = ResponsesTransportMode.storageName options.mode

        let rec run attempt =
            task {
                try
                    return! runAttempt request cancellationToken
                with
                | :? OperationCanceledException as ex -> return raise ex
                | ex when attempt < maxRequestRetries ->
                    options.report
                        $"Responses websocket request failed; retrying with a fresh connection; mode={modeName}; attempt={attempt + 1}; maxRequestRetries={maxRequestRetries}; error={ex.GetType().Name}: {ex.Message}."

                    return! run (attempt + 1)
                | ex ->
                    if maxRequestRetries > 0 then
                        options.report
                            $"Responses websocket request failed after retry; mode={modeName}; attempts={attempt + 1}; maxRequestRetries={maxRequestRetries}; error={ex.GetType().Name}: {ex.Message}."

                    return raise ex
            }

        run 0

    member _.PrepareAsync(cancellationToken) =
        task {
            match options.mode with
            | NewWebSocketPerRequest -> return ()
            | PersistentWebSocket ->
                report "prepare-start; mode=persistent_websocket."
                let! _ = liveConnection cancellationToken
                report "prepare-completed; mode=persistent_websocket."
                return ()
        }

    member _.CreateAndCollectAsync(request, cancellationToken) =
        task {
            match options.mode with
            | NewWebSocketPerRequest -> return! runWithRetry request cancellationToken
            | PersistentWebSocket ->
                let gateSw = Stopwatch.StartNew()
                report "request-gate-wait-start; mode=persistent_websocket."

                try
                    do! requestGate.WaitAsync cancellationToken
                    gateSw.Stop()

                    report
                        $"request-gate-acquired; elapsed={gateSw.Elapsed.TotalMilliseconds:F0}ms; mode=persistent_websocket."
                with :? OperationCanceledException as ex ->
                    gateSw.Stop()

                    report
                        $"request-gate-canceled; elapsed={gateSw.Elapsed.TotalMilliseconds:F0}ms; mode=persistent_websocket; error={ex.Message}."

                    raise ex

                try
                    return! runWithRetry request cancellationToken
                finally
                    report "request-gate-release; mode=persistent_websocket."
                    requestGate.Release() |> ignore
        }

    member _.Dispose() =
        lock connectionGate (fun () ->
            match connection with
            | Some current ->
                ResponsesWebSocket.dispose current
                connection <- None
            | None -> ()

            connectionTask <- None)

        requestGate.Dispose()

    interface IResponsesTransport with
        member this.PrepareAsync cancellationToken = this.PrepareAsync cancellationToken

        member this.CreateAndCollectAsync(request, cancellationToken) =
            this.CreateAndCollectAsync(request, cancellationToken)

        member this.Dispose() = this.Dispose()
