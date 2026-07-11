param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [string]$Url = "http://localhost:5067",
    [ValidateSet("pocket-tts-onnx-v2", "pocket-tts-onnx", "chatterbox-onnx")]
    [string]$TtsRuntime = "pocket-tts-onnx-v2",
    [int]$TtsMaxSteps = 96,
    [int]$TtsNumThreads = 2,
    [ValidateRange(0, 64)]
    [int]$TtsNumSteps = 0,
    [string]$PocketTtsModelDir = "",
    [string]$PocketTtsV2ModelDir = "",
    [ValidateSet("int8", "fp32")]
    [string]$PocketTtsPrecision = "int8",
    [ValidateRange(0.0, 5.0)]
    [double]$PocketTtsTemperature = 0.7,
    [ValidateRange(0, 2147483647)]
    [int]$PocketTtsSeed = 12345,
    [string]$VoiceSamplePath = "",
    [bool]$EnableThinking = $true,
    [bool]$LogThoughtText = $false,
    [string]$CudaBin = "",
    [string]$CudnnBin = "",
    [string]$WebRtcBindAddress = "",
    [int]$WebRtcIcePortStart = 0,
    [int]$WebRtcIcePortEnd = 0,
    [switch]$WebRtcIncludeAllInterfaceAddresses,
    [switch]$RequireReady
)

$ErrorActionPreference = "Stop"

$runScript = Join-Path $PSScriptRoot "run-open-source-voice-a100.ps1"
if (-not (Test-Path -LiteralPath $runScript -PathType Leaf)) {
    throw "Run script was not found: $runScript"
}

function Assert-PathExists([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label was not found: $Path"
    }
}

foreach ($runtimePath in @(
    (Join-Path $PSScriptRoot "FsVoice.OpenSource.Server.exe"),
    (Join-Path $PSScriptRoot "onnxruntime.dll"),
    (Join-Path $PSScriptRoot "onnxruntime_providers_cuda.dll"),
    (Join-Path $PSScriptRoot "onnxruntime_providers_shared.dll"),
    (Join-Path $PSScriptRoot "onnxruntime-genai.dll"),
    (Join-Path $PSScriptRoot "onnxruntime-genai-cuda.dll"),
    (Join-Path $PSScriptRoot "FsColbertIndexes\index-bundle.json"),
    (Join-Path $PSScriptRoot "FsColbert\Models\mxbai-edge-colbert\model_int8.onnx")
)) {
    Assert-PathExists $runtimePath "Runtime dependency"
}

$assetsRootFull = (Resolve-Path -LiteralPath $AssetsRoot).Path
$modelsRoot =
    if (Test-Path -LiteralPath (Join-Path $assetsRootFull "gemma-4-e2b-it-onnx-mobius") -PathType Container) {
        $assetsRootFull
    } else {
        Join-Path $assetsRootFull "models"
    }

$gemmaConfig = Join-Path $modelsRoot "gemma-4-e2b-it-onnx-mobius\Q4_K_M\cuda\genai_config.json"
$chatterboxLm = Join-Path $modelsRoot "chatterbox-onnx\onnx\language_model_q4f16.onnx"
$chatterboxVoice = Join-Path $modelsRoot "chatterbox-onnx\default_voice.wav"
$isPocketTtsV2 = $TtsRuntime -eq "pocket-tts-onnx-v2"
$isLegacyPocketTts = $TtsRuntime -eq "pocket-tts-onnx"
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

$requiredAssets = @($gemmaConfig)
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
        (Join-Path $pocketTtsDir "token_scores.json"),
        (Join-Path $pocketTtsDir "test_wavs\bria.wav")
    )
} else {
    $requiredAssets += @($chatterboxLm, $chatterboxVoice)
}

foreach ($required in $requiredAssets) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        if ($isPocketTtsV2 -and $required.StartsWith($pocketTtsV2Dir, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Pocket TTS April ONNX $PocketTtsPrecision asset was not found: $required. Run .\download-pocket-tts-onnx-v2-assets.ps1 -AssetsRoot '$assetsRootFull' -Precision $PocketTtsPrecision before running the smoke test."
        }
        throw "Required asset was not found: $required"
    }
}

$logDir = Join-Path $PSScriptRoot "served_runs\smoke_logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir "open_source_voice.out.log"
$errLog = Join-Path $logDir "open_source_voice.err.log"
$enableThinkingLiteral = if ($EnableThinking) { '$true' } else { '$false' }
$logThoughtTextLiteral = if ($LogThoughtText) { '$true' } else { '$false' }

$startArgs = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $runScript,
    "-AssetsRoot", $AssetsRoot,
    "-Urls", "http://0.0.0.0:5067",
    "-TtsRuntime", $TtsRuntime,
    "-TtsMaxSteps", "$TtsMaxSteps",
    "-TtsNumThreads", "$TtsNumThreads",
    "-TtsNumSteps", "$TtsNumSteps",
    "-PocketTtsPrecision", $PocketTtsPrecision,
    "-PocketTtsTemperature", $PocketTtsTemperature.ToString([Globalization.CultureInfo]::InvariantCulture),
    "-PocketTtsSeed", "$PocketTtsSeed",
    "-EnableThinking", $enableThinkingLiteral,
    "-LogThoughtText", $logThoughtTextLiteral
)
if (-not [string]::IsNullOrWhiteSpace($PocketTtsModelDir)) {
    $startArgs += @("-PocketTtsModelDir", $PocketTtsModelDir)
}
if (-not [string]::IsNullOrWhiteSpace($PocketTtsV2ModelDir)) {
    $startArgs += @("-PocketTtsV2ModelDir", $PocketTtsV2ModelDir)
}
if (-not [string]::IsNullOrWhiteSpace($CudaBin)) {
    $startArgs += @("-CudaBin", $CudaBin)
}
if (-not [string]::IsNullOrWhiteSpace($CudnnBin)) {
    $startArgs += @("-CudnnBin", $CudnnBin)
}
if (-not [string]::IsNullOrWhiteSpace($VoiceSamplePath)) {
    $startArgs += @("-VoiceSamplePath", $VoiceSamplePath)
}
if (-not [string]::IsNullOrWhiteSpace($WebRtcBindAddress)) {
    $startArgs += @("-WebRtcBindAddress", $WebRtcBindAddress)
}
if ($WebRtcIcePortStart -gt 0 -or $WebRtcIcePortEnd -gt 0) {
    $startArgs += @("-WebRtcIcePortStart", "$WebRtcIcePortStart", "-WebRtcIcePortEnd", "$WebRtcIcePortEnd")
}
if ($WebRtcIncludeAllInterfaceAddresses) {
    $startArgs += "-WebRtcIncludeAllInterfaceAddresses"
}

$process = Start-Process powershell.exe `
    -WindowStyle Hidden `
    -PassThru `
    -RedirectStandardOutput $outLog `
    -RedirectStandardError $errLog `
    -ArgumentList $startArgs

try {
    $deadline = (Get-Date).AddSeconds(90)
    $status = $null
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            throw "Service exited early with code $($process.ExitCode). See $outLog and $errLog"
        }

        try {
            $status = Invoke-RestMethod -Uri "$Url/api/status" -TimeoutSec 5
            break
        } catch {
            Start-Sleep -Milliseconds 1000
        }
    }

    if ($null -eq $status) {
        throw "Timed out waiting for $Url/api/status. See $outLog and $errLog"
    }

    Write-Host "Status: $($status.message)"
    Write-Host "Gemma ready: $($status.gemma.ready)"
    Write-Host "TTS ready: $($status.tts.ready)"

    if ($RequireReady -and -not $status.ready) {
        throw "Service status is not ready. Message: $($status.message)"
    }

    $session = Invoke-RestMethod `
        -Uri "$Url/api/open-source/sessions" `
        -Method Post `
        -ContentType "application/json" `
        -Body (@{ systemPrompt = "You are concise."; mode = "gemma-chatterbox" } | ConvertTo-Json)

    Write-Host "Session created: $($session.id)"
    Write-Host "Open the test page at $Url/"
} finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}
