#!/usr/bin/env bash
# Benchmark all FSR 1 render scale presets via MangoHud.
# Usage: bash scripts/benchmark-renderscale.sh [duration_seconds]
#
# Runs Optimum 4 times (one per preset). Between runs, edits optimum.json
# to set the render scale. The user must stand in the SAME spot for all runs.
set -euo pipefail

DURATION="${1:-20}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OPTIMUM_DIR="${HOME}/.local/share/optimum"
CONFIG="$HOME/.config/VintagestoryData/ModConfig/optimum.json"
OUTPUT_DIR="$REPO_ROOT/benchmarks/renderscale-$(date +%Y%m%d-%H%M%S)"

mkdir -p "$OUTPUT_DIR"

echo "╔══════════════════════════════════════════════╗"
echo "║  FSR 1 Render Scale Benchmark (MangoHud)    ║"
echo "╠══════════════════════════════════════════════╣"
echo "║  Duration: ${DURATION}s per preset                   ║"
echo "║  Presets:  Native, Quality, Balanced, Perf  ║"
echo "║  Output:   $OUTPUT_DIR"
echo "╚══════════════════════════════════════════════╝"
echo ""
echo "Stand in the SAME spot for every run. Press Shift+F2 to log."
echo ""

PRESETS=("1.0" "0.85" "0.77" "0.67")
NAMES=("native" "quality" "balanced" "performance")

for i in "${!PRESETS[@]}"; do
    scale="${PRESETS[$i]}"
    name="${NAMES[$i]}"
    outdir="$OUTPUT_DIR/$name"
    mkdir -p "$outdir"

    # Set render scale in optimum.json
    if [[ -f "$CONFIG" ]]; then
        python3 -c "
import json, sys
with open('$CONFIG') as f: data = json.load(f)
data['RenderScale'] = $scale
with open('$CONFIG', 'w') as f: json.dump(data, f, indent=2)
"
    fi

    echo "─── Run $((i+1))/4: ${name^^} (scale=$scale) ───"
    read -rp "Press ENTER to launch..."

    MANGOHUD_CONFIG="log_duration=$DURATION,log_interval=0,output_folder=$outdir,toggle_logging=Shift_L+F2" \
        mangohud "$OPTIMUM_DIR/run.sh" || true

    echo ""
done

echo "─── Processing results ───"
echo ""

python3 - "$OUTPUT_DIR" << 'PYTHON'
import sys, os, statistics, glob

def parse_mangohud_csv(path):
    frametimes = []
    with open(path) as f:
        lines = f.readlines()
    header_idx = None
    for i, line in enumerate(lines):
        if 'frametime' in line.lower() and 'fps' in line.lower():
            header_idx = i
            break
    if header_idx is None:
        return []
    headers = lines[header_idx].strip().split(',')
    ft_col = None
    for i, h in enumerate(headers):
        if h.strip().lower() == 'frametime':
            ft_col = i
            break
    if ft_col is None:
        return []
    for line in lines[header_idx+1:]:
        parts = line.strip().split(',')
        if len(parts) > ft_col:
            try:
                val = float(parts[ft_col])
                if val > 0:
                    frametimes.append(val)
            except ValueError:
                pass
    return frametimes

def percentile(data, p):
    s = sorted(data)
    return s[min(int(len(s) * p / 100), len(s) - 1)]

output_dir = sys.argv[1]
presets = [("native", "1.00"), ("quality", "0.85"), ("balanced", "0.77"), ("performance", "0.67")]
results = {}

print("╔═══════════════════════════════════════════════════════════════╗")
print("║          RENDER SCALE BENCHMARK RESULTS                      ║")
print("╠═══════════════════════════════════════════════════════════════╣")

for name, scale in presets:
    csvs = glob.glob(os.path.join(output_dir, name, "*.csv"))
    csvs = [c for c in csvs if "summary" not in c]
    if not csvs:
        print(f"  {name:12s} (scale {scale}): NO DATA")
        continue
    ft = parse_mangohud_csv(sorted(csvs)[-1])
    if not ft:
        print(f"  {name:12s} (scale {scale}): NO FRAMETIME DATA")
        continue
    avg = statistics.mean(ft)
    p50 = percentile(ft, 50)
    p95 = percentile(ft, 95)
    p99 = percentile(ft, 99)
    fps = 1000.0 / avg
    results[name] = {'avg': avg, 'fps': fps, 'p95': p95, 'p99': p99, 'frames': len(ft)}
    print(f"  {name:12s} (scale {scale}): {fps:6.1f} FPS | avg {avg:.2f}ms | P95 {p95:.2f}ms | P99 {p99:.2f}ms | {len(ft)} frames")

print("╠═══════════════════════════════════════════════════════════════╣")

if 'native' in results:
    base_fps = results['native']['fps']
    print("  GAINS vs Native:")
    for name, _ in presets[1:]:
        if name in results:
            gain = (results[name]['fps'] - base_fps) / base_fps * 100
            print(f"    {name:12s}: {gain:+.1f}% FPS")

print("╚═══════════════════════════════════════════════════════════════╝")
PYTHON

echo ""
echo "Raw CSVs: $OUTPUT_DIR/"
