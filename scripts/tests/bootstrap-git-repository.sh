#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

# The production script clears these before cloning. Restore an invalid value
# after the clone to exercise the explicit repository-path helper directly.
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE
# Keep this assertion coupled to bootstrap.sh so the test cannot silently
# drift into testing an unused helper.
grep -F "source \"\$script_dir/git-repository.sh\"" "$REPO_ROOT/scripts/bootstrap.sh" >/dev/null
grep -F "optimum_git_in_clone \"\$base\" checkout" "$REPO_ROOT/scripts/bootstrap.sh" >/dev/null
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

echo 'Bootstrap Git repository test passed.'
