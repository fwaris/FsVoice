namespace FsVoice.Hosting.AspNetCore

open System
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open SIPSorcery.Net
open SIPSorceryMedia.Abstractions

type OpenAiRealtimeWebRtcSession
    (
        sessionId: string,
        restClient: IOpenAiRealtimeRestClient,
        connectionGate: IOpenAiWebRtcConnectionGate,
        logger: ILogger<OpenAiRealtimeWebRtcSession>
    ) =
    let mutable peerConnection: RTCPeerConnection = null
    let mutable dataChannel: RTCDataChannel = null
    let mutable requestedCodec: SipAudioCodec option = None
    let mutable negotiatedCodec: SipAudioCodec option = None
    let mutable isDisposed = 0
    let mutable startupComplete = 0
    let received = Event<JsonElement>()
    let connected = Event<unit>()
    let closed = Event<exn option>()

    let newSignal () =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable iceGatheringCompleted = newSignal ()
    let mutable dataChannelConnected = newSignal ()
    let connectionStopwatch = Stopwatch()
    let phaseSync = obj ()
    let mutable currentAttempt = 0
    let mutable currentFailureStage: string option = None

    let setCurrentStage stage =
        lock phaseSync (fun () -> currentFailureStage <- Some stage)

    let getCurrentStage () =
        lock phaseSync (fun () -> currentFailureStage)

    let runConnectStage stage (operation: CancellationToken -> Task<'T>) cancellationToken =
        task {
            setCurrentStage stage

            try
                return! operation cancellationToken
            with ex ->
                setCurrentStage stage
                return raise ex
        }

    let tryGetLocalOfferSdp () =
        match peerConnection with
        | null -> None
        | pc when isNull pc.localDescription -> None
        | pc ->
            let sdp = pc.localDescription.sdp.ToString()

            if String.IsNullOrWhiteSpace sdp then None else Some sdp

    let hasLocalCandidates () =
        tryGetLocalOfferSdp ()
        |> Option.exists (fun sdp -> sdp.Contains("a=candidate:") || sdp.Contains("a=end-of-candidates"))

    let waitForIceGatheringAsync (timeoutMs: int) (cancellationToken: CancellationToken) =
        task {
            if hasLocalCandidates () then
                ()
            else
                use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                timeout.CancelAfter(timeoutMs)

                try
                    do! iceGatheringCompleted.Task.WaitAsync(timeout.Token)
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    raise (OperationCanceledException(cancellationToken))
                | :? OperationCanceledException -> ()
        }

    let triggerClosed reason =
        if Interlocked.CompareExchange(&isDisposed, 0, 0) = 0 then
            closed.Trigger reason

    let triggerConnected attempt =
        connectionStopwatch.Stop()
        let elapsedMs = connectionStopwatch.ElapsedMilliseconds

        if attempt = currentAttempt then
            logger.LogInformation(
                "OpenAI realtime data channel connected for SIP session {SessionId} in {ConnectionLatencyMs} ms.",
                sessionId,
                elapsedMs
            )

            connected.Trigger()
            dataChannelConnected.TrySetResult() |> ignore

    let onDataChannelMessage (_: RTCDataChannel) (_: DataChannelPayloadProtocols) (bytes: byte array) =
        if Interlocked.CompareExchange(&isDisposed, 0, 0) = 0 then
            try
                let text = Encoding.UTF8.GetString bytes
                use document = JsonDocument.Parse text
                received.Trigger(document.RootElement.Clone())
            with ex ->
                logger.LogWarning(
                    ex,
                    "OpenAI realtime data channel message was not valid JSON for SIP session {SessionId}.",
                    sessionId
                )

    let addAudio (pc: RTCPeerConnection) codec =
        requestedCodec <- Some codec
        pc.addTrack (SipsorceryCodecs.toMediaTrack codec) |> ignore

        pc.add_OnAudioFormatsNegotiated (fun formats ->
            let negotiated =
                formats |> Seq.tryHead |> Option.bind SipsorceryCodecs.tryFromAudioFormat

            negotiatedCodec <- negotiated

            match requestedCodec, negotiated with
            | Some requested, Some actual when requested <> actual ->
                triggerClosed (
                    Some(
                        OpenAiCodecRejectedException(
                            $"OpenAI realtime session negotiated {SipAudioCodec.value actual} instead of requested strict codec {SipAudioCodec.value requested}."
                        )
                    )
                )
            | Some _, Some actual ->
                logger.LogInformation(
                    "OpenAI realtime audio negotiated {Codec} for SIP session {SessionId}.",
                    SipAudioCodec.value actual,
                    sessionId
                )
            | _ -> ())

    let createPeerConnection attempt codec =
        task {
            let pc = new RTCPeerConnection(OpenAiRtcConfiguration.create logger sessionId)
            peerConnection <- pc
            addAudio pc codec

            let! dc = pc.createDataChannel ("oai-events")
            dataChannel <- dc
            dc.add_onmessage (OnDataChannelMessageDelegate(onDataChannelMessage))

            dc.add_onopen (
                Action(fun () ->
                    logger.LogInformation(
                        "OpenAI realtime data channel opened for SIP session {SessionId}.",
                        sessionId
                    )

                    triggerConnected attempt)
            )

            dc.add_onclose (
                Action(fun () ->
                    logger.LogInformation(
                        "OpenAI realtime data channel closed for SIP session {SessionId}.",
                        sessionId
                    ))
            )

            pc.add_onicegatheringstatechange (
                Action<_>(fun state ->
                    logger.LogInformation(
                        "OpenAI realtime ICE gathering state for SIP session {SessionId}: {State}.",
                        sessionId,
                        state
                    )

                    if attempt = currentAttempt && state = RTCIceGatheringState.complete then
                        iceGatheringCompleted.TrySetResult() |> ignore)
            )

            pc.add_oniceconnectionstatechange (
                Action<_>(fun state ->
                    logger.LogInformation(
                        "OpenAI realtime ICE state for SIP session {SessionId}: {State}.",
                        sessionId,
                        state
                    ))
            )

            pc.add_onconnectionstatechange (
                Action<_>(fun state ->
                    logger.LogInformation(
                        "OpenAI realtime peer connection state for SIP session {SessionId}: {State}.",
                        sessionId,
                        state
                    )

                    if
                        attempt = currentAttempt
                        && Interlocked.CompareExchange(&startupComplete, 0, 0) = 1
                    then
                        match state with
                        | RTCPeerConnectionState.failed ->
                            triggerClosed (Some(InvalidOperationException "OpenAI realtime peer connection failed."))
                        | RTCPeerConnectionState.closed
                        | RTCPeerConnectionState.disconnected ->
                            triggerClosed (Some(InvalidOperationException "OpenAI realtime peer connection closed."))
                        | _ -> ())
            )

            return pc
        }

    member _.Received = received.Publish
    member _.Connected = connected.Publish
    member _.Closed = closed.Publish

    member _.StartAsync(session: JsonElement, codec: SipAudioCodec, cancellationToken: CancellationToken) =
        task {
            if Interlocked.CompareExchange(&isDisposed, 0, 0) <> 0 then
                invalidOp "The OpenAI realtime WebRTC session has already been disposed."

            Interlocked.Exchange(&startupComplete, 0) |> ignore
            connectionStopwatch.Restart()

            let cleanupAttempt () =
                currentAttempt <- 0

                if dataChannel <> null then
                    dataChannel.close ()
                    dataChannel <- null

                if peerConnection <> null then
                    peerConnection.Close("realtime session setup retry")
                    peerConnection.Dispose()
                    peerConnection <- null

            let connectAttempt (context: OpenAiWebRtcConnectAttemptContext) =
                task {
                    iceGatheringCompleted <- newSignal ()
                    dataChannelConnected <- newSignal ()
                    currentAttempt <- context.attempt
                    let mutable completed = false

                    try
                        let! pc =
                            runConnectStage
                                "offer_creation"
                                (fun _ ->
                                    task {
                                        let! pc = createPeerConnection context.attempt codec
                                        let offer = pc.createOffer ()
                                        do! pc.setLocalDescription offer
                                        return pc
                                    })
                                context.cancellationToken

                        do!
                            runConnectStage
                                "ice_gathering"
                                (fun token -> waitForIceGatheringAsync 4_000 token)
                                context.cancellationToken

                        let offerSdp =
                            tryGetLocalOfferSdp ()
                            |> Option.defaultWith (fun () ->
                                invalidOp "The OpenAI realtime WebRTC offer SDP was empty.")

                        let! clientSecret =
                            runConnectStage
                                "client_secret"
                                (fun token -> restClient.CreateClientSecretAsync(session, token))
                                context.cancellationToken

                        let! answerSdp =
                            runConnectStage
                                "sdp_answer"
                                (fun token ->
                                    task {
                                        try
                                            return! restClient.GetSdpAnswerAsync(clientSecret.value, offerSdp, token)
                                        with ex ->
                                            return
                                                raise (
                                                    OpenAiCodecRejectedException(
                                                        $"OpenAI rejected the realtime SDP offer for strict codec {SipAudioCodec.value codec}: {ex.Message}"
                                                    )
                                                )
                                    })
                                context.cancellationToken

                        if not (SdpCodec.answerContainsCodec codec answerSdp) then
                            setCurrentStage "sdp_answer"

                            raise (
                                OpenAiCodecRejectedException(
                                    $"OpenAI realtime answer did not include requested strict pass-through codec {SipAudioCodec.value codec}."
                                )
                            )

                        do!
                            runConnectStage
                                "remote_description"
                                (fun _ ->
                                    task {
                                        let result =
                                            pc.setRemoteDescription (
                                                RTCSessionDescriptionInit(
                                                    sdp = answerSdp,
                                                    ``type`` = RTCSdpType.answer
                                                )
                                            )

                                        if result <> SetDescriptionResultEnum.OK then
                                            raise (
                                                InvalidOperationException(
                                                    $"Could not apply OpenAI realtime answer SDP: {result}."
                                                )
                                            )
                                    })
                                context.cancellationToken

                        do!
                            runConnectStage
                                "data_channel_open"
                                (fun token -> dataChannelConnected.Task.WaitAsync(token))
                                context.cancellationToken

                        completed <- true
                        return pc
                    finally
                        if not completed then
                            cleanupAttempt ()
                }

            try
                let! result =
                    connectionGate.RunWithRetriesAsync(
                        sessionId,
                        "realtime",
                        connectAttempt,
                        getCurrentStage,
                        cancellationToken
                    )

                Interlocked.Exchange(&startupComplete, 1) |> ignore
                ignore result.value
            with OpenAiWebRtcConnectFailedException(_, _, _, cause) ->
                return raise cause
        }

    member _.SendClientEvent(event: JsonElement) =
        match dataChannel with
        | null -> invalidOp "The OpenAI realtime data channel is not available."
        | channel when channel.readyState = RTCDataChannelState.``open`` ->
            event.GetRawText() |> Encoding.UTF8.GetBytes |> channel.send
        | _ -> invalidOp "The OpenAI realtime data channel is not open."

    member _.PipeFromRtpSession
        (rtpSession: RTPSession, codec: SipAudioCodec, mediaQueueFrames: int, onDrop: int64 -> unit)
        =
        let audioFormat = SipsorceryCodecs.toAudioFormat codec

        let pipe =
            new RealtimeMediaPipe(
                $"{sessionId}-sip-to-openai",
                mediaQueueFrames,
                (fun frame ->
                    match peerConnection with
                    | null -> ()
                    | pc when pc.connectionState = RTCPeerConnectionState.connected ->
                        pc.SendAudio(frame.durationRtpUnits, frame.payload)
                    | _ -> ()),
                logger,
                onDrop
            )

        rtpSession.add_OnAudioFrameReceived (
            Action<EncodedAudioFrame>(fun frame ->
                match peerConnection with
                | null -> ()
                | pc when pc.connectionState = RTCPeerConnectionState.connected ->
                    pipe.TryEnqueue(
                        RtpTimestampExtensions.ToRtpUnits(int frame.DurationMilliSeconds, audioFormat.RtpClockRate),
                        frame.EncodedAudio
                    )
                    |> ignore
                | _ -> ())
        )

        pipe

    member _.PipeToRtpSession
        (
            rtpSession: RTPSession,
            codec: SipAudioCodec,
            mediaQueueFrames: int,
            onDrop: int64 -> unit,
            onSent: unit -> unit
        ) =
        let audioFormat = SipsorceryCodecs.toAudioFormat codec

        match peerConnection with
        | null -> null
        | pc ->
            let pipe =
                new RealtimeMediaPipe(
                    $"{sessionId}-openai-to-sip",
                    mediaQueueFrames,
                    (fun frame ->
                        rtpSession.SendAudio(frame.durationRtpUnits, frame.payload)
                        onSent ()),
                    logger,
                    onDrop
                )

            pc.add_OnAudioFrameReceived (
                Action<EncodedAudioFrame>(fun frame ->
                    pipe.TryEnqueue(
                        RtpTimestampExtensions.ToRtpUnits(int frame.DurationMilliSeconds, audioFormat.RtpClockRate),
                        frame.EncodedAudio
                    )
                    |> ignore)
            )

            pipe

    member _.Dispose() =
        if Interlocked.Exchange(&isDisposed, 1) = 0 then
            if dataChannel <> null then
                dataChannel.close ()
                dataChannel <- null

            if peerConnection <> null then
                peerConnection.Close("realtime SIP session closed")
                peerConnection.Dispose()
                peerConnection <- null

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    interface IOpenAiRealtimeWebRtcSession with
        member this.Received = this.Received
        member this.Connected = this.Connected
        member this.Closed = this.Closed

        member this.StartAsync(session, codec, cancellationToken) =
            this.StartAsync(session, codec, cancellationToken)

        member this.SendClientEvent event = this.SendClientEvent event

        member this.PipeFromRtpSession(rtpSession, codec, mediaQueueFrames, onDrop) =
            this.PipeFromRtpSession(rtpSession, codec, mediaQueueFrames, onDrop)

        member this.PipeToRtpSession(rtpSession, codec, mediaQueueFrames, onDrop, onSent) =
            this.PipeToRtpSession(rtpSession, codec, mediaQueueFrames, onDrop, onSent)

type OpenAiRealtimeWebRtcSessionFactory
    (restClient: IOpenAiRealtimeRestClient, connectionGate: IOpenAiWebRtcConnectionGate, loggerFactory: ILoggerFactory)
    =
    interface IOpenAiRealtimeWebRtcSessionFactory with
        member _.Create sessionId =
            new OpenAiRealtimeWebRtcSession(
                sessionId,
                restClient,
                connectionGate,
                loggerFactory.CreateLogger<OpenAiRealtimeWebRtcSession>()
            )
            :> IOpenAiRealtimeWebRtcSession
