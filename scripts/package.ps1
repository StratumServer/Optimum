<#
.SYNOPSIS
Assembles a ready-to-run Optimum folder (full game plus optimized DLLs).
Requires a successful build first (dotnet build VintageStory.slnx -c Release).

.PARAMETER OutputDir
Where to create the output folder (and zip). Default: repo root.

.PARAMETER Zip
Also compress the folder into Optimum-v<version>-win-x64.zip.

.EXAMPLE
.\scripts\package.ps1                          # folder only
.\scripts\package.ps1 -Zip                     # folder + zip
.\scripts\package.ps1 -OutputDir D:\releases -Zip
#>

[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$Zip,
    [string]$Version = '1.22.3'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot/_hostcaps.ps1"

# Resolve a Windows vanilla install. Bootstrap extracts the Windows client into
# .vanilla/win-x64/. Off-Windows, this keeps native libs from another platform
# out of the win-x64 package.
function Resolve-WindowsVanilla {
    param([string]$RepoRoot, [string]$Version)
    $winDir = Join-Path (Join-Path $RepoRoot '.vanilla/win-x64') 'vintagestory'
    if (Test-Path (Join-Path $winDir 'Vintagestory.exe')) { return $winDir }

    $legacyWin = Join-Path (Join-Path $RepoRoot '.vanilla') 'vintagestory'
    if (($IsWindows -or ($env:OS -eq 'Windows_NT')) -and (Test-Path (Join-Path $legacyWin 'Vintagestory.exe'))) { return $legacyWin }

    if (-not (Test-Cmd innoextract)) {
        throw "Cannot build a win-x64 package on this host: innoextract not found (needed to unpack vs_install_win-x64_$Version.exe). Install it (apt-get install innoextract) or run package.ps1 on Windows."
    }
    $zipCache = Join-Path $RepoRoot '.vanilla/archives'
    New-Item -ItemType Directory -Force -Path $zipCache | Out-Null
    $exeName = "vs_install_win-x64_$Version.exe"
    $installer = Join-Path $zipCache $exeName
    if (-not (Test-Path $installer)) {
        $url = "https://cdn.vintagestory.at/gamefiles/stable/$exeName"
        Write-Host "Downloading $url (~570MB)"
        curl -L --fail -o $installer $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    } else { Write-Host "Using cached $installer" }

    $parent = Split-Path -Parent $winDir
    if (Test-Path $parent) { Remove-Item -Recurse -Force $parent }
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Write-Host "Extracting Windows client with innoextract..."
    innoextract -s -d $parent $installer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "innoextract failed on $installer" }
    # innoextract writes the install tree under app/.
    $appDir = Join-Path $parent 'app'
    if (Test-Path $appDir) { Rename-Item -Path $appDir -NewName 'vintagestory' -Force }
    if (-not (Test-Path (Join-Path $winDir 'Vintagestory.exe'))) {
        throw "Extraction failed: Vintagestory.exe not found under $winDir"
    }
    return $winDir
}

Push-Location $repoRoot
try {
    Show-HostCaps -Only 'win-x64' | Out-Null
    $vanillaDir = Resolve-WindowsVanilla -RepoRoot $repoRoot -Version $Version
    $buildOut = Join-Path $repoRoot 'build/Vintagestory/bin/Release/net10.0'
    $libOut = Join-Path $repoRoot 'build/VintagestoryLib/bin/Release/net10.0'

    if (-not (Test-Path (Join-Path $libOut 'VintagestoryLib.dll'))) {
        throw "Build output not found. Run: dotnet build VintageStory.slnx -c Release"
    }
    $patchedLib = Join-Path $libOut 'VintagestoryLib-patched.dll'
    dotnet run --project (Join-Path $repoRoot 'Optimum.Patcher') -c Release -- (Join-Path $vanillaDir 'VintagestoryLib.dll') (Join-Path $libOut 'VintagestoryLib.dll') $patchedLib
    if ($LASTEXITCODE -ne 0) { throw "Optimum.Patcher failed." }

    # Read Optimum version from OptimumInfo.cs (distinct from the VS -Version).
    $infoFile = Join-Path $repoRoot 'build/VintagestoryLib/Optimum/OptimumInfo.cs'
    $optVer = '0.2.2'
    if (Test-Path $infoFile) {
        $match = [regex]::Match((Get-Content $infoFile -Raw), 'Version\s*=\s*"([^"]+)"')
        if ($match.Success) { $optVer = $match.Groups[1].Value }
    }

    if (-not $OutputDir) { $OutputDir = $repoRoot }
    $name = "Optimum-v$optVer-win-x64"
    $stageDir = Join-Path $OutputDir $name

    # Fresh copy of the vanilla install. Leaves .vanilla untouched.
    Write-Host "Copying vanilla install to $stageDir..."
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    Copy-Item -Recurse -Force $vanillaDir $stageDir

    # Apply optimized DLLs over the copy.
    Write-Host "Applying optimized DLLs..."
    Copy-Item -Force (Join-Path $buildOut 'Vintagestory.dll') $stageDir
    Copy-Item -Force $patchedLib (Join-Path $stageDir 'VintagestoryLib.dll')
    $apiOut = Join-Path $repoRoot (Join-Path 'bin' (Join-Path 'Release' 'net10.0'))
    Copy-Item -Force (Join-Path $apiOut 'VintagestoryAPI.dll') $stageDir
    Copy-Item -Force (Join-Path $apiOut 'VSEssentials.dll') (Join-Path $stageDir 'Mods')
    Copy-Item -Force (Join-Path $apiOut 'VSSurvivalMod.dll') (Join-Path $stageDir 'Mods')
    Copy-Item -Force (Join-Path $apiOut 'VSCreativeMod.dll') (Join-Path $stageDir 'Mods')
    Copy-Item -Force (Join-Path $apiOut 'cairo-sharp.dll') (Join-Path $stageDir 'Lib')

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

    # Use the built apphost. It embeds the Optimum app.ico, unlike the vanilla
    # launcher. On a non-Windows host `dotnet build` produces no Vintagestory.exe,
    # so cross-build the win-x64 apphost on demand. Fall back to the vanilla
    # launcher (vanilla icon) only if even that is unavailable.
    $builtExe = Join-Path $buildOut 'Vintagestory.exe'
    if (-not (Test-Path $builtExe)) {
        $ridExe = Join-Path (Join-Path $buildOut 'win-x64') 'Vintagestory.exe'
        if (-not (Test-Path $ridExe)) {
            Write-Host "Cross-building win-x64 launcher (Optimum.exe apphost)..."
            $proj = Join-Path $repoRoot 'build/Vintagestory/Vintagestory.csproj'
            dotnet build $proj -c Release -r win-x64 --self-contained false -p:UseAppHost=true --nologo
            if ($LASTEXITCODE -ne 0) { Write-Warning "Cross-build failed; will keep vanilla launcher." }
        }
        if (Test-Path $ridExe) { $builtExe = $ridExe }
    }
    if (Test-Path $builtExe) {
        Copy-Item -Force $builtExe $stageDir
    } else {
        Write-Warning "Vintagestory.exe not found and cross-build unavailable; keeping vanilla launcher (vanilla icon)."
    }

    # Remove installer artifacts.
    Get-ChildItem -Path $stageDir -Filter 'unins000.*' | Remove-Item -Force

    # Brand the launcher: Vintagestory.exe -> Optimum.exe. The apphost still
    # loads Vintagestory.dll by name, so the dll keeps its name.
    $exe = Join-Path $stageDir 'Vintagestory.exe'
    if (Test-Path $exe) {
        Rename-Item -Path $exe -NewName 'Optimum.exe' -Force
        Write-Host "Renamed launcher to Optimum.exe"
    } else {
        Write-Warning "Vintagestory.exe not found; launcher not renamed."
    }

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
