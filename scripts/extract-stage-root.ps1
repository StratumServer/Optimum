# innoextract is a Win32 program without a long path manifest and it does not
# use the \\?\ prefix, so Windows' 260 character path limit applies to it even
# when LongPathsEnabled is set. The deepest entry in the Vintage Story
# installer is 129 characters below the extraction root, so the staging
# directory has to be short. Extracting beside the final location
# (<src>\.vanilla\win-x64\.innoextract-stage-<guid>) adds 100+ characters to
# every entry before the archive's own path starts.

# Longest known installer entry below the extraction root, plus a margin for
# future assets. Keep the total under the 248 character directory limit.
$script:ExtractStageDeepestEntry = 160
$script:ExtractStageMaxPath = 240

# Free space the extraction needs on the staging volume and on the volume that
# receives the result. The extracted client is about 1 GB; this leaves room for
# the move and for a retry.
$script:ExtractStageRequiredFreeBytes = 2GB

# Deepest installer entry, as path segments below the extraction root. The
# probe below creates it for real, so a stage directory that cannot hold the
# path (permissions, Controlled Folder Access, a filter driver, the path limit)
# is found before the 570 MB download and the extraction, not after.
$script:ExtractStageProbeEntry = @('app', 'assets', 'survival', 'worldgen', 'dungeontiles', 'refinerynew', 'simple', 'blastfurnace', 'big66', 'trampledcobble', 'paths', 'entrance', 'epstd-lr-ene1.json')

# Free bytes on the volume that holds $Path (or its nearest existing parent),
# or $null when the volume cannot be queried.
function Get-ExtractFreeBytes {
    param([Parameter(Mandatory)][string]$Path)
    try {
        $probe = $Path
        while ($probe -and -not (Test-Path -LiteralPath $probe)) { $probe = Split-Path -Parent $probe }
        if (-not $probe) { return $null }
        $root = [IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $probe).ProviderPath)
        return ([IO.DriveInfo]::new($root)).AvailableFreeSpace
    } catch {
        return $null
    }
}

# Creates the deepest installer entry under $Root and removes it again.
# Returns $null on success, or the reason it failed.
function Test-ExtractStageWritable {
    param([Parameter(Mandatory)][string]$Root)
    $dir = $Root
    foreach ($segment in $script:ExtractStageProbeEntry[0..($script:ExtractStageProbeEntry.Count - 2)]) {
        $dir = Join-Path $dir $segment
    }
    $file = Join-Path $dir $script:ExtractStageProbeEntry[-1]
    $failure = $null
    try {
        New-Item -ItemType Directory -Force -Path $dir -ErrorAction Stop | Out-Null
        [IO.File]::WriteAllBytes($file, [byte[]](1, 2, 3))
        if ((Get-Item -LiteralPath $file).Length -ne 3) { $failure = 'the probe file was truncated' }
    } catch {
        $failure = $_.Exception.Message
    }
    # Remove from the top so a half-created chain does not linger.
    $first = Join-Path $Root $script:ExtractStageProbeEntry[0]
    if (Test-Path -LiteralPath $first) { Remove-Item -LiteralPath $first -Recurse -Force -ErrorAction SilentlyContinue }
    return $failure
}

# Returns a new, empty staging directory that passed three checks: its path
# leaves room for the deepest installer entry, the volume has room for the
# extraction, and the deepest entry can be created and written there. Prefers
# the temp folder, then the drive root, then $Fallback (the previous location
# beside the target; exempt from the length rule only). Throws, listing why each
# candidate was rejected, when none qualifies.
function New-ExtractStageRoot {
    param(
        [Parameter(Mandatory)][string]$Fallback,
        [string[]]$Candidates,
        [long]$RequiredFreeBytes = $script:ExtractStageRequiredFreeBytes
    )
    if (-not $Candidates) {
        $Candidates = @([IO.Path]::GetTempPath())
        if ($env:SystemDrive) { $Candidates += "$($env:SystemDrive)\" }
    }
    $name = "optimum-ix-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $rejected = New-Object System.Collections.Generic.List[string]
    foreach ($parent in $Candidates + @($Fallback)) {
        if (-not $parent) { continue }
        $root = Join-Path $parent $name
        if (($root.Length + $script:ExtractStageDeepestEntry) -gt $script:ExtractStageMaxPath -and $parent -ne $Fallback) {
            $rejected.Add("$parent (path would reach $($root.Length + $script:ExtractStageDeepestEntry) characters)")
            continue
        }
        try {
            New-Item -ItemType Directory -Force -Path $root -ErrorAction Stop | Out-Null
        } catch {
            $rejected.Add("$parent (cannot create a directory: $($_.Exception.Message))")
            continue
        }
        $free = Get-ExtractFreeBytes -Path $root
        if ($null -ne $free -and $free -lt $RequiredFreeBytes) {
            $rejected.Add("$parent (only $([math]::Round($free / 1MB)) MB free, need $([math]::Round($RequiredFreeBytes / 1MB)) MB)")
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            continue
        }
        $failure = Test-ExtractStageWritable -Root $root
        if ($failure) {
            $rejected.Add("$parent (cannot create the deepest installer path: $failure)")
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            continue
        }
        return $root
    }
    throw "No usable extraction directory for the Vintage Story client. Rejected: $($rejected -join '; ')"
}

# Fails early, before the large download, when the extraction cannot work:
# no usable staging directory, or too little room where the result goes.
function Assert-ExtractPreflight {
    param(
        [Parameter(Mandatory)][string]$Fallback,
        [Parameter(Mandatory)][string]$Destination,
        [string[]]$Candidates,
        [long]$RequiredFreeBytes = $script:ExtractStageRequiredFreeBytes
    )
    $stage = New-ExtractStageRoot -Fallback $Fallback -Candidates $Candidates -RequiredFreeBytes $RequiredFreeBytes
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    $free = Get-ExtractFreeBytes -Path $Destination
    if ($null -ne $free -and $free -lt $RequiredFreeBytes) {
        throw "Not enough free space for the Vintage Story client at ${Destination}: $([math]::Round($free / 1MB)) MB free, $([math]::Round($RequiredFreeBytes / 1MB)) MB needed."
    }
}

# Reads the Win32 long path switch for the failure hint. Windows applications
# also need a longPathAware manifest, which innoextract lacks, so the value is
# reported but never relied on.
function Get-LongPathsEnabledState {
    try {
        $value = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction Stop).LongPathsEnabled
        return $(if ($value -eq 1) { 'enabled' } else { 'disabled' })
    } catch {
        return 'unknown'
    }
}

# Explains a failed extraction. Names the path limit when the staging path is
# long enough for it to be the cause.
function Get-ExtractFailureHint {
    param(
        [Parameter(Mandatory)][string]$StageRoot
    )
    $total = $StageRoot.Length + $script:ExtractStageDeepestEntry
    $hint = "Extraction directory: $StageRoot ($($StageRoot.Length) characters; the deepest installer entry brings it to about $total). LongPathsEnabled: $(Get-LongPathsEnabledState)."
    if ($total -gt $script:ExtractStageMaxPath) {
        $hint += " Windows rejects paths over 260 characters even with LongPathsEnabled. Move the Optimum folder closer to the drive root, or install Vintage Story and pass -VanillaDir."
    }
    return $hint
}
