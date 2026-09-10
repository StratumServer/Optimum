#!/bin/bash
# Prints which renderer the running/last client actually selected. A launch is
# not a verification: the bootstrap falls back to OpenGL silently.
LOG="${1:-/tmp/optimum-client.log}"
grep -m1 -E "\[Optimum\] (Vulkan|OpenGL) renderer" "$LOG" || echo "no renderer line yet in $LOG"
grep -m1 "Graphics Card Renderer" "$LOG"
# The exit status must reflect renderer detection only: at the main menu no
# savegame line exists yet and a bare grep would fail the whole script.
grep -m1 "Savegame .* loaded" "$LOG" || echo "no world loaded yet"
