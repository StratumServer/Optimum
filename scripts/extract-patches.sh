#!/usr/bin/env bash
set -euo pipefail

# Regenerates patches/ from the diff between .baseline/ and the working tree.
# Also syncs sources/ for files that have no baseline equivalent.

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

baseline_dir="$repo_root/.baseline"
patches_dir="$repo_root/patches"
sources_dir="$repo_root/sources"

if [[ ! -d "$baseline_dir" ]]; then
  echo "No .baseline/ found. Run scripts/bootstrap.sh first." >&2
  exit 1
fi

# Clear old patches and sources (regenerated from scratch).
rm -rf "$patches_dir" "$sources_dir"
mkdir -p "$patches_dir" "$sources_dir"

patch_count=0
source_count=0
stale_count=0

# Projects that live under baseline/ (decompiled closed-source).
vanilla_projects=("VintagestoryLib" "Vintagestory")

# All project directories in the working tree that have a baseline.
for base_project in "$baseline_dir"/*/; do
  project="$(basename "$base_project")"
  work_dir="$repo_root/$project"

  # For vanilla (decompiled) projects, the working copy is under baseline/.
  for vp in "${vanilla_projects[@]}"; do
    if [[ "$project" == "$vp" ]]; then
      work_dir="$repo_root/baseline/$project"
      break
    fi
  done

  if [[ ! -d "$work_dir" ]]; then
    continue
  fi

  # Generate diffs for modified files.
  while IFS= read -r -d '' file; do
    rel="${file#$work_dir/}"
    base_file="$baseline_dir/$project/$rel"

    if [[ ! -f "$base_file" ]]; then
      # New file (no baseline equivalent) → goes to sources/.
      dest="$sources_dir/$project/$rel"
      mkdir -p "$(dirname "$dest")"
      cp -f "$file" "$dest"
      source_count=$((source_count+1))
    elif ! diff -q "$base_file" "$file" >/dev/null 2>&1; then
      # Modified file → generate patch with git diff (5 lines of context for
      # displacement tolerance, index line for --3way merge in bootstrap).
      patch_file="$patches_dir/$project/$rel.patch"
      mkdir -p "$(dirname "$patch_file")"
      tmp_base="$(mktemp)"
      tmp_work="$(mktemp)"
      sed '1s/^\xEF\xBB\xBF//' "$base_file" | perl -pe 's/\r\n/\n/g; s/\r/\n/g' > "$tmp_base"
      sed '1s/^\xEF\xBB\xBF//' "$file" | perl -pe 's/\r\n/\n/g; s/\r/\n/g' > "$tmp_work"
      git --no-pager -c core.safecrlf=false diff --no-color --no-index -U5 \
        -- "$tmp_base" "$tmp_work" \
        | sed \
          -e "s#^diff --git .*\$#diff --git a/$project/$rel b/$project/$rel#" \
          -e "s#^--- .*\$#--- a/$project/$rel#" \
          -e "s#^+++ .*\$#+++ b/$project/$rel#" \
        > "$patch_file" || true
      rm -f "$tmp_base" "$tmp_work"
      patch_count=$((patch_count+1))
    fi
  done < <(find "$work_dir" -type f -not -path "*/obj/*" -not -path "*/bin/*" -not -path "*/.vs/*" -not -path "*/.git/*" -not -path "*/Generated/*" \( -name '*.cs' -o -name '*.csproj' -o -name '*.json' -o -name '*.xml' -o -name '*.props' -o -name '*.targets' \) -print0)
done

# Remove stale patch/source entries for files that no longer differ.
while IFS= read -r -d '' patch; do
  if [[ ! -s "$patch" ]]; then
    rm -f "$patch"
    stale_count=$((stale_count+1))
  fi
done < <(find "$patches_dir" -type f -name '*.patch' -print0 2>/dev/null)

echo "Wrote $patch_count patch(es), $source_count source file(s); cleared $stale_count stale entry(ies)."
