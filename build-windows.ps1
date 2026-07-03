[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'scripts/build-windows.ps1') @RemainingArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
