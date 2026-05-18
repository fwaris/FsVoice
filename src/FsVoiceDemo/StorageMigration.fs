namespace FsVoiceDemo

open System
open System.IO
open Microsoft.Maui.Storage

module StorageMigration =
    let private knownStringPreferences =
        [ C.LEGACY_SETTINGS_OPENAI_KEY, C.SETTINGS_OPENAI_KEY
          C.LEGACY_SETTINGS_ORACLE_MODEL, C.SETTINGS_ORACLE_MODEL
          C.LEGACY_SETTINGS_RETRIEVAL_MODE, C.SETTINGS_RETRIEVAL_MODE
          C.LEGACY_SETTINGS_ACTIVE_PLUG_IN, C.SETTINGS_ACTIVE_PLUG_IN ]

    let private knownBoolPreferences =
        [ C.LEGACY_SETTINGS_LOG_EXPANSIONS, C.SETTINGS_LOG_EXPANSIONS
          C.LEGACY_SETTINGS_LOG_CHUNKS, C.SETTINGS_LOG_CHUNKS
          C.LEGACY_SETTINGS_USE_LEXICAL_FILTER, C.SETTINGS_USE_LEXICAL_FILTER
          C.LEGACY_SETTINGS_ELABORATE_INDEX_KEYWORDS, C.SETTINGS_ELABORATE_INDEX_KEYWORDS
          C.LEGACY_SETTINGS_USE_HYBRID_PDF_PARSING, C.SETTINGS_USE_HYBRID_PDF_PARSING
          C.LEGACY_SETTINGS_USE_LAYOUT_ANALYSIS, C.SETTINGS_USE_LAYOUT_ANALYSIS ]

    let private copyFileIfMissing sourcePath targetPath =
        if File.Exists sourcePath && not (File.Exists targetPath) then
            Directory.CreateDirectory(Path.GetDirectoryName targetPath) |> ignore
            File.Copy(sourcePath, targetPath, false)

    let private copyDirectoryIfMissing sourceRoot targetRoot =
        if Directory.Exists sourceRoot && not (Directory.Exists targetRoot) then
            for sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories) do
                let relativePath = Path.GetRelativePath(sourceRoot, sourcePath)
                let targetPath = Path.Combine(targetRoot, relativePath)
                copyFileIfMissing sourcePath targetPath

    let private rewriteLibraryPaths oldRoot newRoot =
        let libraryPath = Path.Combine(newRoot, "pdf-library.json")

        if File.Exists libraryPath then
            let json = File.ReadAllText libraryPath
            let updated = json.Replace(oldRoot, newRoot)

            if not (String.Equals(json, updated, StringComparison.Ordinal)) then
                File.WriteAllText(libraryPath, updated)

                if not (Preferences.Default.ContainsKey(C.SETTINGS_PDF_LIBRARY)) then
                    Preferences.Default.Set(C.SETTINGS_PDF_LIBRARY, updated)

    let private migrateStringPreference (legacyKey, newKey) =
        if
            Preferences.Default.ContainsKey(legacyKey)
            && not (Preferences.Default.ContainsKey(newKey))
        then
            Preferences.Default.Set(newKey, Preferences.Default.Get(legacyKey, ""))

    let private migratePdfLibraryPreference (oldRoot: string) (newRoot: string) =
        if
            Preferences.Default.ContainsKey(C.LEGACY_SETTINGS_PDF_LIBRARY)
            && not (Preferences.Default.ContainsKey(C.SETTINGS_PDF_LIBRARY))
        then
            let json = Preferences.Default.Get(C.LEGACY_SETTINGS_PDF_LIBRARY, "")
            Preferences.Default.Set(C.SETTINGS_PDF_LIBRARY, json.Replace(oldRoot, newRoot))

    let private migrateBoolPreference (legacyKey, newKey) =
        if
            Preferences.Default.ContainsKey(legacyKey)
            && not (Preferences.Default.ContainsKey(newKey))
        then
            Preferences.Default.Set(newKey, Preferences.Default.Get(legacyKey, false))

    let migrateFromLegacyProduct () =
        try
            let oldRoot = Path.Combine(FileSystem.AppDataDirectory, C.LEGACY_PRODUCT_NAME)
            let newRoot = Path.Combine(FileSystem.AppDataDirectory, C.PRODUCT_NAME)

            if Directory.Exists oldRoot then
                copyDirectoryIfMissing oldRoot newRoot
                rewriteLibraryPaths oldRoot newRoot

            migratePdfLibraryPreference oldRoot newRoot
            knownStringPreferences |> List.iter migrateStringPreference
            knownBoolPreferences |> List.iter migrateBoolPreference
        with ex ->
            Console.Error.WriteLine($"FsVoice storage migration skipped: {ex.Message}")
