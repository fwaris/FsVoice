namespace Speak2Docs.WorkFlow

open System.Threading
open System.Threading.Channels
open Speak2Docs
open FsVoice.Platform
open RTFlow

module StateMachine =
    type SubState =
        { toHost: ChannelWriter<ToHost>
          bus: WBus<FlowMsg, AgentMsg>
          storageRoot: string
          apiKey: string
          plugIn: FsVoice.Ctx.PlugInDefinition
          qaPlugIn: FsVoice.Ctx.IQaPlugIn
          plugInSettings: Map<string, string>
          retrievalMode: RetrievalMode
          voiceConnection: VoiceConnection
          sources: KnowledgeSource list
          flags: SourceFlags }

    let private startAgents ss =
        async {
            HostAgent.start ss.toHost ss.bus

            OracleAgent.start
                ss.storageRoot
                ss.apiKey
                ss.plugIn
                ss.qaPlugIn
                ss.plugInSettings
                ss.retrievalMode
                ss.sources
                ss.flags
                ss.bus

            VoiceAgent.start ss.plugIn ss.sources.Length ss.voiceConnection ss.bus
        }

    let rec private terminate isAbnormal ss =
        async {
            do! Async.Sleep 250
            ss.bus.Close()
        }
        |> Async.Start

        F(s_terminate ss, [ Ag_FlowDone {| abnormal = isAbnormal |} ])

    and private s_start ss msg =
        async {
            match msg with
            | W_Err err -> return F(s_terminate ss, [ Ag_FlowError err; Ag_FlowDone {| abnormal = true |} ])
            | W_Msg Fl_Start ->
                do! startAgents ss

                return F(s_run ss, [ Ag_SourcesUpdated(ss.retrievalMode, ss.sources, ss.flags) ])
            | W_Msg(Fl_Terminate x) -> return terminate x.abnormal ss
        }

    and private s_run ss msg =
        async {
            match msg with
            | W_Err err -> return F(s_terminate ss, [ Ag_FlowError err; Ag_FlowDone {| abnormal = true |} ])
            | W_Msg(Fl_Terminate x) -> return terminate x.abnormal ss
            | W_Msg Fl_Start -> return F(s_run ss, [])
        }

    and private s_terminate ss _ = async { return F(s_terminate ss, []) }

    let create toHost storageRoot apiKey plugIn qaPlugIn plugInSettings retrievalMode voiceConnection sources flags =
        let bus = WBus<FlowMsg, AgentMsg>.Create()

        let ss =
            { toHost = toHost
              bus = bus
              storageRoot = storageRoot
              apiKey = apiKey
              plugIn = plugIn
              qaPlugIn = qaPlugIn
              plugInSettings = plugInSettings
              retrievalMode = retrievalMode
              voiceConnection = voiceConnection
              sources = sources
              flags = flags }

        RTFlow.Workflow.run CancellationToken.None bus (s_start ss)

        { new IFlow<FlowMsg, AgentMsg> with
            member _.PostToFlow msg = bus.PostToFlow(W_Msg msg)
            member _.PostToAgent msg = bus.PostToAgent msg

            member _.Terminate() =
                bus.PostToFlow(W_Msg(Fl_Terminate {| abnormal = false |})) }
