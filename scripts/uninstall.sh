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

if [[ -d "$HOME/.dotnet" ]]; then
    export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
fi
if [[ -d "$HOME/.local/bin" ]]; then
    export PATH="$HOME/.local/bin:$PATH"
fi

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

cleanup_linux_uri_associations() {
    local restore_target="${1:-}"
    local apps_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
    local cfg_dir="${XDG_CONFIG_HOME:-$HOME/.config}"

    if [[ -n "$restore_target" && "${restore_target,,}" != "optimum.desktop" && "$restore_target" != */* && "$restore_target" != *\\* ]]; then
        local found=0
        [[ -f "$apps_dir/$restore_target" ]] && found=1
        if [[ "$found" -eq 0 ]]; then
            local data_dirs="${XDG_DATA_DIRS:-/usr/local/share:/usr/share}"
            local IFS=':'
            local d
            for d in $data_dirs; do
                [[ -n "$d" && -f "${d%/}/applications/$restore_target" ]] && { found=1; break; }
            done
        fi
        [[ "$found" -eq 0 ]] && restore_target=""
    else
        restore_target=""
    fi

    if command -v update-desktop-database >/dev/null 2>&1 && [[ -d "$apps_dir" ]]; then
        update-desktop-database "$apps_dir" >/dev/null 2>&1 || true
    fi

    if command -v python3 >/dev/null 2>&1; then
        python3 - "$restore_target" \
                  "$cfg_dir/mimeapps.list" \
                  "$apps_dir/mimeapps.list" \
                  "$apps_dir/defaults.list" \
                  "$apps_dir/mimeinfo.cache" <<'PY' 2>/dev/null || true
import os, sys
restore = sys.argv[1]
prefix = "x-scheme-handler/vintagestorymodinstall="
for path in sys.argv[2:]:
    if not os.path.exists(path):
        continue
    with open(path, "r", encoding="utf-8", errors="replace") as fp:
        lines = fp.read().replace("\r\n", "\n").split("\n")
    changed = False
    section = ""
    is_cache = path.endswith("mimeinfo.cache")
    out = []
    for line in lines:
        s = line.strip()
        if s.startswith("[") and s.endswith("]"):
            section = s.lower()
            out.append(line)
            continue
        if s.lower().startswith(prefix):
            items = [x.strip() for x in s[len(prefix):].split(";") if x.strip()]
            filtered = [x for x in items if x.lower() != "optimum.desktop"]
            if len(filtered) != len(items):
                changed = True
                if restore and not is_cache and section == "[default applications]" and all(x.lower() != restore.lower() for x in filtered):
                    filtered.insert(0, restore)
                if filtered:
                    semi = ";" if (s.endswith(";") or len(filtered) > 1 or is_cache) else ""
                    out.append(f"{prefix}{';'.join(filtered)}{semi}")
                continue
        out.append(line)
    if changed:
        while out and out[-1] == "":
            out.pop()
        with open(path, "w", encoding="utf-8") as fp:
            fp.write(("\n".join(out) + "\n") if out else "")
PY
    fi
}

# ---------------------------------------------------------------------------
# Standalone install: delegate to the engine (or fallback if dotnet absent)
# ---------------------------------------------------------------------------
if [[ -f "$INSTALL_DIR/.optimum/install-manifest.json" ]]; then
    if command -v dotnet >/dev/null 2>&1 && [[ -f "$REPO_ROOT/Optimum.Cli/Optimum.Cli.csproj" ]]; then
        dotnet build "$REPO_ROOT/Optimum.Cli/Optimum.Cli.csproj" -c Release --nologo >/dev/null
        exec dotnet run --project "$REPO_ROOT/Optimum.Cli/Optimum.Cli.csproj" -c Release --no-build -- \
            uninstall --install-dir "$INSTALL_DIR"
    fi
    prev_handler=""
    if command -v python3 >/dev/null 2>&1; then
        prev_handler="$(python3 - "$INSTALL_DIR/.optimum/install-manifest.json" <<'PY' 2>/dev/null || true
import json, os, sys
try:
    with open(sys.argv[1], "r", encoding="utf-8") as fp:
        data = json.load(fp)
    for sc in data.get("shortcuts") or []:
        if os.path.isfile(sc):
            os.remove(sc)
    print(data.get("previousUriHandler") or "")
except Exception:
    pass
PY
)"
    fi
    rm -f -- "${XDG_DATA_HOME:-$HOME/.local/share}/applications/optimum.desktop" "$HOME/Desktop/Optimum.desktop" 2>/dev/null || true
    cleanup_linux_uri_associations "$prev_handler"
    rm -rf -- "$INSTALL_DIR"
    printf "\n  ${GREEN}Removed Optimum install at %s.${RESET}\n\n" "$INSTALL_DIR"
    exit 0
fi

# ---------------------------------------------------------------------------
# Legacy overlay install
# ---------------------------------------------------------------------------
[[ -f "$INSTALL_DIR/VintagestoryLib.dll" ]] || { printf "${RED}Not a Vintage Story directory: %s${RESET}\n" "$INSTALL_DIR" >&2; exit 1; }

OPTIMUM_FILES=(
    Optimum Optimum.dll Optimum.deps.json Optimum.runtimeconfig.json
    Optimum.Patcher.dll Optimum.Api.Contracts.dll Optimum.GameContent.dll VintagestoryLib.Donor.dll
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
rm -f -- "${XDG_DATA_HOME:-$HOME/.local/share}/applications/optimum.desktop" "$HOME/Desktop/Optimum.desktop" 2>/dev/null || true
cleanup_linux_uri_associations ""

if [[ "$failed" -ne 0 ]]; then
    printf "${RED}Some Optimum files could not be removed. Close any running Optimum process and retry.${RESET}\n" >&2
    exit 1
fi

printf "\n  ${GREEN}Removed %d Optimum file(s). Vintage Story is vanilla again.${RESET}\n\n" "$removed"
