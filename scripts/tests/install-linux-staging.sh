#!/usr/bin/env bash
# Verifies the Linux installer stages the package without building an archive
# it never uses. Regression cover for the issue #23 follow-up: an install onto
# /mnt/zoomin stalled while package-linux.sh built a throwaway tar.gz in a
# small /tmp, after the staged folder was already complete.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

# --- package-linux.sh --format validation ------------------------------------

if bash "$REPO_ROOT/scripts/package-linux.sh" --format bogus >/dev/null 2>"$TEST_ROOT/fmt-err"; then
    echo 'package-linux.sh accepted an invalid --format' >&2
    exit 1
fi
grep -Fq "targz" "$TEST_ROOT/fmt-err"
grep -Fq "none" "$TEST_ROOT/fmt-err"

# The 'none' arm must sit before the catch-all so it does not fall through to
# the tar.gz default.
none_line="$(grep -n '^\s*none)' "$REPO_ROOT/scripts/package-linux.sh" | head -1 | cut -d: -f1)"
star_line="$(grep -n '^\s*\*)' "$REPO_ROOT/scripts/package-linux.sh" | tail -1 | cut -d: -f1)"
[[ -n "$none_line" && -n "$star_line" && "$none_line" -lt "$star_line" ]]

# --- install-linux.sh / install-linux-legacy.sh cleanup ---------------------

for installer in install-linux.sh install-linux-legacy.sh; do
    ( # subshell: sourcing runs the file's top level, not main
        trap - EXIT  # drop the inherited cleanup trap; test only what sourcing adds
        # shellcheck disable=SC1090
        source "$REPO_ROOT/scripts/$installer"

        # cleanup_stage is a no-op and succeeds when nothing was staged
        # (set -e above fails the test if it returns non-zero).
        STAGE_ROOT=""
        cleanup_stage

        # It removes the staged tree when one exists.
        STAGE_ROOT="$TEST_ROOT/stage-$installer"
        mkdir -p "$STAGE_ROOT/inner"
        cleanup_stage
        [[ ! -e "$STAGE_ROOT" ]]

        # Sourcing must not install an EXIT trap (that would clobber the trap
        # of any script, such as the prerequisite test, that sources this one).
        [[ -z "$(trap -p EXIT)" ]]

        # The installer asks package-linux.sh for the folder only.
        grep -Fq -- '--format none' "$REPO_ROOT/scripts/$installer"
    )
done

echo 'Linux installer staging test passed.'
