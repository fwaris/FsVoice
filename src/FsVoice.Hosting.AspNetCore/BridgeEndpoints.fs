namespace FsVoice.Hosting.AspNetCore

open System
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing

type BridgeSessionFactory = BridgeSessionId -> BridgeSessionOptions

type BridgeStartResponse = { sessionId: string }

type BridgeClientEventDto =
    { eventId: string
      kind: string
      eventType: string
      payload: JsonElement option }

module BridgeClientEventDto =
    let toDomain (dto: BridgeClientEventDto) =
        let kind =
            match dto.kind.Trim().ToLowerInvariant() with
            | "browser" -> BrowserEvent
            | "webrtc" -> WebRtcSignal
            | "realtime" -> RealtimeServerEvent
            | "close" -> Close
            | value -> invalidArg (nameof dto.kind) $"Unsupported bridge client event kind: {value}"

        { eventId =
            if String.IsNullOrWhiteSpace dto.eventId then
                Guid.NewGuid().ToString("N")
            else
                dto.eventId
          kind = kind
          eventType = dto.eventType
          payload = dto.payload
          receivedAt = DateTimeOffset.UtcNow }

module BridgeEndpoints =
    let map
        (prefix: string)
        (store: BridgeSessionStore)
        (createOptions: BridgeSessionFactory)
        (app: IEndpointRouteBuilder)
        =
        app.MapPost(
            $"{prefix}/sessions",
            Func<CancellationToken, Tasks.Task<IResult>>(fun cancellationToken ->
                task {
                    let sessionId = BridgeSessionId.newId ()
                    let! _session = store.CreateAsync(createOptions sessionId, cancellationToken)
                    return Results.Ok({ sessionId = BridgeSessionId.value sessionId })
                })
        )
        |> ignore

        app.MapPost(
            $"{prefix}/sessions/{{sessionId}}/events",
            Func<string, BridgeClientEventDto, CancellationToken, Tasks.Task<IResult>>
                (fun sessionId dto cancellationToken ->
                    task {
                        match store.TryGet(BridgeSessionId.create sessionId) with
                        | None -> return Results.NotFound()
                        | Some session ->
                            do! session.AcceptClientEventAsync(BridgeClientEventDto.toDomain dto, cancellationToken)
                            return Results.Accepted()
                    })
        )
        |> ignore

        app.MapGet(
            $"{prefix}/sessions/{{sessionId}}/events",
            Func<string, IResult>(fun sessionId ->
                match store.TryGet(BridgeSessionId.create sessionId) with
                | None -> Results.NotFound()
                | Some session -> Results.Ok(session.SnapshotEvents()))
        )
        |> ignore

        app.MapDelete(
            $"{prefix}/sessions/{{sessionId}}",
            Func<string, Tasks.Task<IResult>>(fun sessionId ->
                task {
                    do! store.RemoveAsync(BridgeSessionId.create sessionId)
                    return Results.NoContent()
                })
        )
        |> ignore

        app
