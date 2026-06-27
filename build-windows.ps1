# Build Optimum for Windows x64 in one step.
# Produces: Optimum-v0.1.1-win-x64/ (ready to run)
# Requirements: .NET 10 SDK, PowerShell 5.1+
#
# Usage: Right-click > Run with PowerShell, or from terminal:
#   powershell -ExecutionPolicy Bypass -File build-windows.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "Checking prerequisites..."
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET 10 SDK not found. Install from https://dotnet.microsoft.com/download"
    exit 1
}
$sdks = dotnet --list-sdks | Where-Object { $_ -match '^10\.' }
if (-not $sdks) {
    Write-Error ".NET 10 SDK not found. Install from https://dotnet.microsoft.com/download"
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
Write-Host "Done: Optimum-v0.1.1-win-x64/"
Write-Host "Run Optimum.exe from that folder."
