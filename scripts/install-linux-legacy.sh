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
XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
MOD_INSTALL_MIME="x-scheme-handler/vintagestorymodinstall"

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
SET_DEFAULT_URI_HANDLER=0
URI_HANDLER_FLAG_SET=0
INTERACTIVE=1
PREVIOUS_URI_HANDLER=""
PREVIOUS_REGISTERED_URI=0

# Scratch directory for the staged package, removed on exit (see the run guard
# at the end of the file, which installs the trap only for direct execution).
STAGE_ROOT=""
cleanup_stage() {
    if [[ -n "$STAGE_ROOT" && -d "$STAGE_ROOT" ]]; then
        rm -rf "$STAGE_ROOT"
    fi
    return 0
}

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
  --set-default-uri-handler
                          Set Optimum as default handler for ModDB 1-click links
  --no-default-uri-handler
                          Keep the existing default handler for ModDB 1-click links
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
        --set-default-uri-handler)
                             SET_DEFAULT_URI_HANDLER=1; URI_HANDLER_FLAG_SET=1; shift ;;
        --no-default-uri-handler)
                             SET_DEFAULT_URI_HANDLER=0; URI_HANDLER_FLAG_SET=1; shift ;;
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
    local fallback="11.0.0.9375"
    [[ -f "$manifest" ]] || { printf '%s\n' "$fallback"; return; }
    local parsed
    parsed=$(grep -A 4 '"ilspycmd"' "$manifest" | grep -m 1 '"version"' | sed -E 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/' || true)
    printf '%s\n' "${parsed:-$fallback}"
}

ilspycmd_version_bounds() {
    local manifest="$REPO_ROOT/.config/ilspycmd-compat.json"
    if [[ -f "$manifest" ]]; then
        grep -oE '"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+"' "$manifest" | tr -d '"'
        return
    fi
    printf '%s\n' '11.0.0.9375' '11.0.0.9375'
}

ilspycmd_version_at_least() {
    local current="$1" bound="$2"
    local current_major current_minor current_patch current_build
    local bound_major bound_minor bound_patch bound_build
    IFS=. read -r current_major current_minor current_patch current_build <<< "$current"
    IFS=. read -r bound_major bound_minor bound_patch bound_build <<< "$bound"
    if (( 10#$current_major != 10#$bound_major )); then
        (( 10#$current_major > 10#$bound_major ))
        return
    fi
    if (( 10#$current_minor != 10#$bound_minor )); then
        (( 10#$current_minor > 10#$bound_minor ))
        return
    fi
    if (( 10#$current_patch != 10#$bound_patch )); then
        (( 10#$current_patch > 10#$bound_patch ))
        return
    fi
    (( 10#$current_build >= 10#$bound_build ))
}

ilspycmd_version_at_most() {
    local current="$1" bound="$2"
    local current_major current_minor current_patch current_build
    local bound_major bound_minor bound_patch bound_build
    IFS=. read -r current_major current_minor current_patch current_build <<< "$current"
    IFS=. read -r bound_major bound_minor bound_patch bound_build <<< "$bound"
    if (( 10#$current_major != 10#$bound_major )); then
        (( 10#$current_major < 10#$bound_major ))
        return
    fi
    if (( 10#$current_minor != 10#$bound_minor )); then
        (( 10#$current_minor < 10#$bound_minor ))
        return
    fi
    if (( 10#$current_patch != 10#$bound_patch )); then
        (( 10#$current_patch < 10#$bound_patch ))
        return
    fi
    (( 10#$current_build <= 10#$bound_build ))
}

ilspycmd_version_supported() {
    local current="$1" bounds minimum maximum
    [[ "$current" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || return 1
    bounds="$(ilspycmd_version_bounds)" || return 1
    minimum="$(printf '%s\n' "$bounds" | sed -n '1p')"
    maximum="$(printf '%s\n' "$bounds" | sed -n '2p')"
    [[ "$minimum" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || return 1
    [[ "$maximum" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || return 1
    ilspycmd_version_at_least "$current" "$minimum" &&
        ilspycmd_version_at_most "$current" "$maximum"
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

query_ini_mime_handler() {
    local file="$1"
    local section="$2"
    [[ -f "$file" ]] || return 1
    python3 - "$file" "$section" "$MOD_INSTALL_MIME" <<'PY' 2>/dev/null
import sys
path, target_section, mime = sys.argv[1], f"[{sys.argv[2]}].lower()", sys.argv[3].lower() + "="
target_header = f"[{sys.argv[2]}]".lower()
in_sec = False
with open(path, "r", encoding="utf-8", errors="replace") as fp:
    for raw in fp:
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            in_sec = (line.lower() == target_header)
            continue
        if in_sec and line.lower().startswith(mime):
            rhs = line[len(mime):].strip()
            for part in rhs.split(";"):
                part = part.strip()
                if part:
                    print(part)
                    sys.exit(0)
sys.exit(1)
PY
}

query_default_uri_handler() {
    local handler=""
    local f
    for f in "$XDG_CONFIG_HOME/mimeapps.list" "$XDG_DATA_HOME/applications/mimeapps.list" "$XDG_DATA_HOME/applications/defaults.list"; do
        if handler="$(query_ini_mime_handler "$f" "Default Applications")" && [[ -n "$handler" ]]; then
            printf '%s' "$handler"
            return 0
        fi
    done
    if check_cmd xdg-mime; then
        handler="$(xdg-mime query default "$MOD_INSTALL_MIME" 2>/dev/null | tr -d '\r\n' || true)"
        if [[ -n "$handler" ]]; then
            printf '%s' "${handler%%;*}"
            return 0
        fi
    fi
    if handler="$(query_ini_mime_handler "$XDG_DATA_HOME/applications/mimeinfo.cache" "MIME Cache")" && [[ -n "$handler" ]]; then
        printf '%s' "$handler"
        return 0
    fi
    return 0
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

    if [[ "$URI_HANDLER_FLAG_SET" -eq 0 ]]; then
        local existing_handler
        existing_handler="$(query_default_uri_handler)"
        if [[ -n "$existing_handler" && "$existing_handler" != "optimum.desktop" ]]; then
            printf "    Set Optimum as default ModDB 1-click install handler (current: %s)? [y/N] " "$existing_handler"
        else
            printf "    Set Optimum as default ModDB 1-click install handler? [y/N] "
        fi
        read -r reply
        reply="${reply:-N}"
        if [[ "$reply" =~ ^[Yy] ]]; then SET_DEFAULT_URI_HANDLER=1; fi
    fi
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
    STAGE_ROOT="$(mktemp -d)"
    # --format none: we copy the staged folder into place below, so the archive
    # step only builds a tar.gz we delete unread. On a small or tmpfs /tmp that
    # extra ~600 MB has stalled the install before any files land.
    local pkg_args=(--output "$STAGE_ROOT" --format none)
    if [[ -n "$VERSION" ]]; then pkg_args+=(--version "$VERSION"); fi

    bash "$SCRIPT_DIR/package-linux.sh" "${pkg_args[@]}"

    local built_dir
    built_dir="$(find "$STAGE_ROOT" -maxdepth 1 -type d -name 'Optimum-v*-linux-x64' | sort | tail -n 1)"
    if [[ -z "$built_dir" ]]; then
        die "Linux package folder not found under $STAGE_ROOT"
    fi
    BUILT_DIR="$built_dir"
}

read_previous_install_state() {
    PREVIOUS_URI_HANDLER=""
    PREVIOUS_REGISTERED_URI=0
    local manifest="$INSTALL_DIR/.optimum/install-manifest.json"
    if [[ -f "$manifest" ]]; then
        local parsed
        parsed="$(python3 - "$manifest" <<'PY' 2>/dev/null || true
import json, sys
try:
    with open(sys.argv[1], "r", encoding="utf-8") as fp:
        data = json.load(fp)
    reg = "1" if data.get("registeredUriHandler") else "0"
    prev = data.get("previousUriHandler") or ""
    print(f"{reg}|{prev}")
except Exception:
    pass
PY
)"
        if [[ "$parsed" == *\|* ]]; then
            PREVIOUS_REGISTERED_URI="${parsed%%|*}"
            PREVIOUS_URI_HANDLER="${parsed#*|}"
        fi
    fi
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

    read_previous_install_state

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
    local exec_block
    if [[ -n "$DATA_PATH" ]]; then
        mkdir -p "$DATA_PATH"
        # Write datapath.cfg so the .NET launcher reads it on direct exe launch.
        printf '%s' "$DATA_PATH" > "$INSTALL_DIR/datapath.cfg"
        local quoted_data_path="'${DATA_PATH//\'/\'\\\'\'}'"
        exec_block="HAS_CUSTOM_DATA_PATH=0
for arg in \"\${LAUNCH_ARGS[@]}\"; do
    case \"\$arg\" in
        -d|--dataPath|-d=*|--dataPath=*) HAS_CUSTOM_DATA_PATH=1; break ;;
    esac
done
if [[ \"\$HAS_CUSTOM_DATA_PATH\" -eq 0 ]]; then
    exec ./run.sh --dataPath ${quoted_data_path} \"\${LAUNCH_ARGS[@]}\"
fi
exec ./run.sh \"\${LAUNCH_ARGS[@]}\""
    else
        rm -f "$INSTALL_DIR/datapath.cfg"
        exec_block='exec ./run.sh "${LAUNCH_ARGS[@]}"'
    fi

    cat > "$launcher" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

NORMALIZED_MOD_ARG=""

validate_mod_uri() {
    local raw="$1"
    if [[ "$raw" == *$'\n'* || "$raw" == *$'\r'* ]]; then
        echo "Optimum: rejecting mod install URI containing a newline." >&2
        return 1
    fi
    local lower="${raw,,}"
    if [[ "$lower" != vintagestorymodinstall://* ]]; then
        echo "Optimum: unsupported URI scheme (expected vintagestorymodinstall://)." >&2
        return 1
    fi
    local spec="${raw#*:}"
    spec="${spec#//}"
    spec="${spec%/}"
    spec="${spec//%40/@}"
    spec="${spec//%2b/+}"
    spec="${spec//%2B/+}"
    if [[ -z "$spec" || "$spec" == -* || "$spec" == */* || ! "$spec" =~ ^[A-Za-z0-9._+@%*-]+$ ]]; then
        echo "Optimum: malformed mod install URI." >&2
        return 1
    fi
    NORMALIZED_MOD_ARG="vintagestorymodinstall://${spec}"
}

validate_mod_arg() {
    local raw="$1"
    if [[ "$raw" == *$'\n'* || "$raw" == *$'\r'* ]]; then
        echo "Optimum: rejecting mod install argument containing a newline." >&2
        return 1
    fi
    local lower="${raw,,}"
    if [[ "$lower" == vintagestorymodinstall:* || "$raw" == *://* ]]; then
        validate_mod_uri "$raw"
        return $?
    fi
    local spec="${raw%/}"
    spec="${spec//%40/@}"
    if [[ -z "$spec" || "$spec" == -* || "$spec" == */* || ! "$spec" =~ ^[A-Za-z0-9._+@%*-]+$ ]]; then
        echo "Optimum: malformed mod install argument." >&2
        return 1
    fi
    NORMALIZED_MOD_ARG="$spec"
}

LAUNCH_ARGS=()
if [[ $# -gt 0 && "$1" != -* ]]; then
    if [[ $# -ne 1 ]]; then
        echo "Optimum: URI launch expects a single vintagestorymodinstall:// argument." >&2
        exit 2
    fi
    validate_mod_uri "$1" || exit 2
    LAUNCH_ARGS=(-i "$NORMALIZED_MOD_ARG")
else
    while [[ $# -gt 0 ]]; do
        case "$1" in
            -i|--installmod)
                if [[ $# -lt 2 ]]; then
                    echo "Optimum: $1 requires a mod identifier or vintagestorymodinstall:// URI." >&2
                    exit 2
                fi
                validate_mod_arg "$2" || exit 2
                LAUNCH_ARGS+=("$1" "$NORMALIZED_MOD_ARG")
                shift 2
                ;;
            -i=*|--installmod=*)
                validate_mod_arg "${1#*=}" || exit 2
                LAUNCH_ARGS+=("${1%%=*}=$NORMALIZED_MOD_ARG")
                shift
                ;;
            *)
                LAUNCH_ARGS+=("$1")
                shift
                ;;
        esac
    done
fi

EOF
    printf '%s\n' "$exec_block" >> "$launcher"
    chmod +x "$launcher"
}

escape_desktop_exec() {
    local val="$1"
    val="${val//\\/\\\\}"
    val="${val//\"/\\\"}"
    val="${val//\`/\\\`}"
    val="${val//\$/\\\$}"
    printf '%s' "$val"
}

write_desktop_entry() {
    local target="$1"
    local no_display="${2:-0}"
    local launcher="$INSTALL_DIR/optimum-launch.sh"
    local escaped_launcher
    escaped_launcher="$(escape_desktop_exec "$launcher")"
    mkdir -p "$(dirname "$target")"
    {
        printf '[Desktop Entry]\n'
        printf 'Type=Application\n'
        printf 'Name=Optimum\n'
        printf 'GenericName=Vintage Story optimized client\n'
        printf 'Comment=High-performance client for Vintage Story\n'
        printf 'Keywords=vintage;story;vs;optimum;client;\n'
        printf 'Exec="%s" %%u\n' "$escaped_launcher"
        printf 'Path=%s\n' "$INSTALL_DIR"
        printf 'Icon=optimum\n'
        printf 'Terminal=false\n'
        printf 'Categories=Game;\n'
        printf 'MimeType=%s;\n' "$MOD_INSTALL_MIME"
        if [[ "$no_display" -eq 1 ]]; then
            printf 'NoDisplay=true\n'
        fi
        printf 'StartupWMClass=Optimum\n'
    } > "$target"
    chmod +x "$target"
}

upsert_mimeapps_default() {
    local desktop_id="$1"
    local mimeapps="$XDG_CONFIG_HOME/mimeapps.list"
    mkdir -p "$(dirname "$mimeapps")"
    python3 - "$mimeapps" "$MOD_INSTALL_MIME" "$desktop_id" <<'PY'
import os, sys
path, mime, desktop_id = sys.argv[1], sys.argv[2], sys.argv[3]
header = "[Default Applications]"
prefix = mime.lower() + "="
entry = f"{mime}={desktop_id}"
lines = []
if os.path.exists(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fp:
        lines = fp.read().replace("\r\n", "\n").split("\n")
while lines and lines[-1] == "":
    lines.pop()
sec_idx = -1
next_sec = len(lines)
key_idx = -1
for i, raw in enumerate(lines):
    s = raw.strip()
    if s.startswith("[") and s.endswith("]"):
        if s.lower() == header.lower():
            sec_idx = i
        elif sec_idx >= 0 and next_sec == len(lines):
            next_sec = i
        continue
    if sec_idx >= 0 and next_sec == len(lines) and s.lower().startswith(prefix):
        key_idx = i
if key_idx >= 0:
    lines[key_idx] = entry
elif sec_idx >= 0:
    lines.insert(next_sec, entry)
else:
    if lines:
        lines.append("")
    lines.append(header)
    lines.append(entry)
with open(path, "w", encoding="utf-8") as fp:
    fp.write("\n".join(lines) + "\n")
PY
    if check_cmd xdg-mime; then
        xdg-mime default "$desktop_id" "$MOD_INSTALL_MIME" >/dev/null 2>&1 || true
    fi
}

remove_or_restore_uri_handler() {
    local restore_target="${1:-}"
    local apps_dir="$XDG_DATA_HOME/applications"
    if [[ -n "$restore_target" && "$restore_target" != "optimum.desktop" ]]; then
        if [[ ! -f "$apps_dir/$restore_target" && ! -f "/usr/local/share/applications/$restore_target" && ! -f "/usr/share/applications/$restore_target" ]]; then
            restore_target=""
        fi
    else
        restore_target=""
    fi

    local file
    for file in "$XDG_CONFIG_HOME/mimeapps.list" "$apps_dir/mimeapps.list" "$apps_dir/defaults.list"; do
        [[ -f "$file" ]] || continue
        python3 - "$file" "$MOD_INSTALL_MIME" "optimum.desktop" "$restore_target" <<'PY'
import sys
path, mime, target_remove, restore = sys.argv[1], sys.argv[2], sys.argv[3].lower(), sys.argv[4]
prefix = mime.lower() + "="
with open(path, "r", encoding="utf-8", errors="replace") as fp:
    lines = fp.read().replace("\r\n", "\n").split("\n")
section = ""
changed = False
out = []
for line in lines:
    s = line.strip()
    if s.startswith("[") and s.endswith("]"):
        section = s.lower()
        out.append(line)
        continue
    if section in ("[default applications]", "[added associations]") and s.lower().startswith(prefix):
        rhs = s[len(prefix):]
        semi = rhs.rstrip().endswith(";")
        items = [x.strip() for x in rhs.split(";") if x.strip()]
        filtered = [x for x in items if x.lower() != target_remove]
        if len(filtered) != len(items):
            changed = True
            if restore and section == "[default applications]" and all(x.lower() != restore.lower() for x in filtered):
                filtered.insert(0, restore)
            if filtered:
                suffix = ";" if (semi or len(filtered) > 1) else ""
                out.append(f"{mime}={';'.join(filtered)}{suffix}")
            continue
    out.append(line)
if changed:
    while out and out[-1] == "":
        out.pop()
    with open(path, "w", encoding="utf-8") as fp:
        fp.write(("\n".join(out) + "\n") if out else "")
PY
    done

    if [[ -n "$restore_target" ]]; then
        upsert_mimeapps_default "$restore_target"
    fi
}

update_mimeinfo_cache_entry() {
    local action="$1"
    local preferred_first="${2:-}"
    local cache_file="$XDG_DATA_HOME/applications/mimeinfo.cache"
    mkdir -p "$(dirname "$cache_file")"
    python3 - "$cache_file" "$MOD_INSTALL_MIME" "optimum.desktop" "$action" "$preferred_first" <<'PY'
import os, sys
path, mime, desktop_id, action, preferred = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
header = "[MIME Cache]"
prefix = mime.lower() + "="
lines = []
if os.path.exists(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fp:
        lines = fp.read().replace("\r\n", "\n").split("\n")
while lines and lines[-1] == "":
    lines.pop()
if action == "add":
    if not any(l.strip().lower() == header.lower() for l in lines):
        lines.insert(0, header)
    idx = next((i for i, l in enumerate(lines) if l.strip().lower().startswith(prefix)), -1)
    if idx >= 0:
        items = [x.strip() for x in lines[idx].strip()[len(prefix):].split(";") if x.strip()]
        if all(x.lower() != desktop_id.lower() for x in items):
            items.append(desktop_id)
        if preferred:
            items = [preferred] + [x for x in items if x.lower() != preferred.lower()]
        lines[idx] = f"{mime}={';'.join(items)};"
    else:
        if preferred and preferred.lower() != desktop_id.lower():
            lines.append(f"{mime}={preferred};{desktop_id};")
        else:
            lines.append(f"{mime}={desktop_id};")
elif action == "remove":
    if not os.path.exists(path):
        sys.exit(0)
    updated = []
    for l in lines:
        s = l.strip()
        if s.lower().startswith(prefix):
            items = [x.strip() for x in s[len(prefix):].split(";") if x.strip() and x.strip().lower() != desktop_id.lower()]
            if items:
                updated.append(f"{mime}={';'.join(items)};")
            continue
        updated.append(l)
    lines = updated
with open(path, "w", encoding="utf-8") as fp:
    fp.write(("\n".join(lines) + "\n") if lines else "")
PY
}

write_install_manifest() {
    local baseline_previous="$1"
    shift
    local shortcuts=("$@")
    mkdir -p "$INSTALL_DIR/.optimum"
    local opt_ver="dev"
    if [[ -f "$REPO_ROOT/VERSION" ]]; then
        opt_ver="$(tr -d '[:space:]' < "$REPO_ROOT/VERSION" 2>/dev/null || echo "dev")"
    fi
    python3 - "$INSTALL_DIR" "$opt_ver" "$DATA_PATH" "$SET_DEFAULT_URI_HANDLER" "$baseline_previous" "${shortcuts[@]}" <<'PY'
import datetime, json, os, sys
install_dir, opt_ver, data_path, reg_uri, prev_uri = sys.argv[1:6]
shortcuts = [s for s in sys.argv[6:] if s]
entries = sorted(e for e in os.listdir(install_dir) if e != ".optimum")
manifest = {
    "optimumVersion": opt_ver,
    "installedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "installDirectory": install_dir,
    "launcher": os.path.join(install_dir, "optimum-launch.sh"),
    "entries": entries,
    "shortcuts": shortcuts,
    "registeredUriHandler": (reg_uri == "1"),
}
if data_path:
    manifest["dataPath"] = data_path
if reg_uri == "1" and prev_uri:
    manifest["previousUriHandler"] = prev_uri
os.makedirs(os.path.join(install_dir, ".optimum"), exist_ok=True)
with open(os.path.join(install_dir, ".optimum", "install-manifest.json"), "w", encoding="utf-8") as fp:
    json.dump(manifest, fp, indent=2)
PY
}

refresh_desktop_databases() {
    local apps_dir="$XDG_DATA_HOME/applications"
    local icon_root="$XDG_DATA_HOME/icons/hicolor"
    if check_cmd update-desktop-database && [[ -d "$apps_dir" ]]; then
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
    printf "    ModDB handler: %s\n" "$([ "$SET_DEFAULT_URI_HANDLER" -eq 1 ] && echo 'set as default' || echo 'keep existing')"
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

    if [[ -z "$source_dir" ]]; then
        BUILT_DIR=""
        build_and_package
        source_dir="$BUILT_DIR"
    fi

    # Step 5: Install (the staged copy is removed by cleanup_stage on exit)
    install_from_dir "$source_dir"
    write_launcher

    # Step 6: Icon
    local icon_dir="$XDG_DATA_HOME/icons/hicolor/256x256/apps"
    mkdir -p "$icon_dir"
    if [[ -f "$INSTALL_DIR/assets/gameicon.png" ]]; then
        cp -f "$INSTALL_DIR/assets/gameicon.png" "$icon_dir/optimum.png"
    elif [[ -f "$REPO_ROOT/logo.png" ]]; then
        cp -f "$REPO_ROOT/logo.png" "$icon_dir/optimum.png"
    fi

    # Step 7: Shortcuts and XDG URI handler
    local prior_uri_handler
    prior_uri_handler="$(query_default_uri_handler)"
    local baseline_previous=""
    if [[ "$PREVIOUS_REGISTERED_URI" -eq 1 ]]; then
        baseline_previous="$PREVIOUS_URI_HANDLER"
    elif [[ -n "$prior_uri_handler" && "$prior_uri_handler" != "optimum.desktop" ]]; then
        baseline_previous="$prior_uri_handler"
    fi

    local created_shortcuts=()
    local menu_desktop="$XDG_DATA_HOME/applications/optimum.desktop"
    local wrote_menu_desktop=0
    if [[ "$CREATE_MENU" -eq 1 || "$SET_DEFAULT_URI_HANDLER" -eq 1 ]]; then
        local no_display=0
        [[ "$CREATE_MENU" -eq 0 ]] && no_display=1
        write_desktop_entry "$menu_desktop" "$no_display"
        wrote_menu_desktop=1
        created_shortcuts+=("$menu_desktop")
    elif [[ -f "$menu_desktop" ]]; then
        rm -f "$menu_desktop"
    fi

    local desktop_dir
    if check_cmd xdg-user-dir; then
        desktop_dir="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
    else
        desktop_dir="$HOME/Desktop"
    fi
    [[ "$desktop_dir" == "$HOME" ]] && desktop_dir="$HOME/Desktop"
    local desktop_shortcut="$desktop_dir/Optimum.desktop"
    if [[ "$CREATE_DESKTOP" -eq 1 ]]; then
        write_desktop_entry "$desktop_shortcut" 0
        created_shortcuts+=("$desktop_shortcut")
    elif [[ -f "$desktop_shortcut" ]]; then
        rm -f "$desktop_shortcut"
    fi

    refresh_desktop_databases

    local preferred_cache_first=""
    if [[ "$SET_DEFAULT_URI_HANDLER" -eq 1 ]]; then
        upsert_mimeapps_default "optimum.desktop"
        preferred_cache_first="optimum.desktop"
    elif [[ "$PREVIOUS_REGISTERED_URI" -eq 1 ]]; then
        remove_or_restore_uri_handler "$PREVIOUS_URI_HANDLER"
        preferred_cache_first="$PREVIOUS_URI_HANDLER"
    elif [[ -n "$prior_uri_handler" && "$prior_uri_handler" != "optimum.desktop" ]]; then
        upsert_mimeapps_default "$prior_uri_handler"
        preferred_cache_first="$prior_uri_handler"
    fi

    if [[ "$wrote_menu_desktop" -eq 1 ]]; then
        update_mimeinfo_cache_entry "add" "$preferred_cache_first"
    else
        update_mimeinfo_cache_entry "remove"
    fi

    write_install_manifest "$baseline_previous" "${created_shortcuts[@]}"

    # Done
    printf "\n"
    log "Optimum installed"
    printf "    ${BOLD}App:${RESET}     %s\n" "$INSTALL_DIR"
    if [[ "$CREATE_MENU" -eq 1 ]]; then
        printf "    ${BOLD}Menu:${RESET}    %s\n" "$menu_desktop"
    fi
    if [[ "$CREATE_DESKTOP" -eq 1 ]]; then
        printf "    ${BOLD}Desktop:${RESET} %s\n" "$desktop_shortcut"
    fi
    if [[ "$SET_DEFAULT_URI_HANDLER" -eq 1 ]]; then
        printf "    ${BOLD}URI:${RESET}     %s -> optimum.desktop\n" "$MOD_INSTALL_MIME"
    fi
    printf "    ${BOLD}Run:${RESET}     %s/optimum-launch.sh\n" "$INSTALL_DIR"
    printf "\n"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    trap cleanup_stage EXIT
    main
fi
