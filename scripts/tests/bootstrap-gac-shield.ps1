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

    Write-Host "GAC shield test passed under PowerShell $($PSVersionTable.PSVersion)."
} finally {
    if ($gacShieldDir -and (Test-Path -LiteralPath $gacShieldDir)) {
        Remove-Item -LiteralPath $gacShieldDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
