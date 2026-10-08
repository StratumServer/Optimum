#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
reference="${1:-${CAIRO_REFERENCE_PATH:-}}"
candidate="${2:-$repo_root/bin/Release/net10.0/cairo-sharp.dll}"
output="${3:-${TMPDIR:-/tmp}/optimum-cairo-abi-fixture}"

if [[ -z "$reference" || ! -f "$reference" ]]; then
  echo "Usage: $0 <pre-existing-cairo-sharp.dll> [candidate-cairo-sharp.dll] [output-directory]" >&2
  echo "Set CAIRO_REFERENCE_PATH or pass the installed game's Lib/cairo-sharp.dll." >&2
  exit 2
fi
if [[ ! -f "$candidate" ]]; then
  echo "Candidate Cairo assembly not found: $candidate" >&2
  exit 2
fi

mkdir -p "$output"
dotnet build "$repo_root/tools/Cairo.AbiFixture/Cairo.AbiFixture.csproj" \
  -c Release -p:CairoReferencePath="$reference" -o "$output"

reference_dir="$(dirname -- "$reference")"
cp -n "$reference_dir"/*.dll "$output"/ 2>/dev/null || true
cp -f "$reference" "$output/cairo-sharp.baseline.dll"
cp -f "$candidate" "$output/cairo-sharp.dll"
dotnet "$output/Cairo.AbiFixture.dll" "$output/legacy-mod-fixture.png"
