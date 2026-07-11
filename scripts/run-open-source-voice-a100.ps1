param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [string]$Urls = "http://0.0.0.0:5067",
    [ValidateSet("pocket-tts-onnx-v2", "pocket-tts-onnx", "chatterbox-onnx")]
    [string]$TtsRuntime = "pocket-tts-onnx-v2",
    [int]$TtsMaxSteps = 256,
    [int]$TtsNumThreads = 2,
    [ValidateRange(0, 64)]
    [int]$TtsNumSteps = 0,
    [int]$TtsVoiceEmbeddingCacheCapacity = 4,
    [string]$PocketTtsModelDir = "",
    [string]$PocketTtsV2ModelDir = "",
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
    [bool]$UseStructuredToolFiller = $true,
    [bool]$EnableThinking = $true,
    [bool]$LogThoughtText = $false,
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
$isPocketTtsV2 = $TtsRuntime -eq "pocket-tts-onnx-v2"
$isLegacyPocketTts = $TtsRuntime -eq "pocket-tts-onnx"
$isPocketTts = $isPocketTtsV2 -or $isLegacyPocketTts
$pocketTtsDir =
    if ([string]::IsNullOrWhiteSpace($PocketTtsModelDir)) {
        Join-Path $modelsRoot "sherpa-onnx-pocket-tts-int8-2026-01-26"
    } elseif ([IO.Path]::IsPathRooted($PocketTtsModelDir)) {
        [IO.Path]::GetFullPath($PocketTtsModelDir)
    } else {
        [IO.Path]::GetFullPath((Join-Path $assetsRootFull $PocketTtsModelDir))
    }
$pocketTtsV2Dir =
    if ([string]::IsNullOrWhiteSpace($PocketTtsV2ModelDir)) {
        Join-Path $modelsRoot "pocket-tts-onnx-english-2026-04"
    } elseif ([IO.Path]::IsPathRooted($PocketTtsV2ModelDir)) {
        [IO.Path]::GetFullPath($PocketTtsV2ModelDir)
    } else {
        [IO.Path]::GetFullPath((Join-Path $assetsRootFull $PocketTtsV2ModelDir))
    }
$ttsModelDir =
    if ($isPocketTtsV2) {
        $pocketTtsV2Dir
    } elseif ($isLegacyPocketTts) {
        $pocketTtsDir
    } else {
        $chatterboxDir
    }
$ttsExecutionProvider = if ($isPocketTts) { "cpu" } else { "cuda" }
$effectiveTtsNumSteps =
    if ($TtsNumSteps -gt 0) {
        $TtsNumSteps
    } elseif ($isPocketTtsV2) {
        4
    } else {
        1
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
        } elseif ($isLegacyPocketTts) {
            Join-Path $pocketTtsDir "test_wavs\bria.wav"
        } elseif ($isPocketTtsV2) {
            Join-Path $assetsRootFull "voices\default_voice.wav"
        } else {
            Join-Path $chatterboxDir "default_voice.wav"
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

if ($isPocketTtsV2) {
    $precisionSuffix = if ($PocketTtsPrecision -eq "int8") { "_int8" } else { "" }
    $requiredAssets += @(
        (Join-Path $pocketTtsV2Dir "bundle.json"),
        (Join-Path $pocketTtsV2Dir "bos_before_voice.npy"),
        (Join-Path $pocketTtsV2Dir "tokenizer.model"),
        (Join-Path $pocketTtsV2Dir "mimi_encoder.onnx"),
        (Join-Path $pocketTtsV2Dir "text_conditioner.onnx"),
        (Join-Path $pocketTtsV2Dir "flow_lm_flow$precisionSuffix.onnx"),
        (Join-Path $pocketTtsV2Dir "flow_lm_main$precisionSuffix.onnx"),
        (Join-Path $pocketTtsV2Dir "mimi_decoder$precisionSuffix.onnx")
    )
} elseif ($isLegacyPocketTts) {
    $requiredAssets += @(
        (Join-Path $pocketTtsDir "lm_flow.int8.onnx"),
        (Join-Path $pocketTtsDir "lm_main.int8.onnx"),
        (Join-Path $pocketTtsDir "encoder.onnx"),
        (Join-Path $pocketTtsDir "decoder.int8.onnx"),
        (Join-Path $pocketTtsDir "text_conditioner.onnx"),
        (Join-Path $pocketTtsDir "vocab.json"),
        (Join-Path $pocketTtsDir "token_scores.json")
    )
} else {
    $requiredAssets += @(
        (Join-Path $chatterboxDir "tokenizer.json"),
        (Join-Path $chatterboxDir "default_voice.wav"),
        (Join-Path $chatterboxDir "onnx\speech_encoder.onnx"),
        (Join-Path $chatterboxDir "onnx\speech_encoder.onnx_data"),
        (Join-Path $chatterboxDir "onnx\embed_tokens.onnx"),
        (Join-Path $chatterboxDir "onnx\embed_tokens.onnx_data"),
        (Join-Path $chatterboxDir "onnx\language_model_q4f16.onnx"),
        (Join-Path $chatterboxDir "onnx\language_model_q4f16.onnx_data"),
        (Join-Path $chatterboxDir "onnx\conditional_decoder.onnx"),
        (Join-Path $chatterboxDir "onnx\conditional_decoder.onnx_data")
    )
}
$requiredAssets += $voicePath

foreach ($path in $requiredRuntime) {
    Assert-PathExists $path "Runtime dependency"
}
foreach ($path in $requiredAssets) {
    if (
        $isPocketTtsV2 -and
        -not (Test-Path -LiteralPath $path) -and
        $path.StartsWith($pocketTtsV2Dir, [StringComparison]::OrdinalIgnoreCase)
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
$env:OpenSourceVoice__Gemma__UseStructuredToolFiller = $UseStructuredToolFiller.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Gemma__EnableThinking = $EnableThinking.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Gemma__LogThoughtText = $LogThoughtText.ToString().ToLowerInvariant()
$env:OpenSourceVoice__Tts__ModelDir = $ttsModelDir
$env:OpenSourceVoice__Tts__Runtime = $TtsRuntime
$env:OpenSourceVoice__Tts__ExecutionProvider = $ttsExecutionProvider
$env:OpenSourceVoice__Tts__Variant = "q4f16"
$env:OpenSourceVoice__Tts__VoiceSamplePath = $voicePath
$env:OpenSourceVoice__Tts__MaxSteps = "$TtsMaxSteps"
$env:OpenSourceVoice__Tts__NumThreads = "$TtsNumThreads"
$env:OpenSourceVoice__Tts__NumSteps = "$effectiveTtsNumSteps"
$env:OpenSourceVoice__Tts__Precision = $PocketTtsPrecision
$env:OpenSourceVoice__Tts__Temperature = $PocketTtsTemperature.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:OpenSourceVoice__Tts__Seed = "$PocketTtsSeed"
$env:OpenSourceVoice__Tts__VoiceEmbeddingCacheCapacity = "$TtsVoiceEmbeddingCacheCapacity"
$env:OpenSourceVoice__Tts__RequireGpu = if ($isPocketTts) { "false" } else { "true" }
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
Write-Host "UseStructuredToolFiller: $UseStructuredToolFiller"
Write-Host "EnableThinking: $EnableThinking"
Write-Host "LogThoughtText: $LogThoughtText"
Write-Host "TtsRuntime: $TtsRuntime"
Write-Host "TtsModelDir: $ttsModelDir"
Write-Host "TtsExecutionProvider: $ttsExecutionProvider"
Write-Host "TtsNumSteps: $effectiveTtsNumSteps"
if ($isPocketTtsV2) {
    Write-Host "PocketTtsPrecision: $PocketTtsPrecision"
    Write-Host "PocketTtsTemperature: $PocketTtsTemperature"
    Write-Host "PocketTtsSeed: $PocketTtsSeed"
}
Write-Host "VoiceSamplePath: $voicePath"
if (-not [string]::IsNullOrWhiteSpace($WebRtcBindAddress)) { Write-Host "WebRtcBindAddress: $WebRtcBindAddress" }
if ($WebRtcIcePortStart -gt 0 -or $WebRtcIcePortEnd -gt 0) { Write-Host "WebRtcIcePorts: $WebRtcIcePortStart-$WebRtcIcePortEnd/udp" }
Write-Host "Default paper index: $(Join-Path $serviceDir "FsColbertIndexes\index-bundle.json")"
& $exe --urls $Urls
