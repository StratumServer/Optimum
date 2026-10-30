# innoextract is a Win32 program. Its Windows build (crazy-max v1.13.0) embeds a
# manifest without longPathAware, and Windows only lifts the 260 character path
# limit for a process that has both LongPathsEnabled=1 and that manifest element
# (https://learn.microsoft.com/windows/win32/fileio/maximum-file-path-limitation).
# So the limit applies to it whatever LongPathsEnabled says. The deepest entry
# in the Vintage Story installer is 129 characters below the extraction root,
# so the staging directory has to be short. Extracting beside the final
# location (<src>\.vanilla\win-x64\.innoextract-stage-<guid>) adds 100+
# characters to every entry before the archive's own path starts.

# Deepest installer entry known today, in characters below the extraction root.
$script:ExtractStageKnownDeepest = 129

# Length budget used to choose a staging directory: the known entry plus a
# margin for assets added by a future Vintage Story release. Under the limits
# below, which are 259 characters for a file and 247 for a directory (a
# directory path must leave room for an 8.3 name).
$script:ExtractStageDeepestEntry = 160
$script:ExtractStageMaxPath = 240
$script:ExtractStageHardMaxDirectory = 247

# Directories older than this that match optimum-ix-* are leftovers of a run
# that was killed before it could clean up.
$script:ExtractStageStaleAfterHours = 24

# Free space the extraction needs on the staging volume and on the volume that
# receives the result. The extracted client is about 1 GB; this leaves room for
# the move and for a retry. When the installer is not cached yet it also needs
# room for the download (about 570 MB), and when staging and destination share
# a volume the two requirements add up.
$script:ExtractStageRequiredFreeBytes = 2GB
$script:ExtractStageArchiveBytes = 700MB

# Deepest installer entry, as path segments below the extraction root. The
# probe below creates it for real, so a stage directory that cannot be written
# at that depth (permissions, a filter driver, the path limit under Windows
# PowerShell 5.1) is found before the 570 MB download. It cannot predict
# per-process blocks such as Controlled Folder Access, which judge
# innoextract.exe and not this script, and under PowerShell 7 .NET can create
# paths innoextract cannot, so the length budget above is the real guard.
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

# Removes a staging directory and warns when it cannot, instead of leaving it
# behind silently.
function Remove-ExtractStageDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Warning "Could not remove the temporary extraction directory $Path ($($_.Exception.Message)). It is safe to delete."
    }
}

# Removes optimum-ix-* directories left by an earlier run that was killed.
function Remove-StaleExtractStageDirectories {
    param([Parameter(Mandatory)][string]$Parent)
    try {
        $cutoff = (Get-Date).AddHours(-$script:ExtractStageStaleAfterHours)
        Get-ChildItem -LiteralPath $Parent -Directory -Filter 'optimum-ix-*' -ErrorAction Stop |
            Where-Object { $_.LastWriteTime -lt $cutoff } |
            ForEach-Object { Remove-ExtractStageDirectory -Path $_.FullName }
    } catch { }
}

# Creates the directory only if it does not exist yet (another user or process
# cannot have pre-created it), and on Windows restricts it to the current user
# and SYSTEM, because the contents are moved into the build tree afterwards.
function New-PrivateExtractDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { throw "$Path already exists" }
    New-Item -ItemType Directory -Path $Path -ErrorAction Stop | Out-Null
    if ($env:OS -eq 'Windows_NT') {
        try {
            $acl = Get-Acl -LiteralPath $Path
            $acl.SetAccessRuleProtection($true, $false)
            $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
            foreach ($identity in @([Security.Principal.WindowsIdentity]::GetCurrent().User, (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
                $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inherit, 'None', 'Allow')))
            }
            Set-Acl -LiteralPath $Path -AclObject $acl
        } catch {
            Remove-ExtractStageDirectory -Path $Path
            throw "could not restrict access: $($_.Exception.Message)"
        }
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

# Controlled Folder Access (Windows Security, ransomware protection) blocks
# programs it does not trust from writing to protected folders: Documents,
# Pictures, Videos, Music, Desktop and Favorites for the user and for Public,
# plus any folder the user added. It judges the writing program, so this script
# (PowerShell) can create the probe file where innoextract.exe cannot extract.
# What can be known up front is whether the feature is on and which folders it
# protects; a staging directory inside one is rejected. Anything else is caught
# by retrying with another location when the extraction itself fails.
# Enabled is 1 (blocking), 2 (audit only), 0 (off) or $null (not readable).
function Get-ControlledFolderAccessInfo {
    if ($env:OS -ne 'Windows_NT') { return [pscustomobject]@{ Enabled = $null; ProtectedFolders = @() } }
    $folders = New-Object System.Collections.Generic.List[string]
    $enabled = $null
    $base = 'HKLM:\SOFTWARE\{0}\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access'
    foreach ($key in @(($base -f 'Policies\Microsoft'), ($base -f 'Microsoft'))) {
        try {
            $value = (Get-ItemProperty -LiteralPath $key -Name EnableControlledFolderAccess -ErrorAction Stop).EnableControlledFolderAccess
            if ($null -ne $value) { $enabled = [int]$value; break }
        } catch { }
    }
    foreach ($key in @((($base -f 'Microsoft') + '\ProtectedFolders'), (($base -f 'Policies\Microsoft') + '\ProtectedFolders'))) {
        try {
            $item = Get-Item -LiteralPath $key -ErrorAction Stop
            foreach ($name in $item.GetValueNames()) { if ($name) { $folders.Add($name) } }
        } catch { }
    }
    foreach ($special in 'MyDocuments', 'MyPictures', 'MyVideos', 'MyMusic', 'Desktop', 'Favorites') {
        try { $folders.Add([Environment]::GetFolderPath($special)) } catch { }
    }
    if ($env:PUBLIC) { foreach ($leaf in 'Documents', 'Pictures', 'Videos', 'Music', 'Desktop') { $folders.Add((Join-Path $env:PUBLIC $leaf)) } }
    return [pscustomobject]@{ Enabled = $enabled; ProtectedFolders = @($folders | Where-Object { $_ } | Select-Object -Unique) }
}

# The protected folder that contains $Path, or $null.
function Get-ProtectedFolderFor {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Info)
    try { $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') } catch { return $null }
    foreach ($folder in $Info.ProtectedFolders) {
        $f = $folder.TrimEnd('\', '/')
        if ($full -ieq $f -or $full.StartsWith($f + '\', [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($f + '/', [StringComparison]::OrdinalIgnoreCase)) { return $folder }
    }
    return $null
}

# Parent directories to try for the staging folder, shortest and most likely
# to be writable first: the temp folder (and its 8.3 short form, which survives
# a long user name or a redirected temp), the system drive root, the root of
# every other fixed drive, the destination's own drive root, then the user
# profile. Duplicates and empty entries are dropped. The caller still
# checks each one, so a root that is read-only or full is simply skipped.
function Get-ExtractStageCandidates {
    param([string]$Destination)
    $list = New-Object System.Collections.Generic.List[string]
    function Add-Candidate([string]$Path) {
        if (-not $Path) { return }
        # A trailing separator does not make a different directory: the 8.3 form
        # of a short temp path is the same folder with the slash dropped.
        $key = $Path.TrimEnd('\', '/')
        if (-not ($list | Where-Object { $_.TrimEnd('\', '/') -ieq $key })) { $list.Add($Path) }
    }
    function Get-ShortForm([string]$Path) {
        # 8.3 form through the scripting runtime; Windows only, and only when
        # 8.3 names are enabled on the volume.
        try {
            if (-not (Test-Path -LiteralPath $Path)) { return $null }
            $short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($Path).ShortPath
            if ($short -and $short.Length -lt $Path.Length) { return $short }
        } catch { }
        return $null
    }

    $temp = [IO.Path]::GetTempPath()
    Add-Candidate $temp
    Add-Candidate (Get-ShortForm $temp)
    if ($env:SystemDrive) { Add-Candidate "$($env:SystemDrive)\" }
    # Other drives only matter on Windows; elsewhere the mounts are not drives.
    if ($env:OS -eq 'Windows_NT') { try {
        foreach ($drive in [IO.DriveInfo]::GetDrives()) {
            if ($drive.DriveType -eq 'Fixed' -and $drive.IsReady) { Add-Candidate $drive.RootDirectory.FullName }
        }
    } catch { } }
    if ($Destination) {
        try { Add-Candidate ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination))) } catch { }
    }
    # The profile is private to the user. ProgramData and PUBLIC are left out
    # because every local user can write there.
    Add-Candidate $env:USERPROFILE
    if ($env:USERPROFILE) { Add-Candidate (Get-ShortForm $env:USERPROFILE) }
    return $list.ToArray()
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
        [long]$RequiredFreeBytes = $script:ExtractStageRequiredFreeBytes,
        [string[]]$Exclude = @(),
        $ControlledFolderAccess
    )
    if (-not $Candidates) { $Candidates = @(Get-ExtractStageCandidates -Destination $Fallback) }
    if (-not $ControlledFolderAccess) { $ControlledFolderAccess = Get-ControlledFolderAccessInfo }
    $name = "optimum-ix-$([Guid]::NewGuid().ToString('N').Substring(0, 16))"
    $rejected = New-Object System.Collections.Generic.List[string]
    foreach ($parent in $Candidates + @($Fallback)) {
        if (-not $parent) { continue }
        $parentKey = $parent.TrimEnd('\', '/')
        if ($Exclude | Where-Object { $_.TrimEnd('\', '/') -ieq $parentKey }) { continue }
        $root = Join-Path $parent $name
        # The fallback is exempt from the safety margin only, never from the
        # real directory limit: a path over it cannot work for innoextract.
        $isFallback = ($parent -eq $Fallback)
        $needed = $root.Length + $(if ($isFallback) { $script:ExtractStageKnownDeepest } else { $script:ExtractStageDeepestEntry })
        $limit = $(if ($isFallback) { $script:ExtractStageHardMaxDirectory } else { $script:ExtractStageMaxPath })
        if ($needed -gt $limit) {
            $rejected.Add("$parent (path would reach $needed characters, limit $limit)")
            continue
        }
        if ($ControlledFolderAccess.Enabled -eq 1) {
            $protectedBy = Get-ProtectedFolderFor -Path $parent -Info $ControlledFolderAccess
            if ($protectedBy) {
                $rejected.Add("$parent (inside $protectedBy, which Controlled Folder Access protects)")
                continue
            }
        }
        if (Test-Path -LiteralPath $parent) { Remove-StaleExtractStageDirectories -Parent $parent }
        try {
            New-PrivateExtractDirectory -Path $root
        } catch {
            $rejected.Add("$parent (cannot create a private directory: $($_.Exception.Message))")
            continue
        }
        $free = Get-ExtractFreeBytes -Path $root
        if ($null -eq $free) {
            Write-Warning "Could not read the free space under $parent; trying it without that check."
        }
        if ($null -ne $free -and $free -lt $RequiredFreeBytes) {
            $rejected.Add("$parent (only $([math]::Round($free / 1MB)) MB free, need $([math]::Round($RequiredFreeBytes / 1MB)) MB)")
            Remove-ExtractStageDirectory -Path $root
            continue
        }
        $failure = Test-ExtractStageWritable -Root $root
        if ($failure) {
            $rejected.Add("$parent (cannot create the deepest installer path: $failure)")
            Remove-ExtractStageDirectory -Path $root
            continue
        }
        return $root
    }
    throw "No usable extraction directory for the Vintage Story client. Rejected: $($rejected -join '; '). Install Vintage Story and pass -VanillaDir, or move the Optimum folder closer to the drive root."
}

# Fails early, before the large download, when the extraction cannot work: no
# usable staging directory, or too little room where the result goes. Warns
# when the destination path is long enough that later build steps may fail.
function Assert-ExtractPreflight {
    param(
        [Parameter(Mandatory)][string]$Fallback,
        [Parameter(Mandatory)][string]$Destination,
        [string[]]$Candidates,
        [long]$RequiredFreeBytes = $script:ExtractStageRequiredFreeBytes,
        [switch]$ArchiveMissing
    )
    $stage = New-ExtractStageRoot -Fallback $Fallback -Candidates $Candidates -RequiredFreeBytes $RequiredFreeBytes
    try {
        $needed = $RequiredFreeBytes
        if ($ArchiveMissing) { $needed += $script:ExtractStageArchiveBytes }
        $destinationRoot = $null
        $stageRoot = $null
        try {
            $destinationRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination))
            $stageRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($stage))
        } catch { }
        # Staging and destination on one volume hold the extracted files twice
        # while the move runs.
        if ($destinationRoot -and $stageRoot -and ($destinationRoot -ieq $stageRoot)) { $needed += $RequiredFreeBytes }
        $free = Get-ExtractFreeBytes -Path $Destination
        if ($null -eq $free) {
            Write-Warning "Could not read the free space at $Destination (a network share or an unusual volume); continuing without that check."
        }
        if ($null -ne $free -and $free -lt $needed) {
            throw "Not enough free space for the Vintage Story client at ${Destination}: $([math]::Round($free / 1MB)) MB free, $([math]::Round($needed / 1MB)) MB needed."
        }
    } finally {
        Remove-ExtractStageDirectory -Path $stage
    }
    $destinationDeepest = $Destination.Length + 1 + $script:ExtractStageKnownDeepest
    if ($destinationDeepest -gt $script:ExtractStageHardMaxDirectory) {
        Write-Warning "The install path is long: the deepest client file will be $destinationDeepest characters at $Destination. Windows tools that do not opt in to long paths, including some build steps, can fail. If bootstrap stops with a path error, move the Optimum folder closer to the drive root."
    }
}

# Creates a staging directory and runs $Extract with it. $Extract receives the
# directory and returns the extractor's exit code (or throws). On a failure the
# directory is removed and the next location is tried, up to $MaxAttempts, so a
# location that only the extractor cannot write to (Controlled Folder Access,
# a security product, a filter driver) does not end the install while another
# location would work. Returns the staging directory of the attempt that
# succeeded; the caller moves the files out and removes it. Throws, naming each
# failed location, when every attempt fails.
function Invoke-ExtractWithStageFallback {
    param(
        [Parameter(Mandatory)][string]$Fallback,
        [Parameter(Mandatory)][scriptblock]$Extract,
        [string[]]$Candidates,
        [int]$MaxAttempts = 3,
        [string]$ExtractorPath,
        $ControlledFolderAccess
    )
    if (-not $ControlledFolderAccess) { $ControlledFolderAccess = Get-ControlledFolderAccessInfo }
    $tried = New-Object System.Collections.Generic.List[string]
    $failures = New-Object System.Collections.Generic.List[string]
    $parameters = @{ Fallback = $Fallback; ControlledFolderAccess = $ControlledFolderAccess }
    if ($Candidates) { $parameters.Candidates = $Candidates }
    $lastStage = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $stage = New-ExtractStageRoot @parameters -Exclude $tried.ToArray()
        } catch {
            if ($failures.Count -eq 0) { throw }
            $failures.Add("no other location qualifies ($($_.Exception.Message))")
            break
        }
        $code = $null
        $detail = $null
        try { $code = @(& $Extract $stage)[-1] } catch { $detail = $_.Exception.Message }
        if (-not $detail -and ($null -eq $code -or $code -eq 0)) { return $stage }
        if (-not $detail) { $detail = "exit $code" }
        $lastStage = $stage
        $tried.Add((Split-Path -Parent $stage))
        $failures.Add("$($tried[-1]) ($detail)")
        Remove-ExtractStageDirectory -Path $stage
        if ($attempt -lt $MaxAttempts) { Write-Warning "Extraction failed in $($tried[-1]) ($detail). Trying another location." }
    }
    throw "Extraction failed in every location tried: $($failures -join '; '). $(Get-ExtractFailureHint -StageRoot $lastStage -ExtractorPath $ExtractorPath -ControlledFolderAccess $ControlledFolderAccess)"
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
        [Parameter(Mandatory)][string]$StageRoot,
        [string]$ExtractorPath,
        $ControlledFolderAccess
    )
    if (-not $ControlledFolderAccess) { $ControlledFolderAccess = Get-ControlledFolderAccessInfo }
    $total = $StageRoot.Length + $script:ExtractStageDeepestEntry
    $hint = "Extraction directory: $StageRoot ($($StageRoot.Length) characters; the deepest installer entry brings it to about $total). LongPathsEnabled: $(Get-LongPathsEnabledState)."
    if ($total -gt $script:ExtractStageMaxPath) {
        $hint += " Windows rejects paths over 260 characters for programs that have not opted in to long paths, and innoextract has not. Move the Optimum folder closer to the drive root, or install Vintage Story and pass -VanillaDir."
    }
    if ($ControlledFolderAccess.Enabled -eq 1) {
        $protectedBy = Get-ProtectedFolderFor -Path $StageRoot -Info $ControlledFolderAccess
        if ($protectedBy) { $hint += " The directory is inside $protectedBy, which Controlled Folder Access protects." }
        $app = $(if ($ExtractorPath) { $ExtractorPath } else { 'innoextract.exe' })
        $hint += " Controlled Folder Access is on and blocks programs it does not trust from writing to protected folders. In Windows Security, open Virus & threat protection > Ransomware protection > Allow an app through Controlled folder access and add $app, or install Vintage Story and pass -VanillaDir."
    }
    return $hint
}
