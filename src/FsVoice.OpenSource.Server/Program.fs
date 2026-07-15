namespace FsVoice.OpenSource.Server

open System
open System.IO
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open FsVoice.Ctx
open FsVoice.OpenSource
open FsVoice.Retrieval

module Program =
    let private resolveWorkDir (options: OpenSourceVoiceOptions) =
        RuntimePaths.resolveAgainst (Directory.GetCurrentDirectory()) options.WorkDir

    let private resolveRuntimeBase (options: OpenSourceVoiceOptions) =
        RuntimePaths.resolveBaseFromCandidates
            [| Directory.GetCurrentDirectory(); AppContext.BaseDirectory |]
            [| options.Stt.ModelDir; options.Tts.ModelDir; options.Vad.ModelPath |]

    let private createExternalContextProvider options =
        let workDir = resolveWorkDir options

        let bundleDirectory =
            RuntimePaths.resolveAgainst (Directory.GetCurrentDirectory()) options.Index.BundleDirectory

        let providerOptions =
            { ExternalFsColbertContextProviderOptions.create workDir bundleDirectory with
                report = fun message -> printfn "OSS source index: %s" message }

        let provider = new ExternalFsColbertContextProvider(providerOptions)
        let contextProvider = provider :> IQaContextProvider

        let errors =
            contextProvider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult()

        if not (List.isEmpty errors) then
            contextProvider.DisposeAsync().AsTask().GetAwaiter().GetResult()

            invalidOp
                $"External FsColbert bundle startup validation failed:{Environment.NewLine}{String.concat Environment.NewLine errors}"

        let info =
            provider.BundleInfo
            |> Option.defaultWith (fun () -> invalidOp "External FsColbert bundle loaded without bundle metadata.")

        let status =
            { Ready = true
              BundleDirectory = info.bundleDirectory
              BundleId = info.bundleId
              BundleVersion = info.bundleVersion
              ModelId = info.modelId
              SourceCount = info.sourceCount
              Message =
                $"External FsColbert bundle '{info.bundleId}' version {info.bundleVersion} is ready with {info.sourceCount} source(s)." }

        [ contextProvider ], status

    let private assetStatus (options: OpenSourceVoiceOptions) =
        OpenSourceVoiceWebApp.tryReadAssetStatus options.Assets.StatusFile
        |> Option.defaultValue OpenSourceVoiceWebApp.localAssetStatus

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        builder.Services.AddLogging() |> ignore

        let options = OpenSourceVoiceWebApp.bindOptions builder.Configuration
        let contextProviders, indexStatus = createExternalContextProvider options

        let vadRuntime =
            new SileroVadRuntime(options.Vad, resolveRuntimeBase options) :> IVadRuntime

        builder.Services.AddSingleton<IVadRuntime>(vadRuntime) |> ignore

        builder.Services.AddSingleton<IVoiceAgentRuntime>(fun serviceProvider ->
            let logger = serviceProvider.GetRequiredService<ILogger<GemmaVoiceAgentRuntime>>()

            new GemmaVoiceAgentRuntime(
                options,
                contextProviders = contextProviders,
                indexStatus = indexStatus,
                report = fun message -> logger.LogInformation("{VoiceAgentEvent}", message)
            )
            :> IVoiceAgentRuntime)
        |> ignore

        builder.Services.AddSingleton<OpenSourceVoiceWebRtcSessionStore>(fun serviceProvider ->
            new OpenSourceVoiceWebRtcSessionStore(
                serviceProvider.GetRequiredService<IVoiceAgentRuntime>(),
                serviceProvider.GetRequiredService<IVadRuntime>(),
                options,
                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
            ))
        |> ignore

        let app = builder.Build()
        let agent = app.Services.GetRequiredService<IVoiceAgentRuntime>()
        let vad = app.Services.GetRequiredService<IVadRuntime>()

        let webRtcStore =
            app.Services.GetRequiredService<OpenSourceVoiceWebRtcSessionStore>()

        OpenSourceVoiceWebApp.mapWithAssets app agent vad (assetStatus options) options webRtcStore
        |> ignore

        app.Run()
        0
