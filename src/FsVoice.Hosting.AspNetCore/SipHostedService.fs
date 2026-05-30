namespace FsVoice.Hosting.AspNetCore

open System
open System.Collections.Concurrent
open System.Net
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open SIPSorcery.Net
open SIPSorcery.SIP
open SIPSorcery.SIP.App

type SipHostedService<'ToHost, 'FromHost>
    (
        options: IOptions<SipListenerOptions>,
        callBridge: SipCallBridge<'ToHost, 'FromHost>,
        logger: ILogger<SipHostedService<'ToHost, 'FromHost>>,
        loggerFactory: ILoggerFactory
    ) =
    let mutable sipTransport: SIPTransport = null
    let mutable requestHandler: SIPTransportRequestAsyncDelegate = null
    let activeCalls = ConcurrentDictionary<string, CancellationTokenSource>()
    let serviceCancellation = new CancellationTokenSource()

    let sendResponseAsync (request: SIPRequest) status reason =
        task {
            match sipTransport with
            | null -> ()
            | transport ->
                let response = SIPResponse.GetResponse(request, status, reason)
                let! _ = transport.SendResponseAsync(response)
                ()
        }

    let closeLeg reason (ua: SIPUserAgent) (rtpSession: RTPSession) =
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

    let negotiatedCodec (rtpSession: RTPSession) =
        if isNull rtpSession || isNull rtpSession.AudioStream then
            None
        else
            SipsorceryCodecs.tryFromSdpFormat rtpSession.AudioStream.NegotiatedFormat

    let createRtpSession (ua: SIPUserAgent) (callId: string) =
        let rtpSession = new RTPSession(false, false, false)
        let allowedCodecs = SipListenerOptions.allowedCodecs options.Value

        rtpSession.addTrack (SipsorceryCodecs.toMediaTrackMany allowedCodecs) |> ignore

        rtpSession.AcceptRtpFromAny <- true

        rtpSession.add_OnTimeout (
            Action<SDPMediaTypesEnum>(fun mediaType ->
                logger.LogWarning(
                    "RTP timeout for SIP call {CallId} media {MediaType}; hanging up.",
                    callId,
                    mediaType
                )

                closeLeg "RTP timeout" ua rtpSession)
        )

        rtpSession

    let attachUserAgentHandlers (callId: string) (ua: SIPUserAgent) (callCancellation: CancellationTokenSource) =
        ua.add_OnCallHungup (
            Action<SIPDialogue>(fun _ ->
                logger.LogInformation("SIP call {CallId} hung up.", callId)
                callCancellation.Cancel())
        )

        ua.add_ServerCallCancelled (
            SIPUASCancelDelegate(fun _ _ ->
                logger.LogInformation("SIP call {CallId} cancelled before answer.", callId)
                callCancellation.Cancel())
        )

        ua.add_ServerCallRingTimeout (
            SIPUASDelegate(fun _ ->
                logger.LogWarning("SIP call {CallId} ring timeout.", callId)
                callCancellation.Cancel()
                ua.Hangup())
        )

    member _.IsListening = not (isNull sipTransport)

    member private this.StartBridgeInBackground
        (context: SipCallContext)
        (ua: SIPUserAgent)
        (rtpSession: RTPSession)
        (callCancellation: CancellationTokenSource)
        =
        let callId = context.callId

        activeCalls.[callId] <- callCancellation

        Task.Run(
            Func<Task>(fun () ->
                task {
                    try
                        use linked =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                serviceCancellation.Token,
                                callCancellation.Token
                            )

                        do! callBridge.RunAsync(context, rtpSession, ua, linked.Token)
                    finally
                        let mutable removed = Unchecked.defaultof<CancellationTokenSource>
                        activeCalls.TryRemove(callId, &removed) |> ignore
                        callCancellation.Dispose()
                })
        )
        |> ignore

    member private this.HandleInviteAsync(remoteEndPoint: SIPEndPoint, request: SIPRequest) =
        task {
            let callId = Guid.NewGuid().ToString("N")
            let callCancellation = new CancellationTokenSource()
            let ua = new SIPUserAgent(sipTransport, null)
            attachUserAgentHandlers callId ua callCancellation

            let rtpSession = createRtpSession ua callId

            try
                let uas = ua.AcceptCall request
                let! answered = ua.Answer(uas, rtpSession, null)

                if answered && ua.IsCallActive then
                    do! rtpSession.Start()

                    match negotiatedCodec rtpSession with
                    | Some codec when SipListenerOptions.allowedCodecs options.Value |> List.contains codec ->
                        let context =
                            { callId = callId
                              sessionId = BridgeSessionId.newId ()
                              sipUri = request.URI.ToString()
                              remoteEndPoint =
                                if isNull remoteEndPoint then
                                    ""
                                else
                                    remoteEndPoint.ToString()
                              negotiatedCodec = codec }

                        logger.LogInformation(
                            "Answered SIP call {CallId} from {Remote} for {Uri} using codec {Codec}.",
                            context.callId,
                            context.remoteEndPoint,
                            context.sipUri,
                            SipAudioCodec.value codec
                        )

                        this.StartBridgeInBackground context ua rtpSession callCancellation
                    | Some codec ->
                        let ex =
                            InvalidOperationException(
                                $"SIP call negotiated unsupported codec {SipAudioCodec.value codec}; strict WebRTC pass-through requires one configured allowed codec."
                            )

                        logger.LogError(ex, "Rejecting SIP call {CallId} because codec is unsupported.", callId)
                        closeLeg "unsupported strict codec" ua rtpSession
                        callCancellation.Dispose()
                    | None ->
                        let ex =
                            InvalidOperationException(
                                "SIP call did not negotiate a strict codec supported by the OpenAI WebRTC bridge."
                            )

                        logger.LogError(ex, "Rejecting SIP call {CallId}; no strict codec was negotiated.", callId)
                        closeLeg "no strict codec negotiated" ua rtpSession
                        callCancellation.Dispose()
                else
                    closeLeg "SIP answer failed" ua rtpSession
                    callCancellation.Dispose()
            with ex ->
                logger.LogError(ex, "Exception while answering SIP call {CallId}.", callId)
                closeLeg "SIP answer exception" ua rtpSession
                callCancellation.Dispose()
        }

    member private this.HandleRequestAsync
        (localEndPoint: SIPEndPoint, remoteEndPoint: SIPEndPoint, request: SIPRequest)
        =
        task {
            try
                if
                    not (isNull request.Header.From)
                    && not (isNull request.Header.From.FromTag)
                    && not (isNull request.Header.To)
                    && not (isNull request.Header.To.ToTag)
                then
                    ()
                else
                    match request.Method with
                    | SIPMethodsEnum.INVITE ->
                        logger.LogInformation(
                            "Incoming SIP INVITE {Local} <- {Remote} {Uri}.",
                            localEndPoint,
                            remoteEndPoint,
                            request.URI
                        )

                        do! this.HandleInviteAsync(remoteEndPoint, request)
                    | SIPMethodsEnum.OPTIONS
                    | SIPMethodsEnum.REGISTER -> do! sendResponseAsync request SIPResponseStatusCodesEnum.Ok null
                    | SIPMethodsEnum.BYE ->
                        do! sendResponseAsync request SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist null
                    | SIPMethodsEnum.SUBSCRIBE ->
                        do! sendResponseAsync request SIPResponseStatusCodesEnum.MethodNotAllowed null
                    | _ -> do! sendResponseAsync request SIPResponseStatusCodesEnum.MethodNotAllowed null
            with ex ->
                logger.LogWarning(ex, "Exception handling SIP {Method} from {Remote}.", request.Method, remoteEndPoint)
        }

    interface IHostedService with
        member this.StartAsync(_cancellationToken: CancellationToken) =
            task {
                if not options.Value.Enabled then
                    logger.LogInformation("SIP listener is disabled by configuration.")
                else
                    SIPSorcery.LogFactory.Set(loggerFactory)
                    let transport = new SIPTransport()
                    sipTransport <- transport

                    requestHandler <-
                        SIPTransportRequestAsyncDelegate(fun localEndPoint remoteEndPoint request ->
                            this.HandleRequestAsync(localEndPoint, remoteEndPoint, request))

                    transport.add_SIPTransportRequestReceived requestHandler
                    transport.AddSIPChannel(new SIPUDPChannel(IPEndPoint(IPAddress.Any, options.Value.ListenUdpPort)))

                    logger.LogInformation(
                        "SIP listener started on UDP port {Port}; allowed codecs: {Codecs}.",
                        options.Value.ListenUdpPort,
                        SipListenerOptions.allowedCodecs options.Value
                        |> List.map SipAudioCodec.value
                        |> String.concat ","
                    )
            }
            :> Task

        member _.StopAsync(_cancellationToken: CancellationToken) =
            task {
                serviceCancellation.Cancel()

                for call in activeCalls.Values do
                    call.Cancel()

                match sipTransport with
                | null -> ()
                | transport ->
                    if not (isNull requestHandler) then
                        transport.remove_SIPTransportRequestReceived requestHandler

                    transport.Shutdown()
                    sipTransport <- null
                    logger.LogInformation("SIP listener stopped.")
            }
            :> Task
