#!/usr/bin/env bash
# Builds every Optimum package this host can produce, in one run.
# Requires a successful build first (dotnet build VintageStory.slnx -c Release).
#
# Targets: linux-x64, osx-x64, osx-arm64, win-x64. The optimized DLLs are
# platform-agnostic IL, so any host can target any platform (quality varies).
# Vintage Story ships native ARM only for macOS, so Linux/Windows packages are
# x64-only; ARM runs x64 via emulation (box64 / Windows-on-ARM).
#
# Usage:
#   ./scripts/package-all.sh
#   ./scripts/package-all.sh --targets linux-x64,osx-arm64
#   ./scripts/package-all.sh --output ~/releases

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT_DIR="$REPO_ROOT/scripts"

# Defaults
OUTPUT_DIR="$REPO_ROOT"
TARGETS=""
VERSION="$(python3 -c "import json;print(json.load(open('$(dirname "$0")/../forks.json'))['vintageStoryVersion'])" 2>/dev/null || echo 1.22.7)"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --targets) TARGETS="$2"; shift 2 ;;
        --output)  OUTPUT_DIR="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# Detect host OS
OS_TYPE="$(uname -s)"

# Host capability detection (mirrors _hostcaps.ps1 logic)
# macOS ships bash 3.2, which has no associative arrays, so the three maps
# (CAP_QUALITY, CAP_NOTE, RESULTS) live in dynamic variable names instead.
_map_var() { printf '%s_%s' "$1" "$(printf '%s' "$2" | tr - _)"; }
map_set() { printf -v "$(_map_var "$1" "$2")" '%s' "$3"; }
map_get() { local v; v="$(_map_var "$1" "$2")"; printf '%s' "${!v:-${3:-}}"; }

# Linux target: needs tar
if command -v tar &>/dev/null; then
    map_set CAP_QUALITY linux-x64 "Full"
    map_set CAP_NOTE linux-x64 "overlay DLLs on vanilla linux client"
else
    map_set CAP_QUALITY linux-x64 "Blocked"
    map_set CAP_NOTE linux-x64 "tar not found"
fi

# macOS targets
for arch in x64 arm64; do
    if [[ "$OS_TYPE" == "Darwin" ]] && command -v hdiutil &>/dev/null; then
        map_set CAP_QUALITY "osx-$arch" "Full"
        map_set CAP_NOTE "osx-$arch" "hdiutil .dmg (notarizable)"
    elif [[ "$OS_TYPE" == "Linux" ]] && { command -v mkisofs &>/dev/null || command -v genisoimage &>/dev/null; } && command -v cmake &>/dev/null && command -v git &>/dev/null; then
        map_set CAP_QUALITY "osx-$arch" "Degraded"
        map_set CAP_NOTE "osx-$arch" "unsigned .dmg via libdmg-hfsplus"
    else
        map_set CAP_QUALITY "osx-$arch" "Degraded"
        map_set CAP_NOTE "osx-$arch" ".app assembled, .tar.gz fallback (no .dmg toolchain)"
    fi
done

# Windows target. The distro package on older systems is often innoextract 1.9,
# which cannot parse the official Inno Setup 6.4.3 installer. Require the
# supported release line before advertising an uncached off-platform package.
INNO_VERSION=""
if command -v innoextract &>/dev/null; then
    INNO_VERSION="$(innoextract --version 2>/dev/null | sed -n 's/^innoextract \([0-9][0-9]*\)\.\([0-9][0-9]*\).*/\1.\2/p' | head -n 1 || true)"
fi
INNO_MAJOR="${INNO_VERSION%%.*}"
INNO_MINOR="${INNO_VERSION#*.}"
INNO_USABLE=0
if [[ -n "$INNO_VERSION" && ( "$INNO_MAJOR" -gt 1 || ( "$INNO_MAJOR" -eq 1 && "$INNO_MINOR" -ge 11 ) ) ]]; then
    INNO_USABLE=1
fi

WIN_CACHE_DIR="$REPO_ROOT/.vanilla/win-x64/vintagestory"
HAS_WIN_CACHE=0
if [[ -f "$WIN_CACHE_DIR/Vintagestory.exe" ]]; then
    for marker in "$WIN_CACHE_DIR"/assets/version-*.txt; do
        if [[ -f "$marker" ]]; then
            HAS_WIN_CACHE=1
            break
        fi
    done
fi

if ! command -v pwsh &>/dev/null; then
    map_set CAP_QUALITY win-x64 "Blocked"
    map_set CAP_NOTE win-x64 "need pwsh + dotnet for off-platform Windows packaging"
elif ! command -v dotnet &>/dev/null; then
    map_set CAP_QUALITY win-x64 "Blocked"
    map_set CAP_NOTE win-x64 "need dotnet to cross-build the win-x64 launcher"
elif [[ "$HAS_WIN_CACHE" -eq 1 ]]; then
    map_set CAP_QUALITY win-x64 "Degraded"
    map_set CAP_NOTE win-x64 "cross-build Optimum.exe + cached Windows client"
elif [[ "$INNO_USABLE" -eq 1 ]]; then
    map_set CAP_QUALITY win-x64 "Degraded"
    map_set CAP_NOTE win-x64 "cross-build Optimum.exe + innoextract $INNO_VERSION for the official Inno 6.4.3 installer"
elif [[ -n "$INNO_VERSION" && ( "$INNO_MAJOR" -lt 1 || ( "$INNO_MAJOR" -eq 1 && "$INNO_MINOR" -lt 11 ) ) ]]; then
    map_set CAP_QUALITY win-x64 "Blocked"
    map_set CAP_NOTE win-x64 "innoextract $INNO_VERSION is too old; need innoextract >= 1.11"
elif [[ -z "$INNO_VERSION" ]]; then
    map_set CAP_QUALITY win-x64 "Blocked"
    map_set CAP_NOTE win-x64 "need innoextract >= 1.11 + dotnet for off-platform Windows packaging"
else
    map_set CAP_QUALITY win-x64 "Blocked"
    map_set CAP_NOTE win-x64 "need dotnet to cross-build the win-x64 launcher"
fi

# Print capability report
echo ""
echo "Host: $OS_TYPE - packaging capability"
ALL_TARGETS=(linux-x64 osx-x64 osx-arm64 win-x64)
for t in "${ALL_TARGETS[@]}"; do
    printf "  %-12s %-9s %s\n" "$t" "$(map_get CAP_QUALITY "$t")" "$(map_get CAP_NOTE "$t")"
done
echo ""

# Filter to requested targets
if [[ -n "$TARGETS" ]]; then
    IFS=',' read -ra REQUESTED <<< "$TARGETS"
else
    REQUESTED=("${ALL_TARGETS[@]}")
fi

# Build runnable list (Full or Degraded)
RUNNABLE=()
for t in "${REQUESTED[@]}"; do
    t="$(echo "$t" | tr -d ' ')"
    q="$(map_get CAP_QUALITY "$t" "Blocked")"
    if [[ "$q" == "Full" || "$q" == "Degraded" ]]; then
        RUNNABLE+=("$t")
    else
        echo "Skipping $t ($q): $(map_get CAP_NOTE "$t" "unknown")" >&2
    fi
done

if [[ ${#RUNNABLE[@]} -eq 0 ]]; then
    echo "Nothing to build on this host."
    exit 0
fi

# Run each target
FAILED=0

for target in "${RUNNABLE[@]}"; do
    echo "==> Building $target ..."
    case "$target" in
        linux-x64)
            if bash "$SCRIPT_DIR/package-linux.sh" --output "$OUTPUT_DIR" --version "$VERSION"; then
                map_set RESULTS "$target" "OK"
            else
                map_set RESULTS "$target" "FAILED"; FAILED=1
            fi
            ;;
        osx-x64|osx-arm64)
            arch="${target#osx-}"
            if bash "$SCRIPT_DIR/package-macos.sh" --arch "$arch" --output "$OUTPUT_DIR" --version "$VERSION"; then
                map_set RESULTS "$target" "OK"
            else
                map_set RESULTS "$target" "FAILED"; FAILED=1
            fi
            ;;
        win-x64)
            # Windows packaging still uses the PowerShell script (requires pwsh)
            if command -v pwsh &>/dev/null; then
                if pwsh "$SCRIPT_DIR/package.ps1" -OutputDir "$OUTPUT_DIR" -Zip -Version "$VERSION"; then
                    map_set RESULTS "$target" "OK"
                else
                    map_set RESULTS "$target" "FAILED"; FAILED=1
                fi
            else
                echo "  win-x64 packaging requires pwsh (PowerShell). Skipping." >&2
                map_set RESULTS "$target" "SKIPPED"
            fi
            ;;
    esac
done

# Summary
echo ""
echo "Summary:"
for target in "${RUNNABLE[@]}"; do
    status="$(map_get RESULTS "$target" "UNKNOWN")"
    printf "  %-12s %s\n" "$target" "$status"
done

exit $FAILED
