<#
.SYNOPSIS
Assembles a ready-to-run Optimum folder (full game plus optimized DLLs).
Requires a successful build first (dotnet build VintageStory.slnx -c Release).

.PARAMETER OutputDir
Where to create the output folder (and zip). Default: repo root.

.PARAMETER Zip
Also compress the folder into Optimum-v<version>-win-x64.zip.

.PARAMETER VanillaDir
Path to an existing Vintage Story Windows installation.

.EXAMPLE
.\scripts\package.ps1 -VanillaDir C:\Games\VintageStory
.\scripts\package.ps1 -VanillaDir C:\Games\VintageStory -Zip
.\scripts\package.ps1 -VanillaDir C:\Games\VintageStory -OutputDir D:\releases -Zip
#>

[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$Zip,
    [string]$Version,
    [string]$VanillaDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Resolve VS version from forks.json if not passed explicitly.
if (-not $Version) {
    $forksFile = Join-Path $repoRoot 'forks.json'
    if (Test-Path $forksFile) { $Version = (Get-Content $forksFile -Raw | ConvertFrom-Json).vintageStoryVersion }
    else { $Version = '1.22.7' }
}
. "$PSScriptRoot/_hostcaps.ps1"
. "$PSScriptRoot/_exec.ps1"

# Resolve a local Windows vanilla install. This script does not download
# Vintage Story.
function Resolve-WindowsVanilla {
    param([string]$RepoRoot)
    $winDir = Join-Path (Join-Path $RepoRoot '.vanilla/win-x64') 'vintagestory'
    if (Test-Path (Join-Path $winDir 'Vintagestory.exe')) { return $winDir }

    $legacyWin = Join-Path (Join-Path $RepoRoot '.vanilla') 'vintagestory'
    if (($IsWindows -or ($env:OS -eq 'Windows_NT')) -and (Test-Path (Join-Path $legacyWin 'Vintagestory.exe'))) { return $legacyWin }

    throw 'Vintage Story installation not found. Pass -VanillaDir with the folder that contains Vintagestory.exe.'
}

Push-Location $repoRoot
try {
    Show-HostCaps -Only 'win-x64' | Out-Null
    $vanillaDir = if ($VanillaDir) { [IO.Path]::GetFullPath($VanillaDir) } else { Resolve-WindowsVanilla -RepoRoot $repoRoot }
    if (-not (Test-Path (Join-Path $vanillaDir 'Vintagestory.exe'))) {
        throw "Vanilla Windows install not found: $vanillaDir"
    }
    $libOut = Join-Path $repoRoot 'build/VintagestoryLib/bin/Release/net10.0'
    $launcherOut = Join-Path $repoRoot 'Optimum.Launcher/bin/Release/net10.0'
    $patcherOut = Join-Path $repoRoot 'Optimum.Patcher/bin/Release/net10.0'

    if (-not (Test-Path (Join-Path $libOut 'VintagestoryLib.dll'))) {
        throw "Build output not found. Run: dotnet build VintageStory.slnx -c Release"
    }
    # Use the compiled engine as the donor for the launcher's runtime transplant.
    # The staged game keeps its local vanilla engine intact.
    $compiledLib = Join-Path $libOut 'VintagestoryLib.dll'

    $launcherExe = Join-Path $launcherOut 'Optimum.exe'
    if (-not (Test-Path $launcherExe)) {
        $launcherOut = Join-Path $launcherOut 'win-x64'
        $launcherExe = Join-Path $launcherOut 'Optimum.exe'
        if (-not (Test-Path $launcherExe)) {
            Write-Host 'Building the win-x64 Optimum launcher...'
            $launcherProject = Join-Path $repoRoot 'Optimum.Launcher/Optimum.Launcher.csproj'
            Invoke-NativeStep { dotnet build $launcherProject -c Release -r win-x64 --self-contained false -p:UseAppHost=true --nologo }
            if ($LASTEXITCODE -ne 0) { throw 'Could not build the win-x64 Optimum launcher.' }
        }
    }
    foreach ($requiredLauncherFile in @('Optimum.exe', 'Optimum.dll', 'Optimum.deps.json', 'Optimum.runtimeconfig.json')) {
        if (-not (Test-Path (Join-Path $launcherOut $requiredLauncherFile))) {
            throw "Launcher output not found: $requiredLauncherFile"
        }
    }
    foreach ($requiredPatcherFile in @('Optimum.Patcher.dll', 'Optimum.Patcher.deps.json', 'Optimum.Patcher.runtimeconfig.json', 'Mono.Cecil.dll')) {
        if (-not (Test-Path (Join-Path $patcherOut $requiredPatcherFile))) {
            throw "Patcher output not found: $requiredPatcherFile"
        }
    }

    Write-Host 'Preparing runtime donors...'
    $runtimeDonorDir = Join-Path $repoRoot '.vanilla/win-x64/runtime-donors'
    & (Join-Path $PSScriptRoot 'prepare-runtime-donors.ps1') -VanillaDir $vanillaDir -RuntimeDonorDir $runtimeDonorDir -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Runtime donor preparation failed.' }
    $runtimeDonorRoot = Join-Path $repoRoot '.build/runtime-donors'
    $essentialsDonor = Get-ChildItem -Path (Join-Path $runtimeDonorRoot 'VSEssentials') -Recurse -Filter 'VSEssentials.dll' -File |
        Where-Object { $_.FullName -notmatch '[/\\]obj[/\\]' } | Select-Object -First 1 -ExpandProperty FullName
    $survivalDonor = Get-ChildItem -Path (Join-Path $runtimeDonorRoot 'VSSurvivalMod') -Recurse -Filter 'VSSurvivalMod.dll' -File |
        Where-Object { $_.FullName -notmatch '[/\\]obj[/\\]' } | Select-Object -First 1 -ExpandProperty FullName
    if (-not $essentialsDonor -or -not $survivalDonor) {
        throw 'Runtime mod donor output not found.'
    }

    # Read the Optimum release version. The -Version parameter selects the Vintage Story release.
    $optVer = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()

    if (-not $OutputDir) { $OutputDir = $repoRoot }
    $name = "Optimum-v$optVer-win-x64"
    $stageDir = Join-Path $OutputDir $name

    # Fresh copy of the vanilla install. Leaves .vanilla untouched.
    Write-Host "Copying vanilla install to $stageDir..."
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    Copy-Item -Recurse -Force $vanillaDir $stageDir
    Copy-Item -Force (Join-Path $repoRoot 'scripts/uninstall.ps1') (Join-Path $stageDir 'uninstall.ps1')

    # Keep the engine and built-in mods vanilla. The launcher patches copies at startup.
    Write-Host 'Installing launcher, patcher, and runtime donors...'
    $apiOut = Join-Path $repoRoot (Join-Path 'bin' (Join-Path 'Release' 'net10.0'))
    Copy-Item -Force (Join-Path $apiOut 'Optimum.Api.Contracts.dll') $stageDir
    Copy-Item -Force (Join-Path $apiOut 'Optimum.GameContent.dll') $stageDir

    foreach ($launcherFile in @('Optimum.exe', 'Optimum.dll', 'Optimum.deps.json', 'Optimum.runtimeconfig.json')) {
        Copy-Item -Force (Join-Path $launcherOut $launcherFile) $stageDir
    }
    # Native assets for the launcher's patch-progress splash screen (OpenTK's
    # GLFW, SkiaSharp's text renderer). .NET's default native resolver looks
    # for these under runtimes/<rid>/native/ relative to the app base
    # directory, so the folder structure must be preserved, not flattened.
    $launcherRuntimes = Join-Path $launcherOut 'runtimes'
    if (Test-Path $launcherRuntimes) {
        Copy-Item -Recurse -Force $launcherRuntimes $stageDir
    }
    foreach ($patcherFile in @('Optimum.Patcher.dll', 'Optimum.Patcher.deps.json', 'Optimum.Patcher.runtimeconfig.json')) {
        Copy-Item -Force (Join-Path $patcherOut $patcherFile) $stageDir
    }
    Get-ChildItem -Path $patcherOut -Filter 'Mono.Cecil*.dll' -File |
        ForEach-Object { Copy-Item -Force $_.FullName $stageDir }

    $optimumDir = Join-Path $stageDir '.optimum'
    $donorDir = Join-Path $optimumDir 'donors'
    $vanillaModDir = Join-Path $optimumDir 'vanilla/Mods'
    New-Item -ItemType Directory -Force -Path $donorDir, $vanillaModDir | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $optimumDir 'standalone-install'),
        'Optimum standalone package',
        (New-Object System.Text.UTF8Encoding($false)))

    $donorFiles = @(
        @($compiledLib, 'VintagestoryLib.Donor.dll'),
        @((Join-Path $apiOut 'Optimum.Api.Contracts.dll'), 'VintagestoryAPI.Contracts.dll'),
        @($essentialsDonor, 'VSEssentials.Donor.dll'),
        @($survivalDonor, 'VSSurvivalMod.Donor.dll')
    )
    foreach ($donor in $donorFiles) {
        Copy-Item -Force $donor[0] (Join-Path $donorDir $donor[1])
        $sourcePdb = [IO.Path]::ChangeExtension($donor[0], '.pdb')
        if (Test-Path $sourcePdb) {
            Copy-Item -Force $sourcePdb (Join-Path $donorDir ([IO.Path]::ChangeExtension($donor[1], '.pdb')))
        }
    }
    foreach ($modName in @('VSEssentials', 'VSSurvivalMod')) {
        Copy-Item -Force (Join-Path $stageDir "Mods/$modName.dll") $vanillaModDir
        $modPdb = Join-Path $stageDir "Mods/$modName.pdb"
        if (Test-Path $modPdb) { Copy-Item -Force $modPdb $vanillaModDir }
    }

    # Apply optimized shaders.
    $shaderSrc = Join-Path $repoRoot 'sources/shaders'
    $shaderDst = Join-Path $stageDir 'assets/game/shaders'
    if (Test-Path $shaderSrc) {
        Get-ChildItem $shaderSrc -File | ForEach-Object { Copy-Item -Force $_.FullName $shaderDst }
    }

    # Merge translation strings (text-based; vanilla JSON has case-duplicate keys that break ConvertFrom-Json).
    # Read/write explicitly as UTF-8 via .NET, not Get-Content/Set-Content:
    # Windows PowerShell 5.1 (what the Windows installer launches) defaults
    # those cmdlets to the system codepage when a file has no BOM, silently
    # mangling every non-ASCII character the vanilla lang files contain -
    # the degree sign turned "°C" into "Â°C" in the shipped
    # 0.2.2 build. File.ReadAllText/WriteAllText with an explicit encoding
    # is not PowerShell-version-dependent.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $langSrc = Join-Path $repoRoot 'sources/lang'
    $langDst = Join-Path $stageDir 'assets/game/lang'
    if (Test-Path $langSrc) {
        foreach ($srcFile in (Get-ChildItem $langSrc -Filter '*.json')) {
            $dstFile = Join-Path $langDst $srcFile.Name
            if (-not (Test-Path $dstFile)) { continue }
            $lines = [System.IO.File]::ReadAllLines($srcFile.FullName, [System.Text.Encoding]::UTF8) |
                Where-Object { $_ -match '^\s*"optimum-' }
            if ($lines.Count -eq 0) { continue }
            $dstText = [System.IO.File]::ReadAllText($dstFile, [System.Text.Encoding]::UTF8)
            $insertion = ($lines -join "`r`n")
            $dstText = $dstText.TrimEnd()
            if ($dstText.EndsWith('}')) {
                $dstText = $dstText.Substring(0, $dstText.Length - 1).TrimEnd()
                if (-not $dstText.EndsWith(',')) { $dstText += ',' }
                $dstText += "`r`n" + $insertion + "`r`n}"
            }
            [System.IO.File]::WriteAllText($dstFile, $dstText, $utf8NoBom)
        }
    }

    # Validate the staged assets before shipping them. A tolerated-partial
    # innounp extraction or a poisoned .vanilla cache carries zero-byte or
    # truncated files into the stage, and a truncated shader then kills the
    # game at startup with an opaque GL error (the 0.2.1 "blur.vsh ...
    # unexpected $end at <EOF>" reports). Fail the package with a clear
    # message instead.
    $stageAssets = Join-Path $stageDir 'assets'
    $zeroByte = @(Get-ChildItem -Path $stageAssets -Recurse -File |
        Where-Object { $_.Length -eq 0 -and $_.Name -notlike 'version-*.txt' })
    if ($zeroByte.Count -gt 0) {
        $names = ($zeroByte | Select-Object -First 10 | ForEach-Object { $_.FullName }) -join "`n  "
        throw "Staged assets contain $($zeroByte.Count) zero-byte file(s); the vanilla extraction is corrupt. Delete '$vanillaDir' and re-run to re-extract.`n  $names"
    }
    $badShaders = @(Get-ChildItem -Path (Join-Path $stageAssets 'game/shaders') -File |
        Where-Object { $_.Extension -in '.vsh', '.fsh', '.gsh' } |
        Where-Object { (Get-Content $_.FullName -Raw) -notmatch 'void\s+main' })
    if ($badShaders.Count -gt 0) {
        $names = ($badShaders | ForEach-Object { $_.Name }) -join ', '
        throw "Staged shader(s) truncated or corrupt (no 'void main'): $names. Delete '$vanillaDir' and re-run to re-extract."
    }

    # Remove installer artifacts.
    Get-ChildItem -Path $stageDir -Filter 'unins000.*' | Remove-Item -Force

    foreach ($requiredStageFile in @(
        'Optimum.exe',
        'Optimum.dll',
        'Optimum.Patcher.dll',
        'uninstall.ps1',
        'Vintagestory.exe',
        '.optimum/donors/VintagestoryLib.Donor.dll',
        '.optimum/donors/VintagestoryAPI.Contracts.dll',
        '.optimum/donors/VSEssentials.Donor.dll',
        '.optimum/donors/VSSurvivalMod.Donor.dll',
        '.optimum/vanilla/Mods/VSEssentials.dll',
        '.optimum/vanilla/Mods/VSSurvivalMod.dll',
        '.optimum/standalone-install'
    )) {
        if (-not (Test-Path (Join-Path $stageDir $requiredStageFile))) {
            throw "Required package file not found: $requiredStageFile"
        }
    }

    $completionMarker = Join-Path $stageDir '.optimum/package-complete'
    [IO.File]::WriteAllText(
        $completionMarker,
        "Package validation completed for Optimum $optVer",
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "Folder ready: $stageDir" -ForegroundColor Green

    if ($Zip) {
        $zipPath = Join-Path $OutputDir "$name.zip"
        if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
        Write-Host "Packaging $name.zip..."
        Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
        $size = [math]::Round((Get-Item $zipPath).Length / 1MB)
        Write-Host "Done: $zipPath (${size}MB)" -ForegroundColor Green
    }
} finally {
    Pop-Location
}
