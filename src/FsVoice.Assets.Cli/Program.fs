namespace FsVoice.Assets.Cli

open System
open System.Collections.Generic
open System.IO
open System.Threading
open FsVoice.Assets

module private Arguments =
    let parse (args: string array) =
        let values = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let mutable index = 0

        while index < args.Length do
            let name = args[index]

            if not (name.StartsWith("--", StringComparison.Ordinal)) then
                invalidArg (nameof args) $"Unexpected argument: {name}"

            if
                index + 1 >= args.Length
                || args[index + 1].StartsWith("--", StringComparison.Ordinal)
            then
                values[name.Substring(2)] <- "true"
                index <- index + 1
            else
                values[name.Substring(2)] <- args[index + 1]
                index <- index + 2

        values

    let optional (values: Dictionary<string, string>) name fallback =
        match values.TryGetValue name with
        | true, value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> fallback

    let required values name =
        let value = optional values name ""

        if String.IsNullOrWhiteSpace value then
            invalidArg name $"--{name} is required."

        value

    let integer values name fallback =
        match Int32.TryParse(optional values name (string fallback)) with
        | true, value -> value
        | _ -> invalidArg name $"--{name} must be an integer."

    let boolean values name fallback =
        match Boolean.TryParse(optional values name (string fallback)) with
        | true, value -> value
        | _ -> invalidArg name $"--{name} must be true or false."

module Program =
    let private env (name: string) =
        Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.defaultValue ""

    let private report (message: string) =
        Console.WriteLine($"asset-bootstrap: {message}")

    let private sourceMode (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "local" -> AssetSource.Local
        | "azureblob"
        | "azure" -> AssetSource.AzureBlob
        | "s3" -> AssetSource.S3
        | _ -> invalidArg (nameof value) "Asset mode must be local, azureBlob, or s3."

    let private createStore (mode: AssetSource) (values: Dictionary<string, string>) =
        match mode with
        | AssetSource.Local -> invalidOp "Local asset mode does not use a cloud object store."
        | AssetSource.AzureBlob ->
            AssetStore.azureBlob
                { AccountUrl = Arguments.required values "azure-account-url"
                  Container = Arguments.required values "azure-container"
                  SasToken = Arguments.optional values "azure-sas-token" (env "FSVOICE_AZURE_SAS_TOKEN")
                  ManagedIdentityClientId =
                    Arguments.optional values "azure-managed-identity-client-id" (env "AZURE_CLIENT_ID") }
        | AssetSource.S3 ->
            AssetStore.s3
                { Bucket = Arguments.required values "s3-bucket"
                  Region = Arguments.optional values "s3-region" (env "AWS_REGION")
                  AccessKeyId = Arguments.optional values "s3-access-key-id" (env "AWS_ACCESS_KEY_ID")
                  SecretAccessKey = Arguments.optional values "s3-secret-access-key" (env "AWS_SECRET_ACCESS_KEY")
                  SessionToken = Arguments.optional values "s3-session-token" (env "AWS_SESSION_TOKEN")
                  ServiceUrl = Arguments.optional values "s3-service-url" ""
                  ForcePathStyle = Arguments.boolean values "s3-force-path-style" false }

    let private prepareLocal values =
        LocalAssetPreparation.prepare
            { GemmaModel = Arguments.required values "gemma-model"
              SttModelDirectory = Arguments.required values "stt-model-dir"
              VadModel = Arguments.required values "vad-model"
              TtsModelDirectory = Arguments.required values "tts-model-dir"
              VoiceSample = Arguments.required values "voice-sample"
              IndexBundleDirectory = Arguments.required values "index-dir"
              RuntimeEnvironmentPath = Arguments.required values "runtime-env"
              StatusPath = Arguments.required values "status-path"
              Report = report }
        |> ignore

    let private prepareRemote mode values =
        use store = createStore mode values

        AssetCache.prepareAsync
            { Store = store
              CacheRoot = Arguments.required values "cache-root"
              ReleaseId = Arguments.required values "release-id"
              ManifestKey = Arguments.required values "manifest-key"
              ManifestSha256 = Arguments.required values "manifest-sha256"
              RuntimeEnvironmentPath = Arguments.required values "runtime-env"
              StatusPath = Arguments.required values "status-path"
              RetainReleases = Arguments.integer values "retain-releases" 2
              ParallelDownloads = Arguments.integer values "parallel-downloads" 4
              MaxRetries = Arguments.integer values "max-retries" 5
              Report = report }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()
        |> ignore

    let private prepare values =
        let mode = Arguments.optional values "mode" "local" |> sourceMode

        match mode with
        | AssetSource.Local -> prepareLocal values
        | AssetSource.AzureBlob
        | AssetSource.S3 -> prepareRemote mode values

    let private bindings values =
        { GemmaModel = Arguments.optional values "gemma-binding" "models/gemma-4-E2B_q4_0-it.gguf"
          SttModelDirectory = Arguments.optional values "stt-binding" "models/parakeet-tdt-0.6b-v3-onnx"
          VadModel = Arguments.optional values "vad-binding" "models/silero-vad-onnx/silero_vad.onnx"
          TtsModelDirectory = Arguments.optional values "tts-binding" "models/pocket-tts-onnx-english-2026-04"
          VoiceSample = Arguments.optional values "voice-binding" "voices/default_voice.wav"
          IndexBundleDirectory = Arguments.optional values "index-binding" "indexes" }

    let private publish values =
        let mode = Arguments.required values "provider" |> sourceMode

        if mode = AssetSource.Local then
            invalidArg "provider" "Publishing requires provider azureBlob or s3."

        use store = createStore mode values
        let releaseId = Arguments.required values "release-id"

        let result =
            AssetPublishing.publishAsync
                { Store = store
                  SourceRoot = Arguments.required values "source-root"
                  ReleaseId = releaseId
                  ManifestKey = Arguments.optional values "manifest-key" $"releases/{releaseId}/manifest.json"
                  Bindings = bindings values
                  ValuesOutputPath = Arguments.required values "values-output"
                  ParallelUploads = Arguments.integer values "parallel-uploads" 4
                  Report = report }
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        report
            $"Published immutable release {releaseId}: {result.UploadedObjects} uploaded, {result.ReusedObjects} reused, {result.UploadedBytes} bytes transferred."

        Console.WriteLine($"ManifestPath={result.ManifestPath}")
        Console.WriteLine($"ManifestSha256={result.ManifestSha256}")

    let private verify values =
        let manifest, sha =
            AssetPublishing.verifyAsync
                (Arguments.required values "manifest")
                (Arguments.optional values "root" "")
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        report $"Verified asset manifest for release {manifest.ReleaseId}."
        Console.WriteLine($"ManifestSha256={sha}")

    let private usage () =
        Console.Error.WriteLine(
            "Usage: FsVoice.Assets.Cli prepare|verify|publish [options]\n"
            + "  prepare --mode local|azureBlob|s3 ...\n"
            + "  verify --manifest <path> [--root <release-root>]\n"
            + "  publish --provider azureBlob|s3 --source-root <path> --release-id <id> --values-output <path> ..."
        )

    [<EntryPoint>]
    let main argv =
        try
            if argv.Length = 0 then
                usage ()
                2
            else
                let values = Arguments.parse argv[1..]

                match argv[0].ToLowerInvariant() with
                | "prepare" -> prepare values
                | "verify" -> verify values
                | "publish" -> publish values
                | command -> invalidArg (nameof argv) $"Unknown asset command: {command}"

                0
        with ex ->
            Console.Error.WriteLine($"asset-bootstrap error: {ex.Message}")
            1
