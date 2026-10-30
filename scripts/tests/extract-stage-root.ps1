$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\extract-stage-root.ps1')

$failures = New-Object System.Collections.Generic.List[string]
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

$tempBase = Join-Path ([IO.Path]::GetTempPath()) ('ost-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tempBase | Out-Null
$created = New-Object System.Collections.Generic.List[string]

try {
    # The deepest installer entry that failed in the field report.
    $deepest = '\app\assets\survival\worldgen\dungeontiles\refinerynew\simple\blastfurnace\big66\trampledcobble\paths\entrance\epstd-lr-ene1.json'

    # 1. The first usable candidate wins and leaves room for the deepest entry.
    $fallback = Join-Path $tempBase 'fallback'
    New-Item -ItemType Directory -Path $fallback | Out-Null
    $short = Join-Path $tempBase 'short'
    New-Item -ItemType Directory -Path $short | Out-Null
    $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($short)
    $created.Add($root)
    Assert-True (Test-Path -LiteralPath $root -PathType Container) 'stage root was not created'
    Assert-True ($root.StartsWith($short)) "stage root $root is not under the first candidate"
    Assert-True (($root.Length + $deepest.Length) -le 240) "stage root plus the deepest entry is $($root.Length + $deepest.Length) characters"

    # 2. A candidate that is too long for the deepest entry is skipped.
    $tooLong = Join-Path $tempBase ('a' * 200)
    $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($tooLong, $short)
    $created.Add($root)
    Assert-True ($root.StartsWith($short)) "a candidate of $($tooLong.Length) characters was not skipped"
    Assert-True (-not (Test-Path -LiteralPath $tooLong)) 'the over-long candidate was created'

    # 3. The fallback is used when every candidate is too long, even if it is long itself.
    $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($tooLong)
    $created.Add($root)
    Assert-True ($root.StartsWith($fallback)) 'the fallback was not used when all candidates were too long'

    # 4. Every call returns a fresh directory.
    $first = New-ExtractStageRoot -Fallback $fallback -Candidates @($short)
    $second = New-ExtractStageRoot -Fallback $fallback -Candidates @($short)
    $created.Add($first); $created.Add($second)
    Assert-True ($first -ne $second) 'two calls returned the same directory'

    # 5. Failure hints name the limit only when the path can hit it.
    $shortHint = Get-ExtractFailureHint -StageRoot $short
    Assert-True ($shortHint -like "*$($short.Length) characters*") 'hint does not report the path length'
    Assert-True ($shortHint -notlike '*260*') 'hint blames the path limit for a short path'
    $longHint = Get-ExtractFailureHint -StageRoot $tooLong
    Assert-True ($longHint -like '*260 characters*') 'hint does not name the path limit for a long path'
    Assert-True ($longHint -like '*-VanillaDir*') 'hint does not offer -VanillaDir'

    # 6. bootstrap.ps1 uses the helper for the Windows extraction.
    $bootstrap = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\bootstrap.ps1') -Raw
    Assert-True ($bootstrap -match 'extract-stage-root\.ps1') 'bootstrap.ps1 does not load extract-stage-root.ps1'
    Assert-True ($bootstrap -match 'New-ExtractStageRoot') 'bootstrap.ps1 does not call New-ExtractStageRoot'
    Assert-True ($bootstrap -match 'Get-ExtractFailureHint') 'bootstrap.ps1 does not report the extraction directory on failure'
}
finally {
    Remove-Item -LiteralPath $tempBase -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
Write-Host 'extract-stage-root: all checks passed'
