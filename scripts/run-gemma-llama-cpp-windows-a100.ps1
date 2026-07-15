param(
    [string]$LlamaCppRoot = "G:\Chroma\llama-b9987-bin-win-cuda-13.3-x64",
    [string]$AssetsRoot = "G:\Chroma\VoiceAgent_assets",
    [string]$ModelRelativePath = "models\gemma-4-E2B_q4_0-it.gguf",
    [string]$HostAddress = "127.0.0.1",
    [int]$Port = 8081,
    [ValidateRange(8192, 131072)]
    [int]$ContextSize = 16384,
    [string]$GpuLayers = "all",
    [string]$CudaVisibleDevices = "0"
)

$ErrorActionPreference = "Stop"

function Resolve-LlamaServer([string]$Root) {
    $candidates = @(
        (Join-Path $Root "llama-server.exe"),
        (Join-Path $Root "server.exe"),
        (Join-Path $Root "bin\llama-server.exe"),
        (Join-Path $Root "bin\server.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $recursiveMatch =
        Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq "llama-server.exe" -or $_.Name -eq "server.exe" } |
        Select-Object -First 1

    if ($null -ne $recursiveMatch) {
        return $recursiveMatch.FullName
    }

    throw "llama.cpp server executable was not found under: $Root"
}

if (-not (Test-Path -LiteralPath $LlamaCppRoot -PathType Container)) {
    throw "llama.cpp directory was not found: $LlamaCppRoot"
}

$assetsRootFull = (Resolve-Path -LiteralPath $AssetsRoot).Path
$modelPath = [IO.Path]::GetFullPath((Join-Path $assetsRootFull $ModelRelativePath))
if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
    throw "Gemma GGUF model was not found: $modelPath"
}

$serverExe = Resolve-LlamaServer $LlamaCppRoot
$env:CUDA_VISIBLE_DEVICES = $CudaVisibleDevices

$serverArgs = @(
    "--model", $modelPath,
    "--host", $HostAddress,
    "--port", "$Port",
    "--gpu-layers", $GpuLayers,
    "--ctx-size", "$ContextSize",
    "--parallel", "1",
    "--metrics"
)

Write-Host "Starting native llama.cpp Gemma server."
Write-Host "Executable: $serverExe"
Write-Host "Model: $modelPath"
Write-Host "CUDA_VISIBLE_DEVICES: $CudaVisibleDevices"
Write-Host "ContextSize: $ContextSize"
Write-Host "Endpoint: http://${HostAddress}:$Port"
Write-Host "Keep this window open while FsVoice is running. Press Ctrl+C to stop."

Push-Location (Split-Path -Parent $serverExe)
try {
    & $serverExe @serverArgs
    if ($LASTEXITCODE -ne 0) {
        throw "llama.cpp server exited with code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}
