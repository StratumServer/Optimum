#!/usr/bin/env bash
# Build Optimum for Linux x64 in one step.
# Produces: Optimum-v<VERSION>-linux-x64/ (ready to run)
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
exec bash ./scripts/package-linux.sh
