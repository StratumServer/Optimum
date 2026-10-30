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

# Returns a new, empty staging directory whose full path leaves room for the
# deepest installer entry. Prefers the temp folder, then the drive root, then
# $Fallback (the previous location beside the target). Throws when none can be
# created.
function New-ExtractStageRoot {
    param(
        [Parameter(Mandatory)][string]$Fallback,
        [string[]]$Candidates
    )
    if (-not $Candidates) {
        $Candidates = @([IO.Path]::GetTempPath())
        if ($env:SystemDrive) { $Candidates += "$($env:SystemDrive)\" }
    }
    $name = "optimum-ix-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    foreach ($parent in $Candidates + @($Fallback)) {
        if (-not $parent) { continue }
        $root = Join-Path $parent $name
        if (($root.Length + $script:ExtractStageDeepestEntry) -gt $script:ExtractStageMaxPath -and $parent -ne $Fallback) { continue }
        try {
            New-Item -ItemType Directory -Force -Path $root -ErrorAction Stop | Out-Null
            return $root
        } catch {
            continue
        }
    }
    throw "Could not create an extraction directory under any of: $(($Candidates + @($Fallback)) -join ', ')"
}

# Explains a failed extraction. Names the path limit when the staging path is
# long enough for it to be the cause.
function Get-ExtractFailureHint {
    param(
        [Parameter(Mandatory)][string]$StageRoot
    )
    $total = $StageRoot.Length + $script:ExtractStageDeepestEntry
    $hint = "Extraction directory: $StageRoot ($($StageRoot.Length) characters; the deepest installer entry brings it to about $total)."
    if ($total -gt $script:ExtractStageMaxPath) {
        $hint += " Windows rejects paths over 260 characters even with LongPathsEnabled. Move the Optimum folder closer to the drive root, or install Vintage Story and pass -VanillaDir."
    }
    return $hint
}
