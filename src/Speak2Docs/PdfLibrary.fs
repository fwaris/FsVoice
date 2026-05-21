namespace Speak2Docs

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Threading
open Speak2Docs.WorkFlow
open Microsoft.Maui.Storage

module PdfLibrary =
    [<CLIMutable>]
    type PrebuiltKnowledgeAsset =
        { id: string
          kind: string
          displayName: string
          documentAsset: string
          indexAsset: string
          selected: bool }

    [<CLIMutable>]
    type InstalledPrebuiltIndex =
        { id: string
          kind: string
          displayName: string
          storedPath: string
          indexPath: string }

    let private folder () =
        let path = Path.Combine(FileSystem.AppDataDirectory, C.PRODUCT_NAME, "Documents")
        Directory.CreateDirectory(path) |> ignore
        path

    let private prebuiltManifestAsset = "FsColbertIndexes/prebuilt-indexes.json"

    let private prebuiltBundleManifestAsset = "FsColbertIndexes/index-bundle.json"

    let private sanitizeFileName (name: string) =
        let invalid = Path.GetInvalidFileNameChars() |> Set.ofArray

        name.ToCharArray()
        |> Array.map (fun c -> if invalid.Contains c then '_' else c)
        |> String

    let private safeId (value: string) =
        let value = defaultArg (Option.ofObj value) ""

        let cleaned =
            value.ToCharArray()
            |> Array.map (fun c ->
                if Char.IsLetterOrDigit c || c = '-' || c = '_' then
                    c
                else
                    '-')
            |> String

        match Text.notEmpty cleaned with
        | Some id -> id
        | None -> Guid.NewGuid().ToString("N")

    let private kindFromFileName (fileName: string) =
        match Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() with
        | "pdf" -> PdfFile
        | "md" -> MarkdownFile
        | "json" -> JsonFile
        | _ -> MarkdownFile

    let private qaSourceKind kind =
        match kind with
        | PdfFile -> FsVoice.QA.KnowledgeSourceKind.Pdf
        | MarkdownFile -> FsVoice.QA.KnowledgeSourceKind.Markdown
        | JsonFile -> FsVoice.QA.KnowledgeSourceKind.Json

    let private readJsonPassages source path =
        async {
            try
                match FsColbert.DoclingJson.tryDeserialize (File.ReadAllText path) with
                | Ok document ->
                    return
                        Ok(FsColbert.DoclingPassages.toPassages FsColbert.ChunkOptions.fsKameDefaults source document)
                | Error err -> return Error $"Unable to read JSON knowledge source '{path}': {err}"
            with ex ->
                return Error $"Unable to read JSON knowledge source '{path}': {ex.Message}"
        }

    let private pdfParsingMode useHybridPdfParsing useLayoutAnalysis =
        if useHybridPdfParsing && useLayoutAnalysis then
            FsVoice.QA.KnowledgeSources.PdfParsingMode.Hybrid
        elif useHybridPdfParsing then
            FsVoice.QA.KnowledgeSources.PdfParsingMode.HybridWithoutLayout
        else
            FsVoice.QA.KnowledgeSources.PdfParsingMode.Legacy

    let private readPassages report useHybridPdfParsing useLayoutAnalysis (doc: PdfDocumentSource) cancellationToken =
        let source: FsVoice.QA.KnowledgeSource =
            { FsVoice.QA.KnowledgeSource.kind = qaSourceKind doc.kind
              location = doc.storedPath
              enabled = true }

        KnowledgeSources.configurePdfParser useLayoutAnalysis

        FsVoice.QA.KnowledgeSources.loadPassagesForIndexingWithCancellation
            FileSystem.AppDataDirectory
            report
            (pdfParsingMode useHybridPdfParsing useLayoutAnalysis)
            source
            cancellationToken

    let private throwIfKeywordCancellationRequested (options: FsVoice.QA.KnowledgeSources.KeywordGenerationOptions) =
        options.cancellationToken |> Option.iter _.ThrowIfCancellationRequested()

    let private hasExisting (docs: PdfDocumentSource list) originalPath fileName =
        docs
        |> List.exists (fun doc ->
            String.Equals(doc.originalPath, originalPath, StringComparison.OrdinalIgnoreCase)
            || String.Equals(doc.displayName, fileName, StringComparison.OrdinalIgnoreCase))

    let private copyPickedFile (result: FileResult) =
        async {
            let id = Guid.NewGuid().ToString("N")
            let fileName = sanitizeFileName result.FileName
            let storedName = $"{id}-{fileName}"
            let storedPath = Path.Combine(folder (), storedName)

            use! source = result.OpenReadAsync() |> Async.AwaitTask
            use target = File.Create(storedPath)
            do! source.CopyToAsync(target) |> Async.AwaitTask

            return
                { id = id
                  kind = kindFromFileName fileName
                  displayName = fileName
                  storedPath = storedPath
                  originalPath = defaultArg (Text.notEmpty result.FullPath) result.FileName
                  selected = false
                  status = Processing
                  chunkCount = 0
                  error = None }
        }

    let private tryOpenPackageFile logicalName =
        async {
            try
                let! stream = FileSystem.OpenAppPackageFileAsync(logicalName) |> Async.AwaitTask
                return Some stream
            with _ ->
                return None
        }

    let private copyPackageFile logicalName path =
        async {
            match! tryOpenPackageFile logicalName with
            | None -> return false
            | Some source ->
                use source = source

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path))
                |> ignore

                use target = File.Create path
                do! source.CopyToAsync(target) |> Async.AwaitTask
                return true
        }

    let private readPrebuiltManifest () =
        async {
            match! tryOpenPackageFile prebuiltManifestAsset with
            | None -> return []
            | Some stream ->
                use stream = stream
                use reader = new StreamReader(stream)
                let! json = reader.ReadToEndAsync() |> Async.AwaitTask

                if String.IsNullOrWhiteSpace json then
                    return []
                else
                    try
                        return
                            JsonSerializer.Deserialize<PrebuiltKnowledgeAsset array>(json)
                            |> Option.ofObj
                            |> Option.map Array.toList
                            |> Option.defaultValue []
                    with _ ->
                        return []
        }

    let private readPrebuiltBundleManifest () : Async<FsColbert.IndexBundleManifest option> =
        async {
            match! tryOpenPackageFile prebuiltBundleManifestAsset with
            | None -> return None
            | Some stream ->
                use stream = stream
                use reader = new StreamReader(stream)
                let! json = reader.ReadToEndAsync() |> Async.AwaitTask

                if String.IsNullOrWhiteSpace json then
                    return None
                else
                    return
                        match FsColbert.IndexBundle.tryDeserialize json with
                        | Ok manifest -> Some manifest
                        | Error _ -> None
        }

    let private packageIndexAssetPath (path: string) =
        let value = (defaultArg (Option.ofObj path) "").Replace('\\', '/')

        if String.IsNullOrWhiteSpace value then
            value
        elif value.StartsWith("FsColbertIndexes/", StringComparison.OrdinalIgnoreCase) then
            value
        else
            $"FsColbertIndexes/{value}"

    let private prebuiltOriginalPath (asset: PrebuiltKnowledgeAsset) =
        $"app://{asset.documentAsset.Replace('\\', '/')}"

    let private originalPathsEqual left right =
        match PdfDocuments.normalizeBuiltInOriginalPath left, PdfDocuments.normalizeBuiltInOriginalPath right with
        | Some normalizedLeft, Some normalizedRight -> normalizedLeft = normalizedRight
        | _ -> String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let private prebuiltIndexFolder () =
        let path = KnowledgeSources.prebuiltFolder FileSystem.AppDataDirectory
        Directory.CreateDirectory path |> ignore
        path

    let private prebuiltBundleManifestPath () =
        Path.Combine(prebuiltIndexFolder (), "index-bundle.json")

    let private installedPrebuiltManifestPath () =
        let path = KnowledgeSources.prebuiltManifestPath FileSystem.AppDataDirectory

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path))
        |> ignore

        path

    let private readInstalledPrebuiltManifest () =
        let path = installedPrebuiltManifestPath ()

        if File.Exists path then
            try
                JsonSerializer.Deserialize<InstalledPrebuiltIndex array>(File.ReadAllText path)
                |> Option.ofObj
                |> Option.map Array.toList
                |> Option.defaultValue []
            with _ ->
                []
        else
            []

    let private writeInstalledPrebuiltManifestEntries entries =
        let path = installedPrebuiltManifestPath ()
        let json = entries |> List.toArray |> JsonSerializer.Serialize
        File.WriteAllText(path, json)

    let private writeInstalledPrebuiltManifest entries =
        let existing = readInstalledPrebuiltManifest ()
        let merged = existing @ entries |> List.rev |> List.distinctBy _.id |> List.rev

        writeInstalledPrebuiltManifestEntries merged

    let private writeInstalledBundleManifest (manifest: FsColbert.IndexBundleManifest) =
        let path = prebuiltBundleManifestPath ()

        let manifest =
            if File.Exists path then
                match FsColbert.IndexBundle.readManifest path with
                | Ok existing ->
                    { manifest with
                        sources =
                            existing.sources @ manifest.sources
                            |> List.rev
                            |> List.distinctBy _.sourceId
                            |> List.rev }
                | Error _ -> manifest
            else
                manifest

        FsColbert.IndexBundle.writeManifest path manifest

    let private existingPrebuiltDoc (asset: PrebuiltKnowledgeAsset) docs =
        let originalPath = prebuiltOriginalPath asset

        docs
        |> List.tryFind (fun doc ->
            originalPathsEqual doc.originalPath originalPath
            || String.Equals(doc.id, $"prebuilt-{asset.id}", StringComparison.OrdinalIgnoreCase))

    let private prebuiltKind value =
        match (defaultArg (Option.ofObj value) "").Trim().ToLowerInvariant() with
        | "pdf" -> PdfFile
        | "json"
        | "docling-json" -> JsonFile
        | _ -> MarkdownFile

    let private prebuiltChunkCount indexPath =
        try
            if File.Exists indexPath then
                (FsColbert.IndexPersistence.load indexPath).passages.Length
            else
                0
        with _ ->
            0

    let private deleteExistingFile (errors: ResizeArray<string>) path =
        if String.IsNullOrWhiteSpace path || not (File.Exists path) then
            0
        else
            try
                File.Delete path
                1
            with ex ->
                errors.Add $"Unable to delete prebuilt FsColbert index '{path}': {ex.Message}"
                0

    let private deleteStoredDocumentFile (doc: PdfDocumentSource) =
        if String.IsNullOrWhiteSpace doc.storedPath || not (File.Exists doc.storedPath) then
            false
        else
            File.Delete doc.storedPath
            true

    let private installedPrebuiltEntryMatches (doc: PdfDocumentSource) (entry: InstalledPrebuiltIndex) =
        String.Equals(entry.id, doc.id, StringComparison.OrdinalIgnoreCase)
        || String.Equals(entry.storedPath, doc.storedPath, StringComparison.OrdinalIgnoreCase)

    let private installedBundleSourceMatches (doc: PdfDocumentSource) (source: FsColbert.IndexBundleSource) =
        seq {
            yield source.sourceId
            yield source.sourceDisplayName

            match source.sourceLocation with
            | Some location -> yield location
            | None -> ()
        }
        |> Seq.exists (fun candidate ->
            String.Equals(candidate, doc.id, StringComparison.OrdinalIgnoreCase)
            || String.Equals(candidate, doc.displayName, StringComparison.OrdinalIgnoreCase)
            || String.Equals(candidate, doc.storedPath, StringComparison.OrdinalIgnoreCase))

    let private removeInstalledBundleSource (doc: PdfDocumentSource) (errors: ResizeArray<string>) =
        let path = prebuiltBundleManifestPath ()

        if File.Exists path then
            match FsColbert.IndexBundle.readManifest path with
            | Error err -> errors.Add $"Unable to read installed FsColbert bundle manifest '{path}': {err}"
            | Ok manifest ->
                let kept =
                    manifest.sources
                    |> List.filter (fun source -> not (installedBundleSourceMatches doc source))

                if kept.Length <> manifest.sources.Length then
                    try
                        if List.isEmpty kept then
                            File.Delete path
                        else
                            FsColbert.IndexBundle.writeManifest path { manifest with sources = kept }
                    with ex ->
                        errors.Add $"Unable to update installed FsColbert bundle manifest '{path}': {ex.Message}"

    let private cleanupPrebuiltStorageForDocument (doc: PdfDocumentSource) =
        let errors = ResizeArray<string>()
        let entries = readInstalledPrebuiltManifest ()
        let removed, kept = entries |> List.partition (installedPrebuiltEntryMatches doc)

        if not (List.isEmpty removed) then
            writeInstalledPrebuiltManifestEntries kept

        let indexPaths =
            seq {
                for entry in removed do
                    yield entry.indexPath

                yield Path.Combine(prebuiltIndexFolder (), $"{doc.id}.fsci")
            }
            |> Seq.choose Text.notEmpty
            |> Seq.distinctBy _.ToLowerInvariant()
            |> Seq.toList

        let removedIndexCount = indexPaths |> List.sumBy (deleteExistingFile errors)

        removeInstalledBundleSource doc errors
        removedIndexCount, List.ofSeq errors

    let private currentPackagedOriginalPaths
        (assets: PrebuiltKnowledgeAsset list)
        (bundleManifest: FsColbert.IndexBundleManifest option)
        =
        seq {
            for asset in assets do
                yield prebuiltOriginalPath asset

            match bundleManifest with
            | Some manifest ->
                for source in manifest.sources do
                    match source.sourceLocation |> Option.bind Text.notEmpty with
                    | None -> ()
                    | Some sourceLocation ->
                        let documentAsset = packageIndexAssetPath sourceLocation
                        yield $"app://{documentAsset.Replace('\\', '/')}"
            | None -> ()
        }
        |> Seq.choose PdfDocuments.normalizeBuiltInOriginalPath
        |> Set.ofSeq

    let installPrebuiltDocuments (existing: PdfDocumentSource list) =
        async {
            let! assets = readPrebuiltManifest ()
            let! bundleManifest = readPrebuiltBundleManifest ()
            let packagedOriginalPaths = currentPackagedOriginalPaths assets bundleManifest
            let hiddenBuiltInSources = Settings.hiddenBuiltInSources ()
            let docs = ResizeArray<PdfDocumentSource>(existing)
            let installed = ResizeArray<InstalledPrebuiltIndex>()
            let logs = ResizeArray<string>()

            let hiddenBuiltInDocs =
                docs
                |> Seq.toList
                |> List.filter (fun doc ->
                    match PdfDocuments.normalizeBuiltInOriginalPath doc.originalPath with
                    | Some originalPath -> hiddenBuiltInSources.Contains originalPath
                    | None -> false)

            for doc in hiddenBuiltInDocs do
                docs.RemoveAll(fun item -> item.id = doc.id) |> ignore
                logs.Add $"Hid built-in source: {doc.displayName}."

            let staleBuiltInDocs =
                docs
                |> Seq.toList
                |> List.filter (fun doc ->
                    match PdfDocuments.normalizeBuiltInOriginalPath doc.originalPath with
                    | Some originalPath -> not (packagedOriginalPaths.Contains originalPath)
                    | None -> false)

            for doc in staleBuiltInDocs do
                docs.RemoveAll(fun item -> item.id = doc.id) |> ignore

                let removedFile =
                    try
                        deleteStoredDocumentFile doc
                    with _ ->
                        false

                let _, cleanupErrors = cleanupPrebuiltStorageForDocument doc

                logs.Add $"Removed stale built-in source no longer shipped with app: {doc.displayName}."

                if removedFile then
                    logs.Add $"Deleted stale built-in document copy: {doc.displayName}."

                cleanupErrors |> List.iter logs.Add

            for asset in assets do
                let originalPath = prebuiltOriginalPath asset

                if
                    hiddenBuiltInSources.Contains(
                        PdfDocuments.normalizeBuiltInOriginalPath originalPath |> Option.defaultValue ""
                    )
                then
                    ()
                else
                    let id = $"prebuilt-{safeId asset.id}"
                    let documentName = Path.GetFileName asset.documentAsset |> sanitizeFileName
                    let storedPath = Path.Combine(folder (), $"{id}-{documentName}")
                    let indexPath = Path.Combine(prebuiltIndexFolder (), $"{id}.fsci")

                    let! documentCopied =
                        if File.Exists storedPath then
                            async.Return false
                        else
                            copyPackageFile asset.documentAsset storedPath

                    let! indexCopied =
                        if File.Exists indexPath then
                            async.Return false
                        else
                            copyPackageFile asset.indexAsset indexPath

                    let chunkCount = prebuiltChunkCount indexPath

                    let doc =
                        match existingPrebuiltDoc asset (List.ofSeq docs) with
                        | Some current ->
                            { current with
                                storedPath = storedPath
                                status = Ready
                                chunkCount = chunkCount
                                error = None }
                        | None ->
                            { id = id
                              kind = prebuiltKind asset.kind
                              displayName = asset.displayName |> Text.notEmpty |> Option.defaultValue documentName
                              storedPath = storedPath
                              originalPath = originalPath
                              selected = asset.selected
                              status = Ready
                              chunkCount = chunkCount
                              error = None }

                    docs.RemoveAll(fun item -> item.id = doc.id) |> ignore
                    docs.Add doc

                    installed.Add
                        { id = id
                          kind =
                            match doc.kind with
                            | PdfFile -> "pdf"
                            | MarkdownFile -> "markdown"
                            | JsonFile -> "json"
                          displayName = doc.displayName
                          storedPath = storedPath
                          indexPath = indexPath }

                    if documentCopied || indexCopied then
                        logs.Add $"Installed prebuilt knowledge index: {doc.displayName}."

            match bundleManifest with
            | None -> ()
            | Some manifest ->
                let installedBundleSources = ResizeArray<FsColbert.IndexBundleSource>()

                for source in manifest.sources do
                    match source.sourceLocation |> Option.bind Text.notEmpty with
                    | None -> ()
                    | Some sourceLocation ->
                        let documentAsset = packageIndexAssetPath sourceLocation
                        let indexAsset = packageIndexAssetPath source.indexFile
                        let sourceKind = source.sourceKind |> Option.defaultValue ""

                        let asset =
                            { id = source.sourceId
                              kind = sourceKind
                              displayName = source.sourceDisplayName
                              documentAsset = documentAsset
                              indexAsset = indexAsset
                              selected = true }

                        let originalPath = prebuiltOriginalPath asset

                        if
                            hiddenBuiltInSources.Contains(
                                PdfDocuments.normalizeBuiltInOriginalPath originalPath |> Option.defaultValue ""
                            )
                        then
                            ()
                        else
                            let id = $"prebuilt-{safeId asset.id}"
                            let documentName = Path.GetFileName asset.documentAsset |> sanitizeFileName
                            let storedPath = Path.Combine(folder (), $"{id}-{documentName}")
                            let indexPath = Path.Combine(prebuiltIndexFolder (), $"{id}.fsci")

                            let! documentCopied =
                                if File.Exists storedPath then
                                    async.Return false
                                else
                                    copyPackageFile asset.documentAsset storedPath

                            let! indexCopied =
                                if File.Exists indexPath then
                                    async.Return false
                                else
                                    copyPackageFile asset.indexAsset indexPath

                            let chunkCount = prebuiltChunkCount indexPath

                            let doc =
                                match existingPrebuiltDoc asset (List.ofSeq docs) with
                                | Some current ->
                                    { current with
                                        kind = prebuiltKind asset.kind
                                        storedPath = storedPath
                                        status = Ready
                                        chunkCount = chunkCount
                                        error = None }
                                | None ->
                                    { id = id
                                      kind = prebuiltKind asset.kind
                                      displayName =
                                        asset.displayName |> Text.notEmpty |> Option.defaultValue documentName
                                      storedPath = storedPath
                                      originalPath = originalPath
                                      selected = asset.selected
                                      status = Ready
                                      chunkCount = chunkCount
                                      error = None }

                            docs.RemoveAll(fun item -> item.id = doc.id) |> ignore
                            docs.Add doc

                            installed.Add
                                { id = id
                                  kind =
                                    match doc.kind with
                                    | PdfFile -> "pdf"
                                    | MarkdownFile -> "markdown"
                                    | JsonFile -> "json"
                                  displayName = doc.displayName
                                  storedPath = storedPath
                                  indexPath = indexPath }

                            installedBundleSources.Add
                                { source with
                                    sourceLocation = Some storedPath
                                    sourceKind = Some asset.kind
                                    indexFile = Path.GetFileName indexPath }

                            if documentCopied || indexCopied then
                                logs.Add $"Installed bundled FsColbert index: {doc.displayName}."

                if installedBundleSources.Count > 0 then
                    writeInstalledBundleManifest
                        { manifest with
                            sources = List.ofSeq installedBundleSources }

            if installed.Count > 0 then
                writeInstalledPrebuiltManifest (List.ofSeq installed)

            return List.ofSeq docs, List.ofSeq logs
        }

    let private safeCombine (root: string) (relativePath: string) =
        let rootFull = Path.GetFullPath root

        let combined =
            Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)))

        if combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) then
            Some combined
        else
            None

    let private copyExtractedFile root relativePath target =
        match safeCombine root relativePath with
        | None -> false
        | Some sourcePath when not (File.Exists sourcePath) -> false
        | Some sourcePath ->
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath target))
            |> ignore

            File.Copy(sourcePath, target, true)
            true

    let importPrebuiltBundle (existing: PdfDocumentSource list) (bundleName: string) (bundleStream: Stream) =
        async {
            let importRoot =
                Path.Combine(prebuiltIndexFolder (), "Imports", $"{safeId bundleName}-{Guid.NewGuid():N}")

            Directory.CreateDirectory importRoot |> ignore

            let zipPath = Path.Combine(importRoot, "bundle.zip")

            use target = File.Create zipPath
            do! bundleStream.CopyToAsync(target) |> Async.AwaitTask
            target.Dispose()

            ZipFile.ExtractToDirectory(zipPath, importRoot, true)

            let manifestPath = Path.Combine(importRoot, "index-bundle.json")

            if not (File.Exists manifestPath) then
                return existing, [ $"Index bundle '{bundleName}' did not contain index-bundle.json." ]
            else
                match
                    FsColbert.IndexBundle.loadCompatible FsColbert.IndexBundleCompatibility.fsKameDefaults manifestPath
                with
                | Error errors -> return existing, errors
                | Ok bundle ->
                    let docs = ResizeArray<PdfDocumentSource>(existing)
                    let installed = ResizeArray<InstalledPrebuiltIndex>()
                    let installedBundleSources = ResizeArray<FsColbert.IndexBundleSource>()
                    let logs = ResizeArray<string>()

                    for entry in bundle.indexes do
                        match entry.source.sourceLocation |> Option.bind Text.notEmpty with
                        | None -> logs.Add $"Bundle source {entry.source.sourceId} has no sourceLocation."
                        | Some sourceLocation ->
                            let id = $"bundle-{safeId bundle.manifest.bundleId}-{safeId entry.source.sourceId}"
                            let documentName = Path.GetFileName sourceLocation |> sanitizeFileName
                            let storedPath = Path.Combine(folder (), $"{id}-{documentName}")
                            let indexPath = Path.Combine(prebuiltIndexFolder (), $"{id}.fsci")

                            let sourceCopied = copyExtractedFile importRoot sourceLocation storedPath
                            let indexCopied = copyExtractedFile importRoot entry.source.indexFile indexPath

                            if not sourceCopied then
                                logs.Add $"Bundle source file was not found: {sourceLocation}."
                            elif not indexCopied then
                                logs.Add $"Bundle index file was not found: {entry.source.indexFile}."
                            else
                                let sourceKind = entry.source.sourceKind |> Option.defaultValue ""
                                let chunkCount = prebuiltChunkCount indexPath

                                let doc =
                                    match docs |> Seq.tryFind (fun item -> item.id = id) with
                                    | Some current ->
                                        { current with
                                            kind = prebuiltKind sourceKind
                                            displayName = entry.source.sourceDisplayName
                                            storedPath = storedPath
                                            status = Ready
                                            chunkCount = chunkCount
                                            error = None }
                                    | None ->
                                        { id = id
                                          kind = prebuiltKind sourceKind
                                          displayName =
                                            entry.source.sourceDisplayName
                                            |> Text.notEmpty
                                            |> Option.defaultValue documentName
                                          storedPath = storedPath
                                          originalPath = $"bundle://{bundle.manifest.bundleId}/{entry.source.sourceId}"
                                          selected = false
                                          status = Ready
                                          chunkCount = chunkCount
                                          error = None }

                                docs.RemoveAll(fun item -> item.id = doc.id) |> ignore
                                docs.Add doc

                                installed.Add
                                    { id = id
                                      kind =
                                        match doc.kind with
                                        | PdfFile -> "pdf"
                                        | MarkdownFile -> "markdown"
                                        | JsonFile -> "json"
                                      displayName = doc.displayName
                                      storedPath = storedPath
                                      indexPath = indexPath }

                                installedBundleSources.Add
                                    { entry.source with
                                        sourceLocation = Some storedPath
                                        sourceKind = Some sourceKind
                                        indexFile = Path.GetFileName indexPath }

                    if installedBundleSources.Count > 0 then
                        writeInstalledBundleManifest
                            { bundle.manifest with
                                sources = List.ofSeq installedBundleSources }

                    writeInstalledPrebuiltManifest (List.ofSeq installed)

                    logs.Add
                        $"Imported FsColbert bundle '{bundle.manifest.bundleId}' ({installedBundleSources.Count} document(s))."

                    return List.ofSeq docs, List.ofSeq logs
        }

    let copyNewDocuments (existing: PdfDocumentSource list) (results: FileResult seq) =
        async {
            let candidates =
                results
                |> Seq.filter (fun result -> PickedSourceFiles.isDocument result.FileName)
                |> Seq.filter (fun result ->
                    let originalPath = defaultArg (Text.notEmpty result.FullPath) result.FileName
                    not (hasExisting existing originalPath result.FileName))
                |> Seq.toList

            let! copied = candidates |> List.map copyPickedFile |> Async.Parallel

            return copied |> Array.toList
        }

    let processDocument
        report
        keywordOptions
        useHybridPdfParsing
        useLayoutAnalysis
        (cancellationToken: CancellationToken)
        (doc: PdfDocumentSource)
        =
        async {
            let parserName = if useHybridPdfParsing then "Hybrid" else "Legacy"

            report $"Reading {doc.displayName} with {parserName} parser."

            cancellationToken.ThrowIfCancellationRequested()
            throwIfKeywordCancellationRequested keywordOptions

            let! result = readPassages report useHybridPdfParsing useLayoutAnalysis doc cancellationToken
            cancellationToken.ThrowIfCancellationRequested()

            match result with
            | Ok passages ->
                report $"Read {passages.Length} passage(s) from {doc.displayName}; indexing content."
                cancellationToken.ThrowIfCancellationRequested()
                throwIfKeywordCancellationRequested keywordOptions

                let source: FsVoice.QA.KnowledgeSource =
                    { FsVoice.QA.KnowledgeSource.kind = qaSourceKind doc.kind
                      location = doc.storedPath
                      enabled = true }

                match!
                    FsVoice.QA.KnowledgeSources.InindexPassagesWithCancellation
                        FileSystem.AppDataDirectory
                        report
                        keywordOptions
                        (pdfParsingMode useHybridPdfParsing useLayoutAnalysis)
                        source
                        passages
                        cancellationToken
                with
                | Ok() ->
                    cancellationToken.ThrowIfCancellationRequested()
                    report $"Indexed {doc.displayName} with {passages.Length} passage(s)."

                    return
                        { id = doc.id
                          chunkCount = passages.Length
                          error = None }
                | Error err ->
                    report $"Indexing {doc.displayName} failed: {err}"

                    return
                        { id = doc.id
                          chunkCount = 0
                          error = Some err }
            | Error err ->
                report $"Reading {doc.displayName} failed: {err}"

                return
                    { id = doc.id
                      chunkCount = 0
                      error = Some err }
        }

    let processDocuments
        report
        keywordOptions
        useHybridPdfParsing
        useLayoutAnalysis
        (cancellationToken: CancellationToken)
        (docs: PdfDocumentSource list)
        =
        async {
            let mutable results = []

            try
                for doc in docs do
                    cancellationToken.ThrowIfCancellationRequested()
                    throwIfKeywordCancellationRequested keywordOptions

                    report $"Processing document {doc.displayName}."

                    let! result =
                        processDocument
                            report
                            keywordOptions
                            useHybridPdfParsing
                            useLayoutAnalysis
                            cancellationToken
                            doc

                    report $"Finished processing document {doc.displayName}."
                    results <- result :: results

                return Completed(List.rev results)
            with :? OperationCanceledException ->
                return Canceled(List.rev results)
        }

    let deleteStoredDocument (doc: PdfDocumentSource) =
        async {
            if String.IsNullOrWhiteSpace doc.storedPath || not (File.Exists doc.storedPath) then
                return false
            else
                return deleteStoredDocumentFile doc
        }
