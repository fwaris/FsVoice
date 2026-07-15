param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [Parameter(Mandatory = $true)]
    [string]$IndexBundleDirectory,
    [string]$Url = "http://localhost:5067",
    [string]$LlamaCppEndpoint = "http://127.0.0.1:8081",
    [string]$LlamaCppModel = "gemma-4-E2B_q4_0-it.gguf",
    [ValidateRange(8192, 131072)]
    [int]$MinimumLlamaCppContextSize = 16384,
    [string]$ParakeetModelDir = "",
    [string]$VadModelPath = "",
    [bool]$AllowBargeIn = $true,
    [ValidateSet("fp32", "int8")]
    [string]$ParakeetPrecision = "fp32",
    [ValidateSet("cpu", "cuda")]
    [string]$ParakeetExecutionProvider = "cuda",
    [int]$TtsNumThreads = 2,
    [ValidateRange(0, 64)]
    [int]$TtsNumSteps = 0,
    [string]$PocketTtsModelDir = "",
    [ValidateSet("int8", "fp32")]
    [string]$PocketTtsPrecision = "int8",
    [ValidateRange(0.0, 5.0)]
    [double]$PocketTtsTemperature = 0.7,
    [ValidateRange(0, 2147483647)]
    [int]$PocketTtsSeed = 12345,
    [string]$VoiceSamplePath = "",
    [ValidateRange(0, 100)]
    [int]$MaxHistoryTurns = 10,
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

function ConvertTo-ProcessArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

foreach ($runtimePath in @(
    (Join-Path $PSScriptRoot "FsVoice.OpenSource.Server.exe"),
    (Join-Path $PSScriptRoot "onnxruntime.dll"),
    (Join-Path $PSScriptRoot "onnxruntime_providers_cuda.dll"),
    (Join-Path $PSScriptRoot "onnxruntime_providers_shared.dll"),
    (Join-Path $PSScriptRoot "FsColbert\Models\mxbai-edge-colbert\model_int8.onnx")
)) {
    Assert-PathExists $runtimePath "Runtime dependency"
}

$assetsRootFull = (Resolve-Path -LiteralPath $AssetsRoot).Path
$indexBundleDirectoryFull = (Resolve-Path -LiteralPath $IndexBundleDirectory).Path
Assert-PathExists (Join-Path $indexBundleDirectoryFull "index-bundle.json") "External FsColbert bundle manifest"
$modelsRoot =
    if (
        (Test-Path -LiteralPath (Join-Path $assetsRootFull "parakeet-tdt-0.6b-v3-onnx") -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $assetsRootFull "pocket-tts-onnx-english-2026-04") -PathType Container)
    ) {
        $assetsRootFull
    } else {
        Join-Path $assetsRootFull "models"
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

$parakeetEncoder = if ($ParakeetPrecision -eq "int8") { "encoder-model.int8.onnx" } else { "encoder-model.onnx" }
$parakeetDecoder = if ($ParakeetPrecision -eq "int8") { "decoder_joint-model.int8.onnx" } else { "decoder_joint-model.onnx" }
$requiredAssets = @(
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

foreach ($required in $requiredAssets) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        if ($required.StartsWith($parakeetDir, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Parakeet ONNX $ParakeetPrecision asset was not found: $required. Run .\download-parakeet-onnx-assets.ps1 -AssetsRoot '$assetsRootFull' -Precision $ParakeetPrecision before running the smoke test."
        }
        if ($required.StartsWith($pocketTtsDir, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Pocket TTS April ONNX $PocketTtsPrecision asset was not found: $required. Run .\download-pocket-tts-onnx-v2-assets.ps1 -AssetsRoot '$assetsRootFull' -Precision $PocketTtsPrecision before running the smoke test."
        }
        throw "Required asset was not found: $required"
    }
}

$logDir = Join-Path $PSScriptRoot "served_runs\smoke_logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir "open_source_voice.out.log"
$errLog = Join-Path $logDir "open_source_voice.err.log"
$enableThinkingLiteral = if ($EnableThinking) { 'true' } else { 'false' }
$logThoughtTextLiteral = if ($LogThoughtText) { 'true' } else { 'false' }
$allowBargeInLiteral = if ($AllowBargeIn) { 'true' } else { 'false' }

$startArgs = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $runScript,
    "-AssetsRoot", $AssetsRoot,
    "-IndexBundleDirectory", $indexBundleDirectoryFull,
    "-Urls", "http://0.0.0.0:5067",
    "-LlamaCppEndpoint", $LlamaCppEndpoint,
    "-LlamaCppModel", $LlamaCppModel,
    "-MinimumLlamaCppContextSize", "$MinimumLlamaCppContextSize",
    "-MaxHistoryTurns", "$MaxHistoryTurns",
    "-ParakeetPrecision", $ParakeetPrecision,
    "-ParakeetExecutionProvider", $ParakeetExecutionProvider,
    "-AllowBargeIn", $allowBargeInLiteral,
    "-TtsNumThreads", "$TtsNumThreads",
    "-TtsNumSteps", "$TtsNumSteps",
    "-PocketTtsPrecision", $PocketTtsPrecision,
    "-PocketTtsTemperature", $PocketTtsTemperature.ToString([Globalization.CultureInfo]::InvariantCulture),
    "-PocketTtsSeed", "$PocketTtsSeed",
    "-EnableThinking", $enableThinkingLiteral,
    "-LogThoughtText", $logThoughtTextLiteral
)
if (-not [string]::IsNullOrWhiteSpace($ParakeetModelDir)) {
    $startArgs += @("-ParakeetModelDir", $ParakeetModelDir)
}
if (-not [string]::IsNullOrWhiteSpace($VadModelPath)) {
    $startArgs += @("-VadModelPath", $VadModelPath)
}
if (-not [string]::IsNullOrWhiteSpace($PocketTtsModelDir)) {
    $startArgs += @("-PocketTtsModelDir", $PocketTtsModelDir)
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

$processArgumentLine =
    ($startArgs | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '

$process = Start-Process powershell.exe `
    -WindowStyle Hidden `
    -PassThru `
    -RedirectStandardOutput $outLog `
    -RedirectStandardError $errLog `
    -ArgumentList $processArgumentLine

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
    Write-Host "STT ready: $($status.stt.ready)"
    Write-Host "VAD ready: $($status.vad.ready)"
    Write-Host "Barge-in enabled: $($status.vad.allowBargeIn)"
    Write-Host "TTS ready: $($status.tts.ready)"
    Write-Host "Index ready: $($status.index.ready)"
    Write-Host "Index bundle: $($status.index.bundleId) $($status.index.bundleVersion)"

    if ($RequireReady -and -not $status.ready) {
        throw "Service status is not ready. Message: $($status.message)"
    }

    $session = Invoke-RestMethod `
        -Uri "$Url/api/open-source/sessions" `
        -Method Post `
        -ContentType "application/json" `
        -Body (@{ systemPrompt = "You are concise."; mode = "gemma-pocket-tts" } | ConvertTo-Json)

    Write-Host "Session created: $($session.id)"
    Write-Host "Open the test page at $Url/"
} finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}
