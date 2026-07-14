# FsVoice cloud asset releases

FsVoice images never contain Gemma, Parakeet, Silero, Pocket TTS, voice, or
FsColbert index assets. A release manifest pins the exact external files that a
deployment must use. The runtime validates every SHA-256 before it starts
llama.cpp or FsVoice.

## Release layout

Cloud storage contains immutable objects and manifests:

~~~text
objects/sha256/ab/<sha256>
releases/<release-id>/manifest.json
~~~

The manifest is created last and names the Gemma model, Parakeet directory,
Silero model, Pocket TTS directory, voice WAV, FsColbert bundle, every file
length, and every SHA-256. A release ID cannot be reused.

Stage only the intended production assets into one source root before publishing:

~~~text
assets-root/
  models/gemma-4-E2B_q4_0-it.gguf
  models/parakeet-tdt-0.6b-v3-onnx/
  models/silero-vad-onnx/silero_vad.onnx
  models/pocket-tts-onnx-english-2026-04/
  voices/default_voice.wav
  indexes/index-bundle.json
  indexes/indexes/*.fsci
~~~

The publisher deliberately ignores unrelated directories under a larger model
cache.

## Publish a release

Build the assets CLI, then publish using CI-provided write credentials. Keep
credentials in the CI secret store rather than command history or repository
files.

~~~powershell
dotnet run --project src/FsVoice.Assets.Cli -- publish `
  --provider azureBlob `
  --source-root E:\release-assets `
  --release-id fsvoice-2026-07-14 `
  --values-output E:\release-output\values.generated.yaml `
  --azure-account-url https://account.blob.core.windows.net `
  --azure-container fsvoice-assets `
  --azure-sas-token $env:FSVOICE_AZURE_SAS_TOKEN
~~~

For S3, replace the provider settings with `--s3-bucket`, `--s3-region`, and
CI-provided `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` values. The generated
values file contains only the release ID, manifest key, and manifest SHA-256;
merge it into the Helm values used for deployment.

Use `verify --manifest <path> --root <assets-root>` to validate a staged or
downloaded release before publishing.

## Helm deployment

The chart is at `deploy/open-source/helm/fsvoice`. It runs one FsVoice container
per StatefulSet replica. Each replica receives its own stable cache directory
under the configured node-local host path; a replacement on another node will
download the pinned release again.

~~~powershell
helm upgrade --install fsvoice deploy/open-source/helm/fsvoice `
  --namespace voice --create-namespace `
  --values deploy-values.yaml
~~~

For `azureBlob` and `s3`, inject credentials into the deployment values from the
pipeline. The chart renders them as a Kubernetes Secret, uses them only during
asset preparation, and unsets them before llama.cpp and FsVoice start. Runtime
credentials must have read-only object access; publishing credentials remain in
the CI system and need write access.

The default `rootPreflight` cache permission mode creates and owns only the
per-pod cache directory, then drops permanently to UID/GID 1654. Set
`assets.cache.permissionMode: preprovisioned` if node automation already creates
the host directory with that ownership.

## Docker Compose and local/A100 deployments

Use `compose.yml` for pre-provisioned local mounts. Use `compose.remote.yml`
with `.env.remote.example` as a starting point for Azure Blob or S3 downloads.
The remote container caches content-addressed files under
`FSVOICE_ASSET_CACHE_DIR`, retains the current and prior release, and can start
offline only when the configured release manifest and every asset are already
verified in that cache.

The Windows A100 launcher remains local-only and continues to use explicit
`AssetsRoot` and `IndexBundleDirectory` paths. It does not download cloud assets
or require Helm configuration.
