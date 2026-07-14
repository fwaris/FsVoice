namespace FsVoice.Assets

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type ManifestValidationError =
    | UnsupportedSchemaVersion of int
    | InvalidReleaseId of string
    | InvalidCreatedUtc
    | MissingBindings
    | InvalidCompatibility of string
    | EmptyFileList
    | InvalidRelativePath of string
    | DuplicatePath of string
    | InvalidFileSize of string * int64
    | InvalidSha256 of string
    | MissingBindingTarget of string * string
    | MissingFile of string
    | FileSizeMismatch of string * int64 * int64
    | FileHashMismatch of string * string * string

module ManifestValidationError =
    let format = function
        | ManifestValidationError.UnsupportedSchemaVersion version ->
            $"Unsupported asset manifest schema version {version}; expected version 1."
        | ManifestValidationError.InvalidReleaseId value -> $"Invalid asset release ID '{value}'."
        | ManifestValidationError.InvalidCreatedUtc -> "Asset manifest createdUtc must be populated."
        | ManifestValidationError.MissingBindings -> "Asset manifest runtime bindings are required."
        | ManifestValidationError.InvalidCompatibility message -> $"Invalid asset compatibility: {message}"
        | ManifestValidationError.EmptyFileList -> "Asset manifest must contain at least one file."
        | ManifestValidationError.InvalidRelativePath path -> $"Invalid manifest-relative asset path '{path}'."
        | ManifestValidationError.DuplicatePath path -> $"Duplicate asset path '{path}'."
        | ManifestValidationError.InvalidFileSize(path, size) ->
            $"Asset '{path}' has invalid byte length {size}."
        | ManifestValidationError.InvalidSha256 path -> $"Asset '{path}' has an invalid SHA-256."
        | ManifestValidationError.MissingBindingTarget(name, path) ->
            $"Runtime binding '{name}' does not resolve to a manifest file or directory: {path}"
        | ManifestValidationError.MissingFile path -> $"Asset file is missing: {path}"
        | ManifestValidationError.FileSizeMismatch(path, expected, actual) ->
            $"Asset '{path}' has {actual} bytes; expected {expected}."
        | ManifestValidationError.FileHashMismatch(path, expected, actual) ->
            $"Asset '{path}' SHA-256 is {actual}; expected {expected}."

module AssetManifest =
    [<Literal>]
    let SchemaVersion = 1

    [<Literal>]
    let ContractVersion = 1

    let private releaseIdPattern = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)
    let private sha256Pattern = Regex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)

    let jsonOptions =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        )

    let sha256Bytes (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let sha256FileAsync (path: string) (cancellationToken: CancellationToken) =
        task {
            use stream =
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous)

            let! hash = SHA256.HashDataAsync(stream, cancellationToken)
            return Convert.ToHexString(hash).ToLowerInvariant()
        }

    let serialize (manifest: AssetReleaseManifest) = JsonSerializer.SerializeToUtf8Bytes(manifest, jsonOptions)

    let deserialize (bytes: byte array) =
        try
            let manifest = JsonSerializer.Deserialize<AssetReleaseManifest>(bytes, jsonOptions)

            if isNull (box manifest) then
                Error "Asset manifest JSON deserialized to null."
            else
                Ok manifest
        with ex ->
            Error $"Asset manifest JSON is invalid: {ex.Message}"

    let private pathSegments (path: string) = path.Split('/', StringSplitOptions.None)

    let isSafeRelativePath (path: string) =
        not (String.IsNullOrWhiteSpace path)
        && not (Path.IsPathRooted path)
        && not (path.Contains '\\')
        && not (path.Contains ':')
        && not (path |> Seq.exists Char.IsControl)
        && pathSegments path
           |> Array.forall (fun segment ->
               not (String.IsNullOrWhiteSpace segment) && segment <> "." && segment <> "..")

    let resolveRelativePath (root: string) (path: string) =
        if not (isSafeRelativePath path) then
            invalidArg (nameof path) $"Invalid manifest-relative path: {path}"

        let rootFull = Path.GetFullPath root
        let relative = path.Replace('/', Path.DirectorySeparatorChar)
        let resolved = Path.GetFullPath(Path.Combine(rootFull, relative))
        let prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + string Path.DirectorySeparatorChar

        if not (resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) then
            invalidArg (nameof path) $"Asset path escapes its root: {path}"

        resolved

    let private bindingValues (bindings: AssetRuntimeBindings) =
        [ "gemmaModel", bindings.GemmaModel, false
          "sttModelDirectory", bindings.SttModelDirectory, true
          "vadModel", bindings.VadModel, false
          "ttsModelDirectory", bindings.TtsModelDirectory, true
          "voiceSample", bindings.VoiceSample, false
          "indexBundleDirectory", bindings.IndexBundleDirectory, true ]

    let validate (manifest: AssetReleaseManifest) =
        let errors = ResizeArray<ManifestValidationError>()

        if manifest.SchemaVersion <> SchemaVersion then
            errors.Add(ManifestValidationError.UnsupportedSchemaVersion manifest.SchemaVersion)

        if String.IsNullOrWhiteSpace manifest.ReleaseId || not (releaseIdPattern.IsMatch manifest.ReleaseId) then
            errors.Add(ManifestValidationError.InvalidReleaseId manifest.ReleaseId)

        if manifest.CreatedUtc = DateTimeOffset.MinValue then
            errors.Add ManifestValidationError.InvalidCreatedUtc

        if isNull (box manifest.Compatibility) then
            errors.Add(ManifestValidationError.InvalidCompatibility "compatibility is required")
        else
            if manifest.Compatibility.ContractVersion <> ContractVersion then
                errors.Add(
                    ManifestValidationError.InvalidCompatibility(
                        $"contract version {manifest.Compatibility.ContractVersion} is unsupported; expected {ContractVersion}"
                    )
                )

            if String.IsNullOrWhiteSpace manifest.Compatibility.FsColbertModelId then
                errors.Add(ManifestValidationError.InvalidCompatibility "FsColbert model ID is required")

        let files = if isNull manifest.Files then Array.empty else manifest.Files

        if Array.isEmpty files then
            errors.Add ManifestValidationError.EmptyFileList

        let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for file in files do
            if isNull (box file) || not (isSafeRelativePath file.Path) then
                errors.Add(ManifestValidationError.InvalidRelativePath(if isNull (box file) then "<null>" else file.Path))
            else
                if not (seen.Add file.Path) then
                    errors.Add(ManifestValidationError.DuplicatePath file.Path)

                if file.Size <= 0L then
                    errors.Add(ManifestValidationError.InvalidFileSize(file.Path, file.Size))

                if String.IsNullOrWhiteSpace file.Sha256 || not (sha256Pattern.IsMatch file.Sha256) then
                    errors.Add(ManifestValidationError.InvalidSha256 file.Path)

        if isNull (box manifest.Bindings) then
            errors.Add ManifestValidationError.MissingBindings
        else
            let filePaths = files |> Array.map _.Path

            for name, path, isDirectory in bindingValues manifest.Bindings do
                if not (isSafeRelativePath path) then
                    errors.Add(ManifestValidationError.InvalidRelativePath path)
                else
                    let found =
                        if isDirectory then
                            let prefix = path.TrimEnd('/') + "/"
                            filePaths |> Array.exists (fun candidate -> candidate.StartsWith(prefix, StringComparison.Ordinal))
                        else
                            filePaths |> Array.contains path

                    if not found then
                        errors.Add(ManifestValidationError.MissingBindingTarget(name, path))

            let indexManifest = manifest.Bindings.IndexBundleDirectory.TrimEnd('/') + "/index-bundle.json"

            if not (filePaths |> Array.contains indexManifest) then
                errors.Add(ManifestValidationError.MissingBindingTarget("indexBundleManifest", indexManifest))

        errors |> Seq.toList

    let verifyTreeAsync
        (root: string)
        (manifest: AssetReleaseManifest)
        (cancellationToken: CancellationToken)
        =
        task {
            let errors = ResizeArray<ManifestValidationError>(validate manifest)

            if errors.Count = 0 then
                for file in manifest.Files do
                    cancellationToken.ThrowIfCancellationRequested()
                    let path = resolveRelativePath root file.Path

                    if not (File.Exists path) then
                        errors.Add(ManifestValidationError.MissingFile file.Path)
                    else
                        let actualSize = FileInfo(path).Length

                        if actualSize <> file.Size then
                            errors.Add(ManifestValidationError.FileSizeMismatch(file.Path, file.Size, actualSize))
                        else
                            let! actualHash = sha256FileAsync path cancellationToken

                            if not (String.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase)) then
                                errors.Add(
                                    ManifestValidationError.FileHashMismatch(file.Path, file.Sha256, actualHash)
                                )

            return errors |> Seq.toList
        }

    let readFile (path: string) =
        let bytes = File.ReadAllBytes path

        match deserialize bytes with
        | Ok manifest -> Ok(manifest, sha256Bytes bytes)
        | Error error -> Error error

    let writeAtomicBytes (path: string) (bytes: byte array) =
        let directory = Path.GetDirectoryName path

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory directory |> ignore

        let temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")
        File.WriteAllBytes(temporary, bytes)
        File.Move(temporary, path, true)

    let writeAtomicText (path: string) (content: string) =
        writeAtomicBytes path (UTF8Encoding(false).GetBytes content)

    let writeStatus (path: string) (status: AssetBootstrapStatus) =
        writeAtomicBytes path (JsonSerializer.SerializeToUtf8Bytes(status, jsonOptions))

    let tryReadStatus (path: string) =
        if String.IsNullOrWhiteSpace path || not (File.Exists path) then
            None
        else
            try
                JsonSerializer.Deserialize<AssetBootstrapStatus>(File.ReadAllBytes path, jsonOptions)
                |> Option.ofObj
            with _ ->
                None

module AssetRuntimeEnvironment =
    let private shellQuote (value: string) = "'" + value.Replace("'", "'\"'\"'") + "'"

    let private environmentLine name value = name + "=" + shellQuote value

    let write
        (releaseRoot: string)
        (bindings: AssetRuntimeBindings)
        (statusPath: string)
        (environmentPath: string)
        =
        let resolve = AssetManifest.resolveRelativePath releaseRoot
        let gemma = resolve bindings.GemmaModel
        let stt = resolve bindings.SttModelDirectory
        let vad = resolve bindings.VadModel
        let tts = resolve bindings.TtsModelDirectory
        let voice = resolve bindings.VoiceSample
        let indexes = resolve bindings.IndexBundleDirectory

        [ environmentLine "FSVOICE_ASSET_RELEASE_ROOT" (Path.GetFullPath releaseRoot)
          environmentLine "LLAMA_CPP_MODEL" gemma
          environmentLine "OpenSourceVoice__Gemma__LlamaCppModel" (Path.GetFileName gemma)
          environmentLine "OpenSourceVoice__Stt__ModelDir" stt
          environmentLine "OpenSourceVoice__Vad__ModelPath" vad
          environmentLine "OpenSourceVoice__Tts__ModelDir" tts
          environmentLine "OpenSourceVoice__Tts__VoiceSamplePath" voice
          environmentLine "OpenSourceVoice__Index__BundleDirectory" indexes
          environmentLine "OpenSourceVoice__Assets__StatusFile" (Path.GetFullPath statusPath) ]
        |> String.concat Environment.NewLine
        |> fun content -> AssetManifest.writeAtomicText environmentPath (content + Environment.NewLine)

    let writeAbsolute
        (options: LocalAssetPrepareOptions)
        =
        [ environmentLine "LLAMA_CPP_MODEL" (Path.GetFullPath options.GemmaModel)
          environmentLine "OpenSourceVoice__Gemma__LlamaCppModel" (Path.GetFileName options.GemmaModel)
          environmentLine "OpenSourceVoice__Stt__ModelDir" (Path.GetFullPath options.SttModelDirectory)
          environmentLine "OpenSourceVoice__Vad__ModelPath" (Path.GetFullPath options.VadModel)
          environmentLine "OpenSourceVoice__Tts__ModelDir" (Path.GetFullPath options.TtsModelDirectory)
          environmentLine "OpenSourceVoice__Tts__VoiceSamplePath" (Path.GetFullPath options.VoiceSample)
          environmentLine "OpenSourceVoice__Index__BundleDirectory" (Path.GetFullPath options.IndexBundleDirectory)
          environmentLine "OpenSourceVoice__Assets__StatusFile" (Path.GetFullPath options.StatusPath) ]
        |> String.concat Environment.NewLine
        |> fun content -> AssetManifest.writeAtomicText options.RuntimeEnvironmentPath (content + Environment.NewLine)

module LocalAssetPreparation =
    let private requiredFile path label =
        if not (File.Exists path) || FileInfo(path).Length <= 0L then
            invalidOp $"{label} was not found or is empty: {path}"

    let private requiredDirectory path label =
        if not (Directory.Exists path) then
            invalidOp $"{label} was not found: {path}"

    let prepare (options: LocalAssetPrepareOptions) =
        let started = Diagnostics.Stopwatch.StartNew()
        requiredFile options.GemmaModel "Gemma GGUF model"
        requiredDirectory options.SttModelDirectory "Parakeet model directory"
        requiredFile options.VadModel "Silero VAD model"
        requiredDirectory options.TtsModelDirectory "Pocket TTS model directory"
        requiredFile options.VoiceSample "Voice sample"
        requiredDirectory options.IndexBundleDirectory "FsColbert index bundle directory"
        requiredFile (Path.Combine(options.IndexBundleDirectory, "index-bundle.json")) "FsColbert index bundle manifest"
        AssetRuntimeEnvironment.writeAbsolute options
        started.Stop()

        let status =
            { Ready = true
              Mode = "local"
              Provider = "local"
              ReleaseId = "local"
              ManifestSha256 = ""
              CacheRoot = ""
              CacheHit = true
              OfflineManifestUsed = false
              DownloadedBytes = 0L
              DurationMs = started.Elapsed.TotalMilliseconds
              Message = "Pre-provisioned local assets are ready." }

        AssetManifest.writeStatus options.StatusPath status
        options.Report status.Message

        { ReleaseRoot = ""
          Manifest = None
          Status = status }
