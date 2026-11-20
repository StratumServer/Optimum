#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
patches_dir="${1:-$repo_root/patches}"

if [[ "$patches_dir" != /* ]]; then
  patches_dir="$repo_root/$patches_dir"
fi

if [[ ! -d "$patches_dir" ]]; then
  echo "Patch directory not found: $patches_dir" >&2
  exit 1
fi

cd "$repo_root"

# Portable null-delimited sort (GNU sort -z / BSD fallback).
if printf '\0' | sort -z >/dev/null 2>&1; then
  sort_null() { sort -z; }
else
  sort_null() { perl -0 -e 'print sort <STDIN>'; }
fi

failed=0
total=0
patches=()

while IFS= read -r -d '' patch; do
  total=$((total + 1))
  has_old_header=0
  has_new_header=0

  while IFS= read -r line || [[ -n "$line" ]]; do
    case "$line" in
      '--- '*) has_old_header=1 ;;
      '+++ '*) has_new_header=1 ;;
    esac
  done < "$patch"

  if [[ "$has_old_header" == "0" || "$has_new_header" == "0" ]]; then
    echo "Invalid patch syntax: ${patch#$repo_root/}" >&2
    echo "  missing unified-diff file header (expected --- and +++)" >&2
    failed=1
    continue
  fi

  patches+=("$patch")
done < <(find "$patches_dir" -type f -name '*.patch' -print0 | sort_null)

if [[ "${#patches[@]}" -gt 0 ]] && ! git apply --stat "${patches[@]}" >/dev/null 2>&1; then
  for patch in "${patches[@]}"; do
    if ! output="$(git apply --stat "$patch" 2>&1 >/dev/null)"; then
      echo "Invalid patch syntax: ${patch#$repo_root/}" >&2
      printf '  %s\n' "$output" >&2
      failed=1
    fi
  done
fi

if [[ "$failed" == "1" ]]; then
  exit 1
fi

echo "Patch syntax: $total valid"
