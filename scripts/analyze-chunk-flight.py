#!/usr/bin/env python3
"""Summarise chunk flight benchmark runs.

Usage: analyze-chunk-flight.py OUTPUT_DIR
       analyze-chunk-flight.py --self-test

OUTPUT_DIR holds runs/<arm>-<NN>/ directories written by benchmark-chunk-flight.sh. Each run has
markers.env (ARM, FLIGHT_START_EPOCH, FLIGHT_END_EPOCH, RENDERER), client-main.log and an
optional mangohud/*.csv. Frame times come from the in-client chunk stream windows when the build
logs them, and from the MangoHud CSV otherwise. Arms are compared with Welch's t-test on the
per-run values; differences inside the noise are reported as noise.
"""
import csv
import datetime
import glob
import json
import math
import os
import re
import statistics
import sys
import tempfile

SUB_BUCKET_BITS = 2
SUB_BUCKETS = 1 << SUB_BUCKET_BITS
STUTTER_MS = 50.0
WINDOW_SECONDS = 5

LINE_TIME = re.compile(r"^(\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2})")
HIST = re.compile(r"(\w+)=n(\d+)/p50 (\d+)/p95 (\d+)/p99 (\d+)/max (\d+)")
PAIR = re.compile(r"(?:^|[ ,])(over33ms|over50ms|over100ms|sent|uploaded|tessellated|poolAllocs|gcPauseMs|allocMB|tessPerSent|frameMeanUs)=([0-9.]+)")


def bucket_upper_bound(index):
    if index < SUB_BUCKETS:
        return index
    msb = (index >> SUB_BUCKET_BITS) + 1
    sub = index & (SUB_BUCKETS - 1)
    shift = msb - SUB_BUCKET_BITS
    return ((SUB_BUCKETS + sub) << shift) + (1 << shift) - 1


def percentile_from_buckets(buckets, fraction, cap=None):
    total = sum(buckets.values())
    if total == 0:
        return 0.0
    rank = max(1, math.ceil(fraction * total))
    seen = 0
    for index in sorted(buckets):
        seen += buckets[index]
        if seen >= rank:
            value = bucket_upper_bound(index)
            return float(min(value, cap) if cap is not None else value)
    return 0.0


def parse_epoch(text):
    return datetime.datetime.strptime(text, "%d.%m.%Y %H:%M:%S").timestamp()


def parse_markers(path):
    markers = {}
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if "=" in line:
                key, value = line.rstrip("\n").split("=", 1)
                markers[key] = value
    return markers


def stream_windows(log_path, start, end, marker="Optimum chunk stream:"):
    """Windows logged under the marker that lie inside the flight, as dicts."""
    windows = []
    with open(log_path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if marker not in line or "Final" in line:
                continue
            match = LINE_TIME.match(line)
            if not match:
                continue
            stamp = parse_epoch(match.group(1))
            if stamp - WINDOW_SECONDS < start - 1 or stamp > end + 1:
                continue
            window = {"epoch": stamp, "hist": {}, "values": {}, "frameBuckets": {}}
            for name, n, p50, p95, p99, mx in HIST.findall(line):
                window["hist"][name] = tuple(int(x) for x in (n, p50, p95, p99, mx))
            for name, value in PAIR.findall(line):
                window["values"][name] = float(value)
            buckets = re.search(r"frameBuckets=([0-9:,]*)", line)
            if buckets and buckets.group(1):
                for pair in buckets.group(1).split(","):
                    index, count = pair.split(":")
                    window["frameBuckets"][int(index)] = int(count)
            windows.append(window)
    return windows


def mangohud_frames(csv_path, start, end):
    """Frame times in ms inside the flight window, from a MangoHud CSV."""
    stamp = re.search(r"(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})", os.path.basename(csv_path))
    origin = None
    if stamp:
        origin = datetime.datetime.strptime(stamp.group(1) + " " + stamp.group(2), "%Y-%m-%d %H-%M-%S").timestamp()
    with open(csv_path, encoding="utf-8", errors="replace") as handle:
        rows = list(csv.reader(handle))
    header_index = next((i for i, row in enumerate(rows) if "frametime" in row), None)
    if header_index is None:
        return []
    header = rows[header_index]
    frametime = header.index("frametime")
    elapsed = header.index("elapsed") if "elapsed" in header else None
    frames = []
    for row in rows[header_index + 1:]:
        try:
            value = float(row[frametime])
            if elapsed is not None and origin is not None:
                when = origin + float(row[elapsed]) / 1e9
                if when < start or when > end:
                    continue
            frames.append(value)
        except (ValueError, IndexError):
            continue
    return frames


def summarise_run(run_dir):
    markers = parse_markers(os.path.join(run_dir, "markers.env"))
    start = float(markers["FLIGHT_START_EPOCH"])
    end = float(markers["FLIGHT_END_EPOCH"])
    duration = max(end - start, 1.0)
    result = {"arm": markers["ARM"], "run": os.path.basename(run_dir), "renderer": markers.get("RENDERER", ""), "sources": {}}
    log_path = os.path.join(run_dir, "client-main.log")
    windows = stream_windows(log_path, start, end) if os.path.exists(log_path) else []
    merged = {}
    for window in windows:
        for index, count in window["frameBuckets"].items():
            merged[index] = merged.get(index, 0) + count
    frames_in_stream = sum(merged.values())
    if frames_in_stream:
        client = {}
        result["sources"]["client"] = client
        client["frames"] = frames_in_stream
        client["fps"] = frames_in_stream / (len(windows) * WINDOW_SECONDS)
        mean_us = sum(w["values"].get("frameMeanUs", 0) * sum(w["frameBuckets"].values()) for w in windows) / frames_in_stream
        client["frame_mean_ms"] = mean_us / 1000.0
        client["frame_p50_ms"] = percentile_from_buckets(merged, 0.50) / 1000.0
        client["frame_p95_ms"] = percentile_from_buckets(merged, 0.95) / 1000.0
        client["frame_p99_ms"] = percentile_from_buckets(merged, 0.99) / 1000.0
        client["frame_max_ms"] = max((w["hist"].get("frameUs", (0, 0, 0, 0, 0))[4] for w in windows), default=0) / 1000.0
        client["stutters"] = sum(w["values"].get("over50ms", 0) for w in windows)
        upload = [w["hist"]["uploadFrameUs"][3] for w in windows if "uploadFrameUs" in w["hist"] and w["hist"]["uploadFrameUs"][0]]
        client["upload_frame_p99_ms_worst_window"] = max(upload, default=0) / 1000.0
        queue_age = [w["hist"]["queueAgeUs"][3] for w in windows if "queueAgeUs" in w["hist"] and w["hist"]["queueAgeUs"][0]]
        client["queue_age_p99_ms_worst_window"] = max(queue_age, default=0) / 1000.0
        client["chunks_sent"] = sum(w["values"].get("sent", 0) for w in windows)
        sent_total = sum(w["values"].get("sent", 0) for w in windows)
        client["tess_per_sent"] = (sum(w["values"].get("tessellated", 0) for w in windows) / sent_total) if sent_total else 0.0
        client["pool_allocs"] = sum(w["values"].get("poolAllocs", 0) for w in windows)
        client["gc_pause_ms_total"] = windows[-1]["values"].get("gcPauseMs", 0.0) - (windows[0]["values"].get("gcPauseMs", 0.0) if len(windows) > 1 else 0.0)
        client["windows"] = len(windows)
    meter = stream_windows(log_path, start, end, "ChunkFlightMeter:") if os.path.exists(log_path) else []
    meter_buckets = {}
    for window in meter:
        for index, count in window["frameBuckets"].items():
            meter_buckets[index] = meter_buckets.get(index, 0) + count
    meter_frames = sum(meter_buckets.values())
    if meter_frames:
        result["sources"]["meter"] = {
            "frames": meter_frames,
            "fps": meter_frames / (len(meter) * WINDOW_SECONDS),
            "frame_mean_ms": sum(w["values"].get("frameMeanUs", 0) * sum(w["frameBuckets"].values()) for w in meter) / meter_frames / 1000.0,
            "frame_p50_ms": percentile_from_buckets(meter_buckets, 0.50) / 1000.0,
            "frame_p95_ms": percentile_from_buckets(meter_buckets, 0.95) / 1000.0,
            "frame_p99_ms": percentile_from_buckets(meter_buckets, 0.99) / 1000.0,
            "frame_max_ms": max((w["hist"].get("frameUs", (0, 0, 0, 0, 0))[4] for w in meter), default=0) / 1000.0,
            "stutters": sum(w["values"].get("over50ms", 0) for w in meter),
        }
    csvs = sorted(glob.glob(os.path.join(run_dir, "mangohud", "*.csv")))
    frames = mangohud_frames(csvs[0], start, end) if csvs else []
    if frames:
        ordered = sorted(frames)
        n = len(ordered)
        result["sources"]["mangohud"] = {
            "frames": n,
            "fps": n / duration,
            "frame_mean_ms": sum(ordered) / n,
            "frame_p50_ms": ordered[int(n * 0.50)],
            "frame_p95_ms": ordered[min(n - 1, int(n * 0.95))],
            "frame_p99_ms": ordered[min(n - 1, int(n * 0.99))],
            "frame_max_ms": ordered[-1],
            "stutters": sum(1 for value in frames if value > STUTTER_MS),
        }
    return result


def choose_source(runs):
    """One frame source for every run, so arms are compared on the same ruler."""
    for source in ("mangohud", "meter", "client"):
        if all(source in run["sources"] for run in runs):
            return source, ""
    return None, "no frame source is present in every run, so the arms are not comparable"


def flatten(runs, source):
    flat = []
    for run in runs:
        item = {"arm": run["arm"], "run": run["run"], "renderer": run["renderer"], "source": source or "none"}
        if source:
            item.update(run["sources"][source])
        if "client" in run["sources"]:
            for key in ("upload_frame_p99_ms_worst_window", "queue_age_p99_ms_worst_window", "chunks_sent", "tess_per_sent", "pool_allocs", "gc_pause_ms_total", "windows"):
                item["detail_" + key] = run["sources"]["client"][key]
        flat.append(item)
    return flat


def incomplete_beta(a, b, x):
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    log_front = math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b) + a * math.log(x) + b * math.log(1.0 - x)
    front = math.exp(log_front)
    if x < (a + 1.0) / (a + b + 2.0):
        return front * beta_fraction(a, b, x) / a
    return 1.0 - front * beta_fraction(b, a, 1.0 - x) / b


def beta_fraction(a, b, x):
    tiny = 1e-300
    qab, qap, qam = a + b, a + 1.0, a - 1.0
    c, d = 1.0, 1.0 - qab * x / qap
    d = 1.0 / (d if abs(d) > tiny else tiny)
    h = d
    for m in range(1, 300):
        m2 = 2 * m
        aa = m * (b - m) * x / ((qam + m2) * (a + m2))
        d = 1.0 + aa * d
        d = 1.0 / (d if abs(d) > tiny else tiny)
        c = 1.0 + aa / c
        c = c if abs(c) > tiny else tiny
        h *= d * c
        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2))
        d = 1.0 + aa * d
        d = 1.0 / (d if abs(d) > tiny else tiny)
        c = 1.0 + aa / c
        c = c if abs(c) > tiny else tiny
        delta = d * c
        h *= delta
        if abs(delta - 1.0) < 3e-12:
            break
    return h


def welch(a, b):
    """Two-sided Welch t-test. Returns (t, df, p) or None when a group has fewer than two values."""
    if len(a) < 2 or len(b) < 2:
        return None
    va, vb = statistics.variance(a), statistics.variance(b)
    na, nb = len(a), len(b)
    se2 = va / na + vb / nb
    if se2 == 0:
        return (0.0, float(na + nb - 2), 1.0)
    t = (statistics.mean(a) - statistics.mean(b)) / math.sqrt(se2)
    df = se2 ** 2 / ((va / na) ** 2 / (na - 1) + (vb / nb) ** 2 / (nb - 1))
    p = incomplete_beta(df / 2.0, 0.5, df / (df + t * t))
    return (t, df, p)


METRICS = [
    ("fps", "FPS", "higher"),
    ("frame_mean_ms", "frame mean ms", "lower"),
    ("frame_p95_ms", "frame p95 ms", "lower"),
    ("frame_p99_ms", "frame p99 ms", "lower"),
    ("frame_max_ms", "frame max ms", "lower"),
    ("stutters", "frames over 50 ms", "lower"),
]


def render(runs):
    arms = {}
    for run in runs:
        arms.setdefault(run["arm"], []).append(run)
    lines = ["| arm | runs | source | " + " | ".join(label for _, label, _ in METRICS) + " |",
             "| --- | --- | --- | " + " | ".join("---" for _ in METRICS) + " |"]
    for arm, items in arms.items():
        cells = []
        for key, _, _ in METRICS:
            values = [item[key] for item in items if key in item]
            if not values:
                cells.append("n/a")
            elif len(values) == 1:
                cells.append(f"{values[0]:.2f}")
            else:
                cells.append(f"{statistics.mean(values):.2f} ± {statistics.stdev(values):.2f}")
        sources = ",".join(sorted({item["source"] for item in items}))
        lines.append(f"| {arm} | {len(items)} | {sources} | " + " | ".join(cells) + " |")
    names = list(arms)
    comparisons = []
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            for key, label, better in METRICS:
                a = [item[key] for item in arms[names[i]] if key in item]
                b = [item[key] for item in arms[names[j]] if key in item]
                result = welch(a, b)
                if result is None:
                    comparisons.append(f"- {label}: {names[i]} vs {names[j]}: not enough runs for a test")
                    continue
                t, df, p = result
                delta = statistics.mean(a) - statistics.mean(b)
                verdict = "noise" if p >= 0.05 else ("first is better" if (delta > 0) == (better == "higher") else "second is better")
                comparisons.append(f"- {label}: {names[i]} {statistics.mean(a):.2f} vs {names[j]} {statistics.mean(b):.2f} (t={t:.2f}, df={df:.1f}, p={p:.3f}): {verdict}")
    extras = []
    for run in runs:
        if "detail_windows" in run:
            extras.append(f"- {run['run']}: upload p99 worst window {run['detail_upload_frame_p99_ms_worst_window']:.2f} ms, queue age p99 worst window {run['detail_queue_age_p99_ms_worst_window']:.2f} ms, chunks sent {run['detail_chunks_sent']:.0f}, tessellations per sent chunk {run['detail_tess_per_sent']:.2f}, pool allocations {run['detail_pool_allocs']:.0f}, GC pause {run['detail_gc_pause_ms_total']:.0f} ms")
    text = ["## Chunk flight benchmark", "", *lines, ""]
    if comparisons:
        text += ["### Welch t-test per metric", "", *comparisons, ""]
    if extras:
        text += ["### Streaming detail (Optimum builds)", "", *extras, ""]
    renderers = sorted({run["renderer"] for run in runs if run["renderer"]})
    text += ["Renderer: " + (", ".join(renderers) if renderers else "unknown")]
    return "\n".join(text) + "\n"


def analyze(output_dir):
    runs = []
    for run_dir in sorted(glob.glob(os.path.join(output_dir, "runs", "*"))):
        if os.path.exists(os.path.join(run_dir, "markers.env")):
            runs.append(summarise_run(run_dir))
    if not runs:
        print("no runs found", file=sys.stderr)
        return 1
    with open(os.path.join(output_dir, "summary.json"), "w", encoding="utf-8") as handle:
        json.dump(runs, handle, indent=2)
    source, warning = choose_source(runs)
    text = render(flatten(runs, source))
    if warning:
        text = "WARNING: " + warning + "\n\n" + text
    with open(os.path.join(output_dir, "summary.md"), "w", encoding="utf-8") as handle:
        handle.write(text)
    print(text)
    return 0


def synthetic_log(path, start, frame_us_by_window):
    """Write a client log with one chunk stream window per entry."""
    with open(path, "w", encoding="utf-8") as handle:
        for i, samples in enumerate(frame_us_by_window):
            stamp = datetime.datetime.fromtimestamp(start + WINDOW_SECONDS * (i + 1)).strftime("%d.%m.%Y %H:%M:%S")
            buckets = {}
            for value in samples:
                msb = value.bit_length() - 1
                index = value if value < SUB_BUCKETS else ((msb - 1) << SUB_BUCKET_BITS) + ((value >> (msb - SUB_BUCKET_BITS)) & (SUB_BUCKETS - 1))
                buckets[index] = buckets.get(index, 0) + 1
            over50 = sum(1 for v in samples if v > 50000)
            bucket_text = ",".join(f"{k}:{v}" for k, v in sorted(buckets.items()))
            handle.write(
                f"{stamp} [Notification] [Optimum] Optimum chunk stream: frameUs=n{len(samples)}/p50 1/p95 1/p99 1/max {max(samples)}, "
                f"over33ms=0, over50ms={over50}, over100ms=0, uploadFrameUs=n3/p50 100/p95 200/p99 300/max 300, "
                f"queueAgeUs=n3/p50 100/p95 200/p99 400/max 400, sent=10, uploaded=10, tessellated=15, tessPerSent=1.50, poolAllocs=1, "
                f"gc0/1/2=1/0/0, gcPauseMs={i}.5, allocMB=10, frameMeanUs={sum(samples) // len(samples)}, frameBuckets={bucket_text}\n")


def self_test():
    assert bucket_upper_bound(0) == 0 and bucket_upper_bound(3) == 3
    assert bucket_upper_bound(4) == 4 and bucket_upper_bound(8) == 9
    p = welch([1.0, 1.1, 0.9, 1.0, 1.05], [2.0, 2.1, 1.9, 2.0, 2.05])
    assert p is not None and p[2] < 0.001, p
    q = welch([1.0, 1.1, 0.9, 1.0, 1.05], [1.0, 1.1, 0.9, 1.0, 1.05])
    assert q is not None and q[2] > 0.99, q
    assert welch([1.0], [2.0]) is None
    with tempfile.TemporaryDirectory() as root:
        start = datetime.datetime(2026, 11, 11, 10, 0, 0).timestamp()
        for arm, base in (("fast", 10000), ("slow", 20000)):
            for n in range(1, 4):
                run = os.path.join(root, "runs", f"{arm}-{n:02d}")
                os.makedirs(run)
                with open(os.path.join(run, "markers.env"), "w", encoding="utf-8") as handle:
                    handle.write(f"ARM={arm}\nFLIGHT_START_EPOCH={start}\nFLIGHT_END_EPOCH={start + 20}\nRENDERER=test gpu\n")
                windows = [[base + n * 100 + k for k in range(100)] + ([60000] if arm == "slow" else []) for _ in range(4)]
                synthetic_log(os.path.join(run, "client-main.log"), start, windows)
        runs = [summarise_run(d) for d in sorted(glob.glob(os.path.join(root, "runs", "*")))]
        assert len(runs) == 6
        source, warning = choose_source(runs)
        assert source == "client" and not warning, (source, warning)
        flat = flatten(runs, source)
        fast = [r for r in flat if r["arm"] == "fast"][0]
        slow = [r for r in flat if r["arm"] == "slow"][0]
        assert fast["source"] == "client" and fast["detail_windows"] == 4 and fast["frames"] == 400, fast
        assert fast["stutters"] == 0 and slow["stutters"] == 4, (fast, slow)
        assert 9.5 <= fast["frame_p99_ms"] <= 13.0, fast
        assert slow["frame_p99_ms"] > fast["frame_p99_ms"]
        # A mangohud-only vanilla run next to a client-only run has no common ruler.
        mixed = [dict(runs[0]), dict(runs[1], sources={"mangohud": {}})]
        assert choose_source(mixed)[0] is None
        text = render(flat)
        assert "second is better" not in text and "first is better" in text, text
        assert "| fast | 3 | client |" in text
    print("self-test ok")
    return 0


def main(argv):
    if len(argv) == 2 and argv[1] == "--self-test":
        return self_test()
    if len(argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    return analyze(argv[1])


if __name__ == "__main__":
    sys.exit(main(sys.argv))
