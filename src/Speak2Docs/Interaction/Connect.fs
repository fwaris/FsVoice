namespace Speak2Docs

open System
open System.Text.Json
open System.Threading
open System.Threading.Channels
open FSharp.Control
open FsVoice.Types
open Speak2Docs.WorkFlow
open RTOpenAI.Api

module Connect =
    let private log (mailbox: Channel<Msg>) text =
        mailbox.Writer.TryWrite(Log_Append text) |> ignore

    let private realtimeState (state: RTOpenAI.WebRTC.State) =
        if state.IsConnected then RealtimeConnected
        elif state.IsConnecting then RealtimeConnecting
        else RealtimeDisconnected

    let private notifyHost (session: IVoiceSession<ToHost, FromHost>) token message =
        session.SendFromHostAsync(message, token).ContinueWith(ignore) |> ignore

    let private startClientPump
        (mailbox: Channel<Msg>)
        (connection: Connection)
        (clientEvents: Channel<JsonElement>)
        token
        =
        async {
            try
                do!
                    clientEvents.Reader.ReadAllAsync(token)
                    |> AsyncSeq.iter (fun eventJson -> eventJson.GetRawText() |> connection.WebRtcClient.Send |> ignore)
            with
            | :? OperationCanceledException -> ()
            | ex -> log mailbox $"Realtime client event pump failed: {ex.Message}"
        }
        |> fun work -> Async.Start(work, token)

    let private startServerPump
        (mailbox: Channel<Msg>)
        (connection: Connection)
        (serverEvents: Channel<JsonElement>)
        token
        =
        async {
            try
                do!
                    connection.WebRtcClient.OutputChannel.Reader.ReadAllAsync(token)
                    |> AsyncSeq.iterAsync (fun document ->
                        serverEvents.Writer.WriteAsync(document.RootElement.Clone(), token).AsTask()
                        |> Async.AwaitTask)
            with
            | :? OperationCanceledException -> ()
            | ex -> log mailbox $"Realtime server event pump failed: {ex.Message}"
        }
        |> fun work -> Async.Start(work, token)

    let private connectRealtime
        (mailbox: Channel<Msg>)
        (session: IVoiceSession<ToHost, FromHost>)
        token
        apiKey
        (connection: Connection)
        realtimeSession
        =
        task {
            try
                let keyRequest =
                    { KeyReq.Default with
                        session = realtimeSession }

                let! ephemeralKey = Connection.getEphemeralKey apiKey keyRequest
                do! Connection.connect ephemeralKey connection
            with ex ->
                mailbox.Writer.TryWrite(RealtimeConnectFailed ex.Message) |> ignore
                notifyHost session token (RealtimeConnectionFailed ex.Message)
        }
        |> ignore

    let private startHostPump
        (mailbox: Channel<Msg>)
        (session: IVoiceSession<ToHost, FromHost>)
        token
        apiKey
        (connection: Connection)
        =
        async {
            try
                do!
                    session.ToHost.ReadAllAsync(token)
                    |> AsyncSeq.iter (function
                        | Log text -> log mailbox text
                        | RequestRealtimeConnection realtimeSession ->
                            connectRealtime mailbox session token apiKey connection realtimeSession
                        | TranscriptFinalized _ -> ()
                        | ContextReady(_, chunks, inventory) ->
                            log mailbox $"Context ready: {chunks.Length} chunk(s), {inventory.Length} source(s)."
                        | OracleResponseReady(_, Some candidate) ->
                            log mailbox $"Oracle response ready: {Text.truncate 220 candidate.answer}"
                        | OracleResponseReady(_, None) -> log mailbox "Oracle response unavailable."
                        | FlowEnded abnormal ->
                            if abnormal then
                                log mailbox "Realtime flow ended abnormally."
                            else
                                log mailbox "Realtime flow ended.")
            with
            | :? OperationCanceledException -> ()
            | ex -> log mailbox $"Host event pump failed: {ex.Message}"
        }
        |> fun work -> Async.Start(work, token)

    let start (parms: StartParams) : Async<Result<ConnectionBundle, exn>> =
        async {
            try
                let apiKey =
                    RuntimeSettings.snapshot parms.runtimeSettings
                    |> RuntimeSettings.string RuntimeSettings.OpenAiKey parms.apiKey

                match Text.notEmpty apiKey with
                | None -> return Error(InvalidOperationException "OpenAI API key is required." :> exn)
                | Some apiKey ->
                    let! hasPermission = Audio.haveRecordPermission ()

                    if not hasPermission then
                        return Error(UnauthorizedAccessException "Microphone permission was not granted." :> exn)
                    else
                        let conn = Connection.create ()
                        let cancellation = new CancellationTokenSource()
                        let serverEvents = Channel.CreateUnbounded<JsonElement>()
                        let clientEvents = Channel.CreateUnbounded<JsonElement>()

                        let voiceConnection =
                            { VoiceConnection.receiver = serverEvents.Reader
                              sender = clientEvents.Writer }

                        let! session =
                            parms.orchestration.CreateSessionAsync(parms.context, voiceConnection, cancellation.Token)
                            |> Async.AwaitTask

                        let stateSubscription =
                            conn.WebRtcClient.StateChanged.Subscribe(fun state ->
                                parms.mailbox.Writer.TryWrite(WebRTC_StateChanged state) |> ignore
                                notifyHost session cancellation.Token (RealtimeStateChanged(realtimeState state)))

                        let conn =
                            { conn with
                                Disposables = stateSubscription :: conn.Disposables }

                        startClientPump parms.mailbox conn clientEvents cancellation.Token
                        startServerPump parms.mailbox conn serverEvents cancellation.Token
                        startHostPump parms.mailbox session cancellation.Token apiKey conn

                        do! session.StartAsync cancellation.Token |> Async.AwaitTask

                        return
                            Ok
                                { session = session
                                  connection = conn
                                  serverEvents = serverEvents
                                  clientEvents = clientEvents
                                  cancellation = cancellation }
            with ex ->
                Log.exn (ex, "Connect.start")
                return Error ex
        }

    let stop (bundle: ConnectionBundle) : Async<Result<unit, exn>> =
        async {
            try
                bundle.cancellation.Cancel()
                bundle.serverEvents.Writer.TryComplete() |> ignore
                bundle.clientEvents.Writer.TryComplete() |> ignore

                do! bundle.session.StopAsync CancellationToken.None |> Async.AwaitTask
                Connection.close bundle.connection
                bundle.cancellation.Dispose()

                return Ok()
            with ex ->
                Log.exn (ex, "Connect.stop")
                return Error ex
        }
