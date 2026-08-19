#!/usr/bin/env bash
# Paired CPU + GC capture of a running Vintage Story client during a teleport
# chunk burst. Produces a .nettrace plus a .speedscope.json for browser viewing.
#
# Usage: trace-teleport.sh <vanilla|optimum> [duration]
#   duration: dotnet-trace --duration format, default 00:00:45
#
# Protocol (run twice, once per client, same world copy, same settings, VD 32):
#   1. Stand still 10 seconds (baseline warm frame).
#   2. /tp ~2000 ~ ~   (burst: chunk load + mesh + upload).
#   3. Wait for the chunk burst to settle (frame time stabilizes).
#   4. /tp back to origin (second burst).
#   5. The script ends by duration or you press Enter to stop early.
#
# Providers:
#   Microsoft-DotNETCore-SampleProfiler: CPU call stacks at ~10ms interval.
#   Microsoft-Windows-DotNETRuntime keyword 0x1 (GC): GC pause events.
#   Both in one capture; --profile cpu-sampling would fight --clrevents so we
#   use explicit --providers instead.
#
# Also run gcdump before and after one teleport per client for mesh-buffer churn
# analysis (feeds queue rank 11). The script offers to take one after the capture.
#
# Reading the output:
#   Load the .speedscope.json at https://www.speedscope.app/.
#   Sandwich view, compare vanilla vs Optimum side by side:
#     - ChunkTesselatorManager.TesselateChunk, .OnBeforeFrame
#     - ChunkTesselator.NowProcessChunk, BuildBlockPolygons*
#     - TesselatedChunk upload path through ChunkRenderer
#     - GL buffer uploads in ClientPlatformWindows
#     - GC pause events
#     - SystemRenderEntities.OnBeforeRender (C1 gate)
#     - GetEntitiesAround (B4 gate)
#
# Deliverable: docs/benchmarks/teleport-trace-<date>.md with a method/inclusive%
# delta table and one verdict per gated queue item: scheduled or parked.

set -euo pipefail

LABEL="${1:?Usage: trace-teleport.sh <vanilla|optimum> [duration]}"
DURATION="${2:-00:00:45}"
OUT_DIR="benchmarks/traces"
mkdir -p "$OUT_DIR"

# Ensure dotnet-trace is available.
if ! command -v dotnet-trace >/dev/null 2>&1; then
    echo "Installing dotnet-trace..."
    dotnet tool install --global dotnet-trace
fi

# Find the running Vintage Story process.
PID="$(dotnet-trace ps 2>/dev/null | awk '/[Vv]intagestory|Optimum/ {print $1; exit}')"
if [ -z "${PID:-}" ]; then
    echo "No running Vintagestory/Optimum process found."
    echo "Launch the client, load a world at VD 32, then re-run this script."
    exit 1
fi

TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$OUT_DIR/teleport-$LABEL-$TIMESTAMP.nettrace"

echo "=== Optimum trace-teleport ==="
echo "  Label:    $LABEL"
echo "  PID:      $PID"
echo "  Duration: $DURATION"
echo "  Output:   $OUT"
echo ""
echo "Protocol:"
echo "  1. Stand still 10 seconds."
echo "  2. /tp ~2000 ~ ~"
echo "  3. Wait for chunks to settle."
echo "  4. /tp back to origin."
echo "  5. Script ends at $DURATION or press Ctrl+C."
echo ""
echo "Starting capture in 3 seconds..."
sleep 3

dotnet-trace collect -p "$PID" \
    --providers "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x1:4" \
    --duration "$DURATION" \
    -o "$OUT"

echo ""
echo "Converting to speedscope format..."
dotnet-trace convert "$OUT" --format speedscope
SPEEDSCOPE="${OUT%.nettrace}.speedscope.json"
echo "  -> $SPEEDSCOPE"
echo "  Load at https://www.speedscope.app/"

# Offer gcdump collection.
echo ""
read -rp "Collect a GC heap dump now? (y/N) " GCDUMP_ANSWER
if [[ "${GCDUMP_ANSWER:-n}" =~ ^[Yy] ]]; then
    GCDUMP_OUT="$OUT_DIR/gcdump-$LABEL-$TIMESTAMP.gcdump"
    if ! command -v dotnet-gcdump >/dev/null 2>&1; then
        echo "Installing dotnet-gcdump..."
        dotnet tool install --global dotnet-gcdump
    fi
    dotnet-gcdump collect -p "$PID" -o "$GCDUMP_OUT"
    echo "  -> $GCDUMP_OUT"
fi

echo ""
echo "Done. Next steps:"
echo "  - Run the same protocol with the other client (vanilla or optimum)."
echo "  - Compare both .speedscope.json files side by side."
echo "  - Write docs/benchmarks/teleport-trace-$TIMESTAMP.md with the"
echo "    method/inclusive%/delta table and queue-item verdicts."
