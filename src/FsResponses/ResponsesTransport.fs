namespace FsResponses

open System
open System.Net.WebSockets
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
      report: string -> unit }

module ResponsesTransportOptions =
    let create webSocketConfig =
        { webSocketConfig = webSocketConfig
          mode = PersistentWebSocket
          maxRequestRetries = 1
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

    let connectionIsOpen (connection: ResponseWebSocket) =
        connection.socket.State = WebSocketState.Open

    let startConnection cancellationToken =
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
                    | Some current when connectionIsOpen current -> Choice1Of2 current
                    | Some current ->
                        ResponsesWebSocket.dispose current
                        connection <- None

                        match connectionTask with
                        | Some task -> Choice2Of2 task
                        | None -> startConnection cancellationToken |> Choice2Of2
                    | None ->
                        match connectionTask with
                        | Some task -> Choice2Of2 task
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

                    return openedConnection
                with ex ->
                    abandonConnectionTask pendingConnection
                    return raise ex
        }

    let runPersistentAttempt request cancellationToken =
        task {
            let! current = liveConnection cancellationToken

            try
                return! ResponsesWebSocket.createAndCollect current request cancellationToken
            with ex ->
                clearConnection current
                return raise ex
        }

    let runNewConnectionAttempt request cancellationToken =
        ResponsesWebSocket.createWithNewConnection config request cancellationToken

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
                let! _ = liveConnection cancellationToken
                return ()
        }

    member _.CreateAndCollectAsync(request, cancellationToken) =
        task {
            match options.mode with
            | NewWebSocketPerRequest -> return! runWithRetry request cancellationToken
            | PersistentWebSocket ->
                do! requestGate.WaitAsync cancellationToken

                try
                    return! runWithRetry request cancellationToken
                finally
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
