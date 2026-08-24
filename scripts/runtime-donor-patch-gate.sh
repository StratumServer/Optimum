#!/usr/bin/env bash
set -eu
if (set -o pipefail 2>/dev/null); then
    set -o pipefail
fi

if [[ "$#" -ne 2 ]]; then
    echo "Usage: $0 <repository-root> <runtime-root-relative-to-repository>" >&2
    exit 2
fi

repo_root="$1"
runtime_root="$2"
patch_root="$repo_root/patches/runtime"

if echo | sort -z 2>/dev/null; then
    sort_z() { sort -z; }
else
    sort_z() { sort; }
fi

runtime_projects=(VSEssentials VSSurvivalMod)
runtime_patch_failures=()
for project in "${runtime_projects[@]}"; do
    while IFS= read -r -d '' patch; do
        if ! patch_error="$(git -C "$repo_root" apply --check \
            --directory="$runtime_root" \
            --whitespace=nowarn \
            "$patch" 2>&1)"; then
            printf '%s\n' "$patch_error" >&2
            runtime_patch_failures+=("$project requires a patch refresh for $(basename "$patch")")
        fi
    done < <(find "$patch_root/$project" -name '*.patch' -print0 | sort_z)
done

if [[ "${#runtime_patch_failures[@]}" != "0" ]]; then
    echo "Runtime donor compatibility gate failed:" >&2
    printf '  %s\n' "${runtime_patch_failures[@]}" >&2
    exit 1
fi

for project in "${runtime_projects[@]}"; do
    while IFS= read -r -d '' patch; do
        git -C "$repo_root" apply \
            --directory="$runtime_root" \
            --whitespace=nowarn \
            "$patch"
    done < <(find "$patch_root/$project" -name '*.patch' -print0 | sort_z)
done

printf 'Runtime donor patches applied for:'
printf ' %s' "${runtime_projects[@]}"
printf '\n'
