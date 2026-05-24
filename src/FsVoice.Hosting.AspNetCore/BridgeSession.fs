namespace FsVoice.Hosting.AspNetCore

open System
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FsVoice.Platform

type BridgeSession<'ToHost, 'FromHost>(options: BridgeSessionOptions<'ToHost, 'FromHost>) as this =
    let inbound = Channel.CreateUnbounded<JsonElement>()
    let outbound = Channel.CreateUnbounded<JsonElement>()
    let connection = VoiceConnection.create inbound.Reader outbound.Writer
    let eventLog = ResizeArray<BridgeServerEvent>()
    let eventSignal = Channel.CreateUnbounded<BridgeServerEvent>()
    let cts = new CancellationTokenSource()
    let mutable session: IVoiceSession<'ToHost, 'FromHost> option = None
    let mutable started = false

    let appendServerEvent event =
        lock eventLog (fun () -> eventLog.Add event)
        eventSignal.Writer.TryWrite event |> ignore

    let linkedToken (cancellationToken: CancellationToken) =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token)

    let startVoiceOutputPump (cancellationToken: CancellationToken) =
        task {
            try
                while not cancellationToken.IsCancellationRequested do
                    let! canRead = outbound.Reader.WaitToReadAsync(cancellationToken).AsTask()

                    if canRead then
                        let mutable message = Unchecked.defaultof<JsonElement>

                        while outbound.Reader.TryRead(&message) do
                            appendServerEvent (BridgeEvents.fromVoiceEvent message)
                    else
                        return ()
            with :? OperationCanceledException ->
                ()
        }
        :> Task

    let startHostOutputPump (activeSession: IVoiceSession<'ToHost, 'FromHost>) (cancellationToken: CancellationToken) =
        task {
            match options.hostMessageCodec with
            | None -> ()
            | Some codec ->
                try
                    while not cancellationToken.IsCancellationRequested do
                        let! canRead = activeSession.ToHost.WaitToReadAsync(cancellationToken).AsTask()

                        if canRead then
                            let mutable message = Unchecked.defaultof<'ToHost>

                            while activeSession.ToHost.TryRead(&message) do
                                appendServerEvent (BridgeEvents.fromHostMessage (codec.encodeToHost message))
                        else
                            return ()
                with :? OperationCanceledException ->
                    ()
        }
        :> Task

    member _.SessionId = options.sessionId
    member _.Connection = connection

    member _.SnapshotEvents() =
        lock eventLog (fun () -> eventLog |> Seq.toList)

    member _.StartAsync(cancellationToken: CancellationToken) =
        task {
            if not started then
                started <- true

                use linked = linkedToken cancellationToken

                let! activeSession = options.orchestration.CreateSessionAsync(options.context, connection, linked.Token)

                session <- Some activeSession

                startVoiceOutputPump cts.Token |> ignore
                startHostOutputPump activeSession cts.Token |> ignore

                do! activeSession.StartAsync linked.Token
        }

    member _.AcceptClientEventAsync(event: BridgeClientEvent, cancellationToken: CancellationToken) =
        task {
            match event.kind with
            | BridgeClientEventKind.Close -> do! (this :> IAsyncDisposable).DisposeAsync().AsTask()
            | BridgeClientEventKind.HostMessage ->
                match session, options.hostMessageCodec, event.payload with
                | Some activeSession, Some codec, Some payload ->
                    match codec.decodeFromHost payload with
                    | Ok message -> do! activeSession.SendFromHostAsync(message, cancellationToken)
                    | Error error -> invalidArg (nameof event.payload) error
                | Some _, None, _ -> invalidOp "Bridge session does not have a host message codec."
                | None, _, _ -> invalidOp "Bridge session has not been started."
                | _, _, None -> invalidArg (nameof event.payload) "Host message events require a payload."
            | BridgeClientEventKind.BrowserEvent
            | BridgeClientEventKind.WebRtcSignal
            | BridgeClientEventKind.RealtimeServerEvent ->
                do! inbound.Writer.WriteAsync(BridgeEvents.rawClientPayload event, cancellationToken).AsTask()
        }

    member _.WaitForServerEventAsync(cancellationToken: CancellationToken) =
        task {
            let! canRead = eventSignal.Reader.WaitToReadAsync(cancellationToken).AsTask()

            if canRead then
                let mutable event = Unchecked.defaultof<BridgeServerEvent>

                if eventSignal.Reader.TryRead(&event) then
                    return Some event
                else
                    return None
            else
                return None
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            task {
                if not cts.IsCancellationRequested then
                    cts.Cancel()
                    inbound.Writer.TryComplete() |> ignore
                    outbound.Writer.TryComplete() |> ignore

                    match session with
                    | Some activeSession ->
                        do! activeSession.StopAsync CancellationToken.None
                        do! activeSession.DisposeAsync().AsTask()
                    | None -> ()

                    appendServerEvent (BridgeEvents.closed options.sessionId)
                    eventSignal.Writer.TryComplete() |> ignore
                    cts.Dispose()
            }
            |> ValueTask
