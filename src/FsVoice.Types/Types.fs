namespace FsVoice.Types

open System
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open System.Text.Json

/// Abstract voice connection with channels for receiving and sending transport events.
type VoiceConnection =
    { receiver: ChannelReader<JsonElement>
      sender: ChannelWriter<JsonElement> }

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
      settings: Map<string, string>
      report: string -> unit }

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
