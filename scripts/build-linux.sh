#!/usr/bin/env bash
# Build Optimum for Linux x64 in one step.
# Produces: Optimum-v0.2.5-linux-x64/ (ready to run)
# Requirements: .NET 10 SDK, bash, python3, git, curl, perl
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
cd "$repo_root"

echo "Checking prerequisites..."
for cmd in dotnet git curl python3 perl; do
    command -v $cmd >/dev/null || { echo "Missing: $cmd"; exit 1; }
done

SDK=$(dotnet --list-sdks 2>/dev/null | grep -c "^10\." || true)
[ "$SDK" -ge 1 ] || { echo ".NET 10 SDK not found. Install from https://dotnet.microsoft.com/download"; exit 1; }

echo "Running bootstrap (downloads ~570MB on first run)..."
make bootstrap

echo "Building..."
make build

echo "Packaging Linux x64..."
pwsh ./scripts/package-linux.ps1 2>/dev/null && echo "Done." && exit 0

# Fallback if pwsh is not installed.
echo "pwsh not found, using fallback package path..."
make deploy
STAGE="Optimum-v0.2.5-linux-x64"
PATCHED_LIB="build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll"
dotnet run --project Optimum.Patcher -c Release -- \
    .vanilla/linux-x64/vintagestory/VintagestoryLib.vanilla.dll \
    build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll \
    "$PATCHED_LIB"
rm -rf "$STAGE"
cp -r .vanilla/linux-x64/vintagestory "$STAGE"
cp build/Vintagestory/bin/Release/net10.0/Vintagestory.dll "$STAGE/"
cp "$PATCHED_LIB" "$STAGE/VintagestoryLib.dll"
cp bin/Release/net10.0/VintagestoryAPI.dll "$STAGE/"
cp bin/Release/net10.0/VSEssentials.dll "$STAGE/Mods/"
cp bin/Release/net10.0/VSSurvivalMod.dll "$STAGE/Mods/"
cp bin/Release/net10.0/VSCreativeMod.dll "$STAGE/Mods/"
cp bin/Release/net10.0/cairo-sharp.dll "$STAGE/Lib/"
cp sources/shaders/*.fsh sources/shaders/*.vsh "$STAGE/assets/game/shaders/"
EXE="$STAGE/Vintagestory"
[ -f "$EXE" ] && mv "$EXE" "$STAGE/Optimum" && chmod +x "$STAGE/Optimum"
perl -pi -e 's|\./Vintagestory |./Optimum |' "$STAGE/run.sh" 2>/dev/null || true
echo "Done: $STAGE/"
echo "Run with: cd $STAGE && ./Optimum"
