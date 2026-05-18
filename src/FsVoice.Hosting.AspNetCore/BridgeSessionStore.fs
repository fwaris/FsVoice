namespace FsVoice.Hosting.AspNetCore

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

type BridgeSessionStore() =
    let sessions = ConcurrentDictionary<string, BridgeSession>()

    member _.CreateAsync(options: BridgeSessionOptions, cancellationToken: CancellationToken) =
        task {
            let key = BridgeSessionId.value options.sessionId
            let session = new BridgeSession(options)

            if sessions.TryAdd(key, session) then
                do! session.StartAsync cancellationToken
                return session
            else
                return invalidOp $"Bridge session already exists: {key}"
        }

    member _.TryGet(sessionId: BridgeSessionId) =
        match sessions.TryGetValue(BridgeSessionId.value sessionId) with
        | true, session -> Some session
        | false, _ -> None

    member _.RemoveAsync(sessionId: BridgeSessionId) =
        task {
            match sessions.TryRemove(BridgeSessionId.value sessionId) with
            | true, session -> do! (session :> IAsyncDisposable).DisposeAsync().AsTask()
            | false, _ -> ()
        }

    member _.Count = sessions.Count
