<#
.SYNOPSIS
Builds a ready-to-run Optimum.app for macOS and packages it as a .dmg.
Downloads the official Vintage Story macOS client for the chosen architecture,
overlays the optimized DLLs, rebrands the bundle (name, launcher, icon), and
builds a drag-to-Applications disk image.
Requires a successful build first (dotnet build VintageStory.slnx -c Release).

The .dmg comes from hdiutil on macOS. On Linux (and Windows through WSL) the
script builds an unsigned .dmg with libdmg-hfsplus, compiled once into .tools/
(needs genisoimage or mkisofs, plus cmake and git). That image is best-effort:
Gatekeeper warns and it may not mount on every macOS version. With none of those
tools it assembles Optimum.app and writes a .tar.gz fallback.

.PARAMETER OutputDir
Where to write the output. Default: repo root.

.PARAMETER Arch
macOS architecture: arm64 (Apple Silicon, default) or x64 (Intel).

.PARAMETER Version
Vintage Story version. Default: 1.22.3.

.PARAMETER ClientArchive
Path to an existing macOS client tar.gz. If omitted, downloads from the CDN.

.EXAMPLE
pwsh ./scripts/package-macos.ps1 -Arch arm64
pwsh ./scripts/package-macos.ps1 -Arch x64 -OutputDir ~/Downloads
#>

[CmdletBinding()]
param(
    [string]$OutputDir,
    [ValidateSet('arm64', 'x64')]
    [string]$Arch = 'arm64',
    [string]$Version = '1.22.3',
    [string]$ClientArchive
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot/_hostcaps.ps1"
Push-Location $repoRoot
try {
    Show-HostCaps -Only "osx-$Arch" | Out-Null
    $buildOut = Join-Path $repoRoot 'baseline/Vintagestory/bin/Release/net10.0'
    $libOut   = Join-Path $repoRoot 'baseline/VintagestoryLib/bin/Release/net10.0'
    if (-not (Test-Path (Join-Path $libOut 'VintagestoryLib.dll'))) {
        throw "Build output not found. Run: dotnet build VintageStory.slnx -c Release"
    }
    $icns = Join-Path $repoRoot 'logo.icns'
    if (-not (Test-Path $icns)) { throw "logo.icns not found at repo root." }

    # 1. Acquire the official macOS client archive.
    $zipCache = Join-Path $repoRoot '.vanilla-zips'
    New-Item -ItemType Directory -Force -Path $zipCache | Out-Null
    if (-not $ClientArchive) {
        $ClientArchive = Join-Path $zipCache "vs_client_osx-$Arch`_$Version.tar.gz"
    }
    if (-not (Test-Path $ClientArchive)) {
        $url = "https://cdn.vintagestory.at/gamefiles/stable/vs_client_osx-$Arch`_$Version.tar.gz"
        Write-Host "Downloading $url"
        curl -L --fail -o $ClientArchive $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    } else {
        Write-Host "Using cached $ClientArchive"
    }

    # 2. Extract the base bundle (Vintage Story.app) once.
    $baseRoot = Join-Path $repoRoot ".vanilla-mac-$Arch"
    $baseApp  = Join-Path $baseRoot 'Vintage Story.app'
    if (-not (Test-Path $baseApp)) {
        New-Item -ItemType Directory -Force -Path $baseRoot | Out-Null
        Write-Host "Extracting to $baseRoot"
        tar -xzf $ClientArchive -C $baseRoot
    }
    if (-not (Test-Path $baseApp)) { throw "Extraction failed: 'Vintage Story.app' not found" }

    # 3. Version from OptimumInfo.cs.
    $infoFile = Join-Path $repoRoot 'baseline/VintagestoryLib/Optimum/OptimumInfo.cs'
    $optVer = '0.1.1'
    if (Test-Path $infoFile) {
        $m = [regex]::Match((Get-Content $infoFile -Raw), 'Version\s*=\s*"([^"]+)"')
        if ($m.Success) { $optVer = $m.Groups[1].Value }
    }

    if (-not $OutputDir) { $OutputDir = $repoRoot }
    $appDir = Join-Path $OutputDir 'Optimum.app'

    # 4. Fresh copy of the vanilla bundle.
    Write-Host "Assembling $appDir"
    if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
    Copy-Item -Recurse -Force $baseApp $appDir

    # 5. Overlay optimized DLLs (platform-agnostic IL).
    Copy-Item -Force (Join-Path $buildOut 'Vintagestory.dll')    $appDir
    Copy-Item -Force (Join-Path $libOut   'VintagestoryLib.dll') $appDir
    Copy-Item -Force (Join-Path $buildOut 'VintagestoryAPI.dll') $appDir

    # 5b. Overlay optimized shaders.
    $shaderSrc = Join-Path $repoRoot 'sources/shaders'
    $shaderDst = Join-Path $appDir 'assets/game/shaders'
    if (Test-Path $shaderSrc) {
        Get-ChildItem $shaderSrc -File | ForEach-Object { Copy-Item -Force $_.FullName $shaderDst }
    }

    # 6. Rebrand: rename launcher, swap icon, rewrite Info.plist.
    $exe = Join-Path $appDir 'Vintagestory'
    if (Test-Path $exe) {
        Rename-Item -Path $exe -NewName 'Optimum' -Force
        if (Get-Command chmod -ErrorAction SilentlyContinue) {
            chmod +x (Join-Path $appDir 'Optimum')
        }
    } else {
        Write-Warning "Launcher 'Vintagestory' not found in bundle."
    }
    Copy-Item -Force $icns (Join-Path $appDir 'Icon.icns')

    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>ATSApplicationFontsPath</key><string>assets/game/fonts</string>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
    <key>CFBundleDisplayName</key><string>Optimum</string>
    <key>CFBundleExecutable</key><string>Optimum</string>
    <key>CFBundleIconFile</key><string>Icon.icns</string>
    <key>CFBundleIdentifier</key><string>at.vintagestory.optimum</string>
    <key>CFBundleName</key><string>Optimum</string>
    <key>CFBundleShortVersionString</key><string>$Version</string>
    <key>CFBundleVersion</key><string>$Version</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
    <key>LSMinimumSystemVersion</key><string>12.2</string>
    <key>LSSupportsGameMode</key><true/>
    <key>NSHighResolutionCapable</key><false/>
    <key>NSHumanReadableCopyright</key><string>Optimum is a fork of Vintage Story (c) Anego Studios</string>
</dict>
</plist>
"@
    Set-Content -Path (Join-Path $appDir 'Info.plist') -Value $plist -Encoding UTF8 -NoNewline
    Write-Host "Bundle ready: $appDir" -ForegroundColor Green

    # 7. Build the .dmg. macOS uses hdiutil (reliable, notarizable). Linux, and
    # Windows through WSL, use libdmg-hfsplus, built once into .tools/. That .dmg
    # is unsigned and best-effort; run on macOS for a notarizable one.
    $dmg = Join-Path $OutputDir "Optimum-v$optVer-mac-$Arch.dmg"
    if (Test-Path $dmg) { Remove-Item -Force $dmg }

    if ($IsMacOS -and (Get-Command hdiutil -ErrorAction SilentlyContinue)) {
        $dmgStage = Join-Path $OutputDir "_dmg-$Arch"
        if (Test-Path $dmgStage) { Remove-Item -Recurse -Force $dmgStage }
        New-Item -ItemType Directory -Force -Path $dmgStage | Out-Null
        Copy-Item -Recurse -Force $appDir (Join-Path $dmgStage 'Optimum.app')
        ln -s /Applications (Join-Path $dmgStage 'Applications')
        hdiutil create -volname 'Optimum' -srcfolder $dmgStage -ov -format UDZO $dmg
        Remove-Item -Recurse -Force $dmgStage
        Write-Host "Done: $dmg" -ForegroundColor Green
    } else {
        # HFS hybrid builder (cdrtools mkisofs, or genisoimage) plus the
        # libdmg-hfsplus converter. mkisofs -hfs is the reliable path.
        $mkiso = Get-Command mkisofs -ErrorAction SilentlyContinue
        if (-not $mkiso) { $mkiso = Get-Command genisoimage -ErrorAction SilentlyContinue }
        $dmgTool = Join-Path $repoRoot '.tools/libdmg-hfsplus/dmg/dmg'
        if ($mkiso -and -not (Test-Path $dmgTool) -and
            (Get-Command cmake -ErrorAction SilentlyContinue) -and
            (Get-Command git -ErrorAction SilentlyContinue)) {
            Write-Host "Building libdmg-hfsplus (one time)..."
            $src = Join-Path $repoRoot '.tools/libdmg-hfsplus'
            if (-not (Test-Path $src)) {
                git clone --depth 1 https://github.com/fanquake/libdmg-hfsplus.git $src
            }
            Push-Location $src
            try {
                cmake -DCMAKE_POLICY_VERSION_MINIMUM=3.5 . | Out-Null
                make | Out-Null
            } finally { Pop-Location }
        }
        if ($mkiso -and (Test-Path $dmgTool)) {
            $iso = Join-Path $OutputDir "Optimum-$Arch.iso"
            if (Test-Path $iso) { Remove-Item -Force $iso }
            & $mkiso.Source -hfs -V 'Optimum' -D -no-pad -r -file-mode 0755 -o $iso $appDir
            & $dmgTool $iso $dmg
            Remove-Item -Force $iso
            Write-Warning "Built an UNSIGNED .dmg with libdmg-hfsplus. Gatekeeper warns (right-click > Open), and it may not mount on every macOS version. Run on macOS for a notarizable .dmg."
            Write-Host "Done: $dmg" -ForegroundColor Green
        } else {
            $tgz = Join-Path $OutputDir "Optimum-v$optVer-mac-$Arch.tar.gz"
            if (Test-Path $tgz) { Remove-Item -Force $tgz }
            tar -czf $tgz -C $OutputDir 'Optimum.app'
            Write-Warning "No .dmg toolchain (need macOS hdiutil, or cdrtools mkisofs + cmake + git). Wrote $tgz instead."
        }
    }
} finally {
    Pop-Location
}
