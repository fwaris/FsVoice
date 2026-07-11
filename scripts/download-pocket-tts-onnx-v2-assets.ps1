param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsRoot,
    [ValidateSet("int8", "fp32")]
    [string]$Precision = "int8",
    [string]$ModelDirectoryName = "pocket-tts-onnx-english-2026-04",
    [ValidatePattern("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")]
    [string]$RepoId = "KevinAHM/pocket-tts-onnx",
    [ValidatePattern("^[A-Fa-f0-9]{40}$")]
    [string]$Revision = "58a6d00cf13d239b6748cb0769f35c580a8f606c",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$language = "english_2026-04"
# The validated upstream inference path keeps voice encoding and text conditioning
# in FP32 even when the autoregressive/flow/decoder graphs use INT8.
$commonFiles = @(
    "bundle.json",
    "bos_before_voice.npy",
    "tokenizer.model",
    "mimi_encoder.onnx",
    "text_conditioner.onnx",
    "LICENSE"
)
$precisionFiles =
    if ($Precision -eq "int8") {
        @(
            "flow_lm_flow_int8.onnx",
            "flow_lm_main_int8.onnx",
            "mimi_decoder_int8.onnx"
        )
    } else {
        @(
            "flow_lm_flow.onnx",
            "flow_lm_main.onnx",
            "mimi_decoder.onnx"
        )
    }
$requiredFiles = @($commonFiles) + @($precisionFiles)

# These hashes pin the exact English April bundle that was validated with FsVoice.
$expectedSha256 = @{
    "bundle.json" = "bab643150f437f37df080a710520ff39ed9ebd9a339f8ebdc739f7eddfc28b3f"
    "bos_before_voice.npy" = "f46edf4f7007b7ba4ea58831f49d003e59e167b4641c44bb3addfe9231a780b1"
    "tokenizer.model" = "d461765ae179566678c93091c5fa6f2984c31bbe990bf1aa62d92c64d91bc3f6"
    "mimi_encoder.onnx" = "853e2ca623b8782d94c3745ec6133bfdff7ce33d9b11128bd29ea03f28d76e3d"
    "text_conditioner.onnx" = "4ecee995fb69f85c7a7493d11f7b5ee15d9950facc7ab3f5c9c49ef1e03847bb"
    "flow_lm_flow_int8.onnx" = "3dd781ee5abee9e195320bf0106bebd6372a852b3b36352524ee78b40554635d"
    "flow_lm_main_int8.onnx" = "f9bd8106b79a0192c1c43399ab938fb24900a95c1c599870d75a884e99000116"
    "mimi_decoder_int8.onnx" = "3630450a3297a101792a6ac66619ebc70ab916b265e6220c2afaef8b1673f925"
    "flow_lm_flow.onnx" = "085d239f68897e28fb06e95c743738ad8b8c092ee6dc55f5491313e81ff08062"
    "flow_lm_main.onnx" = "6d18315e2c33ca3e3aa4a4e3dca22f56d007fd823127e24948b37695bf54190f"
    "mimi_decoder.onnx" = "86f038caa02a96a0ff9c25526a0ff43a4906c418197ed72d3e30f720ac7ce802"
    "LICENSE" = "fe7b4ce83b8381cc5b216bbb4af73c570688d1b819c73bbaed8ca401f4677cd6"
}

function Test-ExpectedFile([string]$Path, [string]$ExpectedHash) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actualHash -eq $ExpectedHash
}

function Get-RemotePath([string]$FileName) {
    if ($FileName -eq "LICENSE") {
        return "onnx/LICENSE"
    }

    return "onnx/$language/$FileName"
}

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
$modelsPrefix = [IO.Path]::GetFullPath($modelsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($modelsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Model destination must remain under the assets model directory: $destination"
}

$filesToDownload =
    $requiredFiles |
    Where-Object {
        $path = Join-Path $destination $_
        $Force -or -not (Test-ExpectedFile $path $expectedSha256[$_])
    }

if ($filesToDownload.Count -eq 0) {
    Write-Host "Pocket TTS April ONNX $Precision assets are already ready: $destination"
    exit 0
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $temporaryBase ("fsvoice-pocket-tts-v2-" + [Guid]::NewGuid().ToString("N"))))
if (-not $temporaryRoot.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary download path escaped the system temp directory: $temporaryRoot"
}

New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null

try {
    foreach ($fileName in $filesToDownload) {
        $remotePath = Get-RemotePath $fileName
        $url = "https://huggingface.co/$RepoId/resolve/$Revision/$remotePath"
        $downloadPath = Join-Path $temporaryRoot $fileName
        Write-Host "Downloading $fileName from $RepoId at revision $Revision..."
        Invoke-WebRequest -Uri $url -OutFile $downloadPath -UseBasicParsing

        if (-not (Test-ExpectedFile $downloadPath $expectedSha256[$fileName])) {
            throw "Downloaded file failed SHA-256 validation: $fileName"
        }
    }

    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($fileName in $filesToDownload) {
        Copy-Item -LiteralPath (Join-Path $temporaryRoot $fileName) -Destination (Join-Path $destination $fileName) -Force
    }

    foreach ($fileName in $requiredFiles) {
        $path = Join-Path $destination $fileName
        if (-not (Test-ExpectedFile $path $expectedSha256[$fileName])) {
            throw "Pocket TTS April ONNX asset is missing or invalid after staging: $path"
        }
    }

    $availablePrecisions = @()
    $int8Files = @("flow_lm_flow_int8.onnx", "flow_lm_main_int8.onnx", "mimi_decoder_int8.onnx")
    $fp32Files = @("flow_lm_flow.onnx", "flow_lm_main.onnx", "mimi_decoder.onnx")
    if (($int8Files | Where-Object { -not (Test-ExpectedFile (Join-Path $destination $_) $expectedSha256[$_]) }).Count -eq 0) {
        $availablePrecisions += "int8"
    }
    if (($fp32Files | Where-Object { -not (Test-ExpectedFile (Join-Path $destination $_) $expectedSha256[$_]) }).Count -eq 0) {
        $availablePrecisions += "fp32"
    }

    $manifest = [pscustomobject]@{
        createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
        repoId = $RepoId
        revision = $Revision
        language = $language
        availablePrecisions = $availablePrecisions
        files = Get-ChildItem -LiteralPath $destination -File |
            Where-Object { $expectedSha256.ContainsKey($_.Name) } |
            Sort-Object Name |
            ForEach-Object {
                [pscustomobject]@{
                    name = $_.Name
                    bytes = $_.Length
                    sha256 = $expectedSha256[$_.Name]
                }
            }
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination "fsvoice_asset_manifest.json") -Encoding UTF8

    Write-Host "Pocket TTS April ONNX $Precision assets ready: $destination"
    Write-Host "Available precisions: $($availablePrecisions -join ', ')"
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
