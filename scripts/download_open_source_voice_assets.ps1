param(
    [string]$AssetsRoot = ".\VoiceAgent_assets",
    [string]$GemmaRepoId = "justinchuby/gemma-4-e2b-it-onnx",
    [string]$GemmaVariant = "Q4_K_M/cuda",
    [string]$ChatterboxRepoId = "onnx-community/chatterbox-ONNX",
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
$chatterboxDir = Join-Path $assetsRootFull "models\chatterbox-onnx"
$voiceDir = Join-Path $assetsRootFull "voices"

New-Item -ItemType Directory -Force -Path $gemmaDir, $chatterboxDir, $voiceDir | Out-Null

Write-Host "Downloading Gemma $GemmaRepoId $GemmaVariant to $gemmaDir..."
& $HuggingFaceCli download $GemmaRepoId `
    --include "$GemmaVariant/**" `
    --local-dir $gemmaDir
if ($LASTEXITCODE -ne 0) { throw "Gemma download failed with exit code $LASTEXITCODE." }

Write-Host "Downloading Chatterbox $ChatterboxRepoId to $chatterboxDir..."
& $HuggingFaceCli download $ChatterboxRepoId `
    --include "tokenizer.json" `
    --include "default_voice.wav" `
    --include "onnx/speech_encoder.onnx*" `
    --include "onnx/embed_tokens.onnx*" `
    --include "onnx/conditional_decoder.onnx*" `
    --include "onnx/language_model_q4f16.onnx*" `
    --local-dir $chatterboxDir
if ($LASTEXITCODE -ne 0) { throw "Chatterbox download failed with exit code $LASTEXITCODE." }

$defaultVoice = Join-Path $chatterboxDir "default_voice.wav"
$runtimeVoice = Join-Path $voiceDir "default_voice.wav"
Copy-Item -LiteralPath $defaultVoice -Destination $runtimeVoice -Force

$gemmaRuntimeDir = Join-Path $gemmaDir $GemmaVariant.Replace("/", [IO.Path]::DirectorySeparatorChar)
$required = @(
    @{ label = "Gemma genai_config"; path = Join-Path $gemmaRuntimeDir "genai_config.json" },
    @{ label = "Gemma tokenizer"; path = Join-Path $gemmaRuntimeDir "tokenizer.json" },
    @{ label = "Gemma embedding graph"; path = Join-Path $gemmaRuntimeDir "embedding\model.onnx" },
    @{ label = "Gemma audio encoder graph"; path = Join-Path $gemmaRuntimeDir "audio_encoder\model.onnx" },
    @{ label = "Gemma decoder graph"; path = Join-Path $gemmaRuntimeDir "decoder\model.onnx" },
    @{ label = "Chatterbox tokenizer"; path = Join-Path $chatterboxDir "tokenizer.json" },
    @{ label = "Chatterbox default voice"; path = Join-Path $chatterboxDir "default_voice.wav" },
    @{ label = "Chatterbox speech encoder"; path = Join-Path $chatterboxDir "onnx\speech_encoder.onnx" },
    @{ label = "Chatterbox embed tokens"; path = Join-Path $chatterboxDir "onnx\embed_tokens.onnx" },
    @{ label = "Chatterbox decoder"; path = Join-Path $chatterboxDir "onnx\conditional_decoder.onnx" },
    @{ label = "Chatterbox q4f16 LM"; path = Join-Path $chatterboxDir "onnx\language_model_q4f16.onnx" },
    @{ label = "Runtime default voice"; path = $runtimeVoice }
)

foreach ($item in $required) {
    Assert-File $item.path $item.label
}

$files =
    Get-ChildItem -LiteralPath $assetsRootFull -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($assetsRootFull.Length + 1)
            bytes = $_.Length
        }
    }

$manifest = [pscustomobject]@{
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    gemmaRepoId = $GemmaRepoId
    gemmaVariant = $GemmaVariant
    chatterboxRepoId = $ChatterboxRepoId
    assetsRoot = $assetsRootFull
    files = $files
}

$manifestPath = Join-Path $assetsRootFull "open_source_voice_assets_manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Assets ready."
Write-Host "AssetsRoot: $assetsRootFull"
Write-Host "Manifest: $manifestPath"

