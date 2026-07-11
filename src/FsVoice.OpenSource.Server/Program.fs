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
    let private defaultPaperSource =
        { kind = Pdf
          location = "AI on the Pulse Wearable Health Study"
          enabled = true }

    let private tryFindRepoRoot start =
        let rec loop (dir: DirectoryInfo) =
            if isNull dir then
                None
            elif File.Exists(Path.Combine(dir.FullName, "FsVoice.slnx")) then
                Some dir.FullName
            else
                loop dir.Parent

        loop (DirectoryInfo(Path.GetFullPath start))

    let private defaultPaperBundleCandidates () =
        [ yield Path.Combine(AppContext.BaseDirectory, "FsColbertIndexes")
          yield
              Path.Combine(Directory.GetCurrentDirectory(), "src", "Speak2Docs", "Resources", "Raw", "FsColbertIndexes")

          match tryFindRepoRoot (Directory.GetCurrentDirectory()) with
          | Some root -> yield Path.Combine(root, "src", "Speak2Docs", "Resources", "Raw", "FsColbertIndexes")
          | None -> () ]
        |> List.distinctBy (fun path -> Path.GetFullPath(path).ToLowerInvariant())

    let private copyDirectory source target =
        Directory.CreateDirectory target |> ignore

        for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(source, file)
            let destination = Path.Combine(target, relative)

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath destination))
            |> ignore

            File.Copy(file, destination, true)

    let private resolveWorkDir (options: OpenSourceVoiceOptions) =
        RuntimePaths.resolveAgainst (Directory.GetCurrentDirectory()) options.WorkDir

    let private stageDefaultPaperIndex workDir =
        match
            defaultPaperBundleCandidates ()
            |> List.tryFind (fun path -> File.Exists(Path.Combine(path, "index-bundle.json")))
        with
        | None ->
            printfn
                "Default paper FsColbert index bundle was not found. Source QA will start without the built-in paper index."

            false
        | Some source ->
            let target = Path.Combine(workDir, "FsVoice", "FsColbert", "Prebuilt")
            copyDirectory source target
            printfn "Staged default paper FsColbert index bundle from '%s' to '%s'." source target
            true

    let private createDefaultPaperContextProviders options =
        let workDir = resolveWorkDir options

        if not (stageDefaultPaperIndex workDir) then
            []
        else
            let providerOptions =
                { FsColbertContextProviderOptions.create workDir FsColbertWithFallback [ defaultPaperSource ] with
                    buildMissingIndexes = false
                    elaborateIndexKeywords = false
                    report = fun message -> printfn "OSS source index: %s" message }

            let provider = new FsColbertContextProvider(providerOptions) :> IQaContextProvider
            let errors = provider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult()

            for error in errors do
                printfn "OSS source index warning: %s" error

            [ provider ]

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        builder.Services.AddLogging() |> ignore

        let options = OpenSourceVoiceWebApp.bindOptions builder.Configuration
        let contextProviders = createDefaultPaperContextProviders options

        builder.Services.AddSingleton<IVoiceAgentRuntime>(fun serviceProvider ->
            let logger = serviceProvider.GetRequiredService<ILogger<GemmaVoiceAgentRuntime>>()

            new GemmaVoiceAgentRuntime(
                options,
                contextProviders = contextProviders,
                report = fun message -> logger.LogInformation("{VoiceAgentEvent}", message)
            )
            :> IVoiceAgentRuntime)
        |> ignore

        builder.Services.AddSingleton<OpenSourceVoiceWebRtcSessionStore>(fun serviceProvider ->
            new OpenSourceVoiceWebRtcSessionStore(
                serviceProvider.GetRequiredService<IVoiceAgentRuntime>(),
                options,
                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
            ))
        |> ignore

        let app = builder.Build()
        let agent = app.Services.GetRequiredService<IVoiceAgentRuntime>()

        let webRtcStore =
            app.Services.GetRequiredService<OpenSourceVoiceWebRtcSessionStore>()

        OpenSourceVoiceWebApp.map app agent webRtcStore |> ignore

        app.Run()
        0
