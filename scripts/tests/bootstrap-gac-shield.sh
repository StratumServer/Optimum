#!/usr/bin/env bash
# Tests the ilspycmd GAC shield in scripts/bootstrap.sh and scripts/bootstrap.ps1.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

# 1. Assert coupling with both bootstrap scripts
grep -qF 'gac-shield=v1' "$REPO_ROOT/scripts/bootstrap.sh" || {
  echo "Expected gac-shield=v1 manifest marker in scripts/bootstrap.sh" >&2
  exit 1
}
grep -qF 'ilspycmd -r "$gac_shield_dir"' "$REPO_ROOT/scripts/bootstrap.sh" || {
  echo "Expected -r gac_shield_dir ilspycmd invocation in scripts/bootstrap.sh" >&2
  exit 1
}
grep -qF 'gac-shield=v1' "$REPO_ROOT/scripts/bootstrap.ps1" || {
  echo "Expected gac-shield=v1 manifest marker in scripts/bootstrap.ps1" >&2
  exit 1
}
grep -qF 'if ($gacShieldDir) { $ilspyArgs += @('\''-r'\'', $gacShieldDir) }' "$REPO_ROOT/scripts/bootstrap.ps1" || {
  echo "Expected -r gacShieldDir ilspycmd invocation in scripts/bootstrap.ps1" >&2
  exit 1
}

# 2. Test Bash shield creation and root-DLL exclusion
vanilla_dir="$TEST_ROOT/.vanilla/win-x64"
mkdir -p "$vanilla_dir/vintagestory/Lib"
printf 'root-api' > "$vanilla_dir/vintagestory/VintagestoryAPI.dll"
printf 'lib-json' > "$vanilla_dir/vintagestory/Lib/Newtonsoft.Json.dll"
printf 'lib-cecil' > "$vanilla_dir/vintagestory/Lib/Mono.Cecil.dll"
printf 'lib-dup' > "$vanilla_dir/vintagestory/Lib/VintagestoryAPI.dll"
printf 'not-a-dll' > "$vanilla_dir/vintagestory/Lib/readme.txt"

gac_shield_dir="$(mktemp -d "$TEST_ROOT/optimum-gac-shield.XXXXXX")"
lib_dir="$vanilla_dir/vintagestory/Lib"
root_dir="$vanilla_dir/vintagestory"
for lib_dll in "$lib_dir"/*.dll "$lib_dir"/*.DLL; do
  [[ -f "$lib_dll" ]] || continue
  base_name="$(basename "$lib_dll")"
  if [[ ! -f "$root_dir/$base_name" ]]; then
    : > "$gac_shield_dir/$base_name"
  fi
done

[[ -f "$gac_shield_dir/Newtonsoft.Json.dll" ]] || {
  echo "Missing Newtonsoft.Json.dll placeholder in shield directory" >&2
  exit 1
}
[[ ! -s "$gac_shield_dir/Newtonsoft.Json.dll" ]] || {
  echo "Expected 0-byte placeholder for Newtonsoft.Json.dll" >&2
  exit 1
}
[[ -f "$gac_shield_dir/Mono.Cecil.dll" ]] && [[ ! -s "$gac_shield_dir/Mono.Cecil.dll" ]] || {
  echo "Expected 0-byte placeholder for Mono.Cecil.dll" >&2
  exit 1
}
if [[ -e "$gac_shield_dir/VintagestoryAPI.dll" ]]; then
  echo "Root assembly VintagestoryAPI.dll must not be shadowed in shield directory" >&2
  exit 1
fi
if [[ -e "$gac_shield_dir/readme.txt" ]]; then
  echo "Non-DLL file should not be placed in shield directory" >&2
  exit 1
fi

# 3. Verify legacy 2-line manifest invalidates against 3-line gac-shield=v1 manifest
manifest_file="$TEST_ROOT/VintagestoryLib.manifest"
printf '%s\n' "abc123" "ilspycmd: 11.0.0.9375" > "$manifest_file"
if cmp -s "$manifest_file" <(printf '%s\n' "abc123" "ilspycmd: 11.0.0.9375" "gac-shield=v1"); then
  echo "Legacy 2-line decompile manifest should not match gac-shield=v1" >&2
  exit 1
fi
printf '%s\n' "abc123" "ilspycmd: 11.0.0.9375" "gac-shield=v1" > "$manifest_file"
cmp -s "$manifest_file" <(printf '%s\n' "abc123" "ilspycmd: 11.0.0.9375" "gac-shield=v1") || {
  echo "Updated 3-line decompile manifest failed to match" >&2
  exit 1
}

echo "All Bash GAC shield tests passed."
