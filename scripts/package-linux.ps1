<#
.SYNOPSIS
Builds a ready-to-run Optimum package for Linux (x64). Downloads the official
Vintage Story Linux client, overlays the optimized DLLs, renames the launcher
to Optimum, and packages it as tar.gz (default) or zip.
Requires a successful build first (dotnet build VintageStory.slnx -c Release).

Run this on Linux or WSL when possible. Repacking on Windows can drop the unix
executable bit on the Optimum launcher; extract-then-`chmod +x Optimum` fixes it.

.PARAMETER OutputDir
Where to write the package. Default: repo root.

.PARAMETER Format
Archive format: targz (default) or zip.

.PARAMETER Version
Vintage Story version. Default: 1.22.3.

.PARAMETER ClientArchive
Path to an existing linux client tar.gz. If omitted, downloads from the CDN.

.EXAMPLE
pwsh ./scripts/package-linux.ps1
pwsh ./scripts/package-linux.ps1 -Format zip -OutputDir /mnt/d/Downloads
#>

[CmdletBinding()]
param(
    [string]$OutputDir,
    [ValidateSet('targz', 'zip')]
    [string]$Format = 'targz',
    [string]$Version = '1.22.3',
    [string]$ClientArchive
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot/_hostcaps.ps1"
. "$PSScriptRoot/_exec.ps1"
Push-Location $repoRoot
try {
    Show-HostCaps -Only 'linux-x64' | Out-Null
    $buildOut = Join-Path $repoRoot 'build/Vintagestory/bin/Release/net10.0'
    $libOut   = Join-Path $repoRoot 'build/VintagestoryLib/bin/Release/net10.0'
    $modOut   = Join-Path $repoRoot 'bin/Release/net10.0'
    if (-not (Test-Path (Join-Path $libOut 'VintagestoryLib.dll'))) {
        throw "Build output not found. Run: dotnet build VintageStory.slnx -c Release"
    }

    # 1. Acquire the official Linux client archive.
    $zipCache = Join-Path $repoRoot '.vanilla/archives'
    New-Item -ItemType Directory -Force -Path $zipCache | Out-Null
    if (-not $ClientArchive) {
        $ClientArchive = Join-Path $zipCache "vs_client_linux-x64_$Version.tar.gz"
    }
    if (-not (Test-Path $ClientArchive)) {
        $url = "https://cdn.vintagestory.at/gamefiles/stable/vs_client_linux-x64_$Version.tar.gz"
        Write-Host "Downloading $url"
        Invoke-NativeStep { curl -L --fail -o $ClientArchive $url }
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    } else {
        Write-Host "Using cached $ClientArchive"
    }

    # 2. Extract the base install (vintagestory/) once.
    $baseRoot   = Join-Path $repoRoot '.vanilla/linux-x64'
    $vanillaDir = Join-Path $baseRoot 'vintagestory'
    $extractedFresh = $false
    if (-not (Test-Path $vanillaDir)) {
        New-Item -ItemType Directory -Force -Path $baseRoot | Out-Null
        Write-Host "Extracting to $baseRoot"
        Invoke-NativeStep { tar -xzf $ClientArchive -C $baseRoot }
        if ($LASTEXITCODE -ne 0) { throw "tar extraction failed on $ClientArchive" }
        $extractedFresh = $true
    }
    if (-not (Test-Path $vanillaDir)) { throw "Extraction failed: $vanillaDir not found" }
    if ($extractedFresh) {
        Copy-Item -Force (Join-Path $vanillaDir 'VintagestoryLib.dll') (Join-Path $vanillaDir 'VintagestoryLib.vanilla.dll')
    }
    $patchedLib = Join-Path $libOut 'VintagestoryLib-patched.dll'
    $vanillaLib = Join-Path $vanillaDir 'VintagestoryLib.vanilla.dll'
    if (-not (Test-Path $vanillaLib)) {
        throw "Pristine vanilla VintagestoryLib.vanilla.dll not found in $vanillaDir. Delete the matching .vanilla cache and re-run packaging."
    }
    Invoke-NativeStep { dotnet run --project (Join-Path $repoRoot 'Optimum.Patcher') -c Release -- $vanillaLib (Join-Path $libOut 'VintagestoryLib.dll') $patchedLib }
    if ($LASTEXITCODE -ne 0) { throw "Optimum.Patcher failed (exit code $LASTEXITCODE)." }

    # 3. Optimum release version.
    $optVer = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()

    if (-not $OutputDir) { $OutputDir = $repoRoot }
    $name     = "Optimum-v$optVer-linux-x64"
    $stageDir = Join-Path $OutputDir $name

    # 4. Fresh copy of the vanilla install.
    Write-Host "Staging $stageDir"
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    Copy-Item -Recurse -Force $vanillaDir $stageDir

    # 5. Overlay optimized DLLs (platform-agnostic IL).
    Copy-Item -Force (Join-Path $buildOut 'Vintagestory.dll')    $stageDir
    Copy-Item -Force $patchedLib (Join-Path $stageDir 'VintagestoryLib.dll')
    Copy-Item -Force (Join-Path $modOut 'VintagestoryAPI.dll') $stageDir
    Copy-Item -Force (Join-Path $modOut 'VSEssentials.dll') (Join-Path $stageDir 'Mods')
    Copy-Item -Force (Join-Path $modOut 'VSSurvivalMod.dll') (Join-Path $stageDir 'Mods')
    Copy-Item -Force (Join-Path $modOut 'VSCreativeMod.dll') (Join-Path $stageDir 'Mods')
    Copy-Item -Force (Join-Path $modOut 'cairo-sharp.dll') (Join-Path $stageDir 'Lib')

    # 5b. Overlay optimized shaders.
    $shaderSrc = Join-Path $repoRoot 'sources/shaders'
    $shaderDst = Join-Path $stageDir 'assets/game/shaders'
    if (Test-Path $shaderSrc) {
        Get-ChildItem $shaderSrc -File | ForEach-Object { Copy-Item -Force $_.FullName $shaderDst }
    }

    # Merge translation strings (text-based; vanilla JSON has case-duplicate keys that break ConvertFrom-Json).
    # Read/write explicitly as UTF-8 via .NET, not Get-Content/Set-Content:
    # on Windows PowerShell 5.1 those cmdlets default to the system codepage
    # for a BOM-less file, mangling every non-ASCII character (the degree
    # sign turned "°C" into "Â°C" in the shipped 0.2.2 build). This script
    # normally runs under pwsh (cross-platform), which defaults to UTF-8, but
    # the explicit encoding removes the PowerShell-version dependency.
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
            $dstText = $dstText.TrimEnd()
            if ($dstText.EndsWith('}')) {
                $dstText = $dstText.Substring(0, $dstText.Length - 1).TrimEnd()
                if (-not $dstText.EndsWith(',')) { $dstText += ',' }
                $dstText += "`r`n" + ($lines -join "`r`n") + "`r`n}"
            }
            [System.IO.File]::WriteAllText($dstFile, $dstText, $utf8NoBom)
        }
    }

    # Validate the staged assets: zero-byte or truncated files from a bad
    # vanilla extraction surface in-game as opaque GL crashes ("blur.vsh ...
    # unexpected $end at <EOF>"). Fail the package instead.
    $stageAssets = Join-Path $stageDir 'assets'
    $zeroByte = @(Get-ChildItem -Path $stageAssets -Recurse -File |
        Where-Object { $_.Length -eq 0 -and $_.Name -notlike 'version-*.txt' })
    if ($zeroByte.Count -gt 0) {
        $names = ($zeroByte | Select-Object -First 10 | ForEach-Object { $_.FullName }) -join "`n  "
        throw "Staged assets contain $($zeroByte.Count) zero-byte file(s); the vanilla extraction is corrupt. Delete '$vanillaDir' and re-run.`n  $names"
    }
    $badShaders = @(Get-ChildItem -Path $shaderDst -File |
        Where-Object { $_.Extension -in '.vsh', '.fsh', '.gsh' } |
        Where-Object { (Get-Content $_.FullName -Raw) -notmatch 'void\s+main' })
    if ($badShaders.Count -gt 0) {
        $names = ($badShaders | ForEach-Object { $_.Name }) -join ', '
        throw "Staged shader(s) truncated or corrupt (no 'void main'): $names. Delete '$vanillaDir' and re-run."
    }

    # 6. Rebrand: rename launcher, repoint run.sh, swap the icon, brand .desktop.
    $exe = Join-Path $stageDir 'Vintagestory'
    if (Test-Path $exe) {
        Rename-Item -Path $exe -NewName 'Optimum' -Force
        if (Get-Command chmod -ErrorAction SilentlyContinue) {
            chmod +x (Join-Path $stageDir 'Optimum')
        }
    } else {
        Write-Warning "Launcher 'Vintagestory' not found in archive."
    }
    # run.sh launches ./Vintagestory; point it at ./Optimum.
    $runsh = Join-Path $stageDir 'run.sh'
    if (Test-Path $runsh) {
        (Get-Content $runsh -Raw).Replace('./Vintagestory ', './Optimum ') |
            Set-Content -Path $runsh -NoNewline
    }
    # The .desktop entry and the window both read assets/gameicon.png.
    $gameicon = Join-Path $stageDir 'assets/gameicon.png'
    if (Test-Path $gameicon) { Copy-Item -Force (Join-Path $repoRoot 'logo.png') $gameicon }
    # Brand the .desktop launcher entry.
    $desktop = Join-Path $stageDir 'Vintagestory.desktop'
    if (Test-Path $desktop) {
        (Get-Content $desktop -Raw) -replace 'Name(\[[a-z]+\])?=Vintage Story [0-9.]+', 'Name$1=Optimum' |
            Set-Content -Path (Join-Path $stageDir 'Optimum.desktop') -NoNewline
        Remove-Item -Force $desktop
    }

    Write-Host "Folder ready: $stageDir" -ForegroundColor Green

    # 7. Package.
    if ($Format -eq 'zip') {
        $out = Join-Path $OutputDir "$name.zip"
        if (Test-Path $out) { Remove-Item -Force $out }
        Compress-Archive -Path $stageDir -DestinationPath $out -CompressionLevel Optimal
    } else {
        $out = Join-Path $OutputDir "$name.tar.gz"
        if (Test-Path $out) { Remove-Item -Force $out }
        Invoke-NativeStep { tar -czf $out -C $OutputDir $name }
        if ($LASTEXITCODE -ne 0) { throw "tar packaging failed for $out" }
    }
    $size = [math]::Round((Get-Item $out).Length / 1MB)
    Write-Host "Done: $out (${size}MB)" -ForegroundColor Green
} finally {
    Pop-Location
}
