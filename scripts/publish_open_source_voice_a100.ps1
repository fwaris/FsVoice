param(
    [string]$Configuration = "Release",
    [string]$RuntimeRoot = ".\artifacts\open_source_voice_a100_runtime",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\FsVoice.OpenSource.Server\FsVoice.OpenSource.Server.fsproj"
$runtimeRootFull = Join-Path $repoRoot $RuntimeRoot

if (Test-Path -LiteralPath $runtimeRootFull) {
    Remove-Item -LiteralPath $runtimeRootFull -Recurse -Force
}

$publishArgs = @(
    "publish",
    $project,
    "-c", $Configuration,
    "-o", $runtimeRootFull,
    "--self-contained", "false"
)

if ($NoBuild) {
    $publishArgs += "--no-build"
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "run-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "smoke-open-source-voice-a100.ps1") -Destination $runtimeRootFull -Force

$readme = @"
# FsVoice Open-Source Voice Runtime

Run:

````powershell
.\run-open-source-voice-a100.ps1 -AssetsRoot G:\Chroma\VoiceAgent_assets -TtsMaxSteps 256
````

Smoke:

````powershell
.\smoke-open-source-voice-a100.ps1 -AssetsRoot G:\Chroma\VoiceAgent_assets -RequireReady
````

The runtime folder intentionally excludes Gemma and Chatterbox weights. Stage those under AssetsRoot with:

````powershell
.\scripts\download_open_source_voice_assets.ps1 -AssetsRoot G:\Chroma\VoiceAgent_assets
````
"@

Set-Content -LiteralPath (Join-Path $runtimeRootFull "README.open-source-voice.md") -Value $readme -Encoding UTF8

$sizeBytes =
    Get-ChildItem -LiteralPath $runtimeRootFull -Recurse -File |
    Measure-Object -Property Length -Sum |
    Select-Object -ExpandProperty Sum

Write-Host "RuntimeRoot: $runtimeRootFull"
Write-Host ("ExpandedSizeMB: {0:n1}" -f ($sizeBytes / 1MB))

