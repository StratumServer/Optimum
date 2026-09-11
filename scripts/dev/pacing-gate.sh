#!/bin/bash
# Frame-pacing gate for a captured run (plan Phase 0, "Diagnostics").
#
# Reads the logs a run already wrote - OPTIMUM_FPS_LOG (both backends) and
# OPTIMUM_VULKAN_STATS (Vulkan only) - prints a table and exits non-zero unless
# every applicable rule passes:
#
#   blocking_uploads     Vulkan only: blocking_uploads == 0 in every stats sample
#   stddev_vs_baseline   with --baseline: median window stddev <= baseline median stddev x 1.25
#   p99_vs_mean          median window p99 <= 1.5 x median window mean
#   dropped_mesh_writes  with --stats: mesh writes dropped == 0 in every sample
#   uniform_overflows    with --stats: uniform overflows == 0 in every sample
#
# Usage:
#   scripts/dev/pacing-gate.sh --renderer vulkan|opengl --fps <fps.log>
#                              [--stats <vulkan-stats.log>] [--baseline <fps.log>]
#                              [--skip-seconds N]
#   scripts/dev/pacing-gate.sh --self-test
#
#   --renderer      which backend produced the logs; vulkan requires --stats
#   --fps           the OPTIMUM_FPS_LOG file of the run under test
#   --stats         the OPTIMUM_VULKAN_STATS file of the same run
#   --baseline      an OPTIMUM_FPS_LOG file to compare stddev against (typically OpenGL)
#   --skip-seconds  ignore windows and samples that start in the first N seconds (default 0)
#   --self-test     run the gate over embedded sample lines: one pass case and one per fail rule
#
# Exit codes: 0 all rules pass, 1 a rule failed, 2 usage or unreadable input.
# Log formats: docs/taa-acceptance.md, section 3. perf-capture.sh writes both files
# to /tmp/optimum-perf/<label>/. The fps line parser below must stay equal to the one
# in perf-capture.sh (Optimum.Tests/pacing-log-format-coverage-tests.cs).
set -u

exec python3 - "$@" <<'PY'
import argparse, os, re, statistics, sys

FPS_LINE_RE = re.compile(r"\[Optimum\] fps window=(?P<window>[\d.]+) frames=(?P<frames>\d+) mean=(?P<mean>[\d.]+) min=(?P<min>[\d.]+) max=(?P<max>[\d.]+) p99=(?P<p99>[\d.]+)(?: stddev=(?P<stddev>[\d.]+))?")
STATS_LINE_RE = re.compile(r"^stats (?P<elapsed>[\d.]+)s: (?P<frames>\d+) frames \((?P<frame_ms>[\d.]+) ms/frame\), .*, mesh writes dropped (?P<dropped>\d+), uniform overflows (?P<overflows>\d+)$")
STATS_TOKEN_LINE_RE = re.compile(r"^stats\.(?P<kind>pacing|waits|counters) (?P<tokens>.*)$")
TOKEN_RE = re.compile(r"([a-z0-9_]+)=([\d.]+)")

STDDEV_BASELINE_FACTOR = 1.25
P99_MEAN_FACTOR = 1.5


def parse_fps(lines, skip_seconds):
    """Per-second windows as dicts of floats; stddev is absent on pre-stddev logs."""
    windows = []
    elapsed = 0.0
    for line in lines:
        match = FPS_LINE_RE.search(line)
        if not match:
            continue
        window = {k: float(v) for k, v in match.groupdict().items() if v is not None}
        start = elapsed
        elapsed += window["window"]
        if start < skip_seconds:
            continue
        windows.append(window)
    return windows


def parse_stats(lines, skip_seconds):
    """One dict per stats sample: the original line's fields plus kind.token values."""
    samples = []
    current = None
    elapsed = 0.0
    for raw in lines:
        line = raw.rstrip("\n")
        match = STATS_LINE_RE.search(line)
        if match:
            start = elapsed
            elapsed += float(match.group("elapsed"))
            current = {
                "elapsed": float(match.group("elapsed")),
                "frames": int(match.group("frames")),
                "dropped": int(match.group("dropped")),
                "overflows": int(match.group("overflows")),
                "skipped": start < skip_seconds,
                "tokens": {},
            }
            samples.append(current)
            continue
        tokens = STATS_TOKEN_LINE_RE.search(line)
        if tokens and current is not None:
            for key, value in TOKEN_RE.findall(tokens.group("tokens")):
                current["tokens"][tokens.group("kind") + "." + key] = float(value)
    return [s for s in samples if not s["skipped"]]


def evaluate(renderer, fps_lines, stats_lines, baseline_lines, skip_seconds):
    """Returns (exit code, rows, messages). A row is (rule, measured, limit, status)."""
    rows = []
    if renderer == "vulkan" and stats_lines is None:
        return 2, rows, ["--renderer vulkan needs --stats <vulkan-stats.log>"]

    windows = parse_fps(fps_lines, skip_seconds)
    if not windows:
        return 2, rows, ["no [Optimum] fps windows after skipping %g s" % skip_seconds]

    median_mean = statistics.median(w["mean"] for w in windows)
    median_p99 = statistics.median(w["p99"] for w in windows)
    stddevs = [w["stddev"] for w in windows if "stddev" in w]
    median_stddev = statistics.median(stddevs) if stddevs and len(stddevs) == len(windows) else None

    # p99 against mean, same run.
    limit = P99_MEAN_FACTOR * median_mean
    rows.append(("p99_vs_mean", "%.3f ms" % median_p99, "<= %.3f ms (1.5 x mean %.3f)" % (limit, median_mean),
                 "PASS" if median_p99 <= limit else "FAIL"))

    # stddev against a baseline run.
    if baseline_lines is None:
        rows.append(("stddev_vs_baseline",
                     "%.3f ms" % median_stddev if median_stddev is not None else "-",
                     "no --baseline", "SKIP"))
    else:
        baseline = parse_fps(baseline_lines, skip_seconds)
        baseline_stddevs = [w["stddev"] for w in baseline if "stddev" in w]
        if median_stddev is None or not baseline or len(baseline_stddevs) != len(baseline):
            rows.append(("stddev_vs_baseline", "-", "a stddev field in every window of both logs", "FAIL"))
        else:
            baseline_median = statistics.median(baseline_stddevs)
            limit = STDDEV_BASELINE_FACTOR * baseline_median
            rows.append(("stddev_vs_baseline", "%.3f ms" % median_stddev,
                         "<= %.3f ms (1.25 x baseline %.3f)" % (limit, baseline_median),
                         "PASS" if median_stddev <= limit else "FAIL"))

    # Backend counters.
    if stats_lines is None:
        rows.append(("blocking_uploads", "-", "no --stats", "SKIP"))
        rows.append(("dropped_mesh_writes", "-", "no --stats", "SKIP"))
        rows.append(("uniform_overflows", "-", "no --stats", "SKIP"))
    else:
        samples = parse_stats(stats_lines, skip_seconds)
        if not samples:
            return 2, rows, ["no 'stats <s>s:' samples after skipping %g s" % skip_seconds]
        if renderer == "vulkan":
            missing = sum(1 for s in samples if "counters.blocking_uploads" not in s["tokens"])
            worst = max(s["tokens"].get("counters.blocking_uploads", 0.0) for s in samples)
            bad = sum(1 for s in samples if s["tokens"].get("counters.blocking_uploads", 0.0) > 0)
            if missing:
                rows.append(("blocking_uploads", "%d samples lack stats.counters" % missing, "== 0 in every sample", "FAIL"))
            else:
                rows.append(("blocking_uploads", "max %d (%d of %d samples non-zero)" % (worst, bad, len(samples)),
                             "== 0 in every sample", "PASS" if worst == 0 else "FAIL"))
        else:
            rows.append(("blocking_uploads", "-", "Vulkan only", "SKIP"))
        dropped = sum(s["dropped"] for s in samples)
        overflows = sum(s["overflows"] for s in samples)
        rows.append(("dropped_mesh_writes", "%d over %d samples" % (dropped, len(samples)), "== 0",
                     "PASS" if dropped == 0 else "FAIL"))
        rows.append(("uniform_overflows", "%d over %d samples" % (overflows, len(samples)), "== 0",
                     "PASS" if overflows == 0 else "FAIL"))

    summary = ["windows %d, median mean %.3f ms, median p99 %.3f ms, median stddev %s"
               % (len(windows), median_mean, median_p99,
                  "%.3f ms" % median_stddev if median_stddev is not None else "- (log has no stddev field)")]
    code = 1 if any(row[3] == "FAIL" for row in rows) else 0
    return code, rows, summary


def print_table(renderer, rows, messages, out=sys.stdout):
    print("pacing gate (renderer %s)" % renderer, file=out)
    for message in messages:
        print("  " + message, file=out)
    if rows:
        width = max(len(r[1]) for r in rows)
        print("  %-20s %-*s  %-44s %s" % ("rule", width, "measured", "limit", "result"), file=out)
        for rule, measured, limit, status in rows:
            print("  %-20s %-*s  %-44s %s" % (rule, width, measured, limit, status), file=out)


def read_lines(path):
    with open(path, errors="replace") as handle:
        return handle.read().splitlines()


# ---------------------------------------------------------------- self-test

SELF_TEST_FPS = [
    "[Optimum] fps window=1.004 frames=120 mean=8.367 min=7.912 max=11.204 p99=10.811 stddev=0.612",
    "[Optimum] fps window=1.001 frames=121 mean=8.273 min=7.880 max=10.950 p99=10.402 stddev=0.588",
    "[Optimum] fps window=1.006 frames=119 mean=8.454 min=7.901 max=11.870 p99=11.020 stddev=0.640",
]
# A log written before the stddev field existed still parses.
SELF_TEST_FPS_OLD = [
    "[Optimum] fps window=1.004 frames=120 mean=8.367 min=7.912 max=11.204 p99=10.811",
    "[Optimum] fps window=1.001 frames=121 mean=8.273 min=7.880 max=10.950 p99=10.402",
]
SELF_TEST_BASELINE = [
    "[Optimum] fps window=1.002 frames=118 mean=8.491 min=7.950 max=10.900 p99=10.600 stddev=0.560",
    "[Optimum] fps window=1.003 frames=119 mean=8.429 min=7.930 max=10.700 p99=10.500 stddev=0.540",
]
SELF_TEST_STATS = [
    "stats 1.0s: 120 frames (8.4 ms/frame), 3 allocations (812 live), 0 blocking uploads costing 0 ms (0% of the interval), textures +0/-0, mesh writes dropped 0, uniform overflows 0",
    "stats.pacing samples=512 p50_ms=8.301 p95_ms=9.870 p99_ms=10.790 stddev_ms=0.604 stutters=0",
    "stats.waits frame_pacing_n=120 frame_pacing_ms=402.1 upload_submit_n=0 upload_submit_ms=0.0 flush_frame_n=0 flush_frame_ms=0.0 device_wait_idle_n=0 device_wait_idle_ms=0.0 readback_n=0 readback_ms=0.0 occlusion_query_n=0 occlusion_query_ms=0.0 swapchain_acquire_n=120 swapchain_acquire_ms=3.2 present_n=120 present_ms=6.8",
    "stats.counters blocking_uploads=0 uploads=0 scopes=2640 barriers=240 rebar_fallbacks=0 dynamic_state=168000 uniform_ring_used=402112 uniform_ring_capacity=16777216",
    "stats 1.0s: 121 frames (8.3 ms/frame), 0 allocations (812 live), 0 blocking uploads costing 0 ms (0% of the interval), textures +0/-0, mesh writes dropped 0, uniform overflows 0",
    "stats.pacing samples=512 p50_ms=8.296 p95_ms=9.850 p99_ms=10.770 stddev_ms=0.601 stutters=0",
    "stats.waits frame_pacing_n=121 frame_pacing_ms=399.8 upload_submit_n=0 upload_submit_ms=0.0 flush_frame_n=0 flush_frame_ms=0.0 device_wait_idle_n=0 device_wait_idle_ms=0.0 readback_n=0 readback_ms=0.0 occlusion_query_n=0 occlusion_query_ms=0.0 swapchain_acquire_n=121 swapchain_acquire_ms=3.1 present_n=121 present_ms=6.9",
    "stats.counters blocking_uploads=0 uploads=0 scopes=2662 barriers=242 rebar_fallbacks=0 dynamic_state=169400 uniform_ring_used=401600 uniform_ring_capacity=16777216",
]


def replace_in(lines, index, old, new):
    changed = list(lines)
    assert old in changed[index], (old, changed[index])
    changed[index] = changed[index].replace(old, new)
    return changed


def self_test():
    cases = [
        # name, renderer, fps, stats, baseline, skip, expected code, expected failing rules
        ("pass vulkan with stats and baseline", "vulkan", SELF_TEST_FPS, SELF_TEST_STATS, SELF_TEST_BASELINE, 0, 0, set()),
        ("pass opengl without stats, pre-stddev log", "opengl", SELF_TEST_FPS_OLD, None, None, 0, 0, set()),
        ("fail blocking uploads", "vulkan", SELF_TEST_FPS,
         replace_in(SELF_TEST_STATS, 7, "blocking_uploads=0", "blocking_uploads=3"), None, 0, 1, {"blocking_uploads"}),
        ("fail stats sample without counters line", "vulkan", SELF_TEST_FPS,
         SELF_TEST_STATS[:4] + SELF_TEST_STATS[4:7], None, 0, 1, {"blocking_uploads"}),
        ("fail stddev against baseline", "opengl",
         replace_in(replace_in(SELF_TEST_FPS, 0, "stddev=0.612", "stddev=0.910"), 1, "stddev=0.588", "stddev=0.880"),
         None, SELF_TEST_BASELINE, 0, 1, {"stddev_vs_baseline"}),
        ("fail stddev missing under a baseline", "opengl", SELF_TEST_FPS_OLD, None, SELF_TEST_BASELINE, 0, 1,
         {"stddev_vs_baseline"}),
        ("fail p99 against mean", "opengl",
         replace_in(replace_in(SELF_TEST_FPS, 0, "p99=10.811", "p99=13.100"), 1, "p99=10.402", "p99=12.900"),
         None, None, 0, 1, {"p99_vs_mean"}),
        ("fail dropped mesh writes", "vulkan", SELF_TEST_FPS,
         replace_in(SELF_TEST_STATS, 4, "mesh writes dropped 0", "mesh writes dropped 2"), None, 0, 1,
         {"dropped_mesh_writes"}),
        ("fail uniform overflows", "vulkan", SELF_TEST_FPS,
         replace_in(SELF_TEST_STATS, 0, "uniform overflows 0", "uniform overflows 1"), None, 0, 1,
         {"uniform_overflows"}),
        ("skip-seconds drops a bad first window and sample", "vulkan",
         replace_in(SELF_TEST_FPS, 0, "p99=10.811", "p99=90.000"),
         replace_in(SELF_TEST_STATS, 3, "blocking_uploads=0", "blocking_uploads=9"), None, 1.0, 0, set()),
        ("usage: vulkan without stats", "vulkan", SELF_TEST_FPS, None, None, 0, 2, set()),
        ("usage: no fps windows", "opengl", ["no fps here"], None, None, 0, 2, set()),
    ]
    failures = 0
    for name, renderer, fps, stats, baseline, skip, expected_code, expected_fail in cases:
        code, rows, messages = evaluate(renderer, fps, stats, baseline, skip)
        failed = {row[0] for row in rows if row[3] == "FAIL"}
        ok = code == expected_code and failed == expected_fail
        print("%s  %s (exit %d, failed rules %s)" % ("ok  " if ok else "FAIL", name, code, sorted(failed) or "-"))
        if not ok:
            failures += 1
            print_table(renderer, rows, messages)
    if failures:
        print("self-test: %d of %d cases failed" % (failures, len(cases)))
        return 1
    print("self-test: %d cases passed" % len(cases))
    return 0


def main(argv):
    parser = argparse.ArgumentParser(prog="pacing-gate.sh", add_help=True)
    parser.add_argument("--renderer", choices=["vulkan", "opengl"])
    parser.add_argument("--fps")
    parser.add_argument("--stats")
    parser.add_argument("--baseline")
    parser.add_argument("--skip-seconds", type=float, default=0.0)
    parser.add_argument("--self-test", action="store_true")
    try:
        args = parser.parse_args(argv)
    except SystemExit as exit_request:
        return 0 if exit_request.code == 0 else 2

    if args.self_test:
        return self_test()
    if not args.renderer or not args.fps:
        print("--renderer vulkan|opengl and --fps <fps.log> are required (or --self-test)", file=sys.stderr)
        return 2

    try:
        fps_lines = read_lines(args.fps)
        stats_lines = read_lines(args.stats) if args.stats else None
        baseline_lines = read_lines(args.baseline) if args.baseline else None
    except OSError as error:
        print("cannot read %s: %s" % (error.filename, error.strerror), file=sys.stderr)
        return 2

    code, rows, messages = evaluate(args.renderer, fps_lines, stats_lines, baseline_lines, args.skip_seconds)
    print_table(args.renderer, rows, messages)
    print("result: " + {0: "PASS", 1: "FAIL", 2: "ERROR"}[code])
    return code


sys.exit(main(sys.argv[1:]))
PY
