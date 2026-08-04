<#
.SYNOPSIS
    Worldgen throughput benchmark on Windows (native).
    Measures spawn-chunk generation time (log marker "Loading ... spawn chunks..."
    to "Entering runphase RunGame") across worker counts 0 (serial) through MaxWorkers.

.PARAMETER Runs
    Runs per mode. Default 3.

.PARAMETER MaxWorkers
    Highest worker count to test. Default: ProcessorCount - 1 (capped at 6).

.PARAMETER SpawnChunksWidth
    Override for spawn chunk columns. Default 15 (matches the bash benchmark).

.PARAMETER TimeoutSeconds
    Max wait per run before declaring failure. Default 420.

.EXAMPLE
    .\scripts\worldgen-benchmark.ps1
    .\scripts\worldgen-benchmark.ps1 -Runs 5 -MaxWorkers 4
#>
param(
    [int]$Runs = 3,
    [int]$MaxWorkers = [Math]::Min(6, [Environment]::ProcessorCount - 1),
    [int]$SpawnChunksWidth = 15,
    [int]$TimeoutSeconds = 420
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$VanillaServer = Join-Path $ProjectRoot '.vanilla\win-x64\vintagestory'
$PatchedDlls = Join-Path $ProjectRoot 'bin\Release\net10.0'
$PatchedLibDll = Join-Path $ProjectRoot 'build\VintagestoryLib\bin\Release\net10.0\VintagestoryLib-patched.dll'
$BenchDir = Join-Path $ProjectRoot '.worldgen-benchmark-win'
$MagicNumbersTemplate = Join-Path $ProjectRoot '.worldgen-parity-baseline\servermagicnumbers.json'
$Seed = '42424242'

# Validate prerequisites
if (-not (Test-Path $VanillaServer)) {
    Write-Error "Vanilla server not found at $VanillaServer"
}
if (-not (Test-Path $PatchedLibDll)) {
    Write-Error "Patched VintagestoryLib.dll not found (run 'make patch-il' first)"
}
if (-not (Test-Path $MagicNumbersTemplate)) {
    Write-Error "servermagicnumbers.json baseline not found; run the parity harness once"
}

# Clean slate
if (Test-Path $BenchDir) { Remove-Item -Recurse -Force $BenchDir }
New-Item -ItemType Directory -Path $BenchDir -Force | Out-Null

# Build patched server overlay
$PatchedServer = Join-Path $BenchDir 'patched-server'
Write-Host "Building patched server overlay..."
Copy-Item -Recurse -Path $VanillaServer -Destination $PatchedServer

# Overlay exact Cecil-patched official assemblies and Optimum-owned contracts.
Copy-Item -Force (Join-Path $PatchedDlls 'VintagestoryAPI-patched.dll') (Join-Path $PatchedServer 'VintagestoryAPI.dll')
Copy-Item -Force (Join-Path $PatchedDlls 'Optimum.Api.Contracts.dll') $PatchedServer
Copy-Item -Force (Join-Path $PatchedDlls 'Optimum.GameContent.dll') $PatchedServer
Copy-Item -Force (Join-Path $PatchedDlls 'VSEssentials-patched.dll') (Join-Path $PatchedServer 'Mods/VSEssentials.dll')
Copy-Item -Force (Join-Path $PatchedDlls 'VSSurvivalMod-patched.dll') (Join-Path $PatchedServer 'Mods/VSSurvivalMod.dll')
Copy-Item -Force $PatchedLibDll (Join-Path $PatchedServer 'VintagestoryLib.dll')

function Get-SpawnTime {
    param([string]$LogPath)

    $startLine = Select-String -Path $LogPath -Pattern 'spawn chunks\.\.\.' | Select-Object -First 1
    $endLine = Select-String -Path $LogPath -Pattern 'Entering runphase RunGame' | Select-Object -First 1

    if (-not $startLine -or -not $endLine) { return -1 }

    # Parse "16.7.2026 14:38:30 [Server ..." format
    function Parse-LogTime([string]$line) {
        if ($line -match '(\d+)\.(\d+)\.(\d+)\s+(\d+:\d+:\d+)') {
            $day = $Matches[1]; $month = $Matches[2]; $year = $Matches[3]; $time = $Matches[4]
            return [DateTime]::ParseExact("$year-$month-$day $time", 'yyyy-M-d H:mm:ss', $null)
        }
        return $null
    }

    $t0 = Parse-LogTime $startLine.Line
    $t1 = Parse-LogTime $endLine.Line
    if ($null -eq $t0 -or $null -eq $t1) { return -1 }

    return [int]($t1 - $t0).TotalSeconds
}

function Invoke-BenchRun {
    param(
        [string]$Label,
        [string]$MtValue,
        [string]$WorkersValue,
        [int]$RunNumber
    )

    $dataPath = Join-Path $BenchDir "data-$Label-run$RunNumber"
    $logPath = Join-Path $BenchDir "log-$Label-run$RunNumber.txt"

    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null

    # Generate config
    Push-Location $PatchedServer
    & dotnet VintagestoryServer.dll --dataPath $dataPath --genconfig 2>&1 | Out-Null
    & dotnet VintagestoryServer.dll --dataPath $dataPath `
        --setconfig='{ "Port": 0, "MaxClients": 0, "PassTimeWhenEmpty": false }' 2>&1 | Out-Null
    Pop-Location

    # Patch SpawnChunksWidth in magicnumbers
    $magic = Get-Content $MagicNumbersTemplate -Raw
    $magic = $magic -replace '"SpawnChunksWidth":\s*\d+', "`"SpawnChunksWidth`": $SpawnChunksWidth"
    Set-Content -Path (Join-Path $dataPath 'servermagicnumbers.json') -Value $magic

    # Set env vars and launch server
    $env:OPTIMUM_WORLDGEN_MT = $MtValue
    $env:OPTIMUM_WORLDGEN_WORKERS = $WorkersValue

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'dotnet'
    $psi.Arguments = "VintagestoryServer.dll --dataPath `"$dataPath`" --withconfig=`"{ WorldConfig: { Seed: '$Seed', WorldName: 'bench' } }`""
    $psi.WorkingDirectory = $PatchedServer
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['OPTIMUM_WORLDGEN_MT'] = $MtValue
    $psi.EnvironmentVariables['OPTIMUM_WORLDGEN_WORKERS'] = $WorkersValue

    $proc = [System.Diagnostics.Process]::Start($psi)

    # Wait for RunGame marker or timeout
    $deadline = [DateTime]::Now.AddSeconds($TimeoutSeconds)
    $output = [System.Text.StringBuilder]::new()
    $reader = $proc.StandardOutput
    $found = $false

    while (-not $proc.HasExited -and [DateTime]::Now -lt $deadline) {
        if (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            [void]$output.AppendLine($line)
            if ($line -match 'Entering runphase RunGame') {
                $found = $true
                break
            }
        } else {
            Start-Sleep -Milliseconds 200
        }
    }

    # Drain remaining output briefly
    Start-Sleep -Milliseconds 500

    # Kill the server
    if (-not $proc.HasExited) {
        try { $proc.Kill($true) } catch {}
    }
    try { $proc.WaitForExit(10000) } catch {}

    # Write log
    Set-Content -Path $logPath -Value $output.ToString()

    # Clean env
    Remove-Item Env:\OPTIMUM_WORLDGEN_MT -ErrorAction SilentlyContinue
    Remove-Item Env:\OPTIMUM_WORLDGEN_WORKERS -ErrorAction SilentlyContinue

    if (-not $found) { return -1 }
    return Get-SpawnTime $logPath
}

function Get-Median {
    param([int[]]$Values)
    $sorted = $Values | Sort-Object
    $n = $sorted.Count
    if ($n % 2 -eq 1) { return $sorted[($n - 1) / 2] }
    return [int](($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2)
}

# Run the benchmark
$results = @{}

Write-Host ""
Write-Host "=== WORLDGEN BENCHMARK (Windows native, seed $Seed, ${SpawnChunksWidth}x${SpawnChunksWidth} spawn, $Runs runs/mode) ==="
Write-Host "ProcessorCount: $([Environment]::ProcessorCount)"
Write-Host ""

# Serial baseline
Write-Host "--- serial (MT=0) ---"
$serialTimes = @()
for ($r = 1; $r -le $Runs; $r++) {
    Write-Host -NoNewline "  [serial] run $r/$Runs... "
    $t = Invoke-BenchRun -Label 'serial' -MtValue '0' -WorkersValue '' -RunNumber $r
    if ($t -lt 0) {
        Write-Error "Run failed to reach RunGame; check $BenchDir\log-serial-run$r.txt"
    }
    Write-Host "${t}s"
    $serialTimes += $t
    Remove-Item -Recurse -Force (Join-Path $BenchDir "data-serial-run$r") -ErrorAction SilentlyContinue
}
$results['serial'] = @{ Times = $serialTimes; Median = (Get-Median $serialTimes) }

# Worker counts 1 through MaxWorkers
for ($w = 1; $w -le $MaxWorkers; $w++) {
    $label = "$w-worker"
    Write-Host "--- $label (MT=1, WORKERS=$w) ---"
    $times = @()
    for ($r = 1; $r -le $Runs; $r++) {
        Write-Host -NoNewline "  [$label] run $r/$Runs... "
        $t = Invoke-BenchRun -Label $label -MtValue '1' -WorkersValue "$w" -RunNumber $r
        if ($t -lt 0) {
            Write-Error "Run failed to reach RunGame; check $BenchDir\log-$label-run$r.txt"
        }
        Write-Host "${t}s"
        $times += $t
        Remove-Item -Recurse -Force (Join-Path $BenchDir "data-$label-run$r") -ErrorAction SilentlyContinue
    }
    $results[$label] = @{ Times = $times; Median = (Get-Median $times) }
}

# Report
Write-Host ""
Write-Host "=== RESULTS ==="
Write-Host ("{0,-12} {1,-20} {2,-10} {3}" -f 'Mode', 'Runs (s)', 'Median', 'Speedup')

$serialMedian = $results['serial'].Median
$line = "{0,-12} {1,-20} {2,-10} {3}" -f 'serial', ($results['serial'].Times -join ', '), "${serialMedian}s", '1.00x'
Write-Host $line

for ($w = 1; $w -le $MaxWorkers; $w++) {
    $label = "$w-worker"
    $med = $results[$label].Median
    $speedup = if ($med -gt 0) { [Math]::Round($serialMedian / $med, 2) } else { 0 }
    $line = "{0,-12} {1,-20} {2,-10} {3}" -f $label, ($results[$label].Times -join ', '), "${med}s", "${speedup}x"
    Write-Host $line
}

Write-Host ""
Write-Host "ProcessorCount: $([Environment]::ProcessorCount), MaxWorkers tested: $MaxWorkers"
