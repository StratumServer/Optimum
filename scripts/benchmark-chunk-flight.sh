#!/usr/bin/env bash
# Chunk flight benchmark: a fresh seeded world per run, a scripted creative flight in a straight line
# over normal terrain, and per frame data from the ChunkFlightMeter mod (every arm), from the client
# (OPTIMUM_CHUNK_STREAM_LOG, Optimum builds) and optionally from MangoHud. Arms are compared run by run.
#
# How a run works, identically for a vanilla and an Optimum client, with no human input:
#   1. --rndWorld creates a new world from --seed with the playstyle given by -p. -p takes the playstyle
#      langCode, not its code: "preset-exploration" is the standard hilly worldgen with passive creatures
#      and no temporal storms. An unknown code silently leaves the client in the main menu ("surviving"
#      did that), and "creativebuilding" is superflat. Every run generates its chunks from scratch in a
#      throwaway data directory that is removed afterwards, so nothing carries over.
#   2. The ChunkFlightMeter mod (tools/ChunkFlightMeter, pass its build directory with --mod-path) prepares
#      the session from CHUNKFLIGHT_SETUP: it closes the character dialog a new survival world opens and
#      unpauses the game, then runs /gamemode creative, .freemove 1, .noclip 1, .movespeed N, /time set 12:00,
#      /time stop, /weather acp 0, /weather setprecip -1 and an absolute /tp, and holds the heading. It logs
#      every server reply and one "setup complete" line, which this script checks against --setup.
#   3. Warm-up: at least --warmup seconds, then until --settle-windows consecutive five second meter
#      windows saw no chunk column loaded by the server and no chunk received by the client, and the frame
#      count of the last two windows differs by at most 10 percent (the client finished building the area).
#   4. Flight: xdotool holds Control_L (sprint) and w. After a --ramp the measured window lasts --duration.
#      /movespeed 3 flies about 26.7 blocks per second, about 1600 blocks in 60 s, which both clients can
#      still stream at a 512 block view distance; /movespeed 5 (44 blocks/s) outruns chunk loading.
#   5. scripts/analyze-chunk-flight.py reads the logged positions: a run below --min-blocks is INVALID.
# A failed run stops the client, removes the data directory, keeps the logs and prints where they are.
#
# Usage: bash scripts/benchmark-chunk-flight.sh [options]
#   --arm NAME=DIR[@CONF]   client directory to measure, repeatable, with an optional optimum.json for that arm
#                           (default: optimum=.vanilla/linux-x64/vintagestory)
#   --duration SECONDS      measured flight length (default 60)
#   --runs N                runs per arm (default 5), arms rotate so none always goes first
#   --seed N                world seed (default 20261111)
#   --playstyle LANGCODE    world playstyle langCode (default preset-exploration, normal terrain)
#   --setup X,Y,Z,YAW[,SPEED]  absolute start block position, heading in degrees (0 flies towards +Z, south)
#                           and /movespeed (default 512000,200,512000,0,3)
#   --warmup SECONDS        minimum idle time after setup (default 30)
#   --warmup-max SECONDS    give up when the start area has not settled by then (default 300)
#   --settle-windows N      quiet meter windows that end the warm-up (default 3)
#   --ramp SECONDS          flight time before the measured window (default 5)
#   --min-blocks N          a run that flies fewer blocks is reported INVALID (default 22 per second of --duration)
#   --view-distance N       view distance in blocks (default 512)
#   --optimum-config FILE   optimum.json copied into every run
#   --gpu-wrapper CMD       command that puts the client on the GPU (default: vglrun -d egl0 -c proxy)
#   --mod-path DIR          directory with extra client mods, e.g. the frame meter, repeatable
#   --mangohud              also log MangoHud (it does not hook a client run through VirtualGL)
#   --require-upload-visibility  mark each run INVALID unless complete visibility telemetry is present
#   --no-optimum-stream-log record a telemetry-disabled control run
#   --allow-software-gl     measure on a software renderer (the run is refused otherwise)
#   --output DIR            output directory
# Environment: DISPLAY_NUM (default :99), RESOLUTION (default 1920x1080), SETTINGS_SOURCE.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DURATION="${DURATION:-60}"
RUNS="${RUNS:-5}"
SEED="${SEED:-20261111}"
PLAYSTYLE="${PLAYSTYLE:-preset-exploration}"
SETUP="${SETUP:-512000,200,512000,0,3}"
WARMUP="${WARMUP:-30}"
WARMUP_MAX="${WARMUP_MAX:-300}"
SETTLE_WINDOWS="${SETTLE_WINDOWS:-3}"
RAMP="${RAMP:-5}"
LOAD_TIMEOUT="${LOAD_TIMEOUT:-240}"
MIN_BLOCKS="${MIN_BLOCKS:-}"
VIEW_DISTANCE="${VIEW_DISTANCE:-512}"
DISPLAY_NUM="${DISPLAY_NUM:-:99}"
RESOLUTION="${RESOLUTION:-1920x1080}"
OUTPUT_DIR="${OUTPUT_DIR:-/tmp/optimum-flight-bench-$(date +%Y%m%d-%H%M%S)}"
SETTINGS_SOURCE="${SETTINGS_SOURCE:-$HOME/.config/VintagestoryData/clientsettings.json}"
OPTIMUM_CONFIG="${OPTIMUM_CONFIG:-}"
USE_MANGOHUD="${USE_MANGOHUD:-0}"
ALLOW_SOFTWARE_GL="${ALLOW_SOFTWARE_GL:-0}"
REQUIRE_UPLOAD_VISIBILITY="${REQUIRE_UPLOAD_VISIBILITY:-0}"
OPTIMUM_STREAM_LOG="${OPTIMUM_STREAM_LOG:-1}"
GPU_WRAPPER="${GPU_WRAPPER-auto}"
ARM_SPECS=()
MOD_PATHS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arm) ARM_SPECS+=("$2"); shift 2 ;;
        --duration) DURATION="$2"; shift 2 ;;
        --runs) RUNS="$2"; shift 2 ;;
        --seed) SEED="$2"; shift 2 ;;
        --playstyle) PLAYSTYLE="$2"; shift 2 ;;
        --setup) SETUP="$2"; shift 2 ;;
        --warmup) WARMUP="$2"; shift 2 ;;
        --warmup-max) WARMUP_MAX="$2"; shift 2 ;;
        --settle-windows) SETTLE_WINDOWS="$2"; shift 2 ;;
        --ramp) RAMP="$2"; shift 2 ;;
        --min-blocks) MIN_BLOCKS="$2"; shift 2 ;;
        --view-distance) VIEW_DISTANCE="$2"; shift 2 ;;
        --optimum-config) OPTIMUM_CONFIG="$2"; shift 2 ;;
        --gpu-wrapper) GPU_WRAPPER="$2"; shift 2 ;;
        --mod-path) MOD_PATHS+=("$2"); shift 2 ;;
        --mangohud) USE_MANGOHUD=1; shift ;;
        --no-mangohud) USE_MANGOHUD=0; shift ;;
        --require-upload-visibility) REQUIRE_UPLOAD_VISIBILITY=1; OPTIMUM_STREAM_LOG=1; shift ;;
        --no-optimum-stream-log) OPTIMUM_STREAM_LOG=0; shift ;;
        --allow-software-gl) ALLOW_SOFTWARE_GL=1; shift ;;
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,/^# Environment:/p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "ERROR: unknown option $1" >&2; exit 2 ;;
    esac
done

if [[ "$REQUIRE_UPLOAD_VISIBILITY" == "1" && "$OPTIMUM_STREAM_LOG" != "1" ]]; then
    echo "ERROR: --require-upload-visibility needs Optimum chunk stream logging." >&2
    exit 2
fi
if [[ "$REQUIRE_UPLOAD_VISIBILITY" == "1" && ( "$DURATION" != "60" || "$RUNS" != "6" ) ]]; then
    echo "ERROR: the issue #550 visibility gate requires exactly six 60-second runs." >&2
    exit 2
fi

# About 82 percent of the calibrated 26.7 blocks per second.
[[ -n "$MIN_BLOCKS" ]] || MIN_BLOCKS=$((DURATION * 22))

if [[ ${#ARM_SPECS[@]} -eq 0 ]]; then
    ARM_SPECS=("optimum=$REPO_ROOT/.vanilla/linux-x64/vintagestory")
fi

for tool in Xvfb xdotool scrot python3; do
    command -v "$tool" >/dev/null 2>&1 || { echo "ERROR: $tool is required." >&2; exit 1; }
done
[[ "$USE_MANGOHUD" == "1" ]] && ! command -v mangohud >/dev/null 2>&1 && { echo "ERROR: mangohud is required for --mangohud." >&2; exit 1; }
[[ -f "$SETTINGS_SOURCE" ]] || { echo "ERROR: $SETTINGS_SOURCE not found. The client needs a cached session to leave the login screen." >&2; exit 1; }

WRAP=()
if [[ "$GPU_WRAPPER" == "auto" ]]; then
    if command -v vglrun >/dev/null 2>&1; then WRAP=(vglrun -d egl0 -c proxy); fi
elif [[ -n "$GPU_WRAPPER" ]]; then
    read -r -a WRAP <<< "$GPU_WRAPPER"
fi

WIDTH="${RESOLUTION%x*}"
HEIGHT="${RESOLUTION#*x}"
mkdir -p "$OUTPUT_DIR/runs"

XVFB_PID=""
if ! xdpyinfo -display "$DISPLAY_NUM" >/dev/null 2>&1; then
    rm -f "/tmp/.X${DISPLAY_NUM#:}-lock"
    Xvfb "$DISPLAY_NUM" -screen 0 "${RESOLUTION}x24" +extension GLX +render -noreset -nolisten tcp >/dev/null 2>&1 &
    XVFB_PID=$!
    sleep 2
fi

CURRENT_GROUP=""
CURRENT_RUN_DIR=""
# Stops the client of the current run, keeps its logs and drops the throwaway data directory, which
# holds a copy of the cached session and the generated world. Runs after every run and from the exit
# trap, so a failed run cleans up too.
end_run() {
    if [[ -n "$CURRENT_GROUP" ]]; then
        kill -TERM -- "-$CURRENT_GROUP" 2>/dev/null || true
        for _ in $(seq 1 15); do kill -0 "$CURRENT_GROUP" 2>/dev/null || break; sleep 1; done
        kill -KILL -- "-$CURRENT_GROUP" 2>/dev/null || true
        # The client often dies with a segfault on SIGTERM, after the measurement: keep bash quiet about it.
        { wait "$CURRENT_GROUP"; } 2>/dev/null || true
        CURRENT_GROUP=""
    fi
    if [[ -n "$CURRENT_RUN_DIR" && -d "$CURRENT_RUN_DIR/data" ]]; then
        cp "$CURRENT_RUN_DIR/data/Logs/"client-main.log "$CURRENT_RUN_DIR/data/Logs/"client-chat.log "$CURRENT_RUN_DIR/data/Logs/"server-main.log "$CURRENT_RUN_DIR/" 2>/dev/null || true
        rm -rf -- "$CURRENT_RUN_DIR/data"
    fi
    CURRENT_RUN_DIR=""
}
cleanup() {
    end_run
    [[ -n "$XVFB_PID" ]] && kill "$XVFB_PID" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT TERM

GL_INFO="$(DISPLAY="$DISPLAY_NUM" "${WRAP[@]}" glxinfo -B 2>/dev/null || true)"
RENDERER="$(sed -n 's/^OpenGL renderer string: //p' <<< "$GL_INFO" | head -n 1)"
OPENGL_VENDOR="$(sed -n 's/^OpenGL vendor string: //p' <<< "$GL_INFO" | head -n 1)"
OPENGL_VERSION="$(sed -n 's/^OpenGL core profile version string: //p' <<< "$GL_INFO" | head -n 1)"
echo "GL renderer: ${RENDERER:-unknown}"
if [[ "$ALLOW_SOFTWARE_GL" != "1" ]] && [[ -z "$RENDERER" || "$RENDERER" =~ [Ll]lvmpipe|[Ss]oftpipe|[Ss]wrast ]]; then
    echo "ERROR: the client would render on '${RENDERER:-no renderer}'. Software frame times say nothing about the GPU path. Use a GPU wrapper or pass --allow-software-gl." >&2
    exit 3
fi

{
    echo "date=$(date -Is)"
    echo "revision=$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
    echo "dirty=$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null | wc -l)"
    echo "renderer=$RENDERER"
    echo "opengl_vendor=$OPENGL_VENDOR"
    echo "opengl_version=$OPENGL_VERSION"
    echo "gpu_wrapper=${WRAP[*]:-none}"
    echo "kernel=$(uname -sr)"
    echo "cpu=$(LC_ALL=C lscpu 2>/dev/null | sed -n 's/^Model name:[[:space:]]*//p' | head -n 1)"
    echo "dotnet=$(dotnet --version 2>/dev/null || echo unknown)"
    echo "duration=$DURATION runs=$RUNS seed=$SEED playstyle=$PLAYSTYLE setup=$SETUP warmup=$WARMUP settleWindows=$SETTLE_WINDOWS ramp=$RAMP minBlocks=$MIN_BLOCKS viewDistance=$VIEW_DISTANCE resolution=$RESOLUTION mangohud=$USE_MANGOHUD"
    printf 'arm=%s\n' "${ARM_SPECS[@]}"
} > "$OUTPUT_DIR/manifest.txt"

write_settings() {
    python3 - "$SETTINGS_SOURCE" "$1" "$VIEW_DISTANCE" "$WIDTH" "$HEIGHT" <<'PY'
import json, sys
source, target, view, width, height = sys.argv[1:6]
with open(source, encoding="utf-8") as handle:
    settings = json.load(handle)
ints = settings.setdefault("intSettings", {})
ints["viewDistance"] = int(view)
ints["vsyncMode"] = 0
for key in ("musicLevel", "soundLevel", "entitySoundLevel", "ambientSoundLevel", "weatherSoundLevel", "masterSoundLevel"):
    ints[key] = 0
ints["screenWidth"] = int(width)
ints["screenHeight"] = int(height)
with open(target, "w", encoding="utf-8") as handle:
    json.dump(settings, handle)
PY
}

last_pause_state() {
    grep -o "Client pause state is now \(on\|off\)" "$1" 2>/dev/null | tail -n 1 | awk '{print $NF}'
}

# Prints the failure with the log to read, keeps a screenshot, and fails the whole benchmark. The
# exit trap then stops the client and removes the data directory; the logs stay in the run directory.
fail_run() {
    DISPLAY="$DISPLAY_NUM" scrot "$CURRENT_RUN_DIR/failure.png" 2>/dev/null || true
    echo "ERROR: $1" >&2
    echo "       logs: $CURRENT_RUN_DIR/client-main.log (and stdout.log, server-main.log, failure.png)" >&2
    exit 1
}

# Value of key= in the last ChunkFlightMeter window line of a log, or nothing.
last_window_value() {
    grep "ChunkFlightMeter: frameUs=" "$1" 2>/dev/null | tail -n 1 | grep -o "[ ,]$2=[-0-9.,]*" | head -n 1 | cut -d= -f2 | sed 's/,$//' || true
}

run_one() {
    local arm="$1" dir="$2" index="$3"
    local run_dir="$OUTPUT_DIR/runs/$arm-$(printf '%02d' "$index")"
    local data="$run_dir/data"
    local log="$data/Logs/client-main.log"
    [[ -f "$dir/Vintagestory.dll" ]] || { echo "ERROR: $dir has no Vintagestory.dll" >&2; exit 1; }
    rm -rf -- "$run_dir"
    mkdir -p "$data/Logs" "$run_dir/mangohud"
    CURRENT_RUN_DIR="$run_dir"
    write_settings "$data/clientsettings.json"
    local config="${4:-$OPTIMUM_CONFIG}"
    mkdir -p "$data/ModConfig"
    python3 - "$config" "$data/ModConfig/optimum.json" <<'PY'
import json, os, sys
source, target = sys.argv[1:3]
settings = {}
if source and os.path.exists(source):
    with open(source, encoding="utf-8") as handle:
        settings = json.load(handle)
# An unfocused Xvfb window would otherwise drop Optimum to its background frame limit.
settings["BackgroundFpsLimit"] = False
with open(target, "w", encoding="utf-8") as handle:
    json.dump(settings, handle)
PY

    mkdir -p "$run_dir/evidence"
    cp "$data/ModConfig/optimum.json" "$run_dir/evidence/optimum.json"
    python3 - "$data/clientsettings.json" "$run_dir/evidence/clientsettings-effective.json" <<'PY'
import json, sys
source, target = sys.argv[1:3]
with open(source, encoding="utf-8") as handle:
    settings = json.load(handle)
int_settings = settings.get("intSettings", {})
keys = (
    "viewDistance", "vsyncMode", "screenWidth", "screenHeight", "musicLevel", "soundLevel",
    "entitySoundLevel", "ambientSoundLevel", "weatherSoundLevel", "masterSoundLevel",
    "chunkVerticesUploadRateLimiter",
)
with open(target, "w", encoding="utf-8") as handle:
    json.dump({key: int_settings.get(key) for key in keys}, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY
sha256sum "$data/clientsettings.json" | awk '{print $1 "  clientsettings.json"}' > "$run_dir/evidence/config.sha256"
sha256sum "$run_dir/evidence/optimum.json" | awk '{print $1 "  optimum.json"}' >> "$run_dir/evidence/config.sha256"
while IFS= read -r -d '' assembly; do
    assembly_hash="$(sha256sum "$assembly" | cut -d' ' -f1)"
    printf '%s  %s\n' "$assembly_hash" "${assembly#"$dir"/}"
done < <(find "$dir" -maxdepth 1 -type f -name '*.dll' -print0 | sort -z) > "$run_dir/evidence/assemblies.sha256"
for mod_path in "${MOD_PATHS[@]}"; do
    while IFS= read -r -d '' assembly; do
        assembly_hash="$(sha256sum "$assembly" | cut -d' ' -f1)"
        printf '%s  %s\n' "$assembly_hash" "${assembly#"$mod_path"/}"
    done < <(find "$mod_path" -maxdepth 2 -type f -name '*.dll' -print0 | sort -z) >> "$run_dir/evidence/mod-assemblies.sha256"
done

    echo "[$arm #$index] launching $dir"
    local mango=() mods=()
    if [[ "$USE_MANGOHUD" == "1" ]]; then mango=(mangohud --dlsym); fi
    for mod_path in "${MOD_PATHS[@]}"; do mods+=(--addModPath "$mod_path"); done
    (
        cd "$dir"
        export DISPLAY="$DISPLAY_NUM" CHUNKFLIGHT_SETUP="$SETUP"
        if [[ "$OPTIMUM_STREAM_LOG" == "1" ]]; then
            export OPTIMUM_CHUNK_STREAM_LOG=1
        else
            unset OPTIMUM_CHUNK_STREAM_LOG
        fi
        if [[ "$USE_MANGOHUD" == "1" ]]; then
            export MANGOHUD=1 MANGOHUD_CONFIG="output_folder=$run_dir/mangohud,autostart_log=1,log_interval=0,no_display"
        fi
        exec setsid "${WRAP[@]}" "${mango[@]}" dotnet Vintagestory.dll --dataPath "$data" --logPath "$data/Logs" --rndWorld --seed "$SEED" -p "$PLAYSTYLE" "${mods[@]}"
    ) > "$run_dir/stdout.log" 2>&1 &
    local pid=$!
    CURRENT_GROUP="$pid"

    local ready=0
    for _ in $(seq 1 "$LOAD_TIMEOUT"); do
        kill -0 "$pid" 2>/dev/null || fail_run "the client exited before the world loaded"
        if grep -q "Received level finalize" "$log" 2>/dev/null; then ready=1; break; fi
        sleep 1
    done
    [[ "$ready" == 1 ]] || fail_run "the world did not load within ${LOAD_TIMEOUT}s. --playstyle must be a playstyle langCode (preset-exploration, preset-surviveandbuild, creativebuilding, ...); an unknown code leaves the client in the main menu"

    # The meter mod closes the character dialog, switches to creative free flight, freezes time and
    # weather, teleports and sets the heading, then logs one line with what it reached.
    local setup_line=""
    for _ in $(seq 1 90); do
        kill -0 "$pid" 2>/dev/null || fail_run "the client exited during the session setup"
        setup_line="$(grep -m 1 "ChunkFlightMeter: setup complete" "$log" 2>/dev/null || true)"
        [[ -n "$setup_line" ]] && break
        sleep 1
    done
    [[ -n "$setup_line" ]] || fail_run "the meter mod never finished the session setup (is --mod-path pointing at the built ChunkFlightMeter?)"
    python3 - "$SETUP" "$setup_line" <<'PY' || fail_run "the session setup did not reach the requested state: ${setup_line#*ChunkFlightMeter: }"
import math, re, sys
want = [float(v) for v in sys.argv[1].split(",")]
line = sys.argv[2]
pos = [float(v) for v in re.search(r"pos=(-?[0-9.]+),(-?[0-9.]+),(-?[0-9.]+)", line).groups()]
yaw = float(re.search(r"yawDeg=(-?[0-9.]+)", line).group(1))
ok = all(abs(a - b) <= 2.0 for a, b in zip(pos, want[:3]))
ok = ok and abs((yaw - want[3] + 180.0) % 360.0 - 180.0) <= 1.0
ok = ok and "mode=Creative" in line and "freeMove=True" in line and "paused=False" in line
if len(want) > 4:
    ok = ok and abs(float(re.search(r"moveSpeed=([0-9.]+)", line).group(1)) - want[4]) < 0.01
sys.exit(0 if ok else 1)
PY
    echo "[$arm #$index] ${setup_line#*ChunkFlightMeter: }"

    local window
    window="$(DISPLAY="$DISPLAY_NUM" xdotool search --onlyvisible --name "Vintage Story" 2>/dev/null | head -n 1 || true)"
    [[ -n "$window" ]] && DISPLAY="$DISPLAY_NUM" xdotool windowfocus "$window" 2>/dev/null || true
    for _ in 1 2 3; do
        [[ "$(last_pause_state "$log")" == "on" ]] || break
        DISPLAY="$DISPLAY_NUM" xdotool key Escape
        sleep 2
    done

    # Warm-up: at least --warmup seconds, then until the area around the start is complete, which is
    # --settle-windows consecutive five second meter windows in which the server loaded no chunk column
    # and the client received no chunk. Both counters come from the meter mod, so they exist in every arm.
    local waited=0 quiet=0 seen=0 lines settled=0 frames=0 previous_frames=0 steady=0
    while (( waited < WARMUP_MAX )); do
        kill -0 "$pid" 2>/dev/null || fail_run "the client exited during the warm-up"
        lines="$(grep -c "ChunkFlightMeter: frameUs=" "$log" 2>/dev/null || true)"
        if (( lines > seen )); then
            seen="$lines"
            previous_frames="$frames"
            frames="$(grep "ChunkFlightMeter: frameUs=" "$log" | tail -n 1 | sed -n 's/.*frameUs=n\([0-9]*\).*/\1/p')"
            frames="${frames:-0}"
            # Steady when the frame count moved by at most 10 percent against the window before.
            if (( previous_frames > 0 && (frames - previous_frames) * 10 <= previous_frames && (previous_frames - frames) * 10 <= previous_frames )); then steady=1; else steady=0; fi
            if [[ "$(last_window_value "$log" cols)" == "0" && "$(last_window_value "$log" chunks)" == "0" ]]; then quiet=$((quiet + 1)); else quiet=0; fi
        fi
        if (( waited >= WARMUP && quiet >= SETTLE_WINDOWS && steady == 1 )); then settled=1; break; fi
        sleep 1
        waited=$((waited + 1))
    done
    [[ "$settled" == 1 ]] || fail_run "the start area had not settled (no chunks arriving and a steady frame rate) after ${WARMUP_MAX}s of warm-up"
    echo "[$arm #$index] warm-up settled after ${waited}s, start pos=$(last_window_value "$log" pos)"
    DISPLAY="$DISPLAY_NUM" scrot "$run_dir/1_before_flight.png" 2>/dev/null || true

    # Sprint is Control, forward is w. Free movement and the speed were set by the meter mod.
    local start end
    DISPLAY="$DISPLAY_NUM" xdotool keydown Control_L keydown w
    sleep "$RAMP"
    start="$(date +%s.%3N)"
    # Keep captures outside the timed interval; a synchronous screenshot stalls the measured client.
    sleep "$DURATION"
    kill -0 "$pid" 2>/dev/null || fail_run "the client exited during the flight"
    end="$(date +%s.%3N)"
    DISPLAY="$DISPLAY_NUM" scrot "$run_dir/3_after_flight.png" 2>/dev/null || true
    DISPLAY="$DISPLAY_NUM" xdotool keyup w keyup Control_L
    # One more window so the last flight window is in the log.
    sleep 6
    echo "[$arm #$index] flight done, end pos=$(last_window_value "$log" pos)"

    {
        echo "ARM=$arm"
        echo "DIR=$dir"
        echo "FLIGHT_START_EPOCH=$start"
        echo "FLIGHT_END_EPOCH=$end"
        echo "RENDERER=$RENDERER"
        echo "SEED=$SEED"
        echo "PLAYSTYLE=$PLAYSTYLE"
        echo "SETUP=$SETUP"
        echo "WARMUP_SECONDS=$waited"
        echo "MIN_BLOCKS=$MIN_BLOCKS"
        echo "REQUIRE_UPLOAD_VISIBILITY=$REQUIRE_UPLOAD_VISIBILITY"
        echo "OPTIMUM_STREAM_LOG=$OPTIMUM_STREAM_LOG"
        echo "RESOLUTION=$RESOLUTION"
        echo "VIEW_DISTANCE=$VIEW_DISTANCE"
        echo "GPU_WRAPPER=${WRAP[*]:-none}"
        echo "OPENGL_VENDOR=$OPENGL_VENDOR"
        echo "OPENGL_RENDERER=$RENDERER"
        echo "OPENGL_VERSION=$OPENGL_VERSION"
    } > "$run_dir/markers.env"
    end_run
}

NAMES=(); DIRS=(); CONFS=()
for spec in "${ARM_SPECS[@]}"; do
    rest="${spec#*=}"
    NAMES+=("${spec%%=*}")
    DIRS+=("${rest%%@*}")
    if [[ "$rest" == *@* ]]; then CONFS+=("${rest#*@}"); else CONFS+=(""); fi
done
COUNT=${#NAMES[@]}
for ((run = 1; run <= RUNS; run++)); do
    for ((slot = 0; slot < COUNT; slot++)); do
        arm=$(( (slot + run - 1) % COUNT ))
        run_one "${NAMES[$arm]}" "${DIRS[$arm]}" "$run" "${CONFS[$arm]}"
    done
done

python3 "$REPO_ROOT/scripts/analyze-chunk-flight.py" "$OUTPUT_DIR"
echo "Artifacts written to: $OUTPUT_DIR"
