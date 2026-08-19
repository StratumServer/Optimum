#!/usr/bin/env bash
# Sample one process without attaching a managed profiler.
set -euo pipefail

if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
    echo "Usage: $0 PID OUTPUT [INTERVAL_SECONDS]" >&2
    exit 64
fi

pid="$1"
output="$2"
interval="${3:-0.25}"
clock_ticks=$(getconf CLK_TCK)
first_ns=""
last_ns=""
last_user_ticks=0
last_sys_ticks=0
max_rss_kib=0
last_voluntary=0
last_involuntary=0
last_major_faults=0
last_minor_faults=0
last_swap_kib=0
last_affinity="unknown"
last_cgroup="unknown"
sample_count=0

read_sample() {
    local stat_line status_line now_ns user_ticks sys_ticks major_faults minor_faults rss_kib voluntary involuntary swap_kib affinity cgroup
    stat_line=$(cat "/proc/$pid/stat" 2>/dev/null) || return 1
    read -r user_ticks sys_ticks minor_faults major_faults < <(
        awk '{sub(/^.*\) /, ""); print $12, $13, $8, $10}' <<< "$stat_line"
    )
    # Read the fields that do not belong to /proc/$pid/stat from status.
    status_line=$(cat "/proc/$pid/status" 2>/dev/null) || return 1
    rss_kib=$(awk '/^VmRSS:/ {print $2}' <<< "$status_line")
    voluntary=$(awk '/^voluntary_ctxt_switches:/ {print $2}' <<< "$status_line")
    involuntary=$(awk '/^nonvoluntary_ctxt_switches:/ {print $2}' <<< "$status_line")
    swap_kib=$(awk '/^VmSwap:/ {print $2}' <<< "$status_line")
    affinity=$(awk '/^Cpus_allowed_list:/ {print $2}' <<< "$status_line")
    cgroup=$(awk -F: 'NR == 1 {print ($3 == "" ? "/" : $3)}' "/proc/$pid/cgroup" 2>/dev/null || true)
    now_ns=$(date +%s%N)
    [ -n "$first_ns" ] || first_ns="$now_ns"
    last_ns="$now_ns"
    last_user_ticks="$user_ticks"
    last_sys_ticks="$sys_ticks"
    last_major_faults="$major_faults"
    last_minor_faults="$minor_faults"
    [ "${rss_kib:-0}" -gt "$max_rss_kib" ] && max_rss_kib="$rss_kib"
    last_voluntary="${voluntary:-0}"
    last_involuntary="${involuntary:-0}"
    last_swap_kib="${swap_kib:-0}"
    last_affinity="${affinity:-unknown}"
    last_cgroup="${cgroup:-$last_cgroup}"
    sample_count=$((sample_count + 1))
    return 0
}

while kill -0 "$pid" 2>/dev/null; do
    read_sample || break
    sleep "$interval"
done
read_sample || true

if [ -z "$first_ns" ] || [ -z "$last_ns" ]; then
    cat > "$output" <<'EOF'
sample_count=0
elapsed_seconds=0
user_seconds=0
sys_seconds=0
cpu_percent=0
max_rss_kib=0
voluntary_context_switches=0
involuntary_context_switches=0
major_faults=0
minor_faults=0
swap_kib=0
affinity=unknown
cgroup=unknown
EOF
    exit 0
fi

elapsed_seconds=$(awk -v first="$first_ns" -v last="$last_ns" 'BEGIN { printf "%.3f", (last - first) / 1000000000 }')
user_seconds=$(awk -v ticks="$last_user_ticks" -v hz="$clock_ticks" 'BEGIN { printf "%.3f", ticks / hz }')
sys_seconds=$(awk -v ticks="$last_sys_ticks" -v hz="$clock_ticks" 'BEGIN { printf "%.3f", ticks / hz }')
cpu_percent=$(awk -v user="$user_seconds" -v sys="$sys_seconds" -v elapsed="$elapsed_seconds" 'BEGIN { if (elapsed > 0) printf "%.2f", 100 * (user + sys) / elapsed; else print "0" }')

cat > "$output" <<EOF
sample_count=$sample_count
elapsed_seconds=$elapsed_seconds
user_seconds=$user_seconds
sys_seconds=$sys_seconds
cpu_percent=$cpu_percent
max_rss_kib=$max_rss_kib
voluntary_context_switches=$last_voluntary
involuntary_context_switches=$last_involuntary
major_faults=$last_major_faults
minor_faults=$last_minor_faults
swap_kib=$last_swap_kib
affinity=$last_affinity
cgroup=$last_cgroup
EOF
