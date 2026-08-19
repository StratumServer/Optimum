#!/usr/bin/env python3
"""Validate a completed worldgen sweep before accepting its statistics."""

import argparse
import csv
import math
import sys
from collections import Counter, defaultdict


COMMON_COLUMNS = {
    "trial",
    "mode",
    "workers",
    "user_seconds",
    "sys_seconds",
    "cpu_percent",
    "max_rss_kib",
    "voluntary_context_switches",
    "involuntary_context_switches",
    "major_faults",
    "minor_faults",
    "swap_kib",
    "server_exit_code",
    "order_seed",
}


def fail(message):
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def parse_number(row, column):
    value = row.get(column, "")
    if value == "ERROR" or value == "":
        fail(f"{column} is not a number in trial {row.get('trial')} {row.get('mode')}")
    try:
        number = float(value)
    except ValueError:
        fail(f"{column} is invalid in trial {row.get('trial')} {row.get('mode')}: {value}")
    if not math.isfinite(number):
        fail(f"{column} is not finite in trial {row.get('trial')} {row.get('mode')}")
    return number


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("csv_path")
    parser.add_argument("--workload", choices=("spawn", "streaming"), required=True)
    parser.add_argument("--trials", type=int, required=True)
    parser.add_argument("--max-workers", type=int, required=True)
    parser.add_argument("--require-high-resolution", action="store_true")
    parser.add_argument("--require-balanced-order", action="store_true")
    args = parser.parse_args()

    if args.trials < 1 or args.max_workers < 1 or args.max_workers > 3:
        fail("invalid trial or worker count")

    with open(args.csv_path, newline="") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames is None:
            fail("CSV has no header")
        required = set(COMMON_COLUMNS)
        if args.workload == "spawn":
            required.add("seconds")
        else:
            required.update(("spawn_seconds", "pregen_seconds"))
        missing = required.difference(reader.fieldnames)
        if missing:
            fail(f"CSV is missing columns: {', '.join(sorted(missing))}")
        rows = list(reader)

    modes = ["serial"] + [f"{workers}-worker" for workers in range(1, args.max_workers + 1)]
    expected_rows = args.trials * len(modes)
    if len(rows) != expected_rows:
        fail(f"expected {expected_rows} rows, found {len(rows)}")

    by_trial = defaultdict(list)
    mode_counts = Counter()
    order_seeds = set()
    time_columns = ["seconds"] if args.workload == "spawn" else ["spawn_seconds", "pregen_seconds"]
    for row in rows:
        if row["mode"] not in modes:
            fail(f"unexpected mode: {row['mode']}")
        if row["mode"] == "serial" and row["workers"] != "0":
            fail("serial row has a non-zero worker count")
        if row["mode"] != "serial" and row["workers"] != row["mode"].split("-")[0]:
            fail(f"worker label/count mismatch: {row['mode']} / {row['workers']}")
        mode_counts[row["mode"]] += 1
        by_trial[row["trial"]].append(row["mode"])
        order_seeds.add(row["order_seed"])
        for column in time_columns:
            raw = row.get(column, "")
            parse_number(row, column)
            if args.require_high_resolution and "." not in raw:
                fail(f"{column} lacks monotonic precision: {raw}")
            if args.require_high_resolution and len(raw.split(".", 1)[1]) < 3:
                fail(f"{column} has fewer than 3 fractional digits: {raw}")
        for column in COMMON_COLUMNS.difference({"trial", "mode", "workers", "order_seed"}):
            parse_number(row, column)
        if float(row["swap_kib"]) != 0:
            fail(f"process swap is non-zero in trial {row['trial']} {row['mode']}")

    expected_counts = Counter({mode: args.trials for mode in modes})
    if mode_counts != expected_counts:
        fail(f"mode counts differ from expected: {dict(mode_counts)}")
    if len(order_seeds) != 1:
        fail(f"order seed changed within one sweep: {sorted(order_seeds)}")

    for trial in map(str, range(1, args.trials + 1)):
        if Counter(by_trial[trial]) != Counter(modes):
            fail(f"trial {trial} does not contain one row per treatment")

    if args.require_balanced_order:
        positions = {mode: Counter() for mode in modes}
        for order in by_trial.values():
            for position, mode in enumerate(order):
                positions[mode][position] += 1
        for mode, counts in positions.items():
            values = list(counts.values())
            if max(values) - min(values) > 1:
                fail(f"treatment order is not balanced for {mode}: {dict(counts)}")

    print(
        f"PASS: {args.workload} benchmark has {len(rows)} valid rows, "
        f"{len(modes)} modes, order_seed={next(iter(order_seeds))}"
    )


if __name__ == "__main__":
    main()
