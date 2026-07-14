[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'scripts/install-windows.ps1') @RemainingArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
