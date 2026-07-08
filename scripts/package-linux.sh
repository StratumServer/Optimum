#!/usr/bin/env bash
# Builds a ready-to-run Optimum package for Linux (x64). Downloads the official
# Vintage Story Linux client, overlays the optimized DLLs, renames the launcher
# to Optimum, and packages as tar.gz (default), zip, or AppImage.
# Requires a successful build first (dotnet build VintageStory.slnx -c Release).
#
# Usage:
#   ./scripts/package-linux.sh
#   ./scripts/package-linux.sh --format zip --output ~/releases
#   ./scripts/package-linux.sh --format appimage
#   ./scripts/package-linux.sh --client-archive /path/to/vs_client_linux-x64_1.22.3.tar.gz

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
DIM='\033[2m'
RESET='\033[0m'

# Defaults
FORMAT="targz"
OUTPUT_DIR="$REPO_ROOT"
VERSION="1.22.3"
CLIENT_ARCHIVE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --format)       FORMAT="$2"; shift 2 ;;
        --output)       OUTPUT_DIR="$2"; shift 2 ;;
        --version)      VERSION="$2"; shift 2 ;;
        --client-archive) CLIENT_ARCHIVE="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

if [[ "$FORMAT" != "targz" && "$FORMAT" != "zip" && "$FORMAT" != "appimage" ]]; then
    echo "Error: --format must be 'targz', 'zip', or 'appimage'" >&2
    exit 1
fi

# ============================================================================
# Prerequisite checks
# ============================================================================

check_cmd() { command -v "$1" &>/dev/null; }

APPIMAGETOOL_PATH=""

check_appimage_prereqs() {
    local missing=()

    # appimagetool
    if check_cmd appimagetool; then
        APPIMAGETOOL_PATH="appimagetool"
    elif [[ -x "$REPO_ROOT/.tools/appimagetool" ]]; then
        APPIMAGETOOL_PATH="$REPO_ROOT/.tools/appimagetool"
    else
        missing+=("appimagetool")
    fi

    # file (used by appimagetool internally, almost always present)
    if ! check_cmd file; then
        missing+=("file")
    fi

    if [[ ${#missing[@]} -eq 0 ]]; then return 0; fi

    printf "\n${BOLD}  AppImage prerequisites${RESET}\n\n"

    for tool in "${missing[@]}"; do
        printf "    ${RED}✗${RESET}  %s\n" "$tool"
    done
    echo ""

    # Offer to install
    for tool in "${missing[@]}"; do
        case "$tool" in
            appimagetool)
                printf "  ${YELLOW}appimagetool${RESET} not found.\n"
                printf "  Download to .tools/? [Y/n] "
                local reply
                read -r reply
                reply="${reply:-Y}"
                if [[ "$reply" =~ ^[Yy] ]]; then
                    install_appimagetool
                else
                    echo "Error: appimagetool required for --format appimage" >&2
                    echo "  Install: https://github.com/AppImage/appimagetool/releases" >&2
                    echo "  Or: sudo apt install appimagetool" >&2
                    exit 1
                fi
                ;;
            file)
                printf "  ${YELLOW}file${RESET} command not found.\n"
                printf "  Install with: sudo apt install file\n"
                printf "  Install now? [Y/n] "
                local reply2
                read -r reply2
                reply2="${reply2:-Y}"
                if [[ "$reply2" =~ ^[Yy] ]]; then
                    sudo apt install -y file
                    if ! check_cmd file; then
                        echo "Error: failed to install 'file'" >&2
                        exit 1
                    fi
                else
                    echo "Error: 'file' command required for AppImage creation" >&2
                    exit 1
                fi
                ;;
        esac
    done
}

install_appimagetool() {
    local tools_dir="$REPO_ROOT/.tools"
    mkdir -p "$tools_dir"
    local url="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    printf "  Downloading appimagetool...\n"
    curl -L --fail -o "$tools_dir/appimagetool" "$url"
    chmod +x "$tools_dir/appimagetool"
    APPIMAGETOOL_PATH="$tools_dir/appimagetool"
    printf "    ${GREEN}✓${RESET} appimagetool installed to .tools/\n\n"
}

# Run AppImage prereq check early if needed
if [[ "$FORMAT" == "appimage" ]]; then
    check_appimage_prereqs
fi

# ============================================================================
# Build output verification
# ============================================================================

BUILD_OUT="$REPO_ROOT/build/Vintagestory/bin/Release/net10.0"
LIB_OUT="$REPO_ROOT/build/VintagestoryLib/bin/Release/net10.0"
MOD_OUT="$REPO_ROOT/bin/Release/net10.0"

if [[ ! -f "$LIB_OUT/VintagestoryLib.dll" ]]; then
    echo "Error: build output not found. Run: dotnet build VintageStory.slnx -c Release" >&2
    exit 1
fi
PATCHED_LIB="$LIB_OUT/VintagestoryLib-patched.dll"

# ============================================================================
# 1. Acquire the official Linux client archive
# ============================================================================

ZIP_CACHE="$REPO_ROOT/.vanilla/archives"
mkdir -p "$ZIP_CACHE"

if [[ -z "$CLIENT_ARCHIVE" ]]; then
    CLIENT_ARCHIVE="$ZIP_CACHE/vs_client_linux-x64_${VERSION}.tar.gz"
fi

if [[ ! -f "$CLIENT_ARCHIVE" ]]; then
    URL="https://cdn.vintagestory.at/gamefiles/stable/vs_client_linux-x64_${VERSION}.tar.gz"
    echo "Downloading $URL"
    curl -L --fail -o "$CLIENT_ARCHIVE" "$URL"
else
    echo "Using cached $CLIENT_ARCHIVE"
fi

# ============================================================================
# 2. Extract the base install (vintagestory/) once
# ============================================================================

BASE_ROOT="$REPO_ROOT/.vanilla/linux-x64"
VANILLA_DIR="$BASE_ROOT/vintagestory"
EXTRACTED_FRESH=0

if [[ ! -d "$VANILLA_DIR" ]]; then
    mkdir -p "$BASE_ROOT"
    echo "Extracting to $BASE_ROOT"
    tar -xzf "$CLIENT_ARCHIVE" -C "$BASE_ROOT"
    EXTRACTED_FRESH=1
fi

if [[ ! -d "$VANILLA_DIR" ]]; then
    echo "Error: extraction failed, $VANILLA_DIR not found" >&2
    exit 1
fi
if [[ "$EXTRACTED_FRESH" == "1" ]]; then
    cp -f "$VANILLA_DIR/VintagestoryLib.dll" "$VANILLA_DIR/VintagestoryLib.vanilla.dll"
fi
VANILLA_LIB="$VANILLA_DIR/VintagestoryLib.vanilla.dll"
if [[ ! -f "$VANILLA_LIB" ]]; then
    echo "Error: pristine vanilla VintagestoryLib.vanilla.dll not found in $VANILLA_DIR. Delete the matching .vanilla cache and re-run packaging." >&2
    exit 1
fi
dotnet run --project "$REPO_ROOT/Optimum.Patcher" -c Release -- "$VANILLA_LIB" "$LIB_OUT/VintagestoryLib.dll" "$PATCHED_LIB"

# ============================================================================
# 3. Version from OptimumInfo.cs
# ============================================================================

INFO_FILE="$REPO_ROOT/build/VintagestoryLib/Optimum/OptimumInfo.cs"
OPT_VER="0.2.5"
if [[ -f "$INFO_FILE" ]]; then
    MATCH=$(perl -ne 'if (/Version\s*=\s*"([^"]+)"/) { print $1; exit }' "$INFO_FILE" || true)
    if [[ -n "$MATCH" ]]; then OPT_VER="$MATCH"; fi
fi

NAME="Optimum-v${OPT_VER}-linux-x64"
STAGE_DIR="$OUTPUT_DIR/$NAME"

# ============================================================================
# 4. Fresh copy of the vanilla install
# ============================================================================

echo "Staging $STAGE_DIR"
rm -rf "$STAGE_DIR"
cp -a "$VANILLA_DIR" "$STAGE_DIR"

# ============================================================================
# 5. Overlay optimized DLLs (platform-agnostic IL)
# ============================================================================

cp -f "$BUILD_OUT/Vintagestory.dll" "$STAGE_DIR/"
cp -f "$BUILD_OUT/Vintagestory.runtimeconfig.json" "$STAGE_DIR/Vintagestory.runtimeconfig.json"
cp -f "$PATCHED_LIB" "$STAGE_DIR/VintagestoryLib.dll"
cp -f "$MOD_OUT/VintagestoryAPI.dll" "$STAGE_DIR/"
cp -f "$MOD_OUT/VSEssentials.dll" "$STAGE_DIR/Mods/"
cp -f "$MOD_OUT/VSSurvivalMod.dll" "$STAGE_DIR/Mods/"
cp -f "$MOD_OUT/VSCreativeMod.dll" "$STAGE_DIR/Mods/"
cp -f "$MOD_OUT/cairo-sharp.dll" "$STAGE_DIR/Lib/"

# 5b. Overlay optimized shaders.
SHADER_SRC="$REPO_ROOT/sources/shaders"
SHADER_DST="$STAGE_DIR/assets/game/shaders"
if [[ -d "$SHADER_SRC" ]]; then
    find "$SHADER_SRC" -maxdepth 1 -type f -exec cp -f {} "$SHADER_DST/" \;
fi

# 5c. Merge translation strings.
LANG_SRC="$REPO_ROOT/sources/lang"
LANG_DST="$STAGE_DIR/assets/game/lang"
if [[ -d "$LANG_SRC" ]]; then
    for src_file in "$LANG_SRC"/*.json; do
        [[ -f "$src_file" ]] || continue
        dst_file="$LANG_DST/$(basename "$src_file")"
        [[ -f "$dst_file" ]] || continue
        # Read with utf-8-sig (strips BOM if present; vanilla lang files have one).
        # Write with utf-8 (no BOM on output). Python's default encoding is the
        # locale's preferred encoding (ASCII on a minimal/C-locale system),
        # which would mangle non-ASCII characters.
        python3 -c "
import json, sys
with open(sys.argv[1], encoding='utf-8-sig') as f: src = json.load(f)
with open(sys.argv[2], encoding='utf-8-sig') as f: dst = json.load(f)
dst.update(src)
with open(sys.argv[2], 'w', encoding='utf-8') as f: json.dump(dst, f, ensure_ascii=False, indent='\t'); f.write('\n')
" "$src_file" "$dst_file"
    done
fi

# ============================================================================
# Validate the staged assets: zero-byte or truncated files from a bad
# vanilla extraction surface in-game as opaque GL crashes ("blur.vsh ...
# unexpected \$end at <EOF>"). Fail the package instead.
EMPTY_ASSETS=$(find "$STAGE_DIR/assets" -type f -size 0 ! -name 'version-*.txt' || true)
if [[ -n "$EMPTY_ASSETS" ]]; then
    echo "Error: staged assets contain zero-byte file(s); the vanilla extraction is corrupt:" >&2
    echo "$EMPTY_ASSETS" >&2
    exit 1
fi
BAD_SHADERS=""
while IFS= read -r -d '' f; do
    grep -q 'void[[:space:]]*main' "$f" || BAD_SHADERS="$BAD_SHADERS $f"
done < <(find "$SHADER_DST" -maxdepth 1 -type f \( -name '*.vsh' -o -name '*.fsh' -o -name '*.gsh' \) -print0)
if [[ -n "$BAD_SHADERS" ]]; then
    echo "Error: staged shader(s) truncated or corrupt (no 'void main'):$BAD_SHADERS" >&2
    exit 1
fi

# 6. Rebrand: rename launcher, repoint run.sh, swap icon, brand .desktop
# ============================================================================

if [[ -f "$STAGE_DIR/Vintagestory" ]]; then
    mv "$STAGE_DIR/Vintagestory" "$STAGE_DIR/Optimum"
    chmod +x "$STAGE_DIR/Optimum"
else
    echo "Warning: launcher 'Vintagestory' not found in archive." >&2
fi

# run.sh launches ./Vintagestory; point it at ./Optimum.
if [[ -f "$STAGE_DIR/run.sh" ]]; then
    perl -pi -e 's|\./Vintagestory |./Optimum |g' "$STAGE_DIR/run.sh"
fi

# The .desktop entry and the window both read assets/gameicon.png.
if [[ -f "$STAGE_DIR/assets/gameicon.png" && -f "$REPO_ROOT/logo.png" ]]; then
    cp -f "$REPO_ROOT/logo.png" "$STAGE_DIR/assets/gameicon.png"
fi

# Brand the .desktop launcher entry.
if [[ -f "$STAGE_DIR/Vintagestory.desktop" ]]; then
    sed -E 's/Name(\[[a-z]+\])?=Vintage Story [0-9.]+/Name\1=Optimum/g' \
        "$STAGE_DIR/Vintagestory.desktop" > "$STAGE_DIR/Optimum.desktop"
    rm -f "$STAGE_DIR/Vintagestory.desktop"
fi

echo "Folder ready: $STAGE_DIR"

# ============================================================================
# 7. Package
# ============================================================================

build_appimage() {
    local appdir="$OUTPUT_DIR/${NAME}.AppDir"
    local appimage="$OUTPUT_DIR/${NAME}.AppImage"

    echo "Assembling AppDir: $appdir"
    rm -rf "$appdir"
    mkdir -p "$appdir/usr/share/optimum"

    # Move the staged game files into the AppDir
    cp -a "$STAGE_DIR"/. "$appdir/usr/share/optimum/"

    # AppRun: entry point that launches the game
    cat > "$appdir/AppRun" <<'APPRUN'
#!/usr/bin/env bash
set -euo pipefail
HERE="$(dirname "$(readlink -f "$0")")"
GAME_DIR="$HERE/usr/share/optimum"
cd "$GAME_DIR"
exec ./Optimum "$@"
APPRUN
    chmod +x "$appdir/AppRun"

    # .desktop file at AppDir root (required by appimagetool)
    cat > "$appdir/optimum.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Optimum
Comment=High-performance client for Vintage Story
Exec=Optimum
Icon=optimum
Terminal=false
Categories=Game;
StartupWMClass=Optimum
DESKTOP

    # Icon at AppDir root (required by appimagetool: must match Icon= field)
    if [[ -f "$REPO_ROOT/logo.png" ]]; then
        cp -f "$REPO_ROOT/logo.png" "$appdir/optimum.png"
    elif [[ -f "$appdir/usr/share/optimum/assets/gameicon.png" ]]; then
        cp -f "$appdir/usr/share/optimum/assets/gameicon.png" "$appdir/optimum.png"
    fi

    # Also place icon in standard hicolor path for desktop integration
    mkdir -p "$appdir/usr/share/icons/hicolor/256x256/apps"
    if [[ -f "$appdir/optimum.png" ]]; then
        cp -f "$appdir/optimum.png" "$appdir/usr/share/icons/hicolor/256x256/apps/optimum.png"
    fi

    # Build the AppImage
    echo "Running appimagetool..."
    rm -f "$appimage"

    # appimagetool needs ARCH set for the output filename
    ARCH=x86_64 "$APPIMAGETOOL_PATH" --no-appstream "$appdir" "$appimage" 2>&1 | tail -5

    # Clean up AppDir (the .AppImage is self-contained)
    rm -rf "$appdir"

    if [[ -f "$appimage" ]]; then
        chmod +x "$appimage"
        local size
        size=$(du -m "$appimage" | cut -f1)
        echo "Done: $appimage (${size}MB)"
    else
        echo "Error: appimagetool failed to produce $appimage" >&2
        exit 1
    fi
}

case "$FORMAT" in
    zip)
        OUT="$OUTPUT_DIR/${NAME}.zip"
        rm -f "$OUT"
        (cd "$OUTPUT_DIR" && zip -qr "$NAME.zip" "$NAME")
        SIZE=$(du -m "$OUT" | cut -f1)
        echo "Done: $OUT (${SIZE}MB)"
        ;;
    appimage)
        build_appimage
        ;;
    *)
        OUT="$OUTPUT_DIR/${NAME}.tar.gz"
        rm -f "$OUT"
        tar -czf "$OUT" -C "$OUTPUT_DIR" "$NAME"
        SIZE=$(du -m "$OUT" | cut -f1)
        echo "Done: $OUT (${SIZE}MB)"
        ;;
esac
