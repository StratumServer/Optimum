#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
    echo "Usage: $0 LOG_PATH EXPECTED_WORKERS" >&2
    exit 64
fi

log_path="$1"
expected_workers="$2"
scheduler_pattern='Optimum worldgen scheduler started with [0-9]+ worker threads\.'
adaptive_pattern='Optimum adaptive: workers'
disabled_pattern='Optimum worldgen parallelism disabled'

if [ ! -f "$log_path" ]; then
    echo "ERROR: worldgen run log does not exist: $log_path" >&2
    exit 1
fi

case "$expected_workers" in
    0|1|2|3) ;;
    *)
        echo "ERROR: expected worker count must fall between 0 and 3: $expected_workers" >&2
        exit 1
        ;;
esac

scheduler_count=$(grep -Ec "$scheduler_pattern" "$log_path" || true)
adaptive_count=$(grep -Fc "$adaptive_pattern" "$log_path" || true)
disabled_count=$(grep -Fc "$disabled_pattern" "$log_path" || true)

if [ "$adaptive_count" -ne 0 ]; then
    echo "ERROR: run changed the worldgen worker cap under an exact treatment: $log_path" >&2
    exit 1
fi

if [ "$expected_workers" -eq 0 ]; then
    if [ "$scheduler_count" -ne 0 ]; then
        echo "ERROR: serial run started a worldgen scheduler: $log_path" >&2
        exit 1
    fi
    exit 0
fi

if [ "$disabled_count" -ne 0 ]; then
    echo "ERROR: server safety checks disabled the requested worldgen workers: $log_path" >&2
    exit 1
fi

if [ "$scheduler_count" -ne 1 ]; then
    echo "ERROR: parallel run logged $scheduler_count scheduler starts, expected one: $log_path" >&2
    exit 1
fi

realized_workers=$(grep -E "$scheduler_pattern" "$log_path" | sed -E 's/.*started with ([0-9]+) worker threads\..*/\1/')
if [ "$realized_workers" -ne "$expected_workers" ]; then
    echo "ERROR: run requested $expected_workers workers but started $realized_workers: $log_path" >&2
    exit 1
fi
