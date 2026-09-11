#!/bin/bash
# Frame-time capture for the TAA acceptance matrix (TAA-PLAN.md, P5).
#
# Launches the deployed client through scripts/dev/run-client.sh on a chosen
# renderer with TAA on or off, waits for the world ("[Client Chat] Welcome") plus
# a warm-up, records a fixed window of frame times, closes the client through
# scripts/dev/kill-client.sh and prints mean and 1% low frame time.
#
# Sources, both written by the client itself (no external overlay needed):
#   OPTIMUM_FPS_LOG      one line per second, both backends
#                        "[Optimum] fps window=.. frames=.. mean=.. min=.. max=.. p99=.. stddev=.."
#                        (ClientMain.OptimumLogFrameTime; inert unless the var is set;
#                        stddev is optional so logs from older builds still parse)
#   OPTIMUM_VULKAN_STATS one sample per second, Vulkan only (VulkanStats.SampleIfDue):
#                        "stats <s>s: <n> frames (<ms> ms/frame), ..." plus the
#                        stats.pacing / stats.waits / stats.counters key=value lines
#
# The pass/fail verdict on pacing is scripts/dev/pacing-gate.sh, run over the same
# two files; this script only measures and summarises.
#
# Usage:
#   scripts/dev/perf-capture.sh --renderer vulkan|opengl [options]
# Options:
#   --renderer <vulkan|opengl>  required; also rewritten into optimum.json by run-client.sh
#   --taa <on|off>              rewrite OptimumConfig "Taa" before launching (default: leave as is)
#   --vsync <on|off>            set clientsettings "vsyncMode" for the run and restore it after;
#                               use off for cost measurements, a vsync-capped run measures the monitor
#   --world <name>              bare save name passed to run-client.sh (default: "serene cave world")
#   --seconds <n>               measurement window (default 30)
#   --warmup <n>                seconds after the Welcome line before measuring (default 8)
#   --wait <n>                  seconds to wait for the Welcome line (default 180)
#   --label <name>              label for the output directory and the summary line
#   --out <dir>                 output directory (default /tmp/optimum-perf/<label>)
#
# This script never pattern-kills anything (rule 5): closing goes through
# scripts/dev/kill-client.sh, which is the only place that owns that pattern.
set -euo pipefail

RENDERER_ARG=""
TAA_ARG=""
VSYNC_ARG=""
WORLD="serene cave world"
SECONDS_WINDOW=30
WARMUP=8
WAIT_FOR_WORLD=180
LABEL=""
OUT_DIR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --renderer) RENDERER_ARG="${2:-}"; shift 2;;
    --taa)      TAA_ARG="${2:-}"; shift 2;;
    --vsync)    VSYNC_ARG="${2:-}"; shift 2;;
    --world)    WORLD="${2:-}"; shift 2;;
    --seconds)  SECONDS_WINDOW="${2:-}"; shift 2;;
    --warmup)   WARMUP="${2:-}"; shift 2;;
    --wait)     WAIT_FOR_WORLD="${2:-}"; shift 2;;
    --label)    LABEL="${2:-}"; shift 2;;
    --out)      OUT_DIR="${2:-}"; shift 2;;
    -h|--help)  sed -n '2,32p' "${BASH_SOURCE[0]}"; exit 0;;
    *) echo "unknown argument: $1" >&2; exit 2;;
  esac
done

case "$RENDERER_ARG" in
  vulkan|opengl) ;;
  *) echo "--renderer vulkan|opengl is required (got '${RENDERER_ARG}')" >&2; exit 2;;
esac
case "$TAA_ARG" in
  ""|on|off) ;;
  *) echo "--taa takes on or off (got '${TAA_ARG}')" >&2; exit 2;;
esac
# Same check for --vsync: without it "--vsync 0" or "--vsync fals" silently means
# vsync ON, and a vsync-capped run measures the monitor, not the renderer.
case "$VSYNC_ARG" in
  ""|on|off) ;;
  *) echo "--vsync takes on or off (got '${VSYNC_ARG}')" >&2; exit 2;;
esac

REPO="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
DATA_PATH="${DATA_PATH:-$HOME/.config/OptimumVintagestoryData}"
CONFIG="$DATA_PATH/ModConfig/optimum.json"
LABEL="${LABEL:-${RENDERER_ARG}-taa-${TAA_ARG:-asconfigured}}"
OUT_DIR="${OUT_DIR:-/tmp/optimum-perf/$LABEL}"
LOG="$OUT_DIR/client.log"
FPS_LOG="$OUT_DIR/fps.log"
VK_STATS="$OUT_DIR/vulkan-stats.log"

mkdir -p "$OUT_DIR" || exit 1
# run-client.sh changes into the client directory before it opens CLIENT_LOG, so a relative
# --out would put the log (and the fps and stats logs the client writes) somewhere else.
OUT_DIR="$(cd -- "$OUT_DIR" && pwd)" || exit 1
LOG="$OUT_DIR/client.log"
FPS_LOG="$OUT_DIR/fps.log"
VK_STATS="$OUT_DIR/vulkan-stats.log"
rm -f "$LOG" "$FPS_LOG" "$VK_STATS"

# 1. TAA on/off through the config file, before the launch rewrites Renderer.
if [[ -n "$TAA_ARG" ]]; then
  if ! python3 - "$CONFIG" "$TAA_ARG" <<'PY'
import json, sys
path, value = sys.argv[1], sys.argv[2] == "on"
with open(path) as handle:
    data = json.load(handle)
data["Taa"] = value
with open(path, "w") as handle:
    json.dump(data, handle, indent=2)
PY
  then
    echo "failed to set Taa=$TAA_ARG in $CONFIG; not launching" >&2
    exit 1
  fi
  echo "config: Taa=$TAA_ARG"
fi

# 1b. vsync for the run, restored on exit whatever happens next.
CLIENTSETTINGS="$DATA_PATH/clientsettings.json"
VSYNC_SAVED=""
set_vsync() {
  python3 -c 'import json,sys
path, value = sys.argv[1], int(sys.argv[2])
data = json.load(open(path))
data["vsyncMode"] = value
json.dump(data, open(path, "w"), indent=2)' "$CLIENTSETTINGS" "$1"
}
restore_vsync() {
  if [[ -n "$VSYNC_SAVED" ]]; then set_vsync "$VSYNC_SAVED" || true; fi
}
if [[ -n "$VSYNC_ARG" ]]; then
  VSYNC_SAVED="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("vsyncMode", 1))' "$CLIENTSETTINGS")" || exit 1
  trap restore_vsync EXIT
  WANT=1; [[ "$VSYNC_ARG" == "off" ]] && WANT=0
  set_vsync "$WANT" || { echo "failed to set vsyncMode in $CLIENTSETTINGS; not launching" >&2; exit 1; }
  echo "clientsettings: vsyncMode=$WANT (was $VSYNC_SAVED)"
fi

# Renderer is rewritten by run-client.sh; restore the user's value on every exit path.
SAVED_RENDERER="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("Renderer",""))' "$CONFIG")" || exit 1
restore_renderer() {
  [[ -n "$SAVED_RENDERER" ]] || return 0
  python3 -c 'import json,sys; p,v=sys.argv[1],sys.argv[2]; d=json.load(open(p)); d["Renderer"]=v; json.dump(d,open(p,"w"),indent=2)' "$CONFIG" "$SAVED_RENDERER" || true
}
restore_all() { restore_vsync; restore_renderer; }
trap restore_all EXIT

# 2. Launch. The client writes both logs itself; run-client.sh rewrites Renderer.
export OPTIMUM_FPS_LOG="$FPS_LOG"
if [[ "$RENDERER_ARG" == "vulkan" ]]; then
  export OPTIMUM_VULKAN_STATS="$VK_STATS"
else
  unset OPTIMUM_VULKAN_STATS
fi
CLIENT_LOG="$LOG" RENDERER="$RENDERER_ARG" bash "$REPO/scripts/dev/run-client.sh" "$WORLD" || exit 1

# 3. Wait for the world. A launch is not a verification (rule 1): the renderer is
#    confirmed from the log below, never assumed from the argument.
client_alive() {
  # No -q: grep reads all of ps's output, so pipefail never sees ps die of SIGPIPE.
  ps -eo cmd | grep "dotnet [V]intagestory.dll" >/dev/null
}
deadline=$((SECONDS + WAIT_FOR_WORLD))
ready=0
while (( SECONDS < deadline )); do
  if grep -q "\[Client Chat\] Welcome" "$LOG" 2>/dev/null; then ready=1; break; fi
  if ! client_alive; then
    # The process is gone, so the log is complete: one last look, then stop waiting.
    if grep -q "\[Client Chat\] Welcome" "$LOG" 2>/dev/null; then ready=1; break; fi
    echo "the client exited before '[Client Chat] Welcome' appeared; see $LOG" >&2
    grep -m1 -E "Exception|Fatal" "$LOG" >&2 || true
    exit 1
  fi
  sleep 2
done
if (( ready == 0 )); then
  echo "no '[Client Chat] Welcome' line within ${WAIT_FOR_WORLD}s; see $LOG" >&2
  bash "$REPO/scripts/dev/kill-client.sh" >/dev/null 2>&1
  exit 1
fi

# "|| true": with pipefail a missing line must reach the refusal below, not end the
# script with the client still running.
ACTUAL_RENDERER="$(grep -m1 -oE "\[Optimum\] (Vulkan|OpenGL) renderer" "$LOG" | awk '{print $2}' || true)"
if [[ -z "$ACTUAL_RENDERER" ]]; then
  echo "no '[Optimum] <backend> renderer' line in $LOG; refusing to report numbers" >&2
  bash "$REPO/scripts/dev/kill-client.sh" >/dev/null 2>&1
  exit 1
fi
if [[ "${ACTUAL_RENDERER,,}" != "$RENDERER_ARG" ]]; then
  echo "asked for $RENDERER_ARG but the client started on $ACTUAL_RENDERER (silent fallback); refusing to report numbers" >&2
  bash "$REPO/scripts/dev/kill-client.sh" >/dev/null 2>&1
  exit 1
fi

# 4. Warm up, then measure a fixed window. Warm-up lines stay in the file and are
#    skipped by line offset, so nothing truncates a file the client is appending to.
sleep "$WARMUP"
fps_offset=$(wc -l < "$FPS_LOG" 2>/dev/null || echo 0)
vk_offset=0; [[ -f "$VK_STATS" ]] && vk_offset=$(wc -l < "$VK_STATS")
sleep "$SECONDS_WINDOW"

# 5. Close the client before parsing: never leave the game running (rule 5).
bash "$REPO/scripts/dev/kill-client.sh"

# 6. Summarise.
python3 - "$FPS_LOG" "$fps_offset" "$VK_STATS" "$vk_offset" "$LABEL" "$ACTUAL_RENDERER" "$TAA_ARG" "$OUT_DIR" <<'PY'
import os, re, sys

fps_log, fps_offset, vk_log, vk_offset, label, renderer, taa, out_dir = sys.argv[1:9]
fps_offset, vk_offset = int(fps_offset), int(vk_offset)

def tail(path, offset):
    if not os.path.exists(path):
        return []
    with open(path, errors="replace") as handle:
        return handle.read().splitlines()[offset:]

# Must stay equal to FPS_LINE_RE in pacing-gate.sh (Optimum.Tests/pacing-log-format-coverage-tests.cs).
FPS_LINE_RE = re.compile(r"\[Optimum\] fps window=(?P<window>[\d.]+) frames=(?P<frames>\d+) mean=(?P<mean>[\d.]+) min=(?P<min>[\d.]+) max=(?P<max>[\d.]+) p99=(?P<p99>[\d.]+)(?: stddev=(?P<stddev>[\d.]+))?")

windows = [m.groupdict() for m in (FPS_LINE_RE.search(l) for l in tail(fps_log, fps_offset)) if m]
if not windows:
    print("no [Optimum] fps lines in the measurement window of " + fps_log, file=sys.stderr)
    print("is the client built with the OPTIMUM_FPS_LOG patch and was the var exported?", file=sys.stderr)
    sys.exit(1)

frames = sum(int(w["frames"]) for w in windows)
total_ms = sum(float(w["mean"]) * int(w["frames"]) for w in windows)
mean_ms = total_ms / frames
# Per-second p99 is the slowest ~1% of that second's frames. "1% low frame time"
# here is the mean of those per-window p99 values; the worst window is printed
# beside it so a single stall is visible rather than averaged away.
p99s = [float(w["p99"]) for w in windows]
low_ms = sum(p99s) / len(p99s)
worst_ms = max(float(w["max"]) for w in windows)
# Per-second stddev (the client computes it over the same window as p99); the median
# across windows, so one hitch does not stand in for the whole run. Old logs have none.
stddevs = sorted(float(w["stddev"]) for w in windows if w.get("stddev") is not None)
stddev_ms = None
if stddevs and len(stddevs) == len(windows):
    middle = len(stddevs) // 2
    stddev_ms = stddevs[middle] if len(stddevs) % 2 else (stddevs[middle - 1] + stddevs[middle]) / 2.0

# VulkanStats writes "stats 1.0s: 120 frames (8.4 ms/frame), ..."; take the last sample.
vk_frame_ms = None
for line in tail(vk_log, vk_offset):
    m = re.search(r"^stats [\d.]+s: \d+ frames \(([\d.]+) ms/frame\)", line)
    if m:
        vk_frame_ms = float(m.group(1))

print("")
print("label            " + label)
print("renderer         " + renderer + "  (confirmed from the client log)")
print("taa              " + (taa or "as configured"))
print("windows          %d seconds, %d frames" % (len(windows), frames))
print("mean frame time  %.3f ms  (%.1f fps)" % (mean_ms, 1000.0 / mean_ms))
print("1%% low frame time %.3f ms  (%.1f fps)" % (low_ms, 1000.0 / low_ms))
print("worst frame      %.3f ms" % worst_ms)
if stddev_ms is not None:
    print("frame stddev     %.3f ms  (median of per-second windows)" % stddev_ms)
else:
    print("frame stddev     -  (fps log has no stddev field; client built before it)")
if vk_frame_ms is not None:
    print("vulkan stats     last sample %.1f ms/frame (%s)" % (vk_frame_ms, vk_log))
print("pacing verdict   scripts/dev/pacing-gate.sh --renderer %s --fps %s%s"
      % (renderer.lower(), fps_log, (" --stats " + vk_log) if renderer.lower() == "vulkan" else ""))
print("logs             " + out_dir)

summary = os.path.join(out_dir, "summary.csv")
with open(summary, "w") as handle:
    handle.write("label,renderer,taa,windows,frames,mean_ms,low1pct_ms,worst_ms,stddev_ms\n")
    handle.write("%s,%s,%s,%d,%d,%.3f,%.3f,%.3f,%s\n"
                 % (label, renderer, taa or "asconfigured", len(windows), frames, mean_ms, low_ms, worst_ms,
                    "%.3f" % stddev_ms if stddev_ms is not None else ""))
print("summary          " + summary)
PY
