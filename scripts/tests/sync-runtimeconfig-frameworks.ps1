$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..\sync-runtimeconfig-frameworks.ps1'
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('optimum-rtcfg-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    $launcherPath = Join-Path $tempDir 'Optimum.runtimeconfig.json'
    $vanillaPath = Join-Path $tempDir 'Vintagestory.runtimeconfig.json'

    @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
    "configProperties": { "keep": true }
  }
}
'@ | Set-Content -LiteralPath $launcherPath -Encoding UTF8

    @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
    ]
  }
}
'@ | Set-Content -LiteralPath $vanillaPath -Encoding UTF8

    & $scriptPath -LauncherConfig $launcherPath -VanillaConfig $vanillaPath
    $merged = Get-Content -Raw -LiteralPath $launcherPath | ConvertFrom-Json
    $names = @($merged.runtimeOptions.frameworks | ForEach-Object { $_.name })
    if ($names -notcontains 'Microsoft.WindowsDesktop.App') {
        throw 'The vanilla Windows Desktop framework was not copied into the launcher runtimeconfig.'
    }
    if ($merged.runtimeOptions.configProperties.keep -ne $true) {
        throw 'The launcher runtimeconfig properties were not preserved.'
    }
    if ($merged.runtimeOptions.configProperties.'System.GC.Server' -ne $true -or $merged.runtimeOptions.configProperties.'System.GC.Concurrent' -ne $true -or $merged.runtimeOptions.configProperties.'System.GC.DATAs' -ne 1) {
        throw 'The launcher runtimeconfig Server/Concurrent/DATAs GC properties were not configured.'
    }
    if ($merged.runtimeOptions.configProperties.'System.Runtime.TieredPGO' -ne $true) {
        throw 'The launcher runtimeconfig TieredPGO property was not configured.'
    }

    $vanillaMerged = Get-Content -Raw -LiteralPath $vanillaPath | ConvertFrom-Json
    if ($vanillaMerged.runtimeOptions.configProperties.'System.GC.Server' -ne $true -or $vanillaMerged.runtimeOptions.configProperties.'System.GC.Concurrent' -ne $true -or $vanillaMerged.runtimeOptions.configProperties.'System.GC.DATAs' -ne 1) {
        throw 'The vanilla runtimeconfig Server/Concurrent/DATAs GC properties were not configured.'
    }

    $before = Get-Content -Raw -LiteralPath $launcherPath
    $beforeVanilla = Get-Content -Raw -LiteralPath $vanillaPath
    & $scriptPath -LauncherConfig $launcherPath -VanillaConfig $vanillaPath
    $after = Get-Content -Raw -LiteralPath $launcherPath
    $afterVanilla = Get-Content -Raw -LiteralPath $vanillaPath
    if ($before -cne $after -or $beforeVanilla -cne $afterVanilla) {
        throw 'Running the runtimeconfig sync twice changed the output.'
    }

    Write-Output 'Windows PowerShell 5.1 runtimeconfig merge and idempotence passed.'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
