namespace Speak2Docs

open System
open System.IO
open System.Text.Json
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

    let openAiKey () =
        Preferences.Default.Get(C.SETTINGS_OPENAI_KEY, "").Trim()

    let setOpenAiKey (value: string) =
        Preferences.Default.Set(C.SETTINGS_OPENAI_KEY, value.Trim())

    let acceptedTermsVersion () =
        Preferences.Default.Get(C.SETTINGS_ACCEPTED_TERMS_VERSION, "").Trim()

    let hasAcceptedCurrentTerms () =
        String.Equals(acceptedTermsVersion (), C.TERMS_VERSION, StringComparison.Ordinal)

    let setAcceptedTermsVersion (value: string) =
        Preferences.Default.Set(C.SETTINGS_ACCEPTED_TERMS_VERSION, value.Trim())

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

    let private setOracleModel (value: string) =
        let value =
            match Text.notEmpty value with
            | Some v -> v
            | None -> C.DEFAULT_ORACLE_MODEL

        Preferences.Default.Set(C.SETTINGS_ORACLE_MODEL, value)

    let private plugInScopedKey (plugInId: string) (suffix: string) =
        let id = plugInId |> Text.notEmpty |> Option.defaultValue "generic"
        $"FsVoice.PlugIns.{id}.{suffix}"

    let private legacyPlugInScopedKey (plugInId: string) (suffix: string) =
        let id = plugInId |> Text.notEmpty |> Option.defaultValue "generic"
        $"{C.LEGACY_PRODUCT_NAME}.PlugIns.{id}.{suffix}"

    let private contains (key: string) = Preferences.Default.ContainsKey(key)

    let private clamp minValue maxValue value = value |> max minValue |> min maxValue

    let normalizeAnswerMaxOutputTokens value =
        value |> clamp C.MIN_ANSWER_MAX_OUTPUT_TOKENS C.MAX_ANSWER_MAX_OUTPUT_TOKENS

    let private parseAnswerMaxOutputTokens fallback value =
        match Int32.TryParse(defaultArg (Option.ofObj value) "") with
        | true, parsed -> normalizeAnswerMaxOutputTokens parsed
        | false, _ -> fallback

    let private getScopedString (plugInId: string) (suffix: string) (legacyKey: string option) (fallback: string) =
        let key = plugInScopedKey plugInId suffix

        let legacyScopedKey = legacyPlugInScopedKey plugInId suffix

        if contains key then
            Preferences.Default.Get(key, fallback).Trim()
        elif contains legacyScopedKey then
            Preferences.Default.Get(legacyScopedKey, fallback).Trim()
        else
            match legacyKey with
            | Some legacy -> Preferences.Default.Get(legacy, fallback).Trim()
            | None -> fallback

    let private getScopedBool (plugInId: string) (suffix: string) (legacyKey: string option) (fallback: bool) =
        let key = plugInScopedKey plugInId suffix

        let legacyScopedKey = legacyPlugInScopedKey plugInId suffix

        if contains key then
            Preferences.Default.Get(key, fallback)
        elif contains legacyScopedKey then
            Preferences.Default.Get(legacyScopedKey, fallback)
        else
            match legacyKey with
            | Some legacy -> Preferences.Default.Get(legacy, fallback)
            | None -> fallback

    let modelRoleModelId plugInId role fallback =
        let legacy =
            match role with
            | FsVoice.QA.Answer -> Some C.LEGACY_SETTINGS_ORACLE_MODEL
            | _ -> None

        let suffix = $"Models.{FsVoice.QA.ModelRole.storageName role}.ModelId"

        getScopedString plugInId suffix legacy fallback
        |> Text.notEmpty
        |> Option.defaultValue fallback

    let setModelRoleModelId plugInId role value =
        let fallback =
            match role with
            | FsVoice.QA.Answer -> C.DEFAULT_ORACLE_MODEL
            | _ -> ""

        let value = value |> Text.notEmpty |> Option.defaultValue fallback

        let key =
            plugInScopedKey plugInId $"Models.{FsVoice.QA.ModelRole.storageName role}.ModelId"

        Preferences.Default.Set(key, value)

        if role = FsVoice.QA.Answer then
            setOracleModel value

    let activePlugInId () =
        if Preferences.Default.ContainsKey(C.SETTINGS_ACTIVE_PLUG_IN) then
            Preferences.Default.Get(C.SETTINGS_ACTIVE_PLUG_IN, "generic").Trim()
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_ACTIVE_PLUG_IN, "generic").Trim()

    let setActivePlugInId (value: string) =
        let value = value |> Text.notEmpty |> Option.defaultValue "generic"
        Preferences.Default.Set(C.SETTINGS_ACTIVE_PLUG_IN, value)

    let retrievalMode () =
        let key =
            if Preferences.Default.ContainsKey(C.SETTINGS_RETRIEVAL_MODE) then
                C.SETTINGS_RETRIEVAL_MODE
            else
                C.LEGACY_SETTINGS_RETRIEVAL_MODE

        Preferences.Default.Get(key, RetrievalModes.toStorageValue FsColbertWithFallback)
        |> RetrievalModes.ofStorageValue

    let setRetrievalMode mode =
        Preferences.Default.Set(C.SETTINGS_RETRIEVAL_MODE, RetrievalModes.toStorageValue mode)

    let plugInRetrievalMode plugInId fallback =
        let value =
            getScopedString
                plugInId
                "Runtime.RetrievalMode"
                (Some C.LEGACY_SETTINGS_RETRIEVAL_MODE)
                (RetrievalModes.toStorageValue fallback)

        RetrievalModes.ofStorageValue value

    let setPlugInRetrievalMode plugInId mode =
        Preferences.Default.Set(plugInScopedKey plugInId "Runtime.RetrievalMode", RetrievalModes.toStorageValue mode)
        setRetrievalMode mode

    let logExpansions () =
        if Preferences.Default.ContainsKey(C.SETTINGS_LOG_EXPANSIONS) then
            Preferences.Default.Get(C.SETTINGS_LOG_EXPANSIONS, false)
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_LOG_EXPANSIONS, false)

    let setLogExpansions value =
        Preferences.Default.Set(C.SETTINGS_LOG_EXPANSIONS, value)

    let logChunks () =
        if Preferences.Default.ContainsKey(C.SETTINGS_LOG_CHUNKS) then
            Preferences.Default.Get(C.SETTINGS_LOG_CHUNKS, false)
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_LOG_CHUNKS, false)

    let setLogChunks value =
        Preferences.Default.Set(C.SETTINGS_LOG_CHUNKS, value)

    let answerMaxOutputTokens () =
        let fallback = C.DEFAULT_ANSWER_MAX_OUTPUT_TOKENS

        if Preferences.Default.ContainsKey(C.SETTINGS_ANSWER_MAX_OUTPUT_TOKENS) then
            Preferences.Default.Get(C.SETTINGS_ANSWER_MAX_OUTPUT_TOKENS, fallback)
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_ANSWER_MAX_OUTPUT_TOKENS, fallback)
        |> normalizeAnswerMaxOutputTokens

    let setAnswerMaxOutputTokens value =
        let tokens = value |> parseAnswerMaxOutputTokens C.DEFAULT_ANSWER_MAX_OUTPUT_TOKENS
        Preferences.Default.Set(C.SETTINGS_ANSWER_MAX_OUTPUT_TOKENS, tokens)

    let useLexicalFilter () =
        if Preferences.Default.ContainsKey(C.SETTINGS_USE_LEXICAL_FILTER) then
            Preferences.Default.Get(C.SETTINGS_USE_LEXICAL_FILTER, true)
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_USE_LEXICAL_FILTER, true)

    let setUseLexicalFilter value =
        Preferences.Default.Set(C.SETTINGS_USE_LEXICAL_FILTER, value)

    let plugInUseLexicalFilter plugInId fallback =
        getScopedBool plugInId "Runtime.UseLexicalFilter" (Some C.LEGACY_SETTINGS_USE_LEXICAL_FILTER) fallback

    let setPlugInUseLexicalFilter plugInId value =
        Preferences.Default.Set(plugInScopedKey plugInId "Runtime.UseLexicalFilter", value)
        setUseLexicalFilter value

    let elaborateIndexKeywords () =
        if Preferences.Default.ContainsKey(C.SETTINGS_ELABORATE_INDEX_KEYWORDS) then
            Preferences.Default.Get(C.SETTINGS_ELABORATE_INDEX_KEYWORDS, true)
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_ELABORATE_INDEX_KEYWORDS, true)

    let setElaborateIndexKeywords value =
        Preferences.Default.Set(C.SETTINGS_ELABORATE_INDEX_KEYWORDS, value)

    let plugInElaborateIndexKeywords plugInId fallback =
        getScopedBool
            plugInId
            "Runtime.ElaborateIndexKeywords"
            (Some C.LEGACY_SETTINGS_ELABORATE_INDEX_KEYWORDS)
            fallback

    let setPlugInElaborateIndexKeywords plugInId value =
        Preferences.Default.Set(plugInScopedKey plugInId "Runtime.ElaborateIndexKeywords", value)
        setElaborateIndexKeywords value

    let useHybridPdfParsing () =
        if Preferences.Default.ContainsKey(C.SETTINGS_USE_HYBRID_PDF_PARSING) then
            Preferences.Default.Get(C.SETTINGS_USE_HYBRID_PDF_PARSING, true)
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_USE_HYBRID_PDF_PARSING, true)

    let setUseHybridPdfParsing value =
        Preferences.Default.Set(C.SETTINGS_USE_HYBRID_PDF_PARSING, value)

    let useLayoutAnalysis () =
        if Preferences.Default.ContainsKey(C.SETTINGS_USE_LAYOUT_ANALYSIS) then
            Preferences.Default.Get(C.SETTINGS_USE_LAYOUT_ANALYSIS, true)
        else
            Preferences.Default.Get(C.LEGACY_SETTINGS_USE_LAYOUT_ANALYSIS, true)

    let setUseLayoutAnalysis value =
        Preferences.Default.Set(C.SETTINGS_USE_LAYOUT_ANALYSIS, value)

    let plugInSetting plugInId key fallback =
        getScopedString plugInId $"Settings.{key}" None fallback

    let setPlugInSetting plugInId key value =
        Preferences.Default.Set(plugInScopedKey plugInId $"Settings.{key}", value)

    let plugInSettings plugInId (fields: FsVoice.QA.PlugInSettingsField list) =
        fields
        |> List.map (fun field ->
            let fallback = field.defaultValue |> Option.defaultValue ""
            field.key, plugInSetting plugInId field.key fallback)
        |> Map.ofList
