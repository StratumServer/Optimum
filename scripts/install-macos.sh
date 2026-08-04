#!/usr/bin/env bash
# Optimum installer for macOS (arm64/x64) - runtime patcher model.
#
# Installs Optimum INTO the Vintage Story directory. Does NOT modify any
# vanilla files - only adds Optimum.* files and a .optimum/ directory.
#
# Usage:
#   ./install-macos-v2.sh                       # interactive
#   ./install-macos-v2.sh --vs-dir /path/to/vs  # non-interactive
#   ./install-macos-v2.sh --uninstall           # remove Optimum

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

DIST_ARCH="osx-$(uname -m | sed 's/x86_64/x64/' | sed 's/aarch64/arm64/')"
LAUNCHER_DIR="${OPTIMUM_DIST:-$REPO_ROOT/dist/$DIST_ARCH}"
OPTIMUM_LIBS="$REPO_ROOT/bin/Release/net10.0"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
DIM='\033[2m'
RESET='\033[0m'

VS_DIR=""
UNINSTALL=0
INTERACTIVE=1

OPTIMUM_VERSION="$(cat "$REPO_ROOT/VERSION" 2>/dev/null || echo "dev")"

# Optimum-owned files - ONLY these are ever touched
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

die() { printf "${RED}Error:${RESET} %s\n" "$1" >&2; exit 1; }
log() { printf "${GREEN}▸${RESET} %s\n" "$1"; }
warn() { printf "${YELLOW}▸${RESET} %s\n" "$1"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --vs-dir)    VS_DIR="$2"; INTERACTIVE=0; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        --dist)      LAUNCHER_DIR="$2"; shift 2 ;;
        --help|-h)   echo "Usage: $0 [--vs-dir DIR] [--uninstall] [--dist DIR]"; exit 0 ;;
        *)           die "Unknown option: $1" ;;
    esac
done

# --- Detection: find all VS installations ---
find_all_vs_dirs() {
    local found=()
    local candidates=(
        "$HOME/Library/Application Support/vintagestory"
        "/Applications/Vintagestory.app/Contents/MacOS"
        "$HOME/Applications/Vintagestory.app/Contents/MacOS"
        "$HOME/vintagestory"
        "$HOME/Games/vintagestory"
    )

    for dir in "${candidates[@]}"; do
        if [[ -f "$dir/Vintagestory.dll" && -f "$dir/VintagestoryLib.dll" ]]; then
            local real
            real="$(realpath "$dir" 2>/dev/null || echo "$dir")"
            local dupe=0
            for existing in "${found[@]+"${found[@]}"}"; do
                [[ "$(realpath "$existing" 2>/dev/null || echo "$existing")" == "$real" ]] && { dupe=1; break; }
            done
            [[ "$dupe" -eq 0 ]] && found+=("$dir")
        fi
    done

    printf '%s\n' "${found[@]+"${found[@]}"}"
}

prompt_vs_choice() {
    local -a dirs=()
    while IFS= read -r line; do
        [[ -n "$line" ]] && dirs+=("$line")
    done < <(find_all_vs_dirs)

    if [[ ${#dirs[@]} -eq 0 ]]; then
        printf "  ${YELLOW}Could not auto-detect Vintage Story.${RESET}\n"
        printf "  Enter path to VS directory (where Vintagestory.dll lives): "
        read -r VS_DIR
        return
    fi

    if [[ ${#dirs[@]} -eq 1 ]]; then
        printf "  Vintage Story found at: ${CYAN}%s${RESET}\n" "${dirs[0]}"
        printf "  Use this directory? [Y/n]: "
        read -r ans
        if [[ "$ans" =~ ^[Nn] ]]; then
            printf "  Enter VS directory: "
            read -r VS_DIR
        else
            VS_DIR="${dirs[0]}"
        fi
        return
    fi

    # Multiple installations
    printf "  ${BOLD}Multiple Vintage Story installations found:${RESET}\n\n"
    for i in "${!dirs[@]}"; do
        local marker=""
        [[ -f "${dirs[$i]}/Optimum.dll" ]] && marker=" ${DIM}(Optimum installed)${RESET}"
        printf "    ${CYAN}%d)${RESET} %s%b\n" "$((i+1))" "${dirs[$i]}" "$marker"
    done
    printf "\n  Choose [1-%d] or enter a custom path: " "${#dirs[@]}"
    read -r choice

    if [[ "$choice" =~ ^[0-9]+$ && "$choice" -ge 1 && "$choice" -le "${#dirs[@]}" ]]; then
        VS_DIR="${dirs[$((choice-1))]}"
    else
        VS_DIR="$choice"
    fi
}

validate_vs_dir() {
    local dir="$1"
    [[ -d "$dir" ]] || die "Directory does not exist: $dir"
    [[ -f "$dir/Vintagestory.dll" ]] || die "Not a VS installation: $dir"
    [[ -f "$dir/VintagestoryLib.dll" ]] || die "Incomplete VS install: $dir"
}

# --- Uninstall: ONLY Optimum files ---
uninstall_optimum() {
    local dir="$1" removed=0
    for f in "${OPTIMUM_FILES[@]}"; do
        [[ -f "$dir/$f" ]] && { rm -f "$dir/$f"; removed=$((removed+1)); }
    done
    [[ -d "$dir/.optimum" ]] && { rm -rf "$dir/.optimum"; removed=$((removed+1)); }
    if [[ "$removed" -gt 0 ]]; then
        log "Removed $removed Optimum file(s)/directories."
    else
        warn "No Optimum files found."
    fi
}

# --- Install ---
install_optimum() {
    local vs_dir="$1"
    local install_dir="$2"

    # Validate build outputs.
    local patched_lib="$REPO_ROOT/build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll"
    local patched_api="$OPTIMUM_LIBS/VintagestoryAPI-patched.dll"
    local patched_ess="$OPTIMUM_LIBS/VSEssentials-patched.dll"
    local patched_surv="$OPTIMUM_LIBS/VSSurvivalMod-patched.dll"

    [[ -d "$LAUNCHER_DIR" ]] || die "Launcher dist not found: $LAUNCHER_DIR. Run 'make dist'."
    [[ -f "$LAUNCHER_DIR/Optimum.dll" ]] || die "Optimum.dll not found in $LAUNCHER_DIR."
    [[ -f "$patched_lib" ]] || die "Patched VintagestoryLib not found. Run 'make build' and the Patcher."
    [[ -f "$patched_api" ]] || die "Patched VintagestoryAPI not found. Run the Patcher."

    log "Installing Optimum v$OPTIMUM_VERSION to $install_dir"

    # Step 1: Copy the entire vanilla game to the install directory.
    mkdir -p "$install_dir"
    cp -a "$vs_dir/." "$install_dir/"
    rm -rf "$install_dir/.optimum/cache"

    # Step 2: Overlay patched DLLs.
    cp -f "$patched_lib" "$install_dir/VintagestoryLib.dll"
    cp -f "$patched_api" "$install_dir/VintagestoryAPI.dll"
    [[ -f "$patched_ess" ]]  && cp -f "$patched_ess"  "$install_dir/Mods/VSEssentials.dll"
    [[ -f "$patched_surv" ]] && cp -f "$patched_surv" "$install_dir/Mods/VSSurvivalMod.dll"

    # Step 3: Copy Optimum-only assemblies.
    for f in Optimum.Api.Contracts.dll Optimum.GameContent.dll; do
        [[ -f "$OPTIMUM_LIBS/$f" ]] && cp -f "$OPTIMUM_LIBS/$f" "$install_dir/$f"
    done

    # Step 4: Copy launcher.
    for f in Optimum Optimum.dll Optimum.deps.json Optimum.runtimeconfig.json \
             Mono.Cecil.dll Mono.Cecil.Mdb.dll Mono.Cecil.Pdb.dll Mono.Cecil.Rocks.dll; do
        [[ -f "$LAUNCHER_DIR/$f" ]] && cp -f "$LAUNCHER_DIR/$f" "$install_dir/$f"
    done
    chmod +x "$install_dir/Optimum" 2>/dev/null || true

    # Step 5: Remove vanilla launcher.
    rm -f "$install_dir/Vintagestory" 2>/dev/null

    # Step 6: Create .optimum config dir.
    mkdir -p "$install_dir/.optimum"
    [[ -f "$install_dir/.optimum/optimum.json" ]] || echo '{}' > "$install_dir/.optimum/optimum.json"
    echo "$OPTIMUM_VERSION" > "$install_dir/.optimum/version"

    cat > "$install_dir/run-optimum.sh" <<'LAUNCHER'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
exec ./Optimum "$@"
LAUNCHER
    chmod +x "$install_dir/run-optimum.sh"
    log "Done."
}

# ============================================================================
# Main
# ============================================================================

printf "\n${BOLD}  Optimum v${OPTIMUM_VERSION} - macOS Installer${RESET}\n\n"

if [[ -z "$VS_DIR" ]]; then
    if [[ "$INTERACTIVE" -eq 1 ]]; then
        prompt_vs_choice
    else
        VS_DIR="$(find_all_vs_dirs | head -1)"
        [[ -n "$VS_DIR" ]] || die "Could not detect VS. Use --vs-dir."
    fi
fi

[[ -n "$VS_DIR" ]] || die "No directory specified."
validate_vs_dir "$VS_DIR"

if [[ "$UNINSTALL" -eq 1 ]]; then
    uninstall_optimum "$VS_DIR"
    printf "\n${GREEN}  ✓ Optimum removed. Vintage Story is vanilla again.${RESET}\n\n"
    exit 0
fi

# Upgrade
if [[ -f "$VS_DIR/Optimum.dll" ]]; then
    existing_ver="$(cat "$VS_DIR/.optimum/version" 2>/dev/null || echo "")"
    [[ -n "$existing_ver" ]] && warn "Upgrading Optimum v$existing_ver → v$OPTIMUM_VERSION." || warn "Upgrading existing Optimum."
    for f in "${OPTIMUM_FILES[@]}"; do rm -f "$VS_DIR/$f"; done
    rm -rf "$VS_DIR/.optimum/cache"
    log "Old files removed."
fi

INSTALL_DIR="${INSTALL_DIR:-$(dirname "$VS_DIR")/Optimum}"
install_optimum "$VS_DIR" "$INSTALL_DIR"

printf "\n  ${GREEN}✓ Optimum v${OPTIMUM_VERSION} installed.${RESET}\n"
printf "  ${BOLD}Launch:${RESET} %s/run-optimum.sh\n" "$INSTALL_DIR"
printf "  ${BOLD}Uninstall:${RESET} rm -rf %s\n\n" "$INSTALL_DIR"
