param(
    [string]$AssetsRoot = "E:\s\temp\VoiceAgent_assets",
    [string]$ModelRelativePath = "models\gemma-4-E2B_q4_0-it.gguf",
    [string]$Image = "ghcr.io/ggml-org/llama.cpp@sha256:7b3d7834fc7307cb54f24f8869b67bfff276404c416452a48d11321bc36a81be",
    [string]$ContainerName = "fsvoice-gemma-gguf",
    [int]$Port = 8081,
    [ValidateRange(8192, 131072)]
    [int]$ContextSize = 16384,
    [string]$GpuLayers = "all",
    [switch]$Restart
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "docker was not found."
}

$assetsRootFull = (Resolve-Path -LiteralPath $AssetsRoot).Path
$modelPath = [IO.Path]::GetFullPath((Join-Path $assetsRootFull $ModelRelativePath))
if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
    throw "Gemma GGUF model was not found: $modelPath"
}

$modelDirectory = Split-Path -Parent $modelPath
$modelName = Split-Path -Leaf $modelPath
$existing = docker ps -a --filter "name=^/$ContainerName$" --format "{{.Names}}"

if ($existing) {
    $running = docker inspect -f "{{.State.Running}}" $ContainerName

    if ($running -eq "true" -and -not $Restart) {
        $props = Invoke-RestMethod "http://127.0.0.1:$Port/props" -TimeoutSec 5
        $actualContextSize = [int]$props.default_generation_settings.n_ctx

        if ($actualContextSize -lt $ContextSize) {
            throw "llama.cpp container '$ContainerName' is running with n_ctx=$actualContextSize. Rerun with -Restart to apply -ContextSize $ContextSize."
        }

        Write-Host "llama.cpp container is already running: $ContainerName"
        Write-Host "ContextSize: $actualContextSize"
        Write-Host "Endpoint: http://127.0.0.1:$Port"
        exit 0
    }

    docker rm -f $ContainerName | Out-Null
}

$containerId =
    docker run -d `
        --name $ContainerName `
        --gpus all `
        -p "${Port}:8080" `
        -v "${modelDirectory}:/models:ro" `
        $Image `
        -m "/models/$modelName" `
        --host 0.0.0.0 `
        --port 8080 `
        -ngl $GpuLayers `
        -c $ContextSize `
        --parallel 1 `
        --metrics

if ($LASTEXITCODE -ne 0) {
    throw "Failed to start the llama.cpp container."
}

$deadline = (Get-Date).AddMinutes(3)
$ready = $false
while ((Get-Date) -lt $deadline) {
    try {
        $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3
        if ($health.status -eq "ok") {
            $ready = $true
            break
        }
    } catch {
        Start-Sleep -Seconds 2
    }

    if ((docker inspect -f "{{.State.Running}}" $ContainerName 2>$null) -ne "true") {
        break
    }
}

if (-not $ready) {
    docker logs --tail 120 $ContainerName
    throw "llama.cpp did not become ready. Container: $containerId"
}

Write-Host "llama.cpp Gemma GGUF is ready."
Write-Host "Container: $ContainerName"
Write-Host "ContextSize: $ContextSize"
Write-Host "Endpoint: http://127.0.0.1:$Port"
Write-Host "FsVoice Gemma endpoint: OpenSourceVoice__Gemma__LlamaCppEndpoint=http://127.0.0.1:$Port"
