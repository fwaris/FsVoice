param(
    [string]$AssetsRoot = ".\VoiceAgent_assets",
    [ValidateSet("fp32", "int8")]
    [string]$ParakeetPrecision = "fp32",
    [ValidateSet("int8", "fp32")]
    [string]$PocketTtsPrecision = "int8"
)

$ErrorActionPreference = "Stop"

function Assert-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }
}

$assetsRootFull = (New-Item -ItemType Directory -Force -Path $AssetsRoot).FullName

$vadDownloader = Join-Path $PSScriptRoot "download-silero-vad-onnx-assets.ps1"
if (-not (Test-Path -LiteralPath $vadDownloader -PathType Leaf)) {
    throw "Silero VAD asset downloader was not found: $vadDownloader"
}

& $vadDownloader -AssetsRoot $assetsRootFull
if ($LASTEXITCODE -ne 0) { throw "Silero VAD asset download failed with exit code $LASTEXITCODE." }

$parakeetDownloader = Join-Path $PSScriptRoot "download-parakeet-onnx-assets.ps1"
if (-not (Test-Path -LiteralPath $parakeetDownloader -PathType Leaf)) {
    throw "Parakeet asset downloader was not found: $parakeetDownloader"
}

& $parakeetDownloader -AssetsRoot $assetsRootFull -Precision $ParakeetPrecision
if ($LASTEXITCODE -ne 0) { throw "Parakeet asset download failed with exit code $LASTEXITCODE." }

$pocketTtsDownloader = Join-Path $PSScriptRoot "download-pocket-tts-onnx-v2-assets.ps1"
if (-not (Test-Path -LiteralPath $pocketTtsDownloader -PathType Leaf)) {
    throw "Pocket TTS asset downloader was not found: $pocketTtsDownloader"
}

& $pocketTtsDownloader -AssetsRoot $assetsRootFull -Precision $PocketTtsPrecision
if ($LASTEXITCODE -ne 0) { throw "Pocket TTS asset download failed with exit code $LASTEXITCODE." }

$parakeetDir = Join-Path $assetsRootFull "models\parakeet-tdt-0.6b-v3-onnx"
$pocketTtsDir = Join-Path $assetsRootFull "models\pocket-tts-onnx-english-2026-04"
$vadModel = Join-Path $assetsRootFull "models\silero-vad-onnx\silero_vad.onnx"
$parakeetEncoder = if ($ParakeetPrecision -eq "int8") { "encoder-model.int8.onnx" } else { "encoder-model.onnx" }
$parakeetDecoder = if ($ParakeetPrecision -eq "int8") { "decoder_joint-model.int8.onnx" } else { "decoder_joint-model.onnx" }
$precisionSuffix = if ($PocketTtsPrecision -eq "int8") { "_int8" } else { "" }
$required = @(
    @{ label = "Silero VAD model"; path = $vadModel },
    @{ label = "Parakeet preprocessor"; path = Join-Path $parakeetDir "nemo128.onnx" },
    @{ label = "Parakeet encoder"; path = Join-Path $parakeetDir $parakeetEncoder },
    @{ label = "Parakeet decoder"; path = Join-Path $parakeetDir $parakeetDecoder },
    @{ label = "Parakeet vocabulary"; path = Join-Path $parakeetDir "vocab.txt" },
    @{ label = "Pocket TTS bundle"; path = Join-Path $pocketTtsDir "bundle.json" },
    @{ label = "Pocket TTS flow model"; path = Join-Path $pocketTtsDir "flow_lm_flow$precisionSuffix.onnx" },
    @{ label = "Pocket TTS main model"; path = Join-Path $pocketTtsDir "flow_lm_main$precisionSuffix.onnx" },
    @{ label = "Pocket TTS decoder"; path = Join-Path $pocketTtsDir "mimi_decoder$precisionSuffix.onnx" }
)

if ($ParakeetPrecision -eq "fp32") {
    $required += @{ label = "Parakeet encoder data"; path = Join-Path $parakeetDir "encoder-model.onnx.data" }
}

foreach ($item in $required) {
    Assert-File $item.path $item.label
}

$manifest = [pscustomobject]@{
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    gemmaRuntime = "llama.cpp"
    gemmaModel = "models/gemma-4-E2B_q4_0-it.gguf"
    parakeetRepoId = "istupakov/parakeet-tdt-0.6b-v3-onnx"
    parakeetRevision = "8f23f0c03c8761650bdb5b40aaf3e40d2c15f1ce"
    parakeetPrecision = $ParakeetPrecision
    pocketTtsRuntime = "pocket-tts-onnx-v2"
    pocketTtsPrecision = $PocketTtsPrecision
    vadRuntime = "silero-vad-onnx"
    vadVersion = "6.2.1"
    assetsRoot = $assetsRootFull
}

$manifestPath = Join-Path $assetsRootFull "open_source_voice_assets_manifest.json"
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Silero VAD, Parakeet, and Pocket TTS assets are ready. Stage the Gemma GGUF separately at $assetsRootFull\models\gemma-4-E2B_q4_0-it.gguf."
Write-Host "Add a WAV reference voice at $assetsRootFull\voices\default_voice.wav or pass -VoiceSamplePath to the launcher."
Write-Host "AssetsRoot: $assetsRootFull"
Write-Host "Manifest: $manifestPath"
