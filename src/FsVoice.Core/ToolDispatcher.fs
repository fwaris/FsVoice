namespace FsVoice.Core

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsVoice

type VoiceToolDispatchResult =
    | ToolSucceeded of VoiceToolResult
    | ToolFailed of VoiceToolError
    | ToolNotFound of VoiceToolId

type VoiceToolDispatcher(tools: IVoiceTool list, ?defaultTimeout: TimeSpan) =
    let defaultTimeout = defaultArg defaultTimeout (TimeSpan.FromSeconds 45.0)

    let toolsByName =
        tools
        |> List.map (fun tool -> VoiceToolId.qualifiedName tool.Definition.id, tool)
        |> Map.ofList

    member _.Tools = tools

    member _.TryFind toolId =
        toolsByName |> Map.tryFind (VoiceToolId.qualifiedName toolId)

    member this.DispatchAsync(call: VoiceToolCall, cancellationToken: CancellationToken) =
        task {
            match this.TryFind call.toolId with
            | None -> return ToolNotFound call.toolId
            | Some tool ->
                use timeout =
                    let duration = tool.Definition.timeout |> Option.defaultValue defaultTimeout

                    CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                    |> fun source ->
                        source.CancelAfter duration
                        source

                try
                    let! result = tool.InvokeAsync(call, timeout.Token)

                    return
                        match result with
                        | Ok result -> ToolSucceeded result
                        | Error error -> ToolFailed error
                with
                | :? OperationCanceledException when timeout.IsCancellationRequested ->
                    return
                        ToolFailed
                            { callId = call.callId
                              toolId = call.toolId
                              message = "Tool invocation timed out."
                              completedAt = DateTimeOffset.UtcNow }
                | ex ->
                    return
                        ToolFailed
                            { callId = call.callId
                              toolId = call.toolId
                              message = ex.Message
                              completedAt = DateTimeOffset.UtcNow }
        }

module VoiceJson =
    let serialize value = JsonSerializer.SerializeToElement value

    let string value = JsonSerializer.SerializeToElement value
