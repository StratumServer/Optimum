#Requires -Version 7.0
<#
.SYNOPSIS
  Adds the shared frameworks named by vanilla's runtimeconfig to the launcher's runtimeconfig.

.DESCRIPTION
  The Optimum launcher hosts the whole game in its own process, so its
  Optimum.runtimeconfig.json decides which shared frameworks the game can load. The
  launcher project only references Microsoft.NETCore.App. Vanilla's Windows client
  also references Microsoft.WindowsDesktop.App, which holds PresentationFramework
  (Eto.Wpf, behind XPlatImpl.ShowMessageBox). Without that reference a startup dialog
  throws FileNotFoundException for PresentationFramework and hides the real error.

  The script copies every framework entry from the vanilla config that the launcher
  config does not name yet. It rewrites the launcher config only when it adds
  something, and it never removes or changes an existing entry, so running it twice
  gives the same file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherConfig,
    [Parameter(Mandatory)][string]$VanillaConfig
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FrameworkEntries($runtimeOptions) {
    if ($runtimeOptions.PSObject.Properties['frameworks']) { return @($runtimeOptions.frameworks) }
    if ($runtimeOptions.PSObject.Properties['framework']) { return @($runtimeOptions.framework) }
    return @()
}

$launcher = Get-Content -Raw -LiteralPath $LauncherConfig | ConvertFrom-Json
$vanilla = Get-Content -Raw -LiteralPath $VanillaConfig | ConvertFrom-Json

$launcherFrameworks = @(Get-FrameworkEntries $launcher.runtimeOptions)
$known = @($launcherFrameworks | ForEach-Object { $_.name })
$missing = @(Get-FrameworkEntries $vanilla.runtimeOptions | Where-Object { $known -notcontains $_.name })

if ($missing.Count -eq 0) {
    Write-Host "Launcher runtimeconfig already names every framework vanilla names."
    return
}

# runtimeconfig.json allows either a single "framework" or a "frameworks" array.
# Several references need the array form.
$merged = @($launcherFrameworks + $missing)
$launcher.runtimeOptions.PSObject.Properties.Remove('framework')
$launcher.runtimeOptions | Add-Member -NotePropertyName frameworks -NotePropertyValue $merged -Force

$json = $launcher | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($LauncherConfig, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("Launcher runtimeconfig now names: " + (($merged | ForEach-Object { $_.name }) -join ', '))
