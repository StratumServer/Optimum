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
grep -q -- "--require-upload-visibility" "$bench" || fail "the issue gate must be required on six-run measurements"
grep -q -- "--no-optimum-stream-log" "$bench" || fail "a telemetry-disabled control run must be supported"
grep -q 'REQUIRE_UPLOAD_VISIBILITY=$REQUIRE_UPLOAD_VISIBILITY' "$bench" || fail "run markers must record whether visibility evidence is mandatory"
grep -q 'exactly six 60-second runs' "$bench" || fail "the issue #550 gate must enforce its six-run 60-second protocol"
grep -q 'sleep "$DURATION"' "$bench" || fail "the measured flight must run to one uninterrupted duration deadline"
grep -q '2_mid_flight' "$bench" && fail "a synchronous mid-flight screenshot must not extend or stall the measured interval"
grep -q 'assemblies.sha256' "$bench" || fail "run artifacts must preserve the client assembly hashes"
grep -q 'clientsettings-effective.json' "$bench" || fail "run artifacts must preserve the effective non-secret client settings"
grep -q 'chunkVerticesUploadRateLimiter' "$bench" || fail "run artifacts must preserve the vanilla vertex limiter setting"
grep -q 'opengl_version=' "$bench" || fail "the manifest must preserve the graphics driver version"


# The flight must be a real one: normal terrain, sprint (Control) instead of sneak (Shift), a session
# prepared and verified through the meter mod, and a warm-up that waits for the start area.
meter="$repo_root/tools/ChunkFlightMeter/ChunkFlightMeter.cs"
grep -q 'PLAYSTYLE:-preset-exploration' "$bench" || fail "the default playstyle must be the preset-exploration langCode (normal terrain)"
grep -q 'keydown Control_L keydown w' "$bench" || fail "the flight must hold sprint (Control_L) and forward"
grep -q 'Shift_L' "$bench" && fail "Shift is sneak, not sprint"
grep -q 'CHUNKFLIGHT_SETUP="$SETUP"' "$bench" || fail "runs must pass the start position and heading to the meter mod"
grep -q 'ChunkFlightMeter: setup complete' "$bench" || fail "runs must wait for the meter mod setup line"
grep -q 'mode=Creative' "$bench" || fail "runs must verify creative free flight"
grep -q 'SETTLE_WINDOWS' "$bench" || fail "the warm-up must wait for quiet meter windows"
grep -q 'MIN_BLOCKS=' "$bench" || fail "runs must record the minimum distance"
grep -q 'rm -rf -- "$CURRENT_RUN_DIR/data"' "$bench" || fail "the throwaway data directory must be removed"
for command in '/gamemode creative' '.freemove 1' '.movespeed ' '/time stop' '/weather acp 0' '/weather setprecip -1' '/tp ='; do
    grep -qF "$command" "$meter" || fail "the meter mod must send $command"
done
for field in 'pos=' 'ahead=' 'chunks=' 'cols=' 'hitchMs=' 'setup complete'; do
    grep -qF "$field" "$meter" || fail "the meter mod must log $field"
done
grep -q 'SearchOption.AllDirectories' "$meter" && fail "a recursive /sys search hangs the render thread"

# Covers distance parsing, INVALID runs, the outrun warning and the per 100 block metrics.
python3 "$repo_root/scripts/analyze-chunk-flight.py" --self-test || fail "analysis self-test failed"

# A software renderer must be refused before any client starts.
stubs="$work/bin"
mkdir -p "$stubs"
for tool in Xvfb xdotool scrot xdpyinfo; do printf '#!/bin/sh\nexit 0\n' > "$stubs/$tool"; done
printf '#!/bin/sh\necho "OpenGL renderer string: llvmpipe (LLVM 20.1.2, 256 bits)"\n' > "$stubs/glxinfo"
chmod +x "$stubs"/*
echo '{"intSettings":{}}' > "$work/clientsettings.json"
set +e
output="$(PATH="$stubs:$PATH" SETTINGS_SOURCE="$work/clientsettings.json" bash "$bench" --gpu-wrapper "" --require-upload-visibility --duration 30 --runs 1 --output "$work/gateout" --arm "stub=$work" 2>&1)"
status=$?
set -e
[[ "$status" -eq 2 ]] || fail "the required visibility gate accepted a short/single run (exit $status): $output"
grep -q "exactly six 60-second runs" <<< "$output" || fail "visibility duration/run guard message missing: $output"

set +e
output="$(PATH="$stubs:$PATH" SETTINGS_SOURCE="$work/clientsettings.json" bash "$bench" --gpu-wrapper "" --output "$work/out" --arm "stub=$work" 2>&1)"
status=$?
set -e
[[ "$status" -eq 3 ]] || fail "software renderer was not refused (exit $status): $output"
grep -q "Software frame times" <<< "$output" || fail "refusal message missing: $output"
[[ ! -d "$work/out/runs" ]] || [[ -z "$(ls -A "$work/out/runs")" ]] || fail "a run started on a software renderer"

# A client that dies before the world loads must fail loudly, name the log and leave no data directory.
printf '#!/bin/sh\necho "OpenGL renderer string: Test GPU"\n' > "$stubs/glxinfo"
printf '#!/bin/sh\necho "stub client exits"\nexit 1\n' > "$stubs/dotnet"
chmod +x "$stubs"/*
mkdir -p "$work/client"
touch "$work/client/Vintagestory.dll"
set +e
output="$(PATH="$stubs:$PATH" SETTINGS_SOURCE="$work/clientsettings.json" bash "$bench" --gpu-wrapper "" --output "$work/out2" --arm "stub=$work/client" --runs 1 2>&1)"
status=$?
set -e
[[ "$status" -ne 0 ]] || fail "a client that exits early did not fail the benchmark: $output"
grep -q "the client exited before the world loaded" <<< "$output" || fail "early exit message missing: $output"
grep -q "logs: $work/out2/runs/stub-01/client-main.log" <<< "$output" || fail "the failure does not name the log: $output"
[[ -f "$work/out2/runs/stub-01/stdout.log" ]] || fail "the client output of the failed run was not kept"
[[ ! -e "$work/out2/runs/stub-01/data" ]] || fail "the data directory of a failed run was not removed"
[[ ! -e "$work/out2/runs/stub-01/markers.env" ]] || fail "a failed run must not look like a finished one"

# A run that did not fly is INVALID in the summary.
run="$work/out3/runs/stub-01"
mkdir -p "$run"
printf 'ARM=stub\nFLIGHT_START_EPOCH=1794400000\nFLIGHT_END_EPOCH=1794400020\nRENDERER=test\nMIN_BLOCKS=400\n' > "$run/markers.env"
python3 - "$run/client-main.log" <<'PY'
import datetime, sys
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    for i in range(0, 5):
        stamp = datetime.datetime.fromtimestamp(1794400000 + 5 * i).strftime("%d.%m.%Y %H:%M:%S")
        handle.write(f"{stamp} [Notification] ChunkFlightMeter: frameUs=n100/p50 9000/p95 9000/p99 9000/max 9000, over33ms=0, over50ms=0, over100ms=0, "
                     f"hitchMs=0, pos=512000.0,3.0,{512000.0 + i * 5.9:.1f}, ahead=480, chunks=0, cols=0, load1=1.00, cpuMhz=3000, frameMeanUs=9000, frameBuckets=52:100\n")
PY
output="$(python3 "$repo_root/scripts/analyze-chunk-flight.py" "$work/out3")"
grep -q "stub-01: INVALID, flew 24 blocks, below the 400 block minimum" <<< "$output" || fail "a walk was not reported INVALID: $output"

echo "chunk flight benchmark checks passed"
