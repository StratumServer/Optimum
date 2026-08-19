#!/usr/bin/env bash
set -euo pipefail

shader_dir="${1:-sources/shaders}"

if [[ ! -d "$shader_dir" ]]; then
    echo "Error: shader directory not found: $shader_dir" >&2
    exit 1
fi

bad=0
while IFS= read -r -d '' file; do
    if [[ ! -s "$file" ]]; then
        echo "Error: shader is empty: $file" >&2
        bad=1
        continue
    fi

    if ! perl -0777 -ne 'exit(/void\s+main/ ? 0 : 1)' "$file"; then
        echo "Error: shader has no void main: $file" >&2
        bad=1
    fi
done < <(find "$shader_dir" -maxdepth 1 -type f \( -name '*.vsh' -o -name '*.fsh' -o -name '*.gsh' \) -print0)

exit "$bad"
