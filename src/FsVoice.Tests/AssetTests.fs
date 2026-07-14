module FsVoice.Tests.AssetTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open FsVoice.Assets
open Xunit

type private MemoryAssetStore(initialObjects: (string * byte array * string) list) =
    let objects = ConcurrentDictionary<string, byte array * string>(StringComparer.Ordinal)
    let downloadOffsets = ConcurrentQueue<int64>()
    let mutable available = true

    do
        for key, bytes, sha256 in initialObjects do
            objects[key] <- bytes, sha256

    member _.Available
        with get () = available
        and set value = available <- value

    member _.DownloadOffsets = downloadOffsets |> Seq.toList
    member _.Contains key = objects.ContainsKey key

    interface IAssetStore with
        member _.ProviderName = "memory"

        member _.GetObjectInfoAsync(key, _) =
            task {
                if not available then
                    raise (IOException "offline")

                return
                    match objects.TryGetValue key with
                    | true, (bytes, sha256) ->
                        Some
                            { Size = int64 bytes.Length
                              Sha256 = Some sha256 }
                    | _ -> None
            }

        member _.DownloadAsync(key, offset, destination, cancellationToken) =
            task {
                if not available then
                    raise (IOException "offline")

                match objects.TryGetValue key with
                | true, (bytes, _) ->
                    downloadOffsets.Enqueue offset
                    let count = bytes.Length - int offset

                    if count < 0 then
                        invalidOp "invalid byte range"

                    do! destination.WriteAsync(bytes.AsMemory(int offset, count), cancellationToken)
                | _ -> invalidOp $"Missing in-memory object {key}"
            }

        member _.PutObjectIfAbsentAsync(key, sourcePath, sha256, _) =
            task {
                if not available then
                    raise (IOException "offline")

                let bytes = File.ReadAllBytes sourcePath

                if objects.TryAdd(key, (bytes, sha256)) then
                    return AssetUploadResult.Uploaded
                else
                    return AssetUploadResult.AlreadyExists
            }

        member _.Dispose() = ()

module private AssetTestHelpers =
    let tempDirectory name =
        let path = Path.Combine(Path.GetTempPath(), "fsvoice-assets-tests", name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory path |> ignore
        path

    let writeRelative (root: string) (relativePath: string) (value: string) =
        let path = AssetManifest.resolveRelativePath root relativePath
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, value, Encoding.UTF8)
        path

    let bindings =
        { GemmaModel = "models/gemma.gguf"
          SttModelDirectory = "models/parakeet"
          VadModel = "models/silero/silero_vad.onnx"
          TtsModelDirectory = "models/pocket"
          VoiceSample = "voices/default.wav"
          IndexBundleDirectory = "indexes" }

    let createSourceRoot () =
        let root = tempDirectory "source"
        writeRelative root "models/gemma.gguf" "gemma" |> ignore
        writeRelative root "models/parakeet/encoder.onnx" "encoder" |> ignore
        writeRelative root "models/silero/silero_vad.onnx" "vad" |> ignore
        writeRelative root "models/pocket/bundle.json" "{}" |> ignore
        writeRelative root "voices/default.wav" "voice" |> ignore
        writeRelative root "indexes/index-bundle.json" "{\"model_id\":\"test-colbert\",\"sources\":[]}" |> ignore
        writeRelative root "indexes/indexes/test.fsci" "index" |> ignore
        root

    let manifestFromSource root releaseId =
        let files =
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.map (fun path ->
                { Path = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')
                  Size = FileInfo(path).Length
                  Sha256 =
                    AssetManifest.sha256FileAsync path CancellationToken.None
                    |> _.GetAwaiter().GetResult() })
            |> Array.sortBy _.Path

        { SchemaVersion = AssetManifest.SchemaVersion
          ReleaseId = releaseId
          CreatedUtc = DateTimeOffset.UtcNow
          Bindings = bindings
          Compatibility =
            { ContractVersion = AssetManifest.ContractVersion
              FsColbertModelId = "test-colbert" }
          Files = files }

    let storeForManifest manifest =
        let sourceRoot = createSourceRoot ()
        let objects =
            manifest.Files
            |> Array.map (fun entry ->
                let path = AssetManifest.resolveRelativePath sourceRoot entry.Path
                AssetCache.objectKey entry.Sha256, File.ReadAllBytes path, entry.Sha256)
            |> Array.toList

        let manifestBytes = AssetManifest.serialize manifest
        let manifestSha = AssetManifest.sha256Bytes manifestBytes
        let store =
            new MemoryAssetStore(
                ("releases/" + manifest.ReleaseId + "/manifest.json", manifestBytes, manifestSha)
                :: objects
            )

        store, manifestSha

[<Fact>]
let ``Asset manifest rejects traversal and missing index manifest`` () =
    let manifest =
        { SchemaVersion = AssetManifest.SchemaVersion
          ReleaseId = "release-1"
          CreatedUtc = DateTimeOffset.UtcNow
          Bindings = AssetTestHelpers.bindings
          Compatibility =
            { ContractVersion = AssetManifest.ContractVersion
              FsColbertModelId = "test" }
          Files =
            [| { Path = "../escape.gguf"
                 Size = 1L
                 Sha256 = String.replicate 64 "a" } |] }

    let errors = AssetManifest.validate manifest
    Assert.Contains(errors, function | ManifestValidationError.InvalidRelativePath "../escape.gguf" -> true | _ -> false)
    Assert.Contains(errors, function | ManifestValidationError.MissingBindingTarget _ -> true | _ -> false)

[<Fact>]
let ``Asset cache prepares a cold release then uses its exact cached manifest offline`` () =
    let source = AssetTestHelpers.createSourceRoot ()
    let manifest = AssetTestHelpers.manifestFromSource source "release-cache"
    let store, manifestSha = AssetTestHelpers.storeForManifest manifest
    let root = AssetTestHelpers.tempDirectory "cache"
    let statusPath = Path.Combine(root, "status.json")
    let environmentPath = Path.Combine(root, "runtime.env")

    let options =
        { Store = store :> IAssetStore
          CacheRoot = root
          ReleaseId = manifest.ReleaseId
          ManifestKey = "releases/release-cache/manifest.json"
          ManifestSha256 = manifestSha
          RuntimeEnvironmentPath = environmentPath
          StatusPath = statusPath
          RetainReleases = 2
          ParallelDownloads = 2
          MaxRetries = 2
          Report = ignore }

    let cold = AssetCache.prepareAsync options CancellationToken.None |> _.GetAwaiter().GetResult()
    Assert.True(cold.Status.Ready)
    Assert.True(cold.Status.DownloadedBytes > 0L)
    Assert.True(File.Exists environmentPath)
    Assert.Contains("LLAMA_CPP_MODEL", File.ReadAllText environmentPath)

    store.Available <- false
    let warm = AssetCache.prepareAsync options CancellationToken.None |> _.GetAwaiter().GetResult()
    Assert.True(warm.Status.Ready)
    Assert.True(warm.Status.CacheHit)
    Assert.True(warm.Status.OfflineManifestUsed)
    Assert.Equal(0L, warm.Status.DownloadedBytes)

[<Fact>]
let ``Asset publisher uploads content-addressed objects and refuses a duplicate release manifest`` () =
    let source = AssetTestHelpers.createSourceRoot ()
    use store = MemoryAssetStore([])
    let output = Path.Combine(AssetTestHelpers.tempDirectory "publish", "values.generated.yaml")

    let options =
        { Store = store :> IAssetStore
          SourceRoot = source
          ReleaseId = "release-publish"
          ManifestKey = "releases/release-publish/manifest.json"
          Bindings = AssetTestHelpers.bindings
          ValuesOutputPath = output
          ParallelUploads = 2
          Report = ignore }

    let first = AssetPublishing.publishAsync options CancellationToken.None |> _.GetAwaiter().GetResult()
    Assert.True(first.UploadedObjects > 0)
    Assert.True(store.Contains "releases/release-publish/manifest.json")
    Assert.Contains("manifestSha256", File.ReadAllText output)

    Assert.Throws<InvalidOperationException>(fun () ->
        AssetPublishing.publishAsync options CancellationToken.None
        |> _.GetAwaiter().GetResult()
        |> ignore)
    |> ignore
