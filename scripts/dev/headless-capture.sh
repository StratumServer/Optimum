#!/bin/bash
# Headless frame capture: the real client and the real renderer, no visible
# window, frames on disk.
#
# Launches the deployed client on one renderer with OPTIMUM_HEADLESS=1 (the
# window is created with StartVisible/StartFocused false - a real window with a
# real surface and swapchain, never mapped and never focused), waits for the
# world, requires the matching renderer line in the log, waits for the client's
# "[Optimum] headless: <n> frames -> <dir>" line, and closes the client through
# scripts/dev/kill-client.sh.
#
# Frames are written by the client itself through ReadDefaultFramebuffer - the
# same polymorphic call the in-game screenshot makes, a device-side readback on
# Vulkan - so nothing here touches the desktop and no compositor is involved.
# Each selected in-world frame lands as frame-NNNNNN.ppm, which is what
# scripts/dev/ssim.py reads; two captures of the same frame list pair by name:
#   scripts/dev/ssim.py <outA> <outB>
#
# The scene and the camera come from a chat-command script (--commands), fed to
# the client on an in-world frame exactly as if a human had typed it: a line
# starting with "." runs locally (".cam load <points>", ".cam play <seconds>" -
# vanilla's own keyframed camera), anything else goes to the server ("/time set",
# "/weather", "/gamemode"). Author a path once in game with ".cam p" at each
# point and ".cam save" (which puts the point string on the clipboard), then keep
# it in the script file. --fixed-dt pins ClientMain.DeltaTimeLimiter, so every
# simulated frame advances by the same amount however long it really took; that
# makes a sequence repeatable, not bit-exact (chunk streaming, particle and mob
# RNG are not pinned by it).
#
# The run stays out of the way on its own: a permanently unfocused window falls
# under the client's existing background FPS cap (30 FPS), so a capture of N
# frames takes at least N/30 seconds and does not take the machine.
#
# Requires a display (real, nested or Xvfb): GLFW asks for the screen size before
# any window exists and Vulkan needs a WSI surface. "Headless" here means no
# visible window, not no display server.
#
# Usage:
#   scripts/dev/headless-capture.sh --renderer vulkan|opengl --world <name> \
#       --out <dir> [--commands <file>] [--frames <list>|--count <n>]
# Options:
#   --renderer <vulkan|opengl>  required; written into optimum.json "Renderer" for the
#                               run and the previous value restored on exit
#   --world <name>              required; bare save name (not the .vcdbs file name)
#   --out <dir>                 required; created if missing, must hold no frames
#   --commands <file>           chat-command script; dispatched once, on --command-frame
#   --command-frame <n>         in-world frame the script runs on (default 30)
#   --frames <a,b,c>            explicit in-world frames to write
#   --count <n>                 or: this many frames (default 60 when --frames is unset)
#   --stride <s>                with --count: every s-th frame (default 1)
#   --first <f>                 with --count: the first frame (default: the frame after
#                               the command script has run)
#   --fixed-dt <seconds>        pin the simulated frame step (default 0.0166667; 0 = off)
#   --parity-dump               also write the per-attachment dump of --parity-frame,
#                               which is what scripts/dev/taa-rejection.py reads
#   --parity-frame <n>          in-world frame for that dump (default: the first captured frame)
#   --wait <s>                  seconds to wait for the world (default 180)
#   --capture-wait <s>          seconds to wait for the frames after it (default 300)
#
# Exit: 0 frames written on the requested renderer, 1 failure, 2 usage.
# This script never pattern-kills anything (CLAUDE.md rule 5): closing goes
# through scripts/dev/kill-client.sh, which is the only place that owns that pattern.
set -euo pipefail

RENDERER_ARG=""
WORLD=""
OUT_DIR=""
COMMANDS=""
COMMAND_FRAME=30
FRAME_LIST=""
COUNT=""
STRIDE=1
FIRST=""
FIXED_DT="0.0166667"
PARITY_DUMP=0
PARITY_FRAME=""
WAIT_FOR_WORLD=180
WAIT_FOR_CAPTURE=300

while [[ $# -gt 0 ]]; do
  case "$1" in
    --renderer)      RENDERER_ARG="${2:-}"; shift 2;;
    --world)         WORLD="${2:-}"; shift 2;;
    --out)           OUT_DIR="${2:-}"; shift 2;;
    --commands)      COMMANDS="${2:-}"; shift 2;;
    --command-frame) COMMAND_FRAME="${2:-}"; shift 2;;
    --frames)        FRAME_LIST="${2:-}"; shift 2;;
    --count)         COUNT="${2:-}"; shift 2;;
    --stride)        STRIDE="${2:-}"; shift 2;;
    --first)         FIRST="${2:-}"; shift 2;;
    --fixed-dt)      FIXED_DT="${2:-}"; shift 2;;
    --parity-dump)   PARITY_DUMP=1; shift;;
    --parity-frame)  PARITY_FRAME="${2:-}"; shift 2;;
    --wait)          WAIT_FOR_WORLD="${2:-}"; shift 2;;
    --capture-wait)  WAIT_FOR_CAPTURE="${2:-}"; shift 2;;
    -h|--help)       sed -n '2,62p' "${BASH_SOURCE[0]}"; exit 0;;
    *) echo "unknown argument: $1" >&2; exit 2;;
  esac
done

case "$RENDERER_ARG" in
  vulkan) EXPECTED_LINE="[Optimum] Vulkan renderer";;
  opengl) EXPECTED_LINE="[Optimum] OpenGL renderer:";;
  *) echo "--renderer vulkan|opengl is required (got '${RENDERER_ARG}')" >&2; exit 2;;
esac
if [[ -z "$WORLD" ]]; then echo "--world <name> is required" >&2; exit 2; fi
if [[ -z "$OUT_DIR" ]]; then echo "--out <dir> is required" >&2; exit 2; fi
if [[ -n "$COMMANDS" && ! -f "$COMMANDS" ]]; then echo "no such command script: $COMMANDS" >&2; exit 2; fi
if [[ -n "$FRAME_LIST" && -n "$COUNT" ]]; then echo "--frames and --count are exclusive" >&2; exit 2; fi
if [[ -n "$FRAME_LIST" ]] && ! [[ "$FRAME_LIST" =~ ^[0-9]+([,[:space:]]+[0-9]+)*$ ]]; then
  echo "--frames takes non-negative integers separated by commas (got '${FRAME_LIST}')" >&2; exit 2
fi
if [[ -z "$FRAME_LIST" && -z "$COUNT" ]]; then COUNT=60; fi
for pair in "COUNT:$COUNT" "STRIDE:$STRIDE" "COMMAND_FRAME:$COMMAND_FRAME" "FIRST:$FIRST" \
            "PARITY_FRAME:$PARITY_FRAME" "WAIT_FOR_WORLD:$WAIT_FOR_WORLD" "WAIT_FOR_CAPTURE:$WAIT_FOR_CAPTURE"; do
  value="${pair#*:}"
  if [[ -n "$value" ]] && ! [[ "$value" =~ ^[0-9]+$ ]]; then
    echo "${pair%%:*} takes a non-negative integer (got '${value}')" >&2; exit 2
  fi
done
if ! [[ "$FIXED_DT" =~ ^[0-9]*\.?[0-9]+$ ]]; then
  echo "--fixed-dt takes seconds, e.g. 0.0166667 (got '${FIXED_DT}')" >&2; exit 2
fi

REPO="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
DATA_PATH="${DATA_PATH:-$HOME/.config/OptimumVintagestoryData}"
CONFIG="$DATA_PATH/ModConfig/optimum.json"

mkdir -p "$OUT_DIR" || exit 1
OUT_DIR="$(cd -- "$OUT_DIR" && pwd)" || exit 1   # the client requires an absolute path
if compgen -G "$OUT_DIR/frame-*.ppm" >/dev/null || compgen -G "$OUT_DIR/*.p[pgf]m" >/dev/null; then
  echo "$OUT_DIR already holds frames; use an empty directory so stale files cannot pair" >&2
  exit 1
fi
LOG="$OUT_DIR/client.log"
rm -f "$LOG"

# The first frame the capture writes, needed here only to default --parity-frame
# to something inside the captured range.
if [[ -n "$FRAME_LIST" ]]; then
  FIRST_CAPTURED="${FRAME_LIST%%,*}"
  FIRST_CAPTURED="${FIRST_CAPTURED//[[:space:]]/}"
elif [[ -n "$FIRST" ]]; then
  FIRST_CAPTURED="$FIRST"
elif [[ -n "$COMMANDS" ]]; then
  FIRST_CAPTURED=$((COMMAND_FRAME + 1))
else
  FIRST_CAPTURED=0
fi
if [[ -z "$PARITY_FRAME" ]]; then PARITY_FRAME="$FIRST_CAPTURED"; fi

# 1. Renderer for this run, restored on exit whatever happens next.
SAVED_RENDERER="$(python3 -c 'import json,sys; print(json.dumps(json.load(open(sys.argv[1])).get("Renderer")))' "$CONFIG")" || {
  echo "cannot read $CONFIG; not launching" >&2; exit 1; }
set_renderer() {
  python3 -c 'import json,sys
path, value = sys.argv[1], json.loads(sys.argv[2])
data = json.load(open(path))
if value is None:
    data.pop("Renderer", None)
else:
    data["Renderer"] = value
json.dump(data, open(path, "w"), indent=2)' "$CONFIG" "$1"
}
client_alive() {
  # No -q: grep reads all of ps's output, so pipefail never sees ps die of SIGPIPE.
  ps -eo cmd | grep "dotnet [V]intagestory.dll" >/dev/null
}

wait_for_exit() {
  local deadline=$((SECONDS + $1))
  while client_alive; do
    if (( SECONDS >= deadline )); then return 1; fi
    sleep 1
  done
  return 0
}

LAUNCHED=0
CLOSED=0
cleanup() {
  if (( LAUNCHED == 1 && CLOSED == 0 )); then
    bash "$REPO/scripts/dev/kill-client.sh" >/dev/null 2>&1 || true
    CLOSED=1
  fi
  if (( LAUNCHED == 1 )); then
    wait_for_exit 60 || echo "the client is still running after 60 s; restoring Renderer anyway" >&2
  fi
  set_renderer "$SAVED_RENDERER" || echo "failed to restore Renderer=$SAVED_RENDERER in $CONFIG" >&2
}
trap cleanup EXIT
trap 'exit 130' INT TERM

set_renderer "\"$RENDERER_ARG\"" || { echo "failed to set Renderer=$RENDERER_ARG in $CONFIG; not launching" >&2; exit 1; }
echo "config: Renderer=$RENDERER_ARG (was $SAVED_RENDERER)"

# 2. Launch headless. RENDERER is unset so run-client.sh does not rewrite the
#    config a second time.
export OPTIMUM_HEADLESS=1
export OPTIMUM_HEADLESS_FRAMES="$OUT_DIR"
export OPTIMUM_HEADLESS_COMMAND_FRAME="$COMMAND_FRAME"
if [[ -n "$COMMANDS" ]]; then
  export OPTIMUM_HEADLESS_COMMANDS="$(cd -- "$(dirname -- "$COMMANDS")" && pwd)/$(basename -- "$COMMANDS")"
fi
if [[ -n "$FRAME_LIST" ]]; then
  export OPTIMUM_HEADLESS_FRAME_LIST="${FRAME_LIST// /}"
else
  export OPTIMUM_HEADLESS_FRAME_COUNT="$COUNT"
  export OPTIMUM_HEADLESS_FRAME_STRIDE="$STRIDE"
  if [[ -n "$FIRST" ]]; then export OPTIMUM_HEADLESS_FIRST_FRAME="$FIRST"; fi
fi
if [[ "$FIXED_DT" != "0" ]]; then export OPTIMUM_HEADLESS_FIXED_DT="$FIXED_DT"; fi
if (( PARITY_DUMP == 1 )); then
  export OPTIMUM_PARITY_DUMP="$OUT_DIR"
  export OPTIMUM_PARITY_FRAME="$PARITY_FRAME"
fi

LAUNCHED=1
env -u RENDERER CLIENT_LOG="$LOG" bash "$REPO/scripts/dev/run-client.sh" "$WORLD" || exit 1

# wait_for <fixed string> <seconds>: polls the log, fails early when the client exits.
wait_for() {
  local needle="$1" deadline=$((SECONDS + $2))
  while (( SECONDS < deadline )); do
    if grep -qF -- "$needle" "$LOG" 2>/dev/null; then return 0; fi
    if ! client_alive; then
      grep -qF -- "$needle" "$LOG" 2>/dev/null && return 0
      echo "the client exited before '$needle' appeared; see $LOG" >&2
      return 1
    fi
    sleep 2
  done
  echo "no '$needle' line within $2 s; see $LOG" >&2
  return 1
}

# 3. The world, then the renderer. A launch is not a verification (rule 1).
wait_for "[Client Chat] Welcome" "$WAIT_FOR_WORLD" || exit 1

RENDERER_LINE="$(grep -m1 -E "\[Optimum\] (Vulkan renderer|OpenGL renderer:)" "$LOG" || true)"
if [[ -z "$RENDERER_LINE" ]]; then
  echo "no '[Optimum] <backend> renderer' line in $LOG; refusing to report a capture" >&2
  exit 1
fi
if [[ "$RENDERER_LINE" != *"$EXPECTED_LINE"* ]]; then
  echo "asked for $RENDERER_ARG but the log says: $RENDERER_LINE (silent fallback); refusing to report a capture" >&2
  exit 1
fi

# 4. The frames.
wait_for "[Optimum] headless: " "$WAIT_FOR_CAPTURE" || exit 1
wait_for " frames -> " "$WAIT_FOR_CAPTURE" || exit 1
CAPTURE_LINE="$(grep -m1 -F " frames -> " "$LOG" || true)"
COMMAND_LINE="$(grep -m1 -F " commands dispatched" "$LOG" || true)"

# 5. Close the client before reporting: never leave the game running (rule 5).
bash "$REPO/scripts/dev/kill-client.sh"
CLOSED=1

FILES=$(find "$OUT_DIR" -maxdepth 1 -type f -name 'frame-*.ppm' | wc -l)
echo ""
echo "renderer   $RENDERER_LINE"
if [[ -n "$COMMAND_LINE" ]]; then echo "commands   $COMMAND_LINE"; fi
echo "capture    $CAPTURE_LINE"
echo "frames     $FILES in $OUT_DIR"
echo "log        $LOG"
if (( FILES == 0 )); then
  echo "the capture line appeared but no frames were written" >&2
  exit 1
fi
echo "compare    scripts/dev/ssim.py <other capture> $OUT_DIR"
if (( PARITY_DUMP == 1 )); then
  echo "rejection  scripts/dev/taa-rejection.py $OUT_DIR"
fi
