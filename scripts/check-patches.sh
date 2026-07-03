#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
patches_dir="$repo_root/patches"
vanilla_patch_projects="VintagestoryLib Vintagestory"

usage() {
  cat <<'EOF'
Usage: scripts/check-patches.sh
       scripts/check-patches.sh --strict-unavailable

Checks each patch against the current working tree. A patch passes when reverse
apply succeeds, which means the tree contains the optimization.

The script marks absent targets in partial checkouts as unavailable. Use
--strict-unavailable when every fork should exist.
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

if [[ ! -d "$patches_dir" ]]; then
  echo "No patches directory found." >&2
  exit 1
fi

cd "$repo_root"

applied=0
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

  if [[ "$state" != "applied" ]]; then
    printf '%-8s %-7s %s\n' "$state" "$mode" "$rel"
    if [[ -n "$unavailable_path" ]]; then
      printf '  target missing: %s\n' "$unavailable_path"
    fi
    if [[ -n "$patch_error" && "$state" != "pending" ]]; then
      printf '  %s\n' "$(printf '%s\n' "$patch_error" | head -1)"
    fi
  fi
done < <(find "$patches_dir" -type f -name '*.patch' -print0 | sort -z)

echo "Patches: $applied applied, $pending pending, $unavailable unavailable, $conflict conflict, $total total"

if [[ "$pending" -gt 0 || "$conflict" -gt 0 ]]; then
  exit 1
fi

if [[ "$strict_unavailable" == "1" && "$unavailable" -gt 0 ]]; then
  exit 1
fi
