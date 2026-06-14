namespace FsVoice.Ctx

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<CLIMutable>]
type TextReplacement =
    { pattern: string; replacement: string }

[<CLIMutable>]
type QueryExpansionRule =
    { triggers: string list
      terms: string list }

[<CLIMutable>]
type QaPlugInProfile =
    { id: string
      displayName: string
      description: string option
      voiceReplacements: TextReplacement list
      queryExpansionRules: QueryExpansionRule list
      keywordInstruction: string option
      keywordHints: string list
      answerSystemInstruction: string option }

[<CLIMutable>]
type QaPlugInContextManifest =
    { id: string
      provider: string
      displayName: string
      assetPath: string
      enabled: bool }

[<CLIMutable>]
type QaPlugInToolManifest =
    { id: string
      assemblyPath: string
      enabled: bool }

[<CLIMutable>]
type QaPlugInUiManifest =
    { id: string
      kind: string
      assetPath: string }

[<CLIMutable>]
type QaPlugInPackageManifest =
    { contractVersion: int
      id: string
      version: string
      displayName: string
      description: string option
      behaviorProfile: QaPlugInProfile
      contexts: QaPlugInContextManifest list
      tools: QaPlugInToolManifest list
      ui: QaPlugInUiManifest list }

type ModelRole =
    | Realtime
    | Transcriber
    | Answer
    | Keyword
    | QueryExpansion
    | VisualDescription

[<CLIMutable>]
type ModelRoleConfig =
    { provider: string
      modelId: string
      maxOutputTokens: int option
      temperature: float32 option
      reasoningEffort: string option
      extra: Map<string, string> }

[<CLIMutable>]
type PromptSet =
    { realtimeInstructions: string option
      transcriberPrompt: string option
      answerSystem: string option
      answerUserTemplate: string option
      keywordInstruction: string option
      speechResultInstruction: string option }

[<CLIMutable>]
type PlugInRuntimeOptions =
    { retrievalMode: RetrievalMode
      enableQueryExpansion: bool
      elaborateIndexKeywords: bool
      useLexicalFilter: bool
      memoryCandidateChunks: int
      maxContextChunks: int
      realtimeMemoryTimeoutMs: int
      functionCallTimeoutMs: int
      autoWriteback: bool }

[<CLIMutable>]
type PlugInSettingsField =
    { key: string
      label: string
      kind: string
      defaultValue: string option
      description: string option }

[<CLIMutable>]
type PlugInDefinition =
    { id: string
      version: string
      displayName: string
      description: string option
      profile: QaPlugInProfile
      prompts: PromptSet
      models: Map<ModelRole, ModelRoleConfig>
      runtime: PlugInRuntimeOptions
      packagedContexts: QaPlugInContextManifest list
      settingsFacets: PlugInSettingsField list }

type PlugInHostContext =
    { storageRoot: string
      packageRoot: string option
      settings: Map<string, string>
      report: string -> unit }

type IQaPlugIn =
    abstract ContractVersion: int
    abstract Definition: PlugInDefinition
    abstract GetToolProviders: unit -> IQaToolProvider list
    abstract GetContextProviders: PlugInHostContext -> IQaContextProvider list

module ModelRole =
    let storageName role =
        match role with
        | Realtime -> "Realtime"
        | Transcriber -> "Transcriber"
        | Answer -> "Answer"
        | Keyword -> "Keyword"
        | QueryExpansion -> "QueryExpansion"
        | VisualDescription -> "VisualDescription"

    let all =
        [ Realtime; Transcriber; Answer; Keyword; QueryExpansion; VisualDescription ]

    let tryParse value =
        let normalized =
            (defaultArg (Option.ofObj value) "").Trim().Replace("-", "").Replace("_", "")

        all
        |> List.tryFind (fun role -> String.Equals(storageName role, normalized, StringComparison.OrdinalIgnoreCase))

module ModelRoleConfig =
    let create modelId =
        { provider = "openai"
          modelId = modelId
          maxOutputTokens = None
          temperature = None
          reasoningEffort = None
          extra = Map.empty }

    let sanitize fallback (config: ModelRoleConfig) =
        { config with
            provider =
                config.provider
                |> Option.ofObj
                |> Option.bind (fun value ->
                    if String.IsNullOrWhiteSpace value then
                        None
                    else
                        Some(value.Trim()))
                |> Option.defaultValue fallback.provider
            modelId =
                config.modelId
                |> Option.ofObj
                |> Option.bind (fun value ->
                    if String.IsNullOrWhiteSpace value then
                        None
                    else
                        Some(value.Trim()))
                |> Option.defaultValue fallback.modelId
            maxOutputTokens =
                config.maxOutputTokens
                |> Option.bind (fun value -> if value > 0 then Some value else None)
                |> Option.orElse fallback.maxOutputTokens
            temperature = config.temperature |> Option.orElse fallback.temperature
            reasoningEffort = config.reasoningEffort |> Option.orElse fallback.reasoningEffort
            extra = config.extra |> Option.ofObj |> Option.defaultValue Map.empty }

module PromptSet =
    let empty =
        { realtimeInstructions = None
          transcriberPrompt = None
          answerSystem = None
          answerUserTemplate = None
          keywordInstruction = None
          speechResultInstruction = None }

    let private clean value =
        value
        |> Option.bind (fun text -> if String.IsNullOrWhiteSpace text then None else Some text)

    let sanitize (prompts: PromptSet) =
        { realtimeInstructions = clean prompts.realtimeInstructions
          transcriberPrompt = clean prompts.transcriberPrompt
          answerSystem = clean prompts.answerSystem
          answerUserTemplate = clean prompts.answerUserTemplate
          keywordInstruction = clean prompts.keywordInstruction
          speechResultInstruction = clean prompts.speechResultInstruction }

module PlugInRuntimeOptions =
    let defaults =
        { retrievalMode = FsColbertWithFallback
          enableQueryExpansion = false
          elaborateIndexKeywords = true
          useLexicalFilter = true
          memoryCandidateChunks = 14
          maxContextChunks = 12
          realtimeMemoryTimeoutMs = 1200
          functionCallTimeoutMs = 120000
          autoWriteback = true }

    let sanitize runtime =
        { runtime with
            memoryCandidateChunks = max 1 runtime.memoryCandidateChunks
            maxContextChunks = max 1 runtime.maxContextChunks
            realtimeMemoryTimeoutMs = max 100 runtime.realtimeMemoryTimeoutMs
            functionCallTimeoutMs = max 1000 runtime.functionCallTimeoutMs }

module QaPlugInProfile =
    let private notEmpty (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(value.Trim())

    let generic =
        { id = "generic"
          displayName = "Generic QA"
          description = Some "General-purpose knowledge and memory question answering."
          voiceReplacements = []
          queryExpansionRules = []
          keywordInstruction = None
          keywordHints = []
          answerSystemInstruction = None }

    let private jsonOptions =
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        options.AllowTrailingCommas <- true
        options.ReadCommentHandling <- JsonCommentHandling.Skip
        options

    let private cleanList values =
        values
        |> Option.ofObj
        |> Option.defaultValue []
        |> List.choose notEmpty
        |> List.distinctBy _.ToLowerInvariant()

    let sanitize (profile: QaPlugInProfile) =
        let id = profile.id |> notEmpty |> Option.defaultValue generic.id

        let displayName =
            profile.displayName |> notEmpty |> Option.defaultValue generic.displayName

        { profile with
            id = id
            displayName = displayName
            description = profile.description |> Option.bind notEmpty
            voiceReplacements =
                profile.voiceReplacements
                |> Option.ofObj
                |> Option.defaultValue []
                |> List.choose (fun replacement ->
                    match notEmpty replacement.pattern with
                    | None -> None
                    | Some pattern ->
                        Some
                            { pattern = pattern
                              replacement = replacement.replacement |> Option.ofObj |> Option.defaultValue "" })
            queryExpansionRules =
                profile.queryExpansionRules
                |> Option.ofObj
                |> Option.defaultValue []
                |> List.choose (fun rule ->
                    let triggers = cleanList rule.triggers
                    let terms = cleanList rule.terms

                    if List.isEmpty triggers || List.isEmpty terms then
                        None
                    else
                        Some { triggers = triggers; terms = terms })
            keywordInstruction = profile.keywordInstruction |> Option.bind notEmpty
            keywordHints = cleanList profile.keywordHints
            answerSystemInstruction = profile.answerSystemInstruction |> Option.bind notEmpty }

    let fromJsonText (json: string) =
        JsonSerializer.Deserialize<QaPlugInProfile>(json, jsonOptions)
        |> Option.ofObj
        |> Option.defaultValue generic
        |> sanitize

    let load path = File.ReadAllText path |> fromJsonText

    let tryLoad path =
        try
            if String.IsNullOrWhiteSpace path || not (File.Exists path) then
                None
            else
                Some(load path)
        with _ ->
            None

    let renderHints (profile: QaPlugInProfile) =
        let profile = sanitize profile

        if List.isEmpty profile.keywordHints then
            ""
        else
            profile.keywordHints |> String.concat ", "

    let fingerprint (profile: QaPlugInProfile) =
        let profile = sanitize profile

        let canonical =
            JsonSerializer.Serialize(
                {| id = profile.id
                   displayName = profile.displayName
                   description = profile.description
                   voiceReplacements = profile.voiceReplacements
                   queryExpansionRules = profile.queryExpansionRules
                   keywordInstruction = profile.keywordInstruction
                   keywordHints = profile.keywordHints
                   answerSystemInstruction = profile.answerSystemInstruction |},
                jsonOptions
            )

        use sha = SHA256.Create()
        let bytes = Encoding.UTF8.GetBytes canonical

        sha.ComputeHash bytes
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

module QaPlugInPackageManifest =
    let currentContractVersion = 1

    let private jsonOptions =
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        options.AllowTrailingCommas <- true
        options.ReadCommentHandling <- JsonCommentHandling.Skip
        options

    let private notEmpty (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(value.Trim())

    let empty id displayName =
        { contractVersion = currentContractVersion
          id = id
          version = "1.0.0"
          displayName = displayName
          description = None
          behaviorProfile =
            { QaPlugInProfile.generic with
                id = id
                displayName = displayName }
          contexts = []
          tools = []
          ui = [] }

    let sanitize (manifest: QaPlugInPackageManifest) =
        let profile =
            if isNull (box manifest.behaviorProfile) then
                QaPlugInProfile.generic
            else
                QaPlugInProfile.sanitize manifest.behaviorProfile

        { manifest with
            contractVersion =
                if manifest.contractVersion <= 0 then
                    currentContractVersion
                else
                    manifest.contractVersion
            id = profile.id
            displayName = profile.displayName
            version = manifest.version |> notEmpty |> Option.defaultValue "1.0.0"
            description = manifest.description |> Option.bind notEmpty
            behaviorProfile = profile
            contexts = manifest.contexts |> Option.ofObj |> Option.defaultValue []
            tools = manifest.tools |> Option.ofObj |> Option.defaultValue []
            ui = manifest.ui |> Option.ofObj |> Option.defaultValue [] }

    let fromJsonText (json: string) =
        JsonSerializer.Deserialize<QaPlugInPackageManifest>(json, jsonOptions)
        |> Option.ofObj
        |> Option.map sanitize

    let tryLoad path =
        try
            if String.IsNullOrWhiteSpace path || not (File.Exists path) then
                None
            else
                File.ReadAllText path |> fromJsonText
        with _ ->
            None

module DefaultPlugInPrompts =
    let realtimeInstructions =
        """You are FsVoice's low-latency spoken front-end for question answering.

Allowed direct actions:
- greet the user
- handle short rapport, repetition, or simple clarification
- ask a brief follow-up when the request is too vague to answer safely
- answer simple conversational turns directly
- never repeat the startup selected-document-count greeting after the opening greeting, unless the user explicitly asks what is selected

Tool use:
- For every user ask or question (even simple ones about time, weather, etc.), request, summary, comparison, current-info question, or follow-up - which can't be answered trivially from existing context - call QUERY_ORACLE.
- Default to selected sources for source-like requests. If the user asks about the paper, document, PDF, selected sources, source, abstract, section, introduction, conclusion, "this", or "it" in a source context, call QUERY_ORACLE before answering.
- For requests to summarize, explain, extract, find, list, quote, compare, or answer from selected source content, call QUERY_ORACLE. Examples that require QUERY_ORACLE include "summarize the abstract of the paper", "what does the introduction say", and "compare the two documents".
- Do not answer source-like requests from general knowledge or conversational memory. The oracle is responsible for checking selected sources and saying when there is not enough source evidence.
- Pass the user's request as the `question` argument.
- When you call QUERY_ORACLE, you may pass compact advisory hints only when they are clear from the user's words and recent conversation: turn_kind, topic_continuity, memory_action, needs_external_context, sensitive, and confidence. Use needs_external_context = true when the request appears to involve selected sources, documents, tools, current facts, app state, memory, comparison, or summary beyond casual chat. These are hints only; the backend validates them and decides memory/tool/oracle handling.
- Call QUERY_ORACLE silently. Do not say filler or status phrases before the call, such as "let me check", "one moment", "I'll look that up", or similar.
- Do not refuse a request just because it is not about documents or selected sources.
- Do not invent tool results, source details, page contents, citations, live values, or app state.

After QUERY_ORACLE returns:
- Treat the tool result as the authoritative answer.
- Speak the tool result naturally and briefly, without adding new facts or a status preface.
- If the tool says there is not enough information, say that plainly."""

    let transcriberPrompt =
        "Expect natural spoken question-answering conversation. Requests may involve a wide variety of questions or user asks"

    let answerSystem =
        "You are FsVoice's reusable QA backend. Answer from selected knowledge sources, durable memory, and tool observations when they are relevant. Treat tool observations as authoritative for current facts. Treat the user's question as an instruction over the provided evidence. Prefer matched source chunks whose heading, leading text, or local context directly matches the requested topic or section. Treat incidental or deep mentions of the same words as weaker evidence unless the surrounding context clearly answers the request. Do not invent citations, source details, tool results, or live values. If the selected sources and tools do not contain enough information, say that plainly. Keep answers concise and useful."

    let answerUserTemplate =
        "User question:\n{{question}}\n\nTyped durable memory:\n{{typedMemory}}\n\nTool observations:\n{{toolObservations}}\n\nSelected source inventory:\n{{sourceInventory}}\n\nMatched source context:\n{{sourceContext}}\n\nReturn only the answer."

    let speechResultInstruction =
        "Start directly with the provided oracle answer. Speak it naturally and briefly. Do not add a preface such as 'I found', 'It says', 'Let me check', or 'Based on the context'. Do not add facts, omit caveats, or reinterpret it. If the answer contains a refusal or uncertainty, preserve that."

module PlugInDefinition =
    let currentContractVersion = 2

    let defaultModels =
        [ Realtime,
          { ModelRoleConfig.create "gpt-realtime-2" with
              maxOutputTokens = None }
          Transcriber, ModelRoleConfig.create "gpt-4o-mini-transcribe"
          Answer,
          { ModelRoleConfig.create "gpt-5.5" with
              maxOutputTokens = Some QaDefaults.answerMaxOutputTokens }
          Keyword,
          { ModelRoleConfig.create QaDefaults.keywordModel with
              maxOutputTokens = Some 25000 }
          QueryExpansion, ModelRoleConfig.create "gpt-5-nano"
          VisualDescription, ModelRoleConfig.create "gpt-5-mini" ]
        |> Map.ofList

    let defaultPrompts =
        { PromptSet.empty with
            realtimeInstructions = Some DefaultPlugInPrompts.realtimeInstructions
            transcriberPrompt = Some DefaultPlugInPrompts.transcriberPrompt
            answerSystem = Some DefaultPlugInPrompts.answerSystem
            answerUserTemplate = Some DefaultPlugInPrompts.answerUserTemplate
            speechResultInstruction = Some DefaultPlugInPrompts.speechResultInstruction }

    let generic =
        { id = "generic"
          version = "1.0.0"
          displayName = "Generic QA"
          description = Some "General-purpose knowledge and memory question answering."
          profile = QaPlugInProfile.generic
          prompts = defaultPrompts
          models = defaultModels
          runtime = PlugInRuntimeOptions.defaults
          packagedContexts = []
          settingsFacets = [] }

    let model role definition =
        definition.models
        |> Option.ofObj
        |> Option.bind (Map.tryFind role)
        |> Option.orElse (defaultModels |> Map.tryFind role)
        |> Option.defaultValue (ModelRoleConfig.create "gpt-5-nano")

    let private notEmpty fallback value =
        value
        |> Option.ofObj
        |> Option.bind (fun text ->
            if String.IsNullOrWhiteSpace text then
                None
            else
                Some(text.Trim()))
        |> Option.defaultValue fallback

    let private cleanText value =
        value
        |> Option.ofObj
        |> Option.bind (fun text ->
            if String.IsNullOrWhiteSpace text then
                None
            else
                Some(text.Trim()))

    let private sanitizeSettingsFacet (field: PlugInSettingsField) =
        match cleanText field.key with
        | None -> None
        | Some key ->
            let label = cleanText field.label |> Option.defaultValue key
            let kind = cleanText field.kind |> Option.defaultValue "text"

            Some
                { key = key
                  label = label
                  kind = kind
                  defaultValue = field.defaultValue |> Option.bind cleanText
                  description = field.description |> Option.bind cleanText }

    let sanitize (definition: PlugInDefinition) =
        let profile =
            if isNull (box definition.profile) then
                QaPlugInProfile.generic
            else
                QaPlugInProfile.sanitize definition.profile

        let model role fallback =
            definition.models
            |> Option.ofObj
            |> Option.bind (Map.tryFind role)
            |> Option.map (ModelRoleConfig.sanitize fallback)
            |> Option.defaultValue fallback

        let models = defaultModels |> Map.map (fun role fallback -> model role fallback)

        let prompts =
            definition.prompts
            |> fun prompts -> if isNull (box prompts) then defaultPrompts else prompts
            |> PromptSet.sanitize

        { definition with
            id = profile.id
            version = notEmpty "1.0.0" definition.version
            displayName = profile.displayName
            description = definition.description |> Option.orElse profile.description
            profile = profile
            prompts = prompts
            models = models
            runtime =
                definition.runtime
                |> fun runtime ->
                    if isNull (box runtime) then
                        PlugInRuntimeOptions.defaults
                    else
                        runtime
                |> PlugInRuntimeOptions.sanitize
            packagedContexts = definition.packagedContexts |> Option.ofObj |> Option.defaultValue []
            settingsFacets =
                definition.settingsFacets
                |> Option.ofObj
                |> Option.defaultValue []
                |> List.choose (fun field ->
                    if isNull (box field) then
                        None
                    else
                        sanitizeSettingsFacet field)
                |> List.distinctBy (fun field -> field.key.ToLowerInvariant()) }

    let fingerprint definition =
        let definition = sanitize definition

        let modelRows =
            definition.models
            |> Map.toList
            |> List.map (fun (role, config) ->
                {| role = ModelRole.storageName role
                   provider = config.provider
                   modelId = config.modelId
                   maxOutputTokens = config.maxOutputTokens
                   temperature = config.temperature
                   reasoningEffort = config.reasoningEffort
                   extra = config.extra |})

        let canonical =
            let runtime =
                {| retrievalMode = RetrievalModes.toStorageValue definition.runtime.retrievalMode
                   enableQueryExpansion = definition.runtime.enableQueryExpansion
                   elaborateIndexKeywords = definition.runtime.elaborateIndexKeywords
                   useLexicalFilter = definition.runtime.useLexicalFilter
                   memoryCandidateChunks = definition.runtime.memoryCandidateChunks
                   maxContextChunks = definition.runtime.maxContextChunks
                   realtimeMemoryTimeoutMs = definition.runtime.realtimeMemoryTimeoutMs
                   functionCallTimeoutMs = definition.runtime.functionCallTimeoutMs
                   autoWriteback = definition.runtime.autoWriteback |}

            JsonSerializer.Serialize(
                {| id = definition.id
                   version = definition.version
                   displayName = definition.displayName
                   description = definition.description
                   profileHash = QaPlugInProfile.fingerprint definition.profile
                   prompts = definition.prompts
                   models = modelRows
                   runtime = runtime
                   packagedContexts = definition.packagedContexts
                   settingsFacets = definition.settingsFacets |}
            )

        use sha = SHA256.Create()
        let bytes = Encoding.UTF8.GetBytes canonical

        sha.ComputeHash bytes
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()
