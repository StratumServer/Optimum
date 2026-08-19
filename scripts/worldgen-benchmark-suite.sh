#!/usr/bin/env bash
# Run the precise Linux worldgen benchmark suite and retain its evidence.
#
# The default is a release run: 12 balanced trials for spawn and streaming.
# Use --smoke for one short trial per mode during development.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORKER_SWEEP="$PROJECT_ROOT/scripts/worldgen-worker-sweep.sh"
STREAMING_SWEEP="$PROJECT_ROOT/scripts/worldgen-streaming-sweep.sh"
ANALYZER="$PROJECT_ROOT/scripts/analyze-worker-sweep.py"
VALIDATOR="$PROJECT_ROOT/scripts/validate-worldgen-benchmark.py"

TRIALS=12
MAX_WORKERS=3
SPAWN_WIDTH=15
PREGEN_RANGE=9
GENERATE_SECONDS="${GENERATE_SECONDS:-240}"
PREGEN_TIMEOUT="${PREGEN_TIMEOUT:-180}"
ORDER_SEED="${ORDER_SEED:-20260813}"
CPU_LIST="${CPU_LIST:-0-5}"
OUT_DIR=""
RUN_SPAWN=true
RUN_STREAMING=true
SMOKE=false

usage() {
    sed -n '2,11p' "$0"
    cat <<'EOF'

Options:
  --smoke                 One trial, one worker, width 9, pregen range 3.
  --trials N              Trials per mode. Release default: 12.
  --max-workers N         Exact worker treatments from 1 through N.
  --spawn-width N         Spawn chunk width. Release default: 15.
  --pregen-range N        Streaming pregen range. Release default: 9.
  --cpu-list LIST         Affinity inherited by every server process. Default: 0-5.
  --out-dir DIR           Evidence directory. Default: research/worldgen-bench-suite-<timestamp>.
  --skip-spawn            Skip the spawn workload.
  --skip-streaming        Skip the streaming workload.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --smoke) SMOKE=true; shift ;;
        --trials) TRIALS="$2"; shift 2 ;;
        --max-workers) MAX_WORKERS="$2"; shift 2 ;;
        --spawn-width) SPAWN_WIDTH="$2"; shift 2 ;;
        --pregen-range) PREGEN_RANGE="$2"; shift 2 ;;
        --cpu-list) CPU_LIST="$2"; shift 2 ;;
        --out-dir) OUT_DIR="$2"; shift 2 ;;
        --skip-spawn) RUN_SPAWN=false; shift ;;
        --skip-streaming) RUN_STREAMING=false; shift ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 64 ;;
    esac
done

if $SMOKE; then
    TRIALS=1
    MAX_WORKERS=1
    SPAWN_WIDTH=9
    PREGEN_RANGE=3
    GENERATE_SECONDS=90
    PREGEN_TIMEOUT=90
fi

if ! [[ "$TRIALS" =~ ^[1-9][0-9]*$ ]]; then
    echo "ERROR: --trials must be a positive integer." >&2
    exit 64
fi
if ! [[ "$MAX_WORKERS" =~ ^[1-3]$ ]]; then
    echo "ERROR: --max-workers must fall between 1 and 3." >&2
    exit 64
fi
if ! [[ "$SPAWN_WIDTH" =~ ^[1-9][0-9]*$ ]] || ! [[ "$PREGEN_RANGE" =~ ^[1-9][0-9]*$ ]]; then
    echo "ERROR: workload dimensions must be positive integers." >&2
    exit 64
fi
if ! $RUN_SPAWN && ! $RUN_STREAMING; then
    echo "ERROR: at least one workload must remain enabled." >&2
    exit 64
fi

VALIDATION_FLAGS=(--require-high-resolution)
if ! $SMOKE; then
    VALIDATION_FLAGS+=(--require-balanced-order)
fi

if ! command -v taskset >/dev/null 2>&1; then
    echo "ERROR: taskset is required for a controlled Linux run." >&2
    exit 1
fi
if [ ! -x "$VALIDATOR" ]; then
    chmod +x "$VALIDATOR"
fi
if pgrep -af '[d]otnet VintagestoryServer.dll|[w]orldgen-worker-sweep.sh|[w]orldgen-streaming-sweep.sh' >/tmp/optimum-worldgen-live.txt; then
    cat /tmp/optimum-worldgen-live.txt >&2
    echo "ERROR: stop every Vintage Story server before starting the suite." >&2
    exit 1
fi

HOST="$(hostname -s)"
STAMP="$(date +%Y%m%d-%H%M%S)"
if [ -z "$OUT_DIR" ]; then
    OUT_DIR="$PROJECT_ROOT/research/worldgen-bench-suite-$HOST-$STAMP"
elif [[ "$OUT_DIR" != /* ]]; then
    OUT_DIR="$PROJECT_ROOT/$OUT_DIR"
fi
mkdir -p "$OUT_DIR"

metric_file() {
    local resource="$1"
    if [ -f "/proc/pressure/$resource" ]; then
        sed ':a;N;$!ba;s/\n/ | /g' "/proc/pressure/$resource"
    else
        echo unavailable
    fi
}

write_manifest() {
    local revision dirty
    revision="$(git -C "$PROJECT_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
    if git -C "$PROJECT_ROOT" diff --quiet -- . 2>/dev/null; then dirty=false; else dirty=true; fi
    {
        echo "timestamp=$STAMP"
        echo "host=$HOST"
        echo "revision=$revision"
        echo "dirty=$dirty"
        echo "kernel=$(uname -srvmo)"
        echo "dotnet=$(dotnet --version 2>/dev/null || echo unavailable)"
        echo "cpu_list=$CPU_LIST"
        echo "trials=$TRIALS"
        echo "max_workers=$MAX_WORKERS"
        echo "spawn_width=$SPAWN_WIDTH"
        echo "pregen_range=$PREGEN_RANGE"
        echo "generate_seconds=$GENERATE_SECONDS"
        echo "pregen_timeout=$PREGEN_TIMEOUT"
        echo "order_seed=$ORDER_SEED"
        echo "governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null || echo unavailable)"
        echo "boost=$(cat /sys/devices/system/cpu/boost 2>/dev/null || echo unavailable)"
        echo "suite_affinity=$(taskset -pc $$ 2>/dev/null || echo unavailable)"
        echo "server_affinity_expected=$CPU_LIST"
        echo "cpus_effective=$(cat /sys/fs/cgroup/cpuset.cpus.effective 2>/dev/null || echo unavailable)"
        echo "memory=$(free -h 2>/dev/null | tr '\n' ' ' || echo unavailable)"
        echo "pressure_cpu=$(metric_file cpu)"
        echo "pressure_memory=$(metric_file memory)"
        echo "pressure_io=$(metric_file io)"
        echo "docker=$(docker --version 2>/dev/null || echo unavailable)"
        echo "perf_paranoid=$(cat /proc/sys/kernel/perf_event_paranoid 2>/dev/null || echo unavailable)"
    } > "$OUT_DIR/manifest.txt"
    lscpu > "$OUT_DIR/lscpu.txt" 2>&1 || true
    free -h > "$OUT_DIR/memory-before.txt" 2>&1 || true
}

copy_runtime_artifacts() {
    local source_dir="$1" destination="$2"
    rm -rf "$destination"
    mkdir -p "$destination"
    find "$source_dir" -maxdepth 1 -type f \( -name 'log-*.txt' -o -name 'metrics-*.txt' -o -name 'markers-*.txt' \) \
        -exec cp -- {} "$destination"/ \;
}

validate_runtime_affinity() {
    local runtime_dir="$1" metrics affinity cgroup found=0
    while IFS= read -r -d '' metrics; do
        found=1
        affinity=$(awk -F= '$1 == "affinity" { print $2 }' "$metrics")
        cgroup=$(awk -F= '$1 == "cgroup" { print $2 }' "$metrics")
        if [ "$affinity" != "$CPU_LIST" ]; then
            echo "ERROR: $metrics ran on affinity '$affinity', expected '$CPU_LIST'." >&2
            exit 1
        fi
        if [ -z "$cgroup" ] || [ "$cgroup" = unknown ]; then
            echo "ERROR: $metrics has no process cgroup." >&2
            exit 1
        fi
    done < <(find "$runtime_dir" -maxdepth 1 -type f -name 'metrics-*.txt' -print0)
    [ "$found" -eq 1 ] || { echo "ERROR: no process metrics found in $runtime_dir." >&2; exit 1; }
}

clean_runtime_scratch() {
    for scratch in "$PROJECT_ROOT/.worldgen-sweep" "$PROJECT_ROOT/.worldgen-sweep-separate"; do
        [ -d "$scratch" ] || continue
        find "$scratch" -maxdepth 1 -type f \( -name 'log-*.txt' -o -name 'metrics-*.txt' -o -name 'markers-*.txt' -o -name 'output-*.fifo' \) -delete
    done
}

run_spawn() {
    local csv="$OUT_DIR/spawn.csv"
    local console="$OUT_DIR/spawn.console.log"
    local analysis="$OUT_DIR/spawn.analysis.txt"
    echo "=== precise spawn workload ==="
    set +e
    taskset -c "$CPU_LIST" env GENERATE_SECONDS="$GENERATE_SECONDS" ORDER_SEED="$ORDER_SEED" \
        bash "$WORKER_SWEEP" --trials "$TRIALS" --max-workers "$MAX_WORKERS" \
        --spawn-width "$SPAWN_WIDTH" --out "$csv" 2>&1 | tee "$console"
    local status=${PIPESTATUS[0]}
    set -e
    [ "$status" -eq 0 ] || { echo "ERROR: spawn sweep failed with $status." >&2; exit "$status"; }
    python3 "$VALIDATOR" "$csv" --workload spawn --trials "$TRIALS" --max-workers "$MAX_WORKERS" \
        "${VALIDATION_FLAGS[@]}" > "$OUT_DIR/spawn.validation.txt"
    python3 "$ANALYZER" "$csv" --column seconds > "$analysis"
    copy_runtime_artifacts "$PROJECT_ROOT/.worldgen-sweep" "$OUT_DIR/spawn-runtime"
    validate_runtime_affinity "$OUT_DIR/spawn-runtime"
}

run_streaming() {
    local csv="$OUT_DIR/streaming.csv"
    local console="$OUT_DIR/streaming.console.log"
    local analysis="$OUT_DIR/streaming.analysis.txt"
    echo "=== precise streaming workload ==="
    set +e
    taskset -c "$CPU_LIST" env GENERATE_SECONDS="$GENERATE_SECONDS" PREGEN_TIMEOUT="$PREGEN_TIMEOUT" \
        ORDER_SEED="$ORDER_SEED" bash "$STREAMING_SWEEP" --trials "$TRIALS" \
        --max-workers "$MAX_WORKERS" --pregen-range "$PREGEN_RANGE" --out "$csv" 2>&1 | tee "$console"
    local status=${PIPESTATUS[0]}
    set -e
    [ "$status" -eq 0 ] || { echo "ERROR: streaming sweep failed with $status." >&2; exit "$status"; }
    python3 "$VALIDATOR" "$csv" --workload streaming --trials "$TRIALS" --max-workers "$MAX_WORKERS" \
        "${VALIDATION_FLAGS[@]}" > "$OUT_DIR/streaming.validation.txt"
    python3 "$ANALYZER" "$csv" --column pregen_seconds > "$analysis"
    copy_runtime_artifacts "$PROJECT_ROOT/.worldgen-sweep-separate" "$OUT_DIR/streaming-runtime"
    validate_runtime_affinity "$OUT_DIR/streaming-runtime"
}

clean_runtime_scratch
write_manifest
$RUN_SPAWN && run_spawn
$RUN_STREAMING && run_streaming
free -h > "$OUT_DIR/memory-after.txt" 2>&1 || true
for resource in cpu memory io; do
    echo "pressure_after_$resource=$(metric_file "$resource")" >> "$OUT_DIR/manifest.txt"
done

if pgrep -af '[d]otnet VintagestoryServer.dll' > "$OUT_DIR/live-processes-after.txt"; then
    echo "ERROR: server process survived the suite." >&2
    exit 1
fi
echo "PASS: precise worldgen suite completed. Evidence: $OUT_DIR"
