#!/usr/bin/env bash
# Worldgen throughput benchmark: measures the spawn-chunk generation phase
# (log marker "Loading ... spawn chunks..." to "Entering runphase RunGame")
# on the patched server across scheduler modes:
#   serial          work-stealing forced off (OPTIMUM_WORLDGEN_MT=0)
#   1-worker        forced one worker (what 6-7 core hosts get by policy)
#   2-worker        forced two workers (what 8+ core hosts get by policy)
#
# Usage: ./scripts/worldgen-benchmark.sh [runs-per-mode]   (default 3)
#
# Each run boots a fresh world with the parity seed and an enlarged
# SpawnChunksWidth (15 columns instead of 7) so the generation window is long
# enough for the 1-second log-timestamp resolution to matter little.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
VANILLA_SERVER="$PROJECT_ROOT/.vanilla/linux-x64/vintagestory"
PATCHED_DLLS="$PROJECT_ROOT/bin/Release/net10.0"
PATCHED_LIB_DLL="$PROJECT_ROOT/build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll"
BENCH_DIR="$PROJECT_ROOT/.worldgen-benchmark"
SEED="42424242"
RUNS="${1:-3}"
GENERATE_SECONDS="${GENERATE_SECONDS:-420}"
SPAWN_CHUNKS_WIDTH="${SPAWN_CHUNKS_WIDTH:-15}"
MAGICNUMBERS_TEMPLATE="$PROJECT_ROOT/.worldgen-parity-baseline/servermagicnumbers.json"

if [ ! -f "$MAGICNUMBERS_TEMPLATE" ]; then
    echo "ERROR: $MAGICNUMBERS_TEMPLATE not found; run the parity harness once to create the baseline." >&2
    exit 1
fi

if [ ! -f "$PATCHED_LIB_DLL" ]; then
    echo "ERROR: patched VintagestoryLib.dll not found (run 'make patch-il' first)" >&2
    exit 1
fi

rm -rf "$BENCH_DIR"
mkdir -p "$BENCH_DIR"

# Build the patched server overlay once (same layout as the parity harness)
PATCHED_SERVER="$BENCH_DIR/patched-server"
mkdir -p "$PATCHED_SERVER"
find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type f -exec cp -l --target-directory="$PATCHED_SERVER" {} +
while IFS= read -r -d '' directory; do
    ln -s "$directory" "$PATCHED_SERVER/$(basename "$directory")"
done < <(find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type d -print0)
find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type l -exec cp -a --target-directory="$PATCHED_SERVER" {} +
cp --remove-destination "$PATCHED_DLLS/VintagestoryAPI-patched.dll" "$PATCHED_SERVER/VintagestoryAPI.dll"
cp --remove-destination "$PATCHED_DLLS/Optimum.Api.Contracts.dll" "$PATCHED_SERVER/Optimum.Api.Contracts.dll"
cp --remove-destination "$PATCHED_DLLS/Optimum.GameContent.dll" "$PATCHED_SERVER/Optimum.GameContent.dll"
cp --remove-destination "$PATCHED_DLLS/VSEssentials-patched.dll" "$PATCHED_SERVER/Mods/VSEssentials.dll"
cp --remove-destination "$PATCHED_DLLS/VSSurvivalMod-patched.dll" "$PATCHED_SERVER/Mods/VSSurvivalMod.dll"
cp --remove-destination "$PATCHED_LIB_DLL" "$PATCHED_SERVER/VintagestoryLib.dll"

log_epoch() {
    # "16.7.2026 14:01:50 [Server ..." -> epoch seconds
    local line="$1"
    local d t
    d=$(echo "$line" | cut -d' ' -f1)
    t=$(echo "$line" | cut -d' ' -f2)
    date -d "$(echo "$d" | awk -F. '{printf "%s-%s-%s", $3, $2, $1}') $t" +%s
}

bench_run() {
    local mode="$1"    # label: serial | 1-worker | 2-worker
    local mt="$2"      # OPTIMUM_WORLDGEN_MT value
    local workers="$3" # OPTIMUM_WORLDGEN_WORKERS value ("" = policy default)
    local run="$4"
    local datapath="$BENCH_DIR/data-$mode-run$run"
    local log="$BENCH_DIR/log-$mode-run$run.txt"

    mkdir -p "$datapath"
    cd "$PATCHED_SERVER"
    dotnet VintagestoryServer.dll --dataPath "$datapath" --genconfig > /dev/null 2>&1 || true
    dotnet VintagestoryServer.dll --dataPath "$datapath" \
        --setconfig="{ Port: 0, MaxClients: 0, PassTimeWhenEmpty: false }" > /dev/null 2>&1 || true
    sed "s/\"SpawnChunksWidth\": *[0-9]*/\"SpawnChunksWidth\": $SPAWN_CHUNKS_WIDTH/" \
        "$MAGICNUMBERS_TEMPLATE" > "$datapath/servermagicnumbers.json"

    OPTIMUM_WORLDGEN_MT="$mt" OPTIMUM_WORLDGEN_WORKERS="$workers" dotnet VintagestoryServer.dll \
        --dataPath "$datapath" \
        --withconfig="{ WorldConfig: { Seed: '$SEED', WorldName: 'bench' } }" \
        > "$log" 2>&1 &
    local pid=$!
    local deadline=$((SECONDS + GENERATE_SECONDS))
    while kill -0 "$pid" 2>/dev/null && ((SECONDS < deadline)); do
        grep -q "Entering runphase RunGame" "$log" 2>/dev/null && break
        sleep 1
    done
    kill -TERM "$pid" 2>/dev/null || true
    local grace=$((SECONDS + 15))
    while kill -0 "$pid" 2>/dev/null && ((SECONDS < grace)); do sleep 1; done
    kill -KILL "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true

    local start_line end_line
    start_line=$(grep -m1 "spawn chunks\.\.\." "$log" || true)
    end_line=$(grep -m1 "Entering runphase RunGame" "$log" || true)
    if [ -z "$start_line" ] || [ -z "$end_line" ]; then
        echo "ERROR"
        return
    fi
    echo $(( $(log_epoch "$end_line") - $(log_epoch "$start_line") ))
}

median() {
    printf '%s\n' "$@" | sort -n | awk '{ a[NR]=$1 } END { print (NR%2) ? a[(NR+1)/2] : (a[NR/2]+a[NR/2+1])/2 }'
}

declare -A results
run_mode() {
    local label="$1" mt="$2" workers="$3"
    local times=()
    local t run
    for run in $(seq 1 "$RUNS"); do
        echo -n "[$label] run $run/$RUNS... "
        t=$(bench_run "$label" "$mt" "$workers" "$run")
        echo "${t}s"
        if [ "$t" = "ERROR" ]; then
            echo "ERROR: run did not reach RunGame; see $BENCH_DIR/log-$label-run$run.txt" >&2
            exit 1
        fi
        times+=("$t")
        rm -rf "$BENCH_DIR/data-$label-run$run"
    done
    results[$label]=$(median "${times[@]}")
    results[$label,all]="${times[*]}"
}

MAX_WORKERS="${MAX_WORKERS:-$(( $(nproc) - 1 ))}"
if [ "$MAX_WORKERS" -gt 6 ]; then MAX_WORKERS=6; fi

run_mode "serial" 0 ""
for w in $(seq 1 "$MAX_WORKERS"); do
    run_mode "${w}-worker" 1 "$w"
done

echo ""
echo "=== SPAWN WORLDGEN BENCHMARK (seed $SEED, ${SPAWN_CHUNKS_WIDTH}x${SPAWN_CHUNKS_WIDTH} spawn columns, $RUNS runs/mode, $(nproc) cores) ==="
printf "%-15s %-18s %-10s %s\n" "mode" "runs (s)" "median" "speedup"
printf "%-15s %-18s %-10s %s\n" "serial" "${results[serial,all]}" "${results[serial]}s" "1.00x"
for w in $(seq 1 "$MAX_WORKERS"); do
    label="${w}-worker"
    med="${results[$label]}"
    speedup=$(awk -v s="${results[serial]}" -v m="$med" 'BEGIN { if (m > 0) printf "%.2f", s/m; else printf "0" }')
    printf "%-15s %-18s %-10s %s\n" "$label" "${results[$label,all]}" "${med}s" "${speedup}x"
done
