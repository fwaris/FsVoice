namespace FsVoice.Ctx

module ModelCapabilities =
    let private temperatureUnsupportedPrefixes = [ "gpt-5.5" ]

    let supportsTemperature (modelId: string) =
        let modelId =
            modelId
            |> Option.ofObj
            |> Option.defaultValue ""
            |> fun value -> value.Trim().ToLowerInvariant()

        temperatureUnsupportedPrefixes
        |> List.exists (fun prefix -> modelId = prefix || modelId.StartsWith(prefix + "-"))
        |> not

module QaDefaults =
    let nanoModel = "gpt-5-nano"
    let keywordModel = "gpt-5-mini"
    let answerModel = "gpt-5.5"
    let answerMaxOutputTokens = 5000
    let memoryCandidateChunks = 14
    let maxContextChunks = 12
    let neighborSeeds = 4
