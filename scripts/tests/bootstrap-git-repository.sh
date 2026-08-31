#!/usr/bin/env bash
# shellcheck disable=SC2016  # coupling assertions grep for literal script text

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

# The production script clears these before cloning. Restore an invalid value
# after the clone to exercise the explicit repository-path helper directly.
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE
# Keep these assertions coupled to the bootstrap scripts so the test cannot
# silently drift into testing an unused helper. Every git call against a fresh
# clone - the compile forks and the reference repos, in both the Bash and the
# PowerShell bootstrap - must go through the explicit repository-path helper,
# never plain "git -C".
require_line() {
  local needle="$1" file="$2"
  grep -qF -- "$needle" "$file" || {
    echo "expected '$needle' in $file" >&2
    exit 1
  }
}
forbid_pattern() {
  local pattern="$1" file="$2"
  if grep -nE -- "$pattern" "$file"; then
    echo "$file still runs 'git -C' against a fresh clone" >&2
    exit 1
  fi
}

BASH_BOOTSTRAP="$REPO_ROOT/scripts/bootstrap.sh"
require_line 'source "$script_dir/git-repository.sh"' "$BASH_BOOTSTRAP"
require_line 'optimum_git_in_clone "$base" checkout' "$BASH_BOOTSTRAP"
require_line 'optimum_git_in_clone "$dest" checkout' "$BASH_BOOTSTRAP"
forbid_pattern 'git -C "\$(base|dest)"' "$BASH_BOOTSTRAP"

PS_BOOTSTRAP="$REPO_ROOT/scripts/bootstrap.ps1"
require_line 'git-repository.ps1' "$PS_BOOTSTRAP"
require_line 'Remove-Item -Path "Env:$gitEnv"' "$PS_BOOTSTRAP"
require_line 'Invoke-GitInClone $base checkout' "$PS_BOOTSTRAP"
require_line 'Invoke-GitInClone $dest checkout' "$PS_BOOTSTRAP"
forbid_pattern 'git -C \$(base|dest)' "$PS_BOOTSTRAP"

# shellcheck disable=SC1091
source "$REPO_ROOT/scripts/git-repository.sh"

remote="$TEST_ROOT/remote.git"
seed="$TEST_ROOT/seed"
clone="$TEST_ROOT/clone"

git init --bare --quiet "$remote"
git init --quiet "$seed"
git -C "$seed" config user.name Test
git -C "$seed" config user.email test@example.invalid
printf '%s\n' 'bootstrap git repository test' > "$seed/README"
git -C "$seed" add README
git -c commit.gpgsign=false -C "$seed" commit --quiet -m 'test repository'
git -C "$seed" branch -M main
git -C "$seed" remote add origin "$remote"
git -C "$seed" push --quiet --set-upstream origin main
git -C "$remote" symbolic-ref HEAD refs/heads/main

git clone --quiet "$remote" "$clone"
export GIT_DIR="$TEST_ROOT/does-not-exist.git"
export GIT_WORK_TREE="$TEST_ROOT/does-not-exist-worktree"

if git -C "$clone" config core.autocrlf false 2>"$TEST_ROOT/implicit-error"; then
  echo 'implicit Git discovery unexpectedly ignored the invalid environment' >&2
  exit 1
fi
grep -F 'fatal: not in a git directory' "$TEST_ROOT/implicit-error" >/dev/null

optimum_git_in_clone "$clone" config core.autocrlf false
optimum_git_in_clone "$clone" config core.eol lf
optimum_git_in_clone "$clone" checkout --quiet main
[[ "$(optimum_git_in_clone "$clone" config --get core.autocrlf)" == false ]]
[[ "$(optimum_git_in_clone "$clone" config --get core.eol)" == lf ]]
[[ "$(<"$clone/README")" == 'bootstrap git repository test' ]]

# Exercise the PowerShell helper the same way when a shell is available.
if command -v pwsh >/dev/null 2>&1; then
  ps_clone="$TEST_ROOT/clone-ps"
  env -u GIT_DIR -u GIT_WORK_TREE git clone --quiet "$remote" "$ps_clone"
  pwsh -NoProfile -NonInteractive -Command "
    \$ErrorActionPreference = 'Stop'
    . '$REPO_ROOT/scripts/_exec.ps1'
    . '$REPO_ROOT/scripts/git-repository.ps1'
    # Same hostile environment and call shape as bootstrap.ps1's clone loop.
    \$env:GIT_DIR = '$TEST_ROOT/does-not-exist.git'
    \$env:GIT_WORK_TREE = '$TEST_ROOT/does-not-exist-worktree'
    Invoke-NativeStep { Invoke-GitInClone '$ps_clone' config core.autocrlf false }
    Invoke-NativeStep { Invoke-GitInClone '$ps_clone' config core.eol lf }
    Invoke-NativeStep { Invoke-GitInClone '$ps_clone' checkout --quiet main }
    if ((Invoke-GitInClone '$ps_clone' config --get core.autocrlf) -ne 'false') { throw 'core.autocrlf not set via helper' }
    if ((Get-Content '$ps_clone/README' -Raw).Trim() -ne 'bootstrap git repository test') { throw 'checkout via helper did not populate the work tree' }
  "
  echo 'PowerShell Git repository helper test passed.'
else
  echo 'pwsh not found; skipped PowerShell helper test.'
fi

echo 'Bootstrap Git repository test passed.'
