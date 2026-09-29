#!/bin/bash
# Launch the deployed client (after `make deploy`) into a save, detached from the
# calling shell so tool timeouts cannot kill it. Usage:
#   scripts/dev/run-client.sh [world-name] [extra env...]
# Env: RENDERER=vulkan|opengl (rewrites ModConfig/optimum.json), DATA_PATH,
#      OPTIMUM_VULKAN_VALIDATION=1, OPTIMUM_RENDER_TRACE=<file>,
#      OPTIMUM_DUMP_TEXTURES=<ids> OPTIMUM_DUMP_DIR=<abs dir> OPTIMUM_DUMP_AFTER_SECONDS=<n>
# Verify the renderer from the log afterwards: scripts/dev/client-renderer.sh
set -u
WORLD="${1:-serene cave world}"
DATA_PATH="${DATA_PATH:-$HOME/.config/OptimumVintagestoryData}"
REPO="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
CLIENT="$REPO/.vanilla/win-x64/vintagestory"
LOG="${CLIENT_LOG:-/tmp/optimum-client.log}"
if [[ -n "${RENDERER:-}" ]]; then
  # Check the status explicitly - set -e would not help here anyway, and a failed
  # rewrite (missing, invalid or unwritable optimum.json) must abort the launch:
  # starting the client regardless silently runs it on the old renderer, which is
  # exactly the false verification this script exists to prevent.
  if ! python3 - "$DATA_PATH/ModConfig/optimum.json" "$RENDERER" <<'PY'
import json,sys
p,r=sys.argv[1],sys.argv[2]; d=json.load(open(p)); d['Renderer']=r; json.dump(d,open(p,'w'),indent=2)
PY
  then
    echo "failed to set Renderer=$RENDERER in $DATA_PATH/ModConfig/optimum.json; not launching" >&2
    exit 1
  fi
fi
cd "$CLIENT" || exit 1
# PRIME render offload onto the discrete NVIDIA GPU. prime-run is Arch's wrapper and
# is missing on Debian, Ubuntu, Mint, Fedora and openSUSE; the variables it sets are
# the standard ones, so set them directly when the NVIDIA driver is loaded. Without
# that driver they would point GLX at a vendor library that is not installed, so a
# machine without it launches plainly.
LAUNCH=(dotnet)
if command -v prime-run >/dev/null 2>&1; then
  LAUNCH=(prime-run dotnet)
elif [[ -e /proc/driver/nvidia/version ]]; then
  export __NV_PRIME_RENDER_OFFLOAD=1 __GLX_VENDOR_LIBRARY_NAME=nvidia __VK_LAYER_NV_optimus=NVIDIA_only
fi
setsid "${LAUNCH[@]}" Vintagestory.dll --dataPath "$DATA_PATH" -o "$WORLD" > "$LOG" 2>&1 < /dev/null &
disown
echo "launched; log: $LOG"
