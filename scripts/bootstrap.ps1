<#
.SYNOPSIS
Builds a clean working tree for Optimum (client fork):
  1. Downloads the official VS Windows installer (vs_install_win-x64_*.exe).
  2. Decompiles closed-source DLLs (VintagestoryLib.dll, Vintagestory.dll) with ILSpy.
  3. Clones open-source Anego forks at pinned refs.
  4. Applies patches/ on top.
  5. Copies sources/ (Optimum-original files) into the working tree.
  6. Runs post-decompile fixups (csproj rewrite, ambiguity resolution, ref-casts, GeneratedRegex).
  7. Copies sources/ again (fixups may overwrite csproj, sources take priority).

This only reconstructs the dev tree. To build redistributable packages, run
scripts/package-all.ps1 (or `make package`) after `dotnet build`.

.PARAMETER Version
Vintage Story version. Default: 1.22.3.

.PARAMETER ClientArchive
Path to an existing Windows installer (.exe). If omitted, downloads from cdn.vintagestory.at.

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
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $decompileTargets = [ordered]@{
        'VintagestoryLib' = 'Vintagestory.Server+Client engine'
        'Vintagestory'    = 'Client executable'
    }

    $vanillaDir  = Join-Path $repoRoot '.vanilla'
    $baselineDir = Join-Path $repoRoot '.baseline'
    $zipCacheDir = Join-Path $repoRoot '.vanilla-zips'

    if ($Refresh -and (Test-Path $vanillaDir))  { Remove-Item -Recurse -Force $vanillaDir }
    if ($Refresh -and (Test-Path $baselineDir)) { Remove-Item -Recurse -Force $baselineDir }

    # --- 1. Obtain vanilla client files ---
    # Three modes:
    #   a) .vanilla/ already exists (junction from installer, or previous run) -> skip
    #   b) -ClientArchive '__skip__' -> the caller (install-windows.ps1) already placed
    #      .vanilla via junction; verify it exists and move on
    #   c) Normal: download the installer and extract with innounp
    $skipDownload = ($ClientArchive -eq '__skip__')
    if ((Test-Path (Join-Path $vanillaDir 'vintagestory\Vintagestory.exe'))) {
        Write-Host "Using existing $vanillaDir"
    } elseif ($skipDownload) {
        # The installer creates .vanilla/vintagestory as a junction before calling us.
        if (-not (Test-Path (Join-Path $vanillaDir 'vintagestory\Vintagestory.exe'))) {
            throw ".vanilla/vintagestory not found and download was skipped. Provide a VS install path."
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
            curl.exe -L --fail --progress-bar -o $ClientArchive $url
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
            curl.exe -L --fail --silent -o $innounpZip "https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/ui_2_2_9/innounp-2.zip"
            Expand-Archive -Path $innounpZip -DestinationPath $toolsDir -Force
            $found = Get-ChildItem -Path $toolsDir -Recurse -Filter 'innounp.exe' | Select-Object -First 1
            if ($found -and $found.FullName -ne $innounp) {
                Copy-Item -Force $found.FullName $innounp
            }
            Remove-Item -Force $innounpZip -ErrorAction SilentlyContinue
        }

        $extractTarget = Join-Path $vanillaDir 'vintagestory'
        New-Item -ItemType Directory -Force -Path $extractTarget | Out-Null
        Write-Host "Extracting with innounp to $extractTarget"
        & $innounp -x -d"$extractTarget" -c"{app}" $ClientArchive | Out-Null
        # innounp extracts into {app}/ subfolder. Move contents up if needed.
        $appDir = Join-Path $extractTarget '{app}'
        if (Test-Path $appDir) {
            Get-ChildItem -Path $appDir | Move-Item -Destination $extractTarget -Force
            Remove-Item -Force $appDir
        }
        if (-not (Test-Path (Join-Path $extractTarget 'Vintagestory.exe'))) {
            throw "Extraction failed: Vintagestory.exe not found"
        }
        Write-Host "Extraction complete."
    }

    # --- 2. Decompile closed-source DLLs ---
    if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
        $manifest = Join-Path (Join-Path $repoRoot '.config') 'dotnet-tools.json'
        if (Test-Path $manifest) {
            $json = Get-Content $manifest -Raw | ConvertFrom-Json
            $pinnedVersion = $json.tools.ilspycmd.version
            Write-Host "Installing ilspycmd $pinnedVersion"
            dotnet tool install -g ilspycmd --version $pinnedVersion | Out-Null
        } else {
            Write-Host "Installing ilspycmd (latest)"
            dotnet tool install -g ilspycmd | Out-Null
        }
        $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    }

    foreach ($dllBase in $decompileTargets.Keys) {
        $desc = $decompileTargets[$dllBase]
        $dllPath = Get-ChildItem -Path (Join-Path $vanillaDir 'vintagestory') -Recurse -Filter "$dllBase.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $dllPath) { $dllPath = Get-ChildItem -Path $vanillaDir -Recurse -Filter "$dllBase.dll" -ErrorAction SilentlyContinue | Select-Object -First 1 }
        if (-not $dllPath) { Write-Warning "Skipping $dllBase.dll (not found in archive)"; continue }

        $out = Join-Path $baselineDir $dllBase
        if (-not (Test-Path $out) -or $Refresh) {
            Write-Host "Decompiling $dllBase.dll ($desc)"
            if (Test-Path $out) { Remove-Item -Recurse -Force $out }
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            ilspycmd $dllPath.FullName --project -o $out | Out-Null
            # Normalize LangVersion for .NET 10.
            Get-ChildItem -Path $out -Filter '*.csproj' -File | ForEach-Object {
                $text = [IO.File]::ReadAllText($_.FullName)
                $patched = $text -replace '<LangVersion>15\.0</LangVersion>', '<LangVersion>latest</LangVersion>'
                if ($patched -ne $text) { [IO.File]::WriteAllText($_.FullName, $patched) }
            }
        }

        # Copy baseline -> working tree.
        $work = Join-Path (Join-Path $repoRoot 'baseline') $dllBase
        if (Test-Path $work) { Remove-Item -Recurse -Force $work }
        New-Item -ItemType Directory -Force -Path (Split-Path $work) | Out-Null
        Copy-Item -Recurse -Force $out $work

        # Normalize to LF (patches assume LF). ilspycmd emits CRLF on Windows;
        # without this, git apply fails on patches/<target>/*.patch with a CRLF-vs-LF
        # context mismatch, falls back to --3way, and errors "does not exist in index"
        # because baseline/ is gitignored. Forks already get this at clone time (step 3).
        Get-ChildItem -Path $work -Recurse -File -Include '*.cs','*.csproj' -ErrorAction SilentlyContinue | ForEach-Object {
            $bytes = [IO.File]::ReadAllBytes($_.FullName)
            $hasCR = $false
            foreach ($b in $bytes) { if ($b -eq 13) { $hasCR = $true; break } }
            if ($hasCR) {
                $t = [Text.Encoding]::UTF8.GetString($bytes) -replace "`r`n", "`n"
                [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($t))
            }
        }
    }

    # --- 3. Clone open-source forks ---
    $forksFile = Join-Path $repoRoot 'forks.json'
    if (Test-Path $forksFile) {
        $cfg = Get-Content $forksFile -Raw | ConvertFrom-Json
        foreach ($fork in $cfg.forks) {
            $name = $fork.name
            $base = Join-Path $baselineDir $name

            if (-not (Test-Path $base) -or $Refresh) {
                if (Test-Path $base) { Remove-Item -Recurse -Force $base }
                Write-Host "Cloning $name at $($fork.ref)"
                git clone --quiet $fork.url $base 2>$null
                git -C $base checkout --quiet $fork.ref
                Remove-Item -Recurse -Force (Join-Path $base '.git')

                # Normalize to LF (patches assume LF).
                Get-ChildItem -Path $base -Recurse -File -Include '*.cs','*.csproj','*.json','*.xml','*.props','*.targets' -ErrorAction SilentlyContinue | ForEach-Object {
                    $bytes = [IO.File]::ReadAllBytes($_.FullName)
                    $hasCR = $false
                    foreach ($b in $bytes) { if ($b -eq 13) { $hasCR = $true; break } }
                    if ($hasCR) {
                        $t = [Text.Encoding]::UTF8.GetString($bytes) -replace "`r`n", "`n"
                        [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($t))
                    }
                }
            }

            # Copy baseline -> working tree.
            $work = Join-Path $repoRoot $name
            if (Test-Path $work) { Remove-Item -Recurse -Force $work }
            Copy-Item -Recurse -Force $base $work
        }
    }

    # --- 4. Apply patches ---
    $patchesDir = Join-Path $repoRoot 'patches'
    $vanillaPatchProjects = @('VintagestoryLib', 'Vintagestory')
    if (Test-Path $patchesDir) {
        $patches = Get-ChildItem -Path $patchesDir -Recurse -Filter '*.patch' | Sort-Object FullName
        if ($patches.Count -eq 0) {
            Write-Host "No patches/ to apply."
        } else {
            $failed = @()
            foreach ($patch in $patches) {
                $rel = $patch.FullName.Substring($repoRoot.Length + 1)
                $topProj = ($rel -split '[\\/]')[1]
                $applyArgs = @('apply', '--whitespace=nowarn')
                if ($vanillaPatchProjects -contains $topProj) { $applyArgs += '--directory=baseline' }
                $applyArgs += $patch.FullName
                Write-Host "Applying $rel"
                $prevEAP = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                $out = & git @applyArgs 2>&1
                $code = $LASTEXITCODE
                $ErrorActionPreference = $prevEAP
                if ($code -ne 0) {
                    $failed += $rel
                    Write-Host "  FAILED: $out" -ForegroundColor Yellow
                }
            }
            if ($failed.Count -gt 0) {
                Write-Host "`n$($failed.Count) patch(es) failed:" -ForegroundColor Red
                $failed | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            }
        }
    } else {
        Write-Host "No patches/ directory."
    }

    # --- 5. Copy Optimum-only source files (first pass) ---
    $sourcesDir = Join-Path $repoRoot 'sources'
    if (Test-Path $sourcesDir) {
        Get-ChildItem -Path $sourcesDir -Directory | ForEach-Object {
            $proj = $_.Name
            $dst = if ($vanillaPatchProjects -contains $proj) {
                Join-Path $repoRoot "baseline/$proj"
            } else {
                Join-Path $repoRoot $proj
            }
            if (-not (Test-Path $dst)) {
                Write-Warning "sources/$proj has no matching working folder; skipping."
                return
            }
            Get-ChildItem -Path $_.FullName -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring((Join-Path $sourcesDir $proj).Length + 1)
                $target = Join-Path $dst $rel
                $targetDir = Split-Path $target
                if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
                Copy-Item -Force $_.FullName $target
            }
            Write-Host "Synced sources/$proj"
        }
    }

    # --- 6. Post-decompile fixups ---
    Write-Host "Applying post-decompile fixups..."

    # 6a. Overwrite VintagestoryLib.csproj with correct HintPaths.
    $libCsproj = Join-Path (Join-Path (Join-Path $repoRoot 'baseline') 'VintagestoryLib') 'VintagestoryLib.csproj'
    @'
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
    <Reference Include="cairo-sharp"><HintPath>..\..\.vanilla\vintagestory\Lib\cairo-sharp.dll</HintPath></Reference>
    <Reference Include="protobuf-net"><HintPath>..\..\.vanilla\vintagestory\Lib\protobuf-net.dll</HintPath></Reference>
    <Reference Include="Newtonsoft.Json"><HintPath>..\..\.vanilla\vintagestory\Lib\Newtonsoft.Json.dll</HintPath></Reference>
    <Reference Include="CommandLine"><HintPath>..\..\.vanilla\vintagestory\Lib\CommandLine.dll</HintPath></Reference>
    <Reference Include="SkiaSharp"><HintPath>..\..\.vanilla\vintagestory\Lib\SkiaSharp.dll</HintPath></Reference>
    <Reference Include="Open.Nat"><HintPath>..\..\.vanilla\vintagestory\Lib\Open.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Nat"><HintPath>..\..\.vanilla\vintagestory\Lib\Mono.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Cecil"><HintPath>..\..\.vanilla\vintagestory\Lib\Mono.Cecil.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis"><HintPath>..\..\.vanilla\vintagestory\Lib\Microsoft.CodeAnalysis.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis.CSharp"><HintPath>..\..\.vanilla\vintagestory\Lib\Microsoft.CodeAnalysis.CSharp.dll</HintPath></Reference>
    <Reference Include="ICSharpCode.SharpZipLib"><HintPath>..\..\.vanilla\vintagestory\Lib\ICSharpCode.SharpZipLib.dll</HintPath></Reference>
    <Reference Include="Microsoft.Data.Sqlite"><HintPath>..\..\.vanilla\vintagestory\Lib\Microsoft.Data.Sqlite.dll</HintPath></Reference>
    <Reference Include="0Harmony"><HintPath>..\..\.vanilla\vintagestory\Lib\0Harmony.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Desktop"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Windowing.Desktop.dll</HintPath></Reference>
    <Reference Include="OpenTK.Audio.OpenAL"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Audio.OpenAL.dll</HintPath></Reference>
    <Reference Include="OpenTK.Mathematics"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Mathematics.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Common"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Windowing.Common.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.GraphicsLibraryFramework"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Windowing.GraphicsLibraryFramework.dll</HintPath></Reference>
    <Reference Include="DnsClient"><HintPath>..\..\.vanilla\vintagestory\Lib\DnsClient.dll</HintPath></Reference>
    <Reference Include="OpenTK.Graphics"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Graphics.dll</HintPath></Reference>
    <Reference Include="csvorbis"><HintPath>..\..\.vanilla\vintagestory\Lib\csvorbis.dll</HintPath></Reference>
    <Reference Include="csogg"><HintPath>..\..\.vanilla\vintagestory\Lib\csogg.dll</HintPath></Reference>
    <Reference Include="xplatforminterface"><HintPath>..\..\.vanilla\vintagestory\Lib\xplatforminterface.dll</HintPath></Reference>
  </ItemGroup>
</Project>
'@ | Set-Content -Path $libCsproj -Encoding UTF8 -NoNewline

    # 6b. Fix Tavis.JsonPatch PackageReference -> local DLL.
    foreach ($proj in @('VSEssentials/VSEssentialsMod.csproj', 'VSSurvivalMod/VSSurvivalMod.csproj')) {
        $csproj = Join-Path $repoRoot $proj
        if (Test-Path $csproj) {
            $text = [IO.File]::ReadAllText($csproj)
            $patched = $text -replace '<PackageReference Include="Tavis\.JsonPatch" Version="[^"]*"\s*/>', '<Reference Include="Tavis.JsonPatch"><HintPath>..\.vanilla\vintagestory\Lib\Tavis.JsonPatch.dll</HintPath><Private>false</Private></Reference>'
            if ($patched -ne $text) { [IO.File]::WriteAllText($csproj, $patched) }
        }
    }

    # 6c. Fix Mapping ambiguity in ServerSystemUpnp.
    $upnp = Join-Path (Join-Path (Join-Path (Join-Path $repoRoot 'baseline') 'VintagestoryLib') 'Vintagestory.Server') 'ServerSystemUpnp.cs'
    if (Test-Path $upnp) {
        $text = [IO.File]::ReadAllText($upnp)
        $text = $text -replace '(?m)^\tprivate Mapping mapping;', "`tprivate Open.Nat.Mapping mapping;"
        $text = $text -replace '(?m)^\tprivate Mapping mappingUdp;', "`tprivate Open.Nat.Mapping mappingUdp;"
        $text = $text -replace '(?m)^\tprivate Mapping monoNatMapping;', "`tprivate Mono.Nat.Mapping monoNatMapping;"
        $text = $text -replace '(?m)^\tprivate Mapping monoNatMappingUdp;', "`tprivate Mono.Nat.Mapping monoNatMappingUdp;"
        $text = $text -replace 'mapping = new Mapping\(\(Protocol\)0', 'mapping = new Open.Nat.Mapping((Open.Nat.Protocol)0'
        $text = $text -replace 'mappingUdp = new Mapping\(\(Protocol\)1', 'mappingUdp = new Open.Nat.Mapping((Open.Nat.Protocol)1'
        $text = $text -replace 'monoNatMapping = new Mapping\(\(Protocol\)0', 'monoNatMapping = new Mono.Nat.Mapping((Mono.Nat.Protocol)0'
        $text = $text -replace 'monoNatMappingUdp = new Mapping\(\(Protocol\)1', 'monoNatMappingUdp = new Mono.Nat.Mapping((Mono.Nat.Protocol)1'
        [IO.File]::WriteAllText($upnp, $text)
    }

    # 6d. Fix ModContainer CustomAttributeNamedArgument ambiguity.
    $modcont = Join-Path (Join-Path (Join-Path (Join-Path $repoRoot 'baseline') 'VintagestoryLib') 'Vintagestory.Common') 'ModContainer.cs'
    if (Test-Path $modcont) {
        $text = [IO.File]::ReadAllText($modcont)
        $text = [regex]::Replace($text, '(?<!Mono\.Cecil\.)(?<!System\.Reflection\.)(?<=[\s(,])CustomAttributeNamedArgument(?!\w)', 'Mono.Cecil.CustomAttributeNamedArgument')
        [IO.File]::WriteAllText($modcont, $text)
    }

    Write-Host "Post-decompile fixups done."

    # 6e. Final pass: ref-casts, op_Implicit, GeneratedRegex across all baseline .cs files.
    Get-ChildItem -Path (Join-Path $repoRoot 'baseline') -Recurse -Filter '*.cs' | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName)
        $original = $text
        # ref-cast constructor: ((Type)(ref var))._002Ector(...) -> var = new Type(...)
        $text = [regex]::Replace($text, '\(\(([\w.]+)\)\(ref (\w+)\)\)\._002Ector\(', '$2 = new $1(')
        # ref-cast access: ((Type)(ref var)) -> var
        $text = [regex]::Replace($text, '\(\(([\w.]+)\)\(ref ([\w.]+)\)\)', '$2')
        # generic ref-cast
        $text = [regex]::Replace($text, '\([^)]*<[^>]+>\)\(ref (\w+)\)', '$1')
        # JToken.op_Implicit
        $text = [regex]::Replace($text, 'JToken\.op_Implicit\((.+?)\)', '(JToken)($1)')
        if ($text -ne $original) { [IO.File]::WriteAllText($_.FullName, $text) }
    }

    # GeneratedRegex stubs -> new Regex().
    Get-ChildItem -Path (Join-Path $repoRoot 'baseline') -Recurse -Filter '*.cs' | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName)
        $original = $text
        $text = [regex]::Replace($text,
            '\[GeneratedRegex\("([^"]+)"\)\]\s*\[GeneratedCode\([^\]]+\)\]\s*private static Regex (\w+)\(\)\s*\{[^}]+\}',
            "private static Regex `$2()`n`t{`n`t`treturn new Regex(`"`$1`", RegexOptions.Compiled);`n`t}")
        if ($text -ne $original) { [IO.File]::WriteAllText($_.FullName, $text) }
    }

    # MouseWheelEventArgs ambiguity (OpenTK vs Vintagestory.API.Client).
    $cpw = Join-Path (Join-Path (Join-Path (Join-Path $repoRoot 'baseline') 'VintagestoryLib') 'Vintagestory.Client.NoObf') 'ClientPlatformWindows.cs'
    if (Test-Path $cpw) {
        $text = [IO.File]::ReadAllText($cpw)
        $text = $text -replace 'private void Mouse_WheelChanged\(MouseWheelEventArgs e\)', 'private void Mouse_WheelChanged(OpenTK.Windowing.Common.MouseWheelEventArgs e)'
        $text = $text -replace 'MouseWheelEventArgs e2 = new MouseWheelEventArgs', 'Vintagestory.API.Client.MouseWheelEventArgs e2 = new Vintagestory.API.Client.MouseWheelEventArgs'
        [IO.File]::WriteAllText($cpw, $text)
    }

    # --- 7. Copy Optimum-only source files (second pass, after fixups) ---
    if (Test-Path $sourcesDir) {
        Get-ChildItem -Path $sourcesDir -Directory | ForEach-Object {
            $proj = $_.Name
            $dst = if ($vanillaPatchProjects -contains $proj) {
                Join-Path $repoRoot "baseline/$proj"
            } else {
                Join-Path $repoRoot $proj
            }
            if (-not (Test-Path $dst)) { return }
            Get-ChildItem -Path $_.FullName -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring((Join-Path $sourcesDir $proj).Length + 1)
                $target = Join-Path $dst $rel
                $targetDir = Split-Path $target
                if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
                Copy-Item -Force $_.FullName $target
            }
        }
        Write-Host "Synced sources/ into working tree."
    }

    # --- 8. Create solution file if missing ---
    $slnx = Join-Path $repoRoot 'VintageStory.slnx'
    if (-not (Test-Path $slnx)) {
        Write-Host "Creating VintageStory.slnx"
        @'
<Solution>
  <Folder Name="/Vanilla/">
    <Project Path="baseline/VintagestoryLib/VintagestoryLib.csproj" />
    <Project Path="baseline/Vintagestory/Vintagestory.csproj" />
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
'@ | Set-Content -Path $slnx -Encoding UTF8 -NoNewline
    }

    Write-Host ""
    Write-Host "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release" -ForegroundColor Green
} finally {
    Pop-Location
}
