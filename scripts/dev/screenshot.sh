#!/bin/bash
# Screenshot the active window (the game) to a file, then look at it with the Read tool.
OUT="${1:-/tmp/optimum-shot.png}"
if command -v spectacle >/dev/null; then spectacle -b -n -a -o "$OUT"; else import -window "$(xdotool getactivewindow)" "$OUT"; fi
echo "$OUT"
