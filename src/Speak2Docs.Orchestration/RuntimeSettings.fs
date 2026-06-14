namespace Speak2Docs

open Speak2Docs.WorkFlow

type RuntimeSettings = Map<string, string> ref

module RuntimeSettings =
    [<Literal>]
    let OpenAiKey = "openai.key"

    [<Literal>]
    let LogExpansions = "retrieval.logExpansions"

    [<Literal>]
    let LogChunks = "retrieval.logChunks"

    [<Literal>]
    let AnswerMaxOutputTokens = "answer.maxOutputTokens"

    [<Literal>]
    let AnswerReasoningEffort = "answer.reasoningEffort"

    [<Literal>]
    let AnswerToolCallLoopLimit = "answer.toolCallLoopLimit"

    [<Literal>]
    let MaxContextChunks = "answer.maxContextChunks"

    [<Literal>]
    let AudioDefaultToSpeaker = "audio.defaultToSpeaker"

    [<Literal>]
    let UseLexicalFilter = "retrieval.useLexicalFilter"

    [<Literal>]
    let ElaborateIndexKeywords = "retrieval.elaborateIndexKeywords"

    [<Literal>]
    let UseHybridPdfParsing = "pdf.useHybridParsing"

    [<Literal>]
    let UseLayoutAnalysis = "pdf.useLayoutAnalysis"

    [<Literal>]
    let DescribePdfVisuals = "pdf.describeVisuals"

    [<Literal>]
    let IosAudioRoutePolicy = "audio.iosRoutePolicy"

    [<Literal>]
    let DefaultIosAudioRoutePolicy = "speakerphone"

    [<Literal>]
    let DefaultAnswerMaxOutputTokens = 5000

    [<Literal>]
    let MinAnswerMaxOutputTokens = 128

    [<Literal>]
    let MaxAnswerMaxOutputTokens = 32000

    [<Literal>]
    let DefaultAnswerReasoningEffort = "low"

    [<Literal>]
    let DefaultAnswerToolCallLoopLimit = 8

    let DefaultMaxContextChunks = FsVoice.Ctx.QaDefaults.maxContextChunks

    [<Literal>]
    let DefaultRealtimeOracleFunctionCallTimeoutMs = 45000

    let DefaultOracleAnswerTransportMode =
        FsVoice.Ctx.QaAnswerTransportMode.PersistentWebSocket

    [<Literal>]
    let MinAnswerToolCallLoopLimit = 1

    [<Literal>]
    let MaxAnswerToolCallLoopLimit = 8

    [<Literal>]
    let MinMaxContextChunks = 1

    [<Literal>]
    let MaxMaxContextChunks = 30

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

    let int key fallback values =
        values
        |> tryGet key
        |> Option.bind (fun value ->
            match System.Int32.TryParse value with
            | true, parsed -> Some parsed
            | false, _ -> None)
        |> Option.defaultValue fallback

    let private clamp minValue maxValue value = value |> max minValue |> min maxValue

    let answerMaxOutputTokens values =
        values
        |> int AnswerMaxOutputTokens DefaultAnswerMaxOutputTokens
        |> clamp MinAnswerMaxOutputTokens MaxAnswerMaxOutputTokens

    let normalizeAnswerReasoningEffort value =
        match (defaultArg (Option.ofObj value) "").Trim().ToLowerInvariant() with
        | "low" -> "low"
        | "medium" -> "medium"
        | "high" -> "high"
        | _ -> DefaultAnswerReasoningEffort

    let answerReasoningEffort values =
        values
        |> string AnswerReasoningEffort DefaultAnswerReasoningEffort
        |> normalizeAnswerReasoningEffort
        |> Text.notEmpty

    let answerToolCallLoopLimit values =
        values
        |> int AnswerToolCallLoopLimit DefaultAnswerToolCallLoopLimit
        |> clamp MinAnswerToolCallLoopLimit MaxAnswerToolCallLoopLimit

    let maxContextChunks values =
        values
        |> int MaxContextChunks DefaultMaxContextChunks
        |> clamp MinMaxContextChunks MaxMaxContextChunks

    let normalizeIosAudioRoutePolicy value =
        match (defaultArg (Option.ofObj value) "").Trim().ToLowerInvariant() with
        | "speaker"
        | "speakerphone" -> "speakerphone"
        | "receiver"
        | "headset"
        | "receiverorheadset"
        | "receiver-or-headset" -> "receiverOrHeadset"
        | _ -> DefaultIosAudioRoutePolicy

    let iosAudioRoutePolicy values =
        values
        |> string IosAudioRoutePolicy DefaultIosAudioRoutePolicy
        |> normalizeIosAudioRoutePolicy

    let audioDefaultToSpeaker fallback values =
        match values |> tryGet AudioDefaultToSpeaker with
        | Some value ->
            match System.Boolean.TryParse value with
            | true, parsed -> parsed
            | false, _ -> fallback
        | None ->
            values
            |> tryGet IosAudioRoutePolicy
            |> Option.map (fun value -> normalizeIosAudioRoutePolicy value = "speakerphone")
            |> Option.defaultValue fallback

    let modelRoleKey role =
        $"model.{FsVoice.Ctx.ModelRole.storageName role}"

    let plugInSettingKey key = $"plugin.{key}"

    let modelRoleOverrides values =
        FsVoice.Ctx.ModelRole.all
        |> List.choose (fun role -> values |> tryGet (modelRoleKey role) |> Option.map (fun value -> role, value))
        |> Map.ofList

    let plugInSettings (definition: FsVoice.Ctx.PlugInDefinition) values =
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
          elaborateIndexKeywords = bool ElaborateIndexKeywords false values
          useHybridPdfParsing = true
          useLayoutAnalysis = bool UseLayoutAnalysis true values
          describePdfVisuals = bool DescribePdfVisuals false values
          answerToolCallLoopLimit = answerToolCallLoopLimit values }

    let composePlugIn
        (retrievalMode: RetrievalMode)
        (values: Map<string, string>)
        (definition: FsVoice.Ctx.PlugInDefinition)
        =
        let definition =
            PlugInComposer.withHostOverrides
                (modelRoleOverrides values)
                retrievalMode
                (bool UseLexicalFilter definition.runtime.useLexicalFilter values)
                (bool ElaborateIndexKeywords definition.runtime.elaborateIndexKeywords values)
                definition

        let answerModel = FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.Answer definition
        let maxContextChunks = maxContextChunks values

        let functionCallTimeoutMs =
            if
                definition.runtime.functionCallTimeoutMs = FsVoice.Ctx.PlugInRuntimeOptions.defaults.functionCallTimeoutMs
            then
                DefaultRealtimeOracleFunctionCallTimeoutMs
            else
                definition.runtime.functionCallTimeoutMs

        { definition with
            runtime =
                { definition.runtime with
                    memoryCandidateChunks = maxContextChunks
                    maxContextChunks = maxContextChunks
                    functionCallTimeoutMs = functionCallTimeoutMs }
            models =
                definition.models
                |> Map.add
                    FsVoice.Ctx.Answer
                    { answerModel with
                        maxOutputTokens = Some(answerMaxOutputTokens values)
                        reasoningEffort = answerReasoningEffort values |> Option.orElse answerModel.reasoningEffort } }
