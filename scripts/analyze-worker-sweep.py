#!/usr/bin/env python3
"""Analyze exact worldgen worker-count treatments from the sweep scripts.

The default report keeps one-worker, two-worker, and three-worker samples in separate groups. The historical pooling option combines distinct treatments and serves comparison with CSV files from adaptive runs that predate exact mode.
"""
import argparse
import csv
import itertools
import random
import statistics
from collections import defaultdict

RNG = random.Random(1234)


def load(path, column):
    by_mode = defaultdict(list)
    by_trial = defaultdict(dict)
    order = []
    with open(path) as f:
        for row in csv.DictReader(f):
            if row[column] == "ERROR":
                continue
            mode = row["mode"]
            if mode not in order:
                order.append(mode)
            value = float(row[column])
            by_mode[mode].append(value)
            by_trial[row["trial"]][mode] = value
    return by_mode, by_trial, order


def bootstrap_ci(data, n_resamples=10000, alpha=0.05):
    n = len(data)
    means = []
    for _ in range(n_resamples):
        sample = [data[RNG.randrange(n)] for _ in range(n)]
        means.append(sum(sample) / n)
    means.sort()
    lo = means[int((alpha / 2) * n_resamples)]
    hi = means[int((1 - alpha / 2) * n_resamples)]
    return lo, hi


def permutation_test(a, b, n_perm=10000):
    observed = statistics.mean(a) - statistics.mean(b)
    pooled = list(a) + list(b)
    na = len(a)
    count = 0
    for _ in range(n_perm):
        RNG.shuffle(pooled)
        pa = pooled[:na]
        pb = pooled[na:]
        diff = statistics.mean(pa) - statistics.mean(pb)
        if abs(diff) >= abs(observed):
            count += 1
    return count / n_perm


def exact_sign_flip_test(data):
    """Return an exact two-sided paired sign-flip p-value."""
    observed = abs(statistics.mean(data))
    hits = 0
    total = 0
    for signs in itertools.product((-1, 1), repeat=len(data)):
        candidate = abs(sum(value * sign for value, sign in zip(data, signs)) / len(data))
        if candidate >= observed:
            hits += 1
        total += 1
    return hits / total


def report_group(label, data):
    n = len(data)
    mean = statistics.mean(data)
    med = statistics.median(data)
    sd = statistics.stdev(data) if n > 1 else 0.0
    lo, hi = bootstrap_ci(data) if n > 1 else (mean, mean)
    print(f"{label:20s} {n:3d} {mean:7.2f} {med:7.2f} {sd:7.2f} "
          f"[{lo:7.2f}, {hi:7.2f}] {min(data):5.1f} {max(data):5.1f}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("csv_path")
    ap.add_argument("--column", default="seconds",
                     help="CSV column to analyze (default: seconds; use pregen_seconds for the streaming sweep)")
    ap.add_argument(
        "--pool-distinct-treatments",
        action="store_true",
        help="report a historical pooled comparison that combines distinct worker-count treatments",
    )
    args = ap.parse_args()

    by_mode, by_trial, order = load(args.csv_path, args.column)

    print(f"{'mode':20s} {'n':>3s} {'mean':>7s} {'median':>7s} {'stdev':>7s} {'95% CI (mean)':>20s} {'min':>5s} {'max':>5s}")
    for mode in order:
        report_group(mode, by_mode[mode])

    serial = by_mode.get("serial")
    if serial:
        print(f"\nPairwise vs serial (mean={statistics.mean(serial):.2f}), "
              f"permutation test p-value (two-sided, 10000 perms):")
        for mode in order:
            if mode == "serial":
                continue
            data = by_mode[mode]
            p = permutation_test(list(serial), list(data))
            mean = statistics.mean(data)
            speedup = statistics.mean(serial) / mean if mean else float("inf")
            sig = "***" if p < 0.001 else "**" if p < 0.01 else "*" if p < 0.05 else "ns"
            direction = "faster" if mean < statistics.mean(serial) else "slower"
            print(f"  {mode:18s} mean={mean:7.2f}  speedup={speedup:5.2f}x  {direction:7s}  p={p:.4f} {sig}")

        print("\nPaired vs serial by trial (exact sign-flip p-value):")
        for mode in order:
            if mode == "serial":
                continue
            paired = [
                by_trial[trial][mode] - by_trial[trial]["serial"]
                for trial in sorted(by_trial)
                if "serial" in by_trial[trial] and mode in by_trial[trial]
            ]
            if not paired:
                continue
            delta = statistics.mean(paired)
            lo, hi = bootstrap_ci(paired) if len(paired) > 1 else (delta, delta)
            p = exact_sign_flip_test(paired)
            print(
                f"  {mode:18s} delta={delta:+7.3f}  95% CI=[{lo:+7.3f}, {hi:+7.3f}]"
                f"  p={p:.4f}  n={len(paired)}"
            )

    if args.pool_distinct_treatments and serial:
        pooled = [v for mode in order if mode != "serial" for v in by_mode[mode]]
        if pooled:
            print("\n=== Historical pooled comparison, combines distinct worker-count treatments ===")
            report_group("serial", serial)
            report_group("workers (pooled)", pooled)
            p = permutation_test(list(serial), pooled)
            mean_s, mean_w = statistics.mean(serial), statistics.mean(pooled)
            sig = "***" if p < 0.001 else "**" if p < 0.01 else "*" if p < 0.05 else "ns"
            pct = 100.0 * (mean_w - mean_s) / mean_s if mean_s else float("inf")
            print(f"  workers vs serial: {pct:+.1f}%  p={p:.4f} {sig}")

    best_mode = min(order, key=lambda m: statistics.mean(by_mode[m]))
    print(f"\nBest per-label mean: {best_mode} ({statistics.mean(by_mode[best_mode]):.2f})")
