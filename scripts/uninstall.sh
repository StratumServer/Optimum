#!/usr/bin/env bash
# Optimum uninstaller - removes ONLY Optimum-owned files from the VS directory.
# Vanilla Vintage Story files are NEVER touched.
#
# Usage:
#   ./uninstall.sh                   # auto-detect and remove
#   ./uninstall.sh --vs-dir /path    # specify directory
#
# This script can also be placed inside the VS directory itself and invoked
# directly: it will detect it's running from within the install.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BOLD='\033[1m'
DIM='\033[2m'
RESET='\033[0m'

VS_DIR=""

# Parse args
while [[ $# -gt 0 ]]; do
    case "$1" in
        --vs-dir) VS_DIR="$2"; shift 2 ;;
        --help|-h) echo "Usage: $0 [--vs-dir DIR]"; exit 0 ;;
        *) echo "Unknown: $1" >&2; exit 1 ;;
    esac
done

# Exhaustive list of Optimum-owned files
OPTIMUM_FILES=(
    "Optimum"
    "Optimum.dll"
    "Optimum.deps.json"
    "Optimum.runtimeconfig.json"
    "Optimum.Patcher.dll"
    "VintagestoryLib.Donor.dll"
    "Mono.Cecil.dll"
    "Mono.Cecil.Mdb.dll"
    "Mono.Cecil.Pdb.dll"
    "Mono.Cecil.Rocks.dll"
    "run-optimum.sh"
)

# Auto-detect: if Optimum.dll is in the same dir as this script
if [[ -z "$VS_DIR" ]]; then
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    if [[ -f "$script_dir/Optimum.dll" && -f "$script_dir/VintagestoryLib.dll" ]]; then
        VS_DIR="$script_dir"
    fi
fi

# Auto-detect: common paths
if [[ -z "$VS_DIR" ]]; then
    for dir in "$HOME/.local/share/vintagestory" "/opt/vintagestory" "$HOME/Library/Application Support/vintagestory"; do
        if [[ -f "$dir/Optimum.dll" ]]; then
            VS_DIR="$dir"
            break
        fi
    done
fi

if [[ -z "$VS_DIR" ]]; then
    printf "${RED}Could not find Optimum installation. Use --vs-dir.${RESET}\n"
    exit 1
fi

if [[ ! -f "$VS_DIR/VintagestoryLib.dll" ]]; then
    printf "${RED}Not a VS directory: %s${RESET}\n" "$VS_DIR"
    exit 1
fi

if [[ ! -f "$VS_DIR/Optimum.dll" ]]; then
    printf "${YELLOW}Optimum not installed in: %s${RESET}\n" "$VS_DIR"
    exit 0
fi

printf "\n${BOLD}  Optimum Uninstaller${RESET}\n\n"
printf "  Directory: ${DIM}%s${RESET}\n\n" "$VS_DIR"

# Confirm
printf "  Remove Optimum? Vanilla VS will NOT be affected. [y/N]: "
read -r confirm
if [[ ! "$confirm" =~ ^[Yy] ]]; then
    printf "  Cancelled.\n"
    exit 0
fi

# Remove ONLY Optimum files
removed=0
cleanup_failed=0
remove_file() {
    local path="$1"
    if [[ ! -e "$path" ]]; then
        return 0
    fi
    if rm -f -- "$path"; then
        removed=$((removed+1))
    else
        printf "${RED}Could not remove: %s${RESET}\n" "$path" >&2
        cleanup_failed=1
    fi
}

for f in "${OPTIMUM_FILES[@]}"; do
    remove_file "$VS_DIR/$f"
done

# Remove .optimum/ directory
if [[ -d "$VS_DIR/.optimum" ]]; then
    if rm -rf -- "$VS_DIR/.optimum"; then
        removed=$((removed+1))
    else
        printf "${RED}Could not remove: %s${RESET}\n" "$VS_DIR/.optimum" >&2
        cleanup_failed=1
    fi
fi

# Remove desktop entries
remove_file "$HOME/.local/share/applications/optimum.desktop"
remove_file "$HOME/Desktop/Optimum.desktop"

if [[ "$cleanup_failed" -ne 0 ]]; then
    printf "${RED}Optimum cleanup did not finish. Close running Optimum processes and retry.${RESET}\n" >&2
    exit 1
fi

printf "\n  ${GREEN}✓ Removed %d Optimum file(s). Vintage Story is vanilla.${RESET}\n\n" "$removed"
