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

failed=0
total=0

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

  if ! output="$(git apply --stat "$patch" 2>&1 >/dev/null)"; then
    echo "Invalid patch syntax: ${patch#$repo_root/}" >&2
    printf '  %s\n' "$output" >&2
    failed=1
  fi
done < <(find "$patches_dir" -type f -name '*.patch' -print0 | sort -z)

if [[ "$failed" == "1" ]]; then
  exit 1
fi

echo "Patch syntax: $total valid"
