#!/usr/bin/env bash
# benchmark-loadtime.sh - Measures SP load-time performance with the client rendering.
# Runs a fixed singleplayer world through a teleport route, recording frame time
# and tessellation counters from the Optimum diagnostics system.
#
# Requirements:
#   - A saved SP world at the expected path (see WORLD_NAME below)
#   - The Optimum client built and installed (make deploy)
#   - The client must be able to start and connect to the embedded SP server
#
# Usage:
#   bash scripts/benchmark-loadtime.sh [--runs N] [--world NAME] [--output DIR]
#
# Outputs:
#   - Frame time P95/P99 from client-main.log stutterwatch entries
#   - Chunk tessellation counters from .optimum status
#   - Chunk gen time from server log
#   - Results written to OUTPUT_DIR/loadtime-bench-$(date).csv
#
# This script does NOT run headless. The constraint being measured is cores
# shared between tessellation and GL upload, which a headless run cannot reproduce.

set -euo pipefail

RUNS="${RUNS:-3}"
WORLD_NAME="${WORLD_NAME:-benchmark-world}"
OUTPUT_DIR="${OUTPUT_DIR:-docs/benchmarks/results}"
TELEPORT_DELAY=30  # seconds to wait after teleport before collecting metrics
STARTUP_DELAY=45   # seconds to wait for world to fully load

# Parse args
while [[ $# -gt 0 ]]; do
    case $1 in
        --runs) RUNS="$2"; shift 2 ;;
        --world) WORLD_NAME="$2"; shift 2 ;;
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        *) echo "Unknown arg: $1"; exit 1 ;;
    esac
done

mkdir -p "$OUTPUT_DIR"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
RESULTS_FILE="$OUTPUT_DIR/loadtime-bench-$TIMESTAMP.csv"

echo "Optimum loadtime benchmark"
echo "  Runs: $RUNS"
echo "  World: $WORLD_NAME"
echo "  Output: $RESULTS_FILE"
echo ""
echo "IMPORTANT: This script requires manual interaction."
echo "  1. Start the client with: make run OPEN_WORLD=$WORLD_NAME"
echo "  2. Wait for world load, then type: /tp 0 100 0"
echo "  3. Wait ${TELEPORT_DELAY}s for chunks to load"
echo "  4. Type: .optimum status"
echo "  5. Copy the output into $RESULTS_FILE"
echo ""
echo "Automated collection requires the VS client to support command-line"
echo "scripting (not yet available). For now, this script documents the"
echo "protocol and provides the analysis tooling."
echo ""

# Write CSV header
cat > "$RESULTS_FILE" << 'EOF'
run,config,frame_p95_ms,frame_p99_ms,tess_mean_ms,tess_queue_peak,tess_retries,gen_ms_per_col,notes
EOF

echo "Protocol:"
echo "  For each run (1..$RUNS):"
echo "    1. Cold-start: make run OPEN_WORLD=$WORLD_NAME"
echo "    2. Wait ${STARTUP_DELAY}s after main menu disappears"
echo "    3. .optimum stutterwatch 16"
echo "    4. /tp 5000 100 5000  (unexplored territory)"
echo "    5. Wait ${TELEPORT_DELAY}s"
echo "    6. .optimum status > record tessellation line"
echo "    7. .optimum stutterwatch (toggle off)"
echo "    8. Record frame P95/P99 from client-main.log stutter entries"
echo "    9. /quit"
echo ""
echo "After all runs, analyze with:"
echo "  python3 scripts/analyze-frametime-log.py $RESULTS_FILE"
echo ""
echo "Results file created: $RESULTS_FILE"
