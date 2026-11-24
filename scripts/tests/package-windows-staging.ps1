#Requires -Version 5.1
# Verifies that scripts/package.ps1 never overwrites vanilla VintagestoryAPI.dll or stages
# stray Lib-owned DLLs from Optimum.Launcher output, and that Test-StagedVanillaIntegrity
# rejects tampered core assemblies or stray root DLLs under Windows PowerShell 5.1 and PowerShell 7.

$ErrorActionPreference = 'Stop'

$realRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$packageScript = [System.IO.File]::ReadAllText((Join-Path $realRoot 'scripts/package.ps1'))

$helperMatch = [regex]::Match(
    $packageScript,
    '(?ms)^function Test-StagedVanillaIntegrity \{.*?^\}')
if (-not $helperMatch.Success) { throw 'Could not extract Test-StagedVanillaIntegrity from scripts/package.ps1' }
Invoke-Expression $helperMatch.Value

$copyMatch = [regex]::Match(
    $packageScript,
    '(?ms)\$excludedLauncherDlls = @\(.*?ForEach-Object \{ Copy-Item -Force \$_\.FullName \$stageDir \}')
if (-not $copyMatch.Success) { throw 'Could not extract launcher DLL staging loop from scripts/package.ps1' }

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try {
        & $Action
    } catch {
        $threw = $true
    }
    if (-not $threw) { throw "FAIL: $Message" }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("optimum-package-staging-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $repoRoot = Join-Path $testRoot 'repo'
    $launcherOut = Join-Path $testRoot 'launcher-out'
    $stageDir = Join-Path $testRoot 'stage'
    $libDir = Join-Path $stageDir 'Lib'
    New-Item -ItemType Directory -Force -Path $repoRoot, $launcherOut, $libDir | Out-Null

    $vanillaApiBytes = 'official-vanilla-api'
    $vanillaLibBytes = 'official-vanilla-lib'
    [System.IO.File]::WriteAllText((Join-Path $stageDir 'VintagestoryAPI.dll'), $vanillaApiBytes)
    [System.IO.File]::WriteAllText((Join-Path $stageDir 'VintagestoryLib.dll'), $vanillaLibBytes)
    [System.IO.File]::WriteAllText((Join-Path $libDir 'OpenTK.Audio.OpenAL.dll'), 'openal-lib')
    [System.IO.File]::WriteAllText((Join-Path $libDir 'cairo-sharp.dll'), 'cairo-lib')

    # Simulate polluted Optimum.Launcher output alongside genuine launcher dependencies.
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'VintagestoryAPI.dll'), 'compiled-fork-api')
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'VintagestoryLib.dll'), 'compiled-fork-lib')
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'Vintagestory.dll'), 'compiled-fork-exe')
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'cairo-sharp.dll'), 'stray-cairo')
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'CairoSharp.dll'), 'stray-cairo-alias')
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'OpenTK.Audio.OpenAL.dll'), 'stray-openal')
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'SkiaSharp.dll'), 'launcher-skia')
    [System.IO.File]::WriteAllText((Join-Path $launcherOut 'Mono.Cecil.dll'), 'launcher-cecil')

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $apiHash = ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($vanillaApiBytes)))).Replace('-', '').ToLowerInvariant()
        $libHash = ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($vanillaLibBytes)))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }

    $forksJson = @{
        vintageStoryVersion = '1.22.7'
        vintageStoryPins    = @{
            '1.22.7' = @{
                extractedFiles = @{
                    'VintagestoryAPI.dll'         = $apiHash
                    'VintagestoryLib.vanilla.dll' = $libHash
                }
            }
        }
    } | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'forks.json'), $forksJson)

    Invoke-Expression $copyMatch.Value

    Assert-True ([System.IO.File]::ReadAllText((Join-Path $stageDir 'VintagestoryAPI.dll')) -eq $vanillaApiBytes) 'VintagestoryAPI.dll was overwritten by launcher output'
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $stageDir 'VintagestoryLib.dll')) -eq $vanillaLibBytes) 'VintagestoryLib.dll was overwritten by launcher output'
    Assert-True (Test-Path (Join-Path $stageDir 'SkiaSharp.dll')) 'SkiaSharp.dll should be copied from launcher output'
    Assert-True (Test-Path (Join-Path $stageDir 'Mono.Cecil.dll')) 'Mono.Cecil.dll should be copied from launcher output'
    Assert-True (-not (Test-Path (Join-Path $stageDir 'cairo-sharp.dll'))) 'cairo-sharp.dll should not be copied to stage root'
    Assert-True (-not (Test-Path (Join-Path $stageDir 'CairoSharp.dll'))) 'CairoSharp.dll should not be copied to stage root'
    Assert-True (-not (Test-Path (Join-Path $stageDir 'OpenTK.Audio.OpenAL.dll'))) 'OpenTK.Audio.OpenAL.dll should not be copied to stage root'

    Test-StagedVanillaIntegrity -StageDir $stageDir -RepoRoot $repoRoot -GameVersion '1.22.7'

    [System.IO.File]::WriteAllText((Join-Path $stageDir 'VintagestoryAPI.dll'), 'compiled-fork-api')
    Assert-Throws { Test-StagedVanillaIntegrity -StageDir $stageDir -RepoRoot $repoRoot -GameVersion '1.22.7' } 'Corrupted VintagestoryAPI.dll should fail staged verification'
    [System.IO.File]::WriteAllText((Join-Path $stageDir 'VintagestoryAPI.dll'), $vanillaApiBytes)

    [System.IO.File]::WriteAllText((Join-Path $stageDir 'cairo-sharp.dll'), 'stray-cairo')
    Assert-Throws { Test-StagedVanillaIntegrity -StageDir $stageDir -RepoRoot $repoRoot -GameVersion '1.22.7' } 'Stray root cairo-sharp.dll should fail staged verification'

    Write-Host 'PASS: Windows packaging staging filter and vanilla integrity verification passed.'
} finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $testRoot
}
