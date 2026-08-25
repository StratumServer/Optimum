<#
Shared host-capability detection for the packaging scripts.
Dot-source it:  . "$PSScriptRoot/_hostcaps.ps1"

Vintage Story only ships x64 native clients for Windows and Linux (the Windows
one as an Inno installer .exe), and both x64+arm64 for macOS. So:
  - Linux/Windows packages are x64-only; ARM there runs x64 via emulation
    (box64 on Linux, Windows-on-ARM x64 emulation).
  - macOS is the only target with a real arm64 client.
This file reports what the CURRENT host can actually produce, without installing
anything.
#>

function Get-HostOS {
    if ($IsWindows -or ($env:OS -eq 'Windows_NT')) { 'Windows' }
    elseif ($IsMacOS) { 'macOS' }
    elseif ($IsLinux) { 'Linux' }
    else { 'Unknown' }
}

function Test-Cmd { param([string]$Name) [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

function Get-InnoextractVersion {
    $command = Get-Command innoextract -ErrorAction SilentlyContinue
    if (-not $command) { return $null }

    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $command.Path --version 2>&1)
        $exitCode = $LASTEXITCODE
    } catch {
        return $null
    } finally {
        $ErrorActionPreference = $previousEap
    }
    if ($exitCode -ne 0) { return $null }

    $text = $output -join [Environment]::NewLine
    if ($text -notmatch '(?im)^\s*innoextract\s+(\d+)\.(\d+)(?:\.(\d+))?') { return $null }
    $patch = if ($Matches[3]) { [int]$Matches[3] } else { 0 }
    return [Version]::new([int]$Matches[1], [int]$Matches[2], $patch)
}

function Test-InnoextractMinimumVersion {
    $version = Get-InnoextractVersion
    $version -and $version -ge [Version]'1.11'
}

function Test-CachedWindowsClient {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $clientDir = Join-Path $repoRoot '.vanilla/win-x64/package-client'
    $marker = @(Get-ChildItem -Path (Join-Path $clientDir 'assets') -Filter 'version-*.txt' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1)
    (Test-Path (Join-Path $clientDir 'Vintagestory.exe')) -and $marker.Count -gt 0
}

# Returns an array of capability objects: Target, Quality (Full/Degraded/Blocked), Note.
function Get-HostCaps {
    $os = Get-HostOS
    $caps = @()

    # --- Linux target (tar.gz/zip) ---
    if (Test-Cmd tar) {
        $caps += [pscustomobject]@{ Target='linux-x64'; Quality='Full'; Note='overlay DLLs on vanilla linux client' }
    } else {
        $caps += [pscustomobject]@{ Target='linux-x64'; Quality='Blocked'; Note='tar not found' }
    }

    # --- macOS target (.app + .dmg) ---
    foreach ($arch in 'x64','arm64') {
        if ($os -eq 'macOS' -and (Test-Cmd hdiutil)) {
            $caps += [pscustomobject]@{ Target="osx-$arch"; Quality='Full'; Note='hdiutil .dmg (notarizable)' }
        } elseif (($os -eq 'Linux') -and ((Test-Cmd mkisofs) -or (Test-Cmd genisoimage)) -and (Test-Cmd cmake) -and (Test-Cmd git)) {
            $caps += [pscustomobject]@{ Target="osx-$arch"; Quality='Degraded'; Note='unsigned .dmg via libdmg-hfsplus' }
        } else {
            $caps += [pscustomobject]@{ Target="osx-$arch"; Quality='Degraded'; Note='.app assembled, .tar.gz fallback (no .dmg toolchain)' }
        }
    }

    # --- Windows target (folder + zip, Optimum.exe) ---
    if ($os -eq 'Windows') {
        $caps += [pscustomobject]@{ Target='win-x64'; Quality='Full'; Note='native build + local vanilla client' }
    } else {
        $innoVersion = Get-InnoextractVersion
        $hasExtract = $innoVersion -and $innoVersion -ge [Version]'1.11'
        $hasRid     = (Test-Cmd dotnet)   # cross-build the win-x64 apphost
        $hasCachedWinClient = Test-CachedWindowsClient
        if ($hasCachedWinClient -and $hasRid) {
            $caps += [pscustomobject]@{ Target='win-x64'; Quality='Degraded'; Note='cross-build Optimum.exe + cached Windows client' }
        } elseif ($hasExtract -and $hasRid) {
            $caps += [pscustomobject]@{ Target='win-x64'; Quality='Degraded'; Note="cross-build Optimum.exe + innoextract $innoVersion for the official Inno 6.4.3 installer" }
        } elseif (-not $innoVersion) {
            $caps += [pscustomobject]@{ Target='win-x64'; Quality='Blocked'; Note='need innoextract >= 1.11 to unpack the official Windows installer (vs_install_win-x64_*.exe)' }
        } elseif ($innoVersion -lt [Version]'1.11') {
            $caps += [pscustomobject]@{ Target='win-x64'; Quality='Blocked'; Note="innoextract $innoVersion is too old; need innoextract >= 1.11 for Inno Setup 6.4.3" }
        } else {
            $caps += [pscustomobject]@{ Target='win-x64'; Quality='Blocked'; Note='need dotnet to cross-build the win-x64 launcher' }
        }
    }

    $caps
}

function Show-HostCaps {
    param([string[]]$Only)  # optional: filter to specific targets
    $os = Get-HostOS
    $caps = Get-HostCaps
    if ($Only) { $caps = $caps | Where-Object { $Only -contains $_.Target } }

    Write-Host ""
    Write-Host "Host: $os - packaging capability [no native ARM client for Linux/Windows; x64 via emulation]" -ForegroundColor Cyan
    foreach ($c in $caps) {
        $color = switch ($c.Quality) { 'Full' {'Green'} 'Degraded' {'Yellow'} default {'Red'} }
        Write-Host ("  {0,-10} {1,-9} {2}" -f $c.Target, $c.Quality, $c.Note) -ForegroundColor $color
    }
    Write-Host ""
    $caps
}
