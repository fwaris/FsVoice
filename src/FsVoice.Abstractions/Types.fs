namespace FsVoice

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type VoicePluginId = private VoicePluginId of string

module VoicePluginId =
    let value (VoicePluginId value) = value

    let create value =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Plugin id must not be empty."

        let trimmed = value.Trim()

        if trimmed.Contains(" ") then
            invalidArg (nameof value) "Plugin id must not contain whitespace."

        VoicePluginId trimmed

type VoiceToolName = private VoiceToolName of string

module VoiceToolName =
    let value (VoiceToolName value) = value

    let create value =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Tool name must not be empty."

        VoiceToolName(value.Trim())

type VoiceToolId =
    { pluginId: VoicePluginId
      name: VoiceToolName }

module VoiceToolId =
    let qualifiedName toolId =
        $"{VoicePluginId.value toolId.pluginId}.{VoiceToolName.value toolId.name}"

type VoiceJsonSchema = { schema: JsonElement }

type VoiceToolParameter =
    { name: string
      description: string
      required: bool }

type VoiceToolDefinition =
    { id: VoiceToolId
      description: string
      parameters: VoiceToolParameter list
      inputSchema: VoiceJsonSchema option
      timeout: TimeSpan option }

type VoiceToolCall =
    { callId: string
      toolId: VoiceToolId
      arguments: JsonElement
      requestedAt: DateTimeOffset }

type VoiceToolResult =
    { callId: string
      toolId: VoiceToolId
      content: JsonElement
      metadata: IReadOnlyDictionary<string, string>
      completedAt: DateTimeOffset }

type VoiceToolError =
    { callId: string
      toolId: VoiceToolId
      message: string
      completedAt: DateTimeOffset }

type IVoiceTool =
    abstract Definition: VoiceToolDefinition
    abstract InvokeAsync: VoiceToolCall * CancellationToken -> Task<Result<VoiceToolResult, VoiceToolError>>

type VoiceAgentTrigger =
    | SessionStarted
    | TurnCompleted
    | ToolCompleted
    | Timer of interval: TimeSpan
    | Custom of eventName: string

type VoiceAgentDefinition =
    { id: string
      displayName: string
      triggers: VoiceAgentTrigger list }

type IVoiceAgent =
    abstract Definition: VoiceAgentDefinition
    abstract StartAsync: IVoiceEventPublisher * CancellationToken -> Task
    abstract StopAsync: CancellationToken -> Task

and IVoiceEventPublisher =
    abstract Publish: VoiceEvent -> unit

and VoiceTurn =
    { turnId: string
      role: string
      text: string
      createdAt: DateTimeOffset }

and VoiceToolObservation =
    { callId: string
      toolName: string
      content: JsonElement
      createdAt: DateTimeOffset }

and VoiceSuggestion =
    { suggestionId: string
      pluginId: VoicePluginId
      title: string
      detail: string option
      payload: JsonElement option
      createdAt: DateTimeOffset }

and VoiceEvent =
    { name: string
      sessionId: string option
      correlationId: string option
      payload: JsonElement option
      createdAt: DateTimeOffset }

type VoiceMemoryRecord =
    { id: string
      text: string
      score: float option
      metadata: IReadOnlyDictionary<string, string> }

type IVoiceMemoryProvider =
    abstract SearchAsync: query: string * maxResults: int * CancellationToken -> Task<VoiceMemoryRecord list>
    abstract WriteAsync: record: VoiceMemoryRecord * CancellationToken -> Task

type VoicePlugInDefinition =
    { id: string
      version: string
      displayName: string
      description: string option
      prompts: Map<string, string>
      settings: Map<string, string> }

type VoicePluginHostContext =
    { storageRoot: string
      settings: Map<string, string>
      report: string -> unit }

type IVoicePlugin =
    abstract ContractVersion: int
    abstract PluginId: VoicePluginId
    abstract Definition: VoicePlugInDefinition
    abstract GetTools: VoicePluginHostContext -> IVoiceTool list
    abstract GetAgents: VoicePluginHostContext -> IVoiceAgent list

type IVoicePlugIn =
    inherit IVoicePlugin

type VoiceClientEvent =
    { eventId: string
      eventType: string
      payload: JsonElement option }

type VoiceServerEvent =
    { eventId: string
      eventType: string
      payload: JsonElement option }

type IVoiceTransportAdapter =
    inherit IAsyncDisposable

    abstract SendAsync: VoiceClientEvent * CancellationToken -> Task
    abstract ReceiveAsync: CancellationToken -> Task<VoiceServerEvent option>
