namespace Speak2Docs

open System
open System.IO
open System.Text.Json
open Microsoft.Maui.Devices
open Microsoft.Maui.Storage

module Settings =
    [<CLIMutable>]
    type PdfDocumentDto =
        { id: string
          kind: string
          displayName: string
          storedPath: string
          originalPath: string
          selected: bool
          status: string
          chunkCount: int
          error: string }

    let private fsVoiceFolder () =
        let path = Path.Combine(FileSystem.AppDataDirectory, C.PRODUCT_NAME)
        Directory.CreateDirectory(path) |> ignore
        path

    let private pdfLibraryPath () =
        Path.Combine(fsVoiceFolder (), "pdf-library.json")

    let pdfLibraryStoragePath () = pdfLibraryPath ()

    let private hiddenBuiltInSourcesPath () =
        Path.Combine(fsVoiceFolder (), "hidden-built-in-sources.json")

    let private documentsFolder () =
        Path.Combine(fsVoiceFolder (), "Documents")

    let private kindToString kind =
        match kind with
        | PdfFile -> "pdf"
        | MarkdownFile -> "markdown"
        | JsonFile -> "json"

    let private kindFromString value =
        match (defaultArg (Option.ofObj value) "").Trim().ToLowerInvariant() with
        | "markdown"
        | "md" -> MarkdownFile
        | "json"
        | "docling-json" -> JsonFile
        | _ -> PdfFile

    let private kindFromFileName (fileName: string) =
        match Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() with
        | "pdf" -> PdfFile
        | "json" -> JsonFile
        | _ -> MarkdownFile

    let private statusToString (status: PdfProcessingStatus) =
        match status with
        | Queued -> "queued"
        | Processing -> "processing"
        | Ready -> "ready"
        | Failed -> "failed"

    let private statusFromString value : PdfProcessingStatus =
        match (defaultArg (Option.ofObj value) "").Trim().ToLowerInvariant() with
        | "processing" -> Processing
        | "ready" -> Ready
        | "failed" -> Failed
        | _ -> Queued

    let private toDto (doc: PdfDocumentSource) : PdfDocumentDto =
        { id = doc.id
          kind = kindToString doc.kind
          displayName = doc.displayName
          storedPath = doc.storedPath
          originalPath = doc.originalPath
          selected = doc.selected
          status = statusToString doc.status
          chunkCount = doc.chunkCount
          error = defaultArg doc.error "" }

    let private isValidDto (dto: PdfDocumentDto) =
        Text.notEmpty dto.id |> Option.isSome
        && Text.notEmpty dto.displayName |> Option.isSome
        && Text.notEmpty dto.storedPath |> Option.isSome

    let private ofDto (dto: PdfDocumentDto) : PdfDocumentSource =
        let status = statusFromString dto.status

        let status, selected, error =
            match status with
            | Processing
            | Queued -> Failed, false, Some "Processing was interrupted. Tap retry."
            | Ready -> Ready, dto.selected, Text.notEmpty dto.error
            | Failed -> Failed, false, Text.notEmpty dto.error

        { id = dto.id
          kind = kindFromString dto.kind
          displayName = dto.displayName
          storedPath = dto.storedPath
          originalPath = dto.originalPath
          selected = selected
          status = status
          chunkCount = dto.chunkCount
          error = error }

    let private legacyOpenAiKey () =
        Preferences.Default.Get(C.SETTINGS_OPENAI_KEY, "").Trim()

    let openAiKey () =
        let legacy = legacyOpenAiKey ()

        try
            match
                SecureStorage.Default.GetAsync(C.SETTINGS_OPENAI_KEY).GetAwaiter().GetResult()
                |> Option.ofObj
                |> Option.bind Text.notEmpty
            with
            | Some key ->
                if Text.notEmpty legacy |> Option.isSome then
                    Preferences.Default.Remove(C.SETTINGS_OPENAI_KEY)

                key
            | None ->
                match Text.notEmpty legacy with
                | Some key ->
                    SecureStorage.Default.SetAsync(C.SETTINGS_OPENAI_KEY, key).GetAwaiter().GetResult()
                    Preferences.Default.Remove(C.SETTINGS_OPENAI_KEY)
                    key
                | None -> ""
        with _ ->
            legacy

    let setOpenAiKey (value: string) =
        try
            let value = value.Trim()

            if String.IsNullOrWhiteSpace value then
                SecureStorage.Default.Remove(C.SETTINGS_OPENAI_KEY) |> ignore
            else
                SecureStorage.Default.SetAsync(C.SETTINGS_OPENAI_KEY, value).GetAwaiter().GetResult()

            Preferences.Default.Remove(C.SETTINGS_OPENAI_KEY)
            Ok "secure storage"
        with ex ->
            Error ex.Message

    let acceptedTermsVersion () =
        Preferences.Default.Get(C.SETTINGS_ACCEPTED_TERMS_VERSION, "").Trim()

    let hasAcceptedCurrentTerms () =
        String.Equals(acceptedTermsVersion (), C.TERMS_VERSION, StringComparison.Ordinal)

    let setAcceptedTermsVersion (value: string) =
        Preferences.Default.Set(C.SETTINGS_ACCEPTED_TERMS_VERSION, value.Trim())

    let suppressedOpenAiDataDisclosureVersion () =
        Preferences.Default.Get(C.SETTINGS_SUPPRESS_OPENAI_DATA_DISCLOSURE_VERSION, "").Trim()

    let shouldSuppressOpenAiDataDisclosure () =
        String.Equals(
            suppressedOpenAiDataDisclosureVersion (),
            C.OPENAI_DATA_DISCLOSURE_VERSION,
            StringComparison.Ordinal
        )

    let setSuppressOpenAiDataDisclosureVersion (value: string) =
        Preferences.Default.Set(C.SETTINGS_SUPPRESS_OPENAI_DATA_DISCLOSURE_VERSION, value.Trim())

    let clearSuppressOpenAiDataDisclosureVersion () =
        Preferences.Default.Remove(C.SETTINGS_SUPPRESS_OPENAI_DATA_DISCLOSURE_VERSION)

    let private deserializePdfLibrary json =
        if String.IsNullOrWhiteSpace json then
            None
        else
            try
                JsonSerializer.Deserialize<PdfDocumentDto array>(json)
                |> Option.ofObj
                |> Option.bind (fun dtos ->
                    if dtos |> Array.forall isValidDto then
                        Some(dtos |> Array.toList |> List.map ofDto)
                    else
                        None)
            with _ ->
                None

    let private deserializeStringSet json =
        if String.IsNullOrWhiteSpace json then
            Set.empty
        else
            try
                JsonSerializer.Deserialize<string array>(json)
                |> Option.ofObj
                |> Option.map (Array.choose PdfDocuments.normalizeBuiltInOriginalPath >> Set.ofArray)
                |> Option.defaultValue Set.empty
            with _ ->
                Set.empty

    let private tryReadPdfLibraryFile () =
        try
            let path = pdfLibraryPath ()

            if File.Exists path then
                File.ReadAllText path |> deserializePdfLibrary
            else
                None
        with _ ->
            None

    let private tryWritePdfLibraryFile (json: string) =
        try
            let path = pdfLibraryPath ()
            let tempPath = $"{path}.{Guid.NewGuid():N}.tmp"

            File.WriteAllText(tempPath, json)

            if File.Exists path then
                File.Replace(tempPath, path, null)
            else
                File.Move(tempPath, path)

            let savedJson = File.ReadAllText path

            if savedJson = json then
                Ok $"{path} ({json.Length} byte(s))"
            else
                Error
                    $"Read-back verification failed for {path}: expected {json.Length} byte(s), found {savedJson.Length} byte(s)."
        with ex ->
            try
                for tempPath in Directory.EnumerateFiles(fsVoiceFolder (), "pdf-library.json.*.tmp") do
                    File.Delete tempPath
            with _ ->
                ()

            Error ex.Message

    let private tryReadHiddenBuiltInSourcesFile () =
        try
            let path = hiddenBuiltInSourcesPath ()

            if File.Exists path then
                File.ReadAllText path |> deserializeStringSet
            else
                Set.empty
        with _ ->
            Set.empty

    let private tryWriteHiddenBuiltInSourcesFile (json: string) =
        try
            let path = hiddenBuiltInSourcesPath ()
            let tempPath = $"{path}.{Guid.NewGuid():N}.tmp"

            File.WriteAllText(tempPath, json)

            if File.Exists path then
                File.Replace(tempPath, path, null)
            else
                File.Move(tempPath, path)

            Ok path
        with ex ->
            try
                for tempPath in Directory.EnumerateFiles(fsVoiceFolder (), "hidden-built-in-sources.json.*.tmp") do
                    File.Delete tempPath
            with _ ->
                ()

            Error ex.Message

    let private recoverStoredDocuments () =
        try
            let folder = documentsFolder ()

            if Directory.Exists folder then
                Directory.EnumerateFiles folder
                |> Seq.map (fun path ->
                    let storedName = Path.GetFileName path

                    let id, displayName =
                        if storedName.Length > 33 && storedName.[32] = '-' then
                            storedName.Substring(0, 32), storedName.Substring(33)
                        else
                            storedName, storedName

                    ({ id = id
                       kind = kindFromFileName displayName
                       displayName = displayName
                       storedPath = path
                       originalPath = path
                       selected = true
                       status = Ready
                       chunkCount = 0
                       error = None }
                    : PdfDocumentSource))
                |> Seq.toList
            else
                []
        with _ ->
            []

    let pdfLibrary () : PdfDocumentSource list =
        match tryReadPdfLibraryFile () with
        | Some docs -> docs
        | None ->
            let json = Preferences.Default.Get(C.SETTINGS_PDF_LIBRARY, "")

            match deserializePdfLibrary json with
            | Some docs ->
                tryWritePdfLibraryFile json |> ignore
                docs
            | None ->
                let docs = recoverStoredDocuments ()

                if not (List.isEmpty docs) then
                    let json = docs |> List.map toDto |> List.toArray |> JsonSerializer.Serialize
                    tryWritePdfLibraryFile json |> ignore

                docs

    let setPdfLibrary (docs: PdfDocumentSource list) : Result<string, string> =
        let json = docs |> List.map toDto |> List.toArray |> JsonSerializer.Serialize

        Preferences.Default.Set(C.SETTINGS_PDF_LIBRARY, json)
        tryWritePdfLibraryFile json

    let hiddenBuiltInSources () =
        let stored =
            Preferences.Default.Get(C.SETTINGS_HIDDEN_BUILT_IN_SOURCES, "")
            |> deserializeStringSet

        Set.union stored (tryReadHiddenBuiltInSourcesFile ())

    let setHiddenBuiltInSources values =
        let normalized =
            values |> Seq.choose PdfDocuments.normalizeBuiltInOriginalPath |> Set.ofSeq

        let json = normalized |> Set.toArray |> JsonSerializer.Serialize
        Preferences.Default.Set(C.SETTINGS_HIDDEN_BUILT_IN_SOURCES, json)

        match tryWriteHiddenBuiltInSourcesFile json with
        | Ok path -> Ok path
        | Error _ -> Ok "preferences"

    let addHiddenBuiltInSource value =
        match PdfDocuments.normalizeBuiltInOriginalPath value with
        | None -> Error "The source is not a built-in Speak2Docs source."
        | Some source -> hiddenBuiltInSources () |> Set.add source |> setHiddenBuiltInSources

    let clearHiddenBuiltInSources () =
        Preferences.Default.Remove(C.SETTINGS_HIDDEN_BUILT_IN_SOURCES)

        try
            let path = hiddenBuiltInSourcesPath ()

            if File.Exists path then
                File.Delete path
        with _ ->
            ()

    let private plugInScopedKey (plugInId: string) (suffix: string) =
        let id = plugInId |> Text.notEmpty |> Option.defaultValue "generic"
        $"FsVoice.PlugIns.{id}.{suffix}"

    let private clamp minValue maxValue value = value |> max minValue |> min maxValue

    let normalizeAnswerMaxOutputTokens value =
        value |> clamp C.MIN_ANSWER_MAX_OUTPUT_TOKENS C.MAX_ANSWER_MAX_OUTPUT_TOKENS

    let normalizeAnswerToolCallLoopLimit value =
        value
        |> clamp C.MIN_ANSWER_TOOL_CALL_LOOP_LIMIT C.MAX_ANSWER_TOOL_CALL_LOOP_LIMIT

    let normalizeMaxContextChunks value =
        value |> clamp C.MIN_MAX_CONTEXT_CHUNKS C.MAX_MAX_CONTEXT_CHUNKS

    let normalizeAnswerReasoningEffort value =
        RuntimeSettings.normalizeAnswerReasoningEffort value

    let private parseAnswerMaxOutputTokens fallback value =
        match Int32.TryParse(defaultArg (Option.ofObj value) "") with
        | true, parsed -> normalizeAnswerMaxOutputTokens parsed
        | false, _ -> fallback

    let private parseAnswerToolCallLoopLimit fallback value =
        match Int32.TryParse(defaultArg (Option.ofObj value) "") with
        | true, parsed -> normalizeAnswerToolCallLoopLimit parsed
        | false, _ -> fallback

    let private parseMaxContextChunks fallback value =
        match Int32.TryParse(defaultArg (Option.ofObj value) "") with
        | true, parsed -> normalizeMaxContextChunks parsed
        | false, _ -> fallback

    let private getScopedString (plugInId: string) (suffix: string) (fallback: string) =
        let key = plugInScopedKey plugInId suffix
        Preferences.Default.Get(key, fallback).Trim()

    let private getScopedBool (plugInId: string) (suffix: string) (fallback: bool) =
        let key = plugInScopedKey plugInId suffix
        Preferences.Default.Get(key, fallback)

    let modelRoleModelId plugInId role fallback =
        let suffix = $"Models.{FsVoice.Ctx.ModelRole.storageName role}.ModelId"

        getScopedString plugInId suffix fallback
        |> Text.notEmpty
        |> Option.defaultValue fallback

    let setModelRoleModelId plugInId role value =
        let fallback =
            match role with
            | FsVoice.Ctx.Answer -> C.DEFAULT_ORACLE_MODEL
            | FsVoice.Ctx.Keyword -> C.DEFAULT_INDEX_ENRICHMENT_MODEL
            | FsVoice.Ctx.VisualDescription -> C.DEFAULT_VISUAL_DESCRIPTION_MODEL
            | _ -> ""

        let value = value |> Text.notEmpty |> Option.defaultValue fallback

        let key =
            plugInScopedKey plugInId $"Models.{FsVoice.Ctx.ModelRole.storageName role}.ModelId"

        Preferences.Default.Set(key, value)

    let activePlugInId () =
        Preferences.Default.Get(C.SETTINGS_ACTIVE_PLUG_IN, "generic").Trim()

    let setActivePlugInId (value: string) =
        let value = value |> Text.notEmpty |> Option.defaultValue "generic"
        Preferences.Default.Set(C.SETTINGS_ACTIVE_PLUG_IN, value)

    let retrievalMode () =
        Preferences.Default.Get(C.SETTINGS_RETRIEVAL_MODE, RetrievalModes.toStorageValue FsColbertWithFallback)
        |> RetrievalModes.ofStorageValue

    let setRetrievalMode mode =
        Preferences.Default.Set(C.SETTINGS_RETRIEVAL_MODE, RetrievalModes.toStorageValue mode)

    let plugInRetrievalMode plugInId fallback =
        let value =
            getScopedString plugInId "Runtime.RetrievalMode" (RetrievalModes.toStorageValue fallback)

        RetrievalModes.ofStorageValue value

    let setPlugInRetrievalMode plugInId mode =
        Preferences.Default.Set(plugInScopedKey plugInId "Runtime.RetrievalMode", RetrievalModes.toStorageValue mode)
        setRetrievalMode mode

    let logExpansions () =
        Preferences.Default.Get(C.SETTINGS_LOG_EXPANSIONS, false)

    let setLogExpansions value =
        Preferences.Default.Set(C.SETTINGS_LOG_EXPANSIONS, value)

    let logChunks () =
        Preferences.Default.Get(C.SETTINGS_LOG_CHUNKS, false)

    let setLogChunks value =
        Preferences.Default.Set(C.SETTINGS_LOG_CHUNKS, value)

    let activityLogVerbosity () =
        Preferences.Default.Get(C.SETTINGS_ACTIVITY_LOG_LEVEL, ActivityLog.toStorageValue Informational)
        |> ActivityLog.ofStorageValue

    let setActivityLogVerbosity value =
        Preferences.Default.Set(C.SETTINGS_ACTIVITY_LOG_LEVEL, ActivityLog.toStorageValue value)

    let applyPlatformMigrations () =
        if DeviceInfo.Current.Platform = DevicePlatform.iOS then
            let migrated =
                Preferences.Default.Get(C.SETTINGS_IOS_RECEIVER_AUDIO_ROUTE_MIGRATED, false)

            if not migrated then
                Preferences.Default.Set(C.SETTINGS_AUDIO_DEFAULT_TO_SPEAKER, false)
                Preferences.Default.Set(C.SETTINGS_IOS_RECEIVER_AUDIO_ROUTE_MIGRATED, true)

            let speakerMigrated =
                Preferences.Default.Get(C.SETTINGS_IOS_SPEAKER_AUDIO_ROUTE_MIGRATED, false)

            if not speakerMigrated then
                Preferences.Default.Set(C.SETTINGS_AUDIO_DEFAULT_TO_SPEAKER, true)
                Preferences.Default.Set(C.SETTINGS_IOS_SPEAKER_AUDIO_ROUTE_MIGRATED, true)

    let defaultAudioDefaultToSpeaker () =
#if ANDROID || IOS
        true
#else
        false
#endif

    let audioDefaultToSpeaker () =
        Preferences.Default.Get(C.SETTINGS_AUDIO_DEFAULT_TO_SPEAKER, defaultAudioDefaultToSpeaker ())

    let setAudioDefaultToSpeaker value =
        Preferences.Default.Set(C.SETTINGS_AUDIO_DEFAULT_TO_SPEAKER, value)

    let answerMaxOutputTokens () =
        let fallback = C.DEFAULT_ANSWER_MAX_OUTPUT_TOKENS

        Preferences.Default.Get(C.SETTINGS_ANSWER_MAX_OUTPUT_TOKENS, fallback)
        |> normalizeAnswerMaxOutputTokens

    let setAnswerMaxOutputTokens value =
        let tokens = value |> parseAnswerMaxOutputTokens C.DEFAULT_ANSWER_MAX_OUTPUT_TOKENS
        Preferences.Default.Set(C.SETTINGS_ANSWER_MAX_OUTPUT_TOKENS, tokens)

    let answerReasoningEffort () =
        Preferences.Default.Get(C.SETTINGS_ANSWER_REASONING_EFFORT, C.DEFAULT_ANSWER_REASONING_EFFORT)
        |> normalizeAnswerReasoningEffort

    let setAnswerReasoningEffort value =
        Preferences.Default.Set(C.SETTINGS_ANSWER_REASONING_EFFORT, normalizeAnswerReasoningEffort value)

    let answerToolCallLoopLimit () =
        let fallback = C.DEFAULT_ANSWER_TOOL_CALL_LOOP_LIMIT

        Preferences.Default.Get(C.SETTINGS_ANSWER_TOOL_CALL_LOOP_LIMIT, fallback)
        |> normalizeAnswerToolCallLoopLimit

    let setAnswerToolCallLoopLimit value =
        let rounds =
            value |> parseAnswerToolCallLoopLimit C.DEFAULT_ANSWER_TOOL_CALL_LOOP_LIMIT

        Preferences.Default.Set(C.SETTINGS_ANSWER_TOOL_CALL_LOOP_LIMIT, rounds)

    let maxContextChunks () =
        let fallback = C.DEFAULT_MAX_CONTEXT_CHUNKS

        Preferences.Default.Get(C.SETTINGS_MAX_CONTEXT_CHUNKS, fallback)
        |> normalizeMaxContextChunks

    let setMaxContextChunks value =
        let chunks = value |> parseMaxContextChunks C.DEFAULT_MAX_CONTEXT_CHUNKS
        Preferences.Default.Set(C.SETTINGS_MAX_CONTEXT_CHUNKS, chunks)

    let useLexicalFilter () =
        Preferences.Default.Get(C.SETTINGS_USE_LEXICAL_FILTER, true)

    let setUseLexicalFilter value =
        Preferences.Default.Set(C.SETTINGS_USE_LEXICAL_FILTER, value)

    let plugInUseLexicalFilter plugInId fallback =
        getScopedBool plugInId "Runtime.UseLexicalFilter" fallback

    let setPlugInUseLexicalFilter plugInId value =
        Preferences.Default.Set(plugInScopedKey plugInId "Runtime.UseLexicalFilter", value)
        setUseLexicalFilter value

    let elaborateIndexKeywords () =
        Preferences.Default.Get(C.SETTINGS_ELABORATE_INDEX_KEYWORDS, false)

    let setElaborateIndexKeywords value =
        Preferences.Default.Set(C.SETTINGS_ELABORATE_INDEX_KEYWORDS, value)

    let plugInElaborateIndexKeywords plugInId fallback =
        getScopedBool plugInId "Runtime.ElaborateIndexKeywords" fallback

    let setPlugInElaborateIndexKeywords plugInId value =
        Preferences.Default.Set(plugInScopedKey plugInId "Runtime.ElaborateIndexKeywords", value)
        setElaborateIndexKeywords value

    let useHybridPdfParsing () = true

    let setUseHybridPdfParsing _ =
        Preferences.Default.Set(C.SETTINGS_USE_HYBRID_PDF_PARSING, true)

    let useLayoutAnalysis () =
        Preferences.Default.Get(C.SETTINGS_USE_LAYOUT_ANALYSIS, true)

    let setUseLayoutAnalysis value =
        Preferences.Default.Set(C.SETTINGS_USE_LAYOUT_ANALYSIS, value)

    let describePdfVisuals () =
        Preferences.Default.Get(C.SETTINGS_DESCRIBE_PDF_VISUALS, false)

    let setDescribePdfVisuals value =
        Preferences.Default.Set(C.SETTINGS_DESCRIBE_PDF_VISUALS, value)

    let plugInSetting plugInId key fallback =
        getScopedString plugInId $"Settings.{key}" fallback

    let setPlugInSetting plugInId key value =
        Preferences.Default.Set(plugInScopedKey plugInId $"Settings.{key}", value)

    let plugInSettings plugInId (fields: FsVoice.Ctx.PlugInSettingsField list) =
        fields
        |> List.map (fun field ->
            let fallback = field.defaultValue |> Option.defaultValue ""
            field.key, plugInSetting plugInId field.key fallback)
        |> Map.ofList
