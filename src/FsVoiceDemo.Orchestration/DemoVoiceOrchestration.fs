namespace FsVoiceDemo.WorkFlow

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FsVoice.Types
open FsVoiceDemo
open RTFlow

type DemoVoiceOrchestrationOptions =
    { settings: RuntimeSettings
      plugIn: FsVoice.QA.PlugInDefinition
      qaPlugIn: FsVoice.QA.IQaPlugIn
      retrievalMode: RetrievalMode
      sources: KnowledgeSource list }

type private DemoVoiceSession
    (options: DemoVoiceOrchestrationOptions, context: VoiceOrchestrationContext, voiceConnection: VoiceConnection) =
    let toHost = Channel.CreateUnbounded<ToHost>()
    let mutable flow: IFlow<FlowMsg, AgentMsg> option = None
    let mutable retrievalMode = options.retrievalMode
    let mutable sources = options.sources

    let settingsSnapshot () =
        RuntimeSettings.snapshot options.settings

    let sourceFlags () =
        settingsSnapshot () |> RuntimeSettings.sourceFlags

    let startFlow () =
        let settings = settingsSnapshot ()
        let plugIn = RuntimeSettings.composePlugIn retrievalMode settings options.plugIn
        let plugInSettings = RuntimeSettings.plugInSettings plugIn settings
        let apiKey = RuntimeSettings.string RuntimeSettings.OpenAiKey "" settings
        let flags = RuntimeSettings.sourceFlags settings

        let nextFlow =
            StateMachine.create
                toHost.Writer
                context.storageRoot
                apiKey
                plugIn
                options.qaPlugIn
                plugInSettings
                retrievalMode
                voiceConnection
                sources
                flags

        flow <- Some nextFlow
        nextFlow.PostToFlow Fl_Start

    let postSources () =
        match flow with
        | Some active -> active.PostToAgent(Ag_SourcesUpdated(retrievalMode, sources, sourceFlags ()))
        | None -> ()

    let postLog text =
        match flow with
        | Some active -> active.PostToAgent(Ag_Log text)
        | None -> ()

    interface IVoiceSession<ToHost, FromHost> with
        member _.ToHost = toHost.Reader

        member _.SendFromHostAsync(message, _) =
            task {
                match message with
                | SourcesChanged(mode, nextSources) ->
                    retrievalMode <- mode
                    sources <- nextSources

                    postLog
                        $"Host sources changed: mode={RetrievalModes.displayName mode}; sources={nextSources.Length}."

                    postSources ()
                | RuntimeSettingsChanged -> postSources ()
                | RealtimeStateChanged state -> postLog $"Realtime state changed: {state}."
                | RealtimeConnectionFailed error -> postLog $"Realtime connection failed: {error}"
            }

        member _.StartAsync _ =
            task {
                match flow with
                | Some _ -> ()
                | None -> startFlow ()
            }

        member _.StopAsync _ =
            task {
                match flow with
                | Some active ->
                    active.Terminate()
                    flow <- None
                | None -> ()

                toHost.Writer.TryComplete() |> ignore
            }

        member this.DisposeAsync() =
            task { do! (this :> IVoiceSession<ToHost, FromHost>).StopAsync CancellationToken.None }
            |> ValueTask

type DemoVoiceOrchestration(options: DemoVoiceOrchestrationOptions) =
    interface IVoiceOrchestration<ToHost, FromHost> with
        member _.Definition =
            { VoiceOrchestrationDefinition.create "fsvoice.demo" "0.1.0" "FsVoice Demo" with
                description = Some "Platform-neutral FsVoice demo orchestration." }

        member _.CreateSessionAsync(context, voiceConnection, _) =
            Task.FromResult(DemoVoiceSession(options, context, voiceConnection) :> IVoiceSession<ToHost, FromHost>)
