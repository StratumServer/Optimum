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

function Ensure-GcConfigProperties($runtimeOptions) {
    $modified = $false
    if (-not $runtimeOptions.PSObject.Properties['configProperties']) {
        $runtimeOptions | Add-Member -NotePropertyName configProperties -NotePropertyValue ([PSCustomObject]@{}) -Force
        $modified = $true
    }
    $cfg = $runtimeOptions.configProperties
    if ($cfg.PSObject.Properties['System.GC.Server'] -eq $null -or $cfg.'System.GC.Server' -ne $true) {
        $cfg | Add-Member -NotePropertyName 'System.GC.Server' -NotePropertyValue $true -Force
        $modified = $true
    }
    if ($cfg.PSObject.Properties['System.GC.Concurrent'] -eq $null -or $cfg.'System.GC.Concurrent' -ne $true) {
        $cfg | Add-Member -NotePropertyName 'System.GC.Concurrent' -NotePropertyValue $true -Force
        $modified = $true
    }
    if ($cfg.PSObject.Properties['System.GC.DATAs'] -eq $null -or $cfg.'System.GC.DATAs' -ne 1) {
        $cfg | Add-Member -NotePropertyName 'System.GC.DATAs' -NotePropertyValue 1 -Force
        $modified = $true
    }
    return $modified
}

$launcher = Get-Content -Raw -LiteralPath $LauncherConfig | ConvertFrom-Json
$vanilla = Get-Content -Raw -LiteralPath $VanillaConfig | ConvertFrom-Json

$launcherModified = Ensure-GcConfigProperties $launcher.runtimeOptions
$vanillaModified = $false
if ($vanilla -and $vanilla.runtimeOptions) {
    $vanillaModified = Ensure-GcConfigProperties $vanilla.runtimeOptions
    if ($vanillaModified) {
        $vanillaJson = $vanilla | ConvertTo-Json -Depth 10
        [IO.File]::WriteAllText($VanillaConfig, $vanillaJson + "`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Configured Server and Concurrent GC in vanilla runtimeconfig."
    }
}

$launcherFrameworks = @(Get-FrameworkEntries $launcher.runtimeOptions)
$known = @($launcherFrameworks | ForEach-Object { $_.name })
$missing = @(Get-FrameworkEntries $vanilla.runtimeOptions | Where-Object { $known -notcontains $_.name })

if ($missing.Count -eq 0) {
    if ($launcherModified) {
        $json = $launcher | ConvertTo-Json -Depth 10
        [IO.File]::WriteAllText($LauncherConfig, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Configured Server and Concurrent GC in launcher runtimeconfig."
    } else {
        Write-Host "Launcher runtimeconfig already names every framework vanilla names."
    }
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
