param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [Parameter(Mandatory = $true)]
    [string]$IndexBundleDirectory,
    [string]$Urls = "http://0.0.0.0:5067",
    [string]$LlamaCppEndpoint = "http://127.0.0.1:8081",
    [string]$LlamaCppModel = "gemma-4-E2B_q4_0-it.gguf",
    [ValidateRange(8192, 131072)]
    [int]$MinimumLlamaCppContextSize = 16384,
    [string]$ParakeetModelDir = "",
    [ValidateSet("fp32", "int8")]
    [string]$ParakeetPrecision = "fp32",
    [ValidateSet("cpu", "cuda")]
    [string]$ParakeetExecutionProvider = "cuda",
    [int]$ParakeetNumThreads = 4,
    [string]$VadModelPath = "",
    [ValidateSet("true", "false", "1", "0")]
    [string]$AllowBargeIn = "true",
    [int]$TtsNumThreads = 2,
    [ValidateRange(0, 64)]
    [int]$TtsNumSteps = 0,
    [int]$TtsVoiceEmbeddingCacheCapacity = 4,
    [string]$PocketTtsModelDir = "",
    [ValidateSet("int8", "fp32")]
    [string]$PocketTtsPrecision = "int8",
    [ValidateRange(0.0, 5.0)]
    [double]$PocketTtsTemperature = 0.7,
    [ValidateRange(0, 2147483647)]
    [int]$PocketTtsSeed = 12345,
    [string]$WorkDir = "served_runs",
    [ValidateRange(0, 100)]
    [int]$MaxHistoryTurns = 10,
    [string]$VoiceSamplePath = "",
    [ValidateSet("true", "false", "1", "0")]
    [string]$UseStructuredToolFiller = "true",
    [ValidateSet("true", "false", "1", "0")]
    [string]$EnableThinking = "true",
    [ValidateSet("true", "false", "1", "0")]
    [string]$LogThoughtText = "false",
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

function ConvertTo-Boolean([string]$Value) {
    return $Value -eq "true" -or $Value -eq "1"
}

function Assert-LlamaCppService([string]$Endpoint, [int]$MinimumContextSize) {
    $baseUri = $Endpoint.TrimEnd('/')

    try {
        $health = Invoke-RestMethod -Uri "$baseUri/health" -TimeoutSec 5
        if ($health.status -ne "ok") {
            throw "Health status was '$($health.status)'."
        }

        $props = Invoke-RestMethod -Uri "$baseUri/props" -TimeoutSec 5
        $actualContextSize = [int]$props.default_generation_settings.n_ctx

        if ($actualContextSize -lt $MinimumContextSize) {
            throw "llama.cpp is running with n_ctx=$actualContextSize, below the required $MinimumContextSize. Restart run-gemma-llama-cpp-windows-a100.ps1 with -ContextSize $MinimumContextSize."
        }

        return $actualContextSize
    } catch {
        throw "llama.cpp preflight failed at $baseUri. Start or restart run-gemma-llama-cpp-windows-a100.ps1 with -ContextSize $MinimumContextSize. $($_.Exception.Message)"
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
        "cublas64_13.dll",
        "cublasLt64_13.dll",
        "cufft64_12.dll",
        "cudnn64_9.dll"
    )
    $missing = @()
    foreach ($dll in $requiredDlls) {
        if (-not (Find-DllForProcess $dll @($ServiceDir))) {
            $missing += $dll
        }
    }
    if ($missing.Count -gt 0) {
        throw "Missing CUDA/cuDNN runtime DLL(s) required by ONNX Runtime 1.27: $($missing -join ', '). Install/stage CUDA 13.x plus cuDNN 9, copy the DLLs next to FsVoice.OpenSource.Server.exe, or rerun this script with -CudaBin and -CudnnBin."
    }
}

$assetsRootFull = (Resolve-Path -LiteralPath $AssetsRoot).Path
$indexBundleDirectoryFull = (Resolve-Path -LiteralPath $IndexBundleDirectory).Path
$useStructuredToolFillerValue = ConvertTo-Boolean $UseStructuredToolFiller
$enableThinkingValue = ConvertTo-Boolean $EnableThinking
$logThoughtTextValue = ConvertTo-Boolean $LogThoughtText
$allowBargeInValue = ConvertTo-Boolean $AllowBargeIn
$modelsRoot =
    if (
        (Test-Path -LiteralPath (Join-Path $assetsRootFull "parakeet-tdt-0.6b-v3-onnx") -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $assetsRootFull "silero-vad-onnx") -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $assetsRootFull "pocket-tts-onnx-english-2026-04") -PathType Container)
    ) {
        $assetsRootFull
    } else {
        Join-Path $assetsRootFull "models"
    }
$vadPath =
    if ([string]::IsNullOrWhiteSpace($VadModelPath)) {
        Join-Path $modelsRoot "silero-vad-onnx\silero_vad.onnx"
    } elseif ([IO.Path]::IsPathRooted($VadModelPath)) {
        [IO.Path]::GetFullPath($VadModelPath)
    } else {
        [IO.Path]::GetFullPath((Join-Path $assetsRootFull $VadModelPath))
    }

$parakeetDir =
    if ([string]::IsNullOrWhiteSpace($ParakeetModelDir)) {
        Join-Path $modelsRoot "parakeet-tdt-0.6b-v3-onnx"
    } elseif ([IO.Path]::IsPathRooted($ParakeetModelDir)) {
        [IO.Path]::GetFullPath($ParakeetModelDir)
    } else {
        [IO.Path]::GetFullPath((Join-Path $assetsRootFull $ParakeetModelDir))
    }
$pocketTtsDir =
    if ([string]::IsNullOrWhiteSpace($PocketTtsModelDir)) {
        Join-Path $modelsRoot "pocket-tts-onnx-english-2026-04"
    } elseif ([IO.Path]::IsPathRooted($PocketTtsModelDir)) {
        [IO.Path]::GetFullPath($PocketTtsModelDir)
    } else {
        [IO.Path]::GetFullPath((Join-Path $assetsRootFull $PocketTtsModelDir))
    }
$effectiveTtsNumSteps =
    if ($TtsNumSteps -gt 0) {
        $TtsNumSteps
    } else {
        4
    }
$voicePath =
    if (-not [string]::IsNullOrWhiteSpace($VoiceSamplePath)) {
        if ([IO.Path]::IsPathRooted($VoiceSamplePath)) {
            [IO.Path]::GetFullPath($VoiceSamplePath)
        } else {
            [IO.Path]::GetFullPath((Join-Path $assetsRootFull $VoiceSamplePath))
        }
    } else {
        $preferredVoice = Join-Path $assetsRootFull "voices\default_voice.wav"
        if (Test-Path -LiteralPath $preferredVoice -PathType Leaf) {
            $preferredVoice
        } else {
            Join-Path $assetsRootFull "voices\default_voice.wav"
        }
    }

if (-not [IO.Path]::GetExtension($voicePath).Equals(".wav", [StringComparison]::OrdinalIgnoreCase)) {
    throw "VoiceSamplePath must be a WAV file. Convert MP3 references to PCM WAV before starting FsVoice: $voicePath"
}

$serviceDir = $PSScriptRoot
$requiredRuntime = @(
    (Join-Path $serviceDir "FsVoice.OpenSource.Server.exe"),
    (Join-Path $serviceDir "onnxruntime.dll"),
    (Join-Path $serviceDir "onnxruntime_providers_cuda.dll"),
    (Join-Path $serviceDir "onnxruntime_providers_shared.dll"),
    (Join-Path $serviceDir "FsColbert\Models\mxbai-edge-colbert\model_int8.onnx")
)

$requiredAssets = @()
$requiredAssets += $vadPath

$parakeetEncoder = if ($ParakeetPrecision -eq "int8") { "encoder-model.int8.onnx" } else { "encoder-model.onnx" }
$parakeetDecoder = if ($ParakeetPrecision -eq "int8") { "decoder_joint-model.int8.onnx" } else { "decoder_joint-model.onnx" }
$requiredAssets += @(
    (Join-Path $parakeetDir "config.json"),
    (Join-Path $parakeetDir "nemo128.onnx"),
    (Join-Path $parakeetDir $parakeetEncoder),
    (Join-Path $parakeetDir $parakeetDecoder),
    (Join-Path $parakeetDir "vocab.txt")
)
if ($ParakeetPrecision -eq "fp32") {
    $requiredAssets += (Join-Path $parakeetDir "encoder-model.onnx.data")
}

$precisionSuffix = if ($PocketTtsPrecision -eq "int8") { "_int8" } else { "" }
$requiredAssets += @(
    (Join-Path $pocketTtsDir "bundle.json"),
    (Join-Path $pocketTtsDir "bos_before_voice.npy"),
    (Join-Path $pocketTtsDir "tokenizer.model"),
    (Join-Path $pocketTtsDir "mimi_encoder.onnx"),
    (Join-Path $pocketTtsDir "text_conditioner.onnx"),
    (Join-Path $pocketTtsDir "flow_lm_flow$precisionSuffix.onnx"),
    (Join-Path $pocketTtsDir "flow_lm_main$precisionSuffix.onnx"),
    (Join-Path $pocketTtsDir "mimi_decoder$precisionSuffix.onnx")
)
$requiredAssets += $voicePath

foreach ($path in $requiredRuntime) {
    Assert-PathExists $path "Runtime dependency"
}
Assert-PathExists (Join-Path $indexBundleDirectoryFull "index-bundle.json") "External FsColbert bundle manifest"
foreach ($path in $requiredAssets) {
    if (-not (Test-Path -LiteralPath $path) -and $path -eq $vadPath) {
        throw "Silero VAD ONNX asset was not found: $path. Run .\download-silero-vad-onnx-assets.ps1 -AssetsRoot '$assetsRootFull' before starting FsVoice."
    }
    if (
        -not (Test-Path -LiteralPath $path) -and
        $path.StartsWith($parakeetDir, [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Parakeet ONNX $ParakeetPrecision asset was not found: $path. Run .\download-parakeet-onnx-assets.ps1 -AssetsRoot '$assetsRootFull' -Precision $ParakeetPrecision before starting FsVoice."
    }
    if (
        -not (Test-Path -LiteralPath $path) -and
        $path.StartsWith($pocketTtsDir, [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Pocket TTS April ONNX $PocketTtsPrecision asset was not found: $path. Run .\download-pocket-tts-onnx-v2-assets.ps1 -AssetsRoot '$assetsRootFull' -Precision $PocketTtsPrecision before starting FsVoice."
    }
    Assert-PathExists $path "Model asset"
}
Assert-CudaRuntimeDlls $serviceDir
$llamaCppContextSize = Assert-LlamaCppService $LlamaCppEndpoint $MinimumLlamaCppContextSize

$env:OpenSourceVoice__WorkDir = $WorkDir
$env:OpenSourceVoice__Index__BundleDirectory = $indexBundleDirectoryFull
$env:OpenSourceVoice__MaxHistoryTurns = "$MaxHistoryTurns"
$env:OpenSourceVoice__Gemma__LlamaCppEndpoint = $LlamaCppEndpoint
$env:OpenSourceVoice__Gemma__LlamaCppModel = $LlamaCppModel
$env:OpenSourceVoice__Gemma__UseStructuredToolFiller = $useStructuredToolFillerValue.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Gemma__EnableThinking = $enableThinkingValue.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Gemma__LogThoughtText = $logThoughtTextValue.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Vad__ModelPath = $vadPath
$env:OpenSourceVoice__Vad__AllowBargeIn = $allowBargeInValue.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Stt__Runtime = "parakeet-tdt-onnx"
$env:OpenSourceVoice__Stt__ModelDir = $parakeetDir
$env:OpenSourceVoice__Stt__ExecutionProvider = $ParakeetExecutionProvider
$env:OpenSourceVoice__Stt__Precision = $ParakeetPrecision
$env:OpenSourceVoice__Stt__NumThreads = "$ParakeetNumThreads"
$env:OpenSourceVoice__Tts__ModelDir = $pocketTtsDir
$env:OpenSourceVoice__Tts__ExecutionProvider = "cpu"
$env:OpenSourceVoice__Tts__VoiceSamplePath = $voicePath
$env:OpenSourceVoice__Tts__NumThreads = "$TtsNumThreads"
$env:OpenSourceVoice__Tts__NumSteps = "$effectiveTtsNumSteps"
$env:OpenSourceVoice__Tts__Precision = $PocketTtsPrecision
$env:OpenSourceVoice__Tts__Temperature = $PocketTtsTemperature.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:OpenSourceVoice__Tts__Seed = "$PocketTtsSeed"
$env:OpenSourceVoice__Tts__VoiceEmbeddingCacheCapacity = "$TtsVoiceEmbeddingCacheCapacity"
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
Write-Host "IndexBundleDirectory: $indexBundleDirectoryFull"
Write-Host "GemmaRuntime: llama.cpp text-only"
Write-Host "LlamaCppEndpoint: $LlamaCppEndpoint"
Write-Host "LlamaCppModel: $LlamaCppModel"
Write-Host "LlamaCppContextSize: $llamaCppContextSize"
Write-Host "MaxHistoryTurns: $MaxHistoryTurns (rolling window)"
Write-Host "UseStructuredToolFiller: $useStructuredToolFillerValue"
Write-Host "EnableThinking: $enableThinkingValue"
Write-Host "LogThoughtText: $logThoughtTextValue"
Write-Host "VadRuntime: silero-vad-onnx 6.2.1 (cpu)"
Write-Host "VadModelPath: $vadPath"
Write-Host "AllowBargeIn: $allowBargeInValue"
Write-Host "SttRuntime: parakeet-tdt-onnx"
Write-Host "SttModelDir: $parakeetDir"
Write-Host "SttExecutionProvider: $ParakeetExecutionProvider"
Write-Host "ParakeetPrecision: $ParakeetPrecision"
Write-Host "TtsRuntime: pocket-tts-onnx-v2"
Write-Host "TtsModelDir: $pocketTtsDir"
Write-Host "TtsExecutionProvider: cpu"
Write-Host "TtsNumSteps: $effectiveTtsNumSteps"
Write-Host "PocketTtsPrecision: $PocketTtsPrecision"
Write-Host "PocketTtsTemperature: $PocketTtsTemperature"
Write-Host "PocketTtsSeed: $PocketTtsSeed"
Write-Host "VoiceSamplePath: $voicePath"
if (-not [string]::IsNullOrWhiteSpace($WebRtcBindAddress)) { Write-Host "WebRtcBindAddress: $WebRtcBindAddress" }
if ($WebRtcIcePortStart -gt 0 -or $WebRtcIcePortEnd -gt 0) { Write-Host "WebRtcIcePorts: $WebRtcIcePortStart-$WebRtcIcePortEnd/udp" }
& $exe --urls $Urls
