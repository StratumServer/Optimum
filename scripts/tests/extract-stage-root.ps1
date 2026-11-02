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
    Assert-True ($bootstrap -match 'Invoke-ExtractWithStageFallback') 'bootstrap.ps1 does not extract through Invoke-ExtractWithStageFallback'
    Assert-True ($bootstrap -match 'Assert-ExtractPreflight') 'bootstrap.ps1 does not run the preflight before the download'
    Assert-True ($bootstrap -match 'Get-ExtractFailureHint') 'bootstrap.ps1 does not report the extraction directory on failure'

    # 19. Controlled Folder Access: a staging directory inside a protected folder is skipped when the feature blocks, and kept when it does not.
    $protectedDir = Join-Path $tempBase 'Protected Docs'
    New-Item -ItemType Directory -Path $protectedDir | Out-Null
    $cfaOn = [pscustomobject]@{ Enabled = 1; ProtectedFolders = @($protectedDir) }
    $cfaAudit = [pscustomobject]@{ Enabled = 2; ProtectedFolders = @($protectedDir) }
    $cfaOff = [pscustomobject]@{ Enabled = 0; ProtectedFolders = @($protectedDir) }
    $cfaUnknown = [pscustomobject]@{ Enabled = $null; ProtectedFolders = @($protectedDir) }
    $inside = Join-Path $protectedDir 'sub'
    New-Item -ItemType Directory -Path $inside | Out-Null
    Assert-True ((Get-ProtectedFolderFor -Path $inside -Info $cfaOn) -eq $protectedDir) 'a path inside a protected folder was not recognised'
    Assert-True ((Get-ProtectedFolderFor -Path ($protectedDir + '\') -Info $cfaOn) -eq $protectedDir) 'the protected folder itself, with a trailing separator, was not recognised'
    Assert-True ($null -eq (Get-ProtectedFolderFor -Path ($protectedDir + ' other') -Info $cfaOn)) 'a sibling that shares a name prefix was treated as protected'
    Assert-True ($null -eq (Get-ProtectedFolderFor -Path $short -Info $cfaOn)) 'an unrelated path was treated as protected'
    $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($inside, $short) -ControlledFolderAccess $cfaOn
    $created.Add($root)
    Assert-True ($root.StartsWith($short)) 'a protected folder was used while Controlled Folder Access blocks'
    $threw = $false
    try { New-ExtractStageRoot -Fallback $inside -Candidates @($inside) -ControlledFolderAccess $cfaOn | Out-Null } catch {
        $threw = $true
        Assert-True ($_.Exception.Message -like '*Controlled Folder Access protects*') 'the error does not explain the protected folder'
    }
    Assert-True $threw 'a fallback inside a protected folder was used while Controlled Folder Access blocks'
    foreach ($info in @($cfaAudit, $cfaOff, $cfaUnknown)) {
        $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($inside, $short) -ControlledFolderAccess $info
        $created.Add($root)
        Assert-True ($root.StartsWith($inside)) "a protected folder was skipped with Controlled Folder Access at $($info.Enabled)"
    }
    # The real reader returns the documented shape and never throws.
    $real = Get-ControlledFolderAccessInfo
    Assert-True ($null -eq $real.Enabled -or $real.Enabled -in 0, 1, 2) "Controlled Folder Access state $($real.Enabled) is not 0, 1, 2 or unknown"
    Assert-True ($real.ProtectedFolders -is [array] -or $null -eq $real.ProtectedFolders -or $real.ProtectedFolders.Count -eq 0) 'protected folders are not a list'
    if ($env:OS -eq 'Windows_NT') {
        $docs = [Environment]::GetFolderPath('MyDocuments')
        if ($docs) { Assert-True (@($real.ProtectedFolders | Where-Object { $_ -ieq $docs }).Count -eq 1) 'the Documents folder is missing from the protected defaults' }
    }
    # The hint names the app to allow and the Windows Security page.
    $cfaHint = Get-ExtractFailureHint -StageRoot $inside -ExtractorPath 'C:\tools\innoextract.exe' -ControlledFolderAccess $cfaOn
    Assert-True ($cfaHint -like '*Allow an app through Controlled folder access*') 'the hint does not point to the Windows Security setting'
    Assert-True ($cfaHint -like '*C:\tools\innoextract.exe*') 'the hint does not name the extractor to allow'
    Assert-True ($cfaHint -like '*which Controlled Folder Access protects*') 'the hint does not say the directory is protected'
    Assert-True ((Get-ExtractFailureHint -StageRoot $short -ControlledFolderAccess $cfaOff) -notlike '*Controlled*') 'the hint mentions Controlled Folder Access while it is off'

    # 20. An extraction that fails in one location is retried in the next one.
    $locA = Join-Path $tempBase 'locA'; $locB = Join-Path $tempBase 'locB'; $locC = Join-Path $tempBase 'locC'
    New-Item -ItemType Directory -Path $locA, $locB, $locC | Out-Null
    $log = New-Object System.Collections.Generic.List[string]
    $failOnA = { param($stage) $log.Add($stage); if ($stage.StartsWith($locA)) { return 2 }; return 0 }
    $got = Invoke-ExtractWithStageFallback -Fallback $fallback -Candidates @($locA, $locB) -Extract $failOnA -ControlledFolderAccess $cfaOff 3>$null
    $created.Add($got)
    Assert-True ($got.StartsWith($locB)) "the retry did not move to the next location: $got"
    Assert-True ($log.Count -eq 2) "expected two extraction attempts, saw $($log.Count)"
    Assert-True (-not (Test-Path -LiteralPath $log[0])) 'the failed attempt left its staging directory behind'
    Assert-True (Test-Path -LiteralPath $got) 'the successful staging directory is missing'
    # A thrown error counts as a failure too, and extractor output before the exit code does not break the code.
    $log.Clear()
    $noisy = { param($stage) $log.Add($stage); 'stray output'; if ($stage.StartsWith($locA)) { throw 'access denied' }; 0 }
    $got = Invoke-ExtractWithStageFallback -Fallback $fallback -Candidates @($locA, $locB) -Extract $noisy -ControlledFolderAccess $cfaOff 3>$null
    $created.Add($got)
    Assert-True ($got.StartsWith($locB) -and $log.Count -eq 2) 'a thrown extraction error was not retried, or stray output hid the exit code'
    # First attempt succeeds: no retry.
    $log.Clear()
    $got = Invoke-ExtractWithStageFallback -Fallback $fallback -Candidates @($locA, $locB) -Extract { param($stage) $log.Add($stage); 0 } -ControlledFolderAccess $cfaOff
    $created.Add($got)
    Assert-True ($got.StartsWith($locA) -and $log.Count -eq 1) 'a successful first attempt was retried'
    # Every location fails: bounded attempts, nothing left behind, and the error names each location and the Controlled Folder Access fix.
    $log.Clear()
    $threw = $false
    try {
        Invoke-ExtractWithStageFallback -Fallback $fallback -Candidates @($locA, $locB, $locC) -Extract { param($stage) $log.Add($stage); 2 } -ExtractorPath 'C:\tools\innoextract.exe' -ControlledFolderAccess $cfaOn 3>$null | Out-Null
    } catch {
        $threw = $true
        foreach ($loc in @($locA, $locB, $locC)) { Assert-True ($_.Exception.Message.Contains($loc)) "the error does not name $loc" }
        Assert-True ($_.Exception.Message -like '*exit 2*') 'the error does not report the exit code'
        Assert-True ($_.Exception.Message -like '*Allow an app through Controlled folder access*') 'the error does not carry the Controlled Folder Access fix'
    }
    Assert-True $threw 'extraction that failed everywhere was reported as a success'
    Assert-True ($log.Count -eq 3) "expected three attempts, saw $($log.Count)"
    foreach ($attempt in $log) { Assert-True (-not (Test-Path -LiteralPath $attempt)) "failed attempt $attempt was not cleaned up" }
    # The attempt limit holds.
    $log.Clear()
    try { Invoke-ExtractWithStageFallback -Fallback $fallback -Candidates @($locA, $locB, $locC) -Extract { param($stage) $log.Add($stage); 2 } -MaxAttempts 2 -ControlledFolderAccess $cfaOff 3>$null | Out-Null } catch { }
    Assert-True ($log.Count -eq 2) "MaxAttempts 2 allowed $($log.Count) attempts"
    # No usable location at the first attempt keeps the original explanation.
    $threw = $false
    try { Invoke-ExtractWithStageFallback -Fallback $tooLong -Candidates @($tooLong) -Extract { param($stage) 0 } -ControlledFolderAccess $cfaOff | Out-Null } catch {
        $threw = $true
        Assert-True ($_.Exception.Message -like '*-VanillaDir*') 'the first-attempt error lost its -VanillaDir advice'
    }
    Assert-True $threw 'no usable location was reported as a success'
    # Retries also skip a location whose staging directory is protected.
    $log.Clear()
    $got = Invoke-ExtractWithStageFallback -Fallback $fallback -Candidates @($locA, $inside, $locB) -Extract { param($stage) $log.Add($stage); if ($stage.StartsWith($locA)) { 2 } else { 0 } } -ControlledFolderAccess $cfaOn 3>$null
    $created.Add($got)
    Assert-True ($got.StartsWith($locB) -and $log.Count -eq 2) 'a retry used a protected folder'

    # 21. Candidates that differ only by a trailing separator are one candidate.
    $dups = @(Get-ExtractStageCandidates -Destination $fallback)
    $keys = @($dups | ForEach-Object { $_.TrimEnd('\', '/').ToLowerInvariant() })
    Assert-True (($keys | Select-Object -Unique).Count -eq $keys.Count) 'candidates that differ only by a trailing separator were both listed'

    # 22. Windows only: the 8.3 short form of a long path is shorter, is the same folder, and is accepted by the staging search.
    if ($env:OS -eq 'Windows_NT') {
        $longNamed = Join-Path $tempBase ('long-directory-name-' * 4)
        New-Item -ItemType Directory -Path $longNamed | Out-Null
        $shortForm = $null
        try { $shortForm = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($longNamed).ShortPath } catch { }
        if ($shortForm -and $shortForm.Length -lt $longNamed.Length) {
            Assert-True (Test-Path -LiteralPath $shortForm) 'the 8.3 form does not resolve'
            $root = New-ExtractStageRoot -Fallback $fallback -Candidates @($shortForm)
            $created.Add($root)
            Assert-True ($root.StartsWith($shortForm)) 'the 8.3 form was not usable as a staging parent'
            Assert-True ((Test-Path -LiteralPath (Join-Path $longNamed (Split-Path -Leaf $root)))) 'the 8.3 staging directory is not inside the long-named folder'
        } else {
            Write-Host 'extract-stage-root: 8.3 names are not available on this volume; the 8.3 check was skipped'
        }
    }
}
finally {
    # The default search in check 11 creates its directory outside $tempBase.
    foreach ($dir in $created) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tempBase -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
Write-Host 'extract-stage-root: all checks passed'
