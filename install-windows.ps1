<#
.SYNOPSIS
Optimum graphical installer for Windows x64.

Detects prerequisites (Vintage Story install, .NET 10 SDK, ilspycmd), shows
their status in a branded dark-themed panel, and lets the user resolve missing
items before building. Uses the local VS install for decompilation instead of
downloading a 570MB installer.

.PARAMETER Silent
Run headlessly (no window).

.PARAMETER InstallDir
Target folder.

.PARAMETER DataPath
Separate data folder (--dataPath).

.PARAMETER Shortcut
Create a desktop shortcut.

.PARAMETER LogFile
Internal log-tail file for the wizard.

.PARAMETER VsPath
Path to an existing Vintage Story install (skips auto-detection).
#>

[CmdletBinding()]
param(
    [switch]$Silent,
    [string]$InstallDir,
    [string]$DataPath,
    [switch]$Shortcut,
    [switch]$StartMenu,
    [string]$LogFile,
    [string]$VsPath,
    [switch]$DownloadVs
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Self = $PSCommandPath

# ===========================================================================
# Detection helpers
# ===========================================================================

function Find-VintageStory {
    $keys = @('{70364653-036D-49B3-8B80-AF39665F29C1}_is1', 'Vintage Story_is1')
    $hives = @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($hive in $hives) {
        foreach ($key in $keys) {
            $reg = Get-ItemProperty -Path "$hive\$key" -ErrorAction SilentlyContinue
            if (-not $reg) { continue }
            $dir = $reg.InstallLocation
            if ($dir) { $dir = $dir.TrimEnd('\') }
            if ($dir -and (Test-Path (Join-Path $dir 'Vintagestory.exe'))) {
                return @{ Path = $dir; Version = $reg.DisplayVersion }
            }
        }
    }
    $dir = Join-Path $env:APPDATA 'Vintagestory'
    if (Test-Path (Join-Path $dir 'Vintagestory.exe')) {
        # Fallback: extract version from the exe FileVersionInfo
        $exePath = Join-Path $dir 'Vintagestory.exe'
        $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
        $ver = $fvi.ProductVersion
        return @{ Path = $dir; Version = $ver }
    }
    return $null
}

function Get-RequiredVsVersion {
    $json = Get-Content (Join-Path $Root 'forks.json') -Raw | ConvertFrom-Json
    return $json.vintageStoryVersion
}

function Find-OptimumInstall {
    $regKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1'
    $reg = Get-ItemProperty -Path $regKey -ErrorAction SilentlyContinue
    if (-not $reg) { return $null }
    $dir = $reg.InstallLocation
    if ($dir) { $dir = $dir.TrimEnd('\') }
    return @{ Path = $dir; Version = $reg.DisplayVersion }
}

function Test-DotNet10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    return [bool](dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' })
}

function Find-ILSpyCmd {
    if (Get-Command ilspycmd -ErrorAction SilentlyContinue) { return 'ilspycmd' }
    $toolPath = Join-Path $env:USERPROFILE '.dotnet\tools\ilspycmd.exe'
    if (Test-Path $toolPath) { return $toolPath }
    return $null
}

function Install-ILSpyCmd {
    $manifest = Join-Path $Root '.config\dotnet-tools.json'
    if (Test-Path $manifest) {
        $json = Get-Content $manifest -Raw | ConvertFrom-Json
        $ver = $json.tools.ilspycmd.version
        dotnet tool install -g ilspycmd --version $ver 2>&1 | Out-Null
    } else {
        dotnet tool install -g ilspycmd 2>&1 | Out-Null
    }
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

# ===========================================================================
# Worker: headless build pipeline
# ===========================================================================
$script:LogWriter = $null

function Write-Log($msg) {
    $line = [string]$msg
    try { [Console]::Out.WriteLine($line) } catch { }
    if ($script:LogWriter) { try { $script:LogWriter.WriteLine($line) } catch { } }
}

function Write-Phase($text) { Write-Log "==PHASE== $text" }

function Invoke-OptimumBuild {
    param([string]$InstallDir, [string]$DataPath, [bool]$Shortcut, [bool]$StartMenu, [string]$VsPath, [bool]$DownloadVs)

    if (-not $InstallDir) { throw "InstallDir is required." }

    Write-Phase "Verifying tools..."
    if (-not (Test-DotNet10)) {
        throw ".NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and try again."
    }

    # Resolve VS path: use local install or download
    $requiredVer = Get-RequiredVsVersion
    if (-not $VsPath) {
        $vsInfo = Find-VintageStory
        if ($vsInfo) {
            $VsPath = $vsInfo.Path
            if ($vsInfo.Version -and $vsInfo.Version -ne $requiredVer) {
                throw "Vintage Story version mismatch: found $($vsInfo.Version), Optimum 0.1.1 requires $requiredVer. Update or reinstall VS $requiredVer."
            }
        }
    }
    if ((-not $VsPath -or -not (Test-Path (Join-Path $VsPath 'Vintagestory.exe'))) -and $DownloadVs) {
        Write-Phase "Downloading Vintage Story (~570 MB)..."
        $zipCache = Join-Path $Root '.vanilla-zips'
        New-Item -ItemType Directory -Force -Path $zipCache | Out-Null
        $exeName = 'vs_install_win-x64_1.22.3.exe'
        $installer = Join-Path $zipCache $exeName
        if (-not (Test-Path $installer)) {
            $url = "https://cdn.vintagestory.at/gamefiles/stable/$exeName"
            Write-Log "Downloading $url"
            curl.exe -L --fail --progress-bar -o $installer $url
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
        }
        # Extract with innounp
        $toolsDir = Join-Path $Root '.tools'
        $innounp = Join-Path $toolsDir 'innounp.exe'
        if (-not (Test-Path $innounp)) {
            Write-Phase "Downloading extraction tool..."
            New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
            $innounpZip = Join-Path $toolsDir 'innounp-2.zip'
            curl.exe -L --fail --silent -o $innounpZip "https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/ui_2_2_9/innounp-2.zip"
            Expand-Archive -Path $innounpZip -DestinationPath $toolsDir -Force
            $found = Get-ChildItem -Path $toolsDir -Recurse -Filter 'innounp.exe' | Select-Object -First 1
            if ($found -and $found.FullName -ne $innounp) { Copy-Item -Force $found.FullName $innounp }
            Remove-Item -Force $innounpZip -ErrorAction SilentlyContinue
        }
        Write-Phase "Unpacking Vintage Story..."
        $vanillaDir = Join-Path $Root '.vanilla\vintagestory'
        New-Item -ItemType Directory -Force -Path $vanillaDir | Out-Null
        & $innounp -x -d"$vanillaDir" -c"{app}" $installer | Out-Null
        $appDir = Join-Path $vanillaDir '{app}'
        if (Test-Path $appDir) {
            Get-ChildItem -Path $appDir | Move-Item -Destination $vanillaDir -Force
            Remove-Item -Force $appDir
        }
        if (-not (Test-Path (Join-Path $vanillaDir 'Vintagestory.exe'))) {
            throw "Extraction failed: Vintagestory.exe not found"
        }
        $VsPath = $vanillaDir
    }

    if (-not $VsPath -or -not (Test-Path (Join-Path $VsPath 'Vintagestory.exe'))) {
        throw "Vintage Story install not found. Point the installer to your VS folder or enable Download."
    }
    Write-Log "Using Vintage Story from: $VsPath"

    if (-not (Find-ILSpyCmd)) {
        Write-Phase "Installing decompiler tool..."
        Install-ILSpyCmd
    }
    if (-not (Find-ILSpyCmd)) { throw "ilspycmd install failed." }

    $srcRoot   = $Root
    # Clean up previous build temps (enabled by default)
    Get-ChildItem $env:TEMP -Directory -Filter 'Optimum-build-*' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    $buildRoot = Join-Path $env:TEMP ('Optimum-build-' + [guid]::NewGuid().ToString('N'))
    Write-Phase "Setting up build workspace..."
    Write-Log  "Build dir: $buildRoot"
    New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null

    try {
        # Exclude only top-level generated dirs (not subdirs in patches/sources with same names)
        $excludeDirs = @(
            (Join-Path $srcRoot '.git'),
            (Join-Path $srcRoot '.vanilla'),
            (Join-Path $srcRoot '.vanilla-zips'),
            (Join-Path $srcRoot '.vanilla-win'),
            (Join-Path $srcRoot '.vanilla-linux'),
            (Join-Path $srcRoot '.vanilla-mac-x64'),
            (Join-Path $srcRoot '.vanilla-mac-arm64'),
            (Join-Path $srcRoot '.baseline'),
            (Join-Path $srcRoot 'baseline'),
            (Join-Path $srcRoot 'Vintagestory'),
            (Join-Path $srcRoot 'VintagestoryLib'),
            (Join-Path $srcRoot 'VintagestoryApi'),
            (Join-Path $srcRoot 'Cairo'),
            (Join-Path $srcRoot 'VSCreativeMod'),
            (Join-Path $srcRoot 'VSEssentials'),
            (Join-Path $srcRoot 'VSSurvivalMod'),
            (Join-Path $srcRoot 'bin'),
            (Join-Path $srcRoot '.tools'),
            (Join-Path $srcRoot '.vs'),
            (Join-Path $srcRoot '.idea'),
            'obj'
        )
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            robocopy $srcRoot $buildRoot /E /NFL /NDL /NJH /NJS /NP /XD $excludeDirs /XF '*.zip' '*.tar.gz' '*.dmg' *>&1 | Out-Null
        } finally { $ErrorActionPreference = $prevEAP }
        if ($LASTEXITCODE -ge 8) { throw "Failed to copy the source into the temp folder (robocopy $LASTEXITCODE)." }
        $global:LASTEXITCODE = 0

        $vanillaLink = Join-Path $buildRoot '.vanilla\vintagestory'
        New-Item -ItemType Directory -Force -Path (Split-Path $vanillaLink) | Out-Null
        cmd /c mklink /J "$vanillaLink" "$VsPath" | Out-Null
        if (-not (Test-Path (Join-Path $vanillaLink 'Vintagestory.exe'))) {
            Write-Log "Junction failed, copying VS install..."
            Remove-Item -Recurse -Force (Split-Path $vanillaLink) -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path $vanillaLink | Out-Null
            robocopy $VsPath $vanillaLink /E /NFL /NDL /NJH /NJS /NP *>&1 | Out-Null
        }

        Push-Location $buildRoot
        try {
            $prevEAP = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                Write-Phase "Decompiling and patching the engine..."
                & "$buildRoot/scripts/bootstrap.ps1" -ClientArchive '__skip__' *>&1 | ForEach-Object { Write-Log ([string]$_) }

                Write-Phase "Building Optimum (this takes a moment)..."
                # Strip test project from slnx (not needed for installer)
                $slnx = Join-Path $buildRoot 'VintageStory.slnx'
                $slnxContent = [System.IO.File]::ReadAllText($slnx)
                $slnxContent = $slnxContent -replace '(?m)[^\r\n]*Optimum\.Tests[^\r\n]*(\r?\n)?', ''
                [System.IO.File]::WriteAllText($slnx, $slnxContent)
                dotnet build $slnx -c Release --nologo --no-incremental -v q /p:WarningLevel=0 2>&1 |
                    Where-Object { $_ -match 'error|Error|->|BUILD' } |
                    ForEach-Object { Write-Log ([string]$_) }
            } finally { $ErrorActionPreference = $prevEAP }
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }

            $stage = Join-Path $buildRoot 'stage'
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            $prevEAP = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                Write-Phase "Assembling the final package..."
                & "$buildRoot/scripts/package.ps1" -OutputDir $stage *>&1 | ForEach-Object { Write-Log ([string]$_) }
            } finally { $ErrorActionPreference = $prevEAP }

            $built = Get-ChildItem -Path $stage -Directory -Filter 'Optimum-v*-win-x64' | Select-Object -First 1
            if (-not $built) { throw "Packaged folder (Optimum-v*-win-x64) not found in $stage." }

            Write-Phase "Copying files to $InstallDir..."
            New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
            Get-ChildItem -Path $built.FullName -Force | ForEach-Object {
                $dest = Join-Path $InstallDir $_.Name
                if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
                Copy-Item -Recurse -Force -Path $_.FullName -Destination $dest
            }
        } finally { Pop-Location }

        $exe = Join-Path $InstallDir 'Optimum.exe'
        if (-not (Test-Path $exe)) { throw "Optimum.exe not found in $InstallDir after install." }

        if ($DataPath) {
            Write-Phase "Configuring data folder..."
            New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
        }

        if ($Shortcut) {
            Write-Phase "Creating desktop shortcut..."
            $desktop = [Environment]::GetFolderPath('Desktop')
            $lnk = Join-Path $desktop 'Optimum.lnk'
            $sh = New-Object -ComObject WScript.Shell
            $s = $sh.CreateShortcut($lnk)
            $s.TargetPath = $exe
            $s.WorkingDirectory = $InstallDir
            $s.IconLocation = $exe
            if ($DataPath) { $s.Arguments = "--dataPath `"$DataPath`"" }
            $s.Save()
        }

        if ($StartMenu) {
            Write-Phase "Adding Start Menu entry..."
            $startDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'Optimum'
            New-Item -ItemType Directory -Force -Path $startDir | Out-Null
            $lnk = Join-Path $startDir 'Optimum.lnk'
            $sh = New-Object -ComObject WScript.Shell
            $s = $sh.CreateShortcut($lnk)
            $s.TargetPath = $exe
            $s.WorkingDirectory = $InstallDir
            $s.IconLocation = $exe
            if ($DataPath) { $s.Arguments = "--dataPath `"$DataPath`"" }
            $s.Save()
        }

        Write-Phase "Finishing up..."
        $regKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1'
        New-Item -Path $regKey -Force | Out-Null
        Set-ItemProperty -Path $regKey -Name 'DisplayName' -Value "Optimum $requiredVer"
        Set-ItemProperty -Path $regKey -Name 'DisplayVersion' -Value '0.1.1'
        Set-ItemProperty -Path $regKey -Name 'Publisher' -Value 'Zaldaryon'
        Set-ItemProperty -Path $regKey -Name 'InstallLocation' -Value "$InstallDir\"
        Set-ItemProperty -Path $regKey -Name 'DisplayIcon' -Value (Join-Path $InstallDir 'Optimum.exe')
        $uninstallCmd = @"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "&{
    Remove-Item -Recurse -Force '$InstallDir' -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Optimum.lnk') -ErrorAction SilentlyContinue
    `$sm = Join-Path ([Environment]::GetFolderPath('Programs')) 'Optimum'
    if (Test-Path `$sm) { Remove-Item -Recurse -Force `$sm }
    Remove-Item -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1' -Recurse -ErrorAction SilentlyContinue
}"
"@
        Set-ItemProperty -Path $regKey -Name 'UninstallString' -Value ($uninstallCmd -replace "`r?`n",' ')
        Set-ItemProperty -Path $regKey -Name 'NoModify' -Value 1 -Type DWord
        Set-ItemProperty -Path $regKey -Name 'NoRepair' -Value 1 -Type DWord

        Write-Phase "Done."
        Write-Log "OK: Optimum installed to $InstallDir"
    } finally {
        Write-Phase "Cleaning up..."
        Remove-Item -Recurse -Force $buildRoot -ErrorAction SilentlyContinue
    }
}

if ($Silent) {
    if ($LogFile) {
        $logFs = [System.IO.File]::Open($LogFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
        $script:LogWriter = New-Object System.IO.StreamWriter($logFs)
        $script:LogWriter.AutoFlush = $true
    }
    try {
        Invoke-OptimumBuild -InstallDir $InstallDir -DataPath $DataPath -Shortcut:$Shortcut.IsPresent -StartMenu:$StartMenu.IsPresent -VsPath $VsPath -DownloadVs:$DownloadVs.IsPresent
        exit 0
    } catch {
        Write-Log "==PHASE== Failed."
        Write-Log "ERROR: $($_.Exception.Message)"
        exit 1
    } finally {
        if ($script:LogWriter) { $script:LogWriter.Dispose() }
    }
}

# ===========================================================================
# Wizard (WinForms) - Dark theme with Optimum branding
# ===========================================================================
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# --- Color palette: detect Windows dark/light mode, apply matching colors ---
$useDark = $true
try {
    $regTheme = Get-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue
    if ($regTheme -and $regTheme.AppsUseLightTheme -eq 1) { $useDark = $false }
} catch { }

if ($useDark) {
    $colBg        = [System.Drawing.Color]::FromArgb(32, 32, 32)
    $colSurface   = [System.Drawing.Color]::FromArgb(43, 43, 43)
    $colInput     = [System.Drawing.Color]::FromArgb(50, 50, 50)
    $colBorder    = [System.Drawing.Color]::FromArgb(70, 70, 70)
    $colText      = [System.Drawing.Color]::FromArgb(230, 230, 230)
    $colTextDim   = [System.Drawing.Color]::FromArgb(160, 160, 160)
    $colAccent    = [System.Drawing.Color]::FromArgb(200, 200, 200)
    $colAccentDim = [System.Drawing.Color]::FromArgb(150, 150, 150)
    $colSection   = [System.Drawing.Color]::FromArgb(180, 180, 180)
    $colGreen     = [System.Drawing.Color]::FromArgb(80, 200, 120)
    $colRed       = [System.Drawing.Color]::FromArgb(220, 80, 80)
    $colOrange    = [System.Drawing.Color]::FromArgb(220, 160, 50)
    $colHeader    = [System.Drawing.Color]::FromArgb(25, 25, 25)
    $colLog       = [System.Drawing.Color]::FromArgb(20, 20, 20)
    $colLogText   = [System.Drawing.Color]::FromArgb(200, 200, 200)
    $colBtnFg     = [System.Drawing.Color]::FromArgb(20, 20, 20)
} else {
    $colBg        = [System.Drawing.Color]::FromArgb(243, 243, 243)
    $colSurface   = [System.Drawing.Color]::FromArgb(255, 255, 255)
    $colInput     = [System.Drawing.Color]::FromArgb(255, 255, 255)
    $colBorder    = [System.Drawing.Color]::FromArgb(200, 200, 200)
    $colText      = [System.Drawing.Color]::FromArgb(30, 30, 30)
    $colTextDim   = [System.Drawing.Color]::FromArgb(100, 100, 100)
    $colAccent    = [System.Drawing.Color]::FromArgb(50, 50, 50)
    $colAccentDim = [System.Drawing.Color]::FromArgb(80, 80, 80)
    $colSection   = [System.Drawing.Color]::FromArgb(80, 80, 80)
    $colGreen     = [System.Drawing.Color]::FromArgb(30, 150, 70)
    $colRed       = [System.Drawing.Color]::FromArgb(200, 50, 50)
    $colOrange    = [System.Drawing.Color]::FromArgb(180, 120, 20)
    $colHeader    = [System.Drawing.Color]::FromArgb(235, 235, 235)
    $colLog       = [System.Drawing.Color]::FromArgb(250, 250, 250)
    $colLogText   = [System.Drawing.Color]::FromArgb(30, 30, 30)
    $colBtnFg     = [System.Drawing.Color]::FromArgb(255, 255, 255)
}

# --- Helpers ---
function New-FlatButton {
    param([string]$Text, [int]$W = 100, [int]$H = 30, [bool]$Primary = $false)
    $btn = New-Object System.Windows.Forms.Button
    $btn.Text = $Text
    $btn.Size = New-Object System.Drawing.Size($W, $H)
    $btn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $btn.FlatAppearance.BorderSize = 1
    $btn.Font = New-Object System.Drawing.Font('Segoe UI', 9)
    $btn.Cursor = [System.Windows.Forms.Cursors]::Hand
    if ($Primary) {
        $btn.BackColor = $colAccent
        $btn.ForeColor = $colBtnFg
        $btn.FlatAppearance.BorderColor = $colAccent
        $btn.FlatAppearance.MouseOverBackColor = $colAccentDim
        $btn.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
    } else {
        $btn.BackColor = $colSurface
        $btn.ForeColor = $colText
        $btn.FlatAppearance.BorderColor = $colBorder
        $btn.FlatAppearance.MouseOverBackColor = $colInput
    }
    return $btn
}

$script:proc       = $null
$script:logFile    = $null
$script:logPos     = 0
$script:installDir = $null

function Read-NewText {
    param([string]$Path, [ref]$Pos)
    if (-not $Path -or -not (Test-Path $Path)) { return '' }
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($fs.Length -le $Pos.Value) { return '' }
            $fs.Seek($Pos.Value, [System.IO.SeekOrigin]::Begin) | Out-Null
            $sr = New-Object System.IO.StreamReader($fs)
            $txt = $sr.ReadToEnd()
            $Pos.Value = $fs.Length
            return $txt
        } finally { $fs.Dispose() }
    } catch { return '' }
}

function Add-Log {
    param([string]$Text)
    if (-not $Text) { return }
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -eq '') { continue }
        if ($line -like '==PHASE==*') {
            $script:lblStatus.Text = $line.Substring(9).Trim()
        } elseif ($line -match 'Bootstrap complete') {
            $script:txtLog.AppendText("Patches applied. Building optimized client...`r`n")
        } elseif ($line -match '^\s*(Applying patches|Cloning |Decompiling |Synced |Applying post|Host:|Copying vanilla|Applying optimized|Renamed |Folder ready|OK:|ERROR|error )') {
            # Show meaningful progress lines
            $display = $line -replace '^\s+', ''
            $script:txtLog.AppendText($display + "`r`n")
        } elseif ($line -match 'error|FAILED|ERROR|throw') {
            # Always show errors
            $script:txtLog.AppendText($line + "`r`n")
        }
        # Suppress noisy lines (warnings, restore messages, git output)
    }
}

function Update-PrereqStatus {
    # VS install
    $requiredVer = Get-RequiredVsVersion
    $vsInfo = Find-VintageStory
    if ($vsInfo) {
        $script:detectedVsPath = $vsInfo.Path
        $script:detectedVsVer = $vsInfo.Version
        if ($vsInfo.Version -and $vsInfo.Version -ne $requiredVer) {
            $script:lblVsStatus.Text = [char]0x2717 + "  Vintage Story $($vsInfo.Version) (need $requiredVer)"
            $script:lblVsStatus.ForeColor = $colOrange
            $script:chkVsDl.Visible = $true
            $script:chkVsDl.Checked = $true
            $script:btnVsBrowse.Visible = $false
            $script:txtVsPath.Text = ''
        } else {
            $script:lblVsStatus.Text = [char]0x2713 + "  Vintage Story    $($vsInfo.Path)"
            $script:lblVsStatus.ForeColor = $colGreen
            $script:txtVsPath.Text = $vsInfo.Path
            $script:chkVsDl.Visible = $false
            $script:btnVsBrowse.Visible = $false
        }
    } else {
        $script:detectedVsPath = $null
        $script:detectedVsVer = $null
        $script:lblVsStatus.Text = [char]0x2717 + '  Vintage Story'
        $script:lblVsStatus.ForeColor = $colRed
        $script:chkVsDl.Visible = $true
        $script:chkVsDl.Checked = $true
        $script:btnVsBrowse.Visible = $true
    }

    # .NET 10 SDK
    if (Test-DotNet10) {
        $script:lblDotnetStatus.Text = [char]0x2713 + '  .NET 10 SDK'
        $script:lblDotnetStatus.ForeColor = $colGreen
        $script:chkDotnetDl.Visible = $false
    } else {
        $script:lblDotnetStatus.Text = [char]0x2717 + '  .NET 10 SDK'
        $script:lblDotnetStatus.ForeColor = $colRed
        $script:chkDotnetDl.Visible = $true
        $script:chkDotnetDl.Checked = $true
    }

    # ilspycmd
    if (Find-ILSpyCmd) {
        $script:lblIlspyStatus.Text = [char]0x2713 + '  ilspycmd'
        $script:lblIlspyStatus.ForeColor = $colGreen
        $script:chkIlspyDl.Visible = $false
    } else {
        $script:lblIlspyStatus.Text = [char]0x2717 + '  ilspycmd'
        $script:lblIlspyStatus.ForeColor = $colOrange
        $script:chkIlspyDl.Visible = $true
        $script:chkIlspyDl.Checked = $true
    }

    # innounp (needed to extract VS installer if downloading)
    $innounpPath = Join-Path (Join-Path $Root '.tools') 'innounp.exe'
    $hasInnounp = Test-Path $innounpPath
    if ($script:chkVsDl.Visible -and $script:chkVsDl.Checked) {
        $script:lblInnounpStatus.Visible = $true
        $script:chkInnounpDl.Visible = -not $hasInnounp
        if ($hasInnounp) {
            $script:lblInnounpStatus.Text = [char]0x2713 + '  innounp (extractor)'
            $script:lblInnounpStatus.ForeColor = $colGreen
        } else {
            $script:lblInnounpStatus.Text = [char]0x2717 + '  innounp (extractor)'
            $script:lblInnounpStatus.ForeColor = $colOrange
            $script:chkInnounpDl.Checked = $true
        }
    } else {
        $script:lblInnounpStatus.Visible = $false
        $script:chkInnounpDl.Visible = $false
    }

    # Enable Install: either VS detected or user chose to download it, plus .NET 10 present or will download
    $vsOk = [bool]$script:txtVsPath.Text.Trim() -or ($script:chkVsDl.Visible -and $script:chkVsDl.Checked)
    $dotnetOk = (Test-DotNet10) -or ($script:chkDotnetDl.Visible -and $script:chkDotnetDl.Checked)
    $script:btnInstall.Enabled = ($vsOk -and $dotnetOk)
}

# === Form ===
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Optimum Installer'
$form.Size = New-Object System.Drawing.Size(620, 740)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.BackColor = $colBg
$form.ForeColor = $colText
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)

# Load icon from logo.png if present
$logoPng = Join-Path $Root 'logo.png'
if (Test-Path $logoPng) {
    try {
        $icoStream = New-Object System.IO.MemoryStream
        $bmp = [System.Drawing.Bitmap]::new($logoPng)
        $ico = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
        $form.Icon = $ico
    } catch { }
}

# === Header panel with logo ===
$pnlHeader = New-Object System.Windows.Forms.Panel
$pnlHeader.Dock = [System.Windows.Forms.DockStyle]::Top
$pnlHeader.Height = 80
$pnlHeader.BackColor = $colHeader
$form.Controls.Add($pnlHeader)

# Logo image
$script:picLogo = New-Object System.Windows.Forms.PictureBox
$script:picLogo.Location = New-Object System.Drawing.Point(20, 12)
$script:picLogo.Size = New-Object System.Drawing.Size(56, 56)
$script:picLogo.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::Zoom
$script:picLogo.BackColor = [System.Drawing.Color]::Transparent
if (Test-Path $logoPng) {
    try { $script:picLogo.Image = [System.Drawing.Image]::FromFile($logoPng) } catch { }
}
$pnlHeader.Controls.Add($script:picLogo)

# Title
$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = 'Optimum'
$lblTitle.Font = New-Object System.Drawing.Font('Segoe UI', 20, [System.Drawing.FontStyle]::Bold)
$lblTitle.ForeColor = $colText
$lblTitle.Location = New-Object System.Drawing.Point(84, 14)
$lblTitle.AutoSize = $true
$lblTitle.BackColor = [System.Drawing.Color]::Transparent
$pnlHeader.Controls.Add($lblTitle)

# Subtitle
$lblSub = New-Object System.Windows.Forms.Label
$lblSub.Text = 'High-performance client for Vintage Story'
$lblSub.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$lblSub.ForeColor = $colTextDim
$lblSub.Location = New-Object System.Drawing.Point(86, 54)
$lblSub.AutoSize = $true
$lblSub.BackColor = [System.Drawing.Color]::Transparent
$pnlHeader.Controls.Add($lblSub)

# Subtle accent line
$pnlGoldLine = New-Object System.Windows.Forms.Panel
$pnlGoldLine.Location = New-Object System.Drawing.Point(0, 78)
$pnlGoldLine.Size = New-Object System.Drawing.Size(620, 1)
$pnlGoldLine.BackColor = $colBorder
$pnlHeader.Controls.Add($pnlGoldLine)

$y = 94

# === Prerequisites section ===
$lblPrereqTitle = New-Object System.Windows.Forms.Label
$lblPrereqTitle.Text = 'PREREQUISITES'
$lblPrereqTitle.Font = New-Object System.Drawing.Font('Segoe UI', 8, [System.Drawing.FontStyle]::Bold)
$lblPrereqTitle.ForeColor = $colSection
$lblPrereqTitle.Location = New-Object System.Drawing.Point(20, $y)
$lblPrereqTitle.AutoSize = $true
$form.Controls.Add($lblPrereqTitle)
$y += 26

# -- Vintage Story --
$script:lblVsStatus = New-Object System.Windows.Forms.Label
$script:lblVsStatus.Text = 'Vintage Story'
$script:lblVsStatus.Location = New-Object System.Drawing.Point(20, $y)
$script:lblVsStatus.Size = New-Object System.Drawing.Size(440, 18)
$script:lblVsStatus.ForeColor = $colTextDim
$script:lblVsStatus.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$form.Controls.Add($script:lblVsStatus)

$script:chkVsDl = New-Object System.Windows.Forms.CheckBox
$script:chkVsDl.Text = 'Download'
$script:chkVsDl.Location = New-Object System.Drawing.Point(494, ($y - 1))
$script:chkVsDl.AutoSize = $true
$script:chkVsDl.ForeColor = $colText
$script:chkVsDl.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkVsDl.Checked = $true
$script:chkVsDl.Visible = $false
$script:chkVsDl.Add_CheckedChanged({ Update-PrereqStatus })
$form.Controls.Add($script:chkVsDl)

$script:btnVsBrowse = New-FlatButton -Text 'Browse' -W 70 -H 20
$script:btnVsBrowse.Location = New-Object System.Drawing.Point(494, ($y - 1))
$script:btnVsBrowse.Visible = $false
$script:btnVsBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select the folder containing Vintagestory.exe'
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        if (Test-Path (Join-Path $dlg.SelectedPath 'Vintagestory.exe')) {
            $script:txtVsPath.Text = $dlg.SelectedPath
            Update-PrereqStatus
        } else {
            [System.Windows.Forms.MessageBox]::Show(
                "Vintagestory.exe not found in that folder.",
                'Invalid folder', 'OK', 'Warning') | Out-Null
        }
    }
})
$form.Controls.Add($script:btnVsBrowse)

# Hidden field for the VS path
$script:txtVsPath = New-Object System.Windows.Forms.TextBox
$script:txtVsPath.Visible = $false
$form.Controls.Add($script:txtVsPath)
$y += 26

# -- .NET 10 SDK --
$script:lblDotnetStatus = New-Object System.Windows.Forms.Label
$script:lblDotnetStatus.Location = New-Object System.Drawing.Point(20, $y)
$script:lblDotnetStatus.Size = New-Object System.Drawing.Size(440, 18)
$script:lblDotnetStatus.ForeColor = $colTextDim
$script:lblDotnetStatus.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$form.Controls.Add($script:lblDotnetStatus)

$script:chkDotnetDl = New-Object System.Windows.Forms.CheckBox
$script:chkDotnetDl.Text = 'Download'
$script:chkDotnetDl.Location = New-Object System.Drawing.Point(494, ($y - 1))
$script:chkDotnetDl.AutoSize = $true
$script:chkDotnetDl.ForeColor = $colText
$script:chkDotnetDl.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkDotnetDl.Checked = $true
$script:chkDotnetDl.Visible = $false
$form.Controls.Add($script:chkDotnetDl)
$y += 26

# -- ilspycmd --
$script:lblIlspyStatus = New-Object System.Windows.Forms.Label
$script:lblIlspyStatus.Location = New-Object System.Drawing.Point(20, $y)
$script:lblIlspyStatus.Size = New-Object System.Drawing.Size(440, 18)
$script:lblIlspyStatus.ForeColor = $colTextDim
$script:lblIlspyStatus.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$form.Controls.Add($script:lblIlspyStatus)

$script:chkIlspyDl = New-Object System.Windows.Forms.CheckBox
$script:chkIlspyDl.Text = 'Install'
$script:chkIlspyDl.Location = New-Object System.Drawing.Point(494, ($y - 1))
$script:chkIlspyDl.AutoSize = $true
$script:chkIlspyDl.ForeColor = $colText
$script:chkIlspyDl.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkIlspyDl.Checked = $true
$script:chkIlspyDl.Visible = $false
$form.Controls.Add($script:chkIlspyDl)
$y += 26

# -- innounp (extractor, needed when downloading VS) --
$script:lblInnounpStatus = New-Object System.Windows.Forms.Label
$script:lblInnounpStatus.Location = New-Object System.Drawing.Point(20, $y)
$script:lblInnounpStatus.Size = New-Object System.Drawing.Size(440, 18)
$script:lblInnounpStatus.ForeColor = $colTextDim
$script:lblInnounpStatus.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$script:lblInnounpStatus.Visible = $false
$form.Controls.Add($script:lblInnounpStatus)

$script:chkInnounpDl = New-Object System.Windows.Forms.CheckBox
$script:chkInnounpDl.Text = 'Download'
$script:chkInnounpDl.Location = New-Object System.Drawing.Point(494, ($y - 1))
$script:chkInnounpDl.AutoSize = $true
$script:chkInnounpDl.ForeColor = $colText
$script:chkInnounpDl.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkInnounpDl.Checked = $true
$script:chkInnounpDl.Visible = $false
$form.Controls.Add($script:chkInnounpDl)
$y += 30

# Separator
$pnlSep1 = New-Object System.Windows.Forms.Panel
$pnlSep1.Location = New-Object System.Drawing.Point(20, $y)
$pnlSep1.Size = New-Object System.Drawing.Size(564, 1)
$pnlSep1.BackColor = $colBorder
$form.Controls.Add($pnlSep1)
$y += 18

# === Install options ===
$lblOptTitle = New-Object System.Windows.Forms.Label
$lblOptTitle.Text = 'INSTALL OPTIONS'
$lblOptTitle.Font = New-Object System.Drawing.Font('Segoe UI', 8, [System.Drawing.FontStyle]::Bold)
$lblOptTitle.ForeColor = $colSection
$lblOptTitle.Location = New-Object System.Drawing.Point(20, $y)
$lblOptTitle.AutoSize = $true
$form.Controls.Add($lblOptTitle)
$y += 26

# Install folder
$lblDir = New-Object System.Windows.Forms.Label
$lblDir.Text = 'Install folder'
$lblDir.Location = New-Object System.Drawing.Point(20, $y)
$lblDir.AutoSize = $true
$lblDir.ForeColor = $colTextDim
$lblDir.Font = New-Object System.Drawing.Font('Segoe UI', 8.5)
$form.Controls.Add($lblDir)
$y += 18

$script:txtDir = New-Object System.Windows.Forms.TextBox
$script:txtDir.Location = New-Object System.Drawing.Point(20, $y)
$script:txtDir.Size = New-Object System.Drawing.Size(460, 26)
$script:txtDir.Text = 'C:\Games\Optimum'
$script:txtDir.BackColor = $colInput
$script:txtDir.ForeColor = $colText
$script:txtDir.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$form.Controls.Add($script:txtDir)

$script:btnBrowse = New-FlatButton -Text 'Browse' -W 90 -H 26
$script:btnBrowse.Location = New-Object System.Drawing.Point(494, $y)
$script:btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Choose the Optimum install folder'
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:txtDir.Text = (Join-Path $dlg.SelectedPath 'Optimum')
    }
})
$form.Controls.Add($script:btnBrowse)
$y += 38

# Separate data folder
$script:chkSep = New-Object System.Windows.Forms.CheckBox
$script:chkSep.Text = 'Use a separate data folder (--dataPath)'
$script:chkSep.Location = New-Object System.Drawing.Point(20, $y)
$script:chkSep.AutoSize = $true
$script:chkSep.ForeColor = $colText
$script:chkSep.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkSep.Checked = $false
$form.Controls.Add($script:chkSep)
$y += 26

$script:txtData = New-Object System.Windows.Forms.TextBox
$script:txtData.Location = New-Object System.Drawing.Point(40, $y)
$script:txtData.Size = New-Object System.Drawing.Size(544, 26)
$script:txtData.Text = (Join-Path $env:APPDATA 'OptimumData')
$script:txtData.BackColor = $colInput
$script:txtData.ForeColor = $colText
$script:txtData.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$script:txtData.Enabled = $false
$script:txtData.Visible = $false
$form.Controls.Add($script:txtData)
$script:chkSep.Add_CheckedChanged({
    $script:txtData.Visible = $script:chkSep.Checked
    $script:txtData.Enabled = $script:chkSep.Checked
})
$y += 34

# Desktop shortcut
$script:chkShortcut = New-Object System.Windows.Forms.CheckBox
$script:chkShortcut.Text = 'Create a desktop shortcut'
$script:chkShortcut.Location = New-Object System.Drawing.Point(20, $y)
$script:chkShortcut.AutoSize = $true
$script:chkShortcut.ForeColor = $colText
$script:chkShortcut.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkShortcut.Checked = $true
$form.Controls.Add($script:chkShortcut)
$y += 28

# Start Menu shortcut
$script:chkStartMenu = New-Object System.Windows.Forms.CheckBox
$script:chkStartMenu.Text = 'Add to Start Menu'
$script:chkStartMenu.Location = New-Object System.Drawing.Point(20, $y)
$script:chkStartMenu.AutoSize = $true
$script:chkStartMenu.ForeColor = $colText
$script:chkStartMenu.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkStartMenu.Checked = $true
$form.Controls.Add($script:chkStartMenu)
$y += 36

# === Status + progress ===
$script:lblStatus = New-Object System.Windows.Forms.Label
$script:lblStatus.Text = ''
$script:lblStatus.Location = New-Object System.Drawing.Point(20, $y)
$script:lblStatus.Size = New-Object System.Drawing.Size(564, 18)
$script:lblStatus.ForeColor = $colTextDim
$script:lblStatus.Font = New-Object System.Drawing.Font('Segoe UI', 8.5)
$form.Controls.Add($script:lblStatus)
$y += 22

$script:progress = New-Object System.Windows.Forms.ProgressBar
$script:progress.Location = New-Object System.Drawing.Point(20, $y)
$script:progress.Size = New-Object System.Drawing.Size(564, 8)
$script:progress.Style = 'Continuous'
$script:progress.Visible = $false
$form.Controls.Add($script:progress)
$y += 16

# === Log area ===
$script:txtLog = New-Object System.Windows.Forms.TextBox
$script:txtLog.Location = New-Object System.Drawing.Point(20, $y)
$logH = $form.ClientSize.Height - $y - 56
$script:txtLog.Size = New-Object System.Drawing.Size(564, $logH)
$script:txtLog.Multiline = $true
$script:txtLog.ReadOnly = $true
$script:txtLog.ScrollBars = 'Vertical'
$script:txtLog.BackColor = $colLog
$script:txtLog.ForeColor = $colLogText
$script:txtLog.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$script:txtLog.Font = New-Object System.Drawing.Font('Cascadia Mono,Consolas', 8.5)
$form.Controls.Add($script:txtLog)

# === Bottom buttons ===
# === Footer version ===
$btnY = $form.ClientSize.Height - 44
$lblVersion = New-Object System.Windows.Forms.Label
$lblVersion.Text = 'vs1.22.3+v0.1.1'
$lblVersion.Font = New-Object System.Drawing.Font('Segoe UI', 8)
$lblVersion.ForeColor = $colTextDim
$lblVersion.Location = New-Object System.Drawing.Point(20, ($btnY + 10))
$lblVersion.AutoSize = $true
$form.Controls.Add($lblVersion)

# === Bottom buttons ===
$script:btnCancel = New-FlatButton -Text 'Cancel' -W 100 -H 34
$script:btnCancel.Location = New-Object System.Drawing.Point(484, $btnY)
$script:btnCancel.Add_Click({
    if ($script:proc -and -not $script:proc.HasExited) {
        try { $script:proc.Kill() } catch { }
    }
    $form.Close()
})
$form.Controls.Add($script:btnCancel)

$script:btnInstall = New-FlatButton -Text 'Install' -W 110 -H 34 -Primary $true
$script:btnInstall.Location = New-Object System.Drawing.Point(366, $btnY)
$script:btnInstall.Enabled = $false
$form.Controls.Add($script:btnInstall)

# === Timer ===
$script:timer = New-Object System.Windows.Forms.Timer
$script:timer.Interval = 300
$script:timer.Add_Tick({
    Add-Log (Read-NewText $script:logFile ([ref]$script:logPos))
    if ($script:proc -and $script:proc.HasExited) {
        $script:timer.Stop()
        Add-Log (Read-NewText $script:logFile ([ref]$script:logPos))
        $code = $script:proc.ExitCode
        if ($script:logFile -and (Test-Path $script:logFile)) {
            $script:rawLogContent = [System.IO.File]::ReadAllText($script:logFile)
            if ($code -eq 0) { Remove-Item -Force $script:logFile -ErrorAction SilentlyContinue }
        }
        # Always save the full log for transparency
        $logDir = Join-Path $env:LOCALAPPDATA 'Optimum'
        New-Item -ItemType Directory -Force -Path $logDir -ErrorAction SilentlyContinue | Out-Null
        $ts = Get-Date -Format 'yyyy-MM-ddTHHmm'
        $script:savedLog = Join-Path $logDir "optimum-install-$ts.log"
        try { [System.IO.File]::WriteAllText($script:savedLog, $script:rawLogContent) } catch { }

        if ($code -eq 0) {
            $script:progress.Style = 'Continuous'
            $script:progress.Value = 100
            $script:lblStatus.Text = "Done. Installed to $($script:installDir)"
            $script:lblStatus.ForeColor = $colGreen
            $script:btnInstall.Text = 'View Log'
            $script:btnInstall.Enabled = $true
            $script:btnInstall.Tag = 'log'
            $script:btnCancel.Text = 'Exit'
        } else {
            $script:progress.Visible = $false
            $script:lblStatus.Text = 'Installation failed.'
            $script:lblStatus.ForeColor = $colRed
            $script:btnInstall.Text = 'View Log'
            $script:btnInstall.Enabled = $true
            $script:btnInstall.Tag = 'log'
        }
    }
})

# === Install click ===
$script:btnInstall.Add_Click({
    # If button was repurposed to "View Log" after success/failure
    if ($script:btnInstall.Tag -eq 'log' -and $script:savedLog) {
        Start-Process notepad.exe $script:savedLog
        return
    }

    # Disclaimer (shown once per click) - industry standard scrollable form with acceptance checkbox
    $disclaimer = @"
END-USER NOTICE AND LICENSE AGREEMENT

1. INDEPENDENT PROJECT
Optimum is an independent, community-developed project. It is not affiliated with, endorsed by, sponsored by, or associated with Anego Studios or the Vintage Story development team. "Vintage Story" and related marks are trademarks of Anego Studios. All rights to the original game, its assets, and intellectual property belong to their respective owners.

2. WHAT THIS INSTALLER DOES
This installer decompiles the Vintage Story client binaries from your local installation, clones the source-available repositories maintained by Anego Studios (VintagestoryAPI, Cairo, VSEssentials, VSSurvivalMod, VSCreativeMod), applies performance patches at compile time, and recompiles an optimized build. A purchased Vintage Story game account and a local installation of the game are required. No game files, assets, or binaries are distributed with Optimum. The source-available repositories remain the proprietary property of Anego Studios under their stated terms.

3. LICENSE
Optimum is licensed under the GNU General Public License v3.0 with the Commons Clause restriction. You may use, modify, and redistribute the source code under those terms. You may not sell Optimum or any product whose value derives from it.

4. DISCLAIMER OF WARRANTY
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES, OR OTHER LIABILITY ARISING FROM THE USE OF THIS SOFTWARE.

5. SOURCE CODE TRANSPARENCY
The complete source code, patches, and build scripts are publicly available for inspection at: https://github.com/Zaldaryon/Optimum

6. ACCEPTANCE
By checking the box below and proceeding, you acknowledge that you have read, understood, and agree to the terms above.
"@
    $agreeForm = New-Object System.Windows.Forms.Form
    $agreeForm.Text = 'Optimum - License Agreement'
    $agreeForm.Size = New-Object System.Drawing.Size(540, 440)
    $agreeForm.StartPosition = 'CenterParent'
    $agreeForm.FormBorderStyle = 'FixedDialog'
    $agreeForm.MaximizeBox = $false
    $agreeForm.MinimizeBox = $false
    $agreeForm.BackColor = $colBg

    $txt = New-Object System.Windows.Forms.TextBox
    $txt.Multiline = $true
    $txt.ReadOnly = $true
    $txt.ScrollBars = 'Vertical'
    $txt.Location = New-Object System.Drawing.Point(16, 16)
    $txt.Size = New-Object System.Drawing.Size(492, 290)
    $txt.Text = $disclaimer -replace "`n", "`r`n"
    $txt.BackColor = $colInput
    $txt.ForeColor = $colText
    $txt.Font = New-Object System.Drawing.Font('Segoe UI', 9)
    $txt.SelectionStart = 0
    $txt.SelectionLength = 0
    $agreeForm.Controls.Add($txt)

    $chk = New-Object System.Windows.Forms.CheckBox
    $chk.Text = 'I have read and accept the terms above'
    $chk.Location = New-Object System.Drawing.Point(16, 316)
    $chk.AutoSize = $true
    $chk.ForeColor = $colText
    $chk.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $agreeForm.Controls.Add($chk)

    $btnAccept = New-FlatButton -Text 'Continue' -W 110 -H 34
    $btnAccept.Location = New-Object System.Drawing.Point(270, 355)
    $btnAccept.Enabled = $false
    $btnAccept.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $agreeForm.AcceptButton = $btnAccept
    $agreeForm.Controls.Add($btnAccept)

    $btnDecline = New-FlatButton -Text 'Decline' -W 110 -H 34
    $btnDecline.Location = New-Object System.Drawing.Point(390, 355)
    $btnDecline.BackColor = $colSurface
    $btnDecline.ForeColor = $colText
    $btnDecline.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $agreeForm.CancelButton = $btnDecline
    $agreeForm.Controls.Add($btnDecline)

    $chk.Add_CheckedChanged({ $btnAccept.Enabled = $chk.Checked })

    if ($agreeForm.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }

    $dir = $script:txtDir.Text.Trim()
    if (-not $dir) {
        [System.Windows.Forms.MessageBox]::Show('Choose the install folder.', 'Optimum', 'OK', 'Warning') | Out-Null
        return
    }

    # .NET 10 SDK: if missing and user unchecked download, block
    if (-not (Test-DotNet10) -and -not $script:chkDotnetDl.Checked) {
        [System.Windows.Forms.MessageBox]::Show('.NET 10 SDK is required. Check the download option or install it manually.', 'Optimum', 'OK', 'Warning') | Out-Null
        return
    }
    # .NET 10: if missing and checked, open download page (can''t auto-install SDK)
    if (-not (Test-DotNet10) -and $script:chkDotnetDl.Checked) {
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/10.0'
        [System.Windows.Forms.MessageBox]::Show("Install the .NET 10 SDK from the page that opened, then click Install again.", 'Optimum', 'OK', 'Information') | Out-Null
        return
    }

    $vsP = $script:txtVsPath.Text.Trim()
    $downloadVs = ($script:chkVsDl.Visible -and $script:chkVsDl.Checked -and -not $vsP)

    if (-not $downloadVs -and (-not $vsP -or -not (Test-Path (Join-Path $vsP 'Vintagestory.exe')))) {
        [System.Windows.Forms.MessageBox]::Show('A valid Vintage Story install is required, or check the Download option.', 'Optimum', 'OK', 'Warning') | Out-Null
        return
    }

    if ((Test-Path $dir) -and (Get-ChildItem -Path $dir -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        $existingReg = Find-OptimumInstall
        $existingExe = Join-Path $dir 'Optimum.exe'
        $existingSemver = $null
        if ($existingReg -and $existingReg.Path -eq $dir) {
            $existingSemver = $existingReg.Version
        } elseif (Test-Path $existingExe) {
            $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($existingExe)
            $existingSemver = ($fvi.ProductVersion -split '\+')[0]
        }

        $thisVer = '0.1.1'
        if ($existingSemver) {
            if ([version]$existingSemver -lt [version]$thisVer) {
                $r = [System.Windows.Forms.MessageBox]::Show(
                    "Optimum $existingSemver is installed. Remove it and upgrade to $($thisVer)?",
                    'Upgrade', 'YesNo', 'Question')
                if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
                Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
            } else {
                $r = [System.Windows.Forms.MessageBox]::Show(
                    "Optimum $existingSemver is already installed. Reinstall?",
                    'Confirm', 'YesNo', 'Warning')
                if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
            }
        } else {
            $r = [System.Windows.Forms.MessageBox]::Show("'$dir' is not empty. Overwrite?", 'Confirm', 'YesNo', 'Warning')
            if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        }
    }

    $q = [char]34
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File $q$Self$q -Silent -InstallDir $q$dir$q"
    if ($vsP) { $argLine += " -VsPath $q$vsP$q" }
    if ($downloadVs) { $argLine += ' -DownloadVs' }
    if ($script:chkSep.Checked) {
        $data = $script:txtData.Text.Trim()
        if (-not $data) {
            [System.Windows.Forms.MessageBox]::Show('Enter the data folder or uncheck the option.', 'Optimum', 'OK', 'Warning') | Out-Null
            return
        }
        $argLine += " -DataPath $q$data$q"
    }
    if ($script:chkShortcut.Checked) { $argLine += ' -Shortcut' }
    if ($script:chkStartMenu.Checked) { $argLine += ' -StartMenu' }

    $script:installDir = $dir
    $script:logFile = [System.IO.Path]::GetTempFileName()
    $script:logPos = 0
    $argLine += " -LogFile $q$($script:logFile)$q"
    $script:txtLog.Clear()

    $script:btnInstall.Enabled = $false
    $script:progress.Style = 'Marquee'
    $script:progress.MarqueeAnimationSpeed = 30
    $script:progress.Visible = $true
    $script:lblStatus.Text = 'Starting build...'
    $script:lblStatus.ForeColor = $colTextDim

    $script:proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -WindowStyle Hidden -PassThru
    $script:timer.Start()
})

# === On shown ===
$form.Add_Shown({
    Update-PrereqStatus
    if ($script:btnInstall.Enabled) {
        $script:lblStatus.Text = 'Ready to install.'
        $script:lblStatus.ForeColor = $colGreen
    } else {
        $script:lblStatus.Text = 'Resolve prerequisites above.'
        $script:lblStatus.ForeColor = $colOrange
    }
    $script:txtLog.AppendText("")
})

[void]$form.ShowDialog()
