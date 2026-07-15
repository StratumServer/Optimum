#!/usr/bin/env bash
# Optimum vs Vanilla frame time benchmark via MangoHud.
# Usage: bash scripts/benchmark-frametime.sh [duration_seconds]
#
# Runs Optimum first, then vanilla. Both log frame times to CSV.
# After both runs, prints a comparison summary (P50, P95, P99, 1% low).
# The user must be in the SAME scene and stationary for both runs.
set -euo pipefail

DURATION="${1:-30}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OPTIMUM_DIR="${HOME}/.local/share/optimum"
VANILLA_DIR="$REPO_ROOT/.vanilla-linux/vintagestory"
OUTPUT_DIR="$REPO_ROOT/benchmarks/frametime-$(date +%Y%m%d-%H%M%S)"

mkdir -p "$OUTPUT_DIR"

echo "╔══════════════════════════════════════════════╗"
echo "║  Optimum Frame Time Benchmark (MangoHud)    ║"
echo "╠══════════════════════════════════════════════╣"
echo "║  Duration: ${DURATION}s per run                      ║"
echo "║  Output:   $OUTPUT_DIR"
echo "╚══════════════════════════════════════════════╝"
echo ""

# MangoHud config: log frametime with overlay visible (no_display breaks logging in v0.8.1).
# Press Shift+F2 in-game to start/stop logging. Logs for $DURATION seconds then stops.
mkdir -p "$OUTPUT_DIR/optimum" "$OUTPUT_DIR/vanilla"

echo "─── Run 1: OPTIMUM ───"
echo "Launch Optimum, load a world, stand still."
echo "Press Shift+F2 to start logging (${DURATION}s, stops automatically)."
echo "Then close the game."
echo ""
read -rp "Press ENTER to launch Optimum..."

MANGOHUD_CONFIG="log_duration=$DURATION,log_interval=0,output_folder=$OUTPUT_DIR/optimum,toggle_logging=Shift_L+F2" \
    mangohud "$OPTIMUM_DIR/run.sh" || true

echo ""
echo "─── Run 2: VANILLA ───"
echo "Launch vanilla, load the SAME world, stand at the SAME spot."
echo "Press Shift+F2 to start logging. Then close the game."
echo ""
read -rp "Press ENTER to launch Vanilla..."

cd "$VANILLA_DIR"
MANGOHUD_CONFIG="log_duration=$DURATION,log_interval=0,output_folder=$OUTPUT_DIR/vanilla,toggle_logging=Shift_L+F2" \
    mangohud ./Vintagestory || true

echo ""
echo "─── Processing results ───"

# Find the CSVs
OPT_CSV=$(find "$OUTPUT_DIR/optimum" -name "*.csv" | sort | tail -1)
VAN_CSV=$(find "$OUTPUT_DIR/vanilla" -name "*.csv" | sort | tail -1)

if [[ -z "$OPT_CSV" || -z "$VAN_CSV" ]]; then
    echo "ERROR: Missing CSV files. Did you press F12 in both runs?"
    echo "  Optimum dir: $OUTPUT_DIR/optimum/"
    echo "  Vanilla dir: $OUTPUT_DIR/vanilla/"
    ls "$OUTPUT_DIR/optimum/" 2>/dev/null
    ls "$OUTPUT_DIR/vanilla/" 2>/dev/null
    exit 1
fi

echo "Optimum CSV: $OPT_CSV"
echo "Vanilla CSV: $VAN_CSV"
echo ""

# Parse and compare using python
python3 - "$OPT_CSV" "$VAN_CSV" << 'PYTHON'
import sys, csv, statistics

def parse_mangohud_csv(path):
    """Parse MangoHud CSV, extract frametime_ms column."""
    frametimes = []
    with open(path) as f:
        reader = csv.DictReader(f)
        for row in reader:
            # MangoHud uses 'frametime' (ms) as the column name
            for key in ('frametime', 'frametime_ms', 'Frametime'):
                if key in row:
                    try:
                        frametimes.append(float(row[key]))
                    except (ValueError, TypeError):
                        pass
                    break
    return frametimes

def percentile(data, p):
    data_sorted = sorted(data)
    idx = int(len(data_sorted) * p / 100)
    return data_sorted[min(idx, len(data_sorted) - 1)]

def one_percent_low(data):
    """1% low = average of the worst 1% of frames (highest frametimes)."""
    data_sorted = sorted(data, reverse=True)
    n = max(1, len(data_sorted) // 100)
    return statistics.mean(data_sorted[:n])

def report(name, frametimes):
    if not frametimes:
        print(f"  {name}: NO DATA")
        return {}
    avg = statistics.mean(frametimes)
    p50 = percentile(frametimes, 50)
    p95 = percentile(frametimes, 95)
    p99 = percentile(frametimes, 99)
    low1 = one_percent_low(frametimes)
    fps_avg = 1000.0 / avg if avg > 0 else 0
    print(f"  {name}:")
    print(f"    Frames:   {len(frametimes)}")
    print(f"    Avg FPS:  {fps_avg:.1f}")
    print(f"    Avg:      {avg:.2f} ms")
    print(f"    P50:      {p50:.2f} ms")
    print(f"    P95:      {p95:.2f} ms")
    print(f"    P99:      {p99:.2f} ms")
    print(f"    1% Low:   {low1:.2f} ms ({1000/low1:.1f} FPS)")
    return {'avg': avg, 'p50': p50, 'p95': p95, 'p99': p99, 'low1': low1}

opt_ft = parse_mangohud_csv(sys.argv[1])
van_ft = parse_mangohud_csv(sys.argv[2])

print("╔══════════════════════════════════════════════╗")
print("║         FRAME TIME COMPARISON               ║")
print("╠══════════════════════════════════════════════╣")
opt = report("OPTIMUM", opt_ft)
print("")
van = report("VANILLA", van_ft)
print("")

if opt and van:
    print("  ─── DELTA (Optimum vs Vanilla) ───")
    for metric in ('avg', 'p50', 'p95', 'p99', 'low1'):
        diff = opt[metric] - van[metric]
        pct = (diff / van[metric] * 100) if van[metric] != 0 else 0
        better = "faster" if diff < 0 else "slower"
        print(f"    {metric.upper():6s}: {diff:+.2f} ms ({pct:+.1f}% {better})")

print("╚══════════════════════════════════════════════╝")
PYTHON

echo ""
echo "Raw CSVs saved to: $OUTPUT_DIR/"
