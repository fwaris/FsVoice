namespace FsVoice.Ctx

open System
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks

type internal QaResponsesRequestRunner =
    FsResponses.WebSocketCreateRequest -> CancellationToken -> Task<FsResponses.ResponseStreamEvent list>

type internal QaResponsesPrepareRunner = CancellationToken -> Task<unit>

type internal QaResponsesTransportOverride =
    { prepareAnswerConnection: QaResponsesPrepareRunner option
      runAnswerRequest: QaResponsesRequestRunner
      runNewAnswerRequest: QaResponsesRequestRunner option
      runStatelessRequest: QaResponsesRequestRunner }

module internal QaResponsesTransportOverride =
    let same runner =
        { prepareAnswerConnection = None
          runAnswerRequest = runner
          runNewAnswerRequest = None
          runStatelessRequest = runner }

type internal QaResponsesTransport
    (
        options: QaSessionOptions,
        sessionCancellation: CancellationTokenSource,
        report: string -> unit,
        transportOverride: QaResponsesTransportOverride option
    ) =
    let answerConnectionGate = obj ()
    let mutable answerConnection: FsResponses.ResponseWebSocket option = None
    let mutable answerConnectionTask: Task<FsResponses.ResponseWebSocket> option = None
    let config = options.answerResponseWebSocketConfig

    let answerConnectionIsOpen (connection: FsResponses.ResponseWebSocket) =
        connection.socket.State = WebSocketState.Open

    let startAnswerConnection config cancellationToken =
        let task = FsResponses.ResponsesWebSocket.connect config cancellationToken
        answerConnectionTask <- Some task
        task

    let abandonAnswerConnectionTask (task: Task<FsResponses.ResponseWebSocket>) =
        lock answerConnectionGate (fun () ->
            match answerConnectionTask with
            | Some current when Object.ReferenceEquals(current, task) -> answerConnectionTask <- None
            | _ -> ())

    let clearAnswerConnection (connection: FsResponses.ResponseWebSocket) =
        lock answerConnectionGate (fun () ->
            match answerConnection with
            | Some current when Object.ReferenceEquals(current, connection) ->
                FsResponses.ResponsesWebSocket.dispose current
                answerConnection <- None
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
                    abandonAnswerConnectionTask connectionTask
                    return raise ex
        }

    let runOnLiveAnswerConnection config request cancellationToken =
        task {
            let! connection = liveAnswerConnection config cancellationToken

            try
                return! FsResponses.ResponsesWebSocket.createAndCollect connection request cancellationToken
            with ex ->
                clearAnswerConnection connection
                return raise ex
        }

    let persistentRunner request cancellationToken =
        match transportOverride with
        | Some overrideTransport -> overrideTransport.runAnswerRequest request cancellationToken
        | None -> runOnLiveAnswerConnection config request cancellationToken

    let runPersistentAnswerRequest request cancellationToken =
        let rec run attempt =
            task {
                try
                    return! persistentRunner request cancellationToken
                with
                | :? OperationCanceledException as ex -> return raise ex
                | ex when attempt = 1 ->
                    report
                        $"Answer Responses persistent websocket request failed; reconnecting and retrying once; error={ex.GetType().Name}: {ex.Message}."

                    return! run (attempt + 1)
                | ex ->
                    report
                        $"Answer Responses persistent websocket request failed after retry; error={ex.GetType().Name}: {ex.Message}."

                    return raise ex
            }

        run 1

    member _.RunAnswerRequest request cancellationToken =
        task {
            match options.answerTransportMode, transportOverride with
            | NewWebSocketPerRequest, Some { runNewAnswerRequest = Some runNew } ->
                return! runNew request cancellationToken
            | NewWebSocketPerRequest, Some overrideTransport ->
                return! overrideTransport.runAnswerRequest request cancellationToken
            | PersistentWebSocket, Some _ -> return! runPersistentAnswerRequest request cancellationToken
            | NewWebSocketPerRequest, None ->
                return! FsResponses.ResponsesWebSocket.createWithNewConnection config request cancellationToken
            | PersistentWebSocket, None -> return! runPersistentAnswerRequest request cancellationToken
        }

    member _.PrepareAnswerConnection cancellationToken =
        task {
            match options.answerTransportMode, transportOverride with
            | NewWebSocketPerRequest, _ -> return ()
            | PersistentWebSocket, Some { prepareAnswerConnection = Some prepare } -> return! prepare cancellationToken
            | PersistentWebSocket, Some _ -> return ()
            | PersistentWebSocket, None ->
                let! _ = liveAnswerConnection config cancellationToken
                return ()
        }

    member _.RunStatelessRequest request cancellationToken =
        task {
            match transportOverride with
            | Some overrideTransport -> return! overrideTransport.runStatelessRequest request cancellationToken
            | None -> return! FsResponses.ResponsesWebSocket.createWithNewConnection config request cancellationToken
        }

    member _.Dispose() =
        lock answerConnectionGate (fun () ->
            match answerConnection with
            | Some connection ->
                FsResponses.ResponsesWebSocket.dispose connection
                answerConnection <- None
            | None -> ()

            answerConnectionTask <- None)
