$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\extract-stage-root.ps1')

$failures = New-Object System.Collections.Generic.List[string]
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

$tempBase = Join-Path ([IO.Path]::GetTempPath()) ('ost-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tempBase | Out-Null
$created = New-Object System.Collections.Generic.List[string]

# The real budget, captured before the test widens it for its own directories.
$realMaxPath = $script:ExtractStageMaxPath
$realMargin = $script:ExtractStageDeepestEntry
# A Windows temp folder (C:\Users\<name>\AppData\Local\Temp) is longer than a
# Linux one, so the directories this test creates under it would fall outside
# the budget it is not testing. Widen the budget for them; the tests that
# exercise the budget use paths far beyond it.
$script:ExtractStageMaxPath = [Math]::Max($script:ExtractStageMaxPath, $tempBase.Length + 120 + $script:ExtractStageDeepestEntry)

try {
    # The deepest installer entry that failed in the field report.
    $deepest = '\app\assets\survival\worldgen\dungeontiles\refinerynew\simple\blastfurnace\big66\trampledcobble\paths\entrance\epstd-lr-ene1.json'

    # 0. The real budget: a default temp folder fits for user names up to 24 characters, and a drive root fits whatever the user name is.
    $stageName = 'optimum-ix-' + ('0' * 16)
    $tempFor24 = 'C:\Users\' + ('u' * 24) + '\AppData\Local\Temp\' + $stageName
    $tempFor40 = 'C:\Users\' + ('u' * 40) + '\AppData\Local\Temp\' + $stageName
    Assert-True (($tempFor24.Length + $realMargin) -le $realMaxPath) "temp for a 24 character user name needs $($tempFor24.Length + $realMargin) of $realMaxPath"
    Assert-True (($tempFor40.Length + $realMargin) -gt $realMaxPath) 'a 40 character user name unexpectedly fits the temp folder; the test no longer covers the drive root path'
    Assert-True ((('C:\' + $stageName).Length + $realMargin) -le $realMaxPath) 'a drive root does not fit the budget'

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

    # 6. The probe creates and removes the deepest entry.
    Assert-True ($null -eq (Test-ExtractStageWritable -Root $short)) 'the probe failed in a writable directory'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $short 'app'))) 'the probe left files behind'
    $blocked = Join-Path $tempBase 'blocked'
    Set-Content -LiteralPath $blocked -Value 'not a directory'
    Assert-True ($null -ne (Test-ExtractStageWritable -Root $blocked)) 'the probe passed under a path that is a file'

    # 7. A candidate that cannot be written is skipped. A plain file blocks directory creation.
    $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($blocked, $short)
    $created.Add($root)
    Assert-True ($root.StartsWith($short)) 'a candidate that is a file was not skipped'

    # 8. Too little free space rejects every candidate and the error lists why.
    $threw = $false
    try {
        New-ExtractStageRoot -Fallback $fallback -Candidates @($short) -RequiredFreeBytes ([long]::MaxValue) | Out-Null
    } catch {
        $threw = $true
        Assert-True ($_.Exception.Message -like '*MB free*') "the error does not report free space: $($_.Exception.Message)"
        Assert-True ($_.Exception.Message.Contains($short)) 'the error does not name the rejected candidate'
    }
    Assert-True $threw 'a volume without room was accepted'
    Assert-True (@(Get-ChildItem -LiteralPath $short -Directory -Filter 'optimum-ix-*' | Where-Object { $created -notcontains $_.FullName }).Count -eq 0) 'a rejected candidate left its directory behind'

    # 9. The preflight removes its staging directory and checks the destination.
    $before = @(Get-ChildItem -LiteralPath $short -Directory).Count
    Assert-ExtractPreflight -Fallback $fallback -Destination (Join-Path $tempBase 'client') -Candidates @($short)
    Assert-True (@(Get-ChildItem -LiteralPath $short -Directory).Count -eq $before) 'the preflight left its staging directory behind'
    $threw = $false
    try {
        Assert-ExtractPreflight -Fallback $fallback -Destination (Join-Path $tempBase 'client') -Candidates @($short) -RequiredFreeBytes ([long]::MaxValue)
    } catch { $threw = $true }
    Assert-True $threw 'the preflight passed without enough free space'

    # 10. The hint reports the long path switch.
    Assert-True ((Get-ExtractFailureHint -StageRoot $short) -match 'LongPathsEnabled: (enabled|disabled|unknown)') 'hint does not report LongPathsEnabled'

    # 11. The default candidate list starts with the temp folder, has no empty or duplicate entries, and every entry is tried until one qualifies.
    $defaults = @(Get-ExtractStageCandidates -Destination $fallback)
    Assert-True ($defaults.Count -ge 1) 'no default candidates'
    Assert-True ($defaults[0] -eq [IO.Path]::GetTempPath()) 'the temp folder is not the first candidate'
    Assert-True (@($defaults | Where-Object { -not $_ }).Count -eq 0) 'a default candidate is empty'
    Assert-True ((@($defaults | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)).Count -eq $defaults.Count) 'default candidates contain duplicates'
    $root = New-ExtractStageRoot -Fallback $fallback
    $created.Add($root)
    Assert-True (Test-Path -LiteralPath $root -PathType Container) 'the default search found no usable directory'
    # Several bad candidates in a row do not stop the search.
    $bad1 = Join-Path $tempBase 'bad1'; Set-Content -LiteralPath $bad1 -Value 'x'
    $bad2 = Join-Path $tempBase 'bad2'; Set-Content -LiteralPath $bad2 -Value 'x'
    $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($bad1, $tooLong, $bad2, $short)
    $created.Add($root)
    Assert-True ($root.StartsWith($short)) 'the search stopped before reaching a usable candidate'

    # 12. The fallback is exempt from the safety margin only, never from the real limit.
    $longFallback = Join-Path $tempBase ('b' * 150)
    $threw = $false
    try { New-ExtractStageRoot -Fallback $longFallback -Candidates @($tooLong) | Out-Null } catch {
        $threw = $true
        Assert-True ($_.Exception.Message -like '*-VanillaDir*') 'the error does not offer -VanillaDir'
        Assert-True ($_.Exception.Message -like '*limit 247*') 'the error does not name the directory limit'
    }
    Assert-True $threw 'a fallback past the directory limit was accepted'
    Assert-True (-not (Test-Path -LiteralPath $longFallback)) 'the over-long fallback was created'
    # A fallback whose root plus the known deepest entry lands at 244 characters:
    # past the 240 margin, inside the 247 directory limit. The root is
    # <fallback>\optimum-ix-<16 hex>, which is 28 characters after the fallback.
    $midLength = 244 - 129 - 28 - ($tempBase.Length + 1) - 1
    $midFallback = Join-Path $tempBase ('c' * $midLength)
    New-Item -ItemType Directory -Path $midFallback | Out-Null
    $root = New-ExtractStageRoot -Fallback $midFallback -Candidates @($tooLong)
    $created.Add($root)
    Assert-True ($root.StartsWith($midFallback)) 'a fallback inside the real limit but past the margin was rejected'

    # 13. A stage directory is created exclusively, and old leftovers are swept.
    $existing = Join-Path $short 'exists'
    New-Item -ItemType Directory -Path $existing | Out-Null
    $threw = $false
    try { New-PrivateExtractDirectory -Path $existing } catch { $threw = $true }
    Assert-True $threw 'an existing directory was reused'
    $stale = Join-Path $short 'optimum-ix-stale000000000'
    $fresh = Join-Path $short 'optimum-ix-fresh000000000'
    New-Item -ItemType Directory -Path $stale, $fresh | Out-Null
    (Get-Item -LiteralPath $stale).LastWriteTime = (Get-Date).AddDays(-3)
    $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($short)
    $created.Add($root)
    Assert-True (-not (Test-Path -LiteralPath $stale)) 'a stale staging directory was not swept'
    Assert-True (Test-Path -LiteralPath $fresh) 'a recent staging directory was swept'

    # 14. Staging and destination on one volume need room for both.
    $free = Get-ExtractFreeBytes -Path $short
    if ($null -ne $free) {
        $required = [long]($free * 0.6)
        Assert-ExtractPreflight -Fallback $fallback -Destination (Join-Path $short 'client') -Candidates @($short) -RequiredFreeBytes ([long]($free * 0.3))
        $threw = $false
        try { Assert-ExtractPreflight -Fallback $fallback -Destination (Join-Path $short 'client') -Candidates @($short) -RequiredFreeBytes $required } catch { $threw = $true }
        Assert-True $threw 'staging and destination on one volume were not added together'
    }

    # 15. bootstrap.ps1 runs the preflight as a command before the download command, not in a comment.
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\bootstrap.ps1'), [ref]$null, [ref]$null)
    $commands = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
    $preflight = @($commands | Where-Object { $_.GetCommandName() -eq 'Assert-ExtractPreflight' })
    $download = @($commands | Where-Object { $_.GetCommandName() -eq 'curl.exe' })
    Assert-True ($preflight.Count -eq 1) "expected one Assert-ExtractPreflight call, found $($preflight.Count)"
    Assert-True ($download.Count -ge 1) 'no curl.exe download command found'
    if ($preflight.Count -eq 1 -and $download.Count -ge 1) {
        Assert-True ($preflight[0].Extent.StartOffset -lt $download[0].Extent.StartOffset) 'the preflight does not run before the download'
    }

    # 16. Free space at an unreadable location is reported as unknown instead of throwing, and a share path does not break the search.
    $probeDrive = Get-ExtractFreeBytes -Path (Join-Path $tempBase 'does\not\exist')
    Assert-True ($null -eq $probeDrive -or $probeDrive -gt 0) 'free space for a missing path is neither unknown nor a size'
    if ($env:OS -eq 'Windows_NT') {
        $unc = Get-ExtractFreeBytes -Path "\\localhost\$($env:SystemDrive.TrimEnd(':'))$\Windows"
        Assert-True ($null -eq $unc -or $unc -gt 0) 'free space for a share path threw or returned a bad value'
        # Every default candidate on Windows exists, including the 8.3 forms.
        foreach ($candidate in (Get-ExtractStageCandidates -Destination $fallback)) {
            Assert-True (Test-Path -LiteralPath $candidate) "default candidate $candidate does not exist"
        }
    }

    # 17. All three scripts parse in the host that runs this test (Windows PowerShell 5.1 and PowerShell 7 parse differently).
    foreach ($file in @('..\bootstrap.ps1', '..\extract-stage-root.ps1', 'extract-stage-root.ps1')) {
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$null, [ref]$parseErrors)
        Assert-True ($parseErrors.Count -eq 0) "$file has $($parseErrors.Count) parse error(s) in PowerShell $($PSVersionTable.PSVersion)"
    }

    # 18. bootstrap.ps1 uses the helper for the Windows extraction.
    $bootstrap = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\bootstrap.ps1') -Raw
    Assert-True ($bootstrap -match 'extract-stage-root\.ps1') 'bootstrap.ps1 does not load extract-stage-root.ps1'
    Assert-True ($bootstrap -match 'New-ExtractStageRoot') 'bootstrap.ps1 does not call New-ExtractStageRoot'
    Assert-True ($bootstrap -match 'Assert-ExtractPreflight') 'bootstrap.ps1 does not run the preflight before the download'
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
