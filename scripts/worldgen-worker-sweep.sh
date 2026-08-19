#!/usr/bin/env bash
# Worldgen worker-count sweep with blocked randomization. Each run validates its realized scheduler count before the script writes a CSV row. Exact mode rejects worker-cap changes, so one, two, and three workers remain separate treatments.
#
# Usage: ./worldgen-worker-sweep.sh [--trials N] [--max-workers N] [--out FILE] [--spawn-width N]
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VANILLA_SERVER="$PROJECT_ROOT/.vanilla/linux-x64/vintagestory"
PATCHED_DLLS="$PROJECT_ROOT/bin/Release/net10.0"
PATCHED_LIB_DLL="$PROJECT_ROOT/build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll"
RUN_VALIDATOR="$PROJECT_ROOT/scripts/validate-worldgen-run.sh"
PROCESS_METRICS="$PROJECT_ROOT/scripts/measure-process.sh"
LOG_COLLECTOR="$PROJECT_ROOT/scripts/collect-benchmark-log.py"
MAGICNUMBERS_TEMPLATE="$PROJECT_ROOT/.worldgen-parity-baseline/servermagicnumbers.json"
BENCH_DIR="$PROJECT_ROOT/.worldgen-sweep"
SEED="42424242"
GENERATE_SECONDS="${GENERATE_SECONDS:-240}"
ORDER_SEED="${ORDER_SEED:-20260813}"

# Twelve trials support release comparisons. Use --trials 4 for a smoke run.
TRIALS=12
MAX_WORKERS=3
OUT_CSV="$BENCH_DIR/results.csv"
SPAWN_CHUNKS_WIDTH=15

while [ $# -gt 0 ]; do
    case "$1" in
        --trials) TRIALS="$2"; shift 2 ;;
        --max-workers) MAX_WORKERS="$2"; shift 2 ;;
        --out) OUT_CSV="$2"; shift 2 ;;
        --spawn-width) SPAWN_CHUNKS_WIDTH="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 64 ;;
    esac
done

if ! [[ "$MAX_WORKERS" =~ ^[1-3]$ ]]; then
    echo "ERROR: --max-workers must fall between 1 and 3." >&2
    exit 64
fi

case "$OUT_CSV" in
    /*) ;;
    *) OUT_CSV="$PROJECT_ROOT/$OUT_CSV" ;;
esac
mkdir -p "$(dirname "$OUT_CSV")"

if [ ! -f "$MAGICNUMBERS_TEMPLATE" ]; then
    echo "ERROR: $MAGICNUMBERS_TEMPLATE not found." >&2
    echo "Generate it once: boot any server with --genconfig then a real seed until RunGame," >&2
    echo "then copy its servermagicnumbers.json here (see the cross-hardware benchmark plan doc)." >&2
    exit 1
fi
if [ ! -f "$PATCHED_LIB_DLL" ]; then
    echo "ERROR: patched VintagestoryLib.dll not found (run 'make patch-il' first)" >&2
    exit 1
fi
if [ ! -x "$LOG_COLLECTOR" ]; then
    echo "ERROR: benchmark log collector is missing or not executable: $LOG_COLLECTOR" >&2
    exit 1
fi

# Preflight: refuse to run alongside a leftover server from an earlier
# aborted sweep - orphaned dotnet processes surviving a killed parent script
# is exactly what silently contaminated an earlier session's numbers.
if pgrep -f "VintagestoryServer.dll.*$BENCH_DIR" > /dev/null 2>&1; then
    echo "ERROR: a VintagestoryServer process is already running against $BENCH_DIR." >&2
    echo "Kill it first: pkill -f \"VintagestoryServer.dll.*$BENCH_DIR\"" >&2
    exit 1
fi

mkdir -p "$BENCH_DIR"
echo "trial,mode,workers,seconds,user_seconds,sys_seconds,cpu_percent,max_rss_kib,voluntary_context_switches,involuntary_context_switches,major_faults,minor_faults,swap_kib,server_exit_code,order_seed" > "$OUT_CSV"

PATCHED_SERVER="$BENCH_DIR/patched-server"
rm -rf "$PATCHED_SERVER"
mkdir -p "$PATCHED_SERVER"
find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type f -exec cp -l --target-directory="$PATCHED_SERVER" {} +
while IFS= read -r -d '' directory; do
    ln -s "$directory" "$PATCHED_SERVER/$(basename "$directory")"
done < <(find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type d -print0)
find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type l -exec cp -a --target-directory="$PATCHED_SERVER" {} +
cp --remove-destination "$PATCHED_DLLS/VintagestoryAPI.dll" "$PATCHED_SERVER/VintagestoryAPI.dll"
cp --remove-destination "$PATCHED_DLLS/Optimum.Api.Contracts.dll" "$PATCHED_SERVER/Optimum.Api.Contracts.dll"
cp --remove-destination "$PATCHED_DLLS/Optimum.GameContent.dll" "$PATCHED_SERVER/Optimum.GameContent.dll"
cp --remove-destination "$PATCHED_DLLS/VSEssentials.dll" "$PATCHED_SERVER/Mods/VSEssentials.dll"
cp --remove-destination "$PATCHED_DLLS/VSSurvivalMod.dll" "$PATCHED_SERVER/Mods/VSSurvivalMod.dll"
cp --remove-destination "$PATCHED_LIB_DLL" "$PATCHED_SERVER/VintagestoryLib.dll"

# Kill any child server process this script itself started, on any exit path
# (normal completion, error, Ctrl-C) - the earlier session's contamination
# came from exactly this cleanup being missing.
CURRENT_PID=""
METRIC_PID=""
COLLECTOR_PID=""
cleanup() {
    if [ -n "$METRIC_PID" ] && kill -0 "$METRIC_PID" 2>/dev/null; then
        kill -TERM "$METRIC_PID" 2>/dev/null || true
        wait "$METRIC_PID" 2>/dev/null || true
    fi
    if [ -n "$CURRENT_PID" ] && kill -0 "$CURRENT_PID" 2>/dev/null; then
        kill -KILL "$CURRENT_PID" 2>/dev/null || true
        wait "$CURRENT_PID" 2>/dev/null || true
    fi
    if [ -n "$COLLECTOR_PID" ] && kill -0 "$COLLECTOR_PID" 2>/dev/null; then
        kill -TERM "$COLLECTOR_PID" 2>/dev/null || true
        wait "$COLLECTOR_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

log_epoch() {
    local line="$1" d t
    d=$(echo "$line" | cut -d' ' -f1)
    t=$(echo "$line" | cut -d' ' -f2)
    date -d "$(echo "$d" | awk -F. '{printf "%s-%s-%s", $3, $2, $1}') $t" +%s
}

metric_value() {
    local key="$1" path="$2" value
    value=$(awk -F= -v key="$key" '$1 == key { value = $2 } END { print value }' "$path" 2>/dev/null || true)
    printf '%s' "${value:-0}"
}

marker_value() {
    local key="$1" path="$2" value
    value=$(awk -F= -v key="$key" '$1 == key { value = $2 } END { print value }' "$path" 2>/dev/null || true)
    printf '%s' "${value:-}"
}

marker_seconds() {
    local start_key="$1" end_key="$2" path="$3" start_ns end_ns
    start_ns=$(marker_value "$start_key" "$path")
    end_ns=$(marker_value "$end_key" "$path")
    if [ -n "$start_ns" ] && [ -n "$end_ns" ]; then
        awk -v start="$start_ns" -v end="$end_ns" 'BEGIN { printf "%.6f", (end - start) / 1000000000 }'
        return 0
    fi
    return 1
}

run_one() {
    local mode="$1" mt="$2" workers="$3" trial="$4"
    local datapath="$BENCH_DIR/data-$mode-t$trial"
    local log="$BENCH_DIR/log-$mode-t$trial.txt"
    local metrics="$BENCH_DIR/metrics-$mode-t$trial.txt"
    local markers="$BENCH_DIR/markers-$mode-t$trial.txt"
    local output_fifo="$BENCH_DIR/output-$mode-t$trial.fifo"
    RUN_USER_SECONDS=0
    RUN_SYS_SECONDS=0
    RUN_CPU_PERCENT=0
    RUN_MAX_RSS_KIB=0
    RUN_VOLUNTARY_CONTEXT_SWITCHES=0
    RUN_INVOLUNTARY_CONTEXT_SWITCHES=0
    RUN_MAJOR_FAULTS=0
    RUN_MINOR_FAULTS=0
    RUN_SWAP_KIB=0
    RUN_SERVER_EXIT_CODE=0

    mkdir -p "$datapath"
    cd "$PATCHED_SERVER"
    dotnet VintagestoryServer.dll --dataPath "$datapath" --genconfig > /dev/null 2>&1 || true
    dotnet VintagestoryServer.dll --dataPath "$datapath" --setconfig="{ Port: 0, MaxClients: 0, PassTimeWhenEmpty: false }" > /dev/null 2>&1 || true
    sed "s/\"SpawnChunksWidth\": *[0-9]*/\"SpawnChunksWidth\": $SPAWN_CHUNKS_WIDTH/" \
        "$MAGICNUMBERS_TEMPLATE" > "$datapath/servermagicnumbers.json"

    rm -f "$output_fifo"
    mkfifo "$output_fifo"
    "$LOG_COLLECTOR" "$log" "$markers" < "$output_fifo" &
    COLLECTOR_PID=$!
    OPTIMUM_WORLDGEN_MT="$mt" OPTIMUM_WORLDGEN_WORKERS="$workers" dotnet VintagestoryServer.dll \
        --dataPath "$datapath" \
        --withconfig="{ WorldConfig: { Seed: '$SEED', WorldName: 'sweep' } }" \
        > "$output_fifo" 2>&1 &
    CURRENT_PID=$!
    "$PROCESS_METRICS" "$CURRENT_PID" "$metrics" 0.25 &
    METRIC_PID=$!
    local deadline=$((SECONDS + GENERATE_SECONDS))
    while kill -0 "$CURRENT_PID" 2>/dev/null && ((SECONDS < deadline)); do
        grep -q "Entering runphase RunGame" "$log" 2>/dev/null && break
        sleep 1
    done
    kill -TERM "$CURRENT_PID" 2>/dev/null || true
    local grace=$((SECONDS + 15))
    while kill -0 "$CURRENT_PID" 2>/dev/null && ((SECONDS < grace)); do sleep 1; done
    kill -KILL "$CURRENT_PID" 2>/dev/null || true
    server_exit_code=0
    wait "$CURRENT_PID" 2>/dev/null || server_exit_code=$?
    CURRENT_PID=""
    RUN_SERVER_EXIT_CODE="$server_exit_code"
    wait "$METRIC_PID" 2>/dev/null || true
    METRIC_PID=""
    wait "$COLLECTOR_PID" 2>/dev/null || true
    COLLECTOR_PID=""

    "$RUN_VALIDATOR" "$log" "$workers"

    local start_line end_line secs
    start_line=$(grep -m1 "spawn chunks\.\.\." "$log" || true)
    end_line=$(grep -m1 "Entering runphase RunGame" "$log" || true)
    rm -rf "$datapath"
    rm -f "$output_fifo"
    if [ -z "$start_line" ] || [ -z "$end_line" ]; then
        echo "Benchmark server exit code: $RUN_SERVER_EXIT_CODE" >> "$log"
        RUN_SECONDS="ERROR"
        return 0
    fi
    if ! RUN_SECONDS=$(marker_seconds spawn_start_ns spawn_end_ns "$markers"); then
        RUN_SECONDS=$(( $(log_epoch "$end_line") - $(log_epoch "$start_line") ))
    fi
    RUN_USER_SECONDS=$(metric_value user_seconds "$metrics")
    RUN_SYS_SECONDS=$(metric_value sys_seconds "$metrics")
    RUN_CPU_PERCENT=$(metric_value cpu_percent "$metrics")
    RUN_MAX_RSS_KIB=$(metric_value max_rss_kib "$metrics")
    RUN_VOLUNTARY_CONTEXT_SWITCHES=$(metric_value voluntary_context_switches "$metrics")
    RUN_INVOLUNTARY_CONTEXT_SWITCHES=$(metric_value involuntary_context_switches "$metrics")
    RUN_MAJOR_FAULTS=$(metric_value major_faults "$metrics")
    RUN_MINOR_FAULTS=$(metric_value minor_faults "$metrics")
    RUN_SWAP_KIB=$(metric_value swap_kib "$metrics")
}

MODES=("serial:0:0")
for w in $(seq 1 "$MAX_WORKERS"); do
    MODES+=("${w}-worker:1:${w}")
done

RANDOM="$ORDER_SEED"
mapfile -t base_order < <(seq 0 $((${#MODES[@]} - 1)))
for ((i = ${#base_order[@]} - 1; i > 0; i--)); do
    j=$((RANDOM % (i + 1)))
    tmp=${base_order[i]}; base_order[i]=${base_order[j]}; base_order[j]=$tmp
done
echo "order-seed=$ORDER_SEED base-order=${base_order[*]}"

for trial in $(seq 1 "$TRIALS"); do
    rotation=$(((trial - 1) % ${#base_order[@]}))
    order=("${base_order[@]:rotation}" "${base_order[@]:0:rotation}")
    for idx in "${order[@]}"; do
        entry="${MODES[$idx]}"
        mode="${entry%%:*}"
        rest="${entry#*:}"
        mt="${rest%%:*}"
        workers="${rest#*:}"
        RUN_SECONDS="ERROR"
        run_one "$mode" "$mt" "$workers" "$trial"
        secs="$RUN_SECONDS"
        echo "[trial $trial] $mode -> ${secs}s"
        echo "$trial,$mode,$workers,$secs,$RUN_USER_SECONDS,$RUN_SYS_SECONDS,$RUN_CPU_PERCENT,$RUN_MAX_RSS_KIB,$RUN_VOLUNTARY_CONTEXT_SWITCHES,$RUN_INVOLUNTARY_CONTEXT_SWITCHES,$RUN_MAJOR_FAULTS,$RUN_MINOR_FAULTS,$RUN_SWAP_KIB,$RUN_SERVER_EXIT_CODE,$ORDER_SEED" >> "$OUT_CSV"
    done
done

echo "DONE: $OUT_CSV"
echo "Analyze with: python3 scripts/analyze-worker-sweep.py $OUT_CSV"
