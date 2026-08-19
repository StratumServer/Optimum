param(
    [Parameter(Mandatory)]
    [string]$LogPath,
    [Parameter(Mandatory)]
    [ValidateRange(0, 3)]
    [int]$ExpectedWorkers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
    throw "Worldgen run log does not exist: $LogPath"
}

$logText = Get-Content -LiteralPath $LogPath -Raw
$schedulerMatches = [regex]::Matches(
    $logText,
    'Optimum worldgen scheduler started with (\d+) worker threads\.'
)
$adaptiveMatches = [regex]::Matches($logText, 'Optimum adaptive: workers')
$disabledMatches = [regex]::Matches($logText, 'Optimum worldgen parallelism disabled')

if ($adaptiveMatches.Count -ne 0) {
    throw "Run changed the worldgen worker cap under an exact treatment: $LogPath"
}
if ($ExpectedWorkers -eq 0) {
    if ($schedulerMatches.Count -ne 0) {
        throw "Serial run started a worldgen scheduler: $LogPath"
    }
    exit 0
}
if ($disabledMatches.Count -ne 0) {
    throw "Server safety checks disabled the requested worldgen workers: $LogPath"
}
if ($schedulerMatches.Count -ne 1) {
    throw "Parallel run logged $($schedulerMatches.Count) scheduler starts, expected one: $LogPath"
}

$realizedWorkers = [int]$schedulerMatches[0].Groups[1].Value
if ($realizedWorkers -ne $ExpectedWorkers) {
    throw "Run requested $ExpectedWorkers workers but started ${realizedWorkers}: $LogPath"
}
