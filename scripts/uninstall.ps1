<#
.SYNOPSIS
Removes an Optimum standalone package or Optimum files from an existing VS directory.

.PARAMETER InstallDir
Path to an Optimum standalone package. The installer passes this value.

.PARAMETER VsDir
Path to a legacy in-place installation. The script removes Optimum files and
leaves vanilla Vintage Story files in place.

.PARAMETER Force
Skip the confirmation prompt.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$VsDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$registryPath = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1'
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $InstallDir -and $VsDir) {
    $InstallDir = $VsDir
}
if (-not $InstallDir) {
    $InstallDir = $scriptDirectory
}

$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')

function Get-NormalizedPath([string]$Path) {
    if (-not $Path) { return $null }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

$registered = Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue
$registeredDir = if ($registered -and $registered.InstallLocation) {
    Get-NormalizedPath $registered.InstallLocation
} else {
    $null
}
$registeredTarget = $registeredDir -and ((Get-NormalizedPath $InstallDir) -ieq $registeredDir)
$standaloneMarker = Join-Path $InstallDir '.optimum/standalone-install'
$isStandalone = Test-Path $standaloneMarker
$uninstallLog = Join-Path ([IO.Path]::GetTempPath()) 'optimum-uninstall.log'

function Write-UninstallLog([string]$Message) {
    try {
        Add-Content -LiteralPath $uninstallLog -Value ((Get-Date -Format 's') + ' ' + $Message)
    } catch { }
}

Write-UninstallLog "Starting cleanup for $InstallDir"

$optimumFiles = @(
    'Optimum.exe'
    'Optimum.dll'
    'Optimum.deps.json'
    'Optimum.runtimeconfig.json'
    'Optimum.Patcher.dll'
    'Optimum.Api.Contracts.dll'
    'Optimum.GameContent.dll'
    'VintagestoryLib.Donor.dll'
    'Mono.Cecil.dll'
    'Mono.Cecil.Mdb.dll'
    'Mono.Cecil.Pdb.dll'
    'Mono.Cecil.Rocks.dll'
    'datapath.cfg'
)

Write-Host ''
Write-Host '  Optimum Uninstaller' -ForegroundColor Cyan
Write-Host ''
Write-Host "  Directory: $InstallDir" -ForegroundColor DarkGray
Write-Host ''

if (-not $Force) {
    $answer = Read-Host '  Remove Optimum? Vanilla VS will NOT be affected. [y/N]'
    if ($answer -notmatch '^[Yy]') {
        Write-Host '  Cancelled.'
        exit 0
    }
}

$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Optimum.lnk'
$startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'Optimum'
$startMenuLink = Join-Path $startMenuDir 'Optimum.lnk'
foreach ($link in @($desktopLink, $startMenuLink)) {
    if (Test-Path $link) {
        Remove-Item -Force $link -ErrorAction SilentlyContinue
    }
}
if (Test-Path $startMenuDir) {
    Remove-Item -Recurse -Force $startMenuDir -ErrorAction SilentlyContinue
}
if (Test-Path $registryPath) {
    Remove-Item -Recurse -Force $registryPath -ErrorAction SilentlyContinue
}

if ($isStandalone -and ($registeredTarget -or (Test-Path (Join-Path $InstallDir 'Optimum.exe')))) {
    $batchTarget = $InstallDir.Replace('%', '%%')
    $batchLog = Join-Path ([IO.Path]::GetTempPath()) ('optimum-uninstall-' + [Guid]::NewGuid().ToString('N') + '.log')
    $cleanupScript = Join-Path ([IO.Path]::GetTempPath()) (
        'optimum-uninstall-' + [Guid]::NewGuid().ToString('N') + '.cmd')
    $cleanupLines = @(
        '@echo off'
        'setlocal'
        ('set "target={0}"' -f $batchTarget)
        ('set "log={0}"' -f $batchLog.Replace('%', '%%'))
        'echo Cleanup started>"%log%"'
        'set /a attempts=0'
        ':retry'
        'rmdir /s /q "%target%" >nul 2>&1'
        'if not exist "%target%" goto done'
        'set /a attempts+=1'
        'if %attempts% GEQ 60 goto done'
        'timeout /t 1 /nobreak >nul 2>&1'
        'goto retry'
        ':done'
        'if exist "%target%" (echo Cleanup failed after 60 attempts>>"%log%") else (echo Cleanup complete>>"%log%")'
        'del "%~f0" >nul 2>&1'
    )
    [IO.File]::WriteAllText(
        $cleanupScript,
        ($cleanupLines -join [Environment]::NewLine),
        [Text.Encoding]::ASCII)
    $command = '/d /c call "' + $cleanupScript + '"'
    Start-Process -FilePath $env:ComSpec -ArgumentList $command -WindowStyle Hidden | Out-Null
    Write-UninstallLog "Scheduled standalone cleanup: $InstallDir"
    Write-Host "  Removal scheduled. Cleanup log: $batchLog" -ForegroundColor Green
    Write-Host '  Vintage Story was not modified.' -ForegroundColor Green
    exit 0
}

$removed = 0
$failedPaths = [System.Collections.Generic.List[string]]::new()
foreach ($file in $optimumFiles) {
    $path = Join-Path $InstallDir $file
    if (Test-Path $path) {
        try {
            Remove-Item -Force $path -ErrorAction Stop
            $removed++
        } catch {
            $failedPaths.Add($path)
            Write-UninstallLog "Could not remove ${path}: $($_.Exception.Message)"
        }
    }
}

$optimumDirectory = Join-Path $InstallDir '.optimum'
if (Test-Path $optimumDirectory) {
    try {
        Remove-Item -Recurse -Force $optimumDirectory -ErrorAction Stop
        $removed++
    } catch {
        $failedPaths.Add($optimumDirectory)
        Write-UninstallLog "Could not remove ${optimumDirectory}: $($_.Exception.Message)"
    }
}

if ($failedPaths.Count -gt 0) {
    Write-Host "  Cleanup failed for $($failedPaths.Count) path(s). Close Optimum and retry." -ForegroundColor Red
    Write-Host "  Log: $uninstallLog" -ForegroundColor DarkGray
    exit 1
}

Write-Host ''
Write-Host "  Removed $removed Optimum file(s). Vintage Story is intact." -ForegroundColor Green
Write-Host ''
