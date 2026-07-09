param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [string]$Urls = "http://0.0.0.0:5067",
    [int]$TtsMaxSteps = 256,
    [string]$WorkDir = "served_runs",
    [string]$CudaBin = "",
    [string]$CudnnBin = "",
    [string]$WebRtcBindAddress = "",
    [int]$WebRtcIcePortStart = 0,
    [int]$WebRtcIcePortEnd = 0,
    [switch]$WebRtcIncludeAllInterfaceAddresses
)

$ErrorActionPreference = "Stop"

if ($CudaBin) { $env:PATH = "$CudaBin;$env:PATH" }
if ($CudnnBin) { $env:PATH = "$CudnnBin;$env:PATH" }

function Assert-PathExists([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label was not found: $Path"
    }
}

function Find-DllForProcess([string]$DllName, [string[]]$ExtraDirectories) {
    foreach ($dir in $ExtraDirectories) {
        if ($dir -and (Test-Path -LiteralPath (Join-Path $dir $DllName))) {
            return (Join-Path $dir $DllName)
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:PATH)) {
        foreach ($dir in $env:PATH.Split([IO.Path]::PathSeparator, [StringSplitOptions]::RemoveEmptyEntries)) {
            if ($dir -and (Test-Path -LiteralPath (Join-Path $dir $DllName))) {
                return (Join-Path $dir $DllName)
            }
        }
    }

    return $null
}

function Assert-CudaRuntimeDlls([string]$ServiceDir) {
    $requiredDlls = @(
        "cudart64_12.dll",
        "cublas64_12.dll",
        "cublasLt64_12.dll",
        "cufft64_11.dll",
        "curand64_10.dll",
        "cudnn64_9.dll"
    )
    $missing = @()
    foreach ($dll in $requiredDlls) {
        if (-not (Find-DllForProcess $dll @($ServiceDir))) {
            $missing += $dll
        }
    }
    if ($missing.Count -gt 0) {
        throw "Missing CUDA/cuDNN runtime DLL(s) required by the packaged ONNX Runtime CUDA provider: $($missing -join ', '). Install/stage CUDA 12.x runtime plus cuDNN 9, copy the DLLs next to FsVoice.OpenSource.Server.exe, or rerun this script with -CudaBin and -CudnnBin."
    }
}

$assetsRootFull = (Resolve-Path -LiteralPath $AssetsRoot).Path
$modelsRoot =
    if (Test-Path -LiteralPath (Join-Path $assetsRootFull "gemma-4-e2b-it-onnx-mobius") -PathType Container) {
        $assetsRootFull
    } else {
        Join-Path $assetsRootFull "models"
    }

$gemmaDir = Join-Path $modelsRoot "gemma-4-e2b-it-onnx-mobius\Q4_K_M\cuda"
$chatterboxDir = Join-Path $modelsRoot "chatterbox-onnx"
$voicePath = Join-Path $assetsRootFull "voices\default_voice.wav"
if (-not (Test-Path -LiteralPath $voicePath -PathType Leaf)) {
    $voicePath = Join-Path $chatterboxDir "default_voice.wav"
}

$serviceDir = $PSScriptRoot
$requiredRuntime = @(
    (Join-Path $serviceDir "FsVoice.OpenSource.Server.exe"),
    (Join-Path $serviceDir "onnxruntime.dll"),
    (Join-Path $serviceDir "onnxruntime_providers_cuda.dll"),
    (Join-Path $serviceDir "onnxruntime_providers_shared.dll"),
    (Join-Path $serviceDir "onnxruntime-genai.dll"),
    (Join-Path $serviceDir "onnxruntime-genai-cuda.dll"),
    (Join-Path $serviceDir "Microsoft.ML.OnnxRuntimeGenAI.dll"),
    (Join-Path $serviceDir "FsColbertIndexes\index-bundle.json"),
    (Join-Path $serviceDir "FsColbert\Models\mxbai-edge-colbert\model_int8.onnx")
)

$requiredAssets = @(
    (Join-Path $gemmaDir "genai_config.json"),
    (Join-Path $gemmaDir "tokenizer.json"),
    (Join-Path $gemmaDir "embedding\model.onnx"),
    (Join-Path $gemmaDir "audio_encoder\model.onnx"),
    (Join-Path $gemmaDir "decoder\model.onnx"),
    (Join-Path $chatterboxDir "tokenizer.json"),
    (Join-Path $chatterboxDir "default_voice.wav"),
    (Join-Path $chatterboxDir "onnx\speech_encoder.onnx"),
    (Join-Path $chatterboxDir "onnx\speech_encoder.onnx_data"),
    (Join-Path $chatterboxDir "onnx\embed_tokens.onnx"),
    (Join-Path $chatterboxDir "onnx\embed_tokens.onnx_data"),
    (Join-Path $chatterboxDir "onnx\language_model_q4f16.onnx"),
    (Join-Path $chatterboxDir "onnx\language_model_q4f16.onnx_data"),
    (Join-Path $chatterboxDir "onnx\conditional_decoder.onnx"),
    (Join-Path $chatterboxDir "onnx\conditional_decoder.onnx_data"),
    $voicePath
)

foreach ($path in $requiredRuntime) {
    Assert-PathExists $path "Runtime dependency"
}
foreach ($path in $requiredAssets) {
    Assert-PathExists $path "Model asset"
}
Assert-CudaRuntimeDlls $serviceDir

$env:OpenSourceVoice__WorkDir = $WorkDir
$env:OpenSourceVoice__Gemma__ModelDir = $gemmaDir
$env:OpenSourceVoice__Gemma__Runtime = "raw-onnx"
$env:OpenSourceVoice__Gemma__ExecutionProvider = "cuda"
$env:OpenSourceVoice__Tts__ModelDir = $chatterboxDir
$env:OpenSourceVoice__Tts__Runtime = "chatterbox-onnx"
$env:OpenSourceVoice__Tts__ExecutionProvider = "cuda"
$env:OpenSourceVoice__Tts__Variant = "q4f16"
$env:OpenSourceVoice__Tts__VoiceSamplePath = $voicePath
$env:OpenSourceVoice__Tts__MaxSteps = "$TtsMaxSteps"
if (-not [string]::IsNullOrWhiteSpace($WebRtcBindAddress)) {
    $env:OpenSourceVoice__WebRtc__BindAddress = $WebRtcBindAddress
}
if ($WebRtcIcePortStart -gt 0 -or $WebRtcIcePortEnd -gt 0) {
    $env:OpenSourceVoice__WebRtc__IcePortStart = "$WebRtcIcePortStart"
    $env:OpenSourceVoice__WebRtc__IcePortEnd = "$WebRtcIcePortEnd"
}
if ($WebRtcIncludeAllInterfaceAddresses) {
    $env:OpenSourceVoice__WebRtc__IncludeAllInterfaceAddresses = "true"
}

$exe = Join-Path $PSScriptRoot "FsVoice.OpenSource.Server.exe"

Write-Host "Starting FsVoice open-source backend on $Urls"
Write-Host "AssetsRoot: $assetsRootFull"
Write-Host "GemmaModelDir: $gemmaDir"
Write-Host "ChatterboxModelDir: $chatterboxDir"
if (-not [string]::IsNullOrWhiteSpace($WebRtcBindAddress)) { Write-Host "WebRtcBindAddress: $WebRtcBindAddress" }
if ($WebRtcIcePortStart -gt 0 -or $WebRtcIcePortEnd -gt 0) { Write-Host "WebRtcIcePorts: $WebRtcIcePortStart-$WebRtcIcePortEnd/udp" }
Write-Host "Default paper index: $(Join-Path $serviceDir "FsColbertIndexes\index-bundle.json")"
& $exe --urls $Urls
