namespace FsVoice.Testing

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FsVoice
open FsVoice.Core

type FakeVoiceTransport(events: VoiceServerEvent list) =
    let queue = Queue<VoiceServerEvent>(events)

    interface IVoiceTransportAdapter with
        member _.SendAsync(_, _) = Task.CompletedTask

        member _.ReceiveAsync _ =
            if queue.Count = 0 then
                Task.FromResult None
            else
                queue.Dequeue() |> Some |> Task.FromResult

        member _.DisposeAsync() = ValueTask()

type VoiceTextRuntime(plugin: IVoicePlugin, hostContext: VoicePluginHostContext) =
    let transport = new FakeVoiceTransport([])
    let engine = VoiceRuntimeEngine(plugin, hostContext, transport)

    member _.Engine = engine

    member _.SubmitAsync(text: string, cancellationToken: CancellationToken) =
        task {
            do! engine.StartAsync cancellationToken
            let turnId = System.Guid.NewGuid().ToString("N")
            do! engine.AppendUserTurnAsync(turnId, text, cancellationToken)
            return engine.Blackboard
        }
