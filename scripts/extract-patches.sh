#!/usr/bin/env bash
set -euo pipefail

# Regenerates patches/ from the diff between build/snapshot/ and the working tree.
# Also syncs sources/ for files that have no baseline equivalent.

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

baseline_dir="$repo_root/.baseline"
patches_dir="$repo_root/patches"
sources_dir="$repo_root/sources"

if [[ ! -d "$baseline_dir" ]]; then
  echo "No build/snapshot/ found. Run scripts/bootstrap.sh first." >&2
  exit 1
fi

# Clear old patches and auto-generated sources (regenerated from scratch),
# but keep every hand-maintained file the regeneration below cannot rebuild.
# The regeneration writes two kinds of output: *.patch files under patches/,
# and files under sources/<project>/ where <project> has a .baseline/ dir and
# the file is new against that baseline. Everything else is hand-maintained
# and must survive the wipe: patches/cecil-owned.list, sources/lang/,
# sources/shaders/, icon overlays like sources/Vintagestory/app.ico, and the
# csproj/props/targets overlays, which bootstrap.sh folds into the baseline
# so they never show up as new files here.
preserved_dir="$(mktemp -d)"
if [[ -d "$patches_dir" ]]; then
  while IFS= read -r -d '' f; do
    rel="${f#$patches_dir/}"
    mkdir -p "$(dirname "$preserved_dir/patches/$rel")"
    cp -f "$f" "$preserved_dir/patches/$rel"
  done < <(find "$patches_dir" -type f -not -name '*.patch' -print0)
fi
if [[ -d "$sources_dir" ]]; then
  while IFS= read -r -d '' f; do
    rel="${f#$sources_dir/}"
    top="${rel%%/*}"
    case "$f" in
      *.cs|*.json|*.xml)
        # Regenerated below when the project has a baseline; skip those.
        if [[ -d "$baseline_dir/$top" ]]; then continue; fi ;;
    esac
    mkdir -p "$(dirname "$preserved_dir/sources/$rel")"
    cp -f "$f" "$preserved_dir/sources/$rel"
  done < <(find "$sources_dir" -type f -print0)
fi
rm -rf "$patches_dir" "$sources_dir"
mkdir -p "$patches_dir" "$sources_dir"
# Restore preserved files
if [[ -d "$preserved_dir/patches" ]]; then
  cp -a "$preserved_dir/patches/." "$patches_dir"/
fi
if [[ -d "$preserved_dir/sources" ]]; then
  cp -a "$preserved_dir/sources/." "$sources_dir"/
fi
rm -rf "$preserved_dir"

patch_count=0
source_count=0
stale_count=0

# Projects that live under build/ (decompiled closed-source).
vanilla_projects=("VintagestoryLib" "Vintagestory")

# All project directories in the working tree that have a baseline.
for base_project in "$baseline_dir"/*/; do
  project="$(basename "$base_project")"
  work_dir="$repo_root/$project"

  # For vanilla (decompiled) projects, the working copy is under build/.
  for vp in "${vanilla_projects[@]}"; do
    if [[ "$project" == "$vp" ]]; then
      work_dir="$repo_root/build/$project"
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
      # Normalize CRLF only. A leading UTF-8 BOM must survive into the patch
      # context: the working tree file still carries it at apply time (the
      # bootstrap pipeline never strips BOM), so stripping it here would
      # make line 1 fail to match on apply for any BOM-prefixed source.
      perl -pe 's/\r\n/\n/g; s/\r/\n/g' "$base_file" > "$tmp_base"
      perl -pe 's/\r\n/\n/g; s/\r/\n/g' "$file" > "$tmp_work"
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
