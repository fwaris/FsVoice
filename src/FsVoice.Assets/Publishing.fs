namespace FsVoice.Assets

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Control

module AssetPublishing =
    let private bindingValues (bindings: AssetRuntimeBindings) =
        [ bindings.GemmaModel, false, "Gemma model"
          bindings.SttModelDirectory, true, "Parakeet model directory"
          bindings.VadModel, false, "Silero VAD model"
          bindings.TtsModelDirectory, true, "Pocket TTS model directory"
          bindings.VoiceSample, false, "voice sample"
          bindings.IndexBundleDirectory, true, "FsColbert index bundle directory" ]

    let private normalizeRelativePath (sourceRoot: string) (fullPath: string) =
        Path.GetRelativePath(sourceRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/')

    let private ensureRegularFile path label =
        if not (File.Exists path) then
            invalidOp $"{label} was not found: {path}"

        let info = FileInfo path

        if info.Length <= 0L then
            invalidOp $"{label} is empty: {path}"

        if info.Attributes.HasFlag FileAttributes.ReparsePoint then
            invalidOp $"Symbolic links and reparse points cannot be published as assets: {path}"

    let private selectedFiles sourceRoot (bindings: AssetRuntimeBindings) =
        let files = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for relativePath, isDirectory, label in bindingValues bindings do
            if not (AssetManifest.isSafeRelativePath relativePath) then
                invalidOp $"{label} uses an unsafe relative path: {relativePath}"

            let resolved = AssetManifest.resolveRelativePath sourceRoot relativePath

            if isDirectory then
                if not (Directory.Exists resolved) then
                    invalidOp $"{label} was not found: {resolved}"

                let selected = Directory.GetFiles(resolved, "*", SearchOption.AllDirectories)

                if Array.isEmpty selected then
                    invalidOp $"{label} contains no files: {resolved}"

                for file in selected do
                    ensureRegularFile file label
                    files.Add(Path.GetFullPath file) |> ignore
            else
                ensureRegularFile resolved label
                files.Add(Path.GetFullPath resolved) |> ignore

        files |> Seq.sort |> Seq.toArray

    let private fsColbertModelId sourceRoot (bindings: AssetRuntimeBindings) =
        let manifestPath =
            AssetManifest.resolveRelativePath sourceRoot (bindings.IndexBundleDirectory.TrimEnd('/') + "/index-bundle.json")

        ensureRegularFile manifestPath "FsColbert index-bundle.json"
        use document = JsonDocument.Parse(File.ReadAllText manifestPath)

        let tryString (name: string) =
            match document.RootElement.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
            | _ -> None

        tryString "model_id"
        |> Option.orElseWith (fun () -> tryString "modelId")
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp $"FsColbert index bundle does not declare model_id: {manifestPath}")

    let private createManifestAsync options cancellationToken =
        task {
            let sourceRoot = Path.GetFullPath options.SourceRoot

            if not (Directory.Exists sourceRoot) then
                invalidArg (nameof options.SourceRoot) $"Asset source root was not found: {sourceRoot}"

            let selected = selectedFiles sourceRoot options.Bindings

            let! files =
                selected
                |> AsyncSeq.ofSeq
                |> AsyncSeq.mapAsyncParallelThrottled (max 1 options.ParallelUploads) (fun path ->
                    async {
                        let! hash = AssetManifest.sha256FileAsync path cancellationToken |> Async.AwaitTask

                        return
                            { Path = normalizeRelativePath sourceRoot path
                              Size = FileInfo(path).Length
                              Sha256 = hash }
                    })
                |> AsyncSeq.toArrayAsync
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

            let manifest =
                { SchemaVersion = AssetManifest.SchemaVersion
                  ReleaseId = options.ReleaseId
                  CreatedUtc = DateTimeOffset.UtcNow
                  Bindings = options.Bindings
                  Compatibility =
                    { ContractVersion = AssetManifest.ContractVersion
                      FsColbertModelId = fsColbertModelId sourceRoot options.Bindings }
                  Files = files |> Array.sortBy _.Path }

            let errors = AssetManifest.validate manifest

            if not (List.isEmpty errors) then
                errors
                |> List.map ManifestValidationError.format
                |> String.concat Environment.NewLine
                |> fun message -> invalidOp $"Generated asset manifest is invalid:{Environment.NewLine}{message}"

            return manifest
        }

    let private uploadObjectAsync options sourceRoot (entry: AssetFileEntry) cancellationToken =
        task {
            let key = AssetCache.objectKey entry.Sha256
            let! existing = options.Store.GetObjectInfoAsync(key, cancellationToken)

            match existing with
            | Some info when
                info.Size = entry.Size
                && (info.Sha256
                    |> Option.exists (fun value ->
                        String.Equals(value, entry.Sha256, StringComparison.OrdinalIgnoreCase)))
                ->
                options.Report $"Reusing {entry.Path} ({entry.Sha256})."
                return false, 0L
            | Some _ ->
                return
                    invalidOp
                        $"Content-addressed object {key} already exists with incompatible size or SHA-256 metadata."
            | None ->
                let sourcePath = AssetManifest.resolveRelativePath sourceRoot entry.Path
                let! result = options.Store.PutObjectIfAbsentAsync(key, sourcePath, entry.Sha256, cancellationToken)

                match result with
                | AssetUploadResult.Uploaded ->
                    options.Report $"Uploaded {entry.Path} ({entry.Size} bytes)."
                    return true, entry.Size
                | AssetUploadResult.AlreadyExists ->
                    let! raced = options.Store.GetObjectInfoAsync(key, cancellationToken)

                    match raced with
                    | Some info when info.Size = entry.Size -> return false, 0L
                    | _ -> return invalidOp $"Asset object {key} appeared concurrently but failed verification."
        }

    let private yamlQuote (value: string) = "'" + value.Replace("'", "''") + "'"

    let private writeGeneratedValues options manifestSha =
        let content =
            [ "assets:"
              $"  mode: {options.Store.ProviderName}"
              $"  releaseId: {yamlQuote options.ReleaseId}"
              $"  manifestKey: {yamlQuote options.ManifestKey}"
              $"  manifestSha256: {yamlQuote manifestSha}" ]
            |> String.concat Environment.NewLine

        AssetManifest.writeAtomicText options.ValuesOutputPath (content + Environment.NewLine)

    let publishAsync (options: AssetPublishOptions) (cancellationToken: CancellationToken) =
        task {
            if String.IsNullOrWhiteSpace options.ManifestKey then
                invalidArg (nameof options.ManifestKey) "Asset manifestKey is required."

            if options.ParallelUploads <= 0 then
                invalidArg (nameof options.ParallelUploads) "parallelUploads must be greater than zero."

            let! manifest = createManifestAsync options cancellationToken
            let sourceRoot = Path.GetFullPath options.SourceRoot

            let! uploadResults =
                manifest.Files
                |> AsyncSeq.ofSeq
                |> AsyncSeq.mapAsyncParallelThrottled options.ParallelUploads (fun entry ->
                    uploadObjectAsync options sourceRoot entry cancellationToken |> Async.AwaitTask)
                |> AsyncSeq.toArrayAsync
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

            let manifestBytes = AssetManifest.serialize manifest
            let manifestSha = AssetManifest.sha256Bytes manifestBytes
            let valuesDirectory = Path.GetDirectoryName(Path.GetFullPath options.ValuesOutputPath)
            Directory.CreateDirectory valuesDirectory |> ignore
            let manifestPath = Path.Combine(valuesDirectory, options.ReleaseId + ".manifest.json")
            AssetManifest.writeAtomicBytes manifestPath manifestBytes

            let! manifestUpload =
                options.Store.PutObjectIfAbsentAsync(
                    options.ManifestKey,
                    manifestPath,
                    manifestSha,
                    cancellationToken
                )

            match manifestUpload with
            | AssetUploadResult.AlreadyExists ->
                invalidOp
                    $"Asset release manifest already exists at '{options.ManifestKey}'. Release IDs are immutable; choose a new releaseId."
            | AssetUploadResult.Uploaded -> ()

            writeGeneratedValues options manifestSha

            return
                { Manifest = manifest
                  ManifestPath = manifestPath
                  ManifestSha256 = manifestSha
                  UploadedObjects = uploadResults |> Array.filter fst |> Array.length
                  ReusedObjects = uploadResults |> Array.filter (fst >> not) |> Array.length
                  UploadedBytes = uploadResults |> Array.sumBy snd }
        }

    let verifyAsync manifestPath root cancellationToken =
        task {
            let manifest, manifestSha =
                match AssetManifest.readFile manifestPath with
                | Ok value -> value
                | Error error -> invalidOp error

            let validationErrors = AssetManifest.validate manifest

            if not (List.isEmpty validationErrors) then
                validationErrors
                |> List.map ManifestValidationError.format
                |> String.concat Environment.NewLine
                |> invalidOp

            if not (String.IsNullOrWhiteSpace root) then
                let! treeErrors = AssetManifest.verifyTreeAsync root manifest cancellationToken

                if not (List.isEmpty treeErrors) then
                    treeErrors
                    |> List.map ManifestValidationError.format
                    |> String.concat Environment.NewLine
                    |> invalidOp

            return manifest, manifestSha
        }
