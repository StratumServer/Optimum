# Build Optimum for Windows x64 in one step.
# Produces: Optimum-v<VERSION>-win-x64/ (ready to run)
# Requirements: .NET 10 SDK, PowerShell 5.1+
#
# Usage: Right-click > Run with PowerShell, or from terminal:
#   powershell -ExecutionPolicy Bypass -File build-windows.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Checking prerequisites..."
$gitInstallUrl = 'https://git-scm.com/download/win'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET 10 SDK not found. Install from https://dotnet.microsoft.com/download"
    exit 1
}
$sdks = dotnet --list-sdks | Where-Object { $_ -match '^10\.' }
if (-not $sdks) {
    Write-Error ".NET 10 SDK not found. Install from https://dotnet.microsoft.com/download"
    exit 1
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Error "Git not found. Install Git for Windows from $gitInstallUrl"
    exit 1
}

Write-Host "Running bootstrap (downloads ~570MB on first run)..."
& .\scripts\bootstrap.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building..."
dotnet build VintageStory.slnx -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Packaging Windows x64..."
& .\scripts\package.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done: Optimum-v$((Get-Content (Join-Path $PSScriptRoot '..\\VERSION') -Raw).Trim())-win-x64/"
Write-Host "Run Optimum.exe from that folder."
