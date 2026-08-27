#!/usr/bin/env bash
# Optimum uninstaller.
#
# For a standalone install (one with .optimum/install-manifest.json) this
# delegates to `optimum uninstall`, which removes exactly what the manifest
# records: the install directory, the shortcuts, and the registry entry.
#
# For a legacy in-place overlay install (Optimum files dropped next to a vanilla
# Vintage Story) it removes the Optimum-owned files and leaves vanilla alone.
#
# Usage:
#   ./scripts/uninstall.sh [--install-dir DIR] [--yes]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[0;33m'; BOLD='\033[1m'; DIM='\033[2m'; RESET='\033[0m'

INSTALL_DIR=""
ASSUME_YES=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir|--vs-dir) INSTALL_DIR="$2"; shift 2 ;;
        --yes|-y)               ASSUME_YES=1; shift ;;
        --help|-h)              echo "Usage: $0 [--install-dir DIR] [--yes]"; exit 0 ;;
        *) echo "Unknown: $1" >&2; exit 1 ;;
    esac
done

# ---------------------------------------------------------------------------
# Locate the install
# ---------------------------------------------------------------------------
if [[ -z "$INSTALL_DIR" ]]; then
    for dir in \
        "$SCRIPT_DIR" \
        "${XDG_DATA_HOME:-$HOME/.local/share}/optimum" \
        "$HOME/.local/share/vintagestory" \
        "/opt/vintagestory" \
        "$HOME/Library/Application Support/vintagestory"; do
        if [[ -f "$dir/.optimum/install-manifest.json" || -f "$dir/Optimum.dll" ]]; then
            INSTALL_DIR="$dir"
            break
        fi
    done
fi

if [[ -z "$INSTALL_DIR" || ! -d "$INSTALL_DIR" ]]; then
    printf "${RED}Could not find an Optimum install. Pass --install-dir DIR.${RESET}\n" >&2
    exit 1
fi

printf "\n${BOLD}  Optimum uninstaller${RESET}\n  ${DIM}%s${RESET}\n\n" "$INSTALL_DIR"

if [[ "$ASSUME_YES" -ne 1 ]]; then
    printf "  Remove Optimum? Vanilla Vintage Story is not affected. [y/N] "
    read -r reply
    [[ "$reply" =~ ^[Yy] ]] || { echo "  Cancelled."; exit 0; }
fi

# ---------------------------------------------------------------------------
# Standalone install: delegate to the engine
# ---------------------------------------------------------------------------
if [[ -f "$INSTALL_DIR/.optimum/install-manifest.json" ]]; then
    dotnet build "$REPO_ROOT/Optimum.Cli/Optimum.Cli.csproj" -c Release --nologo >/dev/null
    exec dotnet run --project "$REPO_ROOT/Optimum.Cli/Optimum.Cli.csproj" -c Release --no-build -- \
        uninstall --install-dir "$INSTALL_DIR"
fi

# ---------------------------------------------------------------------------
# Legacy overlay install
# ---------------------------------------------------------------------------
[[ -f "$INSTALL_DIR/VintagestoryLib.dll" ]] || { printf "${RED}Not a Vintage Story directory: %s${RESET}\n" "$INSTALL_DIR" >&2; exit 1; }

OPTIMUM_FILES=(
    Optimum Optimum.dll Optimum.deps.json Optimum.runtimeconfig.json
    Optimum.Patcher.dll VintagestoryLib.Donor.dll
    Mono.Cecil.dll Mono.Cecil.Mdb.dll Mono.Cecil.Pdb.dll Mono.Cecil.Rocks.dll
    run-optimum.sh datapath.cfg
)

removed=0
failed=0
for f in "${OPTIMUM_FILES[@]}"; do
    [[ -e "$INSTALL_DIR/$f" ]] || continue
    if rm -f -- "$INSTALL_DIR/$f"; then removed=$((removed + 1)); else failed=1; fi
done
if [[ -d "$INSTALL_DIR/.optimum" ]]; then
    if rm -rf -- "$INSTALL_DIR/.optimum"; then removed=$((removed + 1)); else failed=1; fi
fi
rm -f -- "$HOME/.local/share/applications/optimum.desktop" "$HOME/Desktop/Optimum.desktop" 2>/dev/null || true

if [[ "$failed" -ne 0 ]]; then
    printf "${RED}Some Optimum files could not be removed. Close any running Optimum process and retry.${RESET}\n" >&2
    exit 1
fi

printf "\n  ${GREEN}Removed %d Optimum file(s). Vintage Story is vanilla again.${RESET}\n\n" "$removed"
