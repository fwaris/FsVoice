param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [string]$Urls = "http://0.0.0.0:5067",
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
    [string]$VoiceSamplePath = "",
    [ValidateSet("cpu", "cuda")]
    [string]$GemmaAudioEncoderExecutionProvider = "cpu",
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
$useStructuredToolFillerValue = ConvertTo-Boolean $UseStructuredToolFiller
$enableThinkingValue = ConvertTo-Boolean $EnableThinking
$logThoughtTextValue = ConvertTo-Boolean $LogThoughtText
$modelsRoot =
    if (Test-Path -LiteralPath (Join-Path $assetsRootFull "gemma-4-e2b-it-onnx-mobius") -PathType Container) {
        $assetsRootFull
    } else {
        Join-Path $assetsRootFull "models"
    }

$gemmaDir = Join-Path $modelsRoot "gemma-4-e2b-it-onnx-mobius\Q4_K_M\cuda"
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
    (Join-Path $gemmaDir "decoder\model.onnx")
)

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
foreach ($path in $requiredAssets) {
    if (
        -not (Test-Path -LiteralPath $path) -and
        $path.StartsWith($pocketTtsDir, [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Pocket TTS April ONNX $PocketTtsPrecision asset was not found: $path. Run .\download-pocket-tts-onnx-v2-assets.ps1 -AssetsRoot '$assetsRootFull' -Precision $PocketTtsPrecision before starting FsVoice."
    }
    Assert-PathExists $path "Model asset"
}
Assert-CudaRuntimeDlls $serviceDir

$env:OpenSourceVoice__WorkDir = $WorkDir
$env:OpenSourceVoice__Gemma__ModelDir = $gemmaDir
$env:OpenSourceVoice__Gemma__Runtime = "raw-onnx"
$env:OpenSourceVoice__Gemma__ExecutionProvider = "cuda"
$env:OpenSourceVoice__Gemma__AudioEncoderExecutionProvider = $GemmaAudioEncoderExecutionProvider
$env:OpenSourceVoice__Gemma__UseStructuredToolFiller = $useStructuredToolFillerValue.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Gemma__EnableThinking = $enableThinkingValue.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Gemma__LogThoughtText = $logThoughtTextValue.ToString().ToLowerInvariant()
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
Write-Host "GemmaModelDir: $gemmaDir"
Write-Host "GemmaAudioEncoderExecutionProvider: $GemmaAudioEncoderExecutionProvider"
Write-Host "UseStructuredToolFiller: $useStructuredToolFillerValue"
Write-Host "EnableThinking: $enableThinkingValue"
Write-Host "LogThoughtText: $logThoughtTextValue"
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
Write-Host "Default paper index: $(Join-Path $serviceDir "FsColbertIndexes\index-bundle.json")"
& $exe --urls $Urls
