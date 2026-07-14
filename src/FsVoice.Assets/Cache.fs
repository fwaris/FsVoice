namespace FsVoice.Assets

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open FSharp.Control

module private NativeHardLink =
    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool private CreateHardLinkW(string newFileName, string existingFileName, nativeint securityAttributes)

    [<DllImport("libc", EntryPoint = "link", SetLastError = true)>]
    extern int private linkUnix(string existingFileName, string newFileName)

    let tryCreate source destination =
        try
            if OperatingSystem.IsWindows() then
                CreateHardLinkW(destination, source, 0n)
            else
                linkUnix(source, destination) = 0
        with _ ->
            false

module AssetCache =
    let objectKey (sha256: string) = $"objects/sha256/{sha256.Substring(0, 2)}/{sha256}"

    let private objectPath (cacheRoot: string) (sha256: string) =
        Path.Combine(cacheRoot, "objects", "sha256", sha256.Substring(0, 2), sha256)

    let private verifiedMarkerPath (path: string) = path + ".verified"

    let private manifestCachePath (cacheRoot: string) (releaseId: string) =
        Path.Combine(cacheRoot, "manifests", releaseId + ".json")

    let private releasePath (cacheRoot: string) (releaseId: string) =
        Path.Combine(cacheRoot, "releases", releaseId)

    let private ensurePositive (name: string) (value: int) =
        if value <= 0 then
            invalidArg name $"{name} must be greater than zero."

    let private sanitizedFailure (ex: exn) = ex.GetType().Name

    let private acquireLockAsync (cacheRoot: string) (cancellationToken: CancellationToken) =
        task {
            Directory.CreateDirectory cacheRoot |> ignore
            let lockPath = Path.Combine(cacheRoot, ".prepare.lock")
            let deadline = DateTimeOffset.UtcNow.AddMinutes 60.0
            let mutable lockStream: FileStream option = None

            while lockStream.IsNone do
                cancellationToken.ThrowIfCancellationRequested()

                try
                    lockStream <-
                        Some(
                            new FileStream(
                                lockPath,
                                FileMode.OpenOrCreate,
                                FileAccess.ReadWrite,
                                FileShare.None,
                                4096,
                                FileOptions.DeleteOnClose
                            )
                        )
                with :? IOException ->
                    if DateTimeOffset.UtcNow >= deadline then
                        invalidOp $"Timed out waiting for the asset cache lock: {lockPath}"

                    do! Task.Delay(TimeSpan.FromSeconds 2.0, cancellationToken)

            return lockStream.Value
        }

    let private validateExpectedManifestSha value =
        if
            String.IsNullOrWhiteSpace value
            || value.Length <> 64
            || value |> Seq.exists (fun ch -> not (Uri.IsHexDigit ch))
        then
            invalidArg (nameof value) "A 64-character manifest SHA-256 is required."

        value.ToLowerInvariant()

    let private parseAndValidateManifest expectedReleaseId expectedSha bytes =
        let actualSha = AssetManifest.sha256Bytes bytes

        if not (String.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase)) then
            invalidOp $"Asset manifest SHA-256 mismatch. Expected {expectedSha}; received {actualSha}."

        let manifest =
            match AssetManifest.deserialize bytes with
            | Ok value -> value
            | Error error -> invalidOp error

        let errors = AssetManifest.validate manifest

        if not (List.isEmpty errors) then
            errors
            |> List.map ManifestValidationError.format
            |> String.concat Environment.NewLine
            |> fun message -> invalidOp $"Asset manifest validation failed:{Environment.NewLine}{message}"

        if not (String.Equals(manifest.ReleaseId, expectedReleaseId, StringComparison.Ordinal)) then
            invalidOp
                $"Asset manifest release ID '{manifest.ReleaseId}' does not match configured release '{expectedReleaseId}'."

        manifest, actualSha

    let private loadManifestAsync (options: AssetPrepareOptions) cancellationToken =
        task {
            let expectedSha = validateExpectedManifestSha options.ManifestSha256
            let cachedPath = manifestCachePath options.CacheRoot options.ReleaseId
            let mutable remoteError = false

            let! remoteBytes =
                task {
                    try
                        let! bytes = AssetStore.readBytesAsync options.Store options.ManifestKey cancellationToken
                        return Some bytes
                    with
                    | :? OperationCanceledException -> return raise (OperationCanceledException(cancellationToken))
                    | _ ->
                        remoteError <- true
                        return None
                }

            match remoteBytes with
            | Some bytes ->
                let manifest, actualSha = parseAndValidateManifest options.ReleaseId expectedSha bytes
                AssetManifest.writeAtomicBytes cachedPath bytes
                return manifest, actualSha, false
            | None when File.Exists cachedPath ->
                let bytes = File.ReadAllBytes cachedPath
                let manifest, actualSha = parseAndValidateManifest options.ReleaseId expectedSha bytes
                options.Report $"Cloud storage is unavailable; using verified cached manifest for release {options.ReleaseId}."
                return manifest, actualSha, remoteError
            | None ->
                return
                    invalidOp
                        $"Unable to download asset manifest '{options.ManifestKey}' from {options.Store.ProviderName}, and no verified cached copy exists for release {options.ReleaseId}."
        }

    let private markerContent (entry: AssetFileEntry) = $"{entry.Size}:{entry.Sha256.ToLowerInvariant()}"

    let private verifyCachedObjectAsync cacheRoot (entry: AssetFileEntry) cancellationToken =
        task {
            let path = objectPath cacheRoot entry.Sha256
            let markerPath = verifiedMarkerPath path

            if not (File.Exists path) || FileInfo(path).Length <> entry.Size then
                return false
            elif File.Exists markerPath && File.ReadAllText(markerPath) = markerContent entry then
                return true
            else
                let! actualHash = AssetManifest.sha256FileAsync path cancellationToken

                if String.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase) then
                    AssetManifest.writeAtomicText markerPath (markerContent entry)
                    return true
                else
                    File.Delete path

                    if File.Exists markerPath then
                        File.Delete markerPath

                    return false
        }

    let private availableBytes path =
        let root = Path.GetPathRoot(Path.GetFullPath path)
        DriveInfo(root).AvailableFreeSpace

    let private preflightDiskSpace cacheRoot missingBytes =
        let required = int64 (Math.Ceiling(float missingBytes * 1.10))
        let available = availableBytes cacheRoot

        if available < required then
            invalidOp
                $"Asset cache has insufficient free space. Required {required} bytes (including headroom); available {available} bytes at {cacheRoot}."

    let private downloadObjectAsync
        (options: AssetPrepareOptions)
        (entry: AssetFileEntry)
        (cancellationToken: CancellationToken)
        : Task<int64> =
        task {
            let finalPath = objectPath options.CacheRoot entry.Sha256
            let partialPath = finalPath + ".partial"
            Directory.CreateDirectory(Path.GetDirectoryName finalPath) |> ignore
            let key = objectKey entry.Sha256
            let! remoteInfo = options.Store.GetObjectInfoAsync(key, cancellationToken)

            match remoteInfo with
            | None -> invalidOp $"Required asset object is missing from {options.Store.ProviderName}: {key}"
            | Some info when info.Size <> entry.Size ->
                invalidOp $"Remote asset object {key} has {info.Size} bytes; manifest requires {entry.Size}."
            | Some info ->
                match info.Sha256 with
                | Some sha when not (String.Equals(sha, entry.Sha256, StringComparison.OrdinalIgnoreCase)) ->
                    invalidOp $"Remote asset object metadata does not match its manifest SHA-256: {key}"
                | _ -> ()

            let mutable attempt = 1
            let mutable completed = false
            let mutable downloadedBytes = 0L
            let mutable lastFailure = ""

            while not completed && attempt <= options.MaxRetries do
                cancellationToken.ThrowIfCancellationRequested()

                try
                    let offset =
                        if File.Exists partialPath then FileInfo(partialPath).Length else 0L

                    let resumeOffset = if offset >= 0L && offset < entry.Size then offset else 0L

                    if resumeOffset = 0L && File.Exists partialPath then
                        File.Delete partialPath

                    use destination =
                        new FileStream(
                            partialPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.None,
                            1024 * 1024,
                            FileOptions.Asynchronous ||| FileOptions.SequentialScan
                        )

                    options.Report
                        $"Downloading {entry.Path} from {options.Store.ProviderName} ({entry.Size - resumeOffset} bytes remaining)."

                    do! options.Store.DownloadAsync(key, resumeOffset, destination, cancellationToken)
                    do! destination.FlushAsync cancellationToken
                    downloadedBytes <- downloadedBytes + (destination.Length - resumeOffset)
                    destination.Close()

                    let actualSize = FileInfo(partialPath).Length

                    if actualSize <> entry.Size then
                        invalidOp $"Downloaded {actualSize} bytes for {entry.Path}; expected {entry.Size}."

                    let! actualHash = AssetManifest.sha256FileAsync partialPath cancellationToken

                    if not (String.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase)) then
                        File.Delete partialPath
                        invalidOp $"Downloaded asset SHA-256 mismatch for {entry.Path}."

                    File.Move(partialPath, finalPath, true)
                    AssetManifest.writeAtomicText (verifiedMarkerPath finalPath) (markerContent entry)
                    completed <- true
                with
                | :? OperationCanceledException -> raise (OperationCanceledException(cancellationToken))
                | ex ->
                    lastFailure <- sanitizedFailure ex

                    if attempt < options.MaxRetries then
                        let delaySeconds = Math.Min(30.0, Math.Pow(2.0, float (attempt - 1)))
                        do! Task.Delay(TimeSpan.FromSeconds delaySeconds, cancellationToken)

                    attempt <- attempt + 1

            if not completed then
                invalidOp
                    $"Asset download failed after {options.MaxRetries} attempts for {entry.Path}: {lastFailure}"

            return downloadedBytes
        }

    let private materializeRelease cacheRoot (manifest: AssetReleaseManifest) =
        let releasesRoot = Path.Combine(cacheRoot, "releases")
        let finalRoot = releasePath cacheRoot manifest.ReleaseId
        Directory.CreateDirectory releasesRoot |> ignore

        if Directory.Exists finalRoot then
            Directory.Delete(finalRoot, true)

        let stagingRoot = Path.Combine(releasesRoot, $".staging-{manifest.ReleaseId}-{Guid.NewGuid():N}")
        Directory.CreateDirectory stagingRoot |> ignore

        try
            for entry in manifest.Files do
                let source = objectPath cacheRoot entry.Sha256
                let destination = AssetManifest.resolveRelativePath stagingRoot entry.Path
                Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore

                if not (NativeHardLink.tryCreate source destination) then
                    File.Copy(source, destination, false)

            AssetManifest.writeAtomicBytes (Path.Combine(stagingRoot, ".asset-manifest.json")) (AssetManifest.serialize manifest)
            Directory.Move(stagingRoot, finalRoot)
            finalRoot
        with _ ->
            if Directory.Exists stagingRoot then
                Directory.Delete(stagingRoot, true)

            reraise ()

    let private readReleaseManifest releaseDirectory =
        let path = Path.Combine(releaseDirectory, ".asset-manifest.json")

        if File.Exists path then
            match AssetManifest.readFile path with
            | Ok(manifest, _) -> Some manifest
            | Error _ -> None
        else
            None

    let private garbageCollect cacheRoot activeRelease retainReleases report =
        let releasesRoot = Path.Combine(cacheRoot, "releases")

        if Directory.Exists releasesRoot then
            let releaseDirectories =
                Directory.GetDirectories releasesRoot
                |> Array.filter (fun path -> not ((Path.GetFileName path).StartsWith(".staging-", StringComparison.Ordinal)))
                |> Array.sortByDescending (fun path -> Directory.GetLastWriteTimeUtc path)

            let activePath = releasePath cacheRoot activeRelease

            let keep =
                Array.append [| activePath |] (releaseDirectories |> Array.filter ((<>) activePath))
                |> Array.distinct
                |> Array.truncate (max 1 retainReleases)
                |> HashSet<string>

            for directory in releaseDirectories do
                if not (keep.Contains directory) then
                    Directory.Delete(directory, true)
                    report $"Removed cached asset release {Path.GetFileName directory}."

            let referenced = HashSet<string>(StringComparer.OrdinalIgnoreCase)

            for directory in keep do
                match readReleaseManifest directory with
                | Some manifest ->
                    for entry in manifest.Files do
                        referenced.Add entry.Sha256 |> ignore
                | None -> ()

            let objectsRoot = Path.Combine(cacheRoot, "objects", "sha256")

            if Directory.Exists objectsRoot then
                for path in Directory.GetFiles(objectsRoot, "*", SearchOption.AllDirectories) do
                    if not (path.EndsWith(".verified", StringComparison.Ordinal)) then
                        let hash = Path.GetFileName path

                        if not (referenced.Contains hash) then
                            File.Delete path

                            let marker = verifiedMarkerPath path

                            if File.Exists marker then
                                File.Delete marker

    let prepareAsync (options: AssetPrepareOptions) (cancellationToken: CancellationToken) =
        task {
            if String.IsNullOrWhiteSpace options.ReleaseId then
                invalidArg (nameof options.ReleaseId) "Asset releaseId is required."

            if String.IsNullOrWhiteSpace options.ManifestKey then
                invalidArg (nameof options.ManifestKey) "Asset manifestKey is required."

            ensurePositive (nameof options.RetainReleases) options.RetainReleases
            ensurePositive (nameof options.ParallelDownloads) options.ParallelDownloads
            ensurePositive (nameof options.MaxRetries) options.MaxRetries

            let cacheRoot = Path.GetFullPath options.CacheRoot
            Directory.CreateDirectory cacheRoot |> ignore
            let started = Stopwatch.StartNew()
            use! cacheLock = acquireLockAsync cacheRoot cancellationToken
            let! manifest, manifestSha, offlineManifest = loadManifestAsync { options with CacheRoot = cacheRoot } cancellationToken

            let! cachedFlags =
                manifest.Files
                |> Array.map (fun entry -> verifyCachedObjectAsync cacheRoot entry cancellationToken)
                |> Task.WhenAll

            let missing =
                Array.map2 (fun entry cached -> entry, cached) manifest.Files cachedFlags
                |> Array.choose (fun (entry, cached) -> if cached then None else Some entry)

            missing |> Array.sumBy _.Size |> preflightDiskSpace cacheRoot

            let! downloadResults =
                missing
                |> AsyncSeq.ofSeq
                |> AsyncSeq.mapAsyncParallelThrottled options.ParallelDownloads (fun entry ->
                    downloadObjectAsync { options with CacheRoot = cacheRoot } entry cancellationToken
                    |> Async.AwaitTask)
                |> AsyncSeq.toArrayAsync
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

            let downloadedBytes = Array.sum downloadResults
            let releaseRoot = materializeRelease cacheRoot manifest
            Directory.SetLastWriteTimeUtc(releaseRoot, DateTime.UtcNow)
            AssetManifest.writeAtomicText (Path.Combine(cacheRoot, "current-release")) manifest.ReleaseId
            AssetRuntimeEnvironment.write releaseRoot manifest.Bindings options.StatusPath options.RuntimeEnvironmentPath
            garbageCollect cacheRoot manifest.ReleaseId options.RetainReleases options.Report
            started.Stop()

            let status =
                { Ready = true
                  Mode = options.Store.ProviderName
                  Provider = options.Store.ProviderName
                  ReleaseId = manifest.ReleaseId
                  ManifestSha256 = manifestSha
                  CacheRoot = cacheRoot
                  CacheHit = downloadedBytes = 0L
                  OfflineManifestUsed = offlineManifest
                  DownloadedBytes = downloadedBytes
                  DurationMs = started.Elapsed.TotalMilliseconds
                  Message =
                    if downloadedBytes = 0L then
                        $"Asset release {manifest.ReleaseId} is ready from the local cache."
                    else
                        $"Asset release {manifest.ReleaseId} is ready after downloading {downloadedBytes} bytes." }

            AssetManifest.writeStatus options.StatusPath status
            options.Report status.Message

            return
                { ReleaseRoot = releaseRoot
                  Manifest = Some manifest
                  Status = status }
        }
