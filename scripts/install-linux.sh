#!/usr/bin/env bash
# Optimum interactive installer for Linux x64.
#
# Detects prerequisites, shows their status, offers to install missing items,
# lets the user choose an install directory, builds and deploys.
# Zero PowerShell dependency.
#
# Usage:
#   ./scripts/install-linux.sh                     # interactive
#   ./scripts/install-linux.sh --install-dir DIR   # non-interactive with defaults
#   ./scripts/install-linux.sh --help

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

XDG_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"

if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
fi

# Defaults
INSTALL_DIR=""
DATA_PATH=""
PACKAGE_DIR=""
SKIP_BUILD=0
VERSION=""
CREATE_MENU=1
CREATE_DESKTOP=0
INTERACTIVE=1

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
DIM='\033[2m'
RESET='\033[0m'

usage() {
    cat <<EOF
Usage: $(basename "$0") [options]

Interactive installer for Optimum on Linux. Checks prerequisites, offers to
install missing tools, and builds/deploys to a directory of your choice.

Options:
  --install-dir DIR       Install Optimum to DIR (default: ~/.local/share/optimum)
  --data-path DIR         Separate data folder (--dataPath at launch)
  --package-dir DIR       Install from an existing packaged folder (skip build)
  --skip-build            Package existing build outputs without bootstrap/build
  --version VERSION       Vintage Story version (default: from forks.json)
  --no-menu-entry         Do not create the application menu entry
  --desktop-shortcut      Create a Desktop shortcut
  --non-interactive       Skip prompts, use defaults for all choices
  --help                  Show this help
EOF
}

die() { printf "${RED}ERROR:${RESET} %s\n" "$*" >&2; exit 1; }
log() { printf "${CYAN}==> ${RESET}%s\n" "$*"; }
warn() { printf "${YELLOW}WARNING:${RESET} %s\n" "$*" >&2; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)       [[ $# -gt 1 ]] || die "$1 needs a value"; INSTALL_DIR="$2"; shift 2 ;;
        --data-path)         [[ $# -gt 1 ]] || die "$1 needs a value"; DATA_PATH="$2"; shift 2 ;;
        --package-dir)       [[ $# -gt 1 ]] || die "$1 needs a value"; PACKAGE_DIR="$2"; shift 2 ;;
        --skip-build)        SKIP_BUILD=1; shift ;;
        --version)           [[ $# -gt 1 ]] || die "$1 needs a value"; VERSION="$2"; shift 2 ;;
        --no-menu-entry)     CREATE_MENU=0; shift ;;
        --desktop-shortcut)  CREATE_DESKTOP=1; shift ;;
        --non-interactive)   INTERACTIVE=0; shift ;;
        --help|-h)           usage; exit 0 ;;
        *) die "Unknown option: $1" ;;
    esac
done

# ============================================================================
# Prerequisite detection
# ============================================================================

check_cmd() { command -v "$1" &>/dev/null; }

DOTNET_BIN=""  # set by check_dotnet10 on success
ILSPY_BIN=""   # set by check_ilspycmd on success

check_dotnet10() {
    DOTNET_BIN=""
    local candidates=()
    if command -v dotnet &>/dev/null; then
        candidates+=("$(command -v dotnet)")
    fi
    candidates+=(
        "$HOME/.dotnet/dotnet"
        "/usr/share/dotnet/dotnet"
        "/usr/lib/dotnet/dotnet"
        "/snap/dotnet-sdk/current/dotnet"
    )
    local candidate
    for candidate in "${candidates[@]}"; do
        [[ -x "$candidate" ]] || continue
        if "$candidate" --list-sdks 2>/dev/null | grep -q '^10\.'; then
            DOTNET_BIN="$candidate"
            return 0
        fi
    done
    return 1
}

activate_user_dotnet() {
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export DOTNET_ROOT="$HOME/.dotnet"
        export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
    else
        export PATH="$HOME/.dotnet/tools:$PATH"
    fi
    hash -r
}

pinned_ilspycmd_version() {
    local manifest="$REPO_ROOT/.config/dotnet-tools.json"
    local fallback="10.1.1.8388"
    [[ -f "$manifest" ]] || { printf '%s\n' "$fallback"; return; }
    local parsed
    parsed=$(grep -A 4 '"ilspycmd"' "$manifest" | grep -m 1 '"version"' | sed -E 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/' || true)
    printf '%s\n' "${parsed:-$fallback}"
}

ilspycmd_accepted_prefixes() {
    local manifest="$REPO_ROOT/.config/ilspycmd-compat.json"
    if [[ -f "$manifest" ]]; then
        grep -oE '"[0-9]+\.[0-9]+\.[0-9]+\."' "$manifest" | tr -d '"'
        return
    fi
    printf '%s\n' '10.1.0.' '10.1.1.'
}

ilspycmd_version_supported() {
    local current="$1" prefix
    while IFS= read -r prefix; do
        [[ -n "$prefix" && "$current" == "$prefix"* ]] && return 0
    done < <(ilspycmd_accepted_prefixes)
    return 1
}

find_ilspycmd() {
    ILSPY_BIN=""
    if command -v ilspycmd &>/dev/null; then
        ILSPY_BIN="$(command -v ilspycmd)"
    elif [[ -x "$HOME/.dotnet/tools/ilspycmd" ]]; then
        ILSPY_BIN="$HOME/.dotnet/tools/ilspycmd"
    fi
    [[ -n "$ILSPY_BIN" ]]
}

# Reads ilspycmd's full --version output before parsing it, and bounds the
# call with a timeout. Piping the tool's live stdout straight into `head -n1`
# can hang: `head` exits after one line and closes its end of the pipe, and
# a .NET global tool writing further output into a closed pipe can block
# instead of erroring out (SIGPIPE is ignored by the runtime by default).
ilspycmd_version() {
    local raw
    raw=$(timeout 5 "$ILSPY_BIN" --version 2>/dev/null || true)
    printf '%s\n' "$raw" | head -n 1 | awk '{print $2}'
}

check_ilspycmd() {
    find_ilspycmd || return 1
    local version
    version=$(ilspycmd_version)
    ilspycmd_version_supported "$version"
}

system_install_command() {
    local package="$1"
    if check_cmd apt-get; then
        printf 'sudo apt-get install -y %q' "$package"
    elif check_cmd dnf; then
        printf 'sudo dnf install -y %q' "$package"
    elif check_cmd pacman; then
        printf 'sudo pacman -S --needed --noconfirm %q' "$package"
    elif check_cmd zypper; then
        printf 'sudo zypper --non-interactive install %q' "$package"
    fi
}

install_dotnet10() {
    local installer
    installer=$(mktemp)
    if check_cmd curl; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    elif check_cmd wget; then
        wget -q https://dot.net/v1/dotnet-install.sh -O "$installer"
    else
        rm -f "$installer"
        die "Installing .NET 10 requires curl or wget."
    fi
    if ! bash "$installer" --channel 10.0 --install-dir "$HOME/.dotnet"; then
        rm -f "$installer"
        die "The .NET 10 installer failed."
    fi
    rm -f "$installer"
    activate_user_dotnet
    check_dotnet10 || die ".NET 10 installation finished without a usable 10.x SDK."
}

install_ilspycmd() {
    check_dotnet10 || die "ilspycmd requires the .NET 10 SDK."
    local pinned
    pinned=$(pinned_ilspycmd_version)
    # --allow-downgrade: `tool update --version` refuses to move to an older
    # or NuGet-considered-newer version otherwise ("This cannot be used to
    # downgrade versions, you must uninstall newer versions first" -
    # https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-update).
    # If update still fails, uninstall before falling back to a fresh
    # install: `tool install` always refuses when the tool ID already
    # exists, so retrying it without removing the old one first can never
    # succeed.
    if ! "$DOTNET_BIN" tool update -g ilspycmd --version "$pinned" --allow-downgrade; then
        "$DOTNET_BIN" tool uninstall -g ilspycmd || true
        "$DOTNET_BIN" tool install -g ilspycmd --version "$pinned"
    fi
    export PATH="$HOME/.dotnet/tools:$PATH"
    hash -r
    check_ilspycmd || die "ilspycmd $pinned installation failed."
}

get_required_vs_version() {
    local forks="$REPO_ROOT/forks.json"
    if [[ -f "$forks" ]]; then
        perl -ne 'if (/"vintageStoryVersion"\s*:\s*"([^"]+)"/) { print $1; exit }' "$forks" || echo "1.22.3"
    else
        echo "1.22.3"
    fi
}

# Returns: 0 = present, 1 = missing
declare -A PREREQ_STATUS
declare -A PREREQ_LABEL
declare -A PREREQ_INSTALL_CMD
declare -A PREREQ_INSTALL_URL

detect_prereqs() {
    # .NET 10 SDK
    if check_dotnet10; then
        PREREQ_STATUS[dotnet]="ok"
        local ver
        ver=$("$DOTNET_BIN" --version 2>/dev/null || echo "10.x")
        PREREQ_LABEL[dotnet]=".NET 10 SDK ($ver)"
        if [[ "$DOTNET_BIN" != "dotnet" ]]; then
            PREREQ_LABEL[dotnet]="${PREREQ_LABEL[dotnet]} [found at $DOTNET_BIN, not on PATH]"
        fi
    else
        PREREQ_STATUS[dotnet]="missing"
        PREREQ_LABEL[dotnet]=".NET 10 SDK"
        PREREQ_INSTALL_CMD[dotnet]="curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet"
        PREREQ_INSTALL_URL[dotnet]="https://dotnet.microsoft.com/download/dotnet/10.0"
    fi

    # Git
    if check_cmd git; then
        PREREQ_STATUS[git]="ok"
        PREREQ_LABEL[git]="git ($(git --version 2>/dev/null | cut -d' ' -f3))"
    else
        PREREQ_STATUS[git]="missing"
        PREREQ_LABEL[git]="git"
        PREREQ_INSTALL_CMD[git]="$(system_install_command git)"
    fi

    # curl
    if check_cmd curl; then
        PREREQ_STATUS[curl]="ok"
        PREREQ_LABEL[curl]="curl"
    else
        PREREQ_STATUS[curl]="missing"
        PREREQ_LABEL[curl]="curl"
        PREREQ_INSTALL_CMD[curl]="$(system_install_command curl)"
    fi

    # python3
    if check_cmd python3; then
        PREREQ_STATUS[python3]="ok"
        PREREQ_LABEL[python3]="python3"
    else
        PREREQ_STATUS[python3]="missing"
        PREREQ_LABEL[python3]="python3"
        PREREQ_INSTALL_CMD[python3]="$(system_install_command python3)"
    fi

    # perl
    if check_cmd perl; then
        PREREQ_STATUS[perl]="ok"
        PREREQ_LABEL[perl]="perl"
    else
        PREREQ_STATUS[perl]="missing"
        PREREQ_LABEL[perl]="perl"
        PREREQ_INSTALL_CMD[perl]="$(system_install_command perl)"
    fi

    # bash (always present if running this script)
    PREREQ_STATUS[bash]="ok"
    PREREQ_LABEL[bash]="bash"

    # tar
    if check_cmd tar; then
        PREREQ_STATUS[tar]="ok"
        PREREQ_LABEL[tar]="tar"
    else
        PREREQ_STATUS[tar]="missing"
        PREREQ_LABEL[tar]="tar"
        PREREQ_INSTALL_CMD[tar]="$(system_install_command tar)"
    fi

    # ilspycmd
    local current
    if check_ilspycmd; then
        PREREQ_STATUS[ilspycmd]="ok"
        current=$(ilspycmd_version)
        PREREQ_LABEL[ilspycmd]="ilspycmd ($current)"
    else
        PREREQ_STATUS[ilspycmd]="missing"
        local ilspy_ver
        ilspy_ver=$(pinned_ilspycmd_version)
        if find_ilspycmd; then
            current=$(ilspycmd_version)
            PREREQ_LABEL[ilspycmd]="ilspycmd $current (unsupported, needs $ilspy_ver)"
        else
            PREREQ_LABEL[ilspycmd]="ilspycmd $ilspy_ver (decompiler)"
        fi
        PREREQ_INSTALL_CMD[ilspycmd]="${DOTNET_BIN:-$HOME/.dotnet/dotnet} tool update -g ilspycmd --version $ilspy_ver"
    fi
}

print_prereqs() {
    local order=(git curl python3 perl tar dotnet ilspycmd)
    printf "\n${BOLD}  PREREQUISITES${RESET}\n\n"
    for key in "${order[@]}"; do
        local status="${PREREQ_STATUS[$key]}"
        local label="${PREREQ_LABEL[$key]}"
        if [[ "$status" == "ok" ]]; then
            printf "    ${GREEN}✓${RESET}  %s\n" "$label"
        else
            printf "    ${RED}✗${RESET}  %s\n" "$label"
        fi
    done
    echo ""
}

get_missing_prereqs() {
    local missing=()
    local order=(git curl python3 perl tar dotnet ilspycmd)
    for key in "${order[@]}"; do
        if [[ "${PREREQ_STATUS[$key]}" == "missing" ]]; then
            missing+=("$key")
        fi
    done
    echo "${missing[@]}"
}

offer_install_missing() {
    local missing
    read -ra missing <<< "$(get_missing_prereqs)"
    if [[ ${#missing[@]} -eq 0 ]]; then return 0; fi

    printf "  ${YELLOW}Missing tools detected.${RESET} Install them now?\n\n"

    for key in "${missing[@]}"; do
        local cmd="${PREREQ_INSTALL_CMD[$key]:-}"
        local url="${PREREQ_INSTALL_URL[$key]:-}"
        local label="${PREREQ_LABEL[$key]}"

        if [[ -n "$cmd" ]]; then
            printf "    ${BOLD}%s${RESET}\n" "$label"
            printf "    ${DIM}%s${RESET}\n\n" "$cmd"
        elif [[ -n "$url" ]]; then
            printf "    ${BOLD}%s${RESET}\n" "$label"
            printf "    ${DIM}Download: %s${RESET}\n\n" "$url"
        fi
    done

    if [[ "$INTERACTIVE" -eq 0 ]]; then
        die "Missing prerequisites: ${missing[*]}. Install them and retry."
    fi

    printf "  Install missing tools? [Y/n] "
    local reply
    read -r reply
    reply="${reply:-Y}"

    if [[ "$reply" =~ ^[Yy] ]]; then
        for key in "${missing[@]}"; do
            local cmd="${PREREQ_INSTALL_CMD[$key]:-}"
            if [[ -z "$cmd" ]]; then
                local url="${PREREQ_INSTALL_URL[$key]:-}"
                printf "    ${YELLOW}%s:${RESET} install from %s and re-run this script.\n" "${PREREQ_LABEL[$key]}" "$url"
                continue
            fi

            printf "    ${CYAN}Installing %s...${RESET}\n" "${PREREQ_LABEL[$key]}"
            local installed=0
            case "$key" in
                dotnet) install_dotnet10 && installed=1 ;;
                ilspycmd) install_ilspycmd && installed=1 ;;
                *) eval "$cmd" && installed=1 ;;
            esac
            if [[ "$installed" -eq 1 ]]; then
                printf "    ${GREEN}✓${RESET} %s installed\n" "${PREREQ_LABEL[$key]}"
            else
                printf "    ${RED}✗${RESET} Failed to install %s\n" "${PREREQ_LABEL[$key]}"
                die "Could not install ${PREREQ_LABEL[$key]}. Install it and retry."
            fi
        done

        activate_user_dotnet
        detect_prereqs
        local still_missing
        read -ra still_missing <<< "$(get_missing_prereqs)"
        if [[ ${#still_missing[@]} -gt 0 && "${still_missing[0]}" != "" ]]; then
            die "Still missing: ${still_missing[*]}. Install them and retry."
        fi
        printf "\n    ${GREEN}All prerequisites satisfied.${RESET}\n\n"
    else
        die "Cannot proceed without: ${missing[*]}"
    fi
}

# ============================================================================
# Install directory selection
# ============================================================================

prompt_install_dir() {
    local default="$XDG_DATA_HOME/optimum"

    if [[ -n "$INSTALL_DIR" ]]; then return; fi

    if [[ "$INTERACTIVE" -eq 0 ]]; then
        INSTALL_DIR="$default"
        return
    fi

    printf "  ${BOLD}INSTALL OPTIONS${RESET}\n\n"
    printf "    Install directory [${DIM}%s${RESET}]: " "$default"
    local reply
    read -r reply
    INSTALL_DIR="${reply:-$default}"

    # Expand ~ if present
    INSTALL_DIR="${INSTALL_DIR/#\~/$HOME}"
}

prompt_data_path() {
    if [[ -n "$DATA_PATH" ]]; then return; fi
    if [[ "$INTERACTIVE" -eq 0 ]]; then return; fi

    printf "    Separate data folder? (leave blank for default) [${DIM}~/.config/OptimumVintagestoryData${RESET}]: "
    local reply
    read -r reply
    if [[ -n "$reply" ]]; then
        DATA_PATH="${reply/#\~/$HOME}"
    fi
}

prompt_shortcuts() {
    if [[ "$INTERACTIVE" -eq 0 ]]; then return; fi

    printf "    Create application menu entry? [Y/n] "
    local reply
    read -r reply
    reply="${reply:-Y}"
    if [[ "$reply" =~ ^[Nn] ]]; then CREATE_MENU=0; fi

    printf "    Create desktop shortcut? [y/N] "
    read -r reply
    reply="${reply:-N}"
    if [[ "$reply" =~ ^[Yy] ]]; then CREATE_DESKTOP=1; fi
}

# ============================================================================
# Build and install
# ============================================================================

abs_path() {
    local path="$1"
    if [[ "$path" == /* ]]; then echo "$path"
    else echo "$PWD/${path#./}"
    fi
}

guard_install_dir() {
    local dir="$1"
    case "$dir" in
        ""|"/"|"$HOME"|"$XDG_DATA_HOME"|"$HOME/.local")
            die "Refusing unsafe install dir: $dir" ;;
    esac

    # Block installing into the Vintage Story directory.
    local vs_paths=(
        "$HOME/.local/share/vintagestory"
        "$HOME/ApplicationData/vintagestory"
        "/opt/vintagestory"
    )
    # Also detect any path containing an existing Vintagestory executable.
    local dir_real
    dir_real="$(realpath -m "$dir")"
    for vsp in "${vs_paths[@]}"; do
        local vsp_real
        vsp_real="$(realpath -m "$vsp")"
        if [[ "$dir_real" == "$vsp_real" || "$dir_real" == "$vsp_real"/* ]]; then
            die "Install dir cannot be inside your Vintage Story directory ($vsp). Optimum must install to a separate location."
        fi
    done
    # Check if the target dir itself contains a vanilla Vintagestory executable (without Optimum branding).
    if [[ -f "$dir/Vintagestory" && ! -f "$dir/Optimum" ]]; then
        die "Install dir ($dir) contains a vanilla Vintage Story installation. Optimum must install to a separate location."
    fi
}

# Sets BUILT_DIR to the staged package path. Runs in the foreground so the
# user sees build output; do not call via command substitution.
build_and_package() {
    cd "$REPO_ROOT"

    if [[ "$SKIP_BUILD" -eq 0 ]]; then
        log "Building Optimum..."
        local make_args=()
        if [[ -n "$VERSION" ]]; then make_args+=(VERSION="$VERSION"); fi
        make "${make_args[@]}" refresh
        make "${make_args[@]}" build
    fi

    log "Packaging Linux build..."
    local stage_root
    stage_root="$(mktemp -d)"
    local pkg_args=(--output "$stage_root")
    if [[ -n "$VERSION" ]]; then pkg_args+=(--version "$VERSION"); fi

    bash "$SCRIPT_DIR/package-linux.sh" "${pkg_args[@]}"

    local built_dir
    built_dir="$(find "$stage_root" -maxdepth 1 -type d -name 'Optimum-v*-linux-x64' | sort | tail -n 1)"
    if [[ -z "$built_dir" ]]; then
        die "Linux package folder not found under $stage_root"
    fi
    BUILT_DIR="$built_dir"
}

install_from_dir() {
    local source_dir="$1"
    [[ -d "$source_dir" ]] || die "Package dir not found: $source_dir"
    [[ -f "$source_dir/run.sh" ]] || die "Package dir lacks run.sh: $source_dir"

    guard_install_dir "$INSTALL_DIR"

    local source_real target_real
    source_real="$(realpath -m "$source_dir")"
    target_real="$(realpath -m "$INSTALL_DIR")"
    [[ "$source_real" != "$target_real" ]] || die "Package dir and install dir must differ"

    log "Installing Optimum to $INSTALL_DIR"
    rm -rf "$INSTALL_DIR"
    mkdir -p "$INSTALL_DIR"
    cp -a "$source_dir"/. "$INSTALL_DIR"/

    if [[ -f "$INSTALL_DIR/Optimum" ]]; then
        chmod +x "$INSTALL_DIR/Optimum"
    fi
    chmod +x "$INSTALL_DIR/run.sh"
}

write_launcher() {
    local launcher="$INSTALL_DIR/optimum-launch.sh"
    if [[ -n "$DATA_PATH" ]]; then
        mkdir -p "$DATA_PATH"
        # Write datapath.cfg so the .NET launcher reads it on direct exe launch.
        printf '%s' "$DATA_PATH" > "$INSTALL_DIR/datapath.cfg"
        cat > "$launcher" <<EOF
#!/usr/bin/env bash
set -euo pipefail
cd "\$(dirname "\${BASH_SOURCE[0]}")"
exec ./run.sh --dataPath $(printf '%q' "$DATA_PATH") "\$@"
EOF
    else
        rm -f "$INSTALL_DIR/datapath.cfg"
        cat > "$launcher" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
exec ./run.sh "$@"
EOF
    fi
    chmod +x "$launcher"
}

write_desktop_entry() {
    local target="$1"
    local launcher="$INSTALL_DIR/optimum-launch.sh"
    mkdir -p "$(dirname "$target")"
    cat > "$target" <<EOF
[Desktop Entry]
Type=Application
Name=Optimum
Comment=High-performance client for Vintage Story
Exec="$launcher"
Path=$INSTALL_DIR
Icon=optimum
Terminal=false
Categories=Game;
StartupWMClass=Optimum
EOF
    chmod +x "$target"
}

refresh_desktop_databases() {
    local apps_dir="$XDG_DATA_HOME/applications"
    local icon_root="$XDG_DATA_HOME/icons/hicolor"
    if check_cmd update-desktop-database; then
        update-desktop-database "$apps_dir" >/dev/null 2>&1 || true
    fi
    if check_cmd gtk-update-icon-cache && [[ -d "$icon_root" ]]; then
        gtk-update-icon-cache -q "$icon_root" >/dev/null 2>&1 || true
    fi
}

# ============================================================================
# Main flow
# ============================================================================

print_header() {
    printf "\n"
    printf "  ${BOLD}Optimum Installer${RESET}\n"
    printf "  ${DIM}High-performance client for Vintage Story${RESET}\n"
    printf "  ${DIM}────────────────────────────────────────────${RESET}\n\n"
}

confirm_install() {
    if [[ "$INTERACTIVE" -eq 0 ]]; then return; fi

    printf "\n  ${BOLD}SUMMARY${RESET}\n\n"
    printf "    Install to:    %s\n" "$INSTALL_DIR"
    if [[ -n "$DATA_PATH" ]]; then
        printf "    Data folder:   %s\n" "$DATA_PATH"
    fi
    printf "    Menu entry:    %s\n" "$([ "$CREATE_MENU" -eq 1 ] && echo 'yes' || echo 'no')"
    printf "    Desktop icon:  %s\n" "$([ "$CREATE_DESKTOP" -eq 1 ] && echo 'yes' || echo 'no')"
    printf "\n    Proceed? [Y/n] "
    local reply
    read -r reply
    reply="${reply:-Y}"
    if [[ ! "$reply" =~ ^[Yy] ]]; then
        echo "Cancelled."
        exit 0
    fi
    echo ""
}

main() {
    print_header

    # Step 1: Detect and display prerequisites
    detect_prereqs

    if [[ -z "$PACKAGE_DIR" ]]; then
        print_prereqs
        offer_install_missing
    fi

    # Step 2: Choose install directory and options
    prompt_install_dir
    prompt_data_path
    prompt_shortcuts

    # Resolve to absolute paths
    INSTALL_DIR="$(abs_path "$INSTALL_DIR")"
    if [[ -n "$DATA_PATH" ]]; then DATA_PATH="$(abs_path "$DATA_PATH")"; fi
    if [[ -n "$PACKAGE_DIR" ]]; then PACKAGE_DIR="$(abs_path "$PACKAGE_DIR")"; fi

    # Step 3: Confirm
    confirm_install

    # Step 4: Build or use existing package
    local source_dir="$PACKAGE_DIR"
    local temp_source=""

    if [[ -z "$source_dir" ]]; then
        BUILT_DIR=""
        build_and_package
        temp_source="$BUILT_DIR"
        source_dir="$temp_source"
    fi

    # Step 5: Install
    install_from_dir "$source_dir"
    write_launcher

    if [[ -n "$temp_source" ]]; then
        rm -rf "$(dirname "$temp_source")"
    fi

    # Step 6: Icon
    local icon_dir="$XDG_DATA_HOME/icons/hicolor/256x256/apps"
    mkdir -p "$icon_dir"
    if [[ -f "$INSTALL_DIR/assets/gameicon.png" ]]; then
        cp -f "$INSTALL_DIR/assets/gameicon.png" "$icon_dir/optimum.png"
    elif [[ -f "$REPO_ROOT/logo.png" ]]; then
        cp -f "$REPO_ROOT/logo.png" "$icon_dir/optimum.png"
    fi

    # Step 7: Shortcuts
    if [[ "$CREATE_MENU" -eq 1 ]]; then
        write_desktop_entry "$XDG_DATA_HOME/applications/optimum.desktop"
    fi

    if [[ "$CREATE_DESKTOP" -eq 1 ]]; then
        local desktop_dir
        if check_cmd xdg-user-dir; then
            desktop_dir="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
        else
            desktop_dir="$HOME/Desktop"
        fi
        [[ "$desktop_dir" == "$HOME" ]] && desktop_dir="$HOME/Desktop"
        write_desktop_entry "$desktop_dir/Optimum.desktop"
    fi

    refresh_desktop_databases

    # Done
    printf "\n"
    log "Optimum installed"
    printf "    ${BOLD}App:${RESET}     %s\n" "$INSTALL_DIR"
    if [[ "$CREATE_MENU" -eq 1 ]]; then
        printf "    ${BOLD}Menu:${RESET}    %s\n" "$XDG_DATA_HOME/applications/optimum.desktop"
    fi
    if [[ "$CREATE_DESKTOP" -eq 1 ]]; then
        printf "    ${BOLD}Desktop:${RESET} %s\n" "$desktop_dir/Optimum.desktop"
    fi
    printf "    ${BOLD}Run:${RESET}     %s/optimum-launch.sh\n" "$INSTALL_DIR"
    printf "\n"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main
fi
