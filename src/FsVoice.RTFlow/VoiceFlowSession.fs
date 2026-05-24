namespace FsVoice.RTFlow

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FsVoice.Platform
open global.RTFlow

/// Adapts an RTFlow flow to the public typed voice session contract.
type VoiceFlowSession<'ToHost, 'FromHost, 'FlowMsg, 'AgentMsg>
    (
        flow: IFlow<'FlowMsg, 'AgentMsg>,
        toHost: ChannelReader<'ToHost>,
        sendFromHostAsync: 'FromHost * CancellationToken -> Task,
        startAsync: IFlow<'FlowMsg, 'AgentMsg> * CancellationToken -> Task,
        stopAsync: IFlow<'FlowMsg, 'AgentMsg> * CancellationToken -> Task,
        disposeAsync: unit -> ValueTask
    ) =
    let mutable stopped = false

    interface IVoiceSession<'ToHost, 'FromHost> with
        member _.ToHost = toHost

        member _.SendFromHostAsync(message, cancellationToken) =
            sendFromHostAsync (message, cancellationToken)

        member _.StartAsync cancellationToken = startAsync (flow, cancellationToken)

        member _.StopAsync cancellationToken =
            task {
                if not stopped then
                    stopped <- true
                    do! stopAsync (flow, cancellationToken)
            }

        member _.DisposeAsync() =
            task {
                if not stopped then
                    stopped <- true
                    do! stopAsync (flow, CancellationToken.None)

                do! (disposeAsync ()).AsTask()
            }
            |> ValueTask

module VoiceFlowSession =
    let create flow toHost sendFromHostAsync startAsync stopAsync =
        VoiceFlowSession(flow, toHost, sendFromHostAsync, startAsync, stopAsync, fun () -> ValueTask())
        :> IVoiceSession<_, _>

    let ofFlow
        (flow: IFlow<'FlowMsg, 'AgentMsg>)
        (toHost: ChannelReader<'ToHost>)
        (mapFromHost: 'FromHost -> 'AgentMsg list)
        (startMessage: 'FlowMsg option)
        =
        let sendFromHostAsync (message: 'FromHost, cancellationToken: CancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            message |> mapFromHost |> List.iter flow.PostToAgent

            Task.CompletedTask

        let startAsync (flow: IFlow<'FlowMsg, 'AgentMsg>, cancellationToken: CancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            startMessage |> Option.iter flow.PostToFlow

            Task.CompletedTask

        let stopAsync (flow: IFlow<'FlowMsg, 'AgentMsg>, cancellationToken: CancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            flow.Terminate()
            Task.CompletedTask

        create flow toHost sendFromHostAsync startAsync stopAsync
