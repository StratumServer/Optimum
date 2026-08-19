<#
.SYNOPSIS
    Worldgen throughput benchmark on Windows (native).
    Measures spawn-chunk generation time (log marker "Loading ... spawn chunks..."
    to "Entering runphase RunGame") across worker counts 0 (serial) through MaxWorkers.

.PARAMETER Runs
    Runs per mode. Default 3.

.PARAMETER MaxWorkers
    Highest exact worker count to test. Accepts 1 through 3. Default 3.

.PARAMETER SpawnChunksWidth
    Override for spawn chunk columns. Default 15 (matches the bash benchmark).

.PARAMETER TimeoutSeconds
    Max wait per run before declaring failure. Default 420.

.PARAMETER OutputCsv
    Absolute or project-relative CSV path for every accepted run. Defaults to
    .worldgen-benchmark-win/results.csv.

.EXAMPLE
    .\scripts\worldgen-benchmark.ps1
    .\scripts\worldgen-benchmark.ps1 -Runs 5 -MaxWorkers 3
#>
param(
    [int]$Runs = 3,
    [ValidateRange(1, 3)]
    [int]$MaxWorkers = 3,
    [int]$SpawnChunksWidth = 15,
    [int]$TimeoutSeconds = 420,
    [string]$OutputCsv = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$VanillaServer = Join-Path $ProjectRoot '.vanilla\win-x64\vintagestory'
$PatchedDlls = Join-Path $ProjectRoot 'bin\Release\net10.0'
$PatchedLibDll = Join-Path $ProjectRoot 'build\VintagestoryLib\bin\Release\net10.0\VintagestoryLib-patched.dll'
$RunValidator = Join-Path $ProjectRoot 'scripts\validate-worldgen-run.ps1'
$BenchDir = Join-Path $ProjectRoot '.worldgen-benchmark-win'
$MagicNumbersTemplate = Join-Path $ProjectRoot '.worldgen-parity-baseline\servermagicnumbers.json'
$Seed = '42424242'

if ([string]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = Join-Path $BenchDir 'results.csv'
} elseif (-not [IO.Path]::IsPathRooted($OutputCsv)) {
    $OutputCsv = Join-Path $ProjectRoot $OutputCsv
}
$OutputCsv = [IO.Path]::GetFullPath($OutputCsv)

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

$orphan = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object {
    $_.CommandLine -like '*VintagestoryServer.dll*' -and $_.CommandLine -like "*$BenchDir*"
} | Select-Object -First 1
if ($orphan) {
    Write-Error "A VintagestoryServer process uses $BenchDir. Stop process $($orphan.ProcessId) before starting this benchmark."
}

# Clean slate
if (Test-Path $BenchDir) { Remove-Item -Recurse -Force $BenchDir }
New-Item -ItemType Directory -Path $BenchDir -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputCsv) -Force | Out-Null
Set-Content -LiteralPath $OutputCsv -Value 'mode,run,workers,seconds'

# Build patched server overlay
$PatchedServer = Join-Path $BenchDir 'patched-server'
Write-Host "Building patched server overlay..."
Copy-Item -Recurse -Path $VanillaServer -Destination $PatchedServer

# Overlay exact Cecil-patched official assemblies and Optimum-owned contracts.
Copy-Item -Force (Join-Path $PatchedDlls 'VintagestoryAPI.dll') (Join-Path $PatchedServer 'VintagestoryAPI.dll')
Copy-Item -Force (Join-Path $PatchedDlls 'Optimum.Api.Contracts.dll') $PatchedServer
Copy-Item -Force (Join-Path $PatchedDlls 'Optimum.GameContent.dll') $PatchedServer
Copy-Item -Force (Join-Path $PatchedDlls 'VSEssentials.dll') (Join-Path $PatchedServer 'Mods/VSEssentials.dll')
Copy-Item -Force (Join-Path $PatchedDlls 'VSSurvivalMod.dll') (Join-Path $PatchedServer 'Mods/VSSurvivalMod.dll')
Copy-Item -Force $PatchedLibDll (Join-Path $PatchedServer 'VintagestoryLib.dll')

$script:ActiveBenchmarkProcess = $null
function Stop-ActiveBenchmarkProcess {
    if ($null -ne $script:ActiveBenchmarkProcess -and -not $script:ActiveBenchmarkProcess.HasExited) {
        try { $script:ActiveBenchmarkProcess.Kill($true) } catch {}
        try { $script:ActiveBenchmarkProcess.WaitForExit(10000) } catch {}
    }
    $script:ActiveBenchmarkProcess = $null
}
trap {
    Stop-ActiveBenchmarkProcess
    break
}

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

    $proc = $null
    $output = [System.Text.StringBuilder]::new()
    $found = $false
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        $script:ActiveBenchmarkProcess = $proc

        # Wait for the RunGame marker or timeout.
        $deadline = [DateTime]::Now.AddSeconds($TimeoutSeconds)
        $reader = $proc.StandardOutput

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

        Start-Sleep -Milliseconds 500
    } finally {
        Stop-ActiveBenchmarkProcess
        Remove-Item Env:\OPTIMUM_WORLDGEN_MT -ErrorAction SilentlyContinue
        Remove-Item Env:\OPTIMUM_WORLDGEN_WORKERS -ErrorAction SilentlyContinue
    }

    # Write log
    try {
        $stderr = $proc.StandardError.ReadToEnd()
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            [void]$output.AppendLine($stderr)
        }
    } catch {}
    Set-Content -Path $logPath -Value $output.ToString()

    if (-not $found) { return -1 }
    & $RunValidator -LogPath $logPath -ExpectedWorkers ([int]$WorkersValue)
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
    Add-Content -LiteralPath $OutputCsv -Value ("serial,{0},0,{1}" -f $r, $t)
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
        Add-Content -LiteralPath $OutputCsv -Value ("{0},{1},{2},{3}" -f $label, $r, $w, $t)
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
Write-Host "CSV: $OutputCsv"
