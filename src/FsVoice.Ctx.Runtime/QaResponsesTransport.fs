namespace FsVoice.Ctx

open System
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks

type internal QaResponsesTransport
    (options: QaSessionOptions, sessionCancellation: CancellationTokenSource, report: string -> unit) =
    let answerConnectionGate = obj ()
    let mutable answerConnection: FsResponses.ResponseWebSocket option = None
    let mutable answerConnectionTask: Task<FsResponses.ResponseWebSocket> option = None

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

    member _.RunRequest request cancellationToken =
        task {
            match options.answerTransport with
            | Some(QaAnswerTransport.CustomResponsesWebSocket createAndCollect) ->
                return! createAndCollect request cancellationToken
            | Some(QaAnswerTransport.OpenAIResponsesWebSocket config) ->
                return! runOnLiveAnswerConnection config request cancellationToken
            | None -> return []
        }

    member this.RunWarmupRequest request cancellationToken =
        this.RunRequest request cancellationToken

    member _.RunOfflineRequest request cancellationToken =
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

    member _.Dispose() =
        lock answerConnectionGate (fun () ->
            match answerConnection with
            | Some connection ->
                FsResponses.ResponsesWebSocket.dispose connection
                answerConnection <- None
            | None -> ()

            answerConnectionTask <- None)
