#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VALIDATOR="$PROJECT_ROOT/scripts/validate-worldgen-run.sh"
POWERSHELL_VALIDATOR="$PROJECT_ROOT/scripts/validate-worldgen-run.ps1"
ANALYZER="$PROJECT_ROOT/scripts/analyze-worker-sweep.py"
COLLECTOR="$PROJECT_ROOT/scripts/collect-benchmark-log.py"
CSV_VALIDATOR="$PROJECT_ROOT/scripts/validate-worldgen-benchmark.py"
SUITE="$PROJECT_ROOT/scripts/worldgen-benchmark-suite.sh"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

expect_pass() {
    "$@" > /dev/null 2>&1 || fail "command failed: $*"
}

expect_fail() {
    if "$@" > /dev/null 2>&1; then
        fail "command passed: $*"
    fi
}

fixture_dir="$(mktemp -d)"
trap 'rm -rf "$fixture_dir"' EXIT

expect_pass python3 -m py_compile "$COLLECTOR" "$ANALYZER" "$CSV_VALIDATOR"
expect_pass bash -n "$SUITE"

collector_log="$fixture_dir/collector.log"
collector_markers="$fixture_dir/collector.markers"
printf '%s\n' \
    '13.8.2026 14:00:00 [Server Event] Loading 9x9x8 spawn chunks...' \
    '13.8.2026 14:00:01 [Server Notification] Entering runphase RunGame' \
    '13.8.2026 14:00:02 [Server Notification] Ok, added 49 columns, 0 left to add, waiting until these are done.' \
    '13.8.2026 14:00:03 [Server Notification] Ok, 49 columns, generated!' \
    '13.8.2026 14:00:04 [Server Notification] Entering runphase RunGame' \
    '13.8.2026 14:00:05 [Server Notification] Ok, 49 columns, generated!' \
    | "$COLLECTOR" "$collector_log" "$collector_markers"
grep -q '^spawn_start_ns=' "$collector_markers" || fail 'collector omitted spawn start marker'
grep -q '^spawn_end_ns=' "$collector_markers" || fail 'collector omitted spawn end marker'
grep -q '^pregen_start_ns=' "$collector_markers" || fail 'collector omitted pregen start marker'
grep -q '^pregen_end_ns=' "$collector_markers" || fail 'collector omitted pregen end marker'
python3 - "$collector_markers" <<'PY'
import sys
values = [int(line.split('=', 1)[1]) for line in open(sys.argv[1])]
if values != sorted(values) or len(set(values)) != len(values):
    raise SystemExit('collector markers are not strictly monotonic')
PY

csv_precision="$fixture_dir/precision.csv"
{
    printf '%s\n' 'trial,mode,workers,seconds,user_seconds,sys_seconds,cpu_percent,max_rss_kib,voluntary_context_switches,involuntary_context_switches,major_faults,minor_faults,swap_kib,server_exit_code,order_seed'
    for trial in 1 2 3 4; do
        case "$trial" in
            1) order='serial,1-worker,2-worker,3-worker' ;;
            2) order='1-worker,2-worker,3-worker,serial' ;;
            3) order='2-worker,3-worker,serial,1-worker' ;;
            4) order='3-worker,serial,1-worker,2-worker' ;;
        esac
        index=0
        IFS=',' read -r -a modes <<< "$order"
        for mode in "${modes[@]}"; do
            workers=0
            [ "$mode" = serial ] || workers="${mode%%-*}"
            seconds=$(awk -v t="$trial" -v i="$index" 'BEGIN { printf "%.6f", 20 - t - i / 10 }')
            printf '%s\n' "$trial,$mode,$workers,$seconds,1.000,0.100,100.0,1000,1,1,0,10,0,0,4242"
            index=$((index + 1))
        done
    done
} > "$csv_precision"
expect_pass python3 "$CSV_VALIDATOR" "$csv_precision" --workload spawn --trials 4 --max-workers 3 \
    --require-high-resolution --require-balanced-order
expect_pass python3 "$ANALYZER" "$csv_precision"
python3 "$ANALYZER" "$csv_precision" > "$fixture_dir/precision-report.txt"
grep -q 'Paired vs serial by trial' "$fixture_dir/precision-report.txt" || fail 'analyzer omitted paired report'

streaming_csv="$fixture_dir/streaming-precision.csv"
python3 - "$csv_precision" "$streaming_csv" <<'PY'
import csv
import sys

source, destination = sys.argv[1:]
with open(source, newline='') as source_file, open(destination, 'w', newline='') as destination_file:
    reader = csv.DictReader(source_file)
    fields = ['trial', 'mode', 'workers', 'spawn_seconds', 'pregen_seconds'] + [
        field for field in reader.fieldnames
        if field not in {'trial', 'mode', 'workers', 'seconds'}
    ]
    writer = csv.DictWriter(destination_file, fieldnames=fields)
    writer.writeheader()
    for row in reader:
        row['spawn_seconds'] = row.pop('seconds')
        row['pregen_seconds'] = row['spawn_seconds']
        writer.writerow(row)
PY
expect_pass python3 "$CSV_VALIDATOR" "$streaming_csv" --workload streaming --trials 4 --max-workers 3 \
    --require-high-resolution --require-balanced-order

serial_log="$fixture_dir/serial.log"
one_log="$fixture_dir/one.log"
two_log="$fixture_dir/two.log"
three_log="$fixture_dir/three.log"
mismatch_log="$fixture_dir/mismatch.log"
duplicate_log="$fixture_dir/duplicate.log"
adaptive_log="$fixture_dir/adaptive.log"
disabled_log="$fixture_dir/disabled.log"
serial_scheduler_log="$fixture_dir/serial-scheduler.log"
serial_adaptive_log="$fixture_dir/serial-adaptive.log"

printf '%s\n' 'Entering runphase RunGame' > "$serial_log"
printf '%s\n' 'Optimum worldgen scheduler started with 1 worker threads.' > "$one_log"
printf '%s\n' 'Optimum worldgen scheduler started with 2 worker threads.' > "$two_log"
printf '%s\n' 'Optimum worldgen scheduler started with 3 worker threads.' > "$three_log"
printf '%s\n' 'Optimum worldgen scheduler started with 1 worker threads.' > "$mismatch_log"
printf '%s\n' \
    'Optimum worldgen scheduler started with 2 worker threads.' \
    'Optimum worldgen scheduler started with 2 worker threads.' > "$duplicate_log"
printf '%s\n' \
    'Optimum worldgen scheduler started with 2 worker threads.' \
    'Optimum adaptive: workers 2→3 (up, contention 0.01)' > "$adaptive_log"
printf '%s\n' \
    'Optimum worldgen scheduler started with 2 worker threads.' \
    'Optimum worldgen parallelism disabled because a foreign or Harmony-patched handler is active.' > "$disabled_log"
printf '%s\n' 'Optimum worldgen scheduler started with 1 worker threads.' > "$serial_scheduler_log"
printf '%s\n' 'Optimum adaptive: workers 0→1 (up, contention 0.00)' > "$serial_adaptive_log"

expect_pass "$VALIDATOR" "$serial_log" 0
expect_pass "$VALIDATOR" "$one_log" 1
expect_pass "$VALIDATOR" "$two_log" 2
expect_pass "$VALIDATOR" "$three_log" 3
expect_fail "$VALIDATOR" "$mismatch_log" 2
expect_fail "$VALIDATOR" "$duplicate_log" 2
expect_fail "$VALIDATOR" "$adaptive_log" 2
expect_fail "$VALIDATOR" "$disabled_log" 2
expect_fail "$VALIDATOR" "$serial_scheduler_log" 0
expect_fail "$VALIDATOR" "$serial_adaptive_log" 0
expect_fail "$VALIDATOR" "$one_log" 4

if command -v pwsh > /dev/null 2>&1; then
    expect_pass pwsh -NoProfile -File "$POWERSHELL_VALIDATOR" -LogPath "$serial_log" -ExpectedWorkers 0
    expect_pass pwsh -NoProfile -File "$POWERSHELL_VALIDATOR" -LogPath "$three_log" -ExpectedWorkers 3
    expect_fail pwsh -NoProfile -File "$POWERSHELL_VALIDATOR" -LogPath "$mismatch_log" -ExpectedWorkers 2
    expect_fail pwsh -NoProfile -File "$POWERSHELL_VALIDATOR" -LogPath "$adaptive_log" -ExpectedWorkers 2
fi

csv="$fixture_dir/results.csv"
printf '%s\n' \
    'trial,mode,workers,seconds' \
    '1,serial,0,40' \
    '1,1-worker,1,30' \
    '1,2-worker,2,20' \
    '1,3-worker,3,10' > "$csv"

default_report="$fixture_dir/default-report.txt"
pooled_report="$fixture_dir/pooled-report.txt"
python3 "$ANALYZER" "$csv" > "$default_report"
python3 "$ANALYZER" "$csv" --pool-distinct-treatments > "$pooled_report"

grep -q '^1-worker' "$default_report" || fail 'analyzer omitted the one-worker treatment'
grep -q '^2-worker' "$default_report" || fail 'analyzer omitted the two-worker treatment'
grep -q '^3-worker' "$default_report" || fail 'analyzer omitted the three-worker treatment'
if grep -q 'Historical pooled comparison' "$default_report"; then
    fail 'analyzer pooled distinct treatments without an explicit option'
fi
grep -q 'Historical pooled comparison, combines distinct worker-count treatments' "$pooled_report" || \
    fail 'analyzer did not label historical pooling as a combination of distinct treatments'

help_report="$fixture_dir/help.txt"
python3 "$ANALYZER" --help > "$help_report"
grep -q -- '--pool-distinct-treatments' "$help_report" || fail 'analyzer help omitted the historical pooling option'
tr '\n' ' ' < "$help_report" | grep -Eq 'combines +distinct worker-count treatments' || \
    fail 'analyzer help hid the pooling tradeoff'

for script in worldgen-worker-sweep.sh worldgen-streaming-sweep.sh worldgen-benchmark.sh; do
    bash -n "$PROJECT_ROOT/scripts/$script" || fail "$script failed bash syntax validation"
    if [ "$script" = worldgen-benchmark.sh ]; then
        grep -q 'worldgen-benchmark-suite.sh' "$PROJECT_ROOT/scripts/$script" || \
            fail "$script does not delegate to the precise suite"
    else
        grep -q 'validate-worldgen-run.sh' "$PROJECT_ROOT/scripts/$script" || \
            fail "$script does not use the run validator"
    fi
    if rg -q '(secs|result|t)=\$\((run_one|bench_run)' "$PROJECT_ROOT/scripts/$script"; then
        fail "$script hides the child PID inside a command-substitution process"
    fi
done

if rg -q 'VintagestoryAPI-patched\.dll|VSEssentials-patched\.dll|VSSurvivalMod-patched\.dll' \
    "$PROJECT_ROOT/scripts/worldgen-benchmark.ps1"; then
    fail 'PowerShell benchmark uses obsolete donor DLL names'
fi
grep -q 'Get-CimInstance Win32_Process' "$PROJECT_ROOT/scripts/worldgen-benchmark.ps1" || \
    fail 'PowerShell benchmark lacks an orphan-process preflight'
grep -q 'Stop-ActiveBenchmarkProcess' "$PROJECT_ROOT/scripts/worldgen-benchmark.ps1" || \
    fail 'PowerShell benchmark lacks active-process cleanup'
grep -q '\[string\]\$OutputCsv' "$PROJECT_ROOT/scripts/worldgen-benchmark.ps1" || \
    fail 'PowerShell benchmark lacks a portable output CSV parameter'
grep -q 'Set-Content -LiteralPath \$OutputCsv' "$PROJECT_ROOT/scripts/worldgen-benchmark.ps1" || \
    fail 'PowerShell benchmark does not initialize its output CSV'
grep -q 'Add-Content -LiteralPath \$OutputCsv' "$PROJECT_ROOT/scripts/worldgen-benchmark.ps1" || \
    fail 'PowerShell benchmark does not record accepted runs'

echo 'PASS: worldgen benchmark integrity fixtures'
