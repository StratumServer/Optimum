#Requires -Version 5.1
# Tests the ilspycmd GAC shield construction and manifest invalidation in
# scripts/bootstrap.ps1 under both Windows PowerShell 5.1 and PowerShell 7.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bootstrapPs1 = Join-Path $repoRoot 'scripts/bootstrap.ps1'
$bootstrapContent = [System.IO.File]::ReadAllText($bootstrapPs1)

if (-not $bootstrapContent.Contains('gac-shield=v1')) {
    throw "Expected gac-shield=v1 manifest marker in $bootstrapPs1"
}
if (-not $bootstrapContent.Contains('if ($gacShieldDir) { $ilspyArgs += @(''-r'', $gacShieldDir) }')) {
    throw "Expected -r gacShieldDir ilspycmd invocation in $bootstrapPs1"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("optimum-gac-shield-test-" + [Guid]::NewGuid().ToString('N'))
$gacShieldDir = $null
try {
    $winVanillaDir = Join-Path $testRoot '.vanilla/win-x64/vintagestory'
    $vanillaLibDir = Join-Path $winVanillaDir 'Lib'
    New-Item -ItemType Directory -Force -Path $vanillaLibDir | Out-Null

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $winVanillaDir 'VintagestoryAPI.dll'), 'root-api', $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $vanillaLibDir 'Newtonsoft.Json.dll'), 'lib-json', $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $vanillaLibDir 'Mono.Cecil.dll'), 'lib-cecil', $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $vanillaLibDir 'VintagestoryAPI.dll'), 'lib-dup', $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $vanillaLibDir 'readme.txt'), 'not-a-dll', $utf8NoBom)

    $gacShieldDir = Join-Path ([System.IO.Path]::GetTempPath()) ("optimum-gac-shield-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $gacShieldDir | Out-Null
    Get-ChildItem -LiteralPath $vanillaLibDir -Filter '*.dll' -File | ForEach-Object {
        if (-not (Test-Path -LiteralPath (Join-Path $winVanillaDir $_.Name) -PathType Leaf)) {
            [System.IO.File]::WriteAllBytes((Join-Path $gacShieldDir $_.Name), [byte[]]@())
        }
    }

    foreach ($expected in @('Newtonsoft.Json.dll', 'Mono.Cecil.dll')) {
        $placeholder = Join-Path $gacShieldDir $expected
        if (-not (Test-Path -LiteralPath $placeholder -PathType Leaf)) {
            throw "Missing $expected placeholder in shield directory"
        }
        if ((Get-Item -LiteralPath $placeholder).Length -ne 0) {
            throw "Expected 0-byte placeholder for $expected"
        }
    }

    if (Test-Path -LiteralPath (Join-Path $gacShieldDir 'VintagestoryAPI.dll')) {
        throw 'Root assembly VintagestoryAPI.dll must not be shadowed in shield directory'
    }
    if (Test-Path -LiteralPath (Join-Path $gacShieldDir 'readme.txt')) {
        throw 'Non-DLL file should not be placed in shield directory'
    }

    # Verify legacy 2-line manifest does not match 3-line gac-shield=v1 manifest
    $manifestPath = Join-Path $testRoot 'VintagestoryLib.manifest'
    $dllHash = 'ABC123'
    $verLine = 'ilspycmd: 11.0.0.9375'
    $expectedManifest = "$dllHash`n$verLine`ngac-shield=v1"

    [System.IO.File]::WriteAllText($manifestPath, "$dllHash`n$verLine", [System.Text.Encoding]::UTF8)
    if ((Get-Content -LiteralPath $manifestPath -Raw).TrimEnd() -eq $expectedManifest) {
        throw 'Legacy 2-line decompile manifest should not match gac-shield=v1'
    }

    [System.IO.File]::WriteAllText($manifestPath, $expectedManifest, [System.Text.Encoding]::UTF8)
    if ((Get-Content -LiteralPath $manifestPath -Raw).TrimEnd() -ne $expectedManifest) {
        throw 'Updated 3-line decompile manifest failed to match'
    }

    if (-not $bootstrapContent.Contains('$script:ResolvedIlspycmdVersionLine')) {
        throw "Expected cached ilspycmd version reuse in $bootstrapPs1"
    }

    $cacheOut = Join-Path $testRoot 'cache/VintagestoryLib'
    New-Item -ItemType Directory -Force -Path $cacheOut | Out-Null
    function Test-DecompileCache([string]$OutDir, [string]$DllBase, [string]$ManifestFile, [string]$Expected) {
        $hasExpectedProject = (Test-Path -LiteralPath (Join-Path $OutDir "$DllBase.csproj") -PathType Leaf) -or
            (Test-Path -LiteralPath (Join-Path $OutDir "$($DllBase).vanilla.csproj") -PathType Leaf)
        $cachedSource = if (Test-Path -LiteralPath $OutDir -PathType Container) {
            @(Get-ChildItem -Path $OutDir -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue | Select-Object -First 1)
        } else { @() }
        $valid = $hasExpectedProject -and $cachedSource.Count -gt 0 -and (Test-Path -LiteralPath $ManifestFile -PathType Leaf)
        if ($valid) {
            $valid = (Get-Content -LiteralPath $ManifestFile -Raw).TrimEnd() -eq $Expected
        }
        return $valid
    }

    if (Test-DecompileCache $cacheOut 'VintagestoryLib' $manifestPath $expectedManifest) {
        throw 'Empty cache directory with manifest must be rejected'
    }
    [System.IO.File]::WriteAllText((Join-Path $cacheOut 'VintagestoryLib.csproj'), '<Project />', $utf8NoBom)
    if (Test-DecompileCache $cacheOut 'VintagestoryLib' $manifestPath $expectedManifest) {
        throw 'Cache directory without .cs source files must be rejected'
    }
    [System.IO.File]::WriteAllText((Join-Path $cacheOut 'Dummy.cs'), 'class C {}', $utf8NoBom)
    if (-not (Test-DecompileCache $cacheOut 'VintagestoryLib' $manifestPath $expectedManifest)) {
        throw 'Complete cache directory with matching manifest, csproj, and .cs file must be accepted'
    }
    if (Test-DecompileCache $cacheOut 'VintagestoryLib' $manifestPath "WRONG`n$verLine`ngac-shield=v1") {
        throw 'Cache directory with mismatched input hash must be rejected'
    }

    Write-Host "GAC shield test passed under PowerShell $($PSVersionTable.PSVersion)."
} finally {
    if ($gacShieldDir -and (Test-Path -LiteralPath $gacShieldDir)) {
        Remove-Item -LiteralPath $gacShieldDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
