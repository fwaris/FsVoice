param(
    [string]$Configuration = "Release",
    [string]$RuntimeRoot = ".\artifacts\open_source_voice_a100_runtime",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OrtGenAiRoot = "E:\s\repos\onnxruntime-genai",
    [string]$OrtGenAiBuildName = "WindowsNinjaCudaA100Sm80",
    [string]$OrtGenAiBuildDir = "",
    [string]$OrtGenAiManagedDir = "",
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
$OrtGenAiRoot = [System.IO.Path]::GetFullPath($OrtGenAiRoot)

if ([string]::IsNullOrWhiteSpace($OrtGenAiBuildDir)) {
    $OrtGenAiBuildDir = Join-Path $OrtGenAiRoot "build\$OrtGenAiBuildName\Release"
}
if ([string]::IsNullOrWhiteSpace($OrtGenAiManagedDir)) {
    $OrtGenAiManagedDir = Join-Path $OrtGenAiRoot "src\csharp\bin\$Configuration\net8.0"
}
if ([string]::IsNullOrWhiteSpace($OrtNativeDir)) {
    $sm80OrtNativeDir = Join-Path $OrtGenAiBuildDir "_deps\ortlib-src\runtimes\win-x64\native"
    $nugetOrtNativeDir = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.ml.onnxruntime.gpu.windows\$OrtNativePackageVersion\runtimes\win-x64\native"
    if (Test-Path -LiteralPath $nugetOrtNativeDir) {
        $OrtNativeDir = $nugetOrtNativeDir
    } else {
        $OrtNativeDir = $sm80OrtNativeDir
    }
}

$OrtGenAiBuildDir = [System.IO.Path]::GetFullPath($OrtGenAiBuildDir)
$OrtGenAiManagedDir = [System.IO.Path]::GetFullPath($OrtGenAiManagedDir)
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

function Copy-Directory([string]$Source, [string]$Destination) {
    Assert-PathExists $Source "Directory to copy"
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Source.TrimEnd('\').Length + 1)
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

Assert-PathExists (Join-Path $OrtGenAiBuildDir "onnxruntime-genai.dll") "SM80 ORT GenAI native DLL"
Assert-PathExists (Join-Path $OrtGenAiBuildDir "onnxruntime-genai-cuda.dll") "SM80 ORT GenAI CUDA DLL"
Assert-PathExists (Join-Path $OrtGenAiManagedDir "Microsoft.ML.OnnxRuntimeGenAI.dll") "ORT GenAI managed DLL"
Assert-PathExists (Join-Path $OrtNativeDir "onnxruntime.dll") "SM80 ONNX Runtime DLL"
Assert-PathExists (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") "SM80 ONNX Runtime CUDA provider DLL"
Assert-PathExists (Join-Path $OrtNativeDir "onnxruntime_providers_shared.dll") "SM80 ONNX Runtime shared provider DLL"
Assert-CudaBinaryArchitecture (Join-Path $OrtGenAiBuildDir "onnxruntime-genai-cuda.dll") "sm_80" "SM80 ORT GenAI CUDA DLL"
Assert-CudaBinaryArchitecture (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") "sm_80" "SM80 ONNX Runtime CUDA provider DLL"

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
    "--self-contained", "false"
)

if ($NoBuild) {
    $publishArgs += "--no-build"
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-RequiredFile (Join-Path $OrtGenAiManagedDir "Microsoft.ML.OnnxRuntimeGenAI.dll") (Join-Path $runtimeRootFull "Microsoft.ML.OnnxRuntimeGenAI.dll") "ORT GenAI managed DLL"
Copy-RequiredFile (Join-Path $OrtGenAiBuildDir "onnxruntime-genai.dll") (Join-Path $runtimeRootFull "onnxruntime-genai.dll") "SM80 ORT GenAI native DLL"
Copy-RequiredFile (Join-Path $OrtGenAiBuildDir "onnxruntime-genai-cuda.dll") (Join-Path $runtimeRootFull "onnxruntime-genai-cuda.dll") "SM80 ORT GenAI CUDA DLL"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime.dll") (Join-Path $runtimeRootFull "onnxruntime.dll") "SM80 ONNX Runtime DLL"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") (Join-Path $runtimeRootFull "onnxruntime_providers_cuda.dll") "SM80 ONNX Runtime CUDA provider DLL"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime_providers_shared.dll") (Join-Path $runtimeRootFull "onnxruntime_providers_shared.dll") "SM80 ONNX Runtime shared provider DLL"

$publishedNativeDir = Join-Path $runtimeRootFull "runtimes\win-x64\native"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime.dll") (Join-Path $publishedNativeDir "onnxruntime.dll") "SM80 ONNX Runtime DLL"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") (Join-Path $publishedNativeDir "onnxruntime_providers_cuda.dll") "SM80 ONNX Runtime CUDA provider DLL"
Copy-RequiredFile (Join-Path $OrtNativeDir "onnxruntime_providers_shared.dll") (Join-Path $publishedNativeDir "onnxruntime_providers_shared.dll") "SM80 ONNX Runtime shared provider DLL"
if (Test-Path -LiteralPath (Join-Path $OrtNativeDir "onnxruntime_providers_tensorrt.dll")) {
    Copy-Item -LiteralPath (Join-Path $OrtNativeDir "onnxruntime_providers_tensorrt.dll") -Destination (Join-Path $runtimeRootFull "onnxruntime_providers_tensorrt.dll") -Force
}

$paperIndexSource = Join-Path $repoRoot "src\Speak2Docs\Resources\Raw\FsColbertIndexes"
if (Test-Path -LiteralPath (Join-Path $paperIndexSource "index-bundle.json") -PathType Leaf) {
    Copy-Directory $paperIndexSource (Join-Path $runtimeRootFull "FsColbertIndexes")
}

Copy-Item -LiteralPath (Join-Path $scriptRoot "run-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "smoke-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "download-pocket-tts-onnx-assets.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "download-pocket-tts-onnx-v2-assets.ps1") -Destination $runtimeRootFull -Force

foreach ($required in @(
    "FsVoice.OpenSource.Server.exe",
    "FsVoice.OpenSource.Server.dll",
    "FsVoice.OpenSource.Runtime.dll",
    "FsVoice.Retrieval.dll",
    "FsVoice.Ctx.Runtime.dll",
    "FsVoice.Ctx.Contracts.dll",
    "FsVoice.Core.dll",
    "Microsoft.ML.OnnxRuntime.dll",
    "Microsoft.ML.OnnxRuntimeGenAI.dll",
    "Microsoft.ML.Tokenizers.dll",
    "sherpa-onnx.dll",
    "sherpa-onnx-c-api.dll",
    "onnxruntime.dll",
    "onnxruntime-genai.dll",
    "onnxruntime-genai-cuda.dll",
    "onnxruntime_providers_cuda.dll",
    "onnxruntime_providers_shared.dll",
    "download-pocket-tts-onnx-v2-assets.ps1",
    "FsColbert\Models\mxbai-edge-colbert\model_int8.onnx",
    "FsColbertIndexes\index-bundle.json"
)) {
    Assert-PathExists (Join-Path $runtimeRootFull $required) "Published dependency $required"
}

$excludedTtsWeightNames = @(
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
)
$packagedTtsWeights = @(
    Get-ChildItem -LiteralPath $runtimeRootFull -Recurse -File |
    Where-Object { $excludedTtsWeightNames -contains $_.Name }
)
if ($packagedTtsWeights.Count -gt 0) {
    throw "The A100 service package unexpectedly contains TTS model weights: $($packagedTtsWeights.FullName -join ', ')"
}

$readme = @"
# FsVoice OSS A100 Runtime

This is a framework-dependent Windows x64 publish of `FsVoice.OpenSource.Server`.

It runs:

user audio -> Gemma 4 ONNX ASR/reasoning/tools -> Pocket TTS ONNX -> browser audio.

The runtime includes:

- FsVoice OSS server binaries
- built-in FsColbert query encoder model
- default paper index bundle for source QA
- direct ONNX Runtime support for the April Pocket TTS voice-conditioning architecture
- Sherpa ONNX Pocket TTS managed/native Windows x64 runtime
- SM80/A100 ONNX Runtime CUDA native DLLs copied from:
  `$OrtNativeDir
- SM80/A100 ORT GenAI native DLLs copied from:
  `$OrtGenAiBuildDir

The runtime intentionally excludes Gemma, Pocket TTS, and Chatterbox model weights.
Pocket TTS April ONNX v2 is the default because it preserves the newer voice
conditioning path while avoiding the long full-utterance Chatterbox pass. Stage
the Gemma and Pocket TTS assets under:

`G:\Chroma\VoiceAgent_assets\models\gemma-4-e2b-it-onnx-mobius\Q4_K_M\cuda`
`G:\Chroma\VoiceAgent_assets\models\pocket-tts-onnx-english-2026-04`

Download and checksum-validate the tested English April INT8 bundle directly on
the host with:

~~~powershell
.\download-pocket-tts-onnx-v2-assets.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -Precision int8
~~~

The downloader pins the tested `KevinAHM/pocket-tts-onnx` revision and downloads
only the selected language/precision graphs. Model weights remain outside this
service zip. To test FP32, rerun it with `-Precision fp32`; the two precisions can
coexist in the same model directory.

The older sherpa-compatible January runtime remains available explicitly with
`-TtsRuntime pocket-tts-onnx`. Its assets use:

`G:\Chroma\VoiceAgent_assets\models\sherpa-onnx-pocket-tts-int8-2026-01-26`

Download and validate the legacy sherpa model with:

~~~powershell
.\download-pocket-tts-onnx-assets.ps1 -AssetsRoot G:\Chroma\VoiceAgent_assets
~~~

Chatterbox also remains available as a fallback and uses:

`G:\Chroma\VoiceAgent_assets\models\chatterbox-onnx`

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

The SM80 binaries still require the CUDA 12.x runtime DLLs and cuDNN 9 on PATH,
even when the NVIDIA driver reports CUDA 13.2. If needed, pass `-CudaBin` and
`-CudnnBin` to `run-open-source-voice-a100.ps1`.

The launcher defaults the Gemma audio encoder to CPU because its exported Slice
graph has failed on A100 CUDA with `cudaErrorNoKernelImageForDevice`. Gemma token
embedding/decoding remain on CUDA. Both Pocket TTS backends use their optimized
CPU ONNX path by default, which is intentional even on the A100. Set
`-GemmaAudioEncoderExecutionProvider cuda` only after validating a compatible
ONNX Runtime provider on the target.

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
  -TtsRuntime pocket-tts-onnx-v2 `
  -VoiceSamplePath G:\Chroma\VoiceAgent_assets\voices\default_voice.wav `
  -PocketTtsPrecision int8 `
  -PocketTtsTemperature 0.7 `
  -PocketTtsSeed 12345 `
  -TtsNumThreads 4 `
  -TtsNumSteps 4 `
  -CudaBin "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin" `
  -CudnnBin "C:\Program Files\NVIDIA\CUDNN\v9.23\bin\12.9\x64" `
  -WebRtcIcePortStart 50670 `
  -WebRtcIcePortEnd 50679
~~~

Smoke:

~~~powershell
.\smoke-open-source-voice-a100.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -TtsRuntime pocket-tts-onnx-v2 `
  -PocketTtsPrecision int8 `
  -CudaBin "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin" `
  -CudnnBin "C:\Program Files\NVIDIA\CUDNN\v9.23\bin\12.9\x64" `
  -RequireReady
~~~
"@

Set-Content -LiteralPath (Join-Path $runtimeRootFull "README.open-source-voice-a100.md") -Value $readme -Encoding UTF8

$manifest = [pscustomobject]@{
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    runtimeRoot = $runtimeRootFull
    configuration = $Configuration
    runtimeIdentifier = $RuntimeIdentifier
    ortGenAiBuildDir = $OrtGenAiBuildDir
    ortGenAiManagedDir = $OrtGenAiManagedDir
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
    frameworkDependent = $true
    includesModelWeights = $false
    includesPocketTtsRuntime = $true
    pocketTtsRuntimeVersion = "1.13.4"
    includesPocketTtsOnnxV2Runtime = $true
    defaultTtsRuntime = "pocket-tts-onnx-v2"
    pocketTtsOnnxV2AssetRepo = "KevinAHM/pocket-tts-onnx"
    pocketTtsOnnxV2AssetRevision = "58a6d00cf13d239b6748cb0769f35c580a8f606c"
    pocketTtsOnnxV2DefaultPrecision = "int8"
    pocketTtsOnnxV2DefaultNumSteps = 4
    structuredToolFillerDefault = $true
    includesDefaultPaperIndex = Test-Path -LiteralPath (Join-Path $runtimeRootFull "FsColbertIndexes\index-bundle.json")
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
Write-Host "ORT GenAI build dir: $OrtGenAiBuildDir"
Write-Host "ORT native dir: $OrtNativeDir"
