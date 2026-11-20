#Requires -Version 5.1
# Tests the pinned official client checks in scripts/bootstrap.ps1 under Windows PowerShell 5.1 and
# PowerShell 7: an archive is accepted only when its size and SHA-256 match vintageStoryPins in
# forks.json, an unpinned name only warns, and the extracted pristine VintagestoryLib.vanilla.dll is
# checked the same way. It also checks that this repository's forks.json pins every client archive.

$ErrorActionPreference = 'Stop'

$realRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bootstrap = [System.IO.File]::ReadAllText((Join-Path $realRoot 'scripts/bootstrap.ps1'))

# The helpers run exactly as they are written in bootstrap.ps1.
$match = [regex]::Match(
    $bootstrap,
    '(?ms)^# Fingerprints of the official client this Optimum release.*?(?=^function Get-PinnedIlspycmdVersion)')
if (-not $match.Success) { throw 'Could not extract the pin helpers from bootstrap.ps1' }
Invoke-Expression $match.Value

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}

# 1. This repository pins all four client archives and the two key files of its version.
$forks = Get-Content -LiteralPath (Join-Path $realRoot 'forks.json') -Raw | ConvertFrom-Json
$realVersion = $forks.vintageStoryVersion
$realPins = $forks.vintageStoryPins.$realVersion
Assert-True ($null -ne $realPins) "forks.json has no pins for $realVersion"
foreach ($name in @(
        "vs_install_win-x64_$realVersion.exe", "vs_client_linux-x64_$realVersion.tar.gz",
        "vs_client_osx-x64_$realVersion.tar.gz", "vs_client_osx-arm64_$realVersion.tar.gz")) {
    $pin = $realPins.archives.$name
    Assert-True ($pin -and $pin.sha256.Length -eq 64 -and [int64]$pin.bytes -gt 100000000) "$name is not pinned"
}
Assert-True ($realPins.extractedFiles.'VintagestoryAPI.dll'.Length -eq 64) 'VintagestoryAPI.dll is not pinned'
Assert-True ($realPins.extractedFiles.'VintagestoryLib.vanilla.dll'.Length -eq 64) 'VintagestoryLib.vanilla.dll is not pinned'

# 2. The helpers, against a throwaway source tree with a tiny pinned archive.
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("optimum-vanilla-pins-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $repoRoot = Join-Path $testRoot 'repo'
    New-Item -ItemType Directory -Force -Path $repoRoot | Out-Null
    $Version = '1.0.0'
    $archiveName = "vs_install_win-x64_$Version.exe"
    $archive = Join-Path $testRoot $archiveName
    [System.IO.File]::WriteAllText($archive, 'official client bytes')
    $pristineLibPath = Join-Path $testRoot 'VintagestoryLib.vanilla.dll'
    [System.IO.File]::WriteAllText($pristineLibPath, 'official library')

    $json = @{
        vintageStoryVersion = $Version
        vintageStoryPins    = @{
            '_comment' = 'ignored'
            $Version   = @{
                archives       = @{ $archiveName = @{ bytes = (Get-Item $archive).Length; sha256 = (Get-FileSha256 $archive) } }
                extractedFiles = @{ 'VintagestoryLib.vanilla.dll' = (Get-FileSha256 $pristineLibPath) }
            }
        }
    } | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'forks.json'), $json)

    Assert-True (Test-PinnedArchive $archive) 'the pinned archive was rejected'

    $tampered = Join-Path $testRoot 'tampered'
    [System.IO.File]::WriteAllText($tampered, 'official client bytes, tampered')
    Assert-True (-not (Test-PinnedArchive $tampered $archiveName 6>$null)) 'an archive with another size was accepted'

    $sameSize = Join-Path $testRoot 'same-size'
    [System.IO.File]::WriteAllText($sameSize, 'official client bytez')
    Assert-True (-not (Test-PinnedArchive $sameSize $archiveName 6>$null)) 'an archive with the right size and another hash was accepted'

    $partial = Join-Path $testRoot 'download.partial'
    Copy-Item -LiteralPath $archive -Destination $partial
    Assert-True (Test-PinnedArchive $partial $archiveName) 'a partial file was not checked under its archive name'

    $unpinned = @(Test-PinnedArchive $tampered 'vs_install_win-x64_9.9.9.exe' 3>&1)
    $warningText = ($unpinned | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
        ForEach-Object { $_.Message }) -join ' '
    Assert-True (($unpinned | Where-Object { $_ -is [bool] }) -contains $true) 'an unpinned archive was rejected'
    Assert-True ($warningText -like '*not pinned*') 'an unpinned archive gave no warning'

    Assert-True (Test-PinnedExtraction) 'the pinned library was rejected'
    [System.IO.File]::WriteAllText($pristineLibPath, 'patched library')
    Assert-True (-not (Test-PinnedExtraction 6>$null)) 'a library that is not the official build was accepted'

    # No pins at all (a source tree that predates them): nothing is rejected.
    Remove-Item -LiteralPath (Join-Path $repoRoot 'forks.json')
    Assert-True (Test-PinnedArchive $tampered $archiveName 3>$null) 'a tree without pins rejected an archive'
    Assert-True (Test-PinnedExtraction) 'a tree without pins rejected the library'

    Write-Host 'vanilla pins: all checks passed'
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
