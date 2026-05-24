namespace FsVoice.Hosting.AspNetCore

open System
open System.Text.Json
open FsVoice.Platform

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
    | HostMessage
    | Close

type BridgeClientEvent =
    { eventId: string
      kind: BridgeClientEventKind
      eventType: string
      payload: JsonElement option
      receivedAt: DateTimeOffset }

type BridgeServerEventKind =
    | VoiceEvent
    | HostMessage
    | SessionClosed

type BridgeServerEvent =
    { eventId: string
      kind: BridgeServerEventKind
      eventType: string
      payload: JsonElement option
      createdAt: DateTimeOffset }

type BridgeSessionOptions<'ToHost, 'FromHost> =
    { sessionId: BridgeSessionId
      orchestration: IVoiceOrchestration<'ToHost, 'FromHost>
      context: VoiceOrchestrationContext
      hostMessageCodec: HostMessageCodec<'ToHost, 'FromHost> option }

module BridgeEvents =
    let private stringProperty (name: string) (json: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if
            json.ValueKind = JsonValueKind.Object
            && json.TryGetProperty(name, &property)
            && property.ValueKind = JsonValueKind.String
        then
            property.GetString() |> Option.ofObj
        else
            None

    let private clonedPayload (payload: JsonElement option) =
        payload |> Option.map (fun json -> json.Clone())

    let rawClientPayload (event: BridgeClientEvent) =
        match event.payload with
        | Some payload -> payload.Clone()
        | None ->
            JsonSerializer.SerializeToElement(
                {| event_id = event.eventId
                   ``type`` = event.eventType |}
            )

    let fromVoiceEvent (json: JsonElement) =
        { eventId =
            json
            |> stringProperty "event_id"
            |> Option.defaultValue (Guid.NewGuid().ToString("N"))
          kind = BridgeServerEventKind.VoiceEvent
          eventType = json |> stringProperty "type" |> Option.defaultValue "voice.event"
          payload = Some(json.Clone())
          createdAt = DateTimeOffset.UtcNow }

    let fromHostMessage encoded =
        { eventId = Guid.NewGuid().ToString("N")
          kind = BridgeServerEventKind.HostMessage
          eventType = "host.message"
          payload = Some(encoded)
          createdAt = DateTimeOffset.UtcNow }

    let closed sessionId =
        { eventId = Guid.NewGuid().ToString("N")
          kind = BridgeServerEventKind.SessionClosed
          eventType = "bridge.session.closed"
          payload =
            JsonSerializer.SerializeToElement {| sessionId = BridgeSessionId.value sessionId |}
            |> Some
          createdAt = DateTimeOffset.UtcNow }
