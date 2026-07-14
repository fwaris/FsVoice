namespace FsVoice.OpenSource.Server

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open SIPSorcery.Media
open SIPSorcery.Net
open SIPSorcery.Sys
open SIPSorceryMedia.Abstractions
open FsVoice.OpenSource

type SdpPayload = { Sdp: string; Type: string }

type OpenSourceVoiceWebRtcSession
    (
        agent: IVoiceAgentRuntime,
        vad: IVadRuntime,
        options: OpenSourceVoiceOptions,
        session: VoiceAgentSessionInfo,
        webRtcOptions: WebRtcRuntimeOptions,
        logger: ILogger<OpenSourceVoiceWebRtcSession>
    ) =
    let jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

    let audioFormat = AudioCommonlyUsedFormats.OpusWebRTC
    let audioEncoder = new AudioEncoder(false, true)

    let createPeerConnection () =
        let config =
            RTCConfiguration(
                X_UseRtpFeedbackProfile = true,
                X_ICEIncludeAllInterfaceAddresses = webRtcOptions.IncludeAllInterfaceAddresses
            )

        if webRtcOptions.GatherTimeoutMs > 0 then
            config.X_GatherTimeoutMs <- webRtcOptions.GatherTimeoutMs

        if not (String.IsNullOrWhiteSpace webRtcOptions.BindAddress) then
            match IPAddress.TryParse webRtcOptions.BindAddress with
            | true, address ->
                config.X_BindAddress <- address
                logger.LogInformation("Open-source WebRTC binding RTP/ICE sockets to {BindAddress}.", address)
            | false, _ ->
                invalidArg
                    (nameof webRtcOptions.BindAddress)
                    $"Invalid OpenSourceVoice:WebRtc:BindAddress value '{webRtcOptions.BindAddress}'."

        let portRange =
            if webRtcOptions.IcePortStart = 0 && webRtcOptions.IcePortEnd = 0 then
                None
            else
                if webRtcOptions.IcePortStart <= 0 || webRtcOptions.IcePortEnd <= 0 then
                    invalidArg
                        (nameof webRtcOptions.IcePortStart)
                        "Both OpenSourceVoice:WebRtc:IcePortStart and IcePortEnd must be set when pinning WebRTC ICE ports."

                if webRtcOptions.IcePortStart > webRtcOptions.IcePortEnd then
                    invalidArg
                        (nameof webRtcOptions.IcePortStart)
                        "OpenSourceVoice:WebRtc:IcePortStart must be less than or equal to IcePortEnd."

                if webRtcOptions.IcePortStart % 2 <> 0 then
                    invalidArg
                        (nameof webRtcOptions.IcePortStart)
                        "OpenSourceVoice:WebRtc:IcePortStart must be an even UDP port."

                let range =
                    PortRange(webRtcOptions.IcePortStart, webRtcOptions.IcePortEnd, false, Nullable<int>())

                logger.LogInformation(
                    "Open-source WebRTC using UDP ICE port range {IcePortStart}-{IcePortEnd}.",
                    webRtcOptions.IcePortStart,
                    webRtcOptions.IcePortEnd
                )

                Some range

        match portRange with
        | Some range -> new RTCPeerConnection(config, 0, range, false)
        | None -> new RTCPeerConnection(config)

    let pc = createPeerConnection ()
    let syncRoot = obj ()

    let iceComplete =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable dataChannel: RTCDataChannel = null
    let mutable negotiatedAudioFormat = audioFormat
    let mutable disposed = false

    let nullableString =
        function
        | Some text -> text :> obj
        | None -> null

    let sendJson (payload: obj) =
        lock syncRoot (fun () ->
            if
                not disposed
                && dataChannel <> null
                && dataChannel.readyState = RTCDataChannelState.``open``
            then
                let json = JsonSerializer.Serialize(payload, jsonOptions)
                dataChannel.send json)

    let codecSampleRate (format: AudioFormat) =
        if format.RtpClockRate > 0 then format.RtpClockRate
        elif format.ClockRate > 0 then format.ClockRate
        else 48000

    let codecChannelCount (format: AudioFormat) =
        if format.ChannelCount > 0 then format.ChannelCount else 1

    let monoToInterleaved (channels: int) (mono: int16 array) =
        if channels <= 1 then
            mono
        else
            let interleaved = Array.zeroCreate<int16> (mono.Length * channels)

            for sampleIndex in 0 .. mono.Length - 1 do
                for channelIndex in 0 .. channels - 1 do
                    interleaved[sampleIndex * channels + channelIndex] <- mono[sampleIndex]

            interleaved

    let interleavedToMono (channels: int) (pcm: int16 array) =
        if channels <= 1 then
            pcm
        else
            let sampleCount = pcm.Length / channels

            Array.init sampleCount (fun sampleIndex ->
                let mutable total = 0

                for channelIndex in 0 .. channels - 1 do
                    total <- total + int pcm[sampleIndex * channels + channelIndex]

                int16 (total / channels))

    let sendAudioSamplesAsync (sampleRate: int) (samples: float32 array) (cancellationToken: CancellationToken) =
        task {
            if samples.Length > 0 then
                let format = negotiatedAudioFormat
                let targetRate = codecSampleRate format
                let channels = codecChannelCount format

                let pcmMono =
                    samples
                    |> AudioPcm.resampleLinear sampleRate targetRate
                    |> AudioPcm.float32ToPcm16

                let frameSamplesPerChannel = max 1 (targetRate / 50)
                let mutable offset = 0

                while offset < pcmMono.Length do
                    cancellationToken.ThrowIfCancellationRequested()
                    let length = min frameSamplesPerChannel (pcmMono.Length - offset)
                    let frameMono = Array.zeroCreate<int16> frameSamplesPerChannel
                    Array.Copy(pcmMono, offset, frameMono, 0, length)
                    let frame = monoToInterleaved channels frameMono
                    let encoded = audioEncoder.EncodeAudio(frame, format)

                    let duration =
                        uint32 (int64 frameSamplesPerChannel * int64 format.RtpClockRate / int64 targetRate)

                    pc.SendAudio(duration, encoded)
                    offset <- offset + length
                    do! Task.Delay(20, cancellationToken)
        }

    let eventPayload (event: VoiceAgentStreamingEvent) =
        match event with
        | VoiceAgentTranscription(id, requestId, turnIndex, transcript) ->
            Some(
                box
                    {| ``type`` = "agent.transcription"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       transcript = transcript |}
            )
        | VoiceAgentToolCall(id, requestId, turnIndex, call) ->
            Some(
                box
                    {| ``type`` = "agent.tool_call"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       round = call.Round
                       name = call.Name
                       arguments = call.Arguments
                       rawText = call.RawText |}
            )
        | VoiceAgentToolResult(id, requestId, turnIndex, result) ->
            Some(
                box
                    {| ``type`` = "agent.tool_result"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       round = result.Round
                       name = result.Name
                       success = result.Success
                       result = result.Result
                       error = nullableString result.Error |}
            )
        | VoiceAgentFillerText(id, requestId, turnIndex, text) ->
            Some(
                box
                    {| ``type`` = "agent.filler_text"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       text = text |}
            )
        | VoiceAgentFinalText(id, requestId, turnIndex, text) ->
            Some(
                box
                    {| ``type`` = "agent.final_text"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       text = text |}
            )
        | ResponseToFirstAnswerAudio(id, requestId, turnIndex, durationMs) ->
            Some(
                box
                    {| ``type`` = "metrics.response_to_first_answer_audio"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       durationMs = durationMs |}
            )
        | TtsSynthesisStarted(id, requestId, turnIndex, phase, text) ->
            Some(
                box
                    {| ``type`` = $"tts.{phase}.started"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       phase = phase
                       text = text |}
            )
        | TtsAudioChunk(id, requestId, turnIndex, phase, sampleRate, samples) ->
            Some(
                box
                    {| ``type`` = $"tts.{phase}.chunk"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       phase = phase
                       sampleRate = sampleRate
                       samples = samples.Length |}
            )
        | TtsSynthesisDone(id, requestId, turnIndex, result) ->
            Some(
                box
                    {| ``type`` = $"tts.{result.Phase}.done"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       phase = result.Phase
                       text = result.Text
                       outputPath = nullableString result.OutputPath
                       sampleRate = result.SampleRate
                       samples = result.Samples
                       durationMs = result.DurationMs
                       inferenceTimeMs = result.InferenceTimeMs
                       message = result.Message |}
            )
        | TtsSynthesisCanceled(id, requestId, turnIndex, phase) ->
            Some(
                box
                    {| ``type`` = $"tts.{phase}.canceled"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       phase = phase |}
            )
        | TtsUnavailable(id, requestId, turnIndex, phase, message) ->
            Some(
                box
                    {| ``type`` = "tts.unavailable"
                       id = id
                       requestId = requestId
                       turnIndex = turnIndex
                       phase = phase
                       message = message |}
            )
        | VoiceAgentDone result ->
            Some(
                box
                    {| ``type`` = "agent.done"
                       id = result.Id
                       requestId = result.RequestId
                       turnIndex = result.TurnIndex
                       transcript = result.Transcript
                       finalText = result.FinalText
                       audioUrl = nullableString result.AudioUrl
                       detailsUrl = result.DetailsUrl
                       toolCalls = result.ToolCalls
                       toolResults = result.ToolResults |}
            )
        | VoiceAgentCanceled(id, requestId) ->
            Some(
                box
                    {| ``type`` = "generation.canceled"
                       id = id
                       requestId = nullableString requestId |}
            )

    let emitFromAgent cancellationToken event =
        task {
            match eventPayload event with
            | Some payload -> sendJson payload
            | None -> ()

            match event with
            | TtsAudioChunk(_, _, _, _, sampleRate, samples) ->
                do! sendAudioSamplesAsync sampleRate samples cancellationToken
            | _ -> ()
        }
        :> Task

    let emitVadEvent event =
        match event with
        | SpeechStarted ->
            sendJson (
                box
                    {| ``type`` = "vad.speech_started"
                       id = session.Id |}
            )
        | SpeechStopped(_, durationMs, reason) ->
            sendJson (
                box
                    {| ``type`` = "vad.speech_stopped"
                       id = session.Id
                       durationMs = durationMs
                       reason =
                        match reason with
                        | Silence -> "silence"
                        | MaxDuration -> "max_duration" |}
            )

    let reportTurnError (ex: exn) =
        logger.LogError(ex, "Open-source voice turn failed for session {SessionId}.", session.Id)

        sendJson (
            box
                {| ``type`` = "error"
                   message = ex.Message |}
        )

    let coordinator =
        new OpenSourceVoiceTurnCoordinator(
            agent,
            vad,
            options,
            session,
            emitVadEvent,
            emitFromAgent,
            reportTurnError,
            logger
        )

    let handleControlMessage (message: string) =
        try
            use doc = JsonDocument.Parse message

            let eventType =
                match doc.RootElement.TryGetProperty("type") with
                | true, value -> value.GetString()
                | _ -> null

            match eventType with
            | "turn.cancel" ->
                coordinator.Cancel()

                sendJson (
                    box
                        {| ``type`` = "generation.canceled"
                           id = session.Id |}
                )
            | "turn.start"
            | "turn.end" ->
                sendJson (
                    box
                        {| ``type`` = "error"
                           message =
                            $"Control event '{eventType}' is no longer supported; stream microphone audio continuously for server VAD." |}
                )
            | _ ->
                sendJson (
                    box
                        {| ``type`` = "error"
                           message = $"Unknown control event '{eventType}'." |}
                )
        with ex ->
            sendJson (
                box
                    {| ``type`` = "error"
                       message = ex.Message |}
            )

    let configureDataChannel (channel: RTCDataChannel) =
        dataChannel <- channel

        channel.add_onopen (
            Action(fun () ->
                let status = agent.Status()
                let vadStatus = vad.Status()

                sendJson (
                    box
                        {| ``type`` = "session.ready"
                           id = session.Id
                           mode = session.Mode
                           maxTurnAudioSamples24k = agent.MaxTurnAudioSamples24k
                           gemmaReady = status.Gemma.Ready
                           gemmaMessage = status.Gemma.Message
                           sttReady = status.Stt.Ready
                           sttMessage = status.Stt.Message
                           ttsReady = status.Tts.Ready
                           ttsRuntime = status.Tts.Runtime
                           ttsVoiceCloning = status.Tts.SupportsVoiceCloning
                           ttsMessage = status.Tts.Message
                           vadReady = vadStatus.Ready
                           turnDetection = "server_vad"
                           bargeInEnabled = vadStatus.AllowBargeIn |}
                ))
        )

        channel.add_onmessage (
            OnDataChannelMessageDelegate(fun _ _ bytes ->
                try
                    let message = System.Text.Encoding.UTF8.GetString bytes
                    handleControlMessage message
                with ex ->
                    sendJson (
                        box
                            {| ``type`` = "error"
                               message = ex.Message |}
                    ))
        )

    do
        pc.addTrack (new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendRecv))

        pc.add_OnAudioFormatsNegotiated (
            Action<_>(fun formats ->
                match formats |> Seq.tryHead with
                | Some format ->
                    negotiatedAudioFormat <- format

                    logger.LogInformation(
                        "Open-source voice WebRTC audio negotiated {Codec}; clock={ClockRate}; rtpClock={RtpClockRate}; channels={ChannelCount}.",
                        format.FormatName,
                        format.ClockRate,
                        format.RtpClockRate,
                        format.ChannelCount
                    )
                | None -> ())
        )

        pc.add_OnAudioFrameReceived (
            Action<EncodedAudioFrame>(fun frame ->
                try
                    let format = frame.AudioFormat
                    let pcm16 = audioEncoder.DecodeAudio(frame.EncodedAudio, format)
                    let sourceRate = codecSampleRate format
                    let channels = codecChannelCount format

                    let samples24k =
                        pcm16
                        |> interleavedToMono channels
                        |> AudioPcm.pcm16ToFloat32
                        |> AudioPcm.resampleLinear sourceRate 24000

                    coordinator.Append24k samples24k
                with ex ->
                    logger.LogWarning(ex, "Could not decode inbound WebRTC audio for session {SessionId}.", session.Id))
        )

        pc.add_ondatachannel (Action<RTCDataChannel>(configureDataChannel))

        pc.add_onicegatheringstatechange (
            Action<_>(fun state ->
                if state = RTCIceGatheringState.complete then
                    iceComplete.TrySetResult() |> ignore)
        )

    member _.AcceptOfferAsync(offer: SdpPayload, cancellationToken: CancellationToken) =
        task {
            if String.IsNullOrWhiteSpace offer.Sdp then
                invalidArg "offer" "WebRTC offer SDP is required."

            let remoteType =
                match (if isNull offer.Type then "" else offer.Type).Trim().ToLowerInvariant() with
                | ""
                | "offer" -> RTCSdpType.offer
                | other -> invalidArg "offer" $"Unsupported SDP type '{other}'. Expected offer."

            let result =
                pc.setRemoteDescription (RTCSessionDescriptionInit(sdp = offer.Sdp, ``type`` = remoteType))

            if result <> SetDescriptionResultEnum.OK then
                invalidOp $"Could not apply browser WebRTC offer SDP: {result}."

            let answer = pc.createAnswer (null)
            do! pc.setLocalDescription answer

            let timeout = Task.Delay(1500, cancellationToken)
            let! _ = Task.WhenAny(iceComplete.Task, timeout)

            let answerSdp =
                if isNull pc.localDescription then
                    answer.sdp.ToString()
                else
                    pc.localDescription.sdp.ToString()

            return { Sdp = answerSdp; Type = "answer" }
        }

    member _.Dispose() =
        lock syncRoot (fun () ->
            if not disposed then
                disposed <- true
                (coordinator :> IDisposable).Dispose()

                if dataChannel <> null then
                    dataChannel.close ()
                    dataChannel <- null

                pc.Close("open-source voice session disposed")
                pc.Dispose())

    interface IDisposable with
        member this.Dispose() = this.Dispose()

type OpenSourceVoiceWebRtcSessionStore
    (agent: IVoiceAgentRuntime, vad: IVadRuntime, options: OpenSourceVoiceOptions, loggerFactory: ILoggerFactory) =
    let sessions =
        ConcurrentDictionary<string, OpenSourceVoiceWebRtcSession>(StringComparer.Ordinal)

    member _.CreateOrReplace(session: VoiceAgentSessionInfo) =
        let next =
            new OpenSourceVoiceWebRtcSession(
                agent,
                vad,
                options,
                session,
                options.WebRtc,
                loggerFactory.CreateLogger<OpenSourceVoiceWebRtcSession>()
            )

        sessions.AddOrUpdate(
            session.Id,
            next,
            Func<_, _, _>(fun _ existing ->
                (existing :> IDisposable).Dispose()
                next)
        )
        |> ignore

        next

    member _.Remove(sessionId: string) =
        match sessions.TryRemove sessionId with
        | true, session ->
            session.Dispose()
            true
        | false, _ -> false

    interface IDisposable with
        member _.Dispose() =
            for KeyValue(_, session) in sessions do
                (session :> IDisposable).Dispose()

            sessions.Clear()
