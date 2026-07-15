param(
    [string]$AssetsRoot = ".\VoiceAgent_assets",
    [ValidateSet("fp32", "int8")]
    [string]$Precision = "fp32",
    [string]$Curl = "curl.exe"
)

$ErrorActionPreference = "Stop"

$repoId = "istupakov/parakeet-tdt-0.6b-v3-onnx"
$revision = "8f23f0c03c8761650bdb5b40aaf3e40d2c15f1ce"

function Assert-Command([string]$Command) {
    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) {
        throw "$Command was not found. Install curl or pass its executable path with -Curl."
    }
}

function Assert-FileHash([string]$Path, [string]$ExpectedSha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Parakeet asset was not downloaded: $Path"
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256) {
        throw "Parakeet asset checksum mismatch for $Path. Expected $ExpectedSha256; found $actual."
    }
}

$commonFiles = [ordered]@{
    "config.json" = "666903c76b9798caf2c210afd4f6cd60b08a8dbf9800ec8d7a3bc0d2148ac466"
    "nemo128.onnx" = "a9fde1486ebfcc08f328d75ad4610c67835fea58c73ba57e3209a6f6cf019e9f"
    "vocab.txt" = "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"
}

$precisionFiles =
    if ($Precision -eq "int8") {
        [ordered]@{
            "encoder-model.int8.onnx" = "6139d2fa7e1b086097b277c7149725edbab89cc7c7ae64b23c741be4055aff09"
            "decoder_joint-model.int8.onnx" = "eea7483ee3d1a30375daedc8ed83e3960c91b098812127a0d99d1c8977667a70"
        }
    } else {
        [ordered]@{
            "encoder-model.onnx" = "98a74b21b4cc0017c1e7030319a4a96f4a9506e50f0708f3a516d02a77c96bb1"
            "encoder-model.onnx.data" = "9a22d372c51455c34f13405da2520baefb7125bd16981397561423ed32d24f36"
            "decoder_joint-model.onnx" = "e978ddf6688527182c10fde2eb4b83068421648985ef23f7a86be732be8706c1"
        }
    }

Assert-Command $Curl

$assetsRootFull = (New-Item -ItemType Directory -Force -Path $AssetsRoot).FullName
$modelDir = Join-Path $assetsRootFull "models\parakeet-tdt-0.6b-v3-onnx"
New-Item -ItemType Directory -Force -Path $modelDir | Out-Null

$files = [ordered]@{}
foreach ($entry in $commonFiles.GetEnumerator()) { $files[$entry.Key] = $entry.Value }
foreach ($entry in $precisionFiles.GetEnumerator()) { $files[$entry.Key] = $entry.Value }

Write-Host "Downloading Parakeet TDT ONNX $Precision assets to $modelDir..."
foreach ($entry in $files.GetEnumerator()) {
    $destination = Join-Path $modelDir $entry.Key
    $isValid =
        (Test-Path -LiteralPath $destination -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -eq $entry.Value)

    if ($isValid) {
        Write-Host "Already verified: $($entry.Key)"
    } else {
        $url = "https://huggingface.co/$repoId/resolve/$revision/$($entry.Key)?download=true"
        Write-Host "Downloading: $($entry.Key)"
        & $Curl --location --fail --retry 5 --retry-delay 2 --continue-at - --output $destination $url
        if ($LASTEXITCODE -ne 0) {
            throw "Parakeet asset download failed for $($entry.Key) with exit code $LASTEXITCODE."
        }
    }
}

foreach ($entry in $files.GetEnumerator()) {
    Assert-FileHash (Join-Path $modelDir $entry.Key) $entry.Value
}

$manifest = [pscustomobject]@{
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    repoId = $repoId
    revision = $revision
    precision = $Precision
    modelDir = $modelDir
    files = @(
        $files.GetEnumerator() | ForEach-Object {
            [pscustomobject]@{
                path = $_.Key
                sha256 = $_.Value
            }
        }
    )
}

$manifestPath = Join-Path $modelDir "fsvoice-parakeet-assets.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Parakeet assets are ready."
Write-Host "ModelDir: $modelDir"
Write-Host "Manifest: $manifestPath"
