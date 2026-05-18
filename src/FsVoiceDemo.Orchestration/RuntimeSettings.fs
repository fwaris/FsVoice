namespace FsVoiceDemo

open FsVoiceDemo.WorkFlow

type RuntimeSettings = Map<string, string> ref

module RuntimeSettings =
    [<Literal>]
    let OpenAiKey = "openai.key"

    [<Literal>]
    let LogExpansions = "retrieval.logExpansions"

    [<Literal>]
    let LogChunks = "retrieval.logChunks"

    [<Literal>]
    let UseLexicalFilter = "retrieval.useLexicalFilter"

    [<Literal>]
    let ElaborateIndexKeywords = "retrieval.elaborateIndexKeywords"

    [<Literal>]
    let UseHybridPdfParsing = "pdf.useHybridParsing"

    [<Literal>]
    let UseLayoutAnalysis = "pdf.useLayoutAnalysis"

    let empty () : RuntimeSettings = ref Map.empty

    let replace (settings: RuntimeSettings) values = settings.Value <- values

    let snapshot (settings: RuntimeSettings) = settings.Value

    let private tryGet key values =
        values |> Map.tryFind key |> Option.bind Text.notEmpty

    let string key fallback values =
        values |> tryGet key |> Option.defaultValue fallback

    let bool key fallback values =
        values
        |> tryGet key
        |> Option.bind (fun value ->
            match System.Boolean.TryParse value with
            | true, parsed -> Some parsed
            | false, _ -> None)
        |> Option.defaultValue fallback

    let modelRoleKey role =
        $"model.{FsVoice.QA.ModelRole.storageName role}"

    let plugInSettingKey key = $"plugin.{key}"

    let modelRoleOverrides values =
        FsVoice.QA.ModelRole.all
        |> List.choose (fun role -> values |> tryGet (modelRoleKey role) |> Option.map (fun value -> role, value))
        |> Map.ofList

    let plugInSettings (definition: FsVoice.QA.PlugInDefinition) values =
        definition.settingsFacets
        |> List.choose (fun field ->
            values
            |> tryGet (plugInSettingKey field.key)
            |> Option.orElse field.defaultValue
            |> Option.map (fun value -> field.key, value))
        |> Map.ofList

    let sourceFlags (values: Map<string, string>) : SourceFlags =
        { logExpansions = bool LogExpansions false values
          logChunks = bool LogChunks false values
          useLexicalFilter = bool UseLexicalFilter true values
          elaborateIndexKeywords = bool ElaborateIndexKeywords true values
          useHybridPdfParsing = bool UseHybridPdfParsing true values
          useLayoutAnalysis = bool UseLayoutAnalysis true values }

    let composePlugIn
        (retrievalMode: RetrievalMode)
        (values: Map<string, string>)
        (definition: FsVoice.QA.PlugInDefinition)
        =
        PlugInComposer.withHostOverrides
            (modelRoleOverrides values)
            retrievalMode
            (bool UseLexicalFilter definition.runtime.useLexicalFilter values)
            (bool ElaborateIndexKeywords definition.runtime.elaborateIndexKeywords values)
            definition
