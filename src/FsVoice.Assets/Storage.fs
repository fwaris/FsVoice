namespace FsVoice.Assets

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Threading
open System.Threading.Tasks
open Azure
open Azure.Identity
open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Amazon
open Amazon.Runtime
open Amazon.S3
open Amazon.S3.Model

module private StorageMetadata =
    let tryValue (name: string) (metadata: seq<KeyValuePair<string, string>>) =
        metadata
        |> Seq.tryFind (fun item -> String.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map _.Value
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

type AzureBlobAssetStore(options: AzureBlobStoreOptions) =
    let accountUrl =
        if String.IsNullOrWhiteSpace options.AccountUrl then
            invalidArg (nameof options.AccountUrl) "Azure Blob accountUrl is required."

        options.AccountUrl.TrimEnd('/')

    let container =
        if String.IsNullOrWhiteSpace options.Container then
            invalidArg (nameof options.Container) "Azure Blob container is required."

        options.Container

    let containerUri = Uri($"{accountUrl}/{Uri.EscapeDataString container}")

    let containerClient =
        if not (String.IsNullOrWhiteSpace options.SasToken) then
            let credential = AzureSasCredential(options.SasToken.Trim().TrimStart('?'))
            BlobContainerClient(containerUri, credential)
        else
            let credentialOptions = DefaultAzureCredentialOptions()

            if not (String.IsNullOrWhiteSpace options.ManagedIdentityClientId) then
                credentialOptions.ManagedIdentityClientId <- options.ManagedIdentityClientId

            BlobContainerClient(containerUri, DefaultAzureCredential(credentialOptions))

    let blob key = containerClient.GetBlobClient key

    interface IAssetStore with
        member _.ProviderName = "azureBlob"

        member _.GetObjectInfoAsync(key, cancellationToken) =
            task {
                try
                    let! response = (blob key).GetPropertiesAsync(cancellationToken = cancellationToken)

                    let sha256 =
                        response.Value.Metadata :> seq<KeyValuePair<string, string>>
                        |> StorageMetadata.tryValue "sha256"

                    return
                        Some
                            { Size = response.Value.ContentLength
                              Sha256 = sha256 }
                with :? RequestFailedException as ex when ex.Status = 404 ->
                    return None
            }

        member _.DownloadAsync(key, offset, destination, cancellationToken) =
            task {
                let downloadOptions = BlobDownloadOptions()
                downloadOptions.Range <- HttpRange(offset)
                let! response = (blob key).DownloadStreamingAsync(downloadOptions, cancellationToken)
                use content = response.Value.Content
                do! content.CopyToAsync(destination, 1024 * 1024, cancellationToken)
            }

        member _.PutObjectIfAbsentAsync(key, sourcePath, sha256, cancellationToken) =
            task {
                let metadata = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                metadata["sha256"] <- sha256
                metadata["size"] <- FileInfo(sourcePath).Length.ToString(Globalization.CultureInfo.InvariantCulture)
                let conditions = BlobRequestConditions()
                conditions.IfNoneMatch <- ETag.All
                let uploadOptions = BlobUploadOptions()
                uploadOptions.Metadata <- metadata
                uploadOptions.Conditions <- conditions

                try
                    let! _ = (blob key).UploadAsync(sourcePath, uploadOptions, cancellationToken)
                    return AssetUploadResult.Uploaded
                with :? RequestFailedException as ex when ex.Status = 409 || ex.Status = 412 ->
                    return AssetUploadResult.AlreadyExists
            }

        member _.Dispose() = ()

type S3AssetStore(options: S3StoreOptions) =
    let bucket =
        if String.IsNullOrWhiteSpace options.Bucket then
            invalidArg (nameof options.Bucket) "S3 bucket is required."

        options.Bucket

    let config = AmazonS3Config()

    do
        if not (String.IsNullOrWhiteSpace options.ServiceUrl) then
            config.ServiceURL <- options.ServiceUrl
            config.ForcePathStyle <- options.ForcePathStyle
        elif not (String.IsNullOrWhiteSpace options.Region) then
            config.RegionEndpoint <- RegionEndpoint.GetBySystemName options.Region

    let credentials =
        if
            String.IsNullOrWhiteSpace options.AccessKeyId
            && String.IsNullOrWhiteSpace options.SecretAccessKey
        then
            None
        elif
            String.IsNullOrWhiteSpace options.AccessKeyId
            || String.IsNullOrWhiteSpace options.SecretAccessKey
        then
            invalidArg
                (nameof options)
                "Both S3 accessKeyId and secretAccessKey are required when static credentials are used."
        elif String.IsNullOrWhiteSpace options.SessionToken then
            Some(BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey) :> AWSCredentials)
        else
            Some(
                SessionAWSCredentials(options.AccessKeyId, options.SecretAccessKey, options.SessionToken)
                :> AWSCredentials
            )

    let client =
        match credentials with
        | Some value -> new AmazonS3Client(value, config)
        | None -> new AmazonS3Client(config)

    let metadataSha256 (metadata: MetadataCollection) =
        [ "sha256"; "x-amz-meta-sha256" ]
        |> List.tryPick (fun name ->
            let value = metadata[name]
            if String.IsNullOrWhiteSpace value then None else Some value)

    interface IAssetStore with
        member _.ProviderName = "s3"

        member _.GetObjectInfoAsync(key, cancellationToken) =
            task {
                let request = GetObjectMetadataRequest(BucketName = bucket, Key = key)

                try
                    let! response = client.GetObjectMetadataAsync(request, cancellationToken)

                    return
                        Some
                            { Size = response.ContentLength
                              Sha256 = metadataSha256 response.Metadata }
                with :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
                    return None
            }

        member _.DownloadAsync(key, offset, destination, cancellationToken) =
            task {
                let request = GetObjectRequest(BucketName = bucket, Key = key)

                if offset > 0L then
                    request.ByteRange <- ByteRange($"bytes={offset}-")

                use! response = client.GetObjectAsync(request, cancellationToken)
                do! response.ResponseStream.CopyToAsync(destination, 1024 * 1024, cancellationToken)
            }

        member _.PutObjectIfAbsentAsync(key, sourcePath, sha256, cancellationToken) =
            task {
                let request =
                    PutObjectRequest(BucketName = bucket, Key = key, FilePath = sourcePath)

                request.Metadata["sha256"] <- sha256

                request.Metadata["size"] <-
                    FileInfo(sourcePath).Length.ToString(Globalization.CultureInfo.InvariantCulture)

                request.IfNoneMatch <- "*"

                try
                    let! _ = client.PutObjectAsync(request, cancellationToken)
                    return AssetUploadResult.Uploaded
                with :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed ->
                    return AssetUploadResult.AlreadyExists
            }

        member _.Dispose() = client.Dispose()

module AssetStore =
    let azureBlob options =
        new AzureBlobAssetStore(options) :> IAssetStore

    let s3 options =
        new S3AssetStore(options) :> IAssetStore

    let readBytesAsync (store: IAssetStore) key cancellationToken =
        task {
            use output = new MemoryStream()
            do! store.DownloadAsync(key, 0L, output, cancellationToken)
            return output.ToArray()
        }
