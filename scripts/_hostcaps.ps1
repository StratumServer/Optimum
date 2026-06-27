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
        $hasExtract = (Test-Cmd innoextract)
        $hasRid     = (Test-Cmd dotnet)   # cross-build the win-x64 apphost
        if ($hasExtract -and $hasRid) {
            $caps += [pscustomobject]@{ Target='win-x64'; Quality='Degraded'; Note='cross-build Optimum.exe + innoextract vanilla installer' }
        } elseif (-not $hasExtract) {
            $caps += [pscustomobject]@{ Target='win-x64'; Quality='Blocked'; Note='need innoextract to unpack the Windows installer (vs_install_win-x64_*.exe)' }
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
