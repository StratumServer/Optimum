# Run a Git command against a cloned working tree without relying on Git's
# current-directory discovery. This also makes the command immune to an
# inherited GIT_DIR selecting a different repository.
#
# PowerShell mirror of scripts/git-repository.sh's optimum_git_in_clone so
# bootstrap.ps1 and bootstrap.sh stay in lockstep.
function Invoke-GitInClone {
    param(
        [Parameter(Mandatory)][string]$CloneDir,
        [Parameter(ValueFromRemainingArguments)][string[]]$GitArgs
    )
    & git --git-dir "$CloneDir/.git" --work-tree "$CloneDir" @GitArgs
}
