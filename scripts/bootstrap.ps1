<#
.SYNOPSIS
Builds a clean working tree for Optimum (client fork):
  1. Downloads the official VS Windows installer (vs_install_win-x64_*.exe).
  2. Decompiles closed-source DLLs (VintagestoryLib.dll, Vintagestory.dll) with ILSpy.
  3. Clones open-source Anego forks at pinned refs (and reference-only repos, if any).
  4. Applies post-decompile fixups (csproj rewrites, ambiguity resolution, ref-casts,
     GeneratedRegex, serialization metadata restoration, and the long tail of
     ILSpy decompiler artifacts documented inline below).
  5. Snapshots the post-fixup tree to .baseline/ (used by extract-patches.ps1/.sh).
  6. Applies patches/ on top of build/{VintagestoryLib,Vintagestory} and the fork
     working trees.
  7. Copies sources/ (Optimum-original files) into the working tree.

This mirrors scripts/bootstrap.sh's architecture exactly (same build/ working
tree, same .baseline/ snapshot, same --directory=build patch targeting, same
exit-nonzero-on-patch-failure behavior) so the two scripts stay in lockstep.
Every fixup below is implemented natively in PowerShell (no perl/python3
dependency), since install-windows.ps1's headless pipeline only guarantees
.NET, Git, and Windows PowerShell 5.1 are present on the end user's machine.

This only reconstructs the dev tree. To build redistributable packages, run
scripts/package-all.ps1 (or `make package`) after `dotnet build`.

.PARAMETER Version
Vintage Story version. Default: 1.22.3.

.PARAMETER ClientArchive
Path to an existing Windows installer (.exe). If omitted, downloads from cdn.vintagestory.at.
Pass '__skip__' when the caller (install-windows.ps1) already placed .vanilla via junction.

.PARAMETER Refresh
Force re-extract, re-decompile, re-clone.

.EXAMPLE
.\scripts\bootstrap.ps1
.\scripts\bootstrap.ps1 -Refresh
.\scripts\bootstrap.ps1 -ClientArchive C:\Downloads\vs_install_win-x64_1.22.3.exe
#>

[CmdletBinding()]
param(
    [string]$Version = '1.22.3',
    [string]$ClientArchive,
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_exec.ps1"
$repoRoot = Split-Path -Parent $PSScriptRoot
$gitInstallUrl = 'https://git-scm.com/download/win'
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ===========================================================================
# Helpers
# ===========================================================================

function Read-TextFile([string]$Path) {
    return [System.IO.File]::ReadAllText($Path)
}

function Write-TextFile([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, $Utf8NoBom)
}

function Update-FileInPlace {
    param([string]$Path, [scriptblock]$Transform)
    if (-not (Test-Path $Path)) { return }
    $text = Read-TextFile $Path
    $new = & $Transform $text
    if ($new -ne $text) { Write-TextFile $Path $new }
}

function Update-TreeInPlace {
    param([string[]]$Roots, [scriptblock]$Transform)
    foreach ($root in $Roots) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem -Path $root -Recurse -Filter '*.cs' -File | ForEach-Object {
            $text = Read-TextFile $_.FullName
            $new = & $Transform $text
            if ($new -ne $text) { Write-TextFile $_.FullName $new }
        }
    }
}

function Copy-TreeFresh([string]$Src, [string]$Dst) {
    if (Test-Path $Dst) { Remove-Item -Recurse -Force $Dst }
    $parent = Split-Path -Parent $Dst
    if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -Recurse -Force $Src $Dst
}

function Convert-ToLf([string]$Root) {
    if (-not (Test-Path $Root)) { return }
    Get-ChildItem -Path $Root -Recurse -File -Include '*.cs','*.csproj','*.json','*.xml','*.props','*.targets' | ForEach-Object {
        $bytes = [IO.File]::ReadAllBytes($_.FullName)
        $hasCR = $false
        foreach ($b in $bytes) { if ($b -eq 13) { $hasCR = $true; break } }
        if ($hasCR) {
            $t = [Text.Encoding]::UTF8.GetString($bytes) -creplace "`r`n", "`n"
            [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($t))
        }
    }
}

function Get-PinnedIlspycmdVersion {
    $manifest = Join-Path $repoRoot '.config/dotnet-tools.json'
    if (-not (Test-Path $manifest)) { return $null }
    $json = Get-Content $manifest -Raw | ConvertFrom-Json
    return $json.tools.ilspycmd.version
}

function Get-AcceptedIlspycmdPrefixes {
    $manifest = Join-Path $repoRoot '.config/ilspycmd-compat.json'
    if (-not (Test-Path $manifest)) { return @('10.1.0.', '10.1.1.') }
    $json = Get-Content $manifest -Raw | ConvertFrom-Json
    return @($json.acceptedPrefixes)
}

function Install-IlspycmdIfMissing {
    $dotnetTools = Join-Path $HOME '.dotnet/tools'
    if (Test-Path (Join-Path $dotnetTools 'ilspycmd.exe')) {
        $env:PATH = "$dotnetTools;$env:PATH"
    }

    $pinned = Get-PinnedIlspycmdVersion
    $existing = Get-Command ilspycmd -ErrorAction SilentlyContinue

    if ($existing) {
        if (-not $pinned) { return }
        $verLine = Invoke-NativeStep { & ilspycmd --version 2>$null | Select-Object -First 1 }
        $current = if ($verLine) { ($verLine -split '\s+')[1] } else { '' }
        $acceptablePrefix = Get-AcceptedIlspycmdPrefixes
        $currentOk = $acceptablePrefix | Where-Object { $current.StartsWith($_) }
        if ($currentOk) { return }
        Write-Host "ilspycmd $current is unsupported by this patch set, installing $pinned"
        Invoke-NativeStep { dotnet tool uninstall -g ilspycmd 2>&1 | Out-Null }
    }

    if ($pinned) {
        Write-Host "Installing ilspycmd $pinned"
        Invoke-NativeStep { dotnet tool install -g ilspycmd --version $pinned | Out-Null }
    } else {
        Write-Host "Installing ilspycmd (latest)"
        Invoke-NativeStep { dotnet tool install -g ilspycmd | Out-Null }
    }
    $env:PATH = "$dotnetTools;$env:PATH"
}

# --- fix-base-ctor-calls: base._002Ector(args)/this._002Ector(args) -> : base(args)/: this(args) ---
# Port of scripts/fix-base-ctor-calls.py. ILSpy occasionally decompiles the
# base/this constructor chain call using its IL name (.ctor, mangled to
# _002Ector) as a plain method-call statement instead of C#'s only legal
# syntax for it. The call can appear anywhere in the decompiled body; moving
# it to the initializer position is safe because real field initializers
# always run after the base constructor per C# spec, which is the order this
# produces either way.
function Repair-BaseCtorCalls {
    param([string[]]$Roots)

    $callRe = [regex]'[ \t]*(base|this)\._002Ector\(((?:[^()]|\([^()]*\))*)\);\n'
    $sigRe = [regex]'((?:public|private|protected|internal|static)[ \w]*\s\w+\(([^()]*)\)\s*\n)(\t*\{\n)'

    function local:Invoke-FixOnce([string]$text) {
        foreach ($sigM in $sigRe.Matches($text)) {
            $bodyStart = $sigM.Index + $sigM.Length
            $depth = 1
            $j = $bodyStart
            while ($depth -gt 0 -and $j -lt $text.Length) {
                if ($text[$j] -eq '{') { $depth++ }
                elseif ($text[$j] -eq '}') { $depth-- }
                $j++
            }
            $body = $text.Substring($bodyStart, $j - $bodyStart)
            $callM = $callRe.Match($body)
            if (-not $callM.Success) { continue }
            $kind = $callM.Groups[1].Value
            $ctorArgs = $callM.Groups[2].Value
            $newBody = $body.Substring(0, $callM.Index) + $body.Substring($callM.Index + $callM.Length)
            $header = $sigM.Groups[1].Value
            $braceLine = $sigM.Groups[3].Value
            $newHeader = $header.TrimEnd("`n") + "`n`t`t: $kind($ctorArgs)`n"
            $newText = $text.Substring(0, $sigM.Index) + $newHeader + $braceLine + $newBody + $text.Substring($j)
            return , @($newText, $true)
        }
        return , @($text, $false)
    }

    $files = @()
    foreach ($root in $Roots) {
        if (Test-Path $root) { $files += Get-ChildItem -Path $root -Recurse -Filter '*.cs' -File }
    }

    $totalFilesChanged = 0
    foreach ($file in $files) {
        $text = Read-TextFile $file.FullName
        if (-not $text.Contains('_002Ector(')) { continue }
        $original = $text
        for ($i = 0; $i -lt 50; $i++) {
            $result = Invoke-FixOnce $text
            $text = $result[0]
            if (-not $result[1]) { break }
        }
        if ($text -ne $original) {
            Write-TextFile $file.FullName $text
            $totalFilesChanged++
        }
    }
    Write-Host "Rewrote base/this constructor calls in $totalFilesChanged file(s)."
}

# --- fix-event-reads: bare/this-qualified reads of a custom-accessor event's
# current value (null checks, casts, GetInvocationList()) -> its m_<Name>
# backing field. Port of scripts/fix-event-reads.py. ---
function Repair-EventReads {
    param([string[]]$Roots)

    $stringRe = [regex]'"(?:[^"\\]|\\.)*"'

    $files = @()
    foreach ($root in $Roots) {
        if (Test-Path $root) { $files += Get-ChildItem -Path $root -Recurse -Filter '*.cs' -File }
    }

    $totalFilesChanged = 0
    $totalSubs = 0

    foreach ($file in $files) {
        $text = Read-TextFile $file.FullName

        $backing = @{}
        foreach ($m in [regex]::Matches($text, 'private\s+[\w<>,\.\[\]\s]+?\s+m_(\w+);')) {
            $backing[$m.Groups[1].Value] = $true
        }
        if ($backing.Count -eq 0) { continue }

        # Async state machines capture `this` into a `_003C_003E4__this` field and
        # commonly alias it to a locally-scoped variable at the top of MoveNext.
        # That variable IS `this` for this class, so qualifying an event read
        # through it needs the same rewrite as `this.Name`.
        $allowedQualifiers = @{ 'this' = $true }
        foreach ($m in [regex]::Matches($text, '\b(\w+)\s*=\s*[\w.]*_003C_003E4__this;')) {
            $allowedQualifiers[$m.Groups[1].Value] = $true
        }

        $lines = New-Object System.Collections.Generic.List[string]
        $lines.AddRange([string[]]($text -split "`n"))
        $fileChanged = $false

        $names = $backing.Keys | Sort-Object -Property Length -Descending

        foreach ($name in $names) {
            $escaped = [regex]::Escape($name)
            $declRe = [regex]::new("\bevent\s+\S.*\b$escaped\b")
            if (-not $declRe.IsMatch($text)) { continue }
            # Match an optional `qualifier.` prefix so it can be inspected: only
            # rewrite a bare reference (no qualifier) or one qualified by `this`
            # or a this-alias -- never `SomeOtherType.Name`, a different member
            # (e.g. an enum value) that happens to share the event's name.
            $readRe = [regex]::new("\b(?:(\w+)\.)?$escaped\b(?!\s*[+\-]=)")

            for ($i = 0; $i -lt $lines.Count; $i++) {
                $line = $lines[$i]
                if ($declRe.IsMatch($line)) { continue }

                $spans = @()
                foreach ($sm in $stringRe.Matches($line)) {
                    $spans += , @($sm.Index, ($sm.Index + $sm.Length))
                }

                $out = New-Object System.Text.StringBuilder
                $last = 0
                $n = 0
                foreach ($m in $readRe.Matches($line)) {
                    $inString = $false
                    foreach ($sp in $spans) {
                        if ($sp[0] -le $m.Index -and $m.Index -lt $sp[1]) { $inString = $true; break }
                    }
                    if ($inString) { continue }
                    $hasQualifier = $m.Groups[1].Success
                    $qualifier = $m.Groups[1].Value
                    if ($hasQualifier -and -not $allowedQualifiers.ContainsKey($qualifier)) { continue }
                    $prefix = if ($hasQualifier) { "$qualifier." } else { '' }
                    [void]$out.Append($line.Substring($last, $m.Index - $last))
                    [void]$out.Append($prefix + 'm_' + $name)
                    $last = $m.Index + $m.Length
                    $n++
                }
                [void]$out.Append($line.Substring($last))
                if ($n -gt 0) {
                    $lines[$i] = $out.ToString()
                    $fileChanged = $true
                    $totalSubs += $n
                }
            }
        }

        if ($fileChanged) {
            Write-TextFile $file.FullName ([string]::Join("`n", $lines))
            $totalFilesChanged++
        }
    }

    Write-Host "Rewrote $totalSubs event read(s) across $totalFilesChanged file(s)."
}

# --- value-type constructor pattern: `TYPE val = default(TYPE); val._002Ector(args);`
# -> `TYPE val = new TYPE(args);`. Value-type sibling of the ref-cast pattern
# handled inline further down; needs its own paren/brace-matching pass since it
# has no `((Type)(ref var))` wrapper for a regex to anchor on. ---
function Repair-ValueTypeConstructorCalls {
    param([string[]]$Roots)

    $declRe = [regex]'([ \t]*)([\w<>,.\s]+?)\s+(\w+)\s*=\s*default\(\2\);\n'

    function local:Find-MatchingParen([string]$s, [int]$openIdx) {
        $depth = 1
        $j = $openIdx + 1
        while ($depth -gt 0) {
            if ($s[$j] -eq '(') { $depth++ }
            elseif ($s[$j] -eq ')') { $depth-- }
            $j++
        }
        return $j
    }

    $files = @()
    foreach ($root in $Roots) {
        if (Test-Path $root) { $files += Get-ChildItem -Path $root -Recurse -Filter '*.cs' -File }
    }

    $total = 0
    foreach ($file in $files) {
        $text = Read-TextFile $file.FullName
        if (-not $text.Contains('_002Ector(')) { continue }

        $sb = New-Object System.Text.StringBuilder
        $pos = 0
        $changed = $false

        foreach ($m in $declRe.Matches($text)) {
            if ($m.Index -lt $pos) { continue }
            $indent = $m.Groups[1].Value
            $typename = $m.Groups[2].Value
            $varname = $m.Groups[3].Value
            $callMarker = "$varname._002Ector("
            $afterStart = $m.Index + $m.Length
            $after = $text.Substring($afterStart)
            $stripped = $after.TrimStart("`t", ' ')
            if (-not $stripped.StartsWith($callMarker)) { continue }
            $callStart = $afterStart + ($after.Length - $stripped.Length)
            $parenOpen = $text.IndexOf('(', $callStart)
            $parenClose = Find-MatchingParen $text $parenOpen
            if ($text[$parenClose] -ne ';') { continue }
            $ctorArgs = $text.Substring($parenOpen + 1, $parenClose - 1 - ($parenOpen + 1))
            [void]$sb.Append($text.Substring($pos, $m.Index - $pos))
            [void]$sb.Append("$indent$typename $varname = new $typename($ctorArgs);`n")
            $pos = $parenClose + 2
            $changed = $true
            $total++
        }

        if ($changed) {
            [void]$sb.Append($text.Substring($pos))
            Write-TextFile $file.FullName $sb.ToString()
        }
    }

    Write-Host "Rewrote $total value-type constructor call(s)."
}

function Invoke-GitApplyOptimumPatch {
    param([string]$PatchPath, [string]$DirArg)

    function local:Invoke-Apply([string[]]$ExtraArgs) {
        $applyArgs = @('apply', '--whitespace=nowarn')
        if ($DirArg) { $applyArgs += $DirArg }
        $applyArgs += $ExtraArgs
        $applyArgs += $PatchPath
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & git @applyArgs 2>&1
        $code = $LASTEXITCODE
        $ErrorActionPreference = $prevEAP
        return , @($code, ($out -join "`n"))
    }

    $r = Invoke-Apply @()
    if ($r[0] -eq 0) { return @{ Success = $true; Output = '' } }
    $firstOutput = $r[1]

    $r2 = Invoke-Apply @('-p0')
    if ($r2[0] -eq 0) { return @{ Success = $true; Output = 'applied with -p0' } }

    if ($DirArg) {
        $applyArgs = @('apply', '--whitespace=nowarn', '-p0', $PatchPath)
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out3 = & git @applyArgs 2>&1
        $code3 = $LASTEXITCODE
        $ErrorActionPreference = $prevEAP
        if ($code3 -eq 0) { return @{ Success = $true; Output = 'applied with root -p0' } }
    }

    return @{ Success = $false; Output = $firstOutput }
}

# ===========================================================================
# Main
# ===========================================================================

Push-Location $repoRoot
try {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "Git not found. Install Git for Windows from $gitInstallUrl and run bootstrap again."
    }

    # Vanilla client files always live under .vanilla/win-x64/, matching the
    # convention used by bootstrap.sh, install-windows.ps1, package.ps1, and
    # _hostcaps.ps1.
    $vanillaDir = Join-Path $repoRoot '.vanilla/win-x64'
    $winVanillaDir = Join-Path $vanillaDir 'vintagestory'
    $snapshotDir = Join-Path $repoRoot 'build/snapshot'
    $zipCacheDir = Join-Path $vanillaDir '../archives'
    $sourcesDir = Join-Path $repoRoot 'sources'

    if ($Refresh -and (Test-Path $vanillaDir)) { Remove-Item -Recurse -Force $vanillaDir }
    if ($Refresh -and (Test-Path $snapshotDir)) { Remove-Item -Recurse -Force $snapshotDir }

    # --- 1. Obtain vanilla client files ---
    # Three modes:
    #   a) .vanilla/win-x64/vintagestory already has Vintagestory.exe -> skip
    #   b) -ClientArchive '__skip__' -> the caller (install-windows.ps1) already
    #      placed .vanilla via junction; verify it exists and move on
    #   c) Normal: download the installer and extract with innounp
    $skipDownload = ($ClientArchive -eq '__skip__')
    $freshExtract = $false
    if (Test-Path (Join-Path $winVanillaDir 'Vintagestory.exe')) {
        Write-Host "Using existing $winVanillaDir"
    } elseif ($skipDownload) {
        if (-not (Test-Path (Join-Path $winVanillaDir 'Vintagestory.exe'))) {
            throw ".vanilla/win-x64/vintagestory not found and download was skipped. Provide a VS install path."
        }
    } else {
        New-Item -ItemType Directory -Force -Path $zipCacheDir | Out-Null
        $exeName = "vs_install_win-x64_$Version.exe"
        if (-not $ClientArchive) {
            $ClientArchive = Join-Path $zipCacheDir $exeName
        }
        if (-not (Test-Path $ClientArchive)) {
            $url = "https://cdn.vintagestory.at/gamefiles/stable/$exeName"
            Write-Host "Downloading $url (~570MB)"
            Invoke-NativeStep { curl.exe -L --fail --progress-bar -o $ClientArchive $url }
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
        } else {
            Write-Host "Using cached $ClientArchive"
        }

        # Extract using innounp (download if missing). Supports InnoSetup 6.x.
        $toolsDir = Join-Path $repoRoot '.tools'
        $innounp = Join-Path $toolsDir 'innounp.exe'
        if (-not (Test-Path $innounp)) {
            New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
            $innounpZip = Join-Path $toolsDir 'innounp-2.zip'
            Write-Host "Downloading innounp"
            Invoke-NativeStep { curl.exe -L --fail --silent -o $innounpZip "https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/ui_2_2_9/innounp-2.zip" }
            if ($LASTEXITCODE -ne 0) { throw "Download failed: innounp-2.zip" }
            Expand-Archive -Path $innounpZip -DestinationPath $toolsDir -Force
            $found = Get-ChildItem -Path $toolsDir -Recurse -Filter 'innounp.exe' | Select-Object -First 1
            if ($found -and $found.FullName -ne $innounp) {
                Copy-Item -Force $found.FullName $innounp
            }
            Remove-Item -Force $innounpZip -ErrorAction SilentlyContinue
        }

        $extractTarget = $winVanillaDir
        New-Item -ItemType Directory -Force -Path $extractTarget | Out-Null
        Write-Host "Extracting with innounp to $extractTarget"
        Get-Process -Name 'innounp' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        $innounpProc = Start-Process -FilePath $innounp -ArgumentList "-x -d`"$extractTarget`" -c`"{app}`" `"$ClientArchive`"" -NoNewWindow -PassThru
        $exited = $innounpProc.WaitForExit(300000)
        if (-not $exited) {
            $innounpProc.Kill()
            Get-Process -Name 'innounp' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            throw "innounp timed out after 5 minutes. Kill any innounp.exe in Task Manager and retry."
        }
        $innounpExitCode = $innounpProc.ExitCode
        $appDir = Join-Path $extractTarget '{app}'
        if (Test-Path $appDir) {
            Get-ChildItem -Path $appDir | Move-Item -Destination $extractTarget -Force
            Remove-Item -Force $appDir
        }
        if ($innounpExitCode -ne 0 -and -not (Test-Path (Join-Path $extractTarget 'Vintagestory.exe'))) {
            throw "innounp failed (exit $innounpExitCode)."
        }
        if ($innounpExitCode -ne 0) {
            Write-Warning "innounp exited with code $innounpExitCode after extracting Vintagestory.exe; continuing."
        }
        if (-not (Test-Path (Join-Path $extractTarget 'Vintagestory.exe'))) {
            throw "Extraction failed: Vintagestory.exe not found"
        }
        $freshExtract = $true
        Write-Host "Extraction complete."
    }

    # Validate the vanilla tree before building against it. A tolerated
    # partial innounp extraction (nonzero exit above) or a stale .vanilla
    # cache left by an older failed run carries zero-byte or truncated
    # assets that only surface later, in-game, as opaque GL crashes
    # ("blur.vsh ... unexpected $end at <EOF>"). Catch them here instead.
    $vanillaAssets = Join-Path $winVanillaDir 'assets'
    $corrupt = @(Get-ChildItem -Path $vanillaAssets -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -eq 0 -and $_.Name -notlike 'version-*.txt' })
    $vanillaShaders = Join-Path $vanillaAssets 'game/shaders'
    if (Test-Path $vanillaShaders) {
        $corrupt += @(Get-ChildItem -Path $vanillaShaders -File |
            Where-Object { $_.Extension -in '.vsh', '.fsh', '.gsh' } |
            Where-Object { (Get-Content $_.FullName -Raw) -notmatch 'void\s+main' })
    } else {
        throw "Vanilla client at $winVanillaDir has no assets/game/shaders; the extraction or install is incomplete."
    }
    if ($corrupt.Count -gt 0) {
        $names = ($corrupt | Select-Object -First 10 | ForEach-Object { $_.FullName }) -join "`n  "
        if ($freshExtract) {
            # Wipe the poisoned extraction so the next run re-extracts
            # instead of reusing it via the "Using existing" fast path.
            Remove-Item -Recurse -Force $winVanillaDir -ErrorAction SilentlyContinue
            throw "innounp produced $($corrupt.Count) empty/truncated file(s); the extraction was discarded. Re-run to retry.`n  $names"
        }
        throw "Vanilla client files are corrupt ($($corrupt.Count) empty/truncated file(s)):`n  $names`nIf $winVanillaDir is Optimum's own cache, delete it and retry; if it points at your Vintage Story install, repair or reinstall Vintage Story $Version first."
    }

    # --- 2. Decompile closed-source DLLs ---
    Install-IlspycmdIfMissing

    # Produces .csproj projects in build/snapshot/{name}/ copied to build/{name}/.
    $decompileTargets = [ordered]@{
        'VintagestoryLib' = 'build/VintagestoryLib'
        'Vintagestory'    = 'build/Vintagestory'
    }

    foreach ($dllBase in $decompileTargets.Keys) {
        $workDir = $decompileTargets[$dllBase]
        $dllPath = Get-ChildItem -Path $winVanillaDir -Recurse -Filter "$dllBase.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $dllPath) { Write-Warning "Skipping $dllBase.dll (not found)"; continue }

        $out = Join-Path $snapshotDir $dllBase
        if (-not (Test-Path $out) -or $Refresh) {
            $verLine = Invoke-NativeStep { & ilspycmd --version 2>$null | Select-Object -First 1 }
            Write-Host "Decompiling $dllBase.dll with $verLine"
            if (Test-Path $out) { Remove-Item -Recurse -Force $out }
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            Invoke-NativeStep { ilspycmd $dllPath.FullName --project -o $out 2>$null | Out-Null }
            # Don't trust ilspycmd's exit code alone: it has a known bug
            # (icsharpcode/ILSpy#3101) where it reports failure via a bogus
            # "not using the latest version" self-check even after a fully
            # successful decompile. Verify the real artifact instead.
            $producedProjects = @(Get-ChildItem -Path $out -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($producedProjects.Count -eq 0) {
                throw "ilspycmd produced no .csproj for $dllBase.dll (exit code $LASTEXITCODE). Delete $out and retry, or reinstall ilspycmd."
            }
            $producedProjects | ForEach-Object {
                Update-FileInPlace $_.FullName { param($t) $t -creplace '<LangVersion>15\.0</LangVersion>', '<LangVersion>latest</LangVersion>' }
            }
        }

        Copy-TreeFresh $out (Join-Path $repoRoot $workDir)
    }

    # --- 3. Clone compile-target forks (VintagestoryApi, Cairo, ...) ---
    $forksFile = Join-Path $repoRoot 'forks.json'
    if (Test-Path $forksFile) {
        $cfg = Get-Content $forksFile -Raw | ConvertFrom-Json
        $forks = @($cfg.compile | Where-Object { $_.source -eq 'clone' })

        foreach ($fork in $forks) {
            $name = $fork.name
            $base = Join-Path $snapshotDir $name

            if (-not (Test-Path $base) -or $Refresh) {
                if (Test-Path $base) { Remove-Item -Recurse -Force $base }
                Write-Host "Cloning $name at $($fork.ref)"
                Invoke-NativeStep { git clone --quiet $fork.url $base 2>$null }
                if ($LASTEXITCODE -ne 0) { throw "git clone failed for $name ($($fork.url))." }
                Invoke-NativeStep { git -C $base checkout --quiet $fork.ref }
                if ($LASTEXITCODE -ne 0) { throw "git checkout $($fork.ref) failed for $name." }
                Remove-Item -Recurse -Force (Join-Path $base '.git')
                Convert-ToLf $base
            }

            $dst = Join-Path $repoRoot $name
            Copy-TreeFresh $base $dst
            Convert-ToLf $dst
        }

        # --- 3b. Clone reference repos (for code reading, not compilation). ---
        $refRoots = @($cfg.reference | Where-Object { $_ })
        if ($refRoots.Count -gt 0) {
            $refDir = Join-Path $repoRoot 'ref/source'
            foreach ($r in $refRoots) {
                $dest = Join-Path $refDir $r.name
                if (-not (Test-Path $dest)) {
                    Write-Host "Cloning reference: $($r.name)"
                    Invoke-NativeStep { git clone --quiet --depth=1 $r.url $dest 2>$null }
                    Invoke-NativeStep { git -C $dest checkout --quiet $r.ref 2>$null }
                }
            }
        }
    }

    # --- 6. Post-decompile fixups (csproj rewrites, ambiguity resolution). ---
    Write-Host "Applying post-decompile fixups..."

    # Normalize CRLF across all decompiled .cs files FIRST (ilspycmd on Windows
    # emits CRLF, and several fixups below anchor on `^`/`\n` which assumes LF).
    Get-ChildItem -Path (Join-Path $repoRoot 'build') -Recurse -Filter '*.cs' -File | ForEach-Object {
        $bytes = [IO.File]::ReadAllBytes($_.FullName)
        $hasCR = $false
        foreach ($b in $bytes) { if ($b -eq 13) { $hasCR = $true; break } }
        if ($hasCR) {
            $t = [Text.Encoding]::UTF8.GetString($bytes) -creplace "`r`n", "`n" -creplace "`r", "`n"
            [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($t))
        }
    }

    # 6a. Rewrite VintagestoryLib.csproj with HintPaths to .vanilla DLLs.
    $libCsproj = Join-Path $repoRoot 'build/VintagestoryLib/VintagestoryLib.csproj'
    $libCsprojContent = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>VintagestoryLib</AssemblyName>
    <GenerateAssemblyInfo>False</GenerateAssemblyInfo>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>True</AllowUnsafeBlocks>
    <CheckForOverflowUnderflow>False</CheckForOverflowUnderflow>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\VintagestoryApi\VintagestoryAPI.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="cairo-sharp"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\cairo-sharp.dll</HintPath></Reference>
    <Reference Include="protobuf-net"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\protobuf-net.dll</HintPath></Reference>
    <Reference Include="Newtonsoft.Json"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Newtonsoft.Json.dll</HintPath></Reference>
    <Reference Include="CommandLine"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\CommandLine.dll</HintPath></Reference>
    <Reference Include="SkiaSharp"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\SkiaSharp.dll</HintPath></Reference>
    <Reference Include="Open.Nat"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Open.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Nat"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Mono.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Cecil"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Mono.Cecil.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Microsoft.CodeAnalysis.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis.CSharp"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Microsoft.CodeAnalysis.CSharp.dll</HintPath></Reference>
    <Reference Include="ICSharpCode.SharpZipLib"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\ICSharpCode.SharpZipLib.dll</HintPath></Reference>
    <Reference Include="Microsoft.Data.Sqlite"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Microsoft.Data.Sqlite.dll</HintPath></Reference>
    <Reference Include="0Harmony"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\0Harmony.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Desktop"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Windowing.Desktop.dll</HintPath></Reference>
    <Reference Include="OpenTK.Audio.OpenAL"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Audio.OpenAL.dll</HintPath></Reference>
    <Reference Include="OpenTK.Mathematics"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Mathematics.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Common"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Windowing.Common.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.GraphicsLibraryFramework"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Windowing.GraphicsLibraryFramework.dll</HintPath></Reference>
    <Reference Include="DnsClient"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\DnsClient.dll</HintPath></Reference>
    <Reference Include="OpenTK.Graphics"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Graphics.dll</HintPath></Reference>
    <Reference Include="csvorbis"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\csvorbis.dll</HintPath></Reference>
    <Reference Include="csogg"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\csogg.dll</HintPath></Reference>
    <Reference Include="xplatforminterface"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\xplatforminterface.dll</HintPath></Reference>
  </ItemGroup>
</Project>
'@
    Write-TextFile $libCsproj ($libCsprojContent + "`n")

    # 6b. Fix VSEssentials/VSSurvivalMod: Tavis.JsonPatch PackageReference -> local DLL Reference.
    foreach ($proj in @('VSEssentials/VSEssentialsMod.csproj', 'VSSurvivalMod/VSSurvivalMod.csproj')) {
        Update-FileInPlace (Join-Path $repoRoot $proj) {
            param($t)
            $t -creplace '<PackageReference Include="Tavis\.JsonPatch" Version="[^"]*"\s*/>', '<Reference Include="Tavis.JsonPatch"><HintPath>..\.vanilla\win-x64\vintagestory\Lib\Tavis.JsonPatch.dll</HintPath><Private>false</Private></Reference>'
        }
    }

    # 6c. Fix Mapping ambiguity in ServerSystemUpnp.
    $upnp = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/ServerSystemUpnp.cs'
    Update-FileInPlace $upnp {
        param($t)
        $t = [regex]::Replace($t, '(?m)^\tprivate Mapping mapping;', "`tprivate Open.Nat.Mapping mapping;")
        $t = [regex]::Replace($t, '(?m)^\tprivate Mapping mappingUdp;', "`tprivate Open.Nat.Mapping mappingUdp;")
        $t = [regex]::Replace($t, '(?m)^\tprivate Mapping monoNatMapping;', "`tprivate Mono.Nat.Mapping monoNatMapping;")
        $t = [regex]::Replace($t, '(?m)^\tprivate Mapping monoNatMappingUdp;', "`tprivate Mono.Nat.Mapping monoNatMappingUdp;")
        $t = $t -creplace 'mapping = new Mapping\(\(Protocol\)0', 'mapping = new Open.Nat.Mapping((Open.Nat.Protocol)0'
        $t = $t -creplace 'mappingUdp = new Mapping\(\(Protocol\)1', 'mappingUdp = new Open.Nat.Mapping((Open.Nat.Protocol)1'
        $t = $t -creplace 'monoNatMapping = new Mapping\(\(Protocol\)0', 'monoNatMapping = new Mono.Nat.Mapping((Mono.Nat.Protocol)0'
        $t = $t -creplace 'monoNatMappingUdp = new Mapping\(\(Protocol\)1', 'monoNatMappingUdp = new Mono.Nat.Mapping((Mono.Nat.Protocol)1'
        $t
    }

    # 6d. Fix ModContainer CustomAttributeNamedArgument ambiguity.
    $modcont = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ModContainer.cs'
    Update-FileInPlace $modcont {
        param($t)
        [regex]::Replace($t, '(?<!\.)(?<!Mono\.Cecil\.)(?<!System\.Reflection\.)CustomAttributeNamedArgument(?!\w)', 'Mono.Cecil.CustomAttributeNamedArgument')
    }

    Write-Host "Post-decompile fixups done."

    # 6e. Final pass: catch remaining decompiler artifacts (ref-casts, op_Implicit,
    #     ambiguous types, GeneratedRegex) across the entire build tree.
    Update-TreeInPlace @((Join-Path $repoRoot 'build')) {
        param($t)
        $t = [regex]::Replace($t, '\(\(([\w.]+)\)\(ref (\w+)\)\)\._002Ector\(', '$2 = new $1(')
        $t = [regex]::Replace($t, '\(\(([\w.]+)\)\(ref ([\w.]+)\)\)', '$2')
        $t = [regex]::Replace($t, '\([^)]*<[^>]+>\)\(ref (\w+)\)', '$1')
        $t = [regex]::Replace($t, 'JToken\.op_Implicit\((.+?)\)', '(JToken)($1)')
        $t
    }

    # Fix GeneratedRegex: replace decompiled source-generator stubs with new Regex().
    $generatedRegexPattern = "\t\[GeneratedRegex\(""([^""]+)""\)\]\n\t\[GeneratedCode\([^\]]+\)\]\n\tprivate static Regex (\w+)\(\)\n\t\{\n\t\treturn [^;]+;\n\t\}"
    Update-TreeInPlace @((Join-Path $repoRoot 'build')) {
        param($t)
        [regex]::Replace($t, $generatedRegexPattern, {
            param($m)
            "`tprivate static Regex $($m.Groups[2].Value)()`n`t{`n`t`treturn new Regex(""$($m.Groups[1].Value)"", RegexOptions.Compiled);`n`t}"
        })
    }
    # The fixup above bypasses every [GeneratedRegex] stub, so the source-generated
    # regex-matching implementation classes ILSpy decompiles alongside them are now
    # 100% dead code, never referenced from anywhere. Delete rather than fix artifacts
    # in code nothing calls.
    Get-ChildItem -Path (Join-Path $repoRoot 'build') -Recurse -Directory -Filter 'System.Text.RegularExpressions.Generated' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName }

    # Fix MouseWheelEventArgs ambiguity (OpenTK vs Vintagestory.API.Client).
    $cpw = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs'
    Update-FileInPlace $cpw {
        param($t)
        $t = $t -creplace 'private void Mouse_WheelChanged\(MouseWheelEventArgs e\)', 'private void Mouse_WheelChanged(OpenTK.Windowing.Common.MouseWheelEventArgs e)'
        $t = $t -creplace 'MouseWheelEventArgs e2 = new MouseWheelEventArgs', 'Vintagestory.API.Client.MouseWheelEventArgs e2 = new Vintagestory.API.Client.MouseWheelEventArgs'
        $t
    }

    # 6g. Additional ILSpy artifacts not covered by the generic regex passes.
    $lib = Join-Path $repoRoot 'build/VintagestoryLib'

    # Path ambiguity (Cairo.Path vs System.IO.Path) in GUI screens.
    Get-ChildItem -Path (Join-Path $lib 'Vintagestory.Client') -Filter 'GuiScreen*.cs' -File -ErrorAction SilentlyContinue | ForEach-Object {
        Update-FileInPlace $_.FullName { param($t) [regex]::Replace($t, '(?<![.\w])Path\.(?=DirectorySeparatorChar|Combine|GetTempPath|GetFileName|GetExtension|GetDirectoryName|GetFullPath)', 'System.IO.Path.') }
    }
    Update-FileInPlace (Join-Path $lib 'Vintagestory.Client/ScreenManager.cs') {
        param($t) [regex]::Replace($t, '(?<![.\w])Path\.(?=DirectorySeparatorChar|Combine|GetTempPath|GetFileName|GetExtension|GetDirectoryName|GetFullPath)', 'System.IO.Path.')
    }

    # csvorbis.Block ambiguity in OggDecoder.
    Update-FileInPlace (Join-Path $lib 'Vintagestory.Client.NoObf/OggDecoder.cs') {
        param($t)
        $t = $t -creplace '\bBlock val', 'csvorbis.Block val'
        $t = $t -creplace '\bnew Block\(', 'new csvorbis.Block('
        $t
    }

    # SystemRenderSunMoon: GL.GenQueries/GetQueryObject needs out not ref.
    Update-FileInPlace (Join-Path $lib 'Vintagestory.Client.NoObf/SystemRenderSunMoon.cs') {
        param($t)
        $t = [regex]::Replace($t, 'GL\.GenQueries\((\d+), ref (\w+)\)', 'GL.GenQueries($1, out $2)')
        $t = [regex]::Replace($t, 'GL\.GetQueryObject\(([^,]+), ([^,]+), ref (\w+)\)', 'GL.GetQueryObject($1, $2, out $3)')
        $t
    }

    # SystemRenderOITLayers: RuntimeFieldHandle (ILSpy cannot decompile inline array init).
    $oit = Join-Path $lib 'Vintagestory.Client.NoObf/SystemRenderOITLayers.cs'
    Update-FileInPlace $oit {
        param($t) [regex]::Replace($t, 'RuntimeHelpers\.InitializeArray.*RuntimeFieldHandle.*LdMemberToken.*', '// ILSpy: inline array init not supported')
    }
    # DrawBuffersEnum inline array fixups: ILSpy emits zeroed arrays because it cannot
    # decompile RuntimeFieldHandle-based array initializers. Inject the correct enum
    # values from vanilla.
    Update-FileInPlace $oit {
        param($t)
        [regex]::Replace($t, 'DrawBuffersEnum\[\] array = new DrawBuffersEnum\[6\];\s*// ILSpy: inline array init not supported\s*DrawBuffersEnum\[\] array2 = \(DrawBuffersEnum\[\]\)\(object\)array;', 'DrawBuffersEnum[] array2 = new DrawBuffersEnum[6] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3, DrawBuffersEnum.ColorAttachment4, DrawBuffersEnum.ColorAttachment5 };')
    }

    # ClientPlatformWindows: remaining RuntimeFieldHandle occurrences + VSyncMode bool cast
    # + ErrorCode ambiguity + BufferAccessMask|int cast + Keys->int cast + FramebufferErrorCode
    # int cast + ref->out GL calls + Path ambiguity.
    Update-FileInPlace $cpw {
        param($t)
        $t = [regex]::Replace($t, 'RuntimeHelpers\.InitializeArray\(array[0-9]*, \(RuntimeFieldHandle\)/\*OpCode not supported: LdMemberToken\*/\);', '// ILSpy: inline array init not supported')
        # DrawBuffersEnum inline array fixups: replace zeroed arrays with correct initializers.
        $t = [regex]::Replace($t, 'DrawBuffersEnum\[\] (\w+) = new DrawBuffersEnum\[4\];\s*// ILSpy: inline array init not supported\s*DrawBuffersEnum\[\] (\w+) = \(DrawBuffersEnum\[\]\)\(object\)\1;', 'DrawBuffersEnum[] $2 = new DrawBuffersEnum[4] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 };')
        $t = [regex]::Replace($t, 'DrawBuffersEnum\[\] (\w+) = new DrawBuffersEnum\[3\];\s*// ILSpy: inline array init not supported\s*DrawBuffersEnum\[\] (\w+) = \(DrawBuffersEnum\[\]\)\(object\)\1;', 'DrawBuffersEnum[] $2 = new DrawBuffersEnum[3] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2 };')
        $t = [regex]::Replace($t, 'DrawBuffersEnum\[\] (\w+) = new DrawBuffersEnum\[4\];\s*// ILSpy: inline array init not supported\s*(\w+) = \(DrawBuffersEnum\[\]\)\(object\)\1;', '$2 = new DrawBuffersEnum[4] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 };')
        $t = $t -creplace '\(VSyncMode\)enabled', 'enabled ? VSyncMode.On : VSyncMode.Off'
        $t = [regex]::Replace($t, '(?<![.\w])ErrorCode(?= error| val)', 'OpenTK.Graphics.OpenGL.ErrorCode')
        $t = $t -creplace 'error != ErrorCode\.NoError', 'error != OpenTK.Graphics.OpenGL.ErrorCode.NoError'
        # FramebufferErrorCode: cast int result and fix switch subtraction
        $t = $t -creplace '\*\(FramebufferErrorCode\*\)\(&val\)', '((FramebufferErrorCode)val)'
        $t = $t -creplace 'switch \(val - 36053\)', 'switch ((int)val - 36053)'
        # GL ref -> out: for query/gen methods with nested casts in args (not ClearBuffer which uses ref as input)
        $t = [regex]::Replace($t, '(GL\.(?:Get\w+|Gen\w+)\(.*?), ref (\w+)\)', '$1, out $2)')
        # Path ambiguity in ClientPlatformWindows
        $t = [regex]::Replace($t, '(?<![.\w])Path\.(?=DirectorySeparatorChar|Combine|GetTempPath|GetFileName|GetExtension|GetDirectoryName|GetFullPath)', 'System.IO.Path.')
        # BufferAccessMask: val | int needs cast on the int literal or wrap in (int)val
        $t = [regex]::Replace($t, '\(BufferAccessMask\)\(val \| (0x[0-9a-fA-F]+)\)', '(BufferAccessMask)((int)val | $1)')
        # Keys enum to int: both the dictionary result AND the indexer need casts
        $t = $t -creplace '= KeyConverter\.NewKeysToGlKeys\[e\.Key\]', '= (int)KeyConverter.NewKeysToGlKeys[(int)e.Key]'
        $t
    }

    # LoadedSoundNative.cs + AudioOpenAl.cs - OpenTK AL ref->out, EFX using alias,
    # ALFormat int cast. AL.GetSource/GetBuffer use 'out' not 'ref'; EFX needs a
    # using alias; soundFormat arithmetic needs (int) cast; Vector3 constructors
    # use 'new' not _002Ector.
    $lsn = Join-Path $lib 'Vintagestory.Client/LoadedSoundNative.cs'
    Update-FileInPlace $lsn {
        param($t)
        if ($t -cnotmatch 'using EFX = ') {
            $t = $t -creplace '(?m)^using OpenTK\.Audio\.OpenAL;$', "using OpenTK.Audio.OpenAL;`nusing EFX = OpenTK.Audio.OpenAL.ALC.EFX;"
        }
        $t = [regex]::Replace($t, 'AL\.GetSource\(([^,]+), ([^,]+), ref ', 'AL.GetSource($1, $2, out ')
        $t = [regex]::Replace($t, 'AL\.GetBuffer\(([^,]+), ([^,]+), ref ', 'AL.GetBuffer($1, $2, out ')
        $t = $t -creplace 'soundFormat - 4354', '((int)soundFormat - 4354)'
        $t = [regex]::Replace($t, '\(\(Vector3\)\(ref (\w+)\)\)\._002Ector\(', '$1 = new Vector3(')
        $t
    }

    # AudioOpenAl.cs also uses EFX but the decompiler drops the using alias.
    $aoa = Join-Path $lib 'Vintagestory.Client/AudioOpenAl.cs'
    Update-FileInPlace $aoa {
        param($t)
        if ($t -cnotmatch 'using EFX = ') {
            $t = $t -creplace '(?m)^using OpenTK\.Audio\.OpenAL;$', "using OpenTK.Audio.OpenAL;`nusing EFX = OpenTK.Audio.OpenAL.ALC.EFX;"
        }
        $t
    }

    # ClientProgram.cs - VSyncMode bool cast + ErrorCallback type alias.
    $cprog = Join-Path $lib 'Vintagestory.Client/ClientProgram.cs'
    Update-FileInPlace $cprog {
        param($t)
        if ($t -cnotmatch 'using ErrorCallback = ') {
            $t = $t -creplace '(?m)^using OpenTK\.Windowing\.GraphicsLibraryFramework;$', "using OpenTK.Windowing.GraphicsLibraryFramework;`nusing ErrorCallback = OpenTK.Windowing.GraphicsLibraryFramework.GLFWCallbacks.ErrorCallback;"
        }
        $t = $t -creplace '\(VSyncMode\)\(ClientSettings\.VsyncMode != 0\)', 'ClientSettings.VsyncMode != 0 ? VSyncMode.On : VSyncMode.Off'
        $t
    }

    # ClientPlatformWindows.cs - Ext.CheckFramebufferStatus -> GL.CheckFramebufferStatus,
    # pointer cast simplification, FramebufferErrorCode subtraction needs (int) cast.
    Update-FileInPlace $cpw {
        param($t)
        $t = $t -creplace 'Ext\.CheckFramebufferStatus', 'GL.CheckFramebufferStatus'
        $t = $t -creplace '\(\(object\)\(\*\(FramebufferErrorCode\*\)\(&val\)\)/\*cast due to constrained\. prefix\*/\)\.ToString\(\)', 'val.ToString()'
        $t = $t -creplace 'switch \(val - 36053\)', 'switch ((int)val - 36053)'
        $t = $t -creplace '\*\(ErrorCode\*\)\(&error\)\)', '*(OpenTK.Graphics.OpenGL.ErrorCode*)(&error))'
        $t
    }

    # base._002Ector(args)/this._002Ector(args) -> : base(args)/: this(args) constructor
    # initializers. Far more widespread than any one hand-fixed constructor, so this
    # runs project-wide.
    Repair-BaseCtorCalls -Roots @((Join-Path $repoRoot 'build/VintagestoryLib'), (Join-Path $repoRoot 'build/Vintagestory'))

    # Vintagestory.csproj - needs ProjectReference to VintagestoryLib (not vanilla DLL).
    # The per-OS entry class (ClientLinux/ClientWindows/ClientMac) uses the Vintagestory.Client namespace which lives in the VintagestoryLib project.
    $vsEntryCsproj = Join-Path $repoRoot 'build/Vintagestory/Vintagestory.csproj'
    Update-FileInPlace $vsEntryCsproj {
        param($t)
        $t = $t -creplace '<Reference Include="VintagestoryLib">', '<ProjectReference Include="..\VintagestoryLib\VintagestoryLib.csproj">'
        $t = [regex]::Replace($t, '<HintPath>[^<]*VintagestoryLib.dll</HintPath>', '')
        $t = $t -creplace '</Reference>', '</ProjectReference>'
        $t
    }

    # 6h: Restore serialization metadata lost by ILSpy.
    # ILSpy fails to decode attribute arguments for JsonObject(MemberSerialization.OptIn) and
    # ProtoContract(ImplicitFields = ImplicitFields.AllFields). Without these, Newtonsoft
    # serializes all public fields (breaking client/server config exchange) and protobuf-net
    # serializes 0 bytes (breaking animation packet delivery in multiplayer).
    Write-Host "Restoring serialization metadata..."

    # ProtoContract: animation/tag network packets need ImplicitFields.AllFields for protobuf-net
    foreach ($pf in @(
        "$lib/Vintagestory.Common.Network.Packets/AnimationPacket.cs",
        "$lib/Vintagestory.Common.Network.Packets/BulkAnimationPacket.cs",
        "$lib/Vintagestory.Common.Network.Packets/EntityTagPacket.cs",
        "$lib/Vintagestory.Common.Network.Packets/MountAnimationPacket.cs"
    )) {
        Update-FileInPlace $pf {
            param($t)
            $t = $t -creplace '\[ProtoContract\]', '[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]'
            $t = $t -creplace '\[ProtoContract\(/\*Could not decode attribute arguments\.\*/\)\]', '[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]'
            $t
        }
    }

    # JsonObject: settings/config classes need MemberSerialization.OptIn
    foreach ($jf in @(
        "$lib/Vintagestory.Client.NoObf/ClientSettings.cs",
        "$lib/Vintagestory.Client.NoObf/GltfPbrMetallicRoughness.cs",
        "$lib/Vintagestory.Client.NoObf/GltfType.cs",
        "$lib/Vintagestory.Client.NoObf/MacroBase.cs",
        "$lib/Vintagestory.Common/SettingsBase.cs",
        "$lib/Vintagestory.Common/StartServerArgs.cs",
        "$lib/Vintagestory.Server/ServerConfig.cs",
        "$lib/Vintagestory.Server/ServerPlayerData.cs",
        "$lib/Vintagestory.Server/ServerSettings.cs"
    )) {
        Update-FileInPlace $jf {
            param($t)
            $t = $t -creplace '\[JsonObject\]', '[JsonObject(MemberSerialization.OptIn)]'
            $t = $t -creplace '\[JsonObject\(/\*Could not decode attribute arguments\.\*/\)\]', '[JsonObject(MemberSerialization.OptIn)]'
            $t
        }
    }

    # GltfAccessor: restore JsonProperty name + NullValueHandling args
    $gltf = Join-Path $lib 'Vintagestory.Client.NoObf/GltfAccessor.cs'
    Update-FileInPlace $gltf {
        param($t)
        $t = [regex]::Replace($t, '\[JsonProperty\(/\*Could not decode attribute arguments\.\*/\)\]\r?\n(\s*public double\[\] Max)', '[JsonProperty("max", NullValueHandling = NullValueHandling.Ignore)]' + "`n" + '$1')
        $t = [regex]::Replace($t, '\[JsonProperty\(/\*Could not decode attribute arguments\.\*/\)\]\r?\n(\s*public double\[\] Min)', '[JsonProperty("min", NullValueHandling = NullValueHandling.Ignore)]' + "`n" + '$1')
        $t
    }

    Write-Host "Serialization metadata restored."

    # 6i-6m operate only on the decompiled projects (build/VintagestoryLib,
    # build/Vintagestory), never on build/snapshot/ (the ilspycmd/fork clone cache) or
    # real fork source under $repoRoot/<Fork>. Some of these patterns (System.Func
    # qualification, OrderedDictionary qualification) match genuine hand-written C# and
    # would corrupt fork source like VintagestoryApi/Common/API/Delegates.cs if the scope
    # were widened to all of build/.
    $decompiledDirs = @((Join-Path $repoRoot 'build/VintagestoryLib'), (Join-Path $repoRoot 'build/Vintagestory'))

    # 6i: .NET 9/10 added System.Collections.Generic.OrderedDictionary and the codebase
    # separately defines Vintagestory.API.Common.Func (predates generic delegates) /
    # Vintagestory.API.Datastructures.OrderedDictionary. Once both sides are in scope
    # (via `using`), the bare names become ambiguous. Qualify with whichever side the
    # actual VintagestoryApi interfaces expect.
    Update-TreeInPlace $decompiledDirs {
        param($t)
        $t = [regex]::Replace($t, '(?<!\.)\bOrderedDictionary<', 'Vintagestory.API.Datastructures.OrderedDictionary<')
        $t = [regex]::Replace($t, '(?<!\.)\bFunc<', 'System.Func<')
        $t
    }

    # 6i-exceptions: a handful of interface members declare the *other* side explicitly,
    # so the blanket qualification above picked the wrong one for them specifically.
    Update-TreeInPlace $decompiledDirs {
        param($t)
        $t = $t -creplace 'Vintagestory\.API\.Datastructures\.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>', 'System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>'
        $t = $t -creplace 'System\.Func<ActiveSlotChangeEventArgs, EnumHandling>', 'Vintagestory.API.Common.Func<ActiveSlotChangeEventArgs, EnumHandling>'
        $t = $t -creplace 'System\.Func<IServerPlayer, ActiveSlotChangeEventArgs, EnumHandling>', 'Vintagestory.API.Common.Func<IServerPlayer, ActiveSlotChangeEventArgs, EnumHandling>'
        $t
    }

    # ILSpy binds the cancellable EventHelper casts to System.Func, while vanilla IL uses Vintagestory.API.Common.Func.
    # Run this exception after the Func qualification pass so full donor builds invoke handlers and patch extraction does not recreate an orphan patch.
    Update-FileInPlace (Join-Path $lib 'Vintagestory.API.Common/EventHelper.cs') {
        param($t)
        $t = $t -creplace '\(System\.Func<T, EnumHandling>\)handler', '(Vintagestory.API.Common.Func<T, EnumHandling>)handler'
        $t = $t -creplace '\(System\.Func<T0, T1, EnumHandling>\)handler', '(Vintagestory.API.Common.Func<T0, T1, EnumHandling>)handler'
        $t
    }

    # 6j: ILSpy decompiles compiler-generated async state machines with plain
    # `private void MoveNext()` / `private void SetStateMachine(...)` instead of explicit
    # IAsyncStateMachine interface implementations.
    Update-TreeInPlace $decompiledDirs {
        param($t)
        $t = $t -creplace 'private void MoveNext\(\)', 'void IAsyncStateMachine.MoveNext()'
        $t = $t -creplace 'private void SetStateMachine\(IAsyncStateMachine stateMachine\)', 'void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)'
        $t
    }

    # 6k: types with both an indexer and an explicit [DefaultMember("Item")] attribute
    # conflict, because the compiler auto-emits DefaultMember for indexers.
    Update-TreeInPlace $decompiledDirs {
        param($t) [regex]::Replace($t, '\[DefaultMember\("Item"\)\]\s*\n', '')
    }

    # 6l: ILSpy decompiles `fixed` buffer fields with both the `fixed` keyword AND an
    # explicit [FixedBuffer] attribute (CS1716).
    Update-TreeInPlace $decompiledDirs {
        param($t) [regex]::Replace($t, '\[FixedBuffer\(.*?\)\]\s*\n(\s*public unsafe fixed)', '$1')
    }

    # 6m: ILSpy decompiles destructors (~ClassName()) with an erroneous `virtual` prefix.
    Update-TreeInPlace $decompiledDirs {
        param($t) [regex]::Replace($t, '\bvirtual (~\w+\(\))', '$1')
    }

    # 6n: NvidiaGPUFix64.cs (Windows Optimus GPU selection P/Invoke) has 12
    # [UnmanagedFunctionPointer(/*Could not decode attribute arguments.*/)] delegates.
    # All are extern P/Invoke callback delegates into nvapi64.dll, which uses the
    # standard Windows API (__stdcall) calling convention.
    $nvfix = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory/NvidiaGPUFix64.cs'
    Update-FileInPlace $nvfix {
        param($t) $t -creplace '\[UnmanagedFunctionPointer\(/\*Could not decode attribute arguments\.\*/\)\]', '[UnmanagedFunctionPointer(CallingConvention.StdCall)]'
    }

    # 6o: ConcurrentTagRegistry.cs backdoors private BCL fields via UnsafeAccessor but
    # ILSpy could not decode the attribute arguments.
    $ctr = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistry.cs'
    Update-FileInPlace $ctr {
        param($t)
        $t = [regex]::Replace($t, '\[UnsafeAccessor\(/\*Could not decode attribute arguments\.\*/\)\]\s*\n(\s*public static extern ref T\[\] Items)', '[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]' + "`n" + '$1')
        $t = [regex]::Replace($t, '\[UnsafeAccessor\(/\*Could not decode attribute arguments\.\*/\)\]\s*\n(\s*public static extern ref object Object)', '[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_object")]' + "`n" + '$1')
        $t
    }

    # 6o-2: bare `Enumerator<...>` locals ILSpy failed to qualify with their enclosing
    # collection type. Every occurrence here is a local declared and immediately
    # initialized from a `.GetEnumerator()` call, so `var` works uniformly. This must
    # run before the per-file explicit-type fixes below: those exist only for the
    # leftover bare Enumerator<...> declarations *without* an initializer, but a
    # same-named-but-different-collection bare Enumerator<...> can appear more than
    # once in one file, and running the per-file fix first would blanket every
    # occurrence with one file-wide type, silently breaking the other one.
    Update-TreeInPlace $decompiledDirs {
        param($t) [regex]::Replace($t, '\bEnumerator<[^;=]+>(\s+\w+\s*=\s*[^;]*\.GetEnumerator\(\));', 'var$1;')
    }

    # 6p: bare Enumerator<...> / ConfiguredTaskAwaiter / ConfiguredValueTaskAwaiter field
    # types where ILSpy dropped the enclosing container type.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ChatCommandApi.cs') {
        param($t) $t -creplace '\bEnumerator<string, IChatCommand>', 'Dictionary<string, IChatCommand>.ValueCollection.Enumerator'
    }

    foreach ($f in @(
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistry.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistryFast.cs')
    )) {
        Update-FileInPlace $f { param($t) $t -creplace '\bEnumerator<string, ushort>', 'Dictionary<string, ushort>.Enumerator' }
    }

    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/PlayerInventoryManager.cs') {
        param($t) $t -creplace '\bEnumerator<IInventory>', 'List<IInventory>.Enumerator'
    }

    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/StreamExtensions.cs') {
        param($t)
        $t = [regex]::Replace($t, '\bConfiguredTaskAwaiter<(\w+)>', 'System.Runtime.CompilerServices.ConfiguredTaskAwaitable<$1>.ConfiguredTaskAwaiter')
        $t = [regex]::Replace($t, '(?<!\.)\bConfiguredTaskAwaiter\b(?!\.)', 'System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter')
        $t
    }

    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.ModDb/ModDbUtil.cs') {
        param($t)
        $t = [regex]::Replace($t, '\bConfiguredValueTaskAwaiter<(\w+)>', 'System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<$1>.ConfiguredValueTaskAwaiter')
        $t = [regex]::Replace($t, '(?<!\.)\bConfiguredValueTaskAwaiter\b(?!\.)', 'System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter')
        $t
    }

    # 6p-2: bare Enumerator<...> declared without an initializer (assigned later in
    # separate branches, so `var` can't apply at the declaration site the way it does
    # above). Each needs the container type read off its own `.GetEnumerator()` receiver.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs') {
        param($t) $t -creplace '\bEnumerator<ConfigItem>', 'List<ConfigItem>.Enumerator'
    }

    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs') {
        param($t) $t -creplace '\bEnumerator<ILoadedSound>', 'Queue<ILoadedSound>.Enumerator'
    }

    foreach ($f in @(
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client/SystemClientCommands.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/CmdHelp.cs')
    )) {
        Update-FileInPlace $f { param($t) $t -creplace '\bEnumerator<string, IChatCommand>', 'Dictionary<string, IChatCommand>.Enumerator' }
    }

    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ModLoader.cs') {
        param($t)
        $t = $t -creplace '\bEnumerator<ModContainer>', 'List<ModContainer>.Enumerator'
        $t = $t -creplace '\bEnumerator<ModSystem>', 'List<ModSystem>.Enumerator'
        $t
    }

    $sc = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/SettingsClass.cs'
    Update-FileInPlace $sc {
        param($t)
        $t = $t -creplace '\bEnumerator<SettingsChangedWatcher<SettingsChangedWatcher<T>>>', 'List<SettingsChangedWatcher<SettingsChangedWatcher<T>>>.Enumerator'
        # values is Dictionary<string, T> reinterpreted as Dictionary<string, string> via an
        # (object) bypass cast elsewhere in this file (T and string are both reference types,
        # so this is a deliberate same-size reinterpret, not a real conversion). ILSpy
        # decompiled the write side as an illegal direct (string)value cast instead of the
        # matching reinterpret.
        $t = $t -creplace '\(string\)value;', 'System.Runtime.CompilerServices.Unsafe.As<T, string>(ref value);'
        # Same reinterpret, read side: a raw string out of the (object)-bypassed dictionary
        # assigned directly to T. Indexer results are not addressable, so Unsafe.As (which
        # needs a ref) does not apply here; cast through object instead.
        $t = $t -creplace '\bT newValue = \(\(Dictionary<string, string>\)\(object\)values\)\[key\];', 'T newValue = (T)(object)((Dictionary<string, string>)(object)values)[key];'
        # Watchers is List<SettingsChangedWatcher<T>> reinterpreted the same way; reading an
        # element back out needs the same object-cast round-trip.
        $t = [regex]::Replace($t, '(SettingsChangedWatcher<T> \w+) = (\(\(List<SettingsChangedWatcher<SettingsChangedWatcher<T>>>(?:\.Enumerator\*\)\(&enumerator\)\)->Current|\)\(object\)Watchers\)\[i\]));', '$1 = (SettingsChangedWatcher<T>)(object)$2;')
        # SettingsClass<T> reinterprets its Dictionary<string, T> as Dictionary<string,
        # string> (same object-cast idiom as the rules above); the change-detection
        # comparison in Set() reads the dictionary's string value but was missing the
        # matching cast back to T before comparing against `value`.
        $t = $t.Replace('EqualityComparer<T>.Default.Equals(((Dictionary<string, string>)(object)values)[key], value)', 'EqualityComparer<T>.Default.Equals((T)(object)((Dictionary<string, string>)(object)values)[key], value)')
        $t
    }

    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/CmdLand.cs') {
        param($t) $t -creplace '\bEnumerator<LandClaim>', 'List<LandClaim>.Enumerator'
    }

    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/ServerSystemBlockIdRemapper.cs') {
        param($t) $t -creplace '\bEnumerator<int, AssetLocation>', 'Dictionary<int, AssetLocation>.Enumerator'
    }

    # elementsByIndex is a Dictionary<long, long> storing T's raw bits (T : ILongIndex is
    # assumed exactly long-sized), matching the read side's pointer reinterpret a few lines
    # above in the same file. ILSpy decompiled the write side as an illegal direct
    # (long)elem cast instead of the matching reinterpret.
    foreach ($f in @(
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ConcurrentIndexedFifoQueue.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/IndexedFifoQueue.cs')
    )) {
        Update-FileInPlace $f { param($t) $t -creplace '= \(long\)elem;', '= Unsafe.As<T, long>(ref elem);' }
    }

    # ILSpy decompiles a params ReadOnlySpan<T> element read as an unsafe pointer-cast
    # Unsafe.Read<string>((void*)span[i]) instead of the plain indexer access it actually is.
    foreach ($f in @(
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistry.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistryFast.cs')
    )) {
        Update-FileInPlace $f { param($t) [regex]::Replace($t, 'System\.Runtime\.CompilerServices\.Unsafe\.Read<\w+>\(\(void\*\)([^;]+)\)', '$1') }
    }

    # ILSpy decompiles a reference-type null check as `(int)val != 0` (a value-type-style
    # null pattern) instead of `val != null`.
    foreach ($f in @(
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/BlockAccessorWorldGen.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/BlockAccessorWorldGenUpdateHeightmap.cs')
    )) {
        Update-FileInPlace $f { param($t) [regex]::Replace($t, '\(\(int\)val != 0\) \? \(\(object\)val\)\.ToString\(\) : null', 'val?.ToString()') }
    }

    # 6r: ILSpy decompiles Try*(..., out x) BCL/API calls with `ref` instead of `out` at
    # the call site. Every one of these overloads in this codebase declares the value
    # parameter as `out`, never `ref`, so this is safe everywhere the pattern matches.
    $tryPattern = '\.(TryGetValue|TryDequeue|TryPeek|TryPop|TryTake|TryRemove|TryParse|TryParseExact|TryGetNonEnumeratedCount|TryLoad)((?:<[^<>]*>)?)\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*?)(?:,\s*)?ref\s+((?:[^,()]|\([^()]*\))+)\)'
    $tryEvaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $sep = if ($m.Groups[3].Value.Length -gt 0) { ', ' } else { '' }
        ".$($m.Groups[1].Value)$($m.Groups[2].Value)($($m.Groups[3].Value)$sep" + "out $($m.Groups[4].Value))"
    }
    Update-TreeInPlace $decompiledDirs { param($t) [regex]::Replace($t, $tryPattern, $tryEvaluator) }

    # 6r-2: KeyValuePair<K,V>.Deconstruct(out K, out V) decompiles with both parameters
    # as `ref`. Unlike the Try* fix above, Deconstruct can have more than one `ref`
    # parameter to fix in the same call.
    $deconstructPattern = '\.Deconstruct\(((?:[^()]|\([^()]*\))*)\)'
    $deconstructEvaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $inner = [regex]::Replace($m.Groups[1].Value, '\bref\b', 'out')
        ".Deconstruct($inner)"
    }
    Update-TreeInPlace $decompiledDirs { param($t) [regex]::Replace($t, $deconstructPattern, $deconstructEvaluator) }

    # 6r-3: one int.TryParse(..., ref num) call whose argument expression nests deeper
    # than the one-level tolerance above.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs') {
        param($t) [regex]::Replace($t, '(int\.TryParse\(.*), ref (\w+)\);', '$1, out $2);')
    }

    # 6r-4: ILSpy decompiles implicit conversion operators (decimal/Index/Memory<T>/
    # ReadOnlyMemory<T>/ReadOnlySpan<T>/Span<T>/string's `implicit operator`) as an
    # explicit call to their IL name (op_Implicit), which C# forbids calling directly.
    # `string` is the one exception: its only op_Implicit converts a string into
    # ReadOnlySpan<char>, not into another string.
    Update-TreeInPlace $decompiledDirs {
        param($t)
        $t = [regex]::Replace($t, '(?:System\.)?\bstring\.op_Implicit\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)', '((System.ReadOnlySpan<char>)($1))')
        $t = [regex]::Replace($t, '(?:System\.)?\b((?:Memory|ReadOnlyMemory|ReadOnlySpan|Span)<[^<>]*>|decimal|Index)\.op_Implicit\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)', '(($1)($2))')
        $t
    }

    # 6r-4b: `new System.ReadOnlySpan<char>(ref (char)expr)` -- ReadOnlySpan<char>'s
    # single-value constructor takes `in`, and a cast result is not an addressable
    # lvalue, so `ref` can never bind here (CS1510).
    Update-TreeInPlace $decompiledDirs {
        param($t) [regex]::Replace($t, 'new System\.ReadOnlySpan<char>\(ref (\(char\)[^()]+(?:\([^()]*\))?[^()]*)\)', 'new System.ReadOnlySpan<char>($1)')
    }

    # 6r-5: bare Environment.SpecialFolder/SpecialFolderOption casts missing the
    # Environment. qualifier.
    Update-TreeInPlace $decompiledDirs {
        param($t) [regex]::Replace($t, '\((SpecialFolder|SpecialFolderOption)\)', '(Environment.$1)')
    }

    # 6r-6: bare AppendInterpolatedStringHandler is really StringBuilder's nested
    # interpolated-string-handler type, missing its StringBuilder. qualifier.
    Update-TreeInPlace $decompiledDirs {
        param($t) [regex]::Replace($t, '(?<!\.)\bAppendInterpolatedStringHandler\b', 'StringBuilder.AppendInterpolatedStringHandler')
    }

    # 6s: custom-accessor events have no backing field of their own name, so
    # ILSpy-decompiled reads of the event's current value outside a += or -= context
    # are CS0079. Every such event follows the same Interlocked.CompareExchange
    # pattern against a private m_<Name> field; rewrite reads to that field.
    Repair-EventReads -Roots $decompiledDirs

    # 6t: nested async-lambda state machine structs are decompiled with their fields
    # correctly public but the struct itself left private, even though the enclosing
    # method instantiates and drives it from outside the display class that declares it.
    Update-TreeInPlace $decompiledDirs {
        param($t) $t -creplace 'private struct (\w+) : IAsyncStateMachine', 'public struct $1 : IAsyncStateMachine'
    }

    # 6u: two hoisted-local fields are read bare inside a nested `delegate { ... }` in
    # async state machine structs. Reading a hoisted field bare requires an implicit
    # `this.`, and C# forbids implicitly capturing `this` from a struct inside a nested
    # anonymous method (CS1673). Each occurrence needs its own fix.
    $vswc = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/VSWebClient.cs'
    Update-FileInPlace $vswc {
        param($t)
        $pattern = '(\t+)(Progress<int> val5 = new Progress<int>\(\(Action<int>\)delegate\(int totalBytes\)\n\1\{\n)\1\t_003C_003E8__1\.progress\.Report\(new Tuple<int, long>\(totalBytes, _003C_003E8__1\.contentLength\.Value\)\);'
        $replacement = '${1}var downloadState = _003C_003E8__1;' + "`n" + '${1}${2}${1}' + "`t" + 'downloadState.progress.Report(new Tuple<int, long>(totalBytes, downloadState.contentLength.Value));'
        [regex]::Replace($t, $pattern, $replacement)
    }

    $svc = Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/ServerConsole.cs'
    if (Test-Path $svc) {
        $text = Read-TextFile $svc
        $marker = 'serverConsole.server.EnqueueMainThreadTask((Action)delegate'
        if ($text.Contains($marker) -and -not $text.Contains('var consoleState = _003C_003E8__1;')) {
            $idx = $text.IndexOf($marker)
            $lineStart = $text.LastIndexOf("`n", $idx) + 1
            $indent = $text.Substring($lineStart, $idx - $lineStart)

            $braceOpen = $text.IndexOf('{', $idx)
            $depth = 1
            $j = $braceOpen + 1
            while ($depth -gt 0) {
                if ($text[$j] -eq '{') { $depth++ }
                elseif ($text[$j] -eq '}') { $depth-- }
                $j++
            }

            $body = $text.Substring($idx, $j - $idx)
            $newBody = [regex]::Replace($body, '(?<![.\w])_003C_003E8__1\b', 'consoleState')
            $newText = $text.Substring(0, $lineStart) + $indent + "var consoleState = _003C_003E8__1;`n" + $indent + $newBody + $text.Substring($j)
            Write-TextFile $svc $newText
        }
    }

    # 6v: bare Convert.To*(...) calls in Vintagestory.Common.Database resolve to the
    # sibling namespace Vintagestory.Common.Convert instead of System.Convert, because a
    # namespace that shares a prefix with the current one wins over a `using` import in
    # C#'s lookup order. Scoped to just these two files.
    foreach ($f in @(
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common.Database/SQLiteDbConnectionv1.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common.Database/SQLiteDbConnectionv2.cs')
    )) {
        Update-FileInPlace $f { param($t) [regex]::Replace($t, '(?<!\.)\bConvert\.(To\w+)\(', 'System.Convert.$1(') }
    }

    # 6w: `TYPE val = default(TYPE);` immediately followed by `val._002Ector(args);` --
    # the value-type equivalent of the ref-cast constructor pattern above, but without
    # the `((Type)(ref var))` wrapper that regex expects, so it needs its own pass.
    Repair-ValueTypeConstructorCalls -Roots $decompiledDirs

    # 6x: remaining Phase 4 long-tail singles/small-groups, each verified to be the only
    # occurrence of its pattern.

    # Mutex's 3-arg constructor declares the third parameter `out bool createdNew`;
    # ILSpy rendered the call site with `ref`.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client/ClientProgram.cs') {
        param($t) $t.Replace('new Mutex(true, "Vintagestory", ref flag);', 'new Mutex(true, "Vintagestory", out flag);')
    }

    # FieldAttributes (a [Flags] enum) has no operator& with a bare int literal; the
    # original source must have cast to int first, and ILSpy dropped the cast.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client.NoObf/SystemNetworkProcess.cs') {
        param($t) $t.Replace('(fields[i].Attributes & 0x40)', '((int)fields[i].Attributes & 0x40)')
    }

    # A destructor's compiler-generated try/finally already chains to the base
    # finalizer implicitly; C# forbids calling Finalize() explicitly at all, so
    # ILSpy's rendering of that implicit chain as an explicit call must simply be dropped.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client.NoObf/VAO.cs') {
        param($t)
        ($t -split "`n" | Where-Object { $_ -cnotmatch '^\s*\(\(object\)this\)\.Finalize\(\);\s*$' }) -join "`n"
    }

    # Socket.Dispose(bool) is protected; casting `this` to the base type Socket and
    # calling through that cast strips the derived-class access that protected members
    # need (CS1540), whereas `base.Dispose(disposing)` is what an override calling its
    # base implementation should be.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Client.NoObf/VintageStorySocket.cs') {
        param($t) $t.Replace('((Socket)this).Dispose(disposing);', 'base.Dispose(disposing);')
    }

    # ConcurrentIndexedFifoQueue<T> reinterprets its ConcurrentDictionary<long, T> field
    # as ConcurrentDictionary<long, long> everywhere. Snapshot() reads through that
    # reinterpret but was missing the matching cast back to ICollection<T> on the way out.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ConcurrentIndexedFifoQueue.cs') {
        param($t) $t.Replace('return ((ConcurrentDictionary<long, long>)(object)elementsByIndex).Values;', 'return (System.Collections.Generic.ICollection<T>)(object)((ConcurrentDictionary<long, long>)(object)elementsByIndex).Values;')
    }

    # TagSet is a readonly struct, so its `storage` field can only be taken by `ref`
    # inside TagSet's own constructor; `in` is what Unsafe.AsRef<T>(in T) actually
    # accepts, and it is indistinguishable from `ref` at the IL level.
    Update-FileInPlace $ctr {
        param($t) $t.Replace('Unsafe.AsRef<ReadOnlyMemory<ushort>>(ref set.storage)', 'Unsafe.AsRef<ReadOnlyMemory<ushort>>(in set.storage)')
    }

    Update-FileInPlace $sc {
        param($t) $t.Replace('EqualityComparer<T>.Default.Equals(((Dictionary<string, string>)(object)values)[key], value)', 'EqualityComparer<T>.Default.Equals((T)(object)((Dictionary<string, string>)(object)values)[key], value)')
    }

    # VSWebClient's async state machine funnels any exception caught while disposing an
    # await-using resource through an `object`-typed hoisted field so it can be
    # rethrown later via ExceptionDispatchInfo. C# requires catch/throw clauses to be
    # statically typed as Exception (or a subtype).
    Update-FileInPlace $vswc {
        param($t)
        $t = $t.Replace('catch (object obj)', 'catch (System.Exception obj)')
        $t = $t.Replace('ExceptionDispatchInfo.Capture((obj2 as System.Exception) ?? throw obj2).Throw();', 'ExceptionDispatchInfo.Capture((System.Exception)obj2).Throw();')
        $t
    }

    # `fixed (T* p = someSpan)` is special-cased by the compiler, but explicitly calling
    # `someSpan.GetPinnableReference()` yourself only satisfies the general
    # pattern-based fixed rules when the byref-returning call is prefixed with `&`;
    # ILSpy always drops that `&`.
    foreach ($f in @(
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ZStdCompressorImpl.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ZStdDecompressorImpl.cs'),
        (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Common/ZStdWrapper.cs')
    )) {
        Update-FileInPlace $f { param($t) [regex]::Replace($t, 'fixed \(byte\* (\w+) = (\w+(?:\.\w+)?)\.GetPinnableReference\(\)\)', 'fixed (byte* $1 = &$2.GetPinnableReference())') }
    }

    # PosixSignal defines `enum operator-(PosixSignal, int)` returning PosixSignal, so
    # `signal - -4` is itself a PosixSignal even though only its underlying numeric
    # value is meant to drive the switch; only the literal 0 has an implicit
    # conversion to an enum type, so the nonzero case labels fail to compile.
    Update-FileInPlace (Join-Path $repoRoot 'build/VintagestoryLib/Vintagestory.Server/ServerProgram.cs') {
        param($t) $t -creplace '\(signal - -4\) switch', '((int)signal - -4) switch'
    }

    Write-Host "Ambiguity and decompiler-artifact fixups applied."

    # Snapshot: save post-fixup state as .baseline/ for patch extraction.
    Write-Host "Saving post-fixup baseline snapshot..."
    $postFixupBaselineDir = Join-Path $repoRoot '.baseline'
    if (Test-Path $postFixupBaselineDir) { Remove-Item -Recurse -Force $postFixupBaselineDir }
    New-Item -ItemType Directory -Force -Path $postFixupBaselineDir | Out-Null
    Copy-Item -Recurse -Force (Join-Path $repoRoot 'build/VintagestoryLib') (Join-Path $postFixupBaselineDir 'VintagestoryLib')
    Copy-Item -Recurse -Force (Join-Path $repoRoot 'build/Vintagestory') (Join-Path $postFixupBaselineDir 'Vintagestory')
    # Fork projects (from snapshot, already LF-normalized in step 3).
    if (Test-Path $snapshotDir) {
        Get-ChildItem -Path $snapshotDir -Directory | ForEach-Object {
            $name = $_.Name
            if ($name -eq 'VintagestoryLib' -or $name -eq 'Vintagestory') { return }
            Copy-Item -Recurse -Force $_.FullName (Join-Path $postFixupBaselineDir $name)
        }
    }
    # Apply sources/ overlays to the baseline too, so that extract-patches does not
    # generate spurious diffs for files managed by the overlay. Only
    # .csproj/.props/.targets are overlaid here; .cs files in sources/ are
    # Optimum-original and belong in sources/, not baseline.
    if (Test-Path $sourcesDir) {
        Get-ChildItem -Path $sourcesDir -Recurse -File -Include '*.csproj', '*.props', '*.targets' | ForEach-Object {
            $rel = $_.FullName.Substring($sourcesDir.Length + 1) -creplace '\\', '/'
            $parts = $rel -split '/'
            $topProj = $parts[0]
            $restPath = ($parts[1..($parts.Length - 1)] -join [IO.Path]::DirectorySeparatorChar)
            $target = Join-Path (Join-Path $postFixupBaselineDir $topProj) $restPath
            if (Test-Path $target) { Copy-Item -Force $_.FullName $target }
        }
    }
    Write-Host "Baseline snapshot saved to .baseline/."

    # --- 7. Apply optimization patches on top of the post-fixup baseline. ---
    #
    # Patch directory: patches/{project}/file.patch
    # Projects VintagestoryLib and Vintagestory target build/ (--directory=build).
    # All other projects (VintagestoryApi, VSEssentials, etc.) target repo root.
    #
    # Environment variable:
    #   PATCH_FILTER  - "all" (default), or comma-separated substrings to EXCLUDE
    $patchesDir = Join-Path $repoRoot 'patches'
    $patchFilter = if ($env:PATCH_FILTER) { $env:PATCH_FILTER } else { 'all' }
    $vanillaPatchProjects = @('VintagestoryLib', 'Vintagestory')

    if ((Test-Path $patchesDir) -and (Get-ChildItem -Path $patchesDir -Recurse -Filter '*.patch' -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        # Stage into git index for cleaner apply diagnostics.
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        git add -f build/ VintagestoryApi/ Cairo/ VSEssentials/ VSSurvivalMod/ VSCreativeMod/ 2>$null | Out-Null
        $ErrorActionPreference = $prevEAP

        $failed = @()
        $applied = 0
        $skipped = 0

        $patches = Get-ChildItem -Path $patchesDir -Recurse -Filter '*.patch' -File | Sort-Object FullName
        foreach ($patch in $patches) {
            $rel = $patch.FullName.Substring($repoRoot.Length + 1) -creplace '\\', '/'

            if ($patchFilter -ne 'all') {
                $excludes = $patchFilter -split ','
                $skip = $false
                foreach ($excl in $excludes) {
                    if ($rel -clike "*$excl*") { $skip = $true; break }
                }
                if ($skip) { $skipped++; continue }
            }

            Write-Host "Applying $rel"
            $topProj = ($rel -split '/')[1]
            $dirArg = if ($vanillaPatchProjects -contains $topProj) { '--directory=build' } else { '' }

            $result = Invoke-GitApplyOptimumPatch -PatchPath $patch.FullName -DirArg $dirArg
            if (-not $result.Success) {
                $failed += $rel
                $firstLines = ($result.Output -split "`n") | Select-Object -First 3
                Write-Host "  FAILED: $($firstLines -join "`n")"
            } else {
                if ($result.Output) { Write-Host "  $($result.Output)" }
                $applied++
            }
        }

        # Unstage: the index staging was temporary.
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        git reset HEAD -- build/ VintagestoryApi/ Cairo/ VSEssentials/ VSSurvivalMod/ VSCreativeMod/ 2>$null | Out-Null
        $ErrorActionPreference = $prevEAP

        Write-Host "Patches: $applied applied, $skipped skipped, $($failed.Count) failed (filter: $patchFilter)"
        if ($failed.Count -gt 0) {
            $failed | ForEach-Object { Write-Host "  $_" }
            exit 1
        }
    } else {
        Write-Host "No patches/ to apply."
    }

    # --- 8. Copy Optimum-only source files. ---
    if (Test-Path $sourcesDir) {
        Get-ChildItem -Path $sourcesDir -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($sourcesDir.Length + 1) -creplace '\\', '/'
            $topProj = ($rel -split '/')[0]
            $target = if ($vanillaPatchProjects -contains $topProj) {
                Join-Path $repoRoot ("build/" + $rel)
            } else {
                Join-Path $repoRoot $rel
            }
            $targetDir = Split-Path -Parent $target
            if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
            Copy-Item -Force $_.FullName $target
        }
        Write-Host "Synced sources/ into working tree."
    }

    # --- 9. Create solution file if missing (VintageStory.slnx is normally tracked). ---
    $slnx = Join-Path $repoRoot 'VintageStory.slnx'
    if (-not (Test-Path $slnx)) {
        Write-Host "Creating VintageStory.slnx"
        $slnxContent = @'
<Solution>
  <Folder Name="/Vanilla/">
    <Project Path="build/VintagestoryLib/VintagestoryLib.csproj" />
    <Project Path="build/Vintagestory/Vintagestory.csproj" />
    <Project Path="VintagestoryApi/VintagestoryAPI.csproj" />
    <Project Path="Cairo/Cairo.csproj" />
  </Folder>
  <Folder Name="/Forks/">
    <Project Path="VSEssentials/VSEssentialsMod.csproj" />
    <Project Path="VSSurvivalMod/VSSurvivalMod.csproj" />
    <Project Path="VSCreativeMod/VSCreativeMod.csproj" />
  </Folder>
  <Folder Name="/Tests/">
    <Project Path="Optimum.Tests/Optimum.Tests.csproj" />
  </Folder>
</Solution>
'@
        Write-TextFile $slnx ($slnxContent + "`n")
    }

    Write-Host ""
    Write-Host "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release" -ForegroundColor Green
} finally {
    Pop-Location
}
# Explicit success code: install-windows.ps1 gates on $LASTEXITCODE after
# invoking this script, and without this it would hold whatever the last
# native command happened to return.
exit 0
