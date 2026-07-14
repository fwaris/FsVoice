param(
    [string]$AssetsRoot = ".\VoiceAgent_assets",
    [string]$Curl = "curl.exe"
)

$ErrorActionPreference = "Stop"

$version = "6.2.1"
$modelSha256 = "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3"
$licenseSha256 = "2e63e9a38b6e8fc0c7bc37ce174caca1862870856c6daf5697cfb785e925520b"
$baseUrl = "https://raw.githubusercontent.com/snakers4/silero-vad/v$version"

if (-not (Get-Command $Curl -ErrorAction SilentlyContinue)) {
    throw "$Curl was not found. Install curl or pass its executable path with -Curl."
}

function Assert-FileHash([string]$Path, [string]$ExpectedSha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Silero VAD asset was not downloaded: $Path"
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256) {
        throw "Silero VAD asset checksum mismatch for $Path. Expected $ExpectedSha256; found $actual."
    }
}

function Download-VerifiedFile(
    [string]$Url,
    [string]$Destination,
    [string]$ExpectedSha256
) {
    $isValid =
        (Test-Path -LiteralPath $Destination -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedSha256)

    if ($isValid) {
        Write-Host "Already verified: $(Split-Path -Leaf $Destination)"
        return
    }

    Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
    Write-Host "Downloading: $(Split-Path -Leaf $Destination)"
    & $Curl --location --fail --retry 5 --retry-delay 2 --output $Destination $Url
    if ($LASTEXITCODE -ne 0) {
        throw "Silero VAD asset download failed with exit code $LASTEXITCODE."
    }

    Assert-FileHash $Destination $ExpectedSha256
}

$assetsRootFull = (New-Item -ItemType Directory -Force -Path $AssetsRoot).FullName
$modelDir = Join-Path $assetsRootFull "models\silero-vad-onnx"
New-Item -ItemType Directory -Force -Path $modelDir | Out-Null

$modelPath = Join-Path $modelDir "silero_vad.onnx"
$licensePath = Join-Path $modelDir "LICENSE.silero-vad"

Download-VerifiedFile `
    "$baseUrl/src/silero_vad/data/silero_vad.onnx" `
    $modelPath `
    $modelSha256

Download-VerifiedFile "$baseUrl/LICENSE" $licensePath $licenseSha256

$manifest = [pscustomobject]@{
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    runtime = "silero-vad-onnx"
    version = $version
    repository = "snakers4/silero-vad"
    modelPath = $modelPath
    files = @(
        [pscustomobject]@{ path = "silero_vad.onnx"; sha256 = $modelSha256 }
        [pscustomobject]@{ path = "LICENSE.silero-vad"; sha256 = $licenseSha256 }
    )
}

$manifestPath = Join-Path $modelDir "fsvoice-silero-vad-assets.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Silero VAD ONNX $version assets are ready."
Write-Host "ModelPath: $modelPath"
Write-Host "Manifest: $manifestPath"
