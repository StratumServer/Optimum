#!/usr/bin/env bash
# Build Optimum for macOS in one step.
# Produces: Optimum.app (ready to drag to Applications)
# Requirements: .NET 10 SDK, bash, git, curl, python3, perl
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

echo "Running bootstrap (downloads ~600MB on first run)..."
make bootstrap

echo "Building..."
make build

ARCH=$(uname -m)
case "$ARCH" in
    arm64|aarch64) TARGET="arm64" ;;
    *) TARGET="x64" ;;
esac

echo "Packaging macOS $TARGET..."
if command -v pwsh >/dev/null; then
    pwsh ./scripts/package-macos.ps1 -Arch "$TARGET"
else
    echo "pwsh not found. Install PowerShell to package: https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-macos"
    echo "Or run: make deploy && copy the DLLs into your Vintage Story.app manually."
    exit 1
fi

echo "Done: Optimum.app"
echo "Drag Optimum.app to /Applications to install."
