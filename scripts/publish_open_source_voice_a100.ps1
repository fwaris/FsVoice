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
    if (Test-Path -LiteralPath $sm80OrtNativeDir) {
        $OrtNativeDir = $sm80OrtNativeDir
    } else {
        $OrtNativeDir = $nugetOrtNativeDir
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

function Assert-BinaryContainsText([string]$Path, [string]$Needle, [string]$Label) {
    Assert-PathExists $Path $Label
    $bytes = [IO.File]::ReadAllBytes($Path)
    $text = [Text.Encoding]::ASCII.GetString($bytes)
    if (-not $text.Contains($Needle)) {
        throw "$Label does not contain '$Needle'. This binary is not suitable for the A100 SM80 path: $Path"
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
Assert-BinaryContainsText (Join-Path $OrtGenAiBuildDir "onnxruntime-genai-cuda.dll") "sm_80" "SM80 ORT GenAI CUDA DLL"
Assert-BinaryContainsText (Join-Path $OrtNativeDir "onnxruntime_providers_cuda.dll") "sm_80" "SM80 ONNX Runtime CUDA provider DLL"

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
if (Test-Path -LiteralPath (Join-Path $OrtNativeDir "onnxruntime_providers_tensorrt.dll")) {
    Copy-Item -LiteralPath (Join-Path $OrtNativeDir "onnxruntime_providers_tensorrt.dll") -Destination (Join-Path $runtimeRootFull "onnxruntime_providers_tensorrt.dll") -Force
}

$paperIndexSource = Join-Path $repoRoot "src\Speak2Docs\Resources\Raw\FsColbertIndexes"
if (Test-Path -LiteralPath (Join-Path $paperIndexSource "index-bundle.json") -PathType Leaf) {
    Copy-Directory $paperIndexSource (Join-Path $runtimeRootFull "FsColbertIndexes")
}

Copy-Item -LiteralPath (Join-Path $scriptRoot "run-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot "smoke-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force

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
    "onnxruntime.dll",
    "onnxruntime-genai.dll",
    "onnxruntime-genai-cuda.dll",
    "onnxruntime_providers_cuda.dll",
    "onnxruntime_providers_shared.dll",
    "FsColbert\Models\mxbai-edge-colbert\model_int8.onnx",
    "FsColbertIndexes\index-bundle.json"
)) {
    Assert-PathExists (Join-Path $runtimeRootFull $required) "Published dependency $required"
}

$readme = @"
# FsVoice OSS A100 Runtime

This is a framework-dependent Windows x64 publish of `FsVoice.OpenSource.Server`.

It runs:

user audio -> Gemma 4 ONNX ASR/reasoning/tools -> Chatterbox ONNX TTS -> browser WebRTC audio.

The runtime includes:

- FsVoice OSS server binaries
- built-in FsColbert query encoder model
- default paper index bundle for source QA
- SM80/A100 ONNX Runtime CUDA native DLLs copied from:
  `$OrtNativeDir
- SM80/A100 ORT GenAI native DLLs copied from:
  `$OrtGenAiBuildDir

The runtime intentionally excludes Gemma and Chatterbox model weights. Stage those
under an assets folder, for example:

`G:\Chroma\VoiceAgent_assets\models\gemma-4-e2b-it-onnx-mobius\Q4_K_M\cuda`
`G:\Chroma\VoiceAgent_assets\models\chatterbox-onnx`

A100 host note:

The SM80 binaries still require the CUDA 12.x runtime DLLs and cuDNN 9 on PATH,
even when the NVIDIA driver reports CUDA 13.2. If needed, pass `-CudaBin` and
`-CudnnBin` to `run-open-source-voice-a100.ps1`.

Run:

````powershell
.\run-open-source-voice-a100.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -CudaBin "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin" `
  -CudnnBin "C:\Program Files\NVIDIA\CUDNN\v9.12\bin\12.9" `
  -TtsMaxSteps 256
````

Smoke:

````powershell
.\smoke-open-source-voice-a100.ps1 `
  -AssetsRoot G:\Chroma\VoiceAgent_assets `
  -CudaBin "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin" `
  -CudnnBin "C:\Program Files\NVIDIA\CUDNN\v9.12\bin\12.9" `
  -RequireReady
````
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
    cudaTarget = "sm_80"
    frameworkDependent = $true
    includesModelWeights = $false
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
