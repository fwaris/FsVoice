namespace FsVoice.Hosting.AspNetCore

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open SIPSorcery.Net
open SIPSorceryMedia.Abstractions

[<AllowNullLiteral>]
type SipListenerOptions() =
    member val Enabled = false with get, set
    member val ListenUdpPort = 5060 with get, set
    member val AllowedCodecs = [| "PCMU"; "PCMA" |] with get, set
    member val MediaQueueFrames = 50 with get, set
    member val RealtimeRequestTimeoutSeconds = 15 with get, set

[<AllowNullLiteral>]
type OpenAiRealtimeOptions() =
    member val ApiKey = "" with get, set
    member val RealtimeBaseUrl = "https://api.openai.com/v1/realtime" with get, set
    member val SafetyIdentifier = "fsvoice-server" with get, set
    member val MaxConcurrentWebRtcConnects = 16 with get, set
    member val WebRtcConnectTimeoutSeconds = 8 with get, set
    member val WebRtcConnectMaxAttempts = 2 with get, set

type SipAudioCodec =
    | PCMU
    | PCMA
    | Opus

[<RequireQualifiedAccess>]
module SipAudioCodec =
    let value =
        function
        | PCMU -> "PCMU"
        | PCMA -> "PCMA"
        | Opus -> "opus"

    let tryParse (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            match value.Trim().ToUpperInvariant() with
            | "PCMU"
            | "G711U"
            | "G.711U"
            | "ULAW"
            | "MU-LAW" -> Some PCMU
            | "PCMA"
            | "G711A"
            | "G.711A"
            | "ALAW"
            | "A-LAW" -> Some PCMA
            | "OPUS" -> Some Opus
            | _ -> None

    let defaultAllowed = [ PCMU; PCMA ]

    let fromConfig (values: string seq) =
        let parsed = values |> Seq.choose tryParse |> Seq.distinct |> Seq.toList

        if List.isEmpty parsed then defaultAllowed else parsed

[<RequireQualifiedAccess>]
module SipListenerOptions =
    let private positiveOrDefault fallback value = if value > 0 then value else fallback

    let allowedCodecs (options: SipListenerOptions) =
        if isNull options || isNull options.AllowedCodecs then
            SipAudioCodec.defaultAllowed
        else
            SipAudioCodec.fromConfig options.AllowedCodecs

    let mediaQueueFrames (options: SipListenerOptions) =
        if isNull options then
            50
        else
            positiveOrDefault 50 options.MediaQueueFrames

    let realtimeRequestTimeout (options: SipListenerOptions) =
        let seconds =
            if isNull options then
                15
            else
                positiveOrDefault 15 options.RealtimeRequestTimeoutSeconds

        TimeSpan.FromSeconds(float seconds)

[<RequireQualifiedAccess>]
module OpenAiRealtimeOptions =
    let private positiveOrDefault fallback value = if value > 0 then value else fallback

    let requireApiKey (options: OpenAiRealtimeOptions) =
        if isNull options || String.IsNullOrWhiteSpace options.ApiKey then
            invalidOp "OpenAI:ApiKey is required for SIP realtime sessions."
        else
            options.ApiKey.Trim()

    let realtimeBaseUrl (options: OpenAiRealtimeOptions) =
        if isNull options || String.IsNullOrWhiteSpace options.RealtimeBaseUrl then
            "https://api.openai.com/v1/realtime"
        else
            options.RealtimeBaseUrl.TrimEnd('/')

    let webRtcMaxConcurrentConnects (options: OpenAiRealtimeOptions) =
        if isNull options then
            16
        else
            positiveOrDefault 16 options.MaxConcurrentWebRtcConnects

    let webRtcConnectTimeout (options: OpenAiRealtimeOptions) =
        let seconds =
            if isNull options then
                8
            else
                positiveOrDefault 8 options.WebRtcConnectTimeoutSeconds

        TimeSpan.FromSeconds(float seconds)

    let webRtcConnectMaxAttempts (options: OpenAiRealtimeOptions) =
        if isNull options then
            2
        else
            positiveOrDefault 2 options.WebRtcConnectMaxAttempts

type SipRealtimeState =
    | SipRealtimeDisconnected
    | SipRealtimeConnecting
    | SipRealtimeConnected

type SipCallContext =
    { callId: string
      sessionId: BridgeSessionId
      sipUri: string
      remoteEndPoint: string
      negotiatedCodec: SipAudioCodec }

type SipVoiceSessionFactory<'ToHost, 'FromHost> = SipCallContext -> BridgeSessionOptions<'ToHost, 'FromHost>

type SipRealtimeHostAdapter<'ToHost, 'FromHost> =
    { tryGetRealtimeSession: 'ToHost -> JsonElement option
      stateChanged: SipRealtimeState -> 'FromHost option
      connectionFailed: string -> 'FromHost option }

type SipListenerRegistration<'ToHost, 'FromHost> =
    { createSessionOptions: SipVoiceSessionFactory<'ToHost, 'FromHost>
      hostAdapter: SipRealtimeHostAdapter<'ToHost, 'FromHost> }

[<RequireQualifiedAccess>]
module SdpCodec =
    let private hasPayload payloadId (sdp: string) =
        sdp.Split('\n')
        |> Array.exists (fun line ->
            let trimmed = line.Trim()

            trimmed.StartsWith("m=audio ", StringComparison.OrdinalIgnoreCase)
            && (trimmed.EndsWith($" {payloadId}", StringComparison.Ordinal)
                || trimmed.Contains($" {payloadId} ", StringComparison.Ordinal)))

    let answerContainsCodec codec (sdp: string) =
        if String.IsNullOrWhiteSpace sdp then
            false
        else
            match codec with
            | PCMU ->
                hasPayload "0" sdp
                || sdp.Contains("PCMU/8000", StringComparison.OrdinalIgnoreCase)
            | PCMA ->
                hasPayload "8" sdp
                || sdp.Contains("PCMA/8000", StringComparison.OrdinalIgnoreCase)
            | Opus -> sdp.Contains("opus/48000", StringComparison.OrdinalIgnoreCase)

exception OpenAiCodecRejectedException of string

[<RequireQualifiedAccess>]
module SipsorceryCodecs =
    let toAudioFormat: SipAudioCodec -> AudioFormat =
        function
        | PCMU -> AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU)
        | PCMA -> AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)
        | Opus -> AudioCommonlyUsedFormats.OpusWebRTC

    let tryFromAudioFormat (format: AudioFormat) =
        SipAudioCodec.tryParse format.FormatName
        |> Option.orElseWith (fun () -> SipAudioCodec.tryParse (format.Codec.ToString()))

    let tryFromSdpFormat (format: SDPAudioVideoMediaFormat) =
        match format.ID with
        | 0 -> Some PCMU
        | 8 -> Some PCMA
        | _ ->
            if isNull format.Rtpmap then
                None
            else
                format.Rtpmap.Split('/').[0] |> SipAudioCodec.tryParse

    let toMediaTrack codec =
        MediaStreamTrack(toAudioFormat codec, MediaStreamStatusEnum.SendRecv)

    let toMediaTrackMany codecs =
        let formats = codecs |> List.map toAudioFormat |> ResizeArray

        MediaStreamTrack(System.Collections.Generic.List<AudioFormat>(formats), MediaStreamStatusEnum.SendRecv)

type IOpenAiRealtimeWebRtcSession =
    inherit IDisposable

    abstract Received: IEvent<JsonElement>
    abstract Connected: IEvent<unit>
    abstract Closed: IEvent<exn option>

    abstract StartAsync: session: JsonElement * codec: SipAudioCodec * cancellationToken: CancellationToken -> Task

    abstract SendClientEvent: JsonElement -> unit

    abstract PipeFromRtpSession:
        rtpSession: RTPSession * codec: SipAudioCodec * mediaQueueFrames: int * onDrop: (int64 -> unit) ->
            RealtimeMediaPipe

    abstract PipeToRtpSession:
        rtpSession: RTPSession *
        codec: SipAudioCodec *
        mediaQueueFrames: int *
        onDrop: (int64 -> unit) *
        onSent: (unit -> unit) ->
            RealtimeMediaPipe

type IOpenAiRealtimeWebRtcSessionFactory =
    abstract Create: sessionId: string -> IOpenAiRealtimeWebRtcSession
