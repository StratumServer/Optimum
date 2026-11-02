<#
Regenerate a single patch natively on Windows, mirroring extract-patches.sh:
  git diff --no-index -U5 between the pristine .baseline file and the modified
  working-tree file, then rewrite the diff/---/+++ headers to a/<rel> b/<rel>.

Usage:
  scripts\regen-one-patch.ps1 -Project VintagestoryLib -Rel "Vintagestory.Common/EventManager.cs"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Rel   # path relative to the project dir, forward slashes
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Vanilla decompiled projects live under build/.
$vanilla = @('VintagestoryLib', 'Vintagestory')
if ($vanilla -contains $Project) {
    $workFile = Join-Path $repoRoot "build/$Project/$Rel"
} else {
    $workFile = Join-Path $repoRoot "$Project/$Rel"
}
$baseFile  = Join-Path $repoRoot ".baseline/$Project/$Rel"
$patchFile = Join-Path $repoRoot "patches/$Project/$Rel.patch"

if (-not (Test-Path $baseFile)) { throw "No baseline: $baseFile" }
if (-not (Test-Path $workFile)) { throw "No working file: $workFile" }

# Normalize CRLF->LF into temp files (BOM preserved, matching the .sh).
$tmpBase = [System.IO.Path]::GetTempFileName()
$tmpWork = [System.IO.Path]::GetTempFileName()
$baseText = [System.IO.File]::ReadAllText($baseFile) -replace "`r`n", "`n" -replace "`r", "`n"
$workText = [System.IO.File]::ReadAllText($workFile) -replace "`r`n", "`n" -replace "`r", "`n"
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($tmpBase, $baseText, $utf8)
[System.IO.File]::WriteAllText($tmpWork, $workText, $utf8)

$raw = & git --no-pager -c core.safecrlf=false -c diff.algorithm=myers -c diff.noprefix=false diff --no-color --no-index -U5 -- $tmpBase $tmpWork 2>$null

Remove-Item $tmpBase, $tmpWork -ErrorAction SilentlyContinue

if (-not $raw) { throw "git diff produced no output (files identical?)" }

$a = "a/$Project/$Rel"
$b = "b/$Project/$Rel"
$out = foreach ($line in $raw) {
    if ($line -like 'diff --git *') { "diff --git $a $b" }
    elseif ($line -like '--- *')     { "--- $a" }
    elseif ($line -like '+++ *')     { "+++ $b" }
    else { $line }
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $patchFile) | Out-Null
[System.IO.File]::WriteAllText($patchFile, (($out -join "`n") + "`n"), $utf8)
Write-Host "Wrote $patchFile"
