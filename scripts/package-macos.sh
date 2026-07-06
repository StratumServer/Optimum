#!/usr/bin/env bash
# Builds a ready-to-run Optimum.app for macOS and packages it as a .dmg.
# Downloads the official Vintage Story macOS client for the chosen architecture,
# overlays the optimized DLLs, rebrands the bundle (name, launcher, icon), and
# builds a drag-to-Applications disk image.
# Requires a successful build first (dotnet build VintageStory.slnx -c Release).
#
# The .dmg comes from hdiutil on macOS. On Linux the script builds an unsigned
# .dmg with libdmg-hfsplus (compiled once into .tools/; needs mkisofs or
# genisoimage, plus cmake and git). Without those tools it assembles Optimum.app
# and writes a .tar.gz fallback.
#
# Usage:
#   ./scripts/package-macos.sh --arch arm64
#   ./scripts/package-macos.sh --arch x64 --output ~/releases

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Defaults
ARCH="arm64"
OUTPUT_DIR="$REPO_ROOT"
VERSION="1.22.3"
CLIENT_ARCHIVE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch)           ARCH="$2"; shift 2 ;;
        --output)         OUTPUT_DIR="$2"; shift 2 ;;
        --version)        VERSION="$2"; shift 2 ;;
        --client-archive) CLIENT_ARCHIVE="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

if [[ "$ARCH" != "arm64" && "$ARCH" != "x64" ]]; then
    echo "Error: --arch must be 'arm64' or 'x64'" >&2
    exit 1
fi

# Build output paths
BUILD_OUT="$REPO_ROOT/build/Vintagestory/bin/Release/net10.0"
LIB_OUT="$REPO_ROOT/build/VintagestoryLib/bin/Release/net10.0"
MOD_OUT="$REPO_ROOT/bin/Release/net10.0"

if [[ ! -f "$LIB_OUT/VintagestoryLib.dll" ]]; then
    echo "Error: build output not found. Run: dotnet build VintageStory.slnx -c Release" >&2
    exit 1
fi
PATCHED_LIB="$LIB_OUT/VintagestoryLib-patched.dll"

ICNS="$REPO_ROOT/logo.icns"
if [[ ! -f "$ICNS" ]]; then
    ICNS="$REPO_ROOT/docs/logo.icns"
fi
if [[ ! -f "$ICNS" ]]; then
    echo "Error: logo.icns not found at repo root or docs/." >&2
    exit 1
fi

# 1. Acquire the official macOS client archive.
ZIP_CACHE="$REPO_ROOT/.vanilla/archives"
mkdir -p "$ZIP_CACHE"

if [[ -z "$CLIENT_ARCHIVE" ]]; then
    CLIENT_ARCHIVE="$ZIP_CACHE/vs_client_osx-${ARCH}_${VERSION}.tar.gz"
fi

if [[ ! -f "$CLIENT_ARCHIVE" ]]; then
    URL="https://cdn.vintagestory.at/gamefiles/stable/vs_client_osx-${ARCH}_${VERSION}.tar.gz"
    echo "Downloading $URL"
    curl -L --fail -o "$CLIENT_ARCHIVE" "$URL"
else
    echo "Using cached $CLIENT_ARCHIVE"
fi

# 2. Extract the base bundle (Vintage Story.app) once.
BASE_ROOT="$REPO_ROOT/.vanilla/osx-${ARCH}"
BASE_APP="$BASE_ROOT/Vintage Story.app"

if [[ ! -d "$BASE_APP" ]]; then
    mkdir -p "$BASE_ROOT"
    echo "Extracting to $BASE_ROOT"
    tar -xzf "$CLIENT_ARCHIVE" -C "$BASE_ROOT"
fi

if [[ ! -d "$BASE_APP" ]]; then
    echo "Error: extraction failed, 'Vintage Story.app' not found" >&2
    exit 1
fi
dotnet run --project "$REPO_ROOT/Optimum.Patcher" -c Release -- "$BASE_APP/VintagestoryLib.dll" "$LIB_OUT/VintagestoryLib.dll" "$PATCHED_LIB"

# 3. Version from OptimumInfo.cs.
INFO_FILE="$REPO_ROOT/build/VintagestoryLib/Optimum/OptimumInfo.cs"
OPT_VER="0.2.2"
if [[ -f "$INFO_FILE" ]]; then
    MATCH=$(perl -ne 'if (/Version\s*=\s*"([^"]+)"/) { print $1; exit }' "$INFO_FILE" || true)
    if [[ -n "$MATCH" ]]; then OPT_VER="$MATCH"; fi
fi

APP_DIR="$OUTPUT_DIR/Optimum.app"

# Guard: refuse to write into an existing Vintage Story installation.
if [[ -f "$OUTPUT_DIR/Vintagestory" && ! -f "$OUTPUT_DIR/Optimum" ]]; then
    echo "Error: output directory ($OUTPUT_DIR) contains a vanilla Vintage Story installation. Choose a different --output." >&2
    exit 1
fi
if [[ -d "$OUTPUT_DIR/Vintage Story.app" ]]; then
    echo "Error: output directory contains 'Vintage Story.app'. Optimum must not overwrite your vanilla install." >&2
    exit 1
fi

# 4. Fresh copy of the vanilla bundle.
echo "Assembling $APP_DIR"
rm -rf "$APP_DIR"
cp -a "$BASE_APP" "$APP_DIR"

# 5. Overlay optimized DLLs (platform-agnostic IL).
cp -f "$BUILD_OUT/Vintagestory.dll" "$APP_DIR/"
cp -f "$PATCHED_LIB" "$APP_DIR/VintagestoryLib.dll"
cp -f "$MOD_OUT/VintagestoryAPI.dll" "$APP_DIR/"
cp -f "$MOD_OUT/VSEssentials.dll" "$APP_DIR/Mods/"
cp -f "$MOD_OUT/VSSurvivalMod.dll" "$APP_DIR/Mods/"
cp -f "$MOD_OUT/VSCreativeMod.dll" "$APP_DIR/Mods/"
cp -f "$MOD_OUT/cairo-sharp.dll" "$APP_DIR/Lib/"

# 5b. Overlay optimized shaders.
SHADER_SRC="$REPO_ROOT/sources/shaders"
SHADER_DST="$APP_DIR/assets/game/shaders"
if [[ -d "$SHADER_SRC" ]]; then
    find "$SHADER_SRC" -maxdepth 1 -type f -exec cp -f {} "$SHADER_DST/" \;
fi

# 5c. Merge translation strings.
LANG_SRC="$REPO_ROOT/sources/lang"
LANG_DST="$APP_DIR/assets/game/lang"
if [[ -d "$LANG_SRC" ]]; then
    for src_file in "$LANG_SRC"/*.json; do
        [[ -f "$src_file" ]] || continue
        dst_file="$LANG_DST/$(basename "$src_file")"
        [[ -f "$dst_file" ]] || continue
        # Explicit encoding='utf-8' on every open(): python3's default is the
        # locale's preferred encoding, which is ASCII/C on a minimal/C-locale
        # system and would mangle the lang files' non-ASCII characters
        # (accented letters, the degree sign) on read.
        python3 -c "
import json, sys
with open(sys.argv[1], encoding='utf-8') as f: src = json.load(f)
with open(sys.argv[2], encoding='utf-8') as f: dst = json.load(f)
dst.update(src)
with open(sys.argv[2], 'w', encoding='utf-8') as f: json.dump(dst, f, ensure_ascii=False, indent='\t'); f.write('\n')
" "$src_file" "$dst_file"
    done
fi

# Validate the staged assets: zero-byte or truncated files from a bad
# vanilla extraction surface in-game as opaque GL crashes ("blur.vsh ...
# unexpected \$end at <EOF>"). Fail the package instead.
EMPTY_ASSETS=$(find "$APP_DIR/assets" -type f -size 0 ! -name 'version-*.txt' || true)
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

# 6. Rebrand: rename launcher, swap icon, rewrite Info.plist.
if [[ -f "$APP_DIR/Vintagestory" ]]; then
    mv "$APP_DIR/Vintagestory" "$APP_DIR/Optimum"
    chmod +x "$APP_DIR/Optimum"
else
    echo "Warning: launcher 'Vintagestory' not found in bundle." >&2
fi

cp -f "$ICNS" "$APP_DIR/Icon.icns"

cat > "$APP_DIR/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>ATSApplicationFontsPath</key><string>assets/game/fonts</string>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
    <key>CFBundleDisplayName</key><string>Optimum</string>
    <key>CFBundleExecutable</key><string>Optimum</string>
    <key>CFBundleIconFile</key><string>Icon.icns</string>
    <key>CFBundleIdentifier</key><string>at.vintagestory.optimum</string>
    <key>CFBundleName</key><string>Optimum</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>${VERSION}</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
    <key>LSMinimumSystemVersion</key><string>12.2</string>
    <key>LSSupportsGameMode</key><true/>
    <key>NSHighResolutionCapable</key><false/>
    <key>NSHumanReadableCopyright</key><string>Optimum is a fork of Vintage Story (c) Anego Studios</string>
</dict>
</plist>
EOF

echo "Bundle ready: $APP_DIR"

# 7. Build the .dmg.
DMG="$OUTPUT_DIR/Optimum-v${OPT_VER}-mac-${ARCH}.dmg"
rm -f "$DMG"

OS_TYPE="$(uname -s)"

if [[ "$OS_TYPE" == "Darwin" ]] && command -v hdiutil &>/dev/null; then
    # macOS: hdiutil produces a notarizable .dmg.
    DMG_STAGE="$OUTPUT_DIR/_dmg-${ARCH}"
    rm -rf "$DMG_STAGE"
    mkdir -p "$DMG_STAGE"
    cp -a "$APP_DIR" "$DMG_STAGE/Optimum.app"
    ln -s /Applications "$DMG_STAGE/Applications"
    hdiutil create -volname 'Optimum' -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG"
    rm -rf "$DMG_STAGE"
    echo "Done: $DMG"
else
    # Linux: use mkisofs/genisoimage + libdmg-hfsplus dmg tool.
    # This produces a hybrid ISO/HFS image wrapped as DMG. macOS mounts it.
    # For a fully native HFS+ dmg, build on macOS with hdiutil.
    MKISO=""
    if command -v mkisofs &>/dev/null; then MKISO="mkisofs"
    elif command -v genisoimage &>/dev/null; then MKISO="genisoimage"
    fi

    DMG_TOOL="$REPO_ROOT/.tools/libdmg-hfsplus/dmg/dmg"

    # Build libdmg-hfsplus if needed and possible.
    if [[ -n "$MKISO" && ! -f "$DMG_TOOL" ]] && command -v cmake &>/dev/null && command -v git &>/dev/null; then
        echo "Building libdmg-hfsplus (one time)..."
        LDH_SRC="$REPO_ROOT/.tools/libdmg-hfsplus"
        if [[ ! -d "$LDH_SRC" ]]; then
            git clone --depth 1 https://github.com/fanquake/libdmg-hfsplus.git "$LDH_SRC"
        fi
        (cd "$LDH_SRC" && cmake -DCMAKE_POLICY_VERSION_MINIMUM=3.5 . >/dev/null && make >/dev/null)
    fi

    if [[ -n "$MKISO" && -f "$DMG_TOOL" ]]; then
        ISO="$OUTPUT_DIR/_optimum_${ARCH}.iso"
        rm -f "$ISO"
        $MKISO -hfs -V 'Optimum' -D -no-pad -r -file-mode 0755 -o "$ISO" "$APP_DIR" 2>/dev/null
        "$DMG_TOOL" "$ISO" "$DMG" >/dev/null
        rm -f "$ISO"
        echo "Warning: built an UNSIGNED .dmg (hybrid ISO/HFS). macOS mounts it, but Gatekeeper warns. Right-click > Open to launch. Build on macOS with hdiutil for a notarizable .dmg." >&2
        echo "Done: $DMG"
    else
        # Fallback: tar.gz of the .app bundle.
        TGZ="$OUTPUT_DIR/Optimum-v${OPT_VER}-mac-${ARCH}.tar.gz"
        rm -f "$TGZ"
        tar -czf "$TGZ" -C "$OUTPUT_DIR" 'Optimum.app'
        if [[ -z "$MKISO" ]]; then
            echo "Warning: need mkisofs or genisoimage. Install: sudo apt install cdrtools (or genisoimage)" >&2
        fi
        if [[ ! -f "$DMG_TOOL" ]]; then
            echo "Warning: need cmake + git to build libdmg-hfsplus." >&2
        fi
        echo "Warning: no .dmg toolchain available. Wrote $TGZ instead." >&2
    fi
fi
