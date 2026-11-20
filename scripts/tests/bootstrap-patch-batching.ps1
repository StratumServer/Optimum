#Requires -Version 5.1
# Tests batched patch syntax validation and batched source patch application
# with atomic per-patch fallback in scripts/bootstrap.ps1 under both
# Windows PowerShell 5.1 and PowerShell 7.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bootstrapPs1 = Join-Path $repoRoot 'scripts/bootstrap.ps1'
$bootstrapContent = [System.IO.File]::ReadAllText($bootstrapPs1)

foreach ($needle in @(
    'function Invoke-GitApplyOptimumPatchBatch',
    'function Test-PatchSyntaxBatch',
    'Invoke-GitApplyOptimumPatchBatch -PatchPaths $patchBatch -DirArg $patchBatchDirArg -RepoRoot $repoRoot'
)) {
    if (-not $bootstrapContent.Contains($needle)) {
        throw "Expected '$needle' in $bootstrapPs1"
    }
}

# Extract helper functions from scripts/bootstrap.ps1 so the test exercises the exact code
$fnMatch = [regex]::Match(
    $bootstrapContent,
    '(?ms)^function Invoke-GitApplyOptimumPatch \{.*?^function Test-PatchSyntaxBatch.*?^    return , \$failures\r?\n\}'
)
if (-not $fnMatch.Success) {
    throw "Could not extract patch batching helper functions from $bootstrapPs1"
}
Invoke-Expression $fnMatch.Value

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("optimum-patch-batch-test-" + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($gitEnv in 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR', 'GIT_PREFIX') {
        Remove-Item -Path "Env:$gitEnv" -ErrorAction SilentlyContinue
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $patchDir = Join-Path $testRoot 'patches'
    New-Item -ItemType Directory -Force -Path (Join-Path $patchDir 'VintagestoryApi'), (Join-Path $patchDir 'VintagestoryLib') | Out-Null

    $patch1 = Join-Path $patchDir 'VintagestoryApi/01-First.patch'
    $patch2 = Join-Path $patchDir 'VintagestoryApi/02-Second.patch'
    $patchLib = Join-Path $patchDir 'VintagestoryLib/01-Lib.patch'

    [System.IO.File]::WriteAllText($patch1, @"
--- a/VintagestoryApi/ApiFile.cs
+++ b/VintagestoryApi/ApiFile.cs
@@ -1,4 +1,4 @@
 public class ApiFile
 {
-    public int ValueA() => 1;
+    public int ValueA() => 10;
 }
"@.Replace("`r`n", "`n") + "`n", $utf8NoBom)

    [System.IO.File]::WriteAllText($patch2, @"
--- a/VintagestoryApi/OtherFile.cs
+++ b/VintagestoryApi/OtherFile.cs
@@ -1,4 +1,4 @@
 public class OtherFile
 {
-    public int ValueB() => 2;
+    public int ValueB() => 20;
 }
"@.Replace("`r`n", "`n") + "`n", $utf8NoBom)

    [System.IO.File]::WriteAllText($patchLib, @"
--- a/VintagestoryLib/LibFile.cs
+++ b/VintagestoryLib/LibFile.cs
@@ -1,4 +1,4 @@
 public class LibFile
 {
-    public string Name() => "vanilla";
+    public string Name() => "optimum";
 }
"@.Replace("`r`n", "`n") + "`n", $utf8NoBom)

    # 1. Batch syntax check succeeds on valid patches and pinpoints invalid ones on fallback
    $validFailures = Test-PatchSyntaxBatch -PatchPaths @($patch1, $patch2, $patchLib) -RepoRoot $testRoot
    if ($validFailures.Count -ne 0) {
        throw "Expected 0 syntax failures for valid patches, got $($validFailures.Count)"
    }

    $badHeaderPatch = Join-Path $patchDir 'VintagestoryApi/98-NoHeader.patch'
    [System.IO.File]::WriteAllText($badHeaderPatch, "not a diff`n", $utf8NoBom)
    $badFailures = Test-PatchSyntaxBatch -PatchPaths @($patch1, $badHeaderPatch) -RepoRoot $testRoot
    if ($badFailures.Count -ne 1 -or $badFailures[0] -ne 'patches/VintagestoryApi/98-NoHeader.patch') {
        throw "Expected bad header patch to be reported, got: $($badFailures -join ', ')"
    }
    Remove-Item -LiteralPath $badHeaderPatch -Force

    # 2. Batched apply matches serial apply and handles fallback with -p0 and conflict diagnostics
    $workTree = Join-Path $testRoot 'work'
    New-Item -ItemType Directory -Force -Path (Join-Path $workTree 'VintagestoryApi'), (Join-Path $workTree 'build/VintagestoryLib') | Out-Null
    & git init -q $workTree

    $initialApi = @"
public class ApiFile
{
    public int ValueA() => 1;
}
"@.Replace("`r`n", "`n") + "`n"
    $initialOther = @"
public class OtherFile
{
    public int ValueB() => 2;
}
"@.Replace("`r`n", "`n") + "`n"
    $initialLib = @"
public class LibFile
{
    public string Name() => "vanilla";
}
"@.Replace("`r`n", "`n") + "`n"

    [System.IO.File]::WriteAllText((Join-Path $workTree 'VintagestoryApi/ApiFile.cs'), $initialApi, $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $workTree 'VintagestoryApi/OtherFile.cs'), $initialOther, $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $workTree 'build/VintagestoryLib/LibFile.cs'), $initialLib, $utf8NoBom)

    Push-Location $workTree
    try {
        $r1 = Invoke-GitApplyOptimumPatchBatch -PatchPaths @($patch1, $patch2) -DirArg '' -RepoRoot $testRoot
        $r2 = Invoke-GitApplyOptimumPatchBatch -PatchPaths @($patchLib) -DirArg '--directory=build' -RepoRoot $testRoot
        if ($r1.Applied -ne 2 -or $r1.Failed.Count -ne 0 -or $r2.Applied -ne 1 -or $r2.Failed.Count -ne 0) {
            throw "Clean batch apply failed: r1=$($r1.Applied)/$($r1.Failed.Count), r2=$($r2.Applied)/$($r2.Failed.Count)"
        }

        $apiAfterClean = [System.IO.File]::ReadAllText((Join-Path $workTree 'VintagestoryApi/ApiFile.cs'))
        $otherAfterClean = [System.IO.File]::ReadAllText((Join-Path $workTree 'VintagestoryApi/OtherFile.cs'))
        $libAfterClean = [System.IO.File]::ReadAllText((Join-Path $workTree 'build/VintagestoryLib/LibFile.cs'))
        if (-not $apiAfterClean.Contains('ValueA() => 10;') -or -not $otherAfterClean.Contains('ValueB() => 20;') -or -not $libAfterClean.Contains('"optimum"')) {
            throw 'Batched apply did not produce expected file contents'
        }

        # Reset and test atomic batch failure fallback (-p0 patch + conflicting patch)
        [System.IO.File]::WriteAllText((Join-Path $workTree 'VintagestoryApi/ApiFile.cs'), $initialApi, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $workTree 'VintagestoryApi/OtherFile.cs'), $initialOther, $utf8NoBom)
        $initialThird = @"
public class ThirdFile
{
    public int Flag() => 0;
}
"@.Replace("`r`n", "`n") + "`n"
        [System.IO.File]::WriteAllText((Join-Path $workTree 'VintagestoryApi/ThirdFile.cs'), $initialThird, $utf8NoBom)
        $patchP0 = Join-Path $patchDir 'VintagestoryApi/03-P0Fallback.patch'
        $patchConflict = Join-Path $patchDir 'VintagestoryApi/04-Conflict.patch'

        [System.IO.File]::WriteAllText($patchP0, @"
--- VintagestoryApi/ThirdFile.cs
+++ VintagestoryApi/ThirdFile.cs
@@ -1,4 +1,4 @@
 public class ThirdFile
 {
-    public int Flag() => 0;
+    public int Flag() => 1;
 }
"@.Replace("`r`n", "`n") + "`n", $utf8NoBom)

        [System.IO.File]::WriteAllText($patchConflict, @"
--- a/VintagestoryApi/ApiFile.cs
+++ b/VintagestoryApi/ApiFile.cs
@@ -1,4 +1,4 @@
 public class ApiFile
 {
-    public int NonExistent() => 999;
+    public int NonExistent() => 1000;
 }
"@.Replace("`r`n", "`n") + "`n", $utf8NoBom)

        $rFallback = Invoke-GitApplyOptimumPatchBatch -PatchPaths @($patch1, $patch2, $patchP0, $patchConflict) -DirArg '' -RepoRoot $testRoot
        if ($rFallback.Applied -ne 3 -or $rFallback.Failed.Count -ne 1 -or $rFallback.Failed[0] -ne 'patches/VintagestoryApi/04-Conflict.patch') {
            throw "Fallback batch apply returned unexpected counts: Applied=$($rFallback.Applied), Failed=$($rFallback.Failed -join ', ')"
        }
        $thirdAfterFallback = [System.IO.File]::ReadAllText((Join-Path $workTree 'VintagestoryApi/ThirdFile.cs'))
        if (-not $thirdAfterFallback.Contains('Flag() => 1;')) {
            throw 'Expected -p0 fallback patch to be applied during batch fallback'
        }
    } finally {
        Pop-Location
    }

    Write-Host "PowerShell patch batching test passed under PowerShell $($PSVersionTable.PSVersion)."
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$global:LASTEXITCODE = 0
exit 0
