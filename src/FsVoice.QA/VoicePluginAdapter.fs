namespace FsVoice.QA

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsVoice

type private QaVoiceTool(pluginId: VoicePluginId, tool: IQaTool) =
    let toolId =
        { pluginId = pluginId
          name = VoiceToolName.create tool.Name }

    let parameterSchema =
        let properties = Dictionary<string, obj>()
        let required = ResizeArray<string>()

        for parameter in tool.Parameters do
            properties[parameter.name] <-
                {| ``type`` = "string"
                   description = parameter.description |}

            if parameter.required then
                required.Add parameter.name

        JsonSerializer.SerializeToElement(
            {| ``type`` = "object"
               properties = properties
               required = required.ToArray() |}
        )

    let stringArguments (arguments: JsonElement) =
        let values = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        if arguments.ValueKind = JsonValueKind.Object then
            for property in arguments.EnumerateObject() do
                let value =
                    match property.Value.ValueKind with
                    | JsonValueKind.String -> property.Value.GetString()
                    | JsonValueKind.Null
                    | JsonValueKind.Undefined -> ""
                    | _ -> property.Value.GetRawText()

                values[property.Name] <- defaultArg (Option.ofObj value) ""

        values :> IReadOnlyDictionary<string, string>

    interface IVoiceTool with
        member _.Definition =
            { id = toolId
              description = tool.Description
              parameters =
                tool.Parameters
                |> List.map (fun parameter ->
                    { name = parameter.name
                      description = parameter.description
                      required = parameter.required })
              inputSchema = Some { schema = parameterSchema }
              timeout = None }

        member _.InvokeAsync(call, cancellationToken) =
            task {
                let! result = tool.InvokeAsync(stringArguments call.arguments, cancellationToken)

                return
                    Ok
                        { callId = call.callId
                          toolId = toolId
                          content = JsonSerializer.SerializeToElement {| content = result.content |}
                          metadata = result.metadata
                          completedAt = DateTimeOffset.UtcNow }
            }

type QaVoicePluginAdapter(plugIn: IQaPlugIn) =
    let pluginId = VoicePluginId.create plugIn.Definition.id

    interface IVoicePlugin with
        member _.ContractVersion = 1
        member _.PluginId = pluginId

        member _.Definition =
            { id = plugIn.Definition.id
              version = plugIn.Definition.version
              displayName = plugIn.Definition.displayName
              description = plugIn.Definition.description
              prompts =
                [ "answerSystem", plugIn.Definition.prompts.answerSystem
                  "realtimeInstructions", plugIn.Definition.prompts.realtimeInstructions
                  "speechResultInstruction", plugIn.Definition.prompts.speechResultInstruction ]
                |> List.choose (fun (key, value) -> value |> Option.map (fun text -> key, text))
                |> Map.ofList
              settings = Map.empty }

        member _.GetTools hostContext =
            let qaHost =
                { new IQaToolHost with
                    member _.Report message = hostContext.report message

                    member _.SearchKnowledgeAsync(_, _, _) =
                        Task.FromResult("No QA session is attached to this voice plugin adapter.")

                    member _.SourceInventoryAsync _ =
                        Task.FromResult("No QA session is attached to this voice plugin adapter.")

                    member _.SearchMemoryAsync(_, _, _) =
                        Task.FromResult("No memory provider is attached to this voice plugin adapter.")

                    member _.SearchBlackboardAsync(_, _) =
                        Task.FromResult("No backboard query provider is attached to this voice plugin adapter.") }

            plugIn.GetToolProviders()
            |> List.collect (fun provider -> provider.GetTools qaHost)
            |> List.map (fun tool -> QaVoiceTool(pluginId, tool) :> IVoiceTool)

        member _.GetAgents _ = []

module VoicePluginAdapters =
    let fromQaPlugIn plugIn =
        QaVoicePluginAdapter(plugIn) :> IVoicePlugin
