<#
.SYNOPSIS
Legacy entry point for the Optimum Windows installer.

.DESCRIPTION
Forwards supported arguments to install-windows.ps1. Optimum requires the user
to install Vintage Story before running either installer.
#>

[CmdletBinding()]
param(
    [switch]$Silent,
    [string]$InstallDir,
    [string]$DataPath,
    [switch]$Shortcut,
    [switch]$StartMenu,
    [string]$LogFile,
    [string]$VsPath,
    [switch]$DownloadVs
)

$ErrorActionPreference = 'Stop'

if ($DownloadVs) {
    throw 'Optimum does not download Vintage Story. Install Vintage Story first and pass -VsPath with its installation folder.'
}

$forwardParameters = @{}
foreach ($entry in $PSBoundParameters.GetEnumerator()) {
    if ($entry.Key -ne 'DownloadVs') {
        $forwardParameters[$entry.Key] = $entry.Value
    }
}

& (Join-Path $PSScriptRoot 'install-windows.ps1') @forwardParameters
exit $LASTEXITCODE
