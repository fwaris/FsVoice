namespace Speak2Docs.WorkFlow

open System.Threading.Tasks
open System.Threading.Channels
open FSharp.Control
open RTFlow
open RTFlow.Functions

module HostAgent =
    type State =
        { toHost: ChannelWriter<ToHost>
          bus: WBus<FlowMsg, AgentMsg> }

        member this.Send msg = this.toHost.TryWrite msg |> ignore

    let private update (st: State) (msg: AgentMsg) =
        async {
            match msg with
            | Ag_Log msg -> st.Send(Log msg)
            | Ag_RequestRealtimeConnection session -> st.Send(RequestRealtimeConnection session)
            | Ag_TranscriptUpdated snapshot when snapshot.isFinal -> st.Send(TranscriptFinalized snapshot)
            | Ag_ResponseReady(snapshot, candidate) -> st.Send(OracleResponseReady(snapshot, candidate))
            | Ag_FlowError err -> st.Send(Log err.ErrorText)
            | Ag_FlowDone e -> st.Send(FlowEnded e.abnormal)
            | _ -> ()

            return st
        }

    let startWithReady (ready: TaskCompletionSource<unit>) toHost bus =
        let st0 = { toHost = toHost; bus = bus }

        bus.AgentBus.RunWithReadyAsync("host", ready, st0, update)
        |> FlowUtils.catch bus.PostToFlow

    let start toHost bus =
        let ready =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        startWithReady ready toHost bus
