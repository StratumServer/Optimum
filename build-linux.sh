#!/usr/bin/env bash
# Build Optimum for Linux x64 in one step.
# Produces: Optimum-v0.1.0-linux-x64/ (ready to run)
# Requirements: .NET 10 SDK, bash, python3, git, curl, perl
set -euo pipefail
cd "$(dirname "$0")"

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

# Fallback if pwsh is not installed: manual package
echo "pwsh not found, packaging manually..."
make deploy
STAGE="Optimum-v0.1.0-linux-x64"
rm -rf "$STAGE"
cp -r .vanilla/vintagestory "$STAGE"
cp sources/shaders/*.fsh sources/shaders/*.vsh "$STAGE/assets/game/shaders/"
EXE="$STAGE/Vintagestory"
[ -f "$EXE" ] && mv "$EXE" "$STAGE/Optimum" && chmod +x "$STAGE/Optimum"
sed -i 's|./Vintagestory |./Optimum |' "$STAGE/run.sh" 2>/dev/null || true
echo "Done: $STAGE/"
echo "Run with: cd $STAGE && ./Optimum"
