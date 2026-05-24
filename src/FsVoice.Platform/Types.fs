namespace FsVoice.Platform

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Raw bidirectional voice connection. The platform transports JSON events
/// without interpreting provider-specific protocol details.
type VoiceConnection =
    { receiver: ChannelReader<JsonElement>
      sender: ChannelWriter<JsonElement> }

module VoiceConnection =
    let create receiver sender =
        { receiver = receiver; sender = sender }

    let channelPair () =
        let inbound = Channel.CreateUnbounded<JsonElement>()
        let outbound = Channel.CreateUnbounded<JsonElement>()

        { receiver = inbound.Reader
          sender = outbound.Writer },
        inbound,
        outbound

/// Static metadata for a typed voice orchestration.
type VoiceOrchestrationDefinition =
    { id: string
      version: string
      displayName: string
      description: string option
      metadata: Map<string, string> }

module VoiceOrchestrationDefinition =
    let create id version displayName =
        { id = id
          version = version
          displayName = displayName
          description = None
          metadata = Map.empty }

/// Host-provided services and settings available while creating a voice orchestration session.
type VoiceOrchestrationContext =
    { storageRoot: string
      settings: IReadOnlyDictionary<string, string>
      report: string -> unit }

module VoiceOrchestrationContext =
    let create storageRoot settings report =
        { storageRoot = storageRoot
          settings = settings
          report = report }

    let empty storageRoot =
        create storageRoot (Dictionary<string, string>() :> IReadOnlyDictionary<string, string>) ignore

/// Optional codec for hosts that must serialize app-owned typed messages.
type HostMessageCodec<'ToHost, 'FromHost> =
    { encodeToHost: 'ToHost -> JsonElement
      decodeFromHost: JsonElement -> Result<'FromHost, string> }

/// Running voice orchestration session with strongly typed host interaction messages.
type IVoiceSession<'ToHost, 'FromHost> =
    inherit IAsyncDisposable

    /// Fire-and-forget stream of orchestration intents for the host to project or react to.
    abstract ToHost: ChannelReader<'ToHost>

    /// Sends a typed host or environment event into the orchestration.
    abstract SendFromHostAsync: 'FromHost * CancellationToken -> Task

    abstract StartAsync: CancellationToken -> Task
    abstract StopAsync: CancellationToken -> Task

/// Abstract orchestration for voice-enabled systems.
type IVoiceOrchestration<'ToHost, 'FromHost> =
    abstract Definition: VoiceOrchestrationDefinition

    abstract CreateSessionAsync:
        VoiceOrchestrationContext * VoiceConnection * CancellationToken -> Task<IVoiceSession<'ToHost, 'FromHost>>
