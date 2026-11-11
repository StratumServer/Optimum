#!/usr/bin/env bash
# Builds the two clients the chunk flight benchmark compares: pristine vanilla extracted from the
# official archive, and this tree deployed over a copy of it. Neither touches .vanilla/ or an
# installed Optimum.
# Usage: bash scripts/prepare-flight-arms.sh ARMS_DIR
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
arms="${1:?usage: prepare-flight-arms.sh ARMS_DIR}"
archive="$(ls "$repo_root"/.vanilla/archives/vs_client_linux-x64_*.tar.gz 2>/dev/null | tail -n 1 || true)"
[[ -f "$archive" ]] || { echo "ERROR: no linux client archive under .vanilla/archives. Run scripts/bootstrap.sh first." >&2; exit 1; }

mkdir -p "$arms"
if [[ ! -f "$arms/vanilla/Vintagestory.dll" ]]; then
    rm -rf "$arms/vanilla"
    mkdir -p "$arms/vanilla"
    tar -xzf "$archive" -C "$arms/vanilla" --strip-components=1
fi

rm -rf "$arms/optimum"
cp -a "$arms/vanilla" "$arms/optimum"
cp "$arms/vanilla/VintagestoryLib.dll" "$arms/optimum/VintagestoryLib.vanilla.dll"
make -C "$repo_root" deploy VANILLA_DIR="$arms/optimum" INSTALL_DIR="$arms/.no-install"
# The same frame meter mod goes into both arms, so they are measured with one instrument.
rm -rf "$arms/mods/chunkflightmeter"
VINTAGE_STORY="$arms/vanilla" dotnet build "$repo_root/tools/ChunkFlightMeter/ChunkFlightMeter.csproj" -c Release -o "$arms/mods/chunkflightmeter" --nologo -v quiet

echo "Arms ready: $arms/vanilla and $arms/optimum, meter mod in $arms/mods"
