param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [string]$ArchivePath = "",
    [string]$ModelDirectoryName = "sherpa-onnx-pocket-tts-int8-2026-01-26",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$modelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/sherpa-onnx-pocket-tts-int8-2026-01-26.tar.bz2"
$requiredFiles = @(
    "lm_flow.int8.onnx",
    "lm_main.int8.onnx",
    "encoder.onnx",
    "decoder.int8.onnx",
    "text_conditioner.onnx",
    "vocab.json",
    "token_scores.json",
    "test_wavs\bria.wav"
)

$assetsRootFull = [IO.Path]::GetFullPath($AssetsRoot)
New-Item -ItemType Directory -Force -Path $assetsRootFull | Out-Null
$modelsRoot =
    if (Test-Path -LiteralPath (Join-Path $assetsRootFull "gemma-4-e2b-it-onnx-mobius") -PathType Container) {
        $assetsRootFull
    } else {
        $modelsPath = Join-Path $assetsRootFull "models"
        New-Item -ItemType Directory -Force -Path $modelsPath | Out-Null
        [IO.Path]::GetFullPath($modelsPath)
    }

$destination = [IO.Path]::GetFullPath((Join-Path $modelsRoot $ModelDirectoryName))
$modelsPrefix = [IO.Path]::GetFullPath($modelsRoot).TrimEnd('\') + '\'
if (-not $destination.StartsWith($modelsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Model destination must remain under the assets model directory: $destination"
}

function Test-ModelComplete([string]$Path) {
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $relativePath) -PathType Leaf)) {
            return $false
        }
    }
    return $true
}

if ((Test-ModelComplete $destination) -and -not $Force) {
    Write-Host "Pocket TTS ONNX assets are already ready: $destination"
    exit 0
}

if ((Test-Path -LiteralPath $destination) -and -not $Force) {
    throw "Pocket TTS model directory is incomplete: $destination. Rerun with -Force to replace it."
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $temporaryBase ("fsvoice-pocket-tts-" + [Guid]::NewGuid().ToString("N"))))
if (-not $temporaryRoot.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary extraction path escaped the system temp directory: $temporaryRoot"
}

New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null

try {
    $archive =
        if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
            $download = Join-Path $temporaryRoot "pocket-tts.tar.bz2"
            Write-Host "Downloading Pocket TTS ONNX INT8 assets..."
            Invoke-WebRequest -Uri $modelUrl -OutFile $download
            $download
        } else {
            (Resolve-Path -LiteralPath $ArchivePath).Path
        }

    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($null -eq $tar) {
        throw "tar.exe is required to extract the Pocket TTS .tar.bz2 archive."
    }

    & $tar.Source -xjf $archive -C $temporaryRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Pocket TTS archive extraction failed with exit code $LASTEXITCODE."
    }

    $extracted = Join-Path $temporaryRoot $ModelDirectoryName
    if (-not (Test-ModelComplete $extracted)) {
        throw "The extracted Pocket TTS archive is missing one or more required files: $extracted"
    }

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }

    Move-Item -LiteralPath $extracted -Destination $destination
    Write-Host "Pocket TTS ONNX assets ready: $destination"
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
