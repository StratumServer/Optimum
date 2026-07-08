#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
patches_dir="$repo_root/patches"
vanilla_patch_projects="VintagestoryLib Vintagestory"
cecil_list="$patches_dir/cecil-owned.list"
patcher_program="$repo_root/Optimum.Patcher/Program.cs"

usage() {
  cat <<'EOF'
Usage: scripts/check-patches.sh
       scripts/check-patches.sh --strict-unavailable

Checks each patch against the current working tree. A patch passes when reverse
apply succeeds, which means the tree contains the optimization.

VintagestoryLib.dll ships patched by Mono.Cecil transplant over the vanilla
DLL, not recompiled, so a patch whose release effect ships that way is listed
in patches/cecil-owned.list and reported as "cecil" against the donor tree
(build/) instead of "applied". A patch on that list that fails reverse-apply
still counts as a conflict.

The script marks absent targets in partial checkouts as unavailable. Use
--strict-unavailable when every fork should exist, or when a mismatch between
patches/cecil-owned.list and Optimum.Patcher/Program.cs's own target list
should fail the run instead of just printing a warning.
EOF
}

strict_unavailable=0

case "${1:-}" in
  -h|--help)
  usage
  exit 0
  ;;
  --strict-unavailable)
  strict_unavailable=1
  ;;
  "")
  ;;
  *)
  usage >&2
  exit 2
  ;;
esac

try_patch_check() {
  local patch="$1"
  local reverse="$2"
  local strip="$3"
  local dir_arg="$4"
  local args=(--check --whitespace=nowarn)
  local output

  if [[ "$reverse" == "1" ]]; then
    args+=(--reverse)
  fi

  if [[ -n "$dir_arg" ]]; then
    args+=("$dir_arg")
  fi

  if [[ "$strip" == "p0" ]]; then
    args+=(-p0)
  elif [[ "$strip" == "root-p0" ]]; then
    args=(-p0 --check --whitespace=nowarn)
    if [[ "$reverse" == "1" ]]; then
      args+=(--reverse)
    fi
  fi

  if output="$(git apply "${args[@]}" "$patch" 2>&1)"; then
    return 0
  fi

  patch_error="$output"
  if [[ "$output" == *"patch does not apply"* || "$output" == *"patch failed"* ]]; then
    conflict_error="$output"
  fi
  return 1
}

patch_target_paths() {
  local patch="$1"

  awk '
    /^\+\+\+ / {
      path=$2
      if (path == "/dev/null") next
      sub(/^a\//, "", path)
      sub(/^b\//, "", path)
      sub(/^\.baseline\//, "", path)
      print path
    }
  ' "$patch" | sort -u
}

patch_has_unavailable_target() {
  local patch="$1"
  local dir_arg="$2"
  local relpath
  local actual
  local found_target=0

  while IFS= read -r relpath; do
    [[ -z "$relpath" ]] && continue
    found_target=1

    if [[ -n "$dir_arg" && "$relpath" != build/* ]]; then
      actual="build/$relpath"
    else
      actual="$relpath"
    fi

    if [[ ! -e "$repo_root/$actual" ]]; then
      unavailable_path="$actual"
      return 0
    fi
  done < <(patch_target_paths "$patch")

  if [[ "$found_target" == "0" ]]; then
    unavailable_path=""
    return 1
  fi

  return 1
}

cecil_type_to_patch() {
  local type="$1" ns class
  class="${type##*.}"
  ns="${type%.*}"
  # The decompiled tree mirrors namespaces as directories, so the namespace
  # itself is the patch subdirectory (Vintagestory.Client.NoObf,
  # Vintagestory.Client, Vintagestory.Common, ...).
  echo "patches/VintagestoryLib/$ns/$class.cs.patch"
}

check_cecil_cross_reference() {
  local mismatch=0
  local -A expected=()
  local type rel

  if [[ ! -f "$patcher_program" ]]; then
    return 0
  fi

  while IFS= read -r type; do
    [[ -z "$type" ]] && continue
    rel="$(cecil_type_to_patch "$type")"
    expected["$rel"]=1
    if [[ ! -f "$repo_root/$rel" ]]; then
      continue
    fi
    if [[ -z "${cecil_owned["$rel"]:-}" ]]; then
      echo "cecil-owned.list is missing a patch Program.cs targets: $rel" >&2
      mismatch=1
    fi
  done < <(grep -oE '"Vintagestory\.(Client(\.NoObf)?|Common)\.[A-Za-z0-9_]+"' "$patcher_program" | tr -d '"' | sort -u)

  for rel in "${!cecil_owned[@]}"; do
    if [[ -z "${expected["$rel"]:-}" ]]; then
      echo "cecil-owned.list lists a patch no Program.cs target maps to: $rel" >&2
      mismatch=1
    fi
  done

  return "$mismatch"
}

if [[ ! -d "$patches_dir" ]]; then
  echo "No patches directory found." >&2
  exit 1
fi

cd "$repo_root"

declare -A cecil_owned=()
if [[ -f "$cecil_list" ]]; then
  while IFS= read -r line; do
    [[ -z "$line" || "$line" == \#* ]] && continue
    cecil_owned["$line"]=1
  done < "$cecil_list"
fi

cecil_cross_reference_mismatch=0
check_cecil_cross_reference || cecil_cross_reference_mismatch=1

applied=0
cecil=0
pending=0
unavailable=0
conflict=0
total=0

while IFS= read -r -d '' patch; do
  total=$((total+1))
  rel="${patch#$repo_root/}"
  top_proj="$(echo "$rel" | cut -d/ -f2)"
  dir_arg=""
  patch_error=""
  conflict_error=""
  unavailable_path=""
  mode=""
  state=""

  if echo "$vanilla_patch_projects" | grep -qw "$top_proj"; then
    dir_arg="--directory=build"
  fi

  if ! patch_error="$(git apply --stat "$patch" 2>&1 >/dev/null)"; then
    state="conflict"
    mode="syntax"
    conflict=$((conflict+1))
  elif patch_has_unavailable_target "$patch" "$dir_arg"; then
    state="unavailable"
    mode="target"
    unavailable=$((unavailable+1))
  elif try_patch_check "$patch" 1 default "$dir_arg"; then
    state="applied"
    mode="default"
    applied=$((applied+1))
  elif try_patch_check "$patch" 1 p0 "$dir_arg"; then
    state="applied"
    mode="p0"
    applied=$((applied+1))
  elif [[ -n "$dir_arg" ]] && try_patch_check "$patch" 1 root-p0 "$dir_arg"; then
    state="applied"
    mode="root-p0"
    applied=$((applied+1))
  elif try_patch_check "$patch" 0 default "$dir_arg"; then
    state="pending"
    mode="default"
    pending=$((pending+1))
  elif try_patch_check "$patch" 0 p0 "$dir_arg"; then
    state="pending"
    mode="p0"
    pending=$((pending+1))
  elif [[ -n "$dir_arg" ]] && try_patch_check "$patch" 0 root-p0 "$dir_arg"; then
    state="pending"
    mode="root-p0"
    pending=$((pending+1))
  else
    if [[ -n "$conflict_error" ]]; then
      state="conflict"
      conflict=$((conflict+1))
      patch_error="$conflict_error"
    elif [[ "$patch_error" == *"No such file"* || "$patch_error" == *"does not exist"* || "$patch_error" == *"No such file or directory"* ]]; then
      state="unavailable"
      unavailable=$((unavailable+1))
    else
      state="conflict"
      conflict=$((conflict+1))
    fi
    mode="-"
  fi

  if [[ "$state" == "applied" && -n "${cecil_owned["$rel"]:-}" ]]; then
    state="cecil"
    applied=$((applied-1))
    cecil=$((cecil+1))
  fi

  if [[ "$state" != "applied" && "$state" != "cecil" ]]; then
    printf '%-8s %-7s %s\n' "$state" "$mode" "$rel"
    if [[ -n "$unavailable_path" ]]; then
      printf '  target missing: %s\n' "$unavailable_path"
    fi
    if [[ -n "$patch_error" && "$state" != "pending" ]]; then
      printf '  %s\n' "$(printf '%s\n' "$patch_error" | head -1)"
    fi
  fi
done < <(find "$patches_dir" -type f -name '*.patch' -print0 | sort -z)

echo "Patches: $applied applied, $cecil cecil, $pending pending, $unavailable unavailable, $conflict conflict, $total total"

if [[ "$cecil_cross_reference_mismatch" == "1" ]]; then
  echo "cecil-owned.list and Optimum.Patcher/Program.cs disagree, see warnings above." >&2
  if [[ "$strict_unavailable" == "1" ]]; then
    exit 1
  fi
fi

if [[ "$pending" -gt 0 || "$conflict" -gt 0 ]]; then
  exit 1
fi

if [[ "$strict_unavailable" == "1" && "$unavailable" -gt 0 ]]; then
  exit 1
fi

# Orphan check: every VintagestoryLib patch must be in cecil-owned.list.
# The recompiled VintagestoryLib.dll never ships (ILSpy loses FieldRVA),
# so a Lib patch not on the cecil list never reaches players.
orphans=0
while IFS= read -r -d '' patch; do
  rel="${patch#$repo_root/}"
  if [[ -z "${cecil_owned["$rel"]:-}" ]]; then
    echo "ORPHAN: $rel (not in cecil-owned.list, will not ship)" >&2
    orphans=$((orphans+1))
  fi
done < <(find "$patches_dir/VintagestoryLib" -type f -name '*.patch' -print0 2>/dev/null | sort -z)

if [[ "$orphans" -gt 0 ]]; then
  echo "$orphans orphaned VintagestoryLib patch(es) found. Add to cecil-owned.list or remove." >&2
  exit 1
fi
