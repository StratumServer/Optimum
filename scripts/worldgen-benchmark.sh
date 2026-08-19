#!/usr/bin/env bash
# Compatibility entry point for the precise Linux worldgen suite.
#
# Usage: ./scripts/worldgen-benchmark.sh [trials] [suite options]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
    exec "$SCRIPT_DIR/worldgen-benchmark-suite.sh" --help
fi
TRIALS="${1:-12}"
if [ "$#" -gt 0 ]; then
    shift
fi

exec "$SCRIPT_DIR/worldgen-benchmark-suite.sh" --trials "$TRIALS" "$@"
