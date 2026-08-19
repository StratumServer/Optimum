#!/usr/bin/env bash
# Isolated dedicated-server smoke test (docs/automated-testing.md, "vs-test-server").
#
# Boots the patched Optimum server (assembled from .vanilla/linux-x64 + the
# current bin/Release build via hardlinks, same technique as
# scripts/worldgen-benchmark.sh) into a throwaway dataPath, waits for
# "Entering runphase RunGame", optionally sends console commands, runs for
# --duration seconds, then sends SIGTERM and waits for a clean exit.
#
# A run fails when: startup times out, the process exits with a non-zero
# code, or the log contains a fatal/error/exception line.
#
# Usage:
#   scripts/tests/run-server-smoke.sh [options]
#
# Options:
#   --server-dir DIR      Use this pre-assembled server instead of building
#                          the patched one (e.g. .vanilla/linux-x64/vintagestory
#                          for a pure-vanilla baseline run).
#   --mod PATH             Extra mod path to load (repeatable).
#   --duration N            Seconds to stay in RunGame before stopping (default 20).
#   --startup-timeout N      Seconds to wait for RunGame before failing (default 90).
#   --command "STR"          Console command to send once ready (repeatable).
#   --port N                 Server port (default 0 = let the engine pick from config).
#   --config '<json>'        Extra --withconfig overrides, merged with the defaults.
#   --artifacts DIR           Evidence directory (default $HOME/.local/state/vs-test/runs/<ts>).
#   --keep-data                Keep the generated dataPath after the run.
#   --dry-run                   Print the launch plan and exit.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VANILLA_SERVER="$REPO_ROOT/.vanilla/linux-x64/vintagestory"
PATCHED_DLLS="$REPO_ROOT/bin/Release/net10.0"
PATCHED_LIB_DLL="$REPO_ROOT/build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll"

SERVER_DIR=""
MODS=()
DURATION=20
STARTUP_TIMEOUT=90
COMMANDS=()
PORT=0
EXTRA_CONFIG=""
ARTIFACTS_ROOT="${XDG_STATE_HOME:-$HOME/.local/state}/vs-test/runs"
ARTIFACTS_DIR=""
KEEP_DATA=false
DRY_RUN=false

while [ $# -gt 0 ]; do
    case "$1" in
        --server-dir) SERVER_DIR="$2"; shift 2 ;;
        --mod) MODS+=("$2"); shift 2 ;;
        --duration) DURATION="$2"; shift 2 ;;
        --startup-timeout) STARTUP_TIMEOUT="$2"; shift 2 ;;
        --command) COMMANDS+=("$2"); shift 2 ;;
        --port) PORT="$2"; shift 2 ;;
        --config) EXTRA_CONFIG="$2"; shift 2 ;;
        --artifacts) ARTIFACTS_DIR="$2"; shift 2 ;;
        --keep-data) KEEP_DATA=true; shift ;;
        --dry-run) DRY_RUN=true; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 64 ;;
    esac
done

if [ ! -d "$VANILLA_SERVER" ]; then
    echo "ERROR: vanilla Linux server not found at $VANILLA_SERVER (run 'make bootstrap' first)" >&2
    exit 1
fi

TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
[ -n "$ARTIFACTS_DIR" ] || ARTIFACTS_DIR="$ARTIFACTS_ROOT/$TIMESTAMP"
mkdir -p "$ARTIFACTS_DIR"

assemble_patched_server() {
    local dir="$REPO_ROOT/.build/vs-test-server-patched"
    rm -rf "$dir"
    mkdir -p "$dir"
    find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type f -exec cp -l --target-directory="$dir" {} +
    find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type d ! -name Mods -exec sh -c 'ln -s "$1" "$2/$(basename "$1")"' _ {} "$dir" \;
    mkdir -p "$dir/Mods"
    find "$VANILLA_SERVER/Mods" -mindepth 1 -maxdepth 1 -exec cp -l --target-directory="$dir/Mods" {} +
    find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type l -exec cp -a --target-directory="$dir" {} +

    if [ ! -f "$PATCHED_DLLS/VintagestoryAPI.dll" ]; then
        echo "ERROR: build artifacts missing under $PATCHED_DLLS (run 'make build' first)" >&2
        exit 1
    fi
    # Mirrors the Makefile 'deploy' target's file set (VintagestoryAPI/VSEssentials/
    # VSSurvivalMod are source-patched during bootstrap, not Cecil IL-patched; only
    # VintagestoryLib goes through the Cecil transplant, hence the separate donor path).
    cp --remove-destination "$PATCHED_DLLS/VintagestoryAPI.dll" "$dir/VintagestoryAPI.dll"
    cp --remove-destination "$PATCHED_DLLS/Optimum.Api.Contracts.dll" "$dir/Optimum.Api.Contracts.dll"
    cp --remove-destination "$PATCHED_DLLS/Optimum.GameContent.dll" "$dir/Optimum.GameContent.dll"
    cp --remove-destination "$PATCHED_DLLS/VSEssentials.dll" "$dir/Mods/VSEssentials.dll"
    cp --remove-destination "$PATCHED_DLLS/VSSurvivalMod.dll" "$dir/Mods/VSSurvivalMod.dll"
    if [ -f "$PATCHED_LIB_DLL" ]; then
        cp --remove-destination "$PATCHED_LIB_DLL" "$dir/VintagestoryLib.dll"
    else
        echo "WARNING: $PATCHED_LIB_DLL not found, using vanilla VintagestoryLib.dll (run 'make patch-il' for the Cecil-patched donor)" >&2
    fi
    echo "$dir"
}

if [ -z "$SERVER_DIR" ]; then
    SERVER_DIR="$(assemble_patched_server)"
fi

DATA_PATH="$ARTIFACTS_DIR/dataPath"
LOG_PATH="$ARTIFACTS_DIR/server-log.txt"
mkdir -p "$DATA_PATH"

LAUNCH_ARGS=(--dataPath "$DATA_PATH" --logPath "$DATA_PATH/Logs" --port "$PORT")
for mod in "${MODS[@]:-}"; do
    [ -n "$mod" ] && LAUNCH_ARGS+=(--addModPath "$mod")
done

CONFIG_JSON="{ MaxClients: 0, PassTimeWhenEmpty: false"
[ -n "$EXTRA_CONFIG" ] && CONFIG_JSON="${CONFIG_JSON}, ${EXTRA_CONFIG#\{}"
CONFIG_JSON="${CONFIG_JSON%,}"
[[ "$CONFIG_JSON" == *"}" ]] || CONFIG_JSON="${CONFIG_JSON} }"
LAUNCH_ARGS+=(--withconfig="$CONFIG_JSON")

if $DRY_RUN; then
    echo "server-dir: $SERVER_DIR"
    echo "dataPath:   $DATA_PATH"
    echo "artifacts:  $ARTIFACTS_DIR"
    echo "command:    cd $SERVER_DIR && dotnet VintagestoryServer.dll ${LAUNCH_ARGS[*]}"
    [ ${#COMMANDS[@]} -gt 0 ] && printf 'console cmd: %s\n' "${COMMANDS[@]}"
    exit 0
fi

STDIN_FIFO="$ARTIFACTS_DIR/stdin.fifo"
mkfifo "$STDIN_FIFO"

cd "$SERVER_DIR"
START_EPOCH=$(date +%s)
# Start the reader (background job's redirection opens the FIFO read-end in the
# child) before opening our write-end on fd 9 - opening either end of a FIFO
# blocks until the other end is open, so the order here is load-bearing.
dotnet VintagestoryServer.dll "${LAUNCH_ARGS[@]}" < "$STDIN_FIFO" > "$LOG_PATH" 2>&1 &
SERVER_PID=$!
exec 9>"$STDIN_FIFO"

READY=false
DEADLINE=$((SECONDS + STARTUP_TIMEOUT))
while kill -0 "$SERVER_PID" 2>/dev/null && ((SECONDS < DEADLINE)); do
    if grep -q "Entering runphase RunGame" "$LOG_PATH" 2>/dev/null; then
        READY=true
        break
    fi
    sleep 1
done

if $READY; then
    for cmd in "${COMMANDS[@]:-}"; do
        [ -n "$cmd" ] && echo "$cmd" >&9
        sleep 1
    done
    sleep "$DURATION"
else
    echo "Startup timed out after ${STARTUP_TIMEOUT}s" | tee -a "$LOG_PATH" >&2
fi

kill -TERM "$SERVER_PID" 2>/dev/null || true
SHUTDOWN_DEADLINE=$((SECONDS + 15))
while kill -0 "$SERVER_PID" 2>/dev/null && ((SECONDS < SHUTDOWN_DEADLINE)); do
    sleep 1
done
kill -KILL "$SERVER_PID" 2>/dev/null || true
EXIT_CODE=0
wait "$SERVER_PID" 2>/dev/null || EXIT_CODE=$?
exec 9>&-
rm -f "$STDIN_FIFO"

ELAPSED=$(($(date +%s) - START_EPOCH))
# Anchored on VS's own log-level tags and .NET exception lines, not a bare
# substring match - "no errors"/"ErrorReporter" (both real vanilla log lines)
# would otherwise false-positive.
ERROR_LINES="$(grep -icE '\[Server (Error|Fatal)\]|^[A-Za-z.]+Exception:|Critical error occurred|Unhandled exception' "$LOG_PATH" 2>/dev/null || true)"
DLL_HASH="$(sha256sum "$SERVER_DIR/VintagestoryServer.dll" 2>/dev/null | cut -d' ' -f1)"

PASS=true
FAIL_REASON=""
if ! $READY; then
    PASS=false
    FAIL_REASON="startup timeout"
elif [ "$EXIT_CODE" -ne 0 ] && [ "$EXIT_CODE" -ne 143 ] && [ "$EXIT_CODE" -ne 137 ]; then
    PASS=false
    FAIL_REASON="non-zero exit ($EXIT_CODE)"
elif [ "${ERROR_LINES:-0}" -gt 0 ]; then
    PASS=false
    FAIL_REASON="$ERROR_LINES fatal/error/exception line(s) in log"
fi

cat > "$ARTIFACTS_DIR/result.json" <<JSON
{
  "pass": $PASS,
  "failReason": "$FAIL_REASON",
  "serverDir": "$SERVER_DIR",
  "serverDllHash": "$DLL_HASH",
  "dataPath": "$DATA_PATH",
  "port": $PORT,
  "durationSeconds": $ELAPSED,
  "exitCode": $EXIT_CODE,
  "reachedRunGame": $READY,
  "errorLineCount": ${ERROR_LINES:-0},
  "commands": $(printf '%s\n' "${COMMANDS[@]:-}" | jq -R . | jq -s . 2>/dev/null || echo "[]"),
  "timestamp": "$TIMESTAMP"
}
JSON

if ! $KEEP_DATA && $PASS; then
    rm -rf "$DATA_PATH"
fi

echo "Artifacts: $ARTIFACTS_DIR"
if $PASS; then
    echo "PASS ($ELAPSED s, exit $EXIT_CODE, $ERROR_LINES error line(s))"
    exit 0
else
    echo "FAIL: $FAIL_REASON"
    tail -30 "$LOG_PATH" >&2
    exit 1
fi
