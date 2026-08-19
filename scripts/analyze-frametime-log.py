#!/usr/bin/env python3
"""Compare frametime logs across up to 3 runs (MSI Afterburner .hml,
RTSS plain-text logs, or MangoHud CSV).

Usage:
    python3 scripts/analyze-frametime-log.py off.hml merge4.hml merge8.hml
    python3 scripts/analyze-frametime-log.py --labels off,merge4,merge8 off.hml merge4.hml merge8.hml

Accepts any of:
  - MSI Afterburner "Hardware monitoring log" .hml files (row-type-prefixed
    CSV: "00" file header, "01" GPU name, "02" column names, "03" one
    per-sensor metadata row, "80" one data row per poll). Detected by the
    "Hardware monitoring log" signature on the "00," line. The "Frametime"
    column (logged in ms) is located by name in the "02," header row, so
    this survives column reordering across Afterburner versions/configs.
    Caveat: Afterburner's default hardware polling period is 1000ms (one
    sample per second), not one sample per rendered frame - P95/P99/1%-low
    computed from this data describes second-to-second variance, not true
    frame-to-frame stutter. Lower "Hardware polling period" in Afterburner's
    Settings > General tab (e.g. to 100ms) before a real comparison run for
    a less coarse signal; it will never be genuinely per-frame like RTSS's
    own OSD capture, since it's a periodic poll, not a frame-present hook.
  - RTSS frametime logs (plain text, one numeric value per line, optional
    header row, values in milliseconds).
  - MangoHud CSV logs (frametime is one column among several; the header
    row names it, matched case-insensitively).
  - A bare list of one frametime-in-ms value per line (no header).

Prints, per file: sample count, mean FPS, P50/P95/P99 frame time (ms),
and 1% low FPS (mean FPS of the slowest 1% of samples — the number that
actually reflects stutter when the data is genuinely per-frame; with
Afterburner's default 1Hz polling, treat it as a rougher variance signal
instead).
"""
import argparse
import csv
import re
import sys

NUMBER_RE = re.compile(r"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?")


def parse_afterburner_hml(path: str) -> list[float] | None:
    """MSI Afterburner hardware monitoring log: row-type-prefixed CSV.

    Row "00" is normally the file header ("Hardware monitoring log v1.6"
    or similar), but some exports omit it (observed: a "Save As" copy
    that started directly at "02,") - so detection doesn't require it.
    Row "02" lists column names (2 leading fields are row-type and
    timestamp, then one name per sensor); its presence with a "Frametime"
    column is the actual format signature used here. Row "80" is one data
    row per poll, same field layout. Locate "Frametime" by name rather
    than assuming a fixed index, since the sensor list depends on what's
    enabled/available.
    """
    with open(path, "r", encoding="latin-1", errors="ignore", newline="") as f:
        lines = f.read().splitlines()

    frametime_idx = None
    for line in lines:
        if line.startswith("02,"):
            fields = [c.strip() for c in line.split(",")]
            names = fields[2:]
            for i, name in enumerate(names):
                if name.strip().lower() == "frametime":
                    frametime_idx = i
                    break
            break
    if frametime_idx is None:
        return None

    values = []
    for line in lines:
        if not line.startswith("80,"):
            continue
        fields = line.split(",")
        data = fields[2:]
        if frametime_idx >= len(data):
            continue
        try:
            values.append(float(data[frametime_idx].strip()))
        except ValueError:
            continue
    return values if values else None


def parse_generic(path: str) -> list[float]:
    """First numeric token per line. Skips lines with no number (headers)."""
    values = []
    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = NUMBER_RE.search(line)
            if m:
                try:
                    values.append(float(m.group()))
                except ValueError:
                    continue
    return values


def parse_csv_with_header(path: str) -> list[float] | None:
    """MangoHud-style CSV: a header row names a frametime column."""
    with open(path, "r", encoding="utf-8", errors="ignore", newline="") as f:
        sniffer_sample = f.read(4096)
        f.seek(0)
        try:
            dialect = csv.Sniffer().sniff(sniffer_sample)
        except csv.Error:
            return None
        reader = csv.reader(f, dialect)
        try:
            header = next(reader)
        except StopIteration:
            return None
        col = None
        for i, name in enumerate(header):
            if "frametime" in name.strip().lower().replace("_", "").replace(" ", ""):
                col = i
                break
        if col is None:
            return None
        values = []
        for row in reader:
            if col >= len(row):
                continue
            try:
                values.append(float(row[col]))
            except ValueError:
                continue
        return values if values else None


def load_frametimes(path: str) -> list[float]:
    values = parse_afterburner_hml(path)
    if values:
        return values
    values = parse_csv_with_header(path)
    if values:
        return values
    return parse_generic(path)


def percentile(sorted_values: list[float], pct: float) -> float:
    if not sorted_values:
        return float("nan")
    k = (len(sorted_values) - 1) * (pct / 100.0)
    f, c = int(k), min(int(k) + 1, len(sorted_values) - 1)
    if f == c:
        return sorted_values[f]
    return sorted_values[f] + (sorted_values[c] - sorted_values[f]) * (k - f)


def summarize(label: str, values: list[float]) -> dict:
    if not values:
        return {"label": label, "n": 0}
    s = sorted(values)
    n = len(s)
    mean_ms = sum(s) / n
    p50 = percentile(s, 50)
    p95 = percentile(s, 95)
    p99 = percentile(s, 99)
    onepct_count = max(1, n // 100)
    slowest = s[-onepct_count:]
    onepct_low_fps = 1000.0 / (sum(slowest) / len(slowest))
    return {
        "label": label,
        "n": n,
        "mean_fps": 1000.0 / mean_ms,
        "p50_ms": p50,
        "p95_ms": p95,
        "p99_ms": p99,
        "onepct_low_fps": onepct_low_fps,
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("files", nargs="+", help="frametime log files, one per run (e.g. off, merge4, merge8)")
    ap.add_argument("--labels", help="comma-separated labels, same order as files (default: filenames)")
    args = ap.parse_args()

    labels = args.labels.split(",") if args.labels else [f.rsplit("/", 1)[-1] for f in args.files]
    if len(labels) != len(args.files):
        print("error: --labels count must match file count", file=sys.stderr)
        sys.exit(1)

    rows = []
    for path, label in zip(args.files, labels):
        values = load_frametimes(path)
        if not values:
            print(f"warning: no numeric frametime data found in {path}", file=sys.stderr)
        rows.append(summarize(label, values))

    header = f"{'run':<12} {'frames':>8} {'mean fps':>9} {'p50 ms':>8} {'p95 ms':>8} {'p99 ms':>8} {'1% low fps':>11}"
    print(header)
    print("-" * len(header))
    for r in rows:
        if r["n"] == 0:
            print(f"{r['label']:<12} {'(no data)':>8}")
            continue
        print(
            f"{r['label']:<12} {r['n']:>8} {r['mean_fps']:>9.1f} {r['p50_ms']:>8.2f} "
            f"{r['p95_ms']:>8.2f} {r['p99_ms']:>8.2f} {r['onepct_low_fps']:>11.1f}"
        )

    valid = [r for r in rows if r["n"] > 0]
    if len(valid) >= 2:
        baseline = valid[0]
        print()
        print(f"Relative to '{baseline['label']}':")
        for r in valid[1:]:
            fps_delta = (r["mean_fps"] - baseline["mean_fps"]) / baseline["mean_fps"] * 100
            low_delta = (r["onepct_low_fps"] - baseline["onepct_low_fps"]) / baseline["onepct_low_fps"] * 100
            print(f"  {r['label']:<12} mean fps {fps_delta:+.1f}%   1% low fps {low_delta:+.1f}%")


if __name__ == "__main__":
    main()
