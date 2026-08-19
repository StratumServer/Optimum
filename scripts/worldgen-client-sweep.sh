#!/usr/bin/env bash
# Step 33: Worldgen worker-count sweep with per-pass timing.
# Runs serial (0), 1, 2, 3 workers. Each treatment creates a fresh world, waits for
# worldgen to stabilize, collects per-pass timing from the server log.
#
# Usage: bash scripts/worldgen-client-sweep.sh [--trials N] [--duration N] [--out-dir DIR]
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VANILLA_DIR="$PROJECT_ROOT/.vanilla/linux-x64/vintagestory"
DATA_PATH="$HOME/.config/VintagestoryData"
DURATION=45
TRIALS=2
OUT_DIR="$PROJECT_ROOT/research/worldgen-client-sweep-$(date +%Y%m%d-%H%M%S)"
WORLD_ENTRY_TIMEOUT=150

while [ $# -gt 0 ]; do
    case "$1" in
        --trials) TRIALS="$2"; shift 2 ;;
        --duration) DURATION="$2"; shift 2 ;;
        --out-dir) OUT_DIR="$2"; shift 2 ;;
        --timeout) WORLD_ENTRY_TIMEOUT="$2"; shift 2 ;;
        *) echo "Unknown: $1" >&2; exit 64 ;;
    esac
done

mkdir -p "$OUT_DIR"

CSV="$OUT_DIR/results.csv"
echo "trial,workers,terrain_ms_col,features_ms_col,vegetation_ms_col,sunlight_ms_col,predone_ms_col,total_columns,terrain_cols,features_cols,veg_cols,sun_cols,predone_cols" > "$CSV"

echo "Step 33: Worldgen client sweep (per-pass timing)"
echo "  Trials: $TRIALS per treatment"
echo "  Duration: ${DURATION}s worldgen capture after world entry"
echo "  World entry timeout: ${WORLD_ENTRY_TIMEOUT}s"
echo "  Output: $OUT_DIR"
echo ""

# Build and deploy
echo "Building..."
cd "$PROJECT_ROOT"
dotnet build VintageStory.slnx -c Release --verbosity quiet > /dev/null 2>&1

dotnet run --project Optimum.Patcher -c Release -- \
    "$VANILLA_DIR/VintagestoryLib.vanilla.dll" \
    build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll \
    build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll > /dev/null 2>&1

cp build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll "$VANILLA_DIR/VintagestoryLib.dll"
cp build/Vintagestory/bin/Release/net10.0/Vintagestory.dll "$VANILLA_DIR/"
cp bin/Release/net10.0/VintagestoryAPI.dll "$VANILLA_DIR/"
cp bin/Release/net10.0/Optimum.Api.Contracts.dll "$VANILLA_DIR/"
cp bin/Release/net10.0/VSEssentials.dll "$VANILLA_DIR/Mods/"
cp bin/Release/net10.0/VSSurvivalMod.dll "$VANILLA_DIR/Mods/"
cp bin/Release/net10.0/VSCreativeMod.dll "$VANILLA_DIR/Mods/"
cp bin/Release/net10.0/cairo-sharp.dll "$VANILLA_DIR/Lib/"
echo "Deploy complete."
echo ""

parse_worldgen_log() {
    local log_file="$1"
    local line
    line=$(grep -a "Optimum worldgen pass timing" "$log_file" 2>/dev/null | tail -1 || true)
    if [ -z "$line" ]; then
        echo "0,0,0,0,0,0,0,0,0,0,0"
        return
    fi
    python3 -c "
import re, sys
line = sys.stdin.read()
matches = re.findall(r'(\w+)=([\d,.]+)ms/col\((\d+)cols', line)
terrain = features = vegetation = sunlight = predone = '0'
terrain_cols = features_cols = veg_cols = sun_cols = predone_cols = '0'
total_cols_match = re.search(r'totalColumns=(\d+)', line)
total_cols = total_cols_match.group(1) if total_cols_match else '0'
for name, mean_ms, cols in matches:
    mean_ms = mean_ms.replace(',', '.')
    if name == 'Terrain': terrain = mean_ms; terrain_cols = cols
    elif name == 'TerrainFeatures': features = mean_ms; features_cols = cols
    elif name == 'Vegetation': vegetation = mean_ms; veg_cols = cols
    elif name == 'SunLightFlood': sunlight = mean_ms; sun_cols = cols
    elif name == 'PreDone': predone = mean_ms; predone_cols = cols
print(f'{terrain},{features},{vegetation},{sunlight},{predone},{total_cols},{terrain_cols},{features_cols},{veg_cols},{sun_cols},{predone_cols}')
" <<< "$line"
}

run_trial() {
    local workers="$1"
    local trial="$2"
    local trial_dir="$OUT_DIR/w${workers}_t${trial}"
    local world_name="sweep-w${workers}-t${trial}"
    local world_file="$DATA_PATH/Saves/${world_name}.vcdbs"
    mkdir -p "$trial_dir"

    echo "  [w=$workers, t=$trial] Starting..."

    # Remove any previous world with same name
    rm -f "${world_file}"* 2>/dev/null

    # Set worker env vars
    local env_args=(OPTIMUM_SKIP_CHAR_SELECT=1)
    if [ "$workers" -gt 0 ]; then
        env_args+=(OPTIMUM_WORLDGEN_MT=1 OPTIMUM_WORLDGEN_WORKERS="$workers")
    fi

    # Reset worldgen pass timing counters via a fresh server start
    # Launch client with random world
    local pid
    env "${env_args[@]+"${env_args[@]}"}" \
        dotnet "$VANILLA_DIR/Vintagestory.dll" \
            --openWorld "$world_name" \
            --rndWorld -p preset-surviveandbuild \
        > "$trial_dir/stdout.log" 2>&1 &
    pid=$!

    # Wait for world entry (LevelFinalize fires "game launch tasks")
    # VS truncates client-main.log on each start, so no stale entries exist.
    local waited=0
    local log_file="$DATA_PATH/Logs/client-main.log"

    while [ $waited -lt "$WORLD_ENTRY_TIMEOUT" ]; do
        if grep -q "game launch tasks" "$log_file" 2>/dev/null; then
            break
        fi
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "  [w=$workers, t=$trial] Client exited early!"
            wait "$pid" || true
            echo "$trial,$workers,0,0,0,0,0,0,0,0,0,0,0" >> "$CSV"
            rm -f "${world_file}"* 2>/dev/null
            return
        fi
        sleep 2
        waited=$((waited + 2))
    done

    if [ $waited -ge "$WORLD_ENTRY_TIMEOUT" ]; then
        echo "  [w=$workers, t=$trial] Timeout waiting for world entry"
        kill "$pid" 2>/dev/null || true
        sleep 2
        kill -9 "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
        echo "$trial,$workers,0,0,0,0,0,0,0,0,0,0,0" >> "$CSV"
        rm -f "${world_file}"* 2>/dev/null
        return
    fi

    echo "  [w=$workers, t=$trial] World entered. Letting worldgen run ${DURATION}s..."
    sleep "$DURATION"

    # Kill gracefully (SIGTERM triggers WindowExit which calls serverMain.Stop → Dispose)
    kill -TERM "$pid" 2>/dev/null || true
    sleep 5
    kill -9 "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true

    # Collect worldgen pass timing from server log (periodic entries)
    local server_log="$DATA_PATH/Logs/server-main.log"
    local wg_result
    wg_result=$(parse_worldgen_log "$server_log")

    echo "$trial,$workers,$wg_result" >> "$CSV"
    echo "  [w=$workers, t=$trial] Done. WG: $wg_result"

    # Cleanup world save
    rm -f "${world_file}"* 2>/dev/null
    sleep 2
}

# Randomized blocked design: each block has one trial of each treatment
for trial in $(seq 1 "$TRIALS"); do
    echo "Block $trial/$TRIALS"
    # Shuffle treatment order within block
    treatments=($(shuf -e 0 1 2 3))
    for w in "${treatments[@]}"; do
        run_trial "$w" "$trial"
    done
    echo ""
done

echo "Sweep complete. Results: $CSV"
echo ""
echo "Summary:"
python3 -c "
import csv, sys
from collections import defaultdict

data = defaultdict(lambda: {'terrain': [], 'features': [], 'veg': [], 'sun': [], 'predone': [], 'cols': []})
with open('$CSV') as f:
    reader = csv.DictReader(f)
    for row in reader:
        w = row['workers']
        if float(row['total_columns']) == 0:
            continue
        data[w]['terrain'].append(float(row['terrain_ms_col']))
        data[w]['features'].append(float(row['features_ms_col']))
        data[w]['veg'].append(float(row['vegetation_ms_col']))
        data[w]['sun'].append(float(row['sunlight_ms_col']))
        data[w]['predone'].append(float(row['predone_ms_col']))
        data[w]['cols'].append(float(row['total_columns']))

def mean(lst): return sum(lst)/len(lst) if lst else 0

print(f\"{'Workers':<8} {'Terrain':>8} {'Features':>9} {'Veg':>6} {'Sun':>6} {'PreDone':>8} {'Cols':>6}\")
print('-' * 56)
for w in sorted(data.keys(), key=int):
    d = data[w]
    print(f\"{w:<8} {mean(d['terrain']):>8.2f} {mean(d['features']):>9.2f} {mean(d['veg']):>6.2f} {mean(d['sun']):>6.2f} {mean(d['predone']):>8.2f} {mean(d['cols']):>6.0f}\")
"
