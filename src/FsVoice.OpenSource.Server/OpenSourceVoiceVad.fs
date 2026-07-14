namespace FsVoice.OpenSource.Server

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open FsVoice.OpenSource

type OpenSourceVoiceTurnCoordinator
    (
        agent: IVoiceAgentRuntime,
        vad: IVadRuntime,
        options: OpenSourceVoiceOptions,
        session: VoiceAgentSessionInfo,
        emitVadEvent: VadEndpointEvent -> unit,
        emitAgentEvent: CancellationToken -> VoiceAgentStreamingEvent -> Task,
        reportError: exn -> unit,
        logger: ILogger
    ) =
    let endpoint =
        VoiceActivityEndpoint(vad.CreateSession(), options.Vad, agent.MaxTurnAudioSamples24k)

    let endpointLock = obj ()
    let stateLock = obj ()
    let turnGate = new SemaphoreSlim(1, 1)
    let mutable generation = 0L
    let mutable agentBusy = false
    let mutable activeTurnCancellation: CancellationTokenSource option = None
    let mutable disposed = false

    let cancelActiveTurnUnsafe () =
        activeTurnCancellation
        |> Option.iter (fun cancellation ->
            try
                cancellation.Cancel()
            with ex ->
                logger.LogDebug(ex, "Could not cancel the active voice turn for session {SessionId}.", session.Id))

    let runTurn turnGeneration (samples24k: float32 array) =
        task {
            do! turnGate.WaitAsync()

            try
                let turnCancellation = new CancellationTokenSource()

                let shouldRun =
                    lock stateLock (fun () ->
                        if disposed || turnGeneration <> generation then
                            false
                        else
                            activeTurnCancellation <- Some turnCancellation
                            true)

                if shouldRun then
                    try
                        try
                            let! _ =
                                agent.RunTurnAsync(
                                    { SessionId = session.Id
                                      UserAudio24k = samples24k
                                      RequestId = None },
                                    emitAgentEvent turnCancellation.Token,
                                    turnCancellation.Token
                                )

                            ()
                        with
                        | :? OperationCanceledException -> ()
                        | ex -> reportError ex
                    finally
                        lock stateLock (fun () ->
                            if
                                activeTurnCancellation
                                |> Option.exists (fun current -> obj.ReferenceEquals(current, turnCancellation))
                            then
                                activeTurnCancellation <- None

                            if turnGeneration = generation then
                                agentBusy <- false)

                turnCancellation.Dispose()
            finally
                turnGate.Release() |> ignore
        }

    let handleEvent event =
        match event with
        | SpeechStarted ->
            lock stateLock (fun () ->
                generation <- generation + 1L

                if options.Vad.AllowBargeIn then
                    cancelActiveTurnUnsafe ())

            emitVadEvent event
        | SpeechStopped(samples24k, _, _) ->
            let turnGeneration =
                lock stateLock (fun () ->
                    agentBusy <- true
                    generation)

            emitVadEvent event
            Task.Run(fun () -> runTurn turnGeneration samples24k :> Task) |> ignore

    member _.Append24k(samples24k: float32 array) =
        let suppressed =
            lock stateLock (fun () -> disposed || (agentBusy && not options.Vad.AllowBargeIn))

        if suppressed then
            lock endpointLock endpoint.Reset
        elif samples24k.Length > 0 then
            let events = lock endpointLock (fun () -> endpoint.Append samples24k)
            events |> Array.iter handleEvent

    member _.Cancel() =
        lock stateLock (fun () ->
            generation <- generation + 1L
            agentBusy <- false
            cancelActiveTurnUnsafe ())

        lock endpointLock endpoint.Reset

    member _.IsAgentBusy = lock stateLock (fun () -> agentBusy)

    interface IDisposable with
        member this.Dispose() =
            lock stateLock (fun () ->
                disposed <- true
                generation <- generation + 1L
                agentBusy <- false
                cancelActiveTurnUnsafe ())

            lock endpointLock endpoint.Reset
