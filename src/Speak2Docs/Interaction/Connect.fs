namespace Speak2Docs

open System
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control
open FsVoice.Platform
open Speak2Docs.WorkFlow
open RTOpenAI.Api
open RTOpenAI.WebRTC

module Connect =
    let private log (mailbox: Channel<Msg>) text =
        mailbox.Writer.TryWrite(Log_Append text) |> ignore

    // The native WebRTC path can appear connected even when SDP answer creation failed before any server events arrive.
    let private realtimeServerEventTimeout = TimeSpan.FromSeconds 8.0

    let private realtimeNoServerEventsError =
        [ "possible OpenAI `insufficient_quota` while creating the realtime SDP answer; "
          "no realtime server events arrived after connection setup. Check OpenAI billing/quota and realtime model access." ]
        |> String.concat ""

    let private realtimeState (state: RTOpenAI.WebRTC.State) =
        if state.IsConnected then RealtimeConnected
        elif state.IsConnecting then RealtimeConnecting
        else RealtimeDisconnected

    let private notifyHost (session: IVoiceSession<ToHost, FromHost>) token message =
        session.SendFromHostAsync(message, token).ContinueWith(ignore) |> ignore

    let private platformDefaultToSpeaker =
#if ANDROID || IOS
        true
#else
        false
#endif

    let private audioDefaultToSpeaker settings =
        RuntimeSettings.audioDefaultToSpeaker platformDefaultToSpeaker settings

    let private iosAudioRoutePolicy settings =
        if audioDefaultToSpeaker settings then
            IosAudioRoutePolicy.Speakerphone
        else
            IosAudioRoutePolicy.ReceiverOrHeadset

    let private webRtcClientConfig settings =
        { WebRtcClientConfig.Default with
            IosAudioRoutePolicy = iosAudioRoutePolicy settings }

    let private startupMicrophoneGateEnabled settings =
#if IOS
        audioDefaultToSpeaker settings
#else
        false
#endif

    let private applyAudioRoute settings =
        Audio.applyDefaultToSpeaker (audioDefaultToSpeaker settings)

    let private trySetMicrophoneEnabled (mailbox: Channel<Msg>) (connection: Connection) enabled reason =
        try
            connection.WebRtcClient.SetMicrophoneEnabled enabled
            log mailbox $"Realtime microphone enabled={enabled} ({reason})."
        with ex ->
            log mailbox $"Unable to set realtime microphone enabled={enabled} ({reason}): {ex.Message}"

    let private jsonEventType (json: JsonElement) =
        match json.TryGetProperty("type") with
        | true, property when property.ValueKind = JsonValueKind.String -> property.GetString() |> Option.ofObj
        | _ -> None

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
        (firstServerEvent: TaskCompletionSource<unit>)
        startupMicrophoneGate
        token
        =
        async {
            let mutable startupMicrophoneGateReleased = not startupMicrophoneGate

            try
                do!
                    connection.WebRtcClient.OutputChannel.Reader.ReadAllAsync(token)
                    |> AsyncSeq.iterAsync (fun document ->
                        firstServerEvent.TrySetResult(()) |> ignore

                        if
                            startupMicrophoneGate
                            && not startupMicrophoneGateReleased
                            && (document.RootElement
                                |> jsonEventType
                                |> Option.exists ((=) "output_audio_buffer.stopped"))
                        then
                            startupMicrophoneGateReleased <- true

                            trySetMicrophoneEnabled mailbox connection true "startup output_audio_buffer.stopped"

                        serverEvents.Writer.WriteAsync(document.RootElement.Clone(), token).AsTask()
                        |> Async.AwaitTask)
            with
            | :? OperationCanceledException -> ()
            | ex -> log mailbox $"Realtime server event pump failed: {ex.Message}"
        }
        |> fun work -> Async.Start(work, token)

    let private startRealtimeServerEventWatchdog
        (mailbox: Channel<Msg>)
        connectionId
        (firstServerEvent: Task)
        (token: CancellationToken)
        =
        async {
            try
                let delay = Task.Delay(realtimeServerEventTimeout, token)
                let! completed = Task.WhenAny(firstServerEvent, delay) |> Async.AwaitTask

                if
                    obj.ReferenceEquals(completed, delay)
                    && not firstServerEvent.IsCompleted
                    && not token.IsCancellationRequested
                then
                    mailbox.Writer.TryWrite(RealtimeConnectFailed(connectionId, realtimeNoServerEventsError))
                    |> ignore
            with
            | :? OperationCanceledException -> ()
            | ex -> log mailbox $"Realtime connection watchdog failed: {ex.Message}"
        }
        |> fun work -> Async.Start(work, token)

    let private connectRealtime
        (mailbox: Channel<Msg>)
        (session: IVoiceSession<ToHost, FromHost>)
        (token: CancellationToken)
        (apiKey: string)
        (connectionId: string)
        (connection: Connection)
        (firstServerEvent: Task)
        (realtimeSession: RTOpenAI.Events.Session)
        =
        task {
            try
                let keyRequest =
                    { KeyReq.Default with
                        session = realtimeSession }

                let! ephemeralKey = Connection.getEphemeralKey apiKey keyRequest
                token.ThrowIfCancellationRequested()
                do! (Connection.connect ephemeralKey connection).WaitAsync(token)
                token.ThrowIfCancellationRequested()
                startRealtimeServerEventWatchdog mailbox connectionId firstServerEvent token
            with ex ->
                if token.IsCancellationRequested then
                    log mailbox $"Realtime connect canceled for connection {connectionId}."
                else
                    mailbox.Writer.TryWrite(RealtimeConnectFailed(connectionId, ex.Message))
                    |> ignore

                    notifyHost session token (RealtimeConnectionFailed ex.Message)
        }
        |> ignore

    let private startHostPump
        (mailbox: Channel<Msg>)
        (session: IVoiceSession<ToHost, FromHost>)
        token
        apiKey
        connectionId
        (connection: Connection)
        (firstServerEvent: Task)
        =
        async {
            let mutable realtimeConnectionRequested = false

            try
                do!
                    session.ToHost.ReadAllAsync(token)
                    |> AsyncSeq.iter (function
                        | Log text -> log mailbox text
                        | RequestRealtimeConnection realtimeSession ->
                            if realtimeConnectionRequested then
                                log mailbox "Duplicate realtime connection request ignored."
                            else
                                realtimeConnectionRequested <- true
                                connectRealtime mailbox session token apiKey connectionId connection firstServerEvent realtimeSession
                        | TranscriptFinalized _ -> ()
                        | OracleResponseReady(_, Some candidate) ->
                            log mailbox $"Oracle final response: {Text.normalizeWhitespace candidate.answer}"
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

                if not parms.openAiDataSharingAcknowledged then
                    return Error(UnauthorizedAccessException "OpenAI data notice acknowledgement is required." :> exn)
                else
                    match Text.notEmpty apiKey with
                    | None -> return Error(InvalidOperationException "OpenAI API key is required." :> exn)
                    | Some apiKey ->
                        let settings = RuntimeSettings.snapshot parms.runtimeSettings
                        let! hasPermission = Audio.haveRecordPermission ()

                        if not hasPermission then
                            return Error(UnauthorizedAccessException "Microphone permission was not granted." :> exn)
                        else
                            applyAudioRoute settings
                            let conn = Connection.createWithConfig (webRtcClientConfig settings)
                            let startupMicrophoneGate = startupMicrophoneGateEnabled settings

                            if startupMicrophoneGate then
                                trySetMicrophoneEnabled parms.mailbox conn false "iOS speaker startup guard"

                            let cancellation = new CancellationTokenSource()
                            let serverEvents = Channel.CreateUnbounded<JsonElement>()
                            let clientEvents = Channel.CreateUnbounded<JsonElement>()
                            let firstServerEvent = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                            let voiceConnection =
                                { VoiceConnection.receiver = serverEvents.Reader
                                  sender = clientEvents.Writer }

                            let! session =
                                parms.orchestration.CreateSessionAsync(
                                    parms.context,
                                    voiceConnection,
                                    cancellation.Token
                                )
                                |> Async.AwaitTask

                            let stateSubscription =
                                conn.WebRtcClient.StateChanged.Subscribe(fun state ->
                                    if state.IsConnected then
                                        applyAudioRoute settings

                                    parms.mailbox.Writer.TryWrite(WebRTC_StateChanged(parms.connectionId, state))
                                    |> ignore

                                    notifyHost session cancellation.Token (RealtimeStateChanged(realtimeState state)))

                            let conn =
                                { conn with
                                    Disposables = stateSubscription :: conn.Disposables }

                            startClientPump parms.mailbox conn clientEvents cancellation.Token
                            startServerPump
                                parms.mailbox
                                conn
                                serverEvents
                                firstServerEvent
                                startupMicrophoneGate
                                cancellation.Token

                            startHostPump
                                parms.mailbox
                                session
                                cancellation.Token
                                apiKey
                                parms.connectionId
                                conn
                                firstServerEvent.Task

                            do! session.StartAsync cancellation.Token |> Async.AwaitTask

                            return
                                Ok
                                    { id = parms.connectionId
                                      session = session
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
            let mutable firstError: exn option = None

            let captureError ex label =
                Log.exn (ex, label)

                if firstError.IsNone then
                    firstError <- Some ex

            let tryStep label action =
                async {
                    try
                        do! action
                    with ex ->
                        captureError ex label
                }

            try
                bundle.cancellation.Cancel()
            with ex ->
                captureError ex "Connect.stop cancel"

            bundle.serverEvents.Writer.TryComplete() |> ignore
            bundle.clientEvents.Writer.TryComplete() |> ignore

            do! tryStep "Connect.stop session stop" (bundle.session.StopAsync CancellationToken.None |> Async.AwaitTask)

            do! tryStep "Connect.stop session dispose" (bundle.session.DisposeAsync().AsTask() |> Async.AwaitTask)

            try
                Connection.close bundle.connection
            with ex ->
                captureError ex "Connect.stop connection close"

            try
                bundle.cancellation.Dispose()
            with ex ->
                captureError ex "Connect.stop cancellation dispose"

            do! Async.Sleep 300

            match firstError with
            | None -> return Ok()
            | Some ex -> return Error ex
        }
