#!/usr/bin/env bash
# Automated headless chunk flight benchmark via Xvfb, MangoHud, and xdotool.
# Usage: bash scripts/benchmark-chunk-flight.sh [--duration 15] [--world "world.vcdbs"]
set -euo pipefail

DURATION="${DURATION:-15}"
WORLD="${WORLD:-zaldaryons adventure story.vcdbs}"
DISPLAY_NUM="${DISPLAY_NUM:-:99}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET_DIR="${TARGET_DIR:-$REPO_ROOT/.vanilla/linux-x64/vintagestory}"
OUTPUT_DIR="${OUTPUT_DIR:-/tmp/optimum-flight-bench-$(date +%Y%m%d-%H%M%S)}"
DATA_PATH="${DATA_PATH:-$HOME/.config/VintagestoryData}"

command -v Xvfb >/dev/null 2>&1 || { echo "ERROR: Xvfb is required." >&2; exit 1; }
command -v xdotool >/dev/null 2>&1 || { echo "ERROR: xdotool is required." >&2; exit 1; }
command -v mangohud >/dev/null 2>&1 || { echo "ERROR: mangohud is required." >&2; exit 1; }

mkdir -p "$OUTPUT_DIR"

# Start Xvfb if not running on DISPLAY_NUM
XVFB_STARTED=0
if ! pgrep -f "Xvfb $DISPLAY_NUM" >/dev/null 2>&1; then
    echo "Starting Xvfb on $DISPLAY_NUM..."
    Xvfb "$DISPLAY_NUM" -screen 0 1920x1080x24 +extension GLX +render -noreset >/dev/null 2>&1 &
    XVFB_PID=$!
    XVFB_STARTED=1
    sleep 1
fi

cleanup() {
    if [[ "$XVFB_STARTED" -eq 1 && -n "${XVFB_PID:-}" ]]; then
        kill "$XVFB_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT

echo "╔══════════════════════════════════════════════╗"
echo "║  Automated Headless Chunk Flight Benchmark   ║"
echo "╠══════════════════════════════════════════════╣"
echo "║  Target:   $TARGET_DIR"
echo "║  World:    $WORLD"
echo "║  Display:  $DISPLAY_NUM (Headless X11)"
echo "║  Duration: ${DURATION}s flight"
echo "║  Output:   $OUTPUT_DIR"
echo "╚══════════════════════════════════════════════╝"
echo ""

MANGOHUD_LOG_DIR="$OUTPUT_DIR/mangohud"
mkdir -p "$MANGOHUD_LOG_DIR"

CLIENT_LOG="$DATA_PATH/Logs/client-main.log"
INITIAL_LOG_SIZE=0
if [[ -f "$CLIENT_LOG" ]]; then
    INITIAL_LOG_SIZE=$(wc -c < "$CLIENT_LOG")
fi

echo "Launching Vintage Story..."
cd "$TARGET_DIR"
DISPLAY="$DISPLAY_NUM" MANGOHUD=1 \
    MANGOHUD_CONFIG="output_folder=$MANGOHUD_LOG_DIR,autostart_log=1,log_interval=50,cpu_stats=1,gpu_stats=1,fps=1,frametime=1" \
    mangohud dotnet Vintagestory.dll --dataPath "$DATA_PATH" -o "$WORLD" >/dev/null 2>&1 &
GAME_PID=$!

echo "Waiting for level finalize..."
READY=0
for i in {1..90}; do
    if ! kill -0 "$GAME_PID" 2>/dev/null; then
        echo "ERROR: Game process exited prematurely." >&2
        exit 1
    fi
    if [[ -f "$CLIENT_LOG" ]]; then
        CURRENT_SIZE=$(wc -c < "$CLIENT_LOG")
        if [[ "$CURRENT_SIZE" -gt "$INITIAL_LOG_SIZE" ]]; then
            if tail -c +"$((INITIAL_LOG_SIZE + 1))" "$CLIENT_LOG" | grep -q "Received level finalize"; then
                READY=1
                break
            fi
        fi
    fi
    sleep 1
done

if [[ "$READY" -eq 0 ]]; then
    echo "ERROR: Timed out waiting for world load." >&2
    kill -9 "$GAME_PID" 2>/dev/null || true
    exit 1
fi

echo "World loaded. Settling..."
sleep 4
DISPLAY="$DISPLAY_NUM" scrot "$OUTPUT_DIR/1_loaded.png" 2>/dev/null || true

# Focus and unpause
DISPLAY="$DISPLAY_NUM" xdotool key Escape 2>/dev/null || true
sleep 1
DISPLAY="$DISPLAY_NUM" scrot "$OUTPUT_DIR/2_unpaused.png" 2>/dev/null || true

echo "Starting ${DURATION}s sprint flight..."
DISPLAY="$DISPLAY_NUM" xdotool key space key space 2>/dev/null || true
sleep 0.5
DISPLAY="$DISPLAY_NUM" xdotool keydown Shift_L keydown w keydown space 2>/dev/null || true
sleep "$DURATION"
DISPLAY="$DISPLAY_NUM" xdotool keyup w keyup Shift_L keyup space 2>/dev/null || true
echo "Flight completed."

DISPLAY="$DISPLAY_NUM" scrot "$OUTPUT_DIR/3_after_flight.png" 2>/dev/null || true
sleep 1

# Gracefully request exit
DISPLAY="$DISPLAY_NUM" xdotool key Escape 2>/dev/null || true
sleep 1
kill "$GAME_PID" 2>/dev/null || true
for i in {1..10}; do
    kill -0 "$GAME_PID" 2>/dev/null || break
    sleep 1
done
kill -9 "$GAME_PID" 2>/dev/null || true

echo ""
echo "─── Processing Results ───"
CSV_FILE=$(find "$MANGOHUD_LOG_DIR" -name "*.csv" | head -n 1)
if [[ -f "$CSV_FILE" ]]; then
    python3 -c "
import csv, sys
import numpy as np

with open('$CSV_FILE', 'r', encoding='utf-8') as f:
    reader = csv.reader(f)
    next(reader); next(reader)
    fps, ft, cpu = [], [], []
    for r in reader:
        if len(r) >= 3:
            try:
                fps.append(float(r[0]))
                ft.append(float(r[1]))
                cpu.append(float(r[2]))
            except ValueError: pass

if ft:
    ft_arr = np.array(ft)
    fps_arr = np.array(fps)
    print(f'Frames Recorded:     {len(ft_arr)}')
    print(f'Average FPS:         {np.mean(fps_arr):.2f}')
    print(f'1% Low FPS:          {np.percentile(fps_arr, 1):.2f}')
    print(f'Avg Frametime:       {np.mean(ft_arr):.2f} ms')
    print(f'P95 Frametime:       {np.percentile(ft_arr, 95):.2f} ms')
    print(f'P99 Frametime:       {np.percentile(ft_arr, 99):.2f} ms')
    print(f'Max Frametime Spike: {np.max(ft_arr):.2f} ms')
    print(f'Stutters (> 50ms):   {np.sum(ft_arr > 50.0)}')
"
fi

SERVER_LOG="$DATA_PATH/Logs/server-main.log"
if [[ -f "$SERVER_LOG" ]]; then
    echo ""
    echo "Latest Worldgen Pass Timing:"
    grep "Optimum worldgen pass timing" "$SERVER_LOG" | tail -n 3 || true
fi

echo ""
echo "Artifacts written to: $OUTPUT_DIR"
