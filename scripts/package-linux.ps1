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
Push-Location $repoRoot
try {
    Show-HostCaps -Only 'linux-x64' | Out-Null
    $buildOut = Join-Path $repoRoot 'baseline/Vintagestory/bin/Release/net10.0'
    $libOut   = Join-Path $repoRoot 'baseline/VintagestoryLib/bin/Release/net10.0'
    if (-not (Test-Path (Join-Path $libOut 'VintagestoryLib.dll'))) {
        throw "Build output not found. Run: dotnet build VintageStory.slnx -c Release"
    }

    # 1. Acquire the official Linux client archive.
    $zipCache = Join-Path $repoRoot '.vanilla-zips'
    New-Item -ItemType Directory -Force -Path $zipCache | Out-Null
    if (-not $ClientArchive) {
        $ClientArchive = Join-Path $zipCache "vs_client_linux-x64_$Version.tar.gz"
    }
    if (-not (Test-Path $ClientArchive)) {
        $url = "https://cdn.vintagestory.at/gamefiles/stable/vs_client_linux-x64_$Version.tar.gz"
        Write-Host "Downloading $url"
        curl -L --fail -o $ClientArchive $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    } else {
        Write-Host "Using cached $ClientArchive"
    }

    # 2. Extract the base install (vintagestory/) once.
    $baseRoot   = Join-Path $repoRoot '.vanilla-linux'
    $vanillaDir = Join-Path $baseRoot 'vintagestory'
    if (-not (Test-Path $vanillaDir)) {
        New-Item -ItemType Directory -Force -Path $baseRoot | Out-Null
        Write-Host "Extracting to $baseRoot"
        tar -xzf $ClientArchive -C $baseRoot
    }
    if (-not (Test-Path $vanillaDir)) { throw "Extraction failed: $vanillaDir not found" }

    # 3. Version from OptimumInfo.cs.
    $infoFile = Join-Path $repoRoot 'baseline/VintagestoryLib/Optimum/OptimumInfo.cs'
    $optVer = '0.1.1'
    if (Test-Path $infoFile) {
        $m = [regex]::Match((Get-Content $infoFile -Raw), 'Version\s*=\s*"([^"]+)"')
        if ($m.Success) { $optVer = $m.Groups[1].Value }
    }

    if (-not $OutputDir) { $OutputDir = $repoRoot }
    $name     = "Optimum-v$optVer-linux-x64"
    $stageDir = Join-Path $OutputDir $name

    # 4. Fresh copy of the vanilla install.
    Write-Host "Staging $stageDir"
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    Copy-Item -Recurse -Force $vanillaDir $stageDir

    # 5. Overlay optimized DLLs (platform-agnostic IL).
    Copy-Item -Force (Join-Path $buildOut 'Vintagestory.dll')    $stageDir
    Copy-Item -Force (Join-Path $libOut   'VintagestoryLib.dll') $stageDir
    Copy-Item -Force (Join-Path $buildOut 'VintagestoryAPI.dll') $stageDir

    # 5b. Overlay optimized shaders.
    $shaderSrc = Join-Path $repoRoot 'sources/shaders'
    $shaderDst = Join-Path $stageDir 'assets/game/shaders'
    if (Test-Path $shaderSrc) {
        Get-ChildItem $shaderSrc -File | ForEach-Object { Copy-Item -Force $_.FullName $shaderDst }
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
        tar -czf $out -C $OutputDir $name
    }
    $size = [math]::Round((Get-Item $out).Length / 1MB)
    Write-Host "Done: $out (${size}MB)" -ForegroundColor Green
} finally {
    Pop-Location
}
