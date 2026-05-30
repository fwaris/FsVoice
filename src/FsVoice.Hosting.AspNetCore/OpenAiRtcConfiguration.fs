namespace FsVoice.Hosting.AspNetCore

open System
open System.Collections.Generic
open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets
open Microsoft.Extensions.Logging
open SIPSorcery.Net

[<RequireQualifiedAccess>]
module OpenAiRtcConfiguration =
    type BindAddressCandidate = { address: IPAddress; source: string }

    [<Literal>]
    let BindAddressEnvironmentVariable = "OPENAI_WEBRTC_BIND_ADDRESS"

    [<Literal>]
    let StunServerUrl = "stun:stun.l.google.com:19302"

    let private startsWithInsensitive (prefix: string) (value: string) =
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)

    let private isCarrierGradeNat (address: IPAddress) =
        let bytes = address.GetAddressBytes()
        bytes.Length = 4 && bytes.[0] = 100uy && bytes.[1] >= 64uy && bytes.[1] <= 127uy

    let private isIPv4Private (address: IPAddress) =
        let bytes = address.GetAddressBytes()

        bytes.Length = 4
        && (bytes.[0] = 10uy
            || (bytes.[0] = 172uy && bytes.[1] >= 16uy && bytes.[1] <= 31uy)
            || (bytes.[0] = 192uy && bytes.[1] = 168uy))

    let private isLinkLocal (address: IPAddress) =
        match address.AddressFamily with
        | AddressFamily.InterNetwork ->
            let bytes = address.GetAddressBytes()
            bytes.Length = 4 && bytes.[0] = 169uy && bytes.[1] = 254uy
        | AddressFamily.InterNetworkV6 -> address.IsIPv6LinkLocal
        | _ -> false

    let private isIPv6UniqueLocal (address: IPAddress) =
        if address.AddressFamily <> AddressFamily.InterNetworkV6 then
            false
        else
            let bytes = address.GetAddressBytes()
            bytes.Length = 16 && (bytes.[0] &&& 0xfeuy) = 0xfcuy

    let private bindAddressPriority (address: IPAddress) =
        if IPAddress.IsLoopback address || isLinkLocal address then
            100
        elif address.AddressFamily = AddressFamily.InterNetwork && isIPv4Private address then
            0
        elif address.AddressFamily = AddressFamily.InterNetwork && isCarrierGradeNat address then
            10
        elif
            address.AddressFamily = AddressFamily.InterNetworkV6
            && isIPv6UniqueLocal address
        then
            20
        elif address.AddressFamily = AddressFamily.InterNetwork then
            30
        elif address.AddressFamily = AddressFamily.InterNetworkV6 then
            40
        else
            90

    let private interfacePriority (nic: NetworkInterface) =
        let name = nic.Name

        match nic.NetworkInterfaceType with
        | NetworkInterfaceType.Wireless80211
        | NetworkInterfaceType.Ethernet
        | NetworkInterfaceType.GigabitEthernet
        | NetworkInterfaceType.FastEthernetFx
        | NetworkInterfaceType.FastEthernetT -> 0
        | _ when
            startsWithInsensitive "utun" name
            || startsWithInsensitive "tun" name
            || startsWithInsensitive "tap" name
            || startsWithInsensitive "ppp" name
            || nic.NetworkInterfaceType = NetworkInterfaceType.Tunnel
            ->
            20
        | _ -> 10

    let private tryParseCandidate source (value: string) =
        match IPAddress.TryParse value with
        | true, address -> Some { address = address; source = source }
        | _ -> None

    let preferAutomaticFallbackCandidates candidates =
        let ipv4Candidates =
            candidates
            |> List.filter (fun candidate -> candidate.address.AddressFamily = AddressFamily.InterNetwork)

        if List.isEmpty ipv4Candidates then
            candidates
        else
            ipv4Candidates

    let discoverBindAddressCandidates () =
        NetworkInterface.GetAllNetworkInterfaces()
        |> Seq.filter (fun nic ->
            nic.OperationalStatus = OperationalStatus.Up
            && nic.NetworkInterfaceType <> NetworkInterfaceType.Loopback)
        |> Seq.collect (fun nic ->
            let nicPriority = interfacePriority nic

            nic.GetIPProperties().UnicastAddresses
            |> Seq.map (fun unicast ->
                nicPriority,
                bindAddressPriority unicast.Address,
                { address = unicast.Address
                  source = $"network interface {nic.Name}" }))
        |> Seq.filter (fun (_, _, candidate) ->
            not (IPAddress.IsLoopback candidate.address)
            && not (isLinkLocal candidate.address)
            && (candidate.address.AddressFamily = AddressFamily.InterNetwork
                || candidate.address.AddressFamily = AddressFamily.InterNetworkV6))
        |> Seq.distinctBy (fun (_, _, candidate) -> candidate.address)
        |> Seq.sortBy (fun (nicPriority, addressPriority, candidate) ->
            nicPriority, addressPriority, candidate.address.ToString())
        |> Seq.map (fun (_, _, candidate) -> candidate)
        |> Seq.toList
        |> preferAutomaticFallbackCandidates

    let tryGetConfiguredBindAddressOverride (logger: ILogger) (sessionId: string) =
        Environment.GetEnvironmentVariable(BindAddressEnvironmentVariable)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.bind (fun value ->
            match tryParseCandidate BindAddressEnvironmentVariable value with
            | Some candidate -> Some candidate
            | None ->
                logger.LogWarning(
                    "Ignoring invalid OpenAI WebRTC bind address '{BindAddress}' for SIP session {SessionId}; using automatic bind address discovery.",
                    value,
                    sessionId
                )

                None)

    let private createWithCandidate (logger: ILogger) (sessionId: string) candidate =
        let iceServer = RTCIceServer()
        iceServer.urls <- StunServerUrl

        let config =
            RTCConfiguration(
                X_UseRtpFeedbackProfile = true,
                X_ICEIncludeAllInterfaceAddresses = Option.isNone candidate
            )

        config.iceServers <- List<RTCIceServer>([ iceServer ])

        match candidate with
        | Some candidate ->
            config.X_BindAddress <- candidate.address

            logger.LogInformation(
                "Using OpenAI WebRTC bind address {BindAddress} for SIP session {SessionId} from {Source}.",
                candidate.address,
                sessionId,
                candidate.source
            )
        | None ->
            logger.LogInformation(
                "Using OpenAI WebRTC default socket binding for SIP session {SessionId}; no bind address candidate was available.",
                sessionId
            )

        config

    let create (logger: ILogger) (sessionId: string) =
        let candidate =
            tryGetConfiguredBindAddressOverride logger sessionId
            |> Option.orElseWith (fun () -> discoverBindAddressCandidates () |> List.tryHead)

        createWithCandidate logger sessionId candidate
