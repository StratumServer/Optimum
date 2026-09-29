#!/bin/bash
# Close the client. Prefers a clean window close (no shutdown race), falls back to
# SIGTERM by PID. Never pattern-kill from a shell that also contains the launch
# text: the pattern matches the calling shell and kills it (exit 144).
if command -v xdotool >/dev/null; then
  for w in $(xdotool search --name '^Vintage Story$' 2>/dev/null); do xdotool windowactivate --sync "$w" 2>/dev/null; xdotool key --window "$w" alt+F4; done
  sleep 3
fi
for p in $(ps -eo pid,cmd | grep "dotnet [V]intagestory.dll" | awk '{print $1}'); do kill "$p" 2>/dev/null; done
sleep 1
ps -eo pid,cmd | grep -c "dotnet [V]intagestory.dll" | sed 's/^/remaining: /'
