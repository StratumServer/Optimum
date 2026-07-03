<#
.SYNOPSIS
Builds every Optimum package this host is capable of producing, in one run.
Requires a successful build first (dotnet build VintageStory.slnx -c Release).

Targets: linux-x64, osx-x64, osx-arm64, win-x64. The optimized DLLs are
platform-agnostic IL, so any host can target any platform - but quality varies
(e.g. a .dmg needs macOS hdiutil or a Linux libdmg toolchain; the Windows
package off-Windows needs innoextract). This script reports what the host can do
and runs only the capable targets. There is no native ARM client for Linux or
Windows - x64 runs there via emulation (box64 / Windows-on-ARM).

.PARAMETER OutputDir
Where to write packages. Default: repo root.

.PARAMETER Targets
Subset to build, e.g. -Targets linux-x64,osx-arm64. Default: all capable.

.PARAMETER IncludeDegraded
Also run targets the host can only build in degraded quality (default: on).
Use -IncludeDegraded:$false to build only Full-quality targets.

.EXAMPLE
pwsh ./scripts/package-all.ps1
pwsh ./scripts/package-all.ps1 -Targets linux-x64,osx-arm64 -OutputDir ~/releases
#>

[CmdletBinding()]
param(
    [string]$OutputDir,
    [string[]]$Targets,
    [bool]$IncludeDegraded = $true
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
. "$scriptDir/_hostcaps.ps1"

if (-not $OutputDir) { $OutputDir = Split-Path -Parent $scriptDir }

# `pwsh -File ... -Targets a,b,c` binds the whole thing as one string, so split.
if ($Targets) { $Targets = @($Targets -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

$caps = @(Show-HostCaps)
if ($Targets) { $caps = @($caps | Where-Object { $Targets -contains $_.Target }) }

$runnable = @($caps | Where-Object {
    $_.Quality -eq 'Full' -or ($IncludeDegraded -and $_.Quality -eq 'Degraded')
})
$skipped = @($caps | Where-Object { $runnable -notcontains $_ })

foreach ($s in $skipped) {
    Write-Warning "Skipping $($s.Target) ($($s.Quality)): $($s.Note)"
}
if (-not $runnable) { Write-Host "Nothing to build on this host." -ForegroundColor Yellow; return }

$results = @()
foreach ($c in $runnable) {
    Write-Host "==> Building $($c.Target) ..." -ForegroundColor Cyan
    try {
        switch -Regex ($c.Target) {
            '^linux-x64$' { & "$scriptDir/package-linux.ps1" -OutputDir $OutputDir }
            '^osx-(x64|arm64)$' {
                $arch = $c.Target -replace '^osx-', ''
                & "$scriptDir/package-macos.ps1" -Arch $arch -OutputDir $OutputDir
            }
            '^win-x64$' { & "$scriptDir/package.ps1" -OutputDir $OutputDir -Zip }
        }
        $results += [pscustomobject]@{ Target=$c.Target; Status='OK' }
    } catch {
        Write-Warning "Failed $($c.Target): $_"
        $results += [pscustomobject]@{ Target=$c.Target; Status='FAILED' }
    }
}

Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
foreach ($r in $results) {
    $color = if ($r.Status -eq 'OK') { 'Green' } else { 'Red' }
    Write-Host ("  {0,-10} {1}" -f $r.Target, $r.Status) -ForegroundColor $color
}
if ($results.Status -contains 'FAILED') { exit 1 }
