namespace FsVoice.Core

open System
open FsVoice

type VoiceEventBus() =
    let gate = obj ()
    let mutable subscribers: (VoiceEvent -> unit) list = []
    let mutable events: VoiceEvent list = []

    member _.Events = lock gate (fun () -> events |> List.rev)

    member _.Subscribe(handler: VoiceEvent -> unit) =
        lock gate (fun () -> subscribers <- handler :: subscribers)

        { new IDisposable with
            member _.Dispose() =
                lock gate (fun () ->
                    subscribers <-
                        subscribers
                        |> List.filter (fun existing -> not (Object.ReferenceEquals(existing, handler)))) }

    interface IVoiceEventPublisher with
        member _.Publish event =
            let handlers =
                lock gate (fun () ->
                    events <- event :: events
                    subscribers |> List.rev)

            handlers |> List.iter (fun handler -> handler event)

module VoiceEvents =
    let create name sessionId correlationId payload =
        { name = name
          sessionId = sessionId
          correlationId = correlationId
          payload = payload
          createdAt = DateTimeOffset.UtcNow }
