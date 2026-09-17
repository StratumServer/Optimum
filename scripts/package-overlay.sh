#!/usr/bin/env bash
# Packages the Optimum per-release patch overlay and machine-readable manifest
# for consumption by RiftLauncher and other launcher integrations (Issue #71 / #457).
#
# Contains:
#   - Optimum.Patcher + Mono.Cecil
#   - Optimum.Cli (optimum binary)
#   - Optimum.Api.Contracts.dll
#   - .optimum/donors/ (VintagestoryLib.Donor.dll, VintagestoryAPI.Contracts.dll, etc.)
#   - Shaders and language strings
#   - optimum-manifest.json (hashes, supported game versions, targets)
#
# DOES NOT contain any proprietary Anego game binaries or decompiled vanilla code.
#
# Usage:
#   ./scripts/package-overlay.sh
#   ./scripts/package-overlay.sh --rid win-x64 --output dist
#   ./scripts/package-overlay.sh --version 0.3.14

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Defaults
RID="linux-x64"
OUTPUT_DIR="$REPO_ROOT/dist"
OPT_VER="$(tr -d '[:space:]' < "$REPO_ROOT/VERSION" 2>/dev/null || echo "dev")"
GAME_VER="$(python3 -c "import json;print(json.load(open('$REPO_ROOT/forks.json'))['vintageStoryVersion'])" 2>/dev/null || echo "1.22.7")"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)          RID="$2"; shift 2 ;;
        --output)       OUTPUT_DIR="$2"; shift 2 ;;
        --version)      OPT_VER="$2"; shift 2 ;;
        --game-version) GAME_VER="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

echo "Packaging Optimum patch overlay:"
echo "  Optimum version: $OPT_VER"
echo "  Target RID:      $RID"
echo "  Game version:    $GAME_VER"
echo "  Output dir:      $OUTPUT_DIR"

# Ensure build outputs exist
DONOR_LIB="$REPO_ROOT/build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll"
CONTRACTS_LIB="$REPO_ROOT/bin/Release/net10.0/Optimum.Api.Contracts.dll"
ESSENTIALS_LIB="$REPO_ROOT/.build/runtime-donors/VSEssentials/bin/Release/net10.0/VSEssentials.dll"
SURVIVAL_LIB="$REPO_ROOT/.build/runtime-donors/VSSurvivalMod/bin/Release/net10.0/VSSurvivalMod.dll"

if [[ ! -f "$DONOR_LIB" || ! -f "$CONTRACTS_LIB" ]]; then
    echo "Building VintageStory.slnx (Release)..."
    dotnet build "$REPO_ROOT/VintageStory.slnx" -c Release --nologo
fi

if [[ ! -f "$ESSENTIALS_LIB" || ! -f "$SURVIVAL_LIB" ]]; then
    echo "Preparing runtime donors..."
    bash "$REPO_ROOT/scripts/prepare-runtime-donors.sh"
fi

PATCHER_DIR="$REPO_ROOT/Optimum.Patcher/bin/Release/net10.0"
if [[ ! -f "$PATCHER_DIR/Optimum.Patcher.dll" ]]; then
    echo "Building Optimum.Patcher (Release)..."
    dotnet build "$REPO_ROOT/Optimum.Patcher/Optimum.Patcher.csproj" -c Release --nologo
fi

CLI_DIR="$REPO_ROOT/Optimum.Cli/bin/Release/net10.0"
if [[ ! -f "$CLI_DIR/optimum.dll" ]]; then
    echo "Building Optimum.Cli (Release)..."
    dotnet build "$REPO_ROOT/Optimum.Cli/Optimum.Cli.csproj" -c Release --nologo
fi

# Validate shader overlays
if [[ -f "$REPO_ROOT/scripts/validate-shader-assets.sh" && -d "$REPO_ROOT/sources/shaders" ]]; then
    bash "$REPO_ROOT/scripts/validate-shader-assets.sh" "$REPO_ROOT/sources/shaders"
fi

NAME="Optimum-v${OPT_VER}-${RID}-overlay"
STAGE_DIR="$OUTPUT_DIR/$NAME"
rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR/.optimum/donors" "$STAGE_DIR/patcher" "$STAGE_DIR/assets/game/shaders" "$STAGE_DIR/assets/game/lang"

echo "Staging overlay contents into $STAGE_DIR..."

# 1. Donors
cp -f "$DONOR_LIB" "$STAGE_DIR/.optimum/donors/VintagestoryLib.Donor.dll"
cp -f "$CONTRACTS_LIB" "$STAGE_DIR/.optimum/donors/VintagestoryAPI.Contracts.dll"
if [[ -f "$ESSENTIALS_LIB" ]]; then
    cp -f "$ESSENTIALS_LIB" "$STAGE_DIR/.optimum/donors/VSEssentials.Donor.dll"
fi
if [[ -f "$SURVIVAL_LIB" ]]; then
    cp -f "$SURVIVAL_LIB" "$STAGE_DIR/.optimum/donors/VSSurvivalMod.Donor.dll"
fi

# 2. Contracts at root
cp -f "$CONTRACTS_LIB" "$STAGE_DIR/Optimum.Api.Contracts.dll"
if [[ -f "$REPO_ROOT/bin/Release/net10.0/Optimum.Api.Contracts.pdb" ]]; then
    cp -f "$REPO_ROOT/bin/Release/net10.0/Optimum.Api.Contracts.pdb" "$STAGE_DIR/"
fi

# 3. Patcher tool
cp -f "$PATCHER_DIR"/* "$STAGE_DIR/patcher/"
# Also copy patcher DLL and dependencies to root for seamless discovery
cp -f "$PATCHER_DIR"/Mono.Cecil*.dll "$STAGE_DIR/" 2>/dev/null || true
cp -f "$PATCHER_DIR"/Optimum.Patcher.* "$STAGE_DIR/" 2>/dev/null || true

# 4. CLI tool
cp -f "$CLI_DIR"/* "$STAGE_DIR/"
if [[ -f "$STAGE_DIR/optimum" ]]; then
    chmod +x "$STAGE_DIR/optimum"
fi

# 5. Shaders and the includes they compile against. The TAA resolve, the liquid velocity
# pass and every motion writer include the same files, so a shader that ships without its
# include reads vectors nobody wrote: the two directories go together, and a source file
# that never reached the staged assets fails the package.
SHADER_SRC="$REPO_ROOT/sources/shaders"
SHADER_DST="$STAGE_DIR/assets/game/shaders"
SHADER_INC_SRC="$REPO_ROOT/sources/shaderincludes"
SHADER_INC_DST="$STAGE_DIR/assets/game/shaderincludes"
mkdir -p "$SHADER_INC_DST"
if [[ -d "$SHADER_SRC" ]]; then
    find "$SHADER_SRC" -maxdepth 1 -type f -exec cp -f {} "$SHADER_DST/" \;
fi
if [[ -d "$SHADER_INC_SRC" ]]; then
    find "$SHADER_INC_SRC" -maxdepth 1 -type f -exec cp -f {} "$SHADER_INC_DST/" \;
fi
MISSING_SHADERS=""
for pair in "$SHADER_SRC|$SHADER_DST" "$SHADER_INC_SRC|$SHADER_INC_DST"; do
    src="${pair%%|*}"
    dst="${pair##*|}"
    [[ -d "$src" ]] || continue
    while IFS= read -r -d '' f; do
        [[ -f "$dst/$(basename "$f")" ]] || MISSING_SHADERS="$MISSING_SHADERS $f"
    done < <(find "$src" -maxdepth 1 -type f -print0)
done
if [[ -n "$MISSING_SHADERS" ]]; then
    echo "Error: shader source file(s) never reached the staged assets:$MISSING_SHADERS" >&2
    exit 1
fi

# 6. Language strings
if [[ -d "$REPO_ROOT/sources/lang" ]]; then
    find "$REPO_ROOT/sources/lang" -maxdepth 1 -name '*.json' -exec cp -f {} "$STAGE_DIR/assets/game/lang/" \;
fi

# 7. Version marker
echo "$OPT_VER" > "$STAGE_DIR/.optimum/version"

# 8. Legal check: Ensure no proprietary assemblies are present
FORBIDDEN_FILES=("$STAGE_DIR/Vintagestory.dll" "$STAGE_DIR/VintagestoryLib.dll" "$STAGE_DIR/VintagestoryAPI.dll" "$STAGE_DIR/Mods/VSEssentials.dll")
for forbidden in "${FORBIDDEN_FILES[@]}"; do
    if [[ -f "$forbidden" ]]; then
        echo "Error: Forbidden proprietary binary detected in overlay: $forbidden" >&2
        exit 1
    fi
done

# 9. Compute hashes and generate optimum-manifest.json
echo "Generating optimum-manifest.json..."
python3 - <<PY
import os
import sys
import hashlib
import json

stage_dir = "$STAGE_DIR"
output_dir = "$OUTPUT_DIR"
opt_ver = "$OPT_VER"
game_ver = "$GAME_VER"
rid = "$RID"
archive_filename = f"Optimum-v{opt_ver}-{rid}-overlay.tar.gz"

files_record = []
for root, _, files in os.walk(stage_dir):
    for f in files:
        full_path = os.path.join(root, f)
        rel_path = os.path.relpath(full_path, stage_dir).replace("\\\\", "/")
        size = os.path.getsize(full_path)
        with open(full_path, "rb") as fp:
            sha256 = hashlib.sha256(fp.read()).hexdigest()
        files_record.append({
            "path": rel_path,
            "size": size,
            "sha256": f"sha256:{sha256}"
        })

files_record.sort(key=lambda x: x["path"])

targets = [
    {
        "assembly": "VintagestoryLib.dll",
        "donor": "VintagestoryLib.Donor.dll",
        "mode": "transplant"
    },
    {
        "assembly": "VintagestoryAPI.dll",
        "donor": "VintagestoryAPI.Contracts.dll",
        "mode": "api"
    },
    {
        "assembly": "Mods/VSEssentials.dll",
        "donor": "VSEssentials.Donor.dll",
        "mode": "mod",
        "modName": "vsessentials"
    },
    {
        "assembly": "Mods/VSSurvivalMod.dll",
        "donor": "VSSurvivalMod.Donor.dll",
        "mode": "mod",
        "modName": "vssurvivalmod"
    }
]

manifest = {
    "manifestVersion": 1,
    "optimumVersion": opt_ver,
    "supportedGameVersions": [game_ver],
    "rid": rid,
    "archive": {
        "filename": archive_filename,
        "size": 0,
        "sha256": ""
    },
    "targets": targets,
    "files": files_record
}

manifest_path = os.path.join(stage_dir, "optimum-manifest.json")
with open(manifest_path, "w", encoding="utf-8") as fp:
    json.dump(manifest, fp, indent=2)
PY

# 10. Archive packaging
ARCHIVE_PATH="$OUTPUT_DIR/${NAME}.tar.gz"
echo "Creating archive: $ARCHIVE_PATH"
tar -czf "$ARCHIVE_PATH" -C "$OUTPUT_DIR" "$NAME"

# Update archive size and sha256 in manifest
ARCHIVE_SIZE=$(wc -c < "$ARCHIVE_PATH" | tr -d ' ')
ARCHIVE_SHA256="sha256:$(sha256sum "$ARCHIVE_PATH" | cut -d' ' -f1)"

python3 - <<PY
import json

manifest_path = "$STAGE_DIR/optimum-manifest.json"
with open(manifest_path, "r", encoding="utf-8") as fp:
    manifest = json.load(fp)

manifest["archive"]["size"] = int("$ARCHIVE_SIZE")
manifest["archive"]["sha256"] = "$ARCHIVE_SHA256"

with open(manifest_path, "w", encoding="utf-8") as fp:
    json.dump(manifest, fp, indent=2)

# Also write to dist/optimum-manifest.json next to the tarball
dist_manifest = "$OUTPUT_DIR/optimum-manifest.json"
with open(dist_manifest, "w", encoding="utf-8") as fp:
    json.dump(manifest, fp, indent=2)
PY

echo ""
echo "Done! Produced overlay archive and manifest:"
echo "  Archive:  $ARCHIVE_PATH ($ARCHIVE_SIZE bytes, $ARCHIVE_SHA256)"
echo "  Manifest: $OUTPUT_DIR/optimum-manifest.json"
