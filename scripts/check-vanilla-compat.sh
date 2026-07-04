#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
patches_dir="$repo_root/patches"
compat_allowlist="$script_dir/vanilla-compat-allowlist.txt"

is_allowlisted() {
  local rel="$1"
  [[ -f "$compat_allowlist" ]] || return 1
  grep -qxF "$rel" <(grep -v '^#' "$compat_allowlist" | grep -v '^[[:space:]]*$')
}

failures=0
skips=0

fail() {
  printf 'FAIL %s\n' "$1"
  failures=$((failures+1))
}

skip() {
  printf 'SKIP %s\n' "$1"
  skips=$((skips+1))
}

check_contains() {
  local file="$1"
  local pattern="$2"
  local label="$3"

  if [[ ! -f "$file" ]]; then
    skip "$label: missing $file"
    return
  fi

  if ! rg -q "$pattern" "$file"; then
    fail "$label"
  fi
}

patch_target_paths() {
  local patch="$1"

  awk '
    /^(---|\+\+\+) / {
      path=$2
      if (path == "/dev/null") next
      sub(/^a\//, "", path)
      sub(/^b\//, "", path)
      sub(/^\.baseline\//, "", path)
      print path
    }
  ' "$patch" | sort -u
}

check_patch_targets() {
  local dangerous_target
  dangerous_target='(^|/)(Vintagestory\.Server/|Packet_[^/]*\.cs$|.*Serializer\.cs$|.*Proto.*\.cs$|ClientPackets\.cs$|ServerMain\.cs$|ModInfo[^/]*\.cs$)'

  while IFS= read -r -d '' patch; do
    while IFS= read -r target; do
      [[ -z "$target" ]] && continue
      if [[ "$target" =~ $dangerous_target ]]; then
        fail "patch touches multiplayer compatibility target: ${patch#$repo_root/} -> $target"
      fi
    done < <(patch_target_paths "$patch")
  done < <(find "$patches_dir" -type f -name '*.patch' -print0)
}

check_patch_content() {
  local dangerous_content
  dangerous_content='NetworkVersion|ShortGameVersion|ProtoContract|ProtoMember|ImplicitFields|ModInfoAttribute|RequiredOnClient|RequiredOnServer'

  # Scan only added/removed lines, not unified-diff context. A patch's hunk
  # context legitimately includes unrelated existing code (like a reference to
  # ShortGameVersion three lines above the actual change), and matching on the
  # whole file flags that context as if the patch itself introduced it.
  while IFS= read -r -d '' patch; do
    local rel="${patch#$repo_root/}"
    if grep -E '^[-+][^-+]' "$patch" | rg -q "$dangerous_content"; then
      if is_allowlisted "$rel"; then
        skip "patch changes multiplayer compatibility content (allowlisted): $rel"
      else
        fail "patch changes multiplayer compatibility content: $rel"
      fi
    fi
  done < <(find "$patches_dir" -type f -name '*.patch' -print0)
}

cd "$repo_root"

check_patch_targets
check_patch_content

check_contains \
  "$repo_root/build/VintagestoryLib/Vintagestory.Client/ClientPackets.cs" \
  'NetworkVersion = "1\.22\.6"' \
  "client sends vanilla network version"

check_contains \
  "$repo_root/build/VintagestoryLib/Vintagestory.Client/ClientPackets.cs" \
  'ShortGameVersion = "1\.22\.3"' \
  "client sends vanilla short game version"

check_contains \
  "$repo_root/build/VintagestoryLib/Vintagestory.Server/ServerMain.cs" \
  '"1\.22\.6" != identification\.NetworkVersion' \
  "server expects vanilla network version"

check_contains \
  "$repo_root/patches/VSEssentials/Entity/Behavior/BehaviorRepulseAgents.cs.patch" \
  'cworld != null' \
  "repulsion patch keeps client gate"

check_contains \
  "$repo_root/patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch" \
  'if \(capi == null\)' \
  "microblock patch keeps non-client guard"

check_contains \
  "$repo_root/patches/VSSurvivalMod/Block/BlockSmeltingContainer.cs.patch" \
  'api is not ICoreClientAPI capi' \
  "firepit renderer patch keeps client guard"

if [[ "$failures" -gt 0 ]]; then
  printf 'Vanilla compat: %d failed, %d skipped\n' "$failures" "$skips"
  exit 1
fi

printf 'Vanilla compat: ok, %d skipped\n' "$skips"
