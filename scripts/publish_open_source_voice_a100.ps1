param(
    [string]$Configuration = "Release",
    [string]$RuntimeRoot = ".\artifacts\open_source_voice_a100_runtime",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OrtNativeDir = "",
    [string]$OrtNativePackageVersion = "1.27.0",
    [string]$ZipPath = "",
    [switch]$NoBuild,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$project = Join-Path $repoRoot "src\FsVoice.OpenSource.Server\FsVoice.OpenSource.Server.fsproj"
$runtimeRootFull =
    if ([System.IO.Path]::IsPathRooted($RuntimeRoot)) {
        [System.IO.Path]::GetFullPath($RuntimeRoot)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $repoRoot $RuntimeRoot))
    }

if ([string]::IsNullOrWhiteSpace($OrtNativeDir)) {
    $nugetOrtNativeDir = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.ml.onnxruntime.gpu.windows\$OrtNativePackageVersion\runtimes\win-x64\native"
    $OrtNativeDir = $nugetOrtNativeDir
}

$OrtNativeDir = [System.IO.Path]::GetFullPath($OrtNativeDir)

function Assert-PathExists([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label was not found: $Path"
    }
}

function Copy-RequiredFile([string]$Source, [string]$Destination, [string]$Label) {
    Assert-PathExists $Source $Label
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Find-CuObjDump {
    $command = Get-Command cuobjdump.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $tool = Get-ChildItem "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA" -Filter cuobjdump.exe -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $tool) {
        throw "cuobjdump.exe is required to validate A100 SM80 CUDA binaries."
    }
    return $tool.FullName
}

function Assert-CudaBinaryArchitecture([string]$Path, [string]$Architecture, [string]$Label) {
    Assert-PathExists $Path $Label
    $cuobjdump = Find-CuObjDump
    $inventory = (& $cuobjdump --list-elf $Path 2>&1 | Out-String)
    if ($inventory -notmatch "\.$([regex]::Escape($Architecture))\.cubin") {
        $architectures = [regex]::Matches($inventory, "\.sm_[0-9]+\.cubin") |
            ForEach-Object { $_.Value.Trim('.', 'c', 'u', 'b', 'i', 'n') } |
            Sort-Object -Unique
        throw "$Label has no $Architecture cubin. Found: $($architectures -join ', '). Binary: $Path"
    }
}

Assert-PathExists (Join-Path $OrtNativeDir "onnxruntime.dll") "ONNX Runtime $OrtNativePackageVersion DLL"
Assert-PathExists (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") "ONNX Runtime $OrtNativePackageVersion CUDA provider DLL"
Assert-PathExists (Join-Path $OrtNativeDir "onnxruntime_providers_shared.dll") "ONNX Runtime $OrtNativePackageVersion shared provider DLL"
Assert-CudaBinaryArchitecture (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") "sm_80" "ONNX Runtime $OrtNativePackageVersion CUDA provider DLL"

if (Test-Path -LiteralPath $runtimeRootFull) {
    Remove-Item -LiteralPath $runtimeRootFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $runtimeRootFull | Out-Null

$publishArgs = @(
    "publish",
    $project,
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "-o", $runtimeRootFull,
    "--self-contained", "false",
    "-p:OnnxRuntimeFlavor=Cuda"
)

if ($NoBuild) {
    $publishArgs += "--no-build"
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime.dll") (Join-Path $runtimeRootFull "onnxruntime.dll") "ONNX Runtime $OrtNativePackageVersion DLL"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") (Join-Path $runtimeRootFull "onnxruntime_providers_cuda.dll") "ONNX Runtime $OrtNativePackageVersion CUDA provider DLL"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime_providers_shared.dll") (Join-Path $runtimeRootFull "onnxruntime_providers_shared.dll") "ONNX Runtime $OrtNativePackageVersion shared provider DLL"

$publishedNativeDir = Join-Path $runtimeRootFull "runtimes\win-x64\native"
if (Test-Path -LiteralPath $publishedNativeDir -PathType Container) {
    Get-ChildItem -LiteralPath $publishedNativeDir -File -Filter "onnxruntime*.dll" |
        Remove-Item -Force
}

Copy-Item -LiteralPath (Join-Path $scriptRoot "run-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "smoke-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "run-gemma-llama-cpp-windows-a100.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "download-parakeet-onnx-assets.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "download-silero-vad-onnx-assets.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "download-pocket-tts-onnx-v2-assets.ps1") -Destination $runtimeRootFull -Force

# Symbols are not needed for the A100 test runtime and add substantial transfer size.
Get-ChildItem -LiteralPath $runtimeRootFull -Recurse -File -Filter "*.pdb" |
    Remove-Item -Force

foreach ($required in @(
    "FsVoice.OpenSource.Server.exe",
    "FsVoice.OpenSource.Server.dll",
    "FsVoice.OpenSource.Runtime.dll",
    "FsVoice.Retrieval.dll",
    "FsVoice.Ctx.Runtime.dll",
    "FsVoice.Ctx.Contracts.dll",
    "FsVoice.Core.dll",
    "Microsoft.ML.OnnxRuntime.dll",
    "Microsoft.ML.Tokenizers.dll",
    "onnxruntime.dll",
    "onnxruntime_providers_cuda.dll",
    "onnxruntime_providers_shared.dll",
    "run-gemma-llama-cpp-windows-a100.ps1",
    "download-parakeet-onnx-assets.ps1",
    "download-silero-vad-onnx-assets.ps1",
    "download-pocket-tts-onnx-v2-assets.ps1",
    "FsColbert\Models\mxbai-edge-colbert\model_int8.onnx"
)) {
    Assert-PathExists (Join-Path $runtimeRootFull $required) "Published dependency $required"
}

$excludedModelWeightNames = @(
    "encoder-model.onnx",
    "encoder-model.onnx.data",
    "encoder-model.int8.onnx",
    "decoder_joint-model.onnx",
    "decoder_joint-model.int8.onnx",
    "nemo128.onnx",
    "flow_lm_main.onnx",
    "flow_lm_main_int8.onnx",
    "flow_lm_flow.onnx",
    "flow_lm_flow_int8.onnx",
    "mimi_encoder.onnx",
    "mimi_decoder.onnx",
    "mimi_decoder_int8.onnx",
    "text_conditioner.onnx",
    "lm_main.int8.onnx",
    "lm_flow.int8.onnx",
    "decoder.int8.onnx"
    "silero_vad.onnx"
)
$packagedModelWeights = @(
    Get-ChildItem -LiteralPath $runtimeRootFull -Recurse -File |
    Where-Object { $excludedModelWeightNames -contains $_.Name }
)
if ($packagedModelWeights.Count -gt 0) {
    throw "The A100 service package unexpectedly contains VAD/STT/TTS model weights: $($packagedModelWeights.FullName -join ', ')"
}

$packagedIndexes = @(
    Get-ChildItem -LiteralPath $runtimeRootFull -Recurse -File |
    Where-Object { $_.Extension -eq ".fsci" -or $_.Name -eq "index-bundle.json" }
)
if ($packagedIndexes.Count -gt 0) {
    throw "The A100 service package unexpectedly contains an index bundle: $($packagedIndexes.FullName -join ', ')"
}

$forbiddenLegacyRuntimeNames = @(
    "TorchSharp.dll",
    "LibTorchSharp.dll",
    "torch_cpu.dll",
    "sherpa-onnx.dll",
    "sherpa-onnx-c-api.dll"
)
$packagedLegacyRuntimes = @(
    Get-ChildItem -LiteralPath $runtimeRootFull -Recurse -File |
    Where-Object { $forbiddenLegacyRuntimeNames -contains $_.Name }
)
if ($packagedLegacyRuntimes.Count -gt 0) {
    throw "The A100 service package unexpectedly contains removed runtime files: $($packagedLegacyRuntimes.FullName -join ', ')"
}

$readme = @"
# FsVoice OSS A100 Runtime

This is a framework-dependent Windows x64 publish of `FsVoice.OpenSource.Server`.

It runs:

user audio -> Parakeet TDT ONNX STT -> llama.cpp Gemma 4 GGUF -> Pocket TTS ONNX -> browser audio.

The runtime includes:

- FsVoice OSS server binaries
- built-in FsColbert query encoder model
- Parakeet TDT STT integration with its ONNX preprocessor, encoder, and decoder
- direct ONNX Runtime support for the April Pocket TTS voice-conditioning architecture
- llama.cpp integration for the Gemma 4 GGUF text model
- official ONNX Runtime GPU $OrtNativePackageVersion CUDA 13 native DLLs, with SM80 cubins verified, copied from:
  `$OrtNativeDir

The runtime intentionally excludes Gemma, Silero VAD, Parakeet, Pocket TTS model weights,
and FsColbert index bundles. Stage the assets under:

`G:\Chroma\VoiceAgent_assets\models\gemma-4-E2B_q4_0-it.gguf`
`G:\Chroma\VoiceAgent_assets\models\parakeet-tdt-0.6b-v3-onnx`
`G:\Chroma\VoiceAgent_assets\models\silero-vad-onnx\silero_vad.onnx`
`G:\Chroma\VoiceAgent_assets\models\pocket-tts-onnx-english-2026-04`
`G:\Chroma\VoiceAgent_assets\indexes\faa-v1\index-bundle.json`

The index directory is required at startup and can contain one multi-source
bundle. Replace it with a new versioned directory and restart FsVoice; the
runtime reads the bundle directly and never copies it into `served_runs`.

Download and checksum-validate the pinned Parakeet FP32 STT bundle directly on
the host with:

~~~powershell
.\download-parakeet-onnx-assets.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -Precision fp32
~~~

Download and checksum-validate Silero VAD v6.2.1 directly into the shared
models directory with:

~~~powershell
.\download-silero-vad-onnx-assets.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets
~~~

Silero runs on CPU and performs continuous server-side turn endpointing. The
launcher enables barge-in by default; pass `-AllowBargeIn false` for half-duplex
operation on systems where speaker echo causes interruptions.

For a smaller CPU-oriented Parakeet bundle, use `-Precision int8`. Both
precisions can coexist in the model directory.

Download and checksum-validate the tested English April Pocket TTS INT8 bundle
directly on the host with:

~~~powershell
.\download-pocket-tts-onnx-v2-assets.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -Precision int8
~~~

The downloader pins the tested `KevinAHM/pocket-tts-onnx` revision and downloads
only the selected language/precision graphs. Model weights remain outside this
service zip. To test FP32, rerun it with `-Precision fp32`; the two precisions can
coexist in the same model directory.

Tool filler note:

The launcher defaults `-UseStructuredToolFiller `$true`. Gemma emits the tool call
and a required `spoken_filler` field in one generation. FsVoice removes that field
before invoking the tool and validates it before UI/TTS; missing or unsafe filler
uses a deterministic phrase. To compare with the older second-generation flow,
start with `-UseStructuredToolFiller `$false`.

Gemma thought-channel note:

The launcher defaults `-EnableThinking `$true` and parses Gemma 4's official
`<|channel>thought ... <channel|>` response framing before tool handling, UI,
or TTS. Parsed thought text is discarded from persisted turn artifacts and
normal logs retain only its presence and character count. Use
`-LogThoughtText `$true` only for explicit debugging because it writes the raw
thought text to service logs.

A100 host note:

The official ONNX Runtime 1.27 provider requires CUDA 13.x and cuDNN 9 on PATH.
This package targets the CUDA 13.2 A100 host and verifies that the provider
contains native SM80 cubins. If needed, pass `-CudaBin` and `-CudnnBin` to
`run-open-source-voice-a100.ps1`.

Gemma is text-only in this build. Parakeet owns the complete STT path and runs
on CUDA by default. Pocket TTS remains on its existing optimized CPU ONNX path,
which is intentional even on the A100.

Gemma llama.cpp service:

Start the native Windows llama.cpp CUDA server in a separate PowerShell window.
The launcher uses a 16,384-token context by default and the installed b9987 CUDA
13.3 folder:

~~~powershell
.\run-gemma-llama-cpp-windows-a100.ps1 `
  -LlamaCppRoot G:\Chroma\llama-b9987-bin-win-cuda-13.3-x64 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -ContextSize 16384

.\run-open-source-voice-a100.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -IndexBundleDirectory G:\Chroma\VoiceAgent_assets\indexes\faa-v1 `
  -LlamaCppEndpoint http://127.0.0.1:8081
~~~

FsVoice keeps the most recent 10 completed user/assistant turns in each Gemma
prompt. Older turn artifacts remain on disk but roll out of the active model
context. The llama.cpp executable owns only Gemma text generation; it does not
change Parakeet STT or Pocket TTS.

Remote browser note:

The page tries WebRTC first and automatically falls back to WebSocket PCM when
ICE cannot connect. The fallback uses the same HTTP listener and needs no TURN
server or UDP forwarding. A single SSH TCP forward is enough:

`ssh -L 5067:127.0.0.1:5067 user@a100-host`

Open `http://localhost:5067` on the client. The status message will identify
the WebSocket transport after the WebRTC attempt times out.
Open `http://localhost:5067/?transport=websocket` to select it immediately.

For lower-overhead WebRTC media, expose or forward both:

- TCP 5067 -> A100 TCP 5067
- UDP 50670-50679 -> A100 UDP 50670-50679

Then start the server with `-WebRtcIcePortStart 50670 -WebRtcIcePortEnd 50679`.
Use `-WebRtcBindAddress` only when the A100 has multiple NICs and SIPSorcery
chooses the wrong local address.

Run:

~~~powershell
.\run-open-source-voice-a100.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -IndexBundleDirectory G:\Chroma\VoiceAgent_assets\indexes\faa-v1 `
  -VoiceSamplePath G:\Chroma\VoiceAgent_assets\voices\default_voice.wav `
  -PocketTtsPrecision int8 `
  -PocketTtsTemperature 0.7 `
  -PocketTtsSeed 12345 `
  -TtsNumThreads 4 `
  -TtsNumSteps 4 `
  -ParakeetPrecision fp32 `
  -CudaBin "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.2\bin\x64" `
  -CudnnBin "C:\Program Files\NVIDIA\CUDNN\v9.23\bin\13.2\x64" `
  -WebRtcIcePortStart 50670 `
  -WebRtcIcePortEnd 50679
~~~

Smoke:

~~~powershell
.\smoke-open-source-voice-a100.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -IndexBundleDirectory G:\Chroma\VoiceAgent_assets\indexes\faa-v1 `
  -ParakeetPrecision fp32 `
  -PocketTtsPrecision int8 `
  -CudaBin "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.2\bin\x64" `
  -CudnnBin "C:\Program Files\NVIDIA\CUDNN\v9.23\bin\13.2\x64" `
  -RequireReady
~~~
"@

Set-Content -LiteralPath (Join-Path $runtimeRootFull "README.open-source-voice-a100.md") -Value $readme -Encoding UTF8

$manifest = [pscustomobject]@{
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    runtimeRoot = $runtimeRootFull
    configuration = $Configuration
    runtimeIdentifier = $RuntimeIdentifier
    onnxRuntimePackage = "Microsoft.ML.OnnxRuntime.Gpu"
    onnxRuntimeVersion = $OrtNativePackageVersion
    ortNativeDir = $OrtNativeDir
    ortNativeFiles = @(
        "onnxruntime.dll"
        "onnxruntime_providers_cuda.dll"
        "onnxruntime_providers_shared.dll"
    ) | ForEach-Object {
        $file = Get-Item -LiteralPath (Join-Path $runtimeRootFull $_)
        [pscustomobject]@{
            name = $_
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    cudaTarget = "sm_80"
    cudaRuntime = "13.2"
    cudnnMajor = 9
    frameworkDependent = $true
    includesDebugSymbols = $false
    includesModelWeights = $false
    includesTorchRuntime = $false
    sttRuntime = "parakeet-tdt-onnx"
    parakeetAssetRepo = "istupakov/parakeet-tdt-0.6b-v3-onnx"
    parakeetAssetRevision = "8f23f0c03c8761650bdb5b40aaf3e40d2c15f1ce"
    parakeetDefaultPrecision = "fp32"
    gemmaTextOnly = $true
    gemmaRuntime = "llama.cpp"
    llamaCppContextSize = 16384
    maxHistoryTurns = 10
    llamaCppWindowsBuild = "b9987"
    llamaCppWindowsCuda = "13.3"
    llamaCppWindowsDefaultRoot = "G:\Chroma\llama-b9987-bin-win-cuda-13.3-x64"
    includesPocketTtsOnnxV2Runtime = $true
    vadRuntime = "silero-vad-onnx"
    sileroVadVersion = "6.2.1"
    allowBargeInDefault = $true
    includesVadModel = $false
    ttsRuntime = "pocket-tts-onnx-v2"
    pocketTtsOnnxV2AssetRepo = "KevinAHM/pocket-tts-onnx"
    pocketTtsOnnxV2AssetRevision = "58a6d00cf13d239b6748cb0769f35c580a8f606c"
    pocketTtsOnnxV2DefaultPrecision = "int8"
    pocketTtsOnnxV2DefaultNumSteps = 4
    structuredToolFillerDefault = $true
    includesIndexBundle = $false
    requiresExternalIndexBundle = $true
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtimeRootFull "open_source_voice_a100_manifest.json") -Encoding UTF8

$sizeBytes =
    Get-ChildItem -LiteralPath $runtimeRootFull -Recurse -File |
    Measure-Object -Property Length -Sum |
    Select-Object -ExpandProperty Sum

if (-not $NoZip) {
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        $ZipPath = "$runtimeRootFull.zip"
    } elseif (-not [System.IO.Path]::IsPathRooted($ZipPath)) {
        $ZipPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ZipPath))
    } else {
        $ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
    }

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Compress-Archive -Path (Join-Path $runtimeRootFull "*") -DestinationPath $ZipPath -CompressionLevel Optimal
}

Write-Host "RuntimeRoot: $runtimeRootFull"
Write-Host ("ExpandedSizeMB: {0:n1}" -f ($sizeBytes / 1MB))
if (-not $NoZip) {
    $zipItem = Get-Item -LiteralPath $ZipPath
    Write-Host "ZipPath: $($zipItem.FullName)"
    Write-Host ("ZipSizeMB: {0:n1}" -f ($zipItem.Length / 1MB))
}
Write-Host "ONNX Runtime version: $OrtNativePackageVersion"
Write-Host "ORT native dir: $OrtNativeDir"
Write-Host "CUDA target: 13.2 / sm_80"
