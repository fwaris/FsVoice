namespace FsVoice.Hosting.AspNetCore

open System
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FsVoice.Platform
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open SIPSorcery.Net
open SIPSorcery.SIP.App

type SipCallBridge<'ToHost, 'FromHost>
    (
        registration: SipListenerRegistration<'ToHost, 'FromHost>,
        options: IOptions<SipListenerOptions>,
        openAiFactory: IOpenAiRealtimeWebRtcSessionFactory,
        logger: ILogger<SipCallBridge<'ToHost, 'FromHost>>
    ) =
    let notifyHost
        (session: IVoiceSession<'ToHost, 'FromHost>)
        (cancellationToken: CancellationToken)
        (message: 'FromHost option)
        =
        task {
            match message with
            | None -> ()
            | Some message -> do! session.SendFromHostAsync(message, cancellationToken)
        }

    let waitForRealtimeSessionAsync
        (activeSession: IVoiceSession<'ToHost, 'FromHost>)
        (cancellationToken: CancellationToken)
        =
        task {
            let found =
                TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)

            let _hostPump =
                Task.Run(
                    Func<Task>(fun () ->
                        task {
                            try
                                let mutable keepGoing = true

                                while keepGoing && not cancellationToken.IsCancellationRequested do
                                    let! canRead = activeSession.ToHost.WaitToReadAsync(cancellationToken).AsTask()

                                    if canRead then
                                        let mutable message = Unchecked.defaultof<'ToHost>

                                        while activeSession.ToHost.TryRead(&message) do
                                            match registration.hostAdapter.tryGetRealtimeSession message with
                                            | Some session ->
                                                found.TrySetResult(session.Clone()) |> ignore
                                                keepGoing <- false
                                            | None -> ()
                                    else
                                        keepGoing <- false

                                if not found.Task.IsCompleted then
                                    found.TrySetException(
                                        InvalidOperationException
                                            "Voice orchestration completed before requesting a realtime session."
                                    )
                                    |> ignore
                            with
                            | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> ()
                            | ex -> found.TrySetException ex |> ignore
                        })
                )

            use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            timeout.CancelAfter(SipListenerOptions.realtimeRequestTimeout options.Value)

            try
                return! found.Task.WaitAsync(timeout.Token)
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException(cancellationToken))
            | :? OperationCanceledException ->
                return
                    raise (
                        TimeoutException
                            "Voice orchestration did not request a realtime session before the SIP timeout."
                    )
        }

    let startOpenAiEventPump
        (openAi: IOpenAiRealtimeWebRtcSession)
        (inbound: Channel<JsonElement>)
        (cancellationToken: CancellationToken)
        =
        openAi.Received.Add(fun event ->
            if not cancellationToken.IsCancellationRequested then
                inbound.Writer.TryWrite(event.Clone()) |> ignore)

    let startClientEventPump
        (openAi: IOpenAiRealtimeWebRtcSession)
        (outbound: Channel<JsonElement>)
        (cancellationToken: CancellationToken)
        =
        Task.Run(
            Func<Task>(fun () ->
                task {
                    try
                        let mutable keepGoing = true

                        while keepGoing && not cancellationToken.IsCancellationRequested do
                            let! canRead = outbound.Reader.WaitToReadAsync(cancellationToken).AsTask()

                            if canRead then
                                let mutable event = Unchecked.defaultof<JsonElement>

                                while outbound.Reader.TryRead(&event) do
                                    try
                                        openAi.SendClientEvent event
                                    with ex ->
                                        logger.LogWarning(ex, "Failed to send realtime client event for SIP call.")
                            else
                                keepGoing <- false
                    with :? OperationCanceledException ->
                        ()
                })
        )
        |> ignore

    let closeSipLeg reason (ua: SIPUserAgent) (rtpSession: RTPSession) =
        try
            if not (isNull ua) then
                ua.Hangup()
        with _ ->
            ()

        try
            if not (isNull rtpSession) then
                rtpSession.Close(reason)
        with _ ->
            ()

    member _.RunAsync(context: SipCallContext, rtpSession: RTPSession, ua: SIPUserAgent, cancellationToken) =
        task {
            let voiceConnection, inbound, outbound = VoiceConnection.channelPair ()
            let sessionOptions = registration.createSessionOptions context
            let mutable activeSession: IVoiceSession<'ToHost, 'FromHost> option = None
            let mutable openAi: IOpenAiRealtimeWebRtcSession option = None
            let mutable fromSipPipe: RealtimeMediaPipe option = None
            let mutable toSipPipe: RealtimeMediaPipe option = None

            let closed =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let notify state =
                match activeSession with
                | None -> Task.CompletedTask
                | Some session ->
                    notifyHost session cancellationToken (registration.hostAdapter.stateChanged state) :> Task

            let notifyFailure message =
                match activeSession with
                | None -> Task.CompletedTask
                | Some session ->
                    notifyHost session cancellationToken (registration.hostAdapter.connectionFailed message) :> Task

            try
                let! session =
                    sessionOptions.orchestration.CreateSessionAsync(
                        sessionOptions.context,
                        voiceConnection,
                        cancellationToken
                    )

                activeSession <- Some session

                do! session.StartAsync cancellationToken

                let! realtimeSession = waitForRealtimeSessionAsync session cancellationToken

                let openAiSession = openAiFactory.Create context.callId
                openAi <- Some openAiSession

                startOpenAiEventPump openAiSession inbound cancellationToken
                startClientEventPump openAiSession outbound cancellationToken

                openAiSession.Connected.Add(fun () ->
                    notify SipRealtimeConnected |> fun task -> task.ContinueWith(ignore) |> ignore)

                openAiSession.Closed.Add(fun reason ->
                    match reason with
                    | Some ex ->
                        logger.LogError(
                            ex,
                            "OpenAI realtime WebRTC session closed for SIP call {CallId}.",
                            context.callId
                        )
                    | None -> ()

                    closed.TrySetResult() |> ignore)

                do! notify SipRealtimeConnecting

                fromSipPipe <-
                    openAiSession.PipeFromRtpSession(
                        rtpSession,
                        context.negotiatedCodec,
                        SipListenerOptions.mediaQueueFrames options.Value,
                        ignore
                    )
                    |> Option.ofObj

                do! openAiSession.StartAsync(realtimeSession, context.negotiatedCodec, cancellationToken)

                toSipPipe <-
                    openAiSession.PipeToRtpSession(
                        rtpSession,
                        context.negotiatedCodec,
                        SipListenerOptions.mediaQueueFrames options.Value,
                        ignore,
                        ignore
                    )
                    |> Option.ofObj

                use cancellationRegistration =
                    cancellationToken.Register(fun () -> closed.TrySetResult() |> ignore)

                do! closed.Task
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> ()
            | OpenAiCodecRejectedException message as ex ->
                logger.LogError(
                    ex,
                    "OpenAI rejected strict codec for SIP call {CallId} using {Codec}: {Message}",
                    context.callId,
                    SipAudioCodec.value context.negotiatedCodec,
                    message
                )

                do! notifyFailure message
            | ex ->
                logger.LogError(ex, "SIP call bridge failed for call {CallId}.", context.callId)
                do! notifyFailure ex.Message

            try
                do! notify SipRealtimeDisconnected
            with _ ->
                ()

            fromSipPipe |> Option.iter _.Dispose()
            toSipPipe |> Option.iter _.Dispose()
            openAi |> Option.iter _.Dispose()

            inbound.Writer.TryComplete() |> ignore
            outbound.Writer.TryComplete() |> ignore

            match activeSession with
            | Some session ->
                try
                    do! session.StopAsync CancellationToken.None
                with ex ->
                    logger.LogWarning(ex, "Failed to stop voice orchestration for SIP call {CallId}.", context.callId)

                try
                    do! session.DisposeAsync().AsTask()
                with ex ->
                    logger.LogWarning(
                        ex,
                        "Failed to dispose voice orchestration for SIP call {CallId}.",
                        context.callId
                    )
            | None -> ()

            closeSipLeg "SIP call bridge stopped" ua rtpSession
        }
