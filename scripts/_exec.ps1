<#
Shared native-command execution helper.
Dot-source it:  . "$PSScriptRoot/_exec.ps1"

Windows PowerShell 5.1 (and old PowerShell Core builds) has a long-standing
quirk (PowerShell/PowerShell#4002): with $ErrorActionPreference = 'Stop', ANY
redirection of a native command's stderr - an explicit 2>&1 / 2>$null, or
merely running inside a caller's *>&1 capture (as install-windows.ps1 uses to
stream bootstrap.ps1/package.ps1 output into its log) - turns each stderr
LINE into a terminating error, using that line's text as the exception
message. This fires even when the command exits 0 and the stderr line is
just benign progress/warning text - or, worse, blank, which surfaces as an
empty "ERROR:" with no clue what happened.

ilspycmd hits this directly: it has its own bug (icsharpcode/ILSpy#3101)
where it prints "You are not using the latest version of the tool, please
update." to stderr even when already current. git, curl, and dotnet all
write routine progress/warning text to stderr too. Any of these can abort an
otherwise-successful build under EAP='Stop'.

Run every native command through Invoke-NativeStep so $ErrorActionPreference
can't turn its stderr into a phantom failure. $LASTEXITCODE is left set from
the command for the caller to check explicitly afterward.
#>

function Invoke-NativeStep {
    param([Parameter(Mandatory)][ScriptBlock]$Action)
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Action } finally { $ErrorActionPreference = $prevEAP }
}
