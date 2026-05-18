namespace FsVoice.Hosting.AspNetCore

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FsVoice
open FsVoice.Core

type BridgeTransport() =
    let inbound = Channel.CreateUnbounded<VoiceServerEvent>()
    let outbound = Channel.CreateUnbounded<VoiceClientEvent>()
    let mutable closed = false

    member _.TryWriteServerEvent(event: VoiceServerEvent) =
        if closed then false else inbound.Writer.TryWrite event

    member _.TryReadClientEventAsync(cancellationToken: CancellationToken) =
        task {
            let! canRead = outbound.Reader.WaitToReadAsync(cancellationToken).AsTask()

            if canRead then
                let mutable event = Unchecked.defaultof<VoiceClientEvent>

                if outbound.Reader.TryRead(&event) then
                    return Some event
                else
                    return None
            else
                return None
        }

    member _.Close() =
        closed <- true
        inbound.Writer.TryComplete() |> ignore
        outbound.Writer.TryComplete() |> ignore

    interface IVoiceTransportAdapter with
        member _.SendAsync(event, cancellationToken) =
            outbound.Writer.WriteAsync(event, cancellationToken).AsTask()

        member _.ReceiveAsync(cancellationToken) =
            task {
                let! canRead = inbound.Reader.WaitToReadAsync(cancellationToken).AsTask()

                if canRead then
                    let mutable event = Unchecked.defaultof<VoiceServerEvent>

                    if inbound.Reader.TryRead(&event) then
                        return Some event
                    else
                        return None
                else
                    return None
            }

        member this.DisposeAsync() =
            this.Close()
            ValueTask()

type BridgeSession(options: BridgeSessionOptions) as this =
    let transport = new BridgeTransport()
    let eventLog = ResizeArray<BridgeServerEvent>()
    let eventSignal = Channel.CreateUnbounded<BridgeServerEvent>()
    let cts = new CancellationTokenSource()

    let runtimeOptions =
        options.runtimeOptions
        |> Option.defaultValue
            { VoiceRuntimeOptions.defaults with
                sessionId = BridgeSessionId.value options.sessionId }

    let engine =
        VoiceRuntimeEngine(options.plugin, options.hostContext, transport, runtimeOptions)

    let appendServerEvent event =
        lock eventLog (fun () -> eventLog.Add event)
        eventSignal.Writer.TryWrite event |> ignore

    let runtimeSubscription =
        engine.Subscribe(BridgeEvents.fromRuntimeEvent >> appendServerEvent)

    member _.SessionId = options.sessionId
    member _.Engine = engine

    member _.SnapshotEvents() =
        lock eventLog (fun () -> eventLog |> Seq.toList)

    member _.StartAsync(cancellationToken: CancellationToken) =
        task {
            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token)

            do! engine.StartAsync linked.Token
        }

    member _.AcceptClientEventAsync(event: BridgeClientEvent, cancellationToken: CancellationToken) =
        task {
            match event.kind with
            | Close -> do! (this :> IAsyncDisposable).DisposeAsync().AsTask()
            | BrowserEvent ->
                appendServerEvent
                    { eventId = event.eventId
                      kind = RuntimeEvent
                      eventType = $"browser.{event.eventType}"
                      payload = event.payload
                      createdAt = DateTimeOffset.UtcNow }
            | WebRtcSignal ->
                appendServerEvent
                    { eventId = event.eventId
                      kind = RuntimeEvent
                      eventType = $"webrtc.{event.eventType}"
                      payload = event.payload
                      createdAt = DateTimeOffset.UtcNow }
            | RealtimeServerEvent ->
                let serverEvent =
                    { eventId = event.eventId
                      eventType = event.eventType
                      payload = event.payload }

                transport.TryWriteServerEvent serverEvent |> ignore

            do! Task.CompletedTask
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

    member _.TryReadRealtimeClientEventAsync(cancellationToken: CancellationToken) =
        task {
            let! event = transport.TryReadClientEventAsync cancellationToken
            event |> Option.iter (BridgeEvents.fromRealtimeClientEvent >> appendServerEvent)
            return event
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            task {
                if not cts.IsCancellationRequested then
                    cts.Cancel()
                    do! engine.StopAsync CancellationToken.None
                    transport.Close()
                    appendServerEvent (BridgeEvents.closed options.sessionId)
                    runtimeSubscription.Dispose()
                    eventSignal.Writer.TryComplete() |> ignore
                    cts.Dispose()
            }
            |> ValueTask
