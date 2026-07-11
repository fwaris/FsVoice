param(
    [string]$AssetsRoot = ".\VoiceAgent_assets",
    [string]$GemmaRepoId = "justinchuby/gemma-4-e2b-it-onnx",
    [string]$GemmaVariant = "Q4_K_M/cuda",
    [ValidateSet("int8", "fp32")]
    [string]$PocketTtsPrecision = "int8",
    [string]$HuggingFaceCli = "huggingface-cli"
)

$ErrorActionPreference = "Stop"

function Require-Command([string]$Command) {
    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) {
        throw "$Command was not found. Install the Hugging Face CLI first: pip install huggingface_hub[cli]"
    }
}

function Assert-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }
}

Require-Command $HuggingFaceCli

$assetsRootFull = (New-Item -ItemType Directory -Force -Path $AssetsRoot).FullName
$gemmaDir = Join-Path $assetsRootFull "models\gemma-4-e2b-it-onnx-mobius"
New-Item -ItemType Directory -Force -Path $gemmaDir | Out-Null

Write-Host "Downloading Gemma $GemmaRepoId $GemmaVariant to $gemmaDir..."
& $HuggingFaceCli download $GemmaRepoId `
    --include "$GemmaVariant/**" `
    --local-dir $gemmaDir
if ($LASTEXITCODE -ne 0) { throw "Gemma download failed with exit code $LASTEXITCODE." }

$pocketTtsDownloader = Join-Path $PSScriptRoot "download-pocket-tts-onnx-v2-assets.ps1"
if (-not (Test-Path -LiteralPath $pocketTtsDownloader -PathType Leaf)) {
    throw "Pocket TTS asset downloader was not found: $pocketTtsDownloader"
}

& $pocketTtsDownloader -AssetsRoot $assetsRootFull -Precision $PocketTtsPrecision
if ($LASTEXITCODE -ne 0) { throw "Pocket TTS asset download failed with exit code $LASTEXITCODE." }

$gemmaRuntimeDir = Join-Path $gemmaDir $GemmaVariant.Replace("/", [IO.Path]::DirectorySeparatorChar)
$pocketTtsDir = Join-Path $assetsRootFull "models\pocket-tts-onnx-english-2026-04"
$precisionSuffix = if ($PocketTtsPrecision -eq "int8") { "_int8" } else { "" }
$required = @(
    @{ label = "Gemma genai_config"; path = Join-Path $gemmaRuntimeDir "genai_config.json" },
    @{ label = "Gemma tokenizer"; path = Join-Path $gemmaRuntimeDir "tokenizer.json" },
    @{ label = "Gemma embedding graph"; path = Join-Path $gemmaRuntimeDir "embedding\model.onnx" },
    @{ label = "Gemma audio encoder graph"; path = Join-Path $gemmaRuntimeDir "audio_encoder\model.onnx" },
    @{ label = "Gemma decoder graph"; path = Join-Path $gemmaRuntimeDir "decoder\model.onnx" },
    @{ label = "Pocket TTS bundle"; path = Join-Path $pocketTtsDir "bundle.json" },
    @{ label = "Pocket TTS flow model"; path = Join-Path $pocketTtsDir "flow_lm_flow$precisionSuffix.onnx" },
    @{ label = "Pocket TTS main model"; path = Join-Path $pocketTtsDir "flow_lm_main$precisionSuffix.onnx" },
    @{ label = "Pocket TTS decoder"; path = Join-Path $pocketTtsDir "mimi_decoder$precisionSuffix.onnx" }
)

foreach ($item in $required) {
    Assert-File $item.path $item.label
}

$manifest = [pscustomobject]@{
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    gemmaRepoId = $GemmaRepoId
    gemmaVariant = $GemmaVariant
    pocketTtsRuntime = "pocket-tts-onnx-v2"
    pocketTtsPrecision = $PocketTtsPrecision
    assetsRoot = $assetsRootFull
}

$manifestPath = Join-Path $assetsRootFull "open_source_voice_assets_manifest.json"
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Assets ready. Add a WAV reference voice at $assetsRootFull\voices\default_voice.wav or pass -VoiceSamplePath to the launcher."
Write-Host "AssetsRoot: $assetsRootFull"
Write-Host "Manifest: $manifestPath"
