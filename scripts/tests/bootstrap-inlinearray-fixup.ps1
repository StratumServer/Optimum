#Requires -Version 5.1
# Tests the post-decompile InlineArray<N> global:: normalization fixup under
# both Windows PowerShell 5.1 and PowerShell 7.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bootstrapPs1 = Join-Path $repoRoot 'scripts/bootstrap.ps1'
$pattern = '(?<!struct )(?<!global::)\b_003C_003Ey__InlineArray(\d+)<'
$replacement = 'global::_003C_003Ey__InlineArray$1<'

$bootstrapContent = [System.IO.File]::ReadAllText($bootstrapPs1)
if (-not $bootstrapContent.Contains($pattern)) {
    throw "Expected InlineArray normalization pattern in $bootstrapPs1"
}
if (-not $bootstrapContent.Contains($replacement)) {
    throw "Expected InlineArray normalization replacement in $bootstrapPs1"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("optimum-inlinearray-test-" + [Guid]::NewGuid().ToString('N'))
try {
    $libDir = Join-Path $testRoot 'build/VintagestoryLib'
    $commonDir = Join-Path $libDir 'Vintagestory.Common'
    $clientDir = Join-Path $libDir 'Vintagestory.Client.NoObf'
    $vsDir = Join-Path $testRoot 'build/Vintagestory'
    New-Item -ItemType Directory -Force -Path $commonDir, $clientDir, $vsDir | Out-Null

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $modContainerPath = Join-Path $commonDir 'ModContainer.cs'
    $chunkTesselatorPath = Join-Path $clientDir 'ChunkTesselator.cs'
    $inlineDeclPath = Join-Path $libDir '--y__InlineArray6.cs'

    [System.IO.File]::WriteAllText($modContainerPath, @"
namespace Vintagestory.Common;

public class ModContainer
{
	private void Load()
	{
		_003C_003Ey__InlineArray6<string> buffer = default(_003C_003Ey__InlineArray6<string>);
		_003C_003Ey__InlineArray5<object> other = default(_003C_003Ey__InlineArray5<object>);
	}
}
"@, $utf8NoBom)

    [System.IO.File]::WriteAllText($chunkTesselatorPath, @"
namespace Vintagestory.Client.NoObf;

public class ChunkTesselator
{
	private void Build()
	{
		global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
	}
}
"@, $utf8NoBom)

    [System.IO.File]::WriteAllText($inlineDeclPath, @"
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Auto)]
[InlineArray(6)]
internal struct _003C_003Ey__InlineArray6<T>
{
	[CompilerGenerated]
	private T _element0;
}
"@, $utf8NoBom)

    $decompiledDirs = @($libDir, $vsDir)
    for ($pass = 1; $pass -le 2; $pass++) {
        foreach ($root in $decompiledDirs) {
            if (-not (Test-Path $root)) { continue }
            Get-ChildItem -Path $root -Recurse -Filter '*.cs' -File | ForEach-Object {
                $t = [System.IO.File]::ReadAllText($_.FullName)
                $new = [regex]::Replace($t, $pattern, $replacement)
                if ($new -ne $t) {
                    [System.IO.File]::WriteAllText($_.FullName, $new, $utf8NoBom)
                }
            }
        }

        $modText = [System.IO.File]::ReadAllText($modContainerPath)
        $chunkText = [System.IO.File]::ReadAllText($chunkTesselatorPath)
        $declText = [System.IO.File]::ReadAllText($inlineDeclPath)

        if (-not $modText.Contains('global::_003C_003Ey__InlineArray6<string> buffer = default(global::_003C_003Ey__InlineArray6<string>);')) {
            throw "Pass ${pass}: ModContainer.cs InlineArray6 was not qualified with global::"
        }
        if (-not $modText.Contains('global::_003C_003Ey__InlineArray5<object> other = default(global::_003C_003Ey__InlineArray5<object>);')) {
            throw "Pass ${pass}: ModContainer.cs InlineArray5 was not qualified with global::"
        }
        if ($modText.Contains('global::global::') -or $chunkText.Contains('global::global::')) {
            throw "Pass ${pass}: Double global:: prefix introduced"
        }
        if (-not $declText.Contains('internal struct _003C_003Ey__InlineArray6<T>') -or $declText.Contains('global::')) {
            throw "Pass ${pass}: InlineArray struct declaration was corrupted"
        }
    }

    Write-Host "InlineArray fixup test passed under PowerShell $($PSVersionTable.PSVersion)."
} finally {
    if (Test-Path $testRoot) {
        Remove-Item -Recurse -Force $testRoot -ErrorAction SilentlyContinue
    }
}
