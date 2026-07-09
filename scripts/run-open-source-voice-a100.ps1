param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [string]$Urls = "http://0.0.0.0:5067",
    [int]$TtsMaxSteps = 256,
    [string]$WorkDir = "served_runs"
)

$ErrorActionPreference = "Stop"

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

$exe = Join-Path $PSScriptRoot "FsVoice.OpenSource.Server.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Runtime executable was not found: $exe"
}

Write-Host "Starting FsVoice open-source backend on $Urls"
Write-Host "AssetsRoot: $assetsRootFull"
& $exe --urls $Urls
