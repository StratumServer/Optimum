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
  python3 - "$DATA_PATH/ModConfig/optimum.json" "$RENDERER" <<'PY'
import json,sys
p,r=sys.argv[1],sys.argv[2]; d=json.load(open(p)); d['Renderer']=r; json.dump(d,open(p,'w'),indent=2)
PY
fi
cd "$CLIENT" || exit 1
setsid prime-run dotnet Vintagestory.dll --dataPath "$DATA_PATH" -o "$WORLD" > "$LOG" 2>&1 < /dev/null &
disown
echo "launched; log: $LOG"
