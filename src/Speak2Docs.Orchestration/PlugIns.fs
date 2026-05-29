namespace Speak2Docs

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

type LoadedPlugIn =
    { plugIn: FsVoice.Ctx.IQaPlugIn
      definition: FsVoice.Ctx.PlugInDefinition }

module PlugInHost =
    let private plugInFolder storageRoot = Path.Combine(storageRoot, "plug-ins")

    let private supportsFolderPlugIns () =
        not (RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS")))

    let private bundledAssemblies hostAssemblies =
        [ yield typeof<FsVoice.Ctx.GenericQaPlugIn>.Assembly
          yield Assembly.GetExecutingAssembly()
          yield! hostAssemblies ]
        |> List.distinctBy _.FullName

    let private plugInTypes (assembly: Assembly) =
        try
            assembly.GetTypes()
            |> Array.filter (fun t ->
                t.IsPublic
                && not t.IsAbstract
                && typeof<FsVoice.Ctx.IQaPlugIn>.IsAssignableFrom t)
            |> Array.toList
        with ex ->
            ignore ex
            []

    let private instantiatePlugIn (plugInType: Type) =
        try
            match plugInType.GetConstructor(Type.EmptyTypes) with
            | null -> Error $"PlugIn {plugInType.FullName} must expose a public parameterless constructor."
            | ctor -> Ok(ctor.Invoke(Array.empty) :?> FsVoice.Ctx.IQaPlugIn)
        with ex ->
            Error $"Unable to create plug-in {plugInType.FullName}: {ex.Message}"

    let private loadAssembly path =
        try
            Assembly.LoadFrom path |> Ok
        with ex ->
            Error $"Skipping plug-in assembly {path}: {ex.Message}"

    let private folderAssemblies storageRoot =
        if not (supportsFolderPlugIns ()) then
            [], []
        else
            let folder = plugInFolder storageRoot

            if not (Directory.Exists folder) then
                [], []
            else
                let assemblies = ResizeArray<Assembly>()
                let logs = ResizeArray<string>()

                for path in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly) do
                    match loadAssembly path with
                    | Ok assembly -> assemblies.Add assembly
                    | Error err -> logs.Add err

                List.ofSeq assemblies, List.ofSeq logs

    let private loadFromAssembly (logs: ResizeArray<string>) (assembly: Assembly) =
        assembly
        |> plugInTypes
        |> List.choose (fun plugInType ->
            match instantiatePlugIn plugInType with
            | Error err ->
                logs.Add err
                None
            | Ok plugIn when plugIn.ContractVersion <> FsVoice.Ctx.PlugInDefinition.currentContractVersion ->
                logs.Add
                    $"Skipping plug-in {plugInType.FullName}: contract version {plugIn.ContractVersion} is not supported."

                None
            | Ok plugIn ->
                Some
                    { plugIn = plugIn
                      definition = FsVoice.Ctx.PlugInDefinition.sanitize plugIn.Definition })

    let loadAll storageRoot hostAssemblies =
        let logs = ResizeArray<string>()

        let bundled =
            bundledAssemblies hostAssemblies |> List.collect (loadFromAssembly logs)

        let folderAssemblies, folderLogs = folderAssemblies storageRoot
        folderLogs |> List.iter logs.Add
        let folderPlugIns = folderAssemblies |> List.collect (loadFromAssembly logs)
        let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let accepted = ResizeArray<LoadedPlugIn>()

        for loaded in bundled @ folderPlugIns do
            if seen.Add loaded.definition.id then
                accepted.Add loaded
            else
                logs.Add $"Duplicate plug-in skipped: {loaded.definition.id}"

        List.ofSeq accepted, List.ofSeq logs

    let loadActive storageRoot hostAssemblies activePlugInId =
        let plugIns, logs = loadAll storageRoot hostAssemblies

        let active =
            activePlugInId
            |> Option.bind (fun id ->
                plugIns
                |> List.tryFind (fun loaded ->
                    String.Equals(loaded.definition.id, id, StringComparison.OrdinalIgnoreCase)))
            |> Option.orElse (
                plugIns
                |> List.tryFind (fun loaded -> loaded.definition.id = FsVoice.Ctx.PlugInDefinition.generic.id)
            )
            |> Option.defaultWith (fun () ->
                { plugIn = FsVoice.Ctx.GenericQaPlugIn() :> FsVoice.Ctx.IQaPlugIn
                  definition = FsVoice.Ctx.PlugInDefinition.generic })

        active, logs

module PlugInComposer =
    let toQaRetrievalMode (mode: RetrievalMode) =
        match mode with
        | InternalDocumentIndex -> FsVoice.Ctx.InternalDocumentIndex
        | FsColbertWithFallback -> FsVoice.Ctx.FsColbertWithFallback

    let fromQaRetrievalMode (mode: FsVoice.Ctx.RetrievalMode) =
        match mode with
        | FsVoice.Ctx.InternalDocumentIndex -> InternalDocumentIndex
        | FsVoice.Ctx.FsColbertWithFallback -> FsColbertWithFallback

    let withHostOverrides
        (modelOverrides: Map<FsVoice.Ctx.ModelRole, string>)
        retrievalMode
        useLexicalFilter
        elaborateIndexKeywords
        (definition: FsVoice.Ctx.PlugInDefinition)
        =
        let definition = FsVoice.Ctx.PlugInDefinition.sanitize definition

        let models =
            modelOverrides
            |> Map.fold
                (fun models role modelId ->
                    match Text.notEmpty modelId with
                    | None -> models
                    | Some modelId ->
                        let current =
                            definition.models
                            |> Map.tryFind role
                            |> Option.defaultValue (FsVoice.Ctx.PlugInDefinition.model role definition)

                        models |> Map.add role { current with modelId = modelId })
                definition.models

        { definition with
            models = models
            runtime =
                { definition.runtime with
                    retrievalMode = toQaRetrievalMode retrievalMode
                    useLexicalFilter = useLexicalFilter
                    elaborateIndexKeywords = elaborateIndexKeywords } }
