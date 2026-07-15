namespace FsVoice.Assets

open System
open System.IO
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type AssetSource =
    | Local
    | AzureBlob
    | S3

[<CLIMutable>]
type AssetRuntimeBindings =
    { GemmaModel: string
      SttModelDirectory: string
      VadModel: string
      TtsModelDirectory: string
      VoiceSample: string
      IndexBundleDirectory: string }

[<CLIMutable>]
type AssetCompatibility =
    { ContractVersion: int
      FsColbertModelId: string }

[<CLIMutable>]
type AssetFileEntry =
    { Path: string
      Size: int64
      Sha256: string }

[<CLIMutable>]
type AssetReleaseManifest =
    { SchemaVersion: int
      ReleaseId: string
      CreatedUtc: DateTimeOffset
      Bindings: AssetRuntimeBindings
      Compatibility: AssetCompatibility
      Files: AssetFileEntry array }

[<CLIMutable>]
type AssetBootstrapStatus =
    { Ready: bool
      Mode: string
      Provider: string
      ReleaseId: string
      ManifestSha256: string
      CacheRoot: string
      CacheHit: bool
      OfflineManifestUsed: bool
      DownloadedBytes: int64
      DurationMs: float
      Message: string }

type AssetObjectInfo = { Size: int64; Sha256: string option }

[<RequireQualifiedAccess>]
type AssetUploadResult =
    | Uploaded
    | AlreadyExists

type IAssetStore =
    inherit IDisposable

    abstract ProviderName: string

    abstract GetObjectInfoAsync: key: string * cancellationToken: CancellationToken -> Task<AssetObjectInfo option>

    abstract DownloadAsync:
        key: string * offset: int64 * destination: Stream * cancellationToken: CancellationToken -> Task

    abstract PutObjectIfAbsentAsync:
        key: string * sourcePath: string * sha256: string * cancellationToken: CancellationToken ->
            Task<AssetUploadResult>

type AzureBlobStoreOptions =
    { AccountUrl: string
      Container: string
      SasToken: string
      ManagedIdentityClientId: string }

type S3StoreOptions =
    { Bucket: string
      Region: string
      AccessKeyId: string
      SecretAccessKey: string
      SessionToken: string
      ServiceUrl: string
      ForcePathStyle: bool }

type AssetPrepareOptions =
    { Store: IAssetStore
      CacheRoot: string
      ReleaseId: string
      ManifestKey: string
      ManifestSha256: string
      RuntimeEnvironmentPath: string
      StatusPath: string
      RetainReleases: int
      ParallelDownloads: int
      MaxRetries: int
      Report: string -> unit }

type LocalAssetPrepareOptions =
    { GemmaModel: string
      SttModelDirectory: string
      VadModel: string
      TtsModelDirectory: string
      VoiceSample: string
      IndexBundleDirectory: string
      RuntimeEnvironmentPath: string
      StatusPath: string
      Report: string -> unit }

type AssetPrepareResult =
    { ReleaseRoot: string
      Manifest: AssetReleaseManifest option
      Status: AssetBootstrapStatus }

type AssetPublishOptions =
    { Store: IAssetStore
      SourceRoot: string
      ReleaseId: string
      ManifestKey: string
      Bindings: AssetRuntimeBindings
      ValuesOutputPath: string
      ParallelUploads: int
      Report: string -> unit }

type AssetPublishResult =
    { Manifest: AssetReleaseManifest
      ManifestPath: string
      ManifestSha256: string
      UploadedObjects: int
      ReusedObjects: int
      UploadedBytes: int64 }
