#!/usr/bin/env bash
# Worldgen parity harness: generate chunks with a fixed seed on both vanilla
# and patched servers, then compare per-table chunk database divergence
# against a measured vanilla noise floor.
#
# Usage: ./scripts/worldgen-parity-harness.sh [--refresh-baseline]
#
# Vintage Story worldgen is NOT run-to-run deterministic, even single-threaded:
# the chunk queue order depends on wall-clock timing (creationTime ties), and
# generators read neighbour chunks whose pass state depends on that order.
# Measured on 2026-07-16 (seed 42424242, 1800 chunks): two identical vanilla
# serial runs differ in ~99 chunks (5.5%), 2/49 mapchunks and 16/16 mapregions.
# Vanilla's own multithreaded worldgen (MaxWorldgenThreads=6) differs from
# vanilla serial by ~249 chunks (13.8%). A byte-exact hash gate is therefore
# unachievable BY VANILLA ITSELF and was replaced by a statistical gate:
#
#   patched divergence vs vanilla  <=  reference divergence + max(5, 3*sqrt(ref))
#
# where the reference is vanilla-vs-vanilla (serial mode) or
# vanillaMT-vs-vanillaSerial (when OPTIMUM_WORLDGEN_MT=1). The mapregion table
# is reported but not gated (structure/list ordering makes every region blob
# differ even between two identical vanilla runs). gamedata/playerdata carry
# timestamps and are excluded entirely.
#
# Baseline worlds are cached in .worldgen-parity-baseline/ keyed by the vanilla
# DLL hash + seed, so routine gate runs only boot the patched server once.
# Any worldgen exception or an Optimum scheduler fault in the patched log fails
# the gate regardless of divergence counts.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
VANILLA_SERVER="$PROJECT_ROOT/.vanilla/linux-x64/vintagestory"
PATCHED_DLLS="$PROJECT_ROOT/bin/Release/net10.0"
PATCHED_LIB_DLL="$PROJECT_ROOT/build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll"
HARNESS_DIR="$PROJECT_ROOT/.worldgen-parity"
BASELINE_DIR="$PROJECT_ROOT/.worldgen-parity-baseline"
SEED="42424242"
WORLD_NAME="parity-test"
GENERATE_SECONDS="${GENERATE_SECONDS:-180}"
SHUTDOWN_GRACE_SECONDS="${SHUTDOWN_GRACE_SECONDS:-15}"
RUN_AFTER_READY_SECONDS="${RUN_AFTER_READY_SECONDS:-0}"
# Work-stealing defaults ON in OptimumConfig, so the default gate exercises the
# multithreaded reference. Set OPTIMUM_WORLDGEN_MT=0 to gate the serial path;
# the value is forwarded to the patched server, which honors 0 as a force-off.
MT_MODE="${OPTIMUM_WORLDGEN_MT:-1}"

REFRESH_BASELINE=false
for arg in "$@"; do
    case "$arg" in
        --refresh-baseline) REFRESH_BASELINE=true ;;
        *) echo "Unknown argument: $arg" >&2; exit 64 ;;
    esac
done

if [ ! -f "$VANILLA_SERVER/VintagestoryServer.dll" ]; then
    echo "ERROR: vanilla server not found at $VANILLA_SERVER"
    exit 1
fi

setup_data_dir() {
    local datapath="$1"
    local server_dir="$2"
    mkdir -p "$datapath"

    # Generate a valid default config using the server itself
    cd "$server_dir"
    dotnet VintagestoryServer.dll --dataPath "$datapath" --genconfig > /dev/null 2>&1 || true

    dotnet VintagestoryServer.dll --dataPath "$datapath" \
        --setconfig="{ Port: 0, MaxClients: 0, PassTimeWhenEmpty: false }" \
        > /dev/null 2>&1 || true
}

run_server() {
    local label="$1"
    local server_dir="$2"
    local datapath="$3"
    local log_path="$4"

    echo "[$label] Booting server with seed $SEED (${GENERATE_SECONDS}s timeout)..."
    cd "$server_dir"

    dotnet VintagestoryServer.dll \
        --dataPath "$datapath" \
        --withconfig="{ WorldConfig: { Seed: '$SEED', WorldName: '$WORLD_NAME' } }" \
        > "$log_path" 2>&1 &
    local server_pid=$!
    local boot_deadline=$((SECONDS + GENERATE_SECONDS))
    local reached_run_game=false

    while kill -0 "$server_pid" 2>/dev/null && ((SECONDS < boot_deadline)); do
        if grep -q "Entering runphase RunGame" "$log_path" 2>/dev/null; then
            reached_run_game=true
            break
        fi
        sleep 1
    done

    if [ "$reached_run_game" = true ]; then
        sleep "$RUN_AFTER_READY_SECONDS"
    else
        echo "[$label] RunGame deadline expired."
    fi

    kill -TERM "$server_pid" 2>/dev/null || true
    local shutdown_deadline=$((SECONDS + SHUTDOWN_GRACE_SECONDS))
    while kill -0 "$server_pid" 2>/dev/null && ((SECONDS < shutdown_deadline)); do
        sleep 1
    done
    kill -KILL "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true

    echo "[$label] Server stopped at the RunGame boundary."
}

find_database() {
    local datapath="$1"
    find "$datapath/Saves" -type f -name "*.vcdbs" -print -quit 2>/dev/null
}

# Count rows of one table that differ (or exist on only one side) between two
# databases. Prints the count of run-A rows without an identical run-B row.
count_diff() {
    local table="$1"
    local db_a="$2"
    local db_b="$3"
    comm -23 \
        <(sqlite3 "$db_a" "SELECT position, hex(data) FROM $table ORDER BY position;" | sort) \
        <(sqlite3 "$db_b" "SELECT position, hex(data) FROM $table ORDER BY position;" | sort) \
        | wc -l
}

row_count() {
    local table="$1"
    local db="$2"
    sqlite3 "$db" "SELECT count(*) FROM $table;"
}

# Gate threshold: reference + max(5, 3*sqrt(reference)), rounded up.
gate_limit() {
    local ref="$1"
    awk -v r="$ref" 'BEGIN { m = 3 * sqrt(r); if (m < 5) m = 5; printf "%d\n", int(r + m + 0.999) }'
}

check_log_for_worldgen_errors() {
    local log_path="$1"
    local label="$2"
    local hits
    hits=$(grep -c -e "An error was thrown in pass" \
                   -e "Optimum worldgen scheduler stopped its workers" \
                   -e "Exception throwing during chunk" \
                   "$log_path" 2>/dev/null) || true
    if [ "${hits:-0}" -gt 0 ]; then
        echo "FAIL: $label log contains $hits worldgen error line(s):"
        grep -e "An error was thrown in pass" \
             -e "Optimum worldgen scheduler stopped its workers" \
             -e "Exception throwing during chunk" \
             "$log_path" | head -5
        return 1
    fi
    return 0
}

# === BASELINE (cached vanilla worlds) ===

VANILLA_FINGERPRINT="$(sha256sum "$VANILLA_SERVER/VintagestoryServer.dll" | cut -d' ' -f1)-seed$SEED"

baseline_valid() {
    [ -f "$BASELINE_DIR/meta.txt" ] \
        && [ "$(cat "$BASELINE_DIR/meta.txt")" = "$VANILLA_FINGERPRINT" ] \
        && [ -f "$BASELINE_DIR/vanilla-a.vcdbs" ] \
        && [ -f "$BASELINE_DIR/vanilla-b.vcdbs" ]
}

generate_serial_baseline() {
    echo "=== Generating vanilla baseline (two serial runs, noise floor) ==="
    rm -rf "$BASELINE_DIR"
    mkdir -p "$BASELINE_DIR"
    local run label datapath db
    for run in a b; do
        label="VANILLA-$(echo "$run" | tr '[:lower:]' '[:upper:]')"
        datapath="$HARNESS_DIR/vanilla-$run-data"
        setup_data_dir "$datapath" "$VANILLA_SERVER"
        run_server "$label" "$VANILLA_SERVER" "$datapath" "$BASELINE_DIR/vanilla-$run-log.txt"
        db=$(find_database "$datapath")
        if [ -z "$db" ]; then
            echo "ERROR: vanilla baseline run $run produced no world database." >&2
            exit 2
        fi
        cp "$db" "$BASELINE_DIR/vanilla-$run.vcdbs"
        # Keep the magic numbers config so the MT baseline can reuse it
        cp "$datapath/servermagicnumbers.json" "$BASELINE_DIR/servermagicnumbers.json" 2>/dev/null || true
    done
    echo "$VANILLA_FINGERPRINT" > "$BASELINE_DIR/meta.txt"
}

generate_mt_baseline() {
    echo "=== Generating vanilla-MT baseline (MaxWorldgenThreads=6) ==="
    local datapath="$HARNESS_DIR/vanilla-mt-data"
    setup_data_dir "$datapath" "$VANILLA_SERVER"
    if [ ! -f "$BASELINE_DIR/servermagicnumbers.json" ]; then
        echo "ERROR: no cached servermagicnumbers.json; refresh the serial baseline first." >&2
        exit 2
    fi
    sed 's/"MaxWorldgenThreads": *[0-9]*/"MaxWorldgenThreads": 6/' \
        "$BASELINE_DIR/servermagicnumbers.json" > "$datapath/servermagicnumbers.json"
    run_server "VANILLA-MT" "$VANILLA_SERVER" "$datapath" "$BASELINE_DIR/vanilla-mt-log.txt"
    local db
    db=$(find_database "$datapath")
    if [ -z "$db" ]; then
        echo "ERROR: vanilla-MT baseline run produced no world database." >&2
        exit 2
    fi
    cp "$db" "$BASELINE_DIR/vanilla-mt.vcdbs"
}

rm -rf "$HARNESS_DIR"
mkdir -p "$HARNESS_DIR"

if [ "$REFRESH_BASELINE" = true ] || ! baseline_valid; then
    generate_serial_baseline
fi
if [ "$MT_MODE" = "1" ] && [ ! -f "$BASELINE_DIR/vanilla-mt.vcdbs" ]; then
    generate_mt_baseline
fi

# === PATCHED ===

# Create a patched server: copy vanilla tree, overlay Optimum DLLs
PATCHED_SERVER="$HARNESS_DIR/patched-server"
mkdir -p "$PATCHED_SERVER"
find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type f -exec cp -l --target-directory="$PATCHED_SERVER" {} +
while IFS= read -r -d '' directory; do
    ln -s "$directory" "$PATCHED_SERVER/$(basename "$directory")"
done < <(find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type d -print0)
find "$VANILLA_SERVER" -mindepth 1 -maxdepth 1 -type l -exec cp -a --target-directory="$PATCHED_SERVER" {} +

cp --remove-destination "$PATCHED_DLLS/VintagestoryAPI-patched.dll" "$PATCHED_SERVER/VintagestoryAPI.dll"
cp --remove-destination "$PATCHED_DLLS/Optimum.Api.Contracts.dll" "$PATCHED_SERVER/Optimum.Api.Contracts.dll"
cp --remove-destination "$PATCHED_DLLS/Optimum.GameContent.dll" "$PATCHED_SERVER/Optimum.GameContent.dll"
cp --remove-destination "$PATCHED_DLLS/VSEssentials-patched.dll" "$PATCHED_SERVER/Mods/VSEssentials.dll"
cp --remove-destination "$PATCHED_DLLS/VSSurvivalMod-patched.dll" "$PATCHED_SERVER/Mods/VSSurvivalMod.dll"
echo "  Overlaid: VintagestoryAPI.dll (ABI-preserving Cecil patch)"
echo "  Overlaid: Optimum.Api.Contracts.dll"
echo "  Overlaid: exact Cecil-patched official mod assemblies"

# VintagestoryLib ships Cecil-patched, never recompiled - overlay the
# patcher's output, not a source build.
if [ -f "$PATCHED_LIB_DLL" ]; then
    cp --remove-destination "$PATCHED_LIB_DLL" "$PATCHED_SERVER/VintagestoryLib.dll"
    echo "  Overlaid: VintagestoryLib.dll (Cecil-patched)"
else
    echo "ERROR: patched VintagestoryLib.dll not found at $PATCHED_LIB_DLL (run 'make patch-il' first)" >&2
    exit 1
fi

PATCHED_DATA="$HARNESS_DIR/patched-data"
setup_data_dir "$PATCHED_DATA" "$PATCHED_SERVER"
# Only forward an explicit override; the default run exercises the shipped
# config default (work-stealing on) rather than an env-forced mode.
if [ -n "${OPTIMUM_WORLDGEN_MT:-}" ]; then
    export OPTIMUM_WORLDGEN_MT
fi
run_server "PATCHED" "$PATCHED_SERVER" "$PATCHED_DATA" "$HARNESS_DIR/patched-log.txt"
PATCHED_DB=$(find_database "$PATCHED_DATA")
if [ -z "$PATCHED_DB" ]; then
    echo "INCONCLUSIVE: patched server did not generate world data."
    echo "Check $HARNESS_DIR/patched-log.txt for errors."
    exit 2
fi

# === COMPARE ===

VANILLA_A="$BASELINE_DIR/vanilla-a.vcdbs"
VANILLA_B="$BASELINE_DIR/vanilla-b.vcdbs"

if [ "$MT_MODE" = "1" ]; then
    REF_DB="$BASELINE_DIR/vanilla-mt.vcdbs"
    REF_LABEL="vanillaMT-vs-vanillaA"
else
    REF_DB="$VANILLA_B"
    REF_LABEL="vanillaB-vs-vanillaA (noise floor)"
fi

echo ""
echo "=== WORLDGEN PARITY CHECK (mode: $([ "$MT_MODE" = "1" ] && echo multithreaded || echo serial)) ==="

FAILED=false
check_log_for_worldgen_errors "$HARNESS_DIR/patched-log.txt" "patched" || FAILED=true

printf "%-10s %8s %10s %10s %8s %s\n" "table" "rows" "reference" "patched" "limit" "verdict"
for table in chunk mapchunk; do
    rows=$(row_count "$table" "$VANILLA_A")
    rows_patched=$(row_count "$table" "$PATCHED_DB")
    ref=$(count_diff "$table" "$VANILLA_A" "$REF_DB")
    patched=$(count_diff "$table" "$VANILLA_A" "$PATCHED_DB")
    limit=$(gate_limit "$ref")
    if [ "$rows" != "$rows_patched" ]; then
        verdict="FAIL (row count $rows_patched != $rows)"
        FAILED=true
    elif [ "$patched" -le "$limit" ]; then
        verdict="PASS"
    else
        verdict="FAIL"
        FAILED=true
    fi
    printf "%-10s %8s %10s %10s %8s %s\n" "$table" "$rows" "$ref" "$patched" "$limit" "$verdict"
done

# mapregion blobs differ 16/16 even between two identical vanilla runs
# (structure list order); report only.
mr_ref=$(count_diff mapregion "$VANILLA_A" "$REF_DB")
mr_patched=$(count_diff mapregion "$VANILLA_A" "$PATCHED_DB")
mr_rows=$(row_count mapregion "$VANILLA_A")
printf "%-10s %8s %10s %10s %8s %s\n" "mapregion" "$mr_rows" "$mr_ref" "$mr_patched" "-" "(informational)"

echo ""
echo "Reference: $REF_LABEL"
if [ "$FAILED" = true ]; then
    echo "FAIL: patched worldgen diverges beyond the vanilla reference."
    exit 1
fi
echo "PASS: patched worldgen divergence is within the vanilla reference."
exit 0
