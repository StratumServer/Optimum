#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ -z "${IN_NIX_SHELL:-}" ]]; then
    printf 'Run this smoke check from the project nix develop shell.\n' >&2
    exit 1
fi

actual_sdk="$(dotnet --version)"
sdk_requirement="$(python3 -c 'import json,sys; sdk=json.load(open(sys.argv[1]))["sdk"]; print(sdk["version"] + " " + sdk.get("rollForward", "patch"))' "$repo_root/global.json")"
read -r expected_sdk roll_forward <<< "$sdk_requirement"

if [[ ! "$expected_sdk" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ \
    || ! "$actual_sdk" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'Could not resolve the SDK version in global.json or dotnet output.\n' >&2
    exit 1
fi

expected_major_minor="$(cut -d. -f1-2 <<< "$expected_sdk")"
actual_major_minor="$(cut -d. -f1-2 <<< "$actual_sdk")"
if [[ "$actual_major_minor" != "$expected_major_minor" ]]; then
    printf 'global.json requires SDK %s with rollForward=%s, but dotnet selected %s\n' \
        "$expected_sdk" "$roll_forward" "$actual_sdk" >&2
    exit 1
fi

for tool in git perl python3 curl tar chmod; do
    command -v "$tool" >/dev/null || {
        printf 'Required bootstrap command is missing: %s\n' "$tool" >&2
        exit 1
    }
done

release="${OPTIMUM_SMOKE_OS_RELEASE:-}"
if [[ -z "$release" ]]; then
    release="$(nixos-version 2>/dev/null || true)"
fi
if [[ -z "$release" && -r /etc/os-release ]]; then
    # shellcheck disable=SC1091
    . /etc/os-release
    release="${PRETTY_NAME:-${NAME:-unknown}} ${VERSION_ID:-}"
fi

printf 'Operating system or container userspace release: %s\n' "${release:-unknown}"
printf 'Container kernel: %s\n' "$(uname -sr)"
printf "Smoke command: nix --extra-experimental-features 'nix-command flakes' develop --no-update-lock-file %q -c bash scripts/tests/nixos-dev-shell-smoke.sh\n" "$repo_root"
printf '.NET SDK selected by global.json (%s, rollForward=%s): %s\n' \
    "$expected_sdk" "$roll_forward" "$actual_sdk"
printf 'Bootstrap commands available: git perl python3 curl tar chmod\n'

bash "$repo_root/scripts/check-prereqs.sh"
