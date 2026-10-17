[CmdletBinding()]
param(
    [string]$VanillaDir,
    [string]$RuntimeDonorDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
. "$scriptDir/_exec.ps1"

# Resolve dotnet the same way ilspycmd is resolved below: prefer one on PATH,
# then fall back to the user-profile install at ~/.dotnet. The installer and
# bootstrap can place the SDK under %USERPROFILE%\.dotnet without adding it to
# PATH, so a bare "dotnet" call from here found no SDK and the donor build died
# with "No .NET SDKs were found" (issue #90). When the resolved dotnet lives in
# the profile install, DOTNET_ROOT has to point at it so the host discovers the
# SDK there, matching what IlspycmdInstaller.cs does for the tool it spawns.
#
# Issue #105: a PATH dotnet.exe that exists but has no SDK installed caused the
# script to accept it immediately without checking whether any SDK is visible.
#
# Issue #138: a PATH dotnet.exe with SDKs that cannot resolve the repository
# global.json (for example, only SDK 9 is installed when 10.0.100 is pinned)
# must not be accepted over a user-profile candidate that has the required SDK.
function script:Test-DotNetResolvesGlobalJson([string]$DotnetExe, [string]$RepoRoot) {
    if (-not (Test-Path $DotnetExe)) { return $false }
    $dotnetDir = Split-Path -Parent $DotnetExe
    $oldDotnetRoot = $env:DOTNET_ROOT
    try {
        $env:DOTNET_ROOT = $dotnetDir
        # 1. Evaluate global.json directly via dotnet --version inside $RepoRoot.
        # This honors the exact version and rollForward policy configured in global.json.
        $prevLocation = Get-Location
        try {
            Set-Location $RepoRoot
            $versionOutput = & $DotnetExe --version 2>$null
            if ($LASTEXITCODE -eq 0 -and $versionOutput) {
                return $true
            }
        } finally {
            Set-Location $prevLocation
        }

        # 2. Fallback for mock environments: inspect --list-sdks for a matching major line.
        $sdks = & $DotnetExe --list-sdks 2>$null
        $globalJsonPath = Join-Path $RepoRoot 'global.json'
        $requiredMajor = '10'
        if (Test-Path $globalJsonPath) {
            try {
                $gj = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
                if ($gj.sdk.version -match '^(\d+)\.') {
                    $requiredMajor = $Matches[1]
                }
            } catch { }
        }
        $majorPattern = '^' + [regex]::Escape($requiredMajor) + '\.'
        return [bool]($sdks -and ($sdks | Where-Object { $_ -match $majorPattern }))
    } finally {
        $env:DOTNET_ROOT = $oldDotnetRoot
    }
}

function script:Test-DotNetHasSdk([string]$DotnetExe, [string]$Repo = $repoRoot) {
    return (Test-DotNetResolvesGlobalJson $DotnetExe $Repo)
}

$profileRoot = if ($env:USERPROFILE) { $env:USERPROFILE } else { $env:HOME }
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$profileDotnetExe = Join-Path $profileRoot '.dotnet/dotnet.exe'
if (-not (Test-Path $profileDotnetExe)) {
    $profileDotnetExe = Join-Path $profileRoot '.dotnet/dotnet'
}

if ($dotnetCmd -and (Test-DotNetResolvesGlobalJson $dotnetCmd.Source $repoRoot)) {
    $dotnetPath = $dotnetCmd.Source
} elseif ((Test-Path $profileDotnetExe) -and (Test-DotNetResolvesGlobalJson $profileDotnetExe $repoRoot)) {
    $dotnetPath = $profileDotnetExe
} else {
    # Neither candidate could resolve the repository's global.json.
    $requiredSdk = '10.0.100'
    $globalJsonPath = Join-Path $repoRoot 'global.json'
    if (Test-Path $globalJsonPath) {
        try {
            $gj = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
            if ($gj.sdk.version) { $requiredSdk = $gj.sdk.version }
        } catch { }
    }

    if ($dotnetCmd) {
        $sdks = & $dotnetCmd.Source --list-sdks 2>$null
        $sdkList = if ($sdks) { ($sdks -join '; ') } else { 'none' }
        throw "Incompatible .NET SDK candidate: $($dotnetCmd.Source) (installed: $sdkList). Required SDK: $requiredSdk (from global.json). Install the required .NET SDK or run scripts/bootstrap.ps1."
    } elseif (Test-Path $profileDotnetExe) {
        $sdks = & $profileDotnetExe --list-sdks 2>$null
        $sdkList = if ($sdks) { ($sdks -join '; ') } else { 'none' }
        throw "Incompatible .NET SDK candidate: $profileDotnetExe (installed: $sdkList). Required SDK: $requiredSdk (from global.json). Install the required .NET SDK or run scripts/bootstrap.ps1."
    } else {
        throw "dotnet is required. Install .NET SDK $requiredSdk or run scripts/bootstrap.ps1 first."
    }
}
$dotnetDir = Split-Path -Parent $dotnetPath
$profileDotnet = [IO.Path]::GetFullPath((Join-Path $profileRoot '.dotnet'))
if ($dotnetDir -ieq $profileDotnet) {
    $env:DOTNET_ROOT = $dotnetDir
}

if (-not $VanillaDir) {
    $VanillaDir = Join-Path $repoRoot '.vanilla/win-x64/vintagestory'
}
$VanillaDir = [IO.Path]::GetFullPath($VanillaDir)
$runtimeDonorParent = Split-Path -Parent $VanillaDir
$runtimeDonorDir = if ($RuntimeDonorDir) {
    [IO.Path]::GetFullPath($RuntimeDonorDir)
} elseif ($env:RUNTIME_DONOR_DIR) {
    [IO.Path]::GetFullPath($env:RUNTIME_DONOR_DIR)
} else {
    Join-Path $runtimeDonorParent 'runtime-donors'
}
$runtimeRoot = Join-Path $repoRoot '.build/runtime-donors'
$outputRoot = Join-Path $repoRoot "bin/$Configuration/net10.0"
$contractsDll = Join-Path $outputRoot 'Optimum.Api.Contracts.dll'
$gameContentDll = Join-Path $outputRoot 'Optimum.GameContent.dll'
$apiDll = Join-Path $outputRoot 'VintagestoryAPI.dll'

function Convert-ToLf([string]$Root) {
    if (-not (Test-Path $Root)) { return }
    Get-ChildItem -Path $Root -Recurse -File -Filter '*.patch' | ForEach-Object {
        $bytes = [IO.File]::ReadAllBytes($_.FullName)
        if ($bytes -contains 13) {
            $text = [Text.Encoding]::UTF8.GetString($bytes) -creplace ([char]13 + [char]10), [char]10 -creplace [char]13, [char]10
            [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($text))
        }
    }
}

Convert-ToLf (Join-Path $repoRoot 'patches/runtime')

$ilspy = Get-Command ilspycmd -ErrorAction SilentlyContinue
if (-not $ilspy) {
    $profileRoot = if ($env:USERPROFILE) { $env:USERPROFILE } else { $env:HOME }
    $globalTool = Join-Path $profileRoot '.dotnet/tools/ilspycmd'
    if (-not (Test-Path $globalTool)) {
        $globalTool = Join-Path $profileRoot '.dotnet/tools/ilspycmd.exe'
    }
    if (-not (Test-Path $globalTool)) {
        throw 'ilspycmd is required. Run scripts/bootstrap.ps1 first.'
    }
    $ilspyPath = $globalTool
} else {
    $ilspyPath = $ilspy.Source
}

$required = @(
    (Join-Path $runtimeDonorDir 'VintagestoryAPI.dll'),
    (Join-Path $runtimeDonorDir 'Mods/VSEssentials.dll'),
    (Join-Path $runtimeDonorDir 'Mods/VSEssentials.pdb'),
    (Join-Path $runtimeDonorDir 'Mods/VSCreativeMod.dll'),
    (Join-Path $runtimeDonorDir 'Mods/VSSurvivalMod.dll'),
    (Join-Path $runtimeDonorDir 'Mods/VSSurvivalMod.pdb'),
    (Join-Path $runtimeDonorDir 'runtime-donor-version.txt'),
    (Join-Path $runtimeDonorDir 'runtime-donor-manifest.sha256'),
    $apiDll,
    $contractsDll,
    $gameContentDll
)
foreach ($path in $required) {
    if (-not (Test-Path $path)) {
        throw "Required runtime-donor input not found: $path"
    }
}

$manifestEntries = Get-Content -LiteralPath (Join-Path $runtimeDonorDir 'runtime-donor-manifest.sha256')
$manifestPaths = @()
foreach ($entry in $manifestEntries) {
    if ($entry -notmatch '^([0-9a-fA-F]{64})\s{2}(.+)$') {
        throw "Runtime donor manifest has an invalid entry: $entry"
    }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $relativePath = $Matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
    $relativePath = $relativePath.TrimStart('.', '\', '/')
    $manifestPaths += $relativePath
    $inputPath = Join-Path $runtimeDonorDir $relativePath
    if (-not (Test-Path $inputPath -PathType Leaf)) {
        throw "Runtime donor manifest references a missing file: $relativePath"
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $inputPath).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Runtime donor snapshot failed integrity validation: $runtimeDonorDir"
    }
}
$actualPaths = @(Get-ChildItem -Path $runtimeDonorDir -Recurse -File |
    Where-Object { $_.Name -ne 'runtime-donor-manifest.sha256' } |
    ForEach-Object { $_.FullName.Substring($runtimeDonorDir.Length + 1) } |
    Sort-Object)
$sortedManifestPaths = @($manifestPaths | Sort-Object)
if (($actualPaths -join "`n") -ne ($sortedManifestPaths -join "`n")) {
    throw "Runtime donor snapshot contains an unexpected file set: $runtimeDonorDir"
}
$liveVersionMarker = Get-ChildItem -Path (Join-Path $VanillaDir 'assets') -Filter 'version-*.txt' -File |
    Select-Object -First 1
$snapshotVersion = (Get-Content -LiteralPath (Join-Path $runtimeDonorDir 'runtime-donor-version.txt') -Raw).Trim()
if (-not $liveVersionMarker -or $liveVersionMarker.Name -ne $snapshotVersion) {
    throw "Runtime donor snapshot version does not match the selected vanilla client."
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $apiDll).Hash -eq
    (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $runtimeDonorDir 'VintagestoryAPI.dll')).Hash) {
    throw 'Compiled VintagestoryAPI.dll matches the vanilla snapshot. Rebuild the patched API before donor validation.'
}

if (Test-Path $runtimeRoot) {
    Remove-Item -Recurse -Force $runtimeRoot
}
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

function Invoke-Checked {
    param([scriptblock]$Command, [string]$Failure)
    Invoke-NativeStep { & $Command }
    if ($LASTEXITCODE -ne 0) {
        throw "$Failure (exit code $LASTEXITCODE)."
    }
}

function ConvertTo-XmlText {
    param([string]$Value)
    return $Value -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;'
}

function Decompile-Mod {
    param([string]$Project, [string]$Assembly)
    $output = Join-Path $runtimeRoot $Project
    Write-Host "Decompiling exact $Project runtime donor..."
    $referenceArgs = @()
    foreach ($referenceDir in @(
            $outputRoot,
            $runtimeDonorDir,
            (Join-Path $runtimeDonorDir 'Lib'),
            (Join-Path $runtimeDonorDir 'Mods'))) {
        if (Test-Path $referenceDir -PathType Container) {
            $referenceArgs += @('--referencepath', $referenceDir)
        } else {
            Write-Warning "Vanilla reference directory not found: $referenceDir"
        }
    }
    Invoke-Checked {
        & $ilspyPath `
            --project `
            --nested-directories `
            $referenceArgs `
            --outputdir $output `
            $Assembly | Out-Null
    } "ilspycmd failed for $Project"

    $projectFile = Get-ChildItem -Path $output -Filter '*.csproj' -File |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $projectFile) {
        throw "ilspycmd did not create a project for $Project."
    }

    $text = [IO.File]::ReadAllText($projectFile)
    $hintRoot = $runtimeDonorDir.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $escapedHintRoot = ConvertTo-XmlText $hintRoot
    $vanillaRoot = $VanillaDir.TrimEnd('\', '/')
    $hintPrefixes = @(
        '.vanilla/win-x64/vintagestory/',
        '.vanilla\win-x64\vintagestory\',
        "$vanillaRoot/",
        "$vanillaRoot\",
        ($vanillaRoot.Replace('\', '/') + '/')
    ) | Select-Object -Unique
    foreach ($prefix in $hintPrefixes) {
        $pattern = '<HintPath>' + [regex]::Escape($prefix)
        $replacement = ('<HintPath>' + $escapedHintRoot).Replace('$', '$$')
        $text = [regex]::Replace(
            $text,
            $pattern,
            $replacement,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    $text = [regex]::Replace($text,
        '<LangVersion>\d+\.\d+</LangVersion>',
        '<LangVersion>preview</LangVersion>')
    $text = ([regex]'</PropertyGroup>').Replace(
        $text,
        "  <Nullable>disable</Nullable>`r`n    <NoWarn>`$(NoWarn);0618;8632;0420;0649;0169;9193;9113</NoWarn>`r`n  </PropertyGroup>",
        1)
    if ($text -notmatch '<FrameworkReference\s+Include="Microsoft\.NETCore\.App"\s*/>') {
        $text = ([regex]'</PropertyGroup>').Replace(
            $text,
            "</PropertyGroup>`r`n  <ItemGroup>`r`n    <FrameworkReference Include=`"Microsoft.NETCore.App`" />`r`n  </ItemGroup>",
            1)
    }
    [IO.File]::WriteAllText($projectFile, $text)
    return $projectFile
}

function Add-Reference {
    param([string]$ProjectFile, [string]$Include, [string]$HintPath)
    $text = [IO.File]::ReadAllText($ProjectFile)
    $escapedHintPath = ConvertTo-XmlText $HintPath
    $reference = @"
<ItemGroup>
    <Reference Include="$Include">
      <HintPath>$escapedHintPath</HintPath>
      <Private>false</Private>
    </Reference>
"@
    $text = ([regex]'<ItemGroup>').Replace($text, $reference, 1)
    [IO.File]::WriteAllText($ProjectFile, $text)
}

function Set-ReferenceHintPath {
    param([string]$ProjectFile, [string]$Include, [string]$HintPath)
    $text = [IO.File]::ReadAllText($ProjectFile)
    $escapedHintPath = ConvertTo-XmlText $HintPath
    $reference = @"
<Reference Include="$Include">
      <HintPath>$escapedHintPath</HintPath>
      <Private>false</Private>
    </Reference>
"@
    $pattern = '<Reference Include="' + [regex]::Escape($Include) + '">[\s\S]*?</Reference>|<Reference Include="' + [regex]::Escape($Include) + '"\s*/>'
    $text = ([regex]$pattern).Replace($text, $reference, 1)
    [IO.File]::WriteAllText($ProjectFile, $text)
}

function Exclude-CompileItems {
    param([string]$ProjectFile, [string[]]$Items)
    $text = [IO.File]::ReadAllText($ProjectFile)
    $removes = New-Object System.Text.StringBuilder
    [void]$removes.AppendLine('<ItemGroup Label="Runtime donor excludes">')
    foreach ($item in $Items) {
        [void]$removes.AppendLine(('    <Compile Remove="{0}" />' -f $item))
    }
    [void]$removes.AppendLine('</ItemGroup>')
    $text = $text.Replace('</Project>', $removes.ToString() + '</Project>')
    [IO.File]::WriteAllText($ProjectFile, $text)
}

function Resolve-ProjectReferences {
    param([string]$ProjectFile)
    $text = [IO.File]::ReadAllText($ProjectFile)
    $searchPaths = @(
        (Join-Path $runtimeDonorDir 'Lib'),
        $runtimeDonorDir,
        (Join-Path $runtimeDonorDir 'Mods')
    )
    # Pass 1: Self-closing references without HintPath
    $text = [regex]::Replace($text, '<Reference Include="([^"]+)"\s*/>', {
        param($m)
        $assemblyName = $m.Groups[1].Value
        foreach ($dir in $searchPaths) {
            $probe = Join-Path $dir "$assemblyName.dll"
            if (Test-Path $probe) {
                return ('<Reference Include="{0}"><HintPath>{1}</HintPath><Private>false</Private></Reference>' -f $assemblyName, (ConvertTo-XmlText $probe))
            }
        }
        return $m.Value
    })
    # Pass 2: References with HintPath that does not exist on disk
    $text = [regex]::Replace($text, '<Reference Include="([^"]+)">\s*<HintPath>([^<]+)</HintPath>\s*</Reference>', {
        param($m)
        $assemblyName = $m.Groups[1].Value
        $hintPath = $m.Groups[2].Value
        if (Test-Path $hintPath) {
            return $m.Value
        }
        foreach ($dir in $searchPaths) {
            $probe = Join-Path $dir "$assemblyName.dll"
            if (Test-Path $probe) {
                return ('<Reference Include="{0}"><HintPath>{1}</HintPath><Private>false</Private></Reference>' -f $assemblyName, (ConvertTo-XmlText $probe))
            }
        }
        return $m.Value
    })
    [IO.File]::WriteAllText($ProjectFile, $text)
}

$essentialsProject = Decompile-Mod `
    'VSEssentials' `
    (Join-Path $runtimeDonorDir 'Mods/VSEssentials.dll')
$survivalProject = Decompile-Mod `
    'VSSurvivalMod' `
    (Join-Path $runtimeDonorDir 'Mods/VSSurvivalMod.dll')

Add-Reference $essentialsProject 'Optimum.Api.Contracts' $contractsDll
Add-Reference $essentialsProject 'Optimum.GameContent' $gameContentDll
Add-Reference $survivalProject 'Optimum.Api.Contracts' $contractsDll
Set-ReferenceHintPath $essentialsProject 'VintagestoryAPI' $apiDll
Set-ReferenceHintPath $survivalProject 'VintagestoryAPI' $apiDll
Set-ReferenceHintPath `
    $survivalProject `
    'VSCreativeMod' `
    (Join-Path $runtimeDonorDir 'Mods/VSCreativeMod.dll')
Exclude-CompileItems $survivalProject @(
    'Vintagestory/GameContent/ModSystemVillagerDebug.cs',
    'Vintagestory/ServerMods/ChiselBlockBulkSetMaterial.cs',
    'Vintagestory/ServerMods/UpgradeTasks.cs'
)
Resolve-ProjectReferences $essentialsProject
Resolve-ProjectReferences $survivalProject

$runtimeProjects = @('VSEssentials', 'VSSurvivalMod')
$runtimePatchFailures = @()
foreach ($project in $runtimeProjects) {
    $patchRoot = Join-Path $repoRoot "patches/runtime/$project"
    Get-ChildItem -Path $patchRoot -Filter '*.patch' -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $patchPath = $_.FullName
            $patchOutput = Invoke-NativeStep {
                & git -C $repoRoot apply --check --directory='.build/runtime-donors' --whitespace=nowarn $patchPath 2>&1 |
                    Out-String
            }
            $patchExitCode = $LASTEXITCODE
            if ($patchExitCode -ne 0) {
                $details = ([string]$patchOutput).Trim()
                if (-not $details) {
                    $details = 'git apply --check failed without diagnostic output.'
                }
                $runtimePatchFailures += "$project requires a patch refresh for $($_.Name): $details"
            }
        }
}
if ($runtimePatchFailures.Count -gt 0) {
    throw "Runtime donor compatibility gate failed:`n  $($runtimePatchFailures -join "`n  ")"
}

foreach ($project in $runtimeProjects) {
    $patchRoot = Join-Path $repoRoot "patches/runtime/$project"
    Get-ChildItem -Path $patchRoot -Filter '*.patch' -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            Invoke-Checked {
                git -C $repoRoot apply `
                    --directory='.build/runtime-donors' `
                    --whitespace=nowarn `
                    $_.FullName
            } "Could not apply runtime patch $($_.FullName)"
        }
}

Copy-Item -Force `
    (Join-Path $repoRoot 'sources/VSEssentials/Systems/OptimumStatus.cs') `
    (Join-Path $runtimeRoot 'VSEssentials/Vintagestory/GameContent/OptimumStatusModSystem.cs')
Copy-Item -Force `
    (Join-Path $repoRoot 'sources/VSSurvivalMod/BlockEntityRenderer/CrucibleInFirepitRenderer.cs') `
    (Join-Path $runtimeRoot 'VSSurvivalMod/Vintagestory/GameContent/CrucibleInFirepitRenderer.cs')
Copy-Item -Force `
    (Join-Path $repoRoot 'sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs') `
    (Join-Path $runtimeRoot 'VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitShapeCache.cs')
Copy-Item -Force `
    (Join-Path $repoRoot 'sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs') `
    (Join-Path $runtimeRoot 'VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitAnimatorCache.cs')
Copy-Item -Force `
    (Join-Path $repoRoot 'sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs') `
    (Join-Path $runtimeRoot 'VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitTexturePrewarmer.cs')

Write-Host 'Building exact runtime donors...'
$oldPlatform = $env:Platform
Remove-Item Env:Platform -ErrorAction SilentlyContinue
$buildErrors = @()
try {
    Write-Host "  Building VSEssentials..."
    Invoke-NativeStep { & $dotnetPath build $essentialsProject -c $Configuration --nologo }
    if ($LASTEXITCODE -ne 0) {
        $buildErrors += 'VSEssentials'
    }
    Write-Host "  Building VSSurvivalMod..."
    Invoke-NativeStep { & $dotnetPath build $survivalProject -c $Configuration --nologo }
    if ($LASTEXITCODE -ne 0) {
        $buildErrors += 'VSSurvivalMod'
    }
} finally {
    if ($null -ne $oldPlatform) {
        $env:Platform = $oldPlatform
    }
}

if ($buildErrors.Count -gt 0) {
    throw "Runtime donor build failed: $($buildErrors -join ', ')"
}

Write-Host 'Runtime donors ready under .build/runtime-donors.'
