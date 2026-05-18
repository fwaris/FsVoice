namespace FsVoice.Core

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsVoice

type VoiceRuntimeOptions =
    { sessionId: string
      toolTimeout: TimeSpan option }

module VoiceRuntimeOptions =
    let defaults =
        { sessionId = Guid.NewGuid().ToString("N")
          toolTimeout = None }

type VoiceRuntimeEngine
    (
        plugin: IVoicePlugin,
        hostContext: VoicePluginHostContext,
        transport: IVoiceTransportAdapter,
        ?options: VoiceRuntimeOptions
    ) =
    let options = defaultArg options VoiceRuntimeOptions.defaults
    let eventBus = VoiceEventBus()
    let blackboardGate = obj ()
    let mutable blackboard = Blackboard.empty
    let tools = plugin.GetTools hostContext
    let dispatcher = VoiceToolDispatcher(tools, ?defaultTimeout = options.toolTimeout)
    let agents = plugin.GetAgents hostContext
    let mutable started = false

    let updateBlackboard update =
        lock blackboardGate (fun () -> blackboard <- update blackboard)

    let currentBlackboard () =
        lock blackboardGate (fun () -> blackboard)

    member _.SessionId = options.sessionId
    member _.Events = eventBus.Events
    member _.Blackboard = currentBlackboard ()
    member _.ToolDispatcher = dispatcher

    member _.Subscribe(handler: VoiceEvent -> unit) = eventBus.Subscribe handler

    member _.StartAsync(cancellationToken: CancellationToken) =
        task {
            if not started then
                started <- true

                for agent in agents do
                    do! agent.StartAsync(eventBus, cancellationToken)

                (eventBus :> IVoiceEventPublisher)
                    .Publish(
                        VoiceEvents.create
                            "session.started"
                            (Some options.sessionId)
                            None
                            (Some(
                                JsonSerializer.SerializeToElement {| pluginId = VoicePluginId.value plugin.PluginId |}
                            ))
                    )
        }

    member _.StopAsync(cancellationToken: CancellationToken) =
        task {
            if started then
                for agent in agents |> List.rev do
                    do! agent.StopAsync cancellationToken

                (eventBus :> IVoiceEventPublisher)
                    .Publish(VoiceEvents.create "session.ended" (Some options.sessionId) None None)

                started <- false
        }

    member _.AppendUserTurnAsync(turnId: string, text: string, cancellationToken: CancellationToken) =
        task {
            let turn =
                { turnId = turnId
                  role = "user"
                  text = text
                  createdAt = DateTimeOffset.UtcNow }

            updateBlackboard (Blackboard.appendTurn turn)

            (eventBus :> IVoiceEventPublisher)
                .Publish(
                    VoiceEvents.create
                        "speech.final"
                        (Some options.sessionId)
                        (Some turnId)
                        (Some(JsonSerializer.SerializeToElement {| text = text |}))
                )

            do! Task.CompletedTask
        }

    member _.DispatchToolAsync(call: VoiceToolCall, cancellationToken: CancellationToken) =
        task {
            (eventBus :> IVoiceEventPublisher)
                .Publish(
                    VoiceEvents.create
                        "tool.started"
                        (Some options.sessionId)
                        (Some call.callId)
                        (Some(JsonSerializer.SerializeToElement {| toolName = VoiceToolId.qualifiedName call.toolId |}))
                )

            let! result = dispatcher.DispatchAsync(call, cancellationToken)

            match result with
            | ToolSucceeded toolResult ->
                let observation =
                    { callId = toolResult.callId
                      toolName = VoiceToolId.qualifiedName toolResult.toolId
                      content = toolResult.content
                      createdAt = toolResult.completedAt }

                updateBlackboard (Blackboard.appendToolObservation observation)

                (eventBus :> IVoiceEventPublisher)
                    .Publish(
                        VoiceEvents.create
                            "tool.completed"
                            (Some options.sessionId)
                            (Some call.callId)
                            (Some(
                                JsonSerializer.SerializeToElement {| toolName = VoiceToolId.qualifiedName call.toolId |}
                            ))
                    )
            | ToolFailed error ->
                (eventBus :> IVoiceEventPublisher)
                    .Publish(
                        VoiceEvents.create
                            "tool.failed"
                            (Some options.sessionId)
                            (Some call.callId)
                            (Some(JsonSerializer.SerializeToElement {| message = error.message |}))
                    )
            | ToolNotFound toolId ->
                (eventBus :> IVoiceEventPublisher)
                    .Publish(
                        VoiceEvents.create
                            "tool.failed"
                            (Some options.sessionId)
                            (Some call.callId)
                            (Some(
                                JsonSerializer.SerializeToElement
                                    {| message = $"Tool not found: {VoiceToolId.qualifiedName toolId}" |}
                            ))
                    )

            return result
        }

    member this.RunUntilClosedAsync(cancellationToken: CancellationToken) =
        task {
            do! this.StartAsync cancellationToken

            let mutable keepRunning = true

            while keepRunning && not cancellationToken.IsCancellationRequested do
                let! serverEvent = transport.ReceiveAsync cancellationToken

                match serverEvent with
                | None -> keepRunning <- false
                | Some event ->
                    (eventBus :> IVoiceEventPublisher)
                        .Publish(
                            VoiceEvents.create
                                event.eventType
                                (Some options.sessionId)
                                (Some event.eventId)
                                event.payload
                        )

            do! this.StopAsync cancellationToken
        }
