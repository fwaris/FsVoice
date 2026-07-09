param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [string]$Url = "http://localhost:5067",
    [int]$TtsMaxSteps = 96,
    [string]$CudaBin = "",
    [string]$CudnnBin = "",
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

foreach ($required in @($gemmaConfig, $chatterboxLm, $chatterboxVoice)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required asset was not found: $required"
    }
}

$logDir = Join-Path $PSScriptRoot "served_runs\smoke_logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir "open_source_voice.out.log"
$errLog = Join-Path $logDir "open_source_voice.err.log"

$startArgs = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $runScript,
    "-AssetsRoot", $AssetsRoot,
    "-Urls", "http://0.0.0.0:5067",
    "-TtsMaxSteps", "$TtsMaxSteps"
)
if (-not [string]::IsNullOrWhiteSpace($CudaBin)) {
    $startArgs += @("-CudaBin", $CudaBin)
}
if (-not [string]::IsNullOrWhiteSpace($CudnnBin)) {
    $startArgs += @("-CudnnBin", $CudnnBin)
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
