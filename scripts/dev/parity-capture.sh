#!/bin/bash
# Per-attachment parity capture (docs/vulkan-acceptance.md, section 3).
#
# Launches the deployed client on one renderer with OPTIMUM_PARITY_DUMP=<out>
# and OPTIMUM_PARITY_FRAME=<n>, waits for the world ("[Client Chat] Welcome"),
# requires the matching renderer line in the log, waits for the client's
# "[Optimum] parity dump: <count> attachments -> <dir>" line, and closes the
# client through scripts/dev/kill-client.sh. It sends no chat commands.
#
# The client dumps every attachment of every framebuffer slot once, on in-world
# frame <n> (0 = the first frame rendered with the player in the world), after
# the post chain and the final blit and before presentation. Compare two
# captures with:
#   scripts/dev/ssim.py <outA> <outB> --allowlist docs/parity-allowlist.md
#
# Usage:
#   scripts/dev/parity-capture.sh --renderer vulkan|opengl --world <name> --frame <n> --out <dir>
# Options:
#   --renderer <vulkan|opengl>  required; written into optimum.json "Renderer" for the
#                               run and the previous value restored on exit
#   --world <name>              required; bare save name (not the .vcdbs file name)
#   --frame <n>                 required; in-world frame to dump, n >= 0
#   --out <dir>                 required; created if missing, must hold no dump files
#   --wait <s>                  seconds to wait for the Welcome line (default 180)
#   --dump-wait <s>             seconds to wait for the dump line after it (default 120)
#
# Exit: 0 dump written on the requested renderer, 1 failure, 2 usage.
# This script never pattern-kills anything (CLAUDE.md rule 5): closing goes
# through scripts/dev/kill-client.sh, which is the only place that owns that pattern.
set -euo pipefail

RENDERER_ARG=""
WORLD=""
FRAME=""
OUT_DIR=""
WAIT_FOR_WORLD=180
WAIT_FOR_DUMP=120

while [[ $# -gt 0 ]]; do
  case "$1" in
    --renderer)  RENDERER_ARG="${2:-}"; shift 2;;
    --world)     WORLD="${2:-}"; shift 2;;
    --frame)     FRAME="${2:-}"; shift 2;;
    --out)       OUT_DIR="${2:-}"; shift 2;;
    --wait)      WAIT_FOR_WORLD="${2:-}"; shift 2;;
    --dump-wait) WAIT_FOR_DUMP="${2:-}"; shift 2;;
    -h|--help)   sed -n '2,29p' "${BASH_SOURCE[0]}"; exit 0;;
    *) echo "unknown argument: $1" >&2; exit 2;;
  esac
done

case "$RENDERER_ARG" in
  vulkan) EXPECTED_LINE="[Optimum] Vulkan renderer";;
  opengl) EXPECTED_LINE="[Optimum] OpenGL renderer:";;
  *) echo "--renderer vulkan|opengl is required (got '${RENDERER_ARG}')" >&2; exit 2;;
esac
if [[ -z "$WORLD" ]]; then echo "--world <name> is required" >&2; exit 2; fi
if ! [[ "$FRAME" =~ ^[0-9]+$ ]]; then echo "--frame takes a non-negative integer (got '${FRAME}')" >&2; exit 2; fi
if [[ -z "$OUT_DIR" ]]; then echo "--out <dir> is required" >&2; exit 2; fi
if ! [[ "$WAIT_FOR_WORLD" =~ ^[0-9]+$ && "$WAIT_FOR_DUMP" =~ ^[0-9]+$ ]]; then
  echo "--wait and --dump-wait take whole seconds" >&2; exit 2
fi

REPO="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
DATA_PATH="${DATA_PATH:-$HOME/.config/OptimumVintagestoryData}"
CONFIG="$DATA_PATH/ModConfig/optimum.json"

mkdir -p "$OUT_DIR" || exit 1
OUT_DIR="$(cd -- "$OUT_DIR" && pwd)" || exit 1   # the client requires an absolute path
if compgen -G "$OUT_DIR/*.p[pgf]m" >/dev/null; then
  echo "$OUT_DIR already holds dump files; use an empty directory so stale files cannot pair" >&2
  exit 1
fi
LOG="$OUT_DIR/client.log"
rm -f "$LOG"

# 1. Renderer for this run, restored on exit whatever happens next. The client is
#    closed before the restore, so a config write on shutdown cannot undo it.
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

# wait_for_exit <seconds>: polls until no client process is left. kill-client.sh
# returns without waiting for the process to go.
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
  # The restore waits for the process to be gone, so nothing the client still
  # does on its way out can rewrite the config after it.
  if (( LAUNCHED == 1 )); then
    wait_for_exit 60 || echo "the client is still running after 60 s; restoring Renderer anyway" >&2
  fi
  set_renderer "$SAVED_RENDERER" || echo "failed to restore Renderer=$SAVED_RENDERER in $CONFIG" >&2
}
trap cleanup EXIT
trap 'exit 130' INT TERM

set_renderer "\"$RENDERER_ARG\"" || { echo "failed to set Renderer=$RENDERER_ARG in $CONFIG; not launching" >&2; exit 1; }
echo "config: Renderer=$RENDERER_ARG (was $SAVED_RENDERER)"

# 2. Launch with the dump switched on. RENDERER is unset so run-client.sh does not
#    rewrite the config a second time.
export OPTIMUM_PARITY_DUMP="$OUT_DIR"
export OPTIMUM_PARITY_FRAME="$FRAME"
# LAUNCHED is set first: if the launch itself fails half-way, cleanup still closes
# whatever started (kill-client.sh is a no-op when nothing runs).
LAUNCHED=1
env -u RENDERER CLIENT_LOG="$LOG" bash "$REPO/scripts/dev/run-client.sh" "$WORLD" || exit 1

# wait_for <fixed string> <seconds>: polls the log, fails early when the client exits.
wait_for() {
  local needle="$1" deadline=$((SECONDS + $2))
  while (( SECONDS < deadline )); do
    if grep -qF -- "$needle" "$LOG" 2>/dev/null; then return 0; fi
    if ! client_alive; then
      # The process is gone, so the log is complete: one last look, no delay.
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

# 4. The dump line.
wait_for "[Optimum] parity dump:" "$WAIT_FOR_DUMP" || exit 1
DUMP_LINE="$(grep -m1 -F "[Optimum] parity dump:" "$LOG" || true)"

# 5. Close the client before reporting: never leave the game running (rule 5).
bash "$REPO/scripts/dev/kill-client.sh"
CLOSED=1

FILES=$(find "$OUT_DIR" -maxdepth 1 -type f \( -name '*.ppm' -o -name '*.pgm' -o -name '*.pfm' \) | wc -l)
echo ""
echo "renderer   $RENDERER_LINE"
echo "dump       $DUMP_LINE"
echo "files      $FILES in $OUT_DIR"
echo "log        $LOG"
if (( FILES == 0 )); then
  echo "the dump line appeared but no files were written" >&2
  exit 1
fi
echo "compare    scripts/dev/ssim.py <other capture> $OUT_DIR --allowlist docs/parity-allowlist.md"
