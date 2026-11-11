#!/usr/bin/env bash
# Validates the chunk flight benchmark scripts without launching a client.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
bench="$repo_root/scripts/benchmark-chunk-flight.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

bash -n "$bench" || fail "benchmark script has a syntax error"
bash -n "$repo_root/scripts/prepare-flight-arms.sh" || fail "arms script has a syntax error"

# The benchmark must sample every frame on a fresh seeded world.
grep -q "log_interval=0" "$bench" || fail "MangoHud must sample every frame (log_interval=0)"
grep -q "log_interval=50" "$bench" && fail "MangoHud log_interval=50 hides single frame spikes"
grep -q -- "--rndWorld" "$bench" || fail "each run needs a fresh world (--rndWorld)"
grep -q "OPTIMUM_CHUNK_STREAM_LOG=1" "$bench" || fail "runs must enable the chunk stream log"

python3 "$repo_root/scripts/analyze-chunk-flight.py" --self-test || fail "analysis self-test failed"

# A software renderer must be refused before any client starts.
stubs="$work/bin"
mkdir -p "$stubs"
for tool in Xvfb xdotool scrot xdpyinfo; do printf '#!/bin/sh\nexit 0\n' > "$stubs/$tool"; done
printf '#!/bin/sh\necho "OpenGL renderer string: llvmpipe (LLVM 20.1.2, 256 bits)"\n' > "$stubs/glxinfo"
chmod +x "$stubs"/*
echo '{"intSettings":{}}' > "$work/clientsettings.json"
set +e
output="$(PATH="$stubs:$PATH" SETTINGS_SOURCE="$work/clientsettings.json" bash "$bench" --gpu-wrapper "" --output "$work/out" --arm "stub=$work" 2>&1)"
status=$?
set -e
[[ "$status" -eq 3 ]] || fail "software renderer was not refused (exit $status): $output"
grep -q "Software frame times" <<< "$output" || fail "refusal message missing: $output"
[[ ! -d "$work/out/runs" ]] || [[ -z "$(ls -A "$work/out/runs")" ]] || fail "a run started on a software renderer"

echo "chunk flight benchmark checks passed"
