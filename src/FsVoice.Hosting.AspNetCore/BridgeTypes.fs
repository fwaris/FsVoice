namespace FsVoice.Hosting.AspNetCore

open System
open System.Text.Json
open FsVoice

type BridgeSessionId = private BridgeSessionId of string

module BridgeSessionId =
    let value (BridgeSessionId value) = value

    let create value =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Bridge session id must not be empty."

        BridgeSessionId(value.Trim())

    let newId () =
        Guid.NewGuid().ToString("N") |> BridgeSessionId

type BridgeClientEventKind =
    | BrowserEvent
    | WebRtcSignal
    | RealtimeServerEvent
    | Close

type BridgeClientEvent =
    { eventId: string
      kind: BridgeClientEventKind
      eventType: string
      payload: JsonElement option
      receivedAt: DateTimeOffset }

type BridgeServerEventKind =
    | RuntimeEvent
    | RealtimeClientEvent
    | SessionClosed

type BridgeServerEvent =
    { eventId: string
      kind: BridgeServerEventKind
      eventType: string
      payload: JsonElement option
      createdAt: DateTimeOffset }

type BridgeSessionOptions =
    { sessionId: BridgeSessionId
      plugin: IVoicePlugin
      hostContext: VoicePluginHostContext
      runtimeOptions: FsVoice.Core.VoiceRuntimeOptions option }

module BridgeEvents =
    let fromRuntimeEvent (event: VoiceEvent) =
        { eventId = event.correlationId |> Option.defaultValue (Guid.NewGuid().ToString("N"))
          kind = RuntimeEvent
          eventType = event.name
          payload = event.payload
          createdAt = event.createdAt }

    let fromRealtimeClientEvent (event: VoiceClientEvent) =
        { eventId = event.eventId
          kind = RealtimeClientEvent
          eventType = event.eventType
          payload = event.payload
          createdAt = DateTimeOffset.UtcNow }

    let closed sessionId =
        { eventId = Guid.NewGuid().ToString("N")
          kind = SessionClosed
          eventType = "bridge.session.closed"
          payload =
            JsonSerializer.SerializeToElement {| sessionId = BridgeSessionId.value sessionId |}
            |> Some
          createdAt = DateTimeOffset.UtcNow }
