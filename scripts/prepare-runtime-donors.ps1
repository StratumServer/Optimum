[CmdletBinding()]
param(
    [string]$VanillaDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
. "$scriptDir/_exec.ps1"
if (-not $VanillaDir) {
    $VanillaDir = Join-Path $repoRoot '.vanilla/win-x64/vintagestory'
}
$VanillaDir = [IO.Path]::GetFullPath($VanillaDir)
$runtimeRoot = Join-Path $repoRoot '.build/runtime-donors'
$outputRoot = Join-Path $repoRoot "bin/$Configuration/net10.0"
$contractsDll = Join-Path $outputRoot 'Optimum.Api.Contracts.dll'
$gameContentDll = Join-Path $outputRoot 'Optimum.GameContent.dll'

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
    (Join-Path $VanillaDir 'VintagestoryAPI.dll'),
    (Join-Path $VanillaDir 'Mods/VSEssentials.dll'),
    (Join-Path $VanillaDir 'Mods/VSEssentials.pdb'),
    (Join-Path $VanillaDir 'Mods/VSCreativeMod.dll'),
    (Join-Path $VanillaDir 'Mods/VSSurvivalMod.dll'),
    (Join-Path $VanillaDir 'Mods/VSSurvivalMod.pdb'),
    $contractsDll,
    $gameContentDll
)
foreach ($path in $required) {
    if (-not (Test-Path $path)) {
        throw "Required runtime-donor input not found: $path"
    }
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
            $VanillaDir,
            (Join-Path $VanillaDir 'Lib'),
            (Join-Path $VanillaDir 'Mods'))) {
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
    $hintRoot = $VanillaDir.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $text = $text.Replace(
        '<HintPath>.vanilla/win-x64/vintagestory/',
        "<HintPath>$(ConvertTo-XmlText $hintRoot)")
    $text = [regex]::Replace($text,
        '<LangVersion>\d+\.\d+</LangVersion>',
        '<LangVersion>preview</LangVersion>')
    $text = [regex]::Replace(
        $text,
        '</PropertyGroup>',
        "  <Nullable>disable</Nullable>`r`n    <NoWarn>`$(NoWarn);0618;8632;0420;0649;0169;9193;9113</NoWarn>`r`n  </PropertyGroup>",
        1)
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
    $text = [regex]::Replace($text, '<ItemGroup>', $reference, 1)
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
    $pattern = '<Reference Include="' + [regex]::Escape($Include) + '"\s*/>'
    $text = [regex]::Replace($text, $pattern, $reference, 1)
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
        (Join-Path $VanillaDir 'Lib'),
        $VanillaDir,
        (Join-Path $VanillaDir 'Mods')
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
    (Join-Path $VanillaDir 'Mods/VSEssentials.dll')
$survivalProject = Decompile-Mod `
    'VSSurvivalMod' `
    (Join-Path $VanillaDir 'Mods/VSSurvivalMod.dll')

Add-Reference $essentialsProject 'Optimum.Api.Contracts' $contractsDll
Add-Reference $essentialsProject 'Optimum.GameContent' $gameContentDll
Add-Reference $survivalProject 'Optimum.Api.Contracts' $contractsDll
Set-ReferenceHintPath `
    $survivalProject `
    'VSCreativeMod' `
    (Join-Path $VanillaDir 'Mods/VSCreativeMod.dll')
Exclude-CompileItems $survivalProject @(
    'Vintagestory/GameContent/ModSystemVillagerDebug.cs',
    'Vintagestory/ServerMods/ChiselBlockBulkSetMaterial.cs',
    'Vintagestory/ServerMods/UpgradeTasks.cs'
)
Resolve-ProjectReferences $essentialsProject
Resolve-ProjectReferences $survivalProject

foreach ($project in @('VSEssentials', 'VSSurvivalMod')) {
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
    Invoke-NativeStep { & dotnet build $essentialsProject -c $Configuration --nologo }
    if ($LASTEXITCODE -ne 0) {
        $buildErrors += 'VSEssentials'
    }
    Write-Host "  Building VSSurvivalMod..."
    Invoke-NativeStep { & dotnet build $survivalProject -c $Configuration --nologo }
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
