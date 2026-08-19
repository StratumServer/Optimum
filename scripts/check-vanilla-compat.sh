#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
patches_dir="$repo_root/patches"
compat_allowlist="$script_dir/vanilla-compat-allowlist.txt"

# Ground truth for which vanilla client this build actually targets: the
# extracted client always drops an empty assets/version-X.Y.Z.txt marker
# (see .vanilla/win-x64/vintagestory/assets/). Falls back to forks.json's
# pinned version if .vanilla/ hasn't been populated yet (e.g. this script
# run before bootstrap). Do not hardcode a version number below this line -
# use $game_version so the checks work for any supported version.
game_version="$(find "$repo_root/.vanilla/win-x64/vintagestory/assets" -maxdepth 1 -name 'version-*.txt' 2>/dev/null \
  | head -1 | sed -E 's#.*/version-([0-9.]+)\.txt#\1#')"
if [[ -z "$game_version" ]]; then
  game_version="$(python3 -c "import json;print(json.load(open('$repo_root/forks.json'))['vintageStoryVersion'])" 2>/dev/null || echo 1.22.7)"
fi
game_version_re="${game_version//./\\.}"

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
    local rel="${patch#$repo_root/}"
    while IFS= read -r target; do
      [[ -z "$target" ]] && continue
      if [[ "$target" =~ $dangerous_target ]]; then
        if is_allowlisted "$rel"; then
          skip "patch touches multiplayer compatibility target (allowlisted): $rel -> $target"
        else
          fail "patch touches multiplayer compatibility target: $rel -> $target"
        fi
      fi
    done < <(patch_target_paths "$patch")
  done < <(find "$patches_dir" -type f -name '*.patch' \
    -not -path "$patches_dir/runtime/*" -print0)
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
  done < <(find "$patches_dir" -type f -name '*.patch' \
    -not -path "$patches_dir/runtime/*" -print0)
}

# Decompiler type-misbinding check: compare castclass/isinst targets in the
# compiled donor against the vanilla assembly. ilspy can bind a same-named
# type to the wrong namespace and the recompile accepts it silently; a wrong
# cast target only fails at runtime (EventHelper cast handlers to System.Func
# where vanilla IL uses Vintagestory.API.Common.Func, so every cancellable
# event handler threw InvalidCastException in full-compiled builds).
check_cast_divergences() {
  local vanilla_dll="$repo_root/.vanilla/win-x64/vintagestory/VintagestoryLib.vanilla.dll"
  local donor_dll="$repo_root/build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll"

  if [[ ! -f "$vanilla_dll" || ! -f "$donor_dll" ]]; then
    skip "cast divergence check: vanilla or compiled VintagestoryLib.dll not built"
    return
  fi

  if ! dotnet run --project "$repo_root/Optimum.Patcher" -c Release -- \
      --compare-casts "$vanilla_dll" "$donor_dll"; then
    fail "compiled VintagestoryLib casts diverge from vanilla IL (decompiler misbinding), see above"
  fi
}

cd "$repo_root"

check_patch_targets
check_patch_content
check_cast_divergences

# NetworkVersion is the wire-protocol version, distinct from the game
# version, and confirmed unchanged between 1.22.5 and 1.22.6 (see
# ref/vintagestory/1.22.6/source/DIFF-1.22.5-1.22.6.md). Left as a literal
# "1.22.6" intentionally - do not swap in $game_version here.
check_contains \
  "$repo_root/build/VintagestoryLib/Vintagestory.Client/ClientPackets.cs" \
  'NetworkVersion = "1\.22\.6"' \
  "client sends vanilla network version"

check_contains \
  "$repo_root/build/VintagestoryLib/Vintagestory.Client/ClientPackets.cs" \
  "ShortGameVersion = \"$game_version_re\"" \
  "client sends vanilla short game version ($game_version)"

check_contains \
  "$repo_root/build/VintagestoryLib/Vintagestory.Server/ServerMain.cs" \
  '"1\.22\.6" != identification\.NetworkVersion' \
  "server expects vanilla network version"

check_contains \
  "$repo_root/patches/VSEssentials/Entity/Behavior/BehaviorRepulseAgents.cs.patch" \
  'OptimumConfig\.RepulsionGateEnabled.*cworld != null' \
  "repulsion patch keeps client-world gate"

check_contains \
  "$repo_root/patches/VSSurvivalMod/BlockEntity/BEMicroBlock.cs.patch" \
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
