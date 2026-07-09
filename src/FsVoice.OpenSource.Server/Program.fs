namespace FsVoice.OpenSource.Server

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open FsVoice.OpenSource

module Program =
    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        builder.Services.AddLogging() |> ignore

        let options = OpenSourceVoiceWebApp.bindOptions builder.Configuration
        builder.Services.AddSingleton<IVoiceAgentRuntime>(fun _ ->
            new GemmaVoiceAgentRuntime(options) :> IVoiceAgentRuntime)
        |> ignore

        builder.Services.AddSingleton<OpenSourceVoiceWebRtcSessionStore>() |> ignore

        let app = builder.Build()
        let agent = app.Services.GetRequiredService<IVoiceAgentRuntime>()
        let webRtcStore = app.Services.GetRequiredService<OpenSourceVoiceWebRtcSessionStore>()
        OpenSourceVoiceWebApp.map app agent webRtcStore |> ignore

        app.Run()
        0
