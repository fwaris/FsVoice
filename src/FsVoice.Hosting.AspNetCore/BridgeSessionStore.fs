namespace FsVoice.Hosting.AspNetCore

open System.Collections.Concurrent
open System.Threading

type BridgeSessionStore<'ToHost, 'FromHost>() =
    let sessions = ConcurrentDictionary<string, BridgeSession<'ToHost, 'FromHost>>()

    member _.CreateAsync(options: BridgeSessionOptions<'ToHost, 'FromHost>, cancellationToken: CancellationToken) =
        task {
            let key = BridgeSessionId.value options.sessionId
            let session = new BridgeSession<'ToHost, 'FromHost>(options)

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
            | true, session -> do! (session :> System.IAsyncDisposable).DisposeAsync().AsTask()
            | false, _ -> ()
        }

    member _.Count = sessions.Count
