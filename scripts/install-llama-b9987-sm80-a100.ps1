param(
    [string]$LlamaCppRoot = "G:\Chroma\llama-b9987-bin-win-cuda-13.3-x64",
    [string]$Sm80DllPath = (Join-Path $PSScriptRoot "ggml-cuda-sm80.dll")
)

$ErrorActionPreference = "Stop"
$expectedSha256 = "33f7fa7da9fd6bfef7b55bc0385768388200b7b4829c356adab9f67b4eedb3c1"

if (Get-Process -Name "llama-server" -ErrorAction SilentlyContinue) {
    throw "llama-server.exe is running. Stop it before replacing ggml-cuda.dll."
}

if (-not (Test-Path -LiteralPath $LlamaCppRoot -PathType Container)) {
    throw "llama.cpp directory was not found: $LlamaCppRoot"
}

if (-not (Test-Path -LiteralPath $Sm80DllPath -PathType Leaf)) {
    throw "SM80 CUDA DLL was not found: $Sm80DllPath"
}

$sourceHash = (Get-FileHash -LiteralPath $Sm80DllPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceHash -ne $expectedSha256) {
    throw "SM80 CUDA DLL checksum mismatch. Expected $expectedSha256 but found $sourceHash."
}

$targetPath = Join-Path $LlamaCppRoot "ggml-cuda.dll"
$backupPath = Join-Path $LlamaCppRoot "ggml-cuda.official-b9987-sm86-sm89.dll"

if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
    throw "Existing b9987 CUDA DLL was not found: $targetPath"
}

$targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($targetHash -eq $expectedSha256) {
    Write-Host "The SM80 CUDA DLL is already installed: $targetPath"
    exit 0
}

if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
    Copy-Item -LiteralPath $targetPath -Destination $backupPath
    Write-Host "Backed up the official CUDA DLL to: $backupPath"
}

Copy-Item -LiteralPath $Sm80DllPath -Destination $targetPath -Force
$installedHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($installedHash -ne $expectedSha256) {
    throw "Installed SM80 CUDA DLL checksum verification failed."
}

Write-Host "Installed the b9987 CUDA 13.3 SM80-only backend: $targetPath"
Write-Host "SHA256: $installedHash"
Write-Host "You can now rerun run-gemma-llama-cpp-windows-a100.ps1."
