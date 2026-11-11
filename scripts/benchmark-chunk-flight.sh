#!/usr/bin/env bash
# Chunk flight benchmark: a fresh seeded world per run, a scripted sprint flight, and per frame
# data from the client (OPTIMUM_CHUNK_STREAM_LOG) and from MangoHud. Arms are compared run by run.
#
# Usage: bash scripts/benchmark-chunk-flight.sh [options]
#   --arm NAME=DIR          client directory to measure, repeatable (default: optimum=.vanilla/linux-x64/vintagestory)
#   --duration SECONDS      flight length (default 60)
#   --runs N                runs per arm (default 5), arms rotate so none always goes first
#   --seed N                world seed (default 20261111)
#   --view-distance N       view distance in blocks (default 512)
#   --optimum-config FILE   optimum.json copied into every run
#   --gpu-wrapper CMD       command that puts the client on the GPU (default: vglrun -d egl0 -c proxy)
#   --mod-path DIR          directory with extra client mods, e.g. the frame meter, repeatable
#   --mangohud              also log MangoHud (it does not hook a client run through VirtualGL)
#   --allow-software-gl     measure on a software renderer (the run is refused otherwise)
#   --output DIR            output directory
# Environment: DISPLAY_NUM (default :99), RESOLUTION (default 1920x1080), SETTINGS_SOURCE.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DURATION="${DURATION:-60}"
RUNS="${RUNS:-5}"
SEED="${SEED:-20261111}"
VIEW_DISTANCE="${VIEW_DISTANCE:-512}"
DISPLAY_NUM="${DISPLAY_NUM:-:99}"
RESOLUTION="${RESOLUTION:-1920x1080}"
OUTPUT_DIR="${OUTPUT_DIR:-/tmp/optimum-flight-bench-$(date +%Y%m%d-%H%M%S)}"
SETTINGS_SOURCE="${SETTINGS_SOURCE:-$HOME/.config/VintagestoryData/clientsettings.json}"
OPTIMUM_CONFIG="${OPTIMUM_CONFIG:-}"
USE_MANGOHUD="${USE_MANGOHUD:-0}"
ALLOW_SOFTWARE_GL="${ALLOW_SOFTWARE_GL:-0}"
GPU_WRAPPER="${GPU_WRAPPER-auto}"
ARM_SPECS=()
MOD_PATHS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arm) ARM_SPECS+=("$2"); shift 2 ;;
        --duration) DURATION="$2"; shift 2 ;;
        --runs) RUNS="$2"; shift 2 ;;
        --seed) SEED="$2"; shift 2 ;;
        --view-distance) VIEW_DISTANCE="$2"; shift 2 ;;
        --optimum-config) OPTIMUM_CONFIG="$2"; shift 2 ;;
        --gpu-wrapper) GPU_WRAPPER="$2"; shift 2 ;;
        --mod-path) MOD_PATHS+=("$2"); shift 2 ;;
        --mangohud) USE_MANGOHUD=1; shift ;;
        --no-mangohud) USE_MANGOHUD=0; shift ;;
        --allow-software-gl) ALLOW_SOFTWARE_GL=1; shift ;;
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "ERROR: unknown option $1" >&2; exit 2 ;;
    esac
done

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
cleanup() {
    [[ -n "$CURRENT_GROUP" ]] && kill -KILL -- "-$CURRENT_GROUP" 2>/dev/null || true
    [[ -n "$XVFB_PID" ]] && kill "$XVFB_PID" 2>/dev/null || true
}
trap cleanup EXIT

RENDERER="$(DISPLAY="$DISPLAY_NUM" "${WRAP[@]}" glxinfo -B 2>/dev/null | sed -n 's/^OpenGL renderer string: //p' | head -n 1)"
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
    echo "gpu_wrapper=${WRAP[*]:-none}"
    echo "kernel=$(uname -sr)"
    echo "cpu=$(lscpu 2>/dev/null | sed -n 's/^Model name:[[:space:]]*//p' | head -n 1)"
    echo "dotnet=$(dotnet --version 2>/dev/null || echo unknown)"
    echo "duration=$DURATION runs=$RUNS seed=$SEED viewDistance=$VIEW_DISTANCE resolution=$RESOLUTION mangohud=$USE_MANGOHUD"
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
ints["screenWidth"] = int(width)
ints["screenHeight"] = int(height)
with open(target, "w", encoding="utf-8") as handle:
    json.dump(settings, handle)
PY
}

last_pause_state() {
    grep -o "Client pause state is now \(on\|off\)" "$1" 2>/dev/null | tail -n 1 | awk '{print $NF}'
}

run_one() {
    local arm="$1" dir="$2" index="$3"
    local run_dir="$OUTPUT_DIR/runs/$arm-$(printf '%02d' "$index")"
    local data="$run_dir/data"
    local log="$data/Logs/client-main.log"
    mkdir -p "$data/Logs" "$run_dir/mangohud"
    write_settings "$data/clientsettings.json"
    if [[ -n "$OPTIMUM_CONFIG" ]]; then
        mkdir -p "$data/ModConfig"
        cp "$OPTIMUM_CONFIG" "$data/ModConfig/optimum.json"
    fi

    echo "[$arm #$index] launching $dir"
    local mango=() mods=()
    if [[ "$USE_MANGOHUD" == "1" ]]; then mango=(mangohud --dlsym); fi
    for mod_path in "${MOD_PATHS[@]}"; do mods+=(--addModPath "$mod_path"); done
    (
        cd "$dir"
        export DISPLAY="$DISPLAY_NUM" OPTIMUM_CHUNK_STREAM_LOG=1
        if [[ "$USE_MANGOHUD" == "1" ]]; then
            export MANGOHUD=1 MANGOHUD_CONFIG="output_folder=$run_dir/mangohud,autostart_log=1,log_interval=0,no_display"
        fi
        exec setsid "${WRAP[@]}" "${mango[@]}" dotnet Vintagestory.dll --dataPath "$data" --logPath "$data/Logs" --rndWorld --seed "$SEED" -p creativebuilding "${mods[@]}"
    ) > "$run_dir/stdout.log" 2>&1 &
    local pid=$!
    CURRENT_GROUP="$pid"

    local ready=0
    for _ in $(seq 1 240); do
        if ! kill -0 "$pid" 2>/dev/null; then echo "ERROR: the client exited early, see $run_dir/stdout.log" >&2; return 1; fi
        if grep -q "Received level finalize" "$log" 2>/dev/null; then ready=1; break; fi
        sleep 1
    done
    [[ "$ready" == 1 ]] || { echo "ERROR: timed out waiting for the world to load" >&2; return 1; }

    sleep 3
    local window
    window="$(DISPLAY="$DISPLAY_NUM" xdotool search --onlyvisible --name "Vintage Story" 2>/dev/null | head -n 1 || true)"
    [[ -n "$window" ]] && DISPLAY="$DISPLAY_NUM" xdotool windowfocus "$window" 2>/dev/null || true
    for _ in 1 2 3; do
        [[ "$(last_pause_state "$log")" == "on" ]] || break
        DISPLAY="$DISPLAY_NUM" xdotool key Escape
        sleep 2
    done
    sleep 5
    DISPLAY="$DISPLAY_NUM" scrot "$run_dir/1_before_flight.png" 2>/dev/null || true

    local start end
    start="$(date +%s)"
    DISPLAY="$DISPLAY_NUM" xdotool key space
    sleep 0.2
    DISPLAY="$DISPLAY_NUM" xdotool key space
    sleep 0.5
    DISPLAY="$DISPLAY_NUM" xdotool keydown Shift_L keydown w
    sleep "$DURATION"
    DISPLAY="$DISPLAY_NUM" xdotool keyup w keyup Shift_L
    end="$(date +%s)"
    DISPLAY="$DISPLAY_NUM" scrot "$run_dir/2_after_flight.png" 2>/dev/null || true
    sleep 6

    kill -TERM -- "-$pid" 2>/dev/null || true
    for _ in $(seq 1 15); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
    kill -KILL -- "-$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    CURRENT_GROUP=""

    cp "$data/Logs/"client-*.log "$run_dir/" 2>/dev/null || true
    cp "$data/Logs/"server-main.log "$run_dir/" 2>/dev/null || true
    {
        echo "ARM=$arm"
        echo "DIR=$dir"
        echo "FLIGHT_START_EPOCH=$start"
        echo "FLIGHT_END_EPOCH=$end"
        echo "RENDERER=$RENDERER"
        echo "SEED=$SEED"
    } > "$run_dir/markers.env"
    # The data directory holds the cached session and the generated world: drop it.
    rm -rf "$data"
}

NAMES=(); DIRS=()
for spec in "${ARM_SPECS[@]}"; do NAMES+=("${spec%%=*}"); DIRS+=("${spec#*=}"); done
COUNT=${#NAMES[@]}
for ((run = 1; run <= RUNS; run++)); do
    for ((slot = 0; slot < COUNT; slot++)); do
        arm=$(( (slot + run - 1) % COUNT ))
        run_one "${NAMES[$arm]}" "${DIRS[$arm]}" "$run"
    done
done

python3 "$REPO_ROOT/scripts/analyze-chunk-flight.py" "$OUTPUT_DIR"
echo "Artifacts written to: $OUTPUT_DIR"
