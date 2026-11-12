#!/usr/bin/env python3
"""Summarise chunk flight benchmark runs.

Usage: analyze-chunk-flight.py OUTPUT_DIR
       analyze-chunk-flight.py --self-test

OUTPUT_DIR holds runs/<arm>-<NN>/ directories written by benchmark-chunk-flight.sh. Each run has
markers.env (ARM, FLIGHT_START_EPOCH, FLIGHT_END_EPOCH, RENDERER, MIN_BLOCKS), client-main.log and
an optional mangohud/*.csv. Frame times have three possible sources: MangoHud, the ChunkFlightMeter
mod (present in every arm) and the in-client Optimum chunk stream windows. Arms are only compared on
a source that every run has. The meter also logs the player position, the loaded terrain ahead and
the chunks received per window, which give the blocks flown, the distance-normalised hitch metrics
and the validity of a run: a run that flew fewer than MIN_BLOCKS blocks, or has no positions at all,
is listed as INVALID and left out of the statistics. Arms are compared with Welch's t-test on the
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
PAIR = re.compile(r"(?:^|[ ,])(over33ms|over50ms|over100ms|sent|uploaded|tessellated|poolAllocs|gcPauseMs|allocMB|tessPerSent|frameMeanUs|hitchMs|load1|cpuMhz|ahead|chunks|cols|visibilityAttempted|visibilityCaptured|visibilityInside|visibilityOutside|visibilityCapacityDropped|visibilityUnclassified|visibilityPending|visibilityBelow250Ms|visibilityClassificationFailures|visibilityContractFailures|streamWindowStartEpochMs|streamWindowEndEpochMs)=([0-9.]+)")
BUCKETS = re.compile(r"(\w+Buckets)=([0-9:,]*)")
POSITION = re.compile(r"pos=(-?[0-9.]+),(-?[0-9.]+),(-?[0-9.]+)")


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


def percentile_with_censoring(buckets, fraction):
    total = sum(buckets.values())
    if total == 0:
        return None, False
    rank = max(1, math.ceil(fraction * total))
    seen = 0
    for index in sorted(buckets):
        seen += buckets[index]
        if seen >= rank:
            return float(bucket_upper_bound(index)), index == 4 * 47 - 1
    return None, False


def merge_bucket_counts(windows, key):
    merged = {}
    for window in windows:
        for index, count in window["buckets"].get(key, {}).items():
            merged[index] = merged.get(index, 0) + count
    return merged


def summarize_bucket_histogram(windows, bucket_key, histogram_key):
    buckets = merge_bucket_counts(windows, bucket_key)
    count = sum(buckets.values())
    hist_max = max((window["hist"].get(histogram_key, (0, 0, 0, 0, 0))[4] for window in windows), default=0)
    result = {"samples": count, "max": hist_max, "bucket_counts": buckets}
    for percentile in (0.50, 0.95, 0.99):
        value, censored = percentile_with_censoring(buckets, percentile)
        name = f"p{int(percentile * 100)}"
        result[name] = value
        result[name + "_censored"] = censored
    return result


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


def stream_windows(log_path, start, end, marker="Optimum chunk stream:", previous=None, complete_only=False):
    """Windows logged under the marker that lie inside the flight, as dicts.

    When previous is a list, the last window that ended before the first flight window is appended
    to it: its position is where the first flight window started.
    """
    windows = []
    with open(log_path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if marker not in line or "Final" in line:
                continue
            match = LINE_TIME.match(line)
            if not match:
                continue
            stamp = parse_epoch(match.group(1))
            if not complete_only and stamp > end + 1:
                continue
            window = {"epoch": stamp, "start_epoch": stamp - WINDOW_SECONDS, "hist": {}, "values": {}, "buckets": {}, "frameBuckets": {}}
            position = POSITION.search(line)
            if position:
                window["pos"] = tuple(float(v) for v in position.groups())
            for name, n, p50, p95, p99, mx in HIST.findall(line):
                window["hist"][name] = tuple(int(x) for x in (n, p50, p95, p99, mx))
            for name, value in PAIR.findall(line):
                window["values"][name] = float(value)
            for name, bucket_text in BUCKETS.findall(line):
                values = {}
                if bucket_text:
                    for pair in bucket_text.split(","):
                        if not pair:
                            continue
                        index, count = pair.split(":")
                        values[int(index)] = int(count)
                window["buckets"][name] = values
                if name == "frameBuckets":
                    window["frameBuckets"] = values
            window["precise_bounds"] = "streamWindowStartEpochMs" in window["values"] and "streamWindowEndEpochMs" in window["values"]
            if window["precise_bounds"]:
                window["start_epoch"] = window["values"]["streamWindowStartEpochMs"] / 1000.0
                window["end_epoch"] = window["values"]["streamWindowEndEpochMs"] / 1000.0
            else:
                window["end_epoch"] = stamp
            if complete_only:
                if window["precise_bounds"]:
                    if window["end_epoch"] <= window["start_epoch"] or window["start_epoch"] < start or window["end_epoch"] > end:
                        continue
                elif window["start_epoch"] < start + 1 or window["epoch"] > end - 1:
                    continue
            elif stamp - WINDOW_SECONDS < start - 1:
                if previous is not None:
                    previous[:] = [window]
                continue
            windows.append(window)
    return windows


def covered_seconds(windows):
    """Return the union of telemetry-window bounds, excluding gaps and overlap."""
    intervals = sorted(
        (window["start_epoch"], window["end_epoch"])
        for window in windows
        if window["end_epoch"] > window["start_epoch"]
    )
    if not intervals:
        return 0.0

    total = 0.0
    start, end = intervals[0]
    for next_start, next_end in intervals[1:]:
        if next_start > end:
            total += end - start
            start, end = next_start, next_end
        else:
            end = max(end, next_end)
    return total + end - start


def telemetry_window_discontinuities(windows):
    """Return internal gaps or overlaps; precise snapshots should share exact boundaries."""
    intervals = sorted(
        (window["start_epoch"], window["end_epoch"])
        for window in windows
        if window["end_epoch"] > window["start_epoch"]
    )
    discontinuities = []
    for previous, current in zip(intervals, intervals[1:]):
        delta = current[0] - previous[1]
        if abs(delta) > 0.001:
            discontinuities.append(delta)
    return discontinuities


def histogram_bucket_mismatches(windows, required=False):
    """Find per-window summaries whose reported sample count disagrees with bucket totals."""
    pairs = (
        ("inFrustumQueueAgeUs", "inFrustumQueueAgeBuckets"),
        ("outOfFrustumQueueAgeUs", "outOfFrustumQueueAgeBuckets"),
        ("frameAfterUploadUs", "frameAfterUploadBuckets"),
        ("frameAfterNoUploadUs", "frameAfterNoUploadBuckets"),
        ("visibilityCaptureUs", "visibilityCaptureBuckets"),
        ("visibilityClassifyUs", "visibilityClassifyBuckets"),
        ("visibilityRecordUs", "visibilityRecordBuckets"),
    )
    mismatches = []
    for index, window in enumerate(windows, start=1):
        for histogram_name, bucket_name in pairs:
            histogram = window["hist"].get(histogram_name)
            buckets_present = bucket_name in window["buckets"]
            if histogram is None or not buckets_present:
                if required:
                    mismatches.append(f"window {index} is missing {histogram_name} or {bucket_name}")
                continue
            bucket_count = sum(window["buckets"][bucket_name].values())
            if bucket_count != histogram[0]:
                mismatches.append(
                    f"window {index} {histogram_name} reports {histogram[0]} samples but {bucket_name} accounts for {bucket_count}"
                )
    return mismatches


def flight_path(windows, before, duration, min_blocks):
    """Distance, speed, altitude and streaming figures of one run from the meter windows."""
    path = {}
    positions = [w["pos"] for w in before + windows if "pos" in w]
    if windows and len(positions) >= 2 and "pos" in windows[-1]:
        first, last = positions[0], positions[-1]
        seconds = (len(positions) - 1) * WINDOW_SECONDS
        distance = math.hypot(last[0] - first[0], last[2] - first[2])
        path["distance_blocks"] = distance
        path["speed_blocks_per_s"] = distance / seconds
        # The windows cover a little less than the flight; the speed scales it to the full flight time.
        path["flight_blocks"] = path["speed_blocks_per_s"] * duration
        path["start_pos"] = "%.1f,%.1f,%.1f" % first
        path["mean_altitude"] = sum(p[1] for p in positions) / len(positions)
        path["altitude_min"] = min(p[1] for p in positions)
        path["altitude_max"] = max(p[1] for p in positions)
        if distance > 0:
            ux, uz = (last[0] - first[0]) / distance, (last[2] - first[2]) / distance
            path["max_lateral_blocks"] = max(abs((p[0] - first[0]) * uz - (p[2] - first[2]) * ux) for p in positions)
    if windows and "ahead" in windows[0]["values"]:
        ahead = [w["values"].get("ahead", 0.0) for w in windows]
        path["ahead_min_blocks"] = min(ahead)
        path["ahead_mean_blocks"] = sum(ahead) / len(ahead)
    if windows and "chunks" in windows[0]["values"]:
        path["chunks_received"] = sum(w["values"].get("chunks", 0.0) for w in windows)
        path["columns_generated"] = sum(w["values"].get("cols", 0.0) for w in windows)
        path["windows_without_chunks"] = sum(1 for w in windows if w["values"].get("chunks", 0.0) == 0)
    if min_blocks > 0:
        if "flight_blocks" not in path:
            path["invalid"] = "no player positions in the log, the flight cannot be verified"
        elif path["flight_blocks"] < min_blocks:
            path["invalid"] = f"flew {path['flight_blocks']:.0f} blocks, below the {min_blocks:.0f} block minimum"
    return path


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


def summarize_visibility_windows(windows, duration, required=False, flight_start=None, flight_end=None):
    """Summarise only complete in-flight telemetry windows and return validation failures."""
    present = required or any(
        "visibilityAttempted" in window["values"] or "inFrustumQueueAgeBuckets" in window["buckets"]
        for window in windows
    )
    if not present:
        return {"present": False, "invalid_reasons": []}

    age_inside = summarize_bucket_histogram(windows, "inFrustumQueueAgeBuckets", "inFrustumQueueAgeUs")
    age_outside = summarize_bucket_histogram(windows, "outOfFrustumQueueAgeBuckets", "outOfFrustumQueueAgeUs")
    after_upload = summarize_bucket_histogram(windows, "frameAfterUploadBuckets", "frameAfterUploadUs")
    after_no_upload = summarize_bucket_histogram(windows, "frameAfterNoUploadBuckets", "frameAfterNoUploadUs")
    capture = summarize_bucket_histogram(windows, "visibilityCaptureBuckets", "visibilityCaptureUs")
    classify = summarize_bucket_histogram(windows, "visibilityClassifyBuckets", "visibilityClassifyUs")
    record = summarize_bucket_histogram(windows, "visibilityRecordBuckets", "visibilityRecordUs")

    counter_names = (
        "visibilityAttempted", "visibilityCaptured", "visibilityInside", "visibilityOutside",
        "visibilityCapacityDropped", "visibilityUnclassified", "visibilityPending",
        "visibilityBelow250Ms", "visibilityClassificationFailures", "visibilityContractFailures",
    )
    counters = {name: sum(window["values"].get(name, 0) for window in windows) for name in counter_names}
    coverage_seconds = covered_seconds(windows)
    failures = []

    minimum_coverage = 45 if required else min(45, duration * 0.75)
    if coverage_seconds < minimum_coverage:
        failures.append(f"only {coverage_seconds:.3f}s of complete telemetry windows, below the required {minimum_coverage:.3f}s coverage")
    if required and not (59.5 <= duration <= 60.5):
        failures.append(f"required issue flights must be 60s; this run measured {duration:.3f}s")
    if required and any(not window["precise_bounds"] for window in windows):
        failures.append("one or more telemetry windows lack precise start/end timestamps")
    if required and windows and flight_start is not None and flight_end is not None:
        if windows[0]["start_epoch"] > flight_start + WINDOW_SECONDS + 0.5:
            failures.append("complete telemetry starts more than one window after the flight began")
        if windows[-1]["end_epoch"] < flight_end - WINDOW_SECONDS - 0.5:
            failures.append("complete telemetry ends more than one window before the flight ended")
    if required:
        for delta in telemetry_window_discontinuities(windows):
            if delta > 0:
                failures.append(f"telemetry windows contain an internal gap of {delta:.3f}s")
            else:
                failures.append(f"telemetry windows overlap by {-delta:.3f}s")
    failures.extend(histogram_bucket_mismatches(windows, required=required))
    if counters["visibilityAttempted"] != counters["visibilityCaptured"] + counters["visibilityCapacityDropped"]:
        failures.append("attempted visibility samples do not equal captured plus capacity-dropped samples")
    if counters["visibilityCaptured"] != counters["visibilityInside"] + counters["visibilityOutside"] + counters["visibilityUnclassified"] + counters["visibilityPending"]:
        failures.append("captured visibility samples do not reconcile with inside, outside, unclassified, and pending counts")
    if counters["visibilityCapacityDropped"]:
        failures.append(f"{int(counters['visibilityCapacityDropped'])} visibility samples were dropped by the bounded buffer")
    if counters["visibilityUnclassified"] or counters["visibilityPending"]:
        failures.append("visibility classification left unclassified or pending samples")
    if counters["visibilityClassificationFailures"] or counters["visibilityContractFailures"]:
        failures.append("visibility classification or accounting reported a failure")
    if counters["visibilityInside"] != age_inside["samples"]:
        failures.append("in-frustum sample counter does not match its age histogram")
    if counters["visibilityOutside"] != age_outside["samples"]:
        failures.append("out-of-frustum sample counter does not match its age histogram")
    if counters["visibilityBelow250Ms"] > age_inside["samples"]:
        failures.append("below-250-ms count exceeds in-frustum age samples")
    if age_inside["samples"] < 200:
        failures.append(f"only {age_inside['samples']} in-frustum queue-age samples, below the required 200")
    if after_upload["samples"] < 200 or after_no_upload["samples"] < 200:
        failures.append("one or both upload/no-upload frame distributions have fewer than 200 samples")
    for label, histogram in (("in-frustum queue age", age_inside), ("out-of-frustum queue age", age_outside),
                             ("frames after uploads", after_upload), ("frames after no uploads", after_no_upload)):
        if histogram["p95_censored"]:
            failures.append(f"{label} p95 falls in the maximum histogram bucket and is censored")

    below_fraction = (counters["visibilityBelow250Ms"] / age_inside["samples"]) if age_inside["samples"] else None
    threshold_reached = below_fraction is not None and below_fraction >= 0.95
    threshold_failure = None
    if below_fraction is not None and not threshold_reached:
        threshold_failure = f"only {below_fraction:.3%} of in-frustum uploads were younger than 250 ms"
    run_passes = not failures and threshold_reached

    return {
        "present": True,
        "windows": len(windows),
        "coverage_seconds": coverage_seconds,
        "counters": counters,
        "in_frustum_age": age_inside,
        "out_of_frustum_age": age_outside,
        "frame_after_upload": after_upload,
        "frame_after_no_upload": after_no_upload,
        "capture_cost": capture,
        "classify_cost": classify,
        "record_cost": record,
        "bucket_sets": {
            "inFrustumQueueAgeBuckets": age_inside["bucket_counts"],
            "outOfFrustumQueueAgeBuckets": age_outside["bucket_counts"],
            "frameAfterUploadBuckets": after_upload["bucket_counts"],
            "frameAfterNoUploadBuckets": after_no_upload["bucket_counts"],
        },
        "below_250_fraction": below_fraction,
        "threshold_failure": threshold_failure,
        "threshold_pass": run_passes,
        "invalid_reasons": failures,
    }


def summarise_run(run_dir):
    markers = parse_markers(os.path.join(run_dir, "markers.env"))
    start = float(markers["FLIGHT_START_EPOCH"])
    end = float(markers["FLIGHT_END_EPOCH"])
    duration = max(end - start, 1.0)
    result = {"arm": markers["ARM"], "run": os.path.basename(run_dir), "renderer": markers.get("RENDERER", ""), "sources": {}}
    log_path = os.path.join(run_dir, "client-main.log")
    windows = stream_windows(log_path, start, end) if os.path.exists(log_path) else []
    visibility_windows = stream_windows(log_path, start, end, complete_only=True) if os.path.exists(log_path) else []
    visibility = summarize_visibility_windows(
        visibility_windows,
        duration,
        required=markers.get("REQUIRE_UPLOAD_VISIBILITY") == "1",
        flight_start=start,
        flight_end=end,
    )
    result["upload_visibility"] = visibility
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
    if visibility["present"]:
        client = result["sources"].setdefault("client", {})
        inside = visibility["in_frustum_age"]
        outside = visibility["out_of_frustum_age"]
        after_upload = visibility["frame_after_upload"]
        after_no_upload = visibility["frame_after_no_upload"]
        client.update({
            "visibility_windows": visibility["windows"],
            "visibility_coverage_seconds": visibility["coverage_seconds"],
            "in_frustum_queue_age_samples": inside["samples"],
            "in_frustum_queue_age_p50_ms_estimate": (inside["p50"] / 1000.0) if inside["p50"] is not None else None,
            "in_frustum_queue_age_p95_ms_estimate": (inside["p95"] / 1000.0) if inside["p95"] is not None else None,
            "in_frustum_queue_age_p99_ms_estimate": (inside["p99"] / 1000.0) if inside["p99"] is not None else None,
            "out_of_frustum_queue_age_samples": outside["samples"],
            "out_of_frustum_queue_age_p95_ms_estimate": (outside["p95"] / 1000.0) if outside["p95"] is not None else None,
            "in_frustum_below250_count": visibility["counters"]["visibilityBelow250Ms"],
            "in_frustum_below250_fraction": visibility["below_250_fraction"],
            "frame_after_upload_samples": after_upload["samples"],
            "frame_after_upload_p50_ms_estimate": (after_upload["p50"] / 1000.0) if after_upload["p50"] is not None else None,
            "frame_after_upload_p95_ms_estimate": (after_upload["p95"] / 1000.0) if after_upload["p95"] is not None else None,
            "frame_after_upload_p99_ms_estimate": (after_upload["p99"] / 1000.0) if after_upload["p99"] is not None else None,
            "frame_after_no_upload_samples": after_no_upload["samples"],
            "frame_after_no_upload_p50_ms_estimate": (after_no_upload["p50"] / 1000.0) if after_no_upload["p50"] is not None else None,
            "frame_after_no_upload_p95_ms_estimate": (after_no_upload["p95"] / 1000.0) if after_no_upload["p95"] is not None else None,
            "frame_after_no_upload_p99_ms_estimate": (after_no_upload["p99"] / 1000.0) if after_no_upload["p99"] is not None else None,
            "visibility_capture_p95_ms": (visibility["capture_cost"]["p95"] / 1000.0) if visibility["capture_cost"]["p95"] is not None else None,
            "visibility_classify_p95_ms": (visibility["classify_cost"]["p95"] / 1000.0) if visibility["classify_cost"]["p95"] is not None else None,
            "visibility_record_p95_ms": (visibility["record_cost"]["p95"] / 1000.0) if visibility["record_cost"]["p95"] is not None else None,
            "visibility_attempted": visibility["counters"]["visibilityAttempted"],
            "visibility_captured": visibility["counters"]["visibilityCaptured"],
            "visibility_dropped": visibility["counters"]["visibilityCapacityDropped"],
            "visibility_unclassified": visibility["counters"]["visibilityUnclassified"],
            "visibility_pending": visibility["counters"]["visibilityPending"],
            "visibility_threshold_pass": visibility["threshold_pass"],
            "visibility_threshold_failure": visibility["threshold_failure"],
            "visibility_invalid_reasons": visibility["invalid_reasons"],
        })
        client["visibility_bucket_sets"] = visibility["bucket_sets"]
    before = []
    meter = stream_windows(log_path, start, end, "ChunkFlightMeter: frameUs=", before) if os.path.exists(log_path) else []
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
            "hitch_ms": sum(w["values"].get("hitchMs", 0) for w in meter),
        }
    result["min_blocks"] = float(markers.get("MIN_BLOCKS", 0))
    result["path"] = flight_path(meter, before, duration, result["min_blocks"])
    if visibility["invalid_reasons"]:
        prior = result["path"].get("invalid")
        reasons = "; ".join(visibility["invalid_reasons"])
        result["path"]["invalid"] = f"{prior}; {reasons}" if prior else reasons
    if meter:
        result["mean_cpu_mhz"] = sum(w["values"].get("cpuMhz", 0) for w in meter) / len(meter)
        result["mean_load1"] = sum(w["values"].get("load1", 0) for w in meter) / len(meter)
    distance = result["path"].get("distance_blocks", 0.0)
    if distance > 0 and "meter" in result["sources"]:
        # Per 100 blocks flown, so runs that covered different distances stay comparable.
        result["path"]["stutters_per_100_blocks"] = result["sources"]["meter"]["stutters"] * 100.0 / distance
        result["path"]["hitch_ms_per_100_blocks"] = result["sources"]["meter"]["hitch_ms"] * 100.0 / distance
        result["path"]["chunks_per_100_blocks"] = result["path"].get("chunks_received", 0.0) * 100.0 / distance
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
        for key in ("mean_cpu_mhz", "mean_load1"):
            if key in run:
                item[key] = run[key]
        item.update(run.get("path", {}))
        if "client" in run["sources"]:
            for key in ("upload_frame_p99_ms_worst_window", "queue_age_p99_ms_worst_window", "chunks_sent", "tess_per_sent", "pool_allocs", "gc_pause_ms_total", "windows"):
                if key in run["sources"]["client"]:
                    item["detail_" + key] = run["sources"]["client"][key]
            for key in (
                "visibility_windows", "visibility_coverage_seconds", "in_frustum_queue_age_samples",
                "in_frustum_queue_age_p50_ms_estimate", "in_frustum_queue_age_p95_ms_estimate",
                "in_frustum_queue_age_p99_ms_estimate", "out_of_frustum_queue_age_samples",
                "out_of_frustum_queue_age_p95_ms_estimate", "in_frustum_below250_count",
                "in_frustum_below250_fraction", "frame_after_upload_samples",
                "frame_after_upload_p50_ms_estimate", "frame_after_upload_p95_ms_estimate",
                "frame_after_upload_p99_ms_estimate", "frame_after_no_upload_samples",
                "frame_after_no_upload_p50_ms_estimate", "frame_after_no_upload_p95_ms_estimate",
                "frame_after_no_upload_p99_ms_estimate", "visibility_capture_p95_ms",
                "visibility_classify_p95_ms", "visibility_record_p95_ms", "visibility_attempted", "visibility_captured",
                "visibility_dropped", "visibility_unclassified", "visibility_pending",
                "visibility_threshold_pass", "visibility_threshold_failure", "visibility_invalid_reasons",
            ):
                if key in run["sources"]["client"]:
                    item["detail_" + key] = run["sources"]["client"][key]
            if "visibility_bucket_sets" in run["sources"]["client"]:
                item["detail_visibility_bucket_sets"] = run["sources"]["client"]["visibility_bucket_sets"]
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
    ("hitch_ms", "hitch ms (sum of frames over 50 ms)", "lower"),
    ("stutters_per_100_blocks", "frames over 50 ms per 100 blocks", "lower"),
    ("hitch_ms_per_100_blocks", "hitch ms per 100 blocks", "lower"),
    ("flight_blocks", "blocks flown", "higher"),
    ("ahead_mean_blocks", "loaded terrain ahead, mean blocks", "higher"),
    ("ahead_min_blocks", "loaded terrain ahead, min blocks", "higher"),
    ("chunks_received", "chunks received", "higher"),
    ("mean_cpu_mhz", "mean CPU MHz", "higher"),
]


def render(all_runs):
    invalid = [run for run in all_runs if run.get("invalid")]
    runs = [run for run in all_runs if not run.get("invalid")]
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
    for run in all_runs:
        if "detail_windows" in run:
            extras.append(f"- {run['run']}: upload p99 worst window {run['detail_upload_frame_p99_ms_worst_window']:.2f} ms, queue age p99 worst window {run['detail_queue_age_p99_ms_worst_window']:.2f} ms, chunks sent {run['detail_chunks_sent']:.0f}, tessellations per sent chunk {run['detail_tess_per_sent']:.2f}, pool allocations {run['detail_pool_allocs']:.0f}, GC pause {run['detail_gc_pause_ms_total']:.0f} ms")
        if "detail_in_frustum_queue_age_samples" in run:
            fraction = run["detail_in_frustum_below250_fraction"]
            fraction_text = "n/a" if fraction is None else f"{fraction:.2%}"
            decision = "INVALID" if run["detail_visibility_invalid_reasons"] else ("PASS" if run["detail_visibility_threshold_pass"] else "FAIL")
            p95_inside = run["detail_in_frustum_queue_age_p95_ms_estimate"]
            p95_outside = run["detail_out_of_frustum_queue_age_p95_ms_estimate"]
            p95_after_upload = run["detail_frame_after_upload_p95_ms_estimate"]
            p95_after_no_upload = run["detail_frame_after_no_upload_p95_ms_estimate"]
            fmt = lambda value: "n/a" if value is None else f"{value:.2f} ms"
            extras.append(
            f"- {run['run']}: visibility windows {run['detail_visibility_windows']} / {run['detail_visibility_coverage_seconds']:.3f} s; "
                f"in-frustum queue age n={run['detail_in_frustum_queue_age_samples']}, p95 estimate {fmt(p95_inside)}, "
                f"<250 ms {fraction_text} ({run['detail_in_frustum_below250_count']:.0f} samples): {decision}; "
                f"outside n={run['detail_out_of_frustum_queue_age_samples']}, p95 estimate {fmt(p95_outside)}; "
                f"frame after upload n={run['detail_frame_after_upload_samples']}, p95 {fmt(p95_after_upload)}; "
                f"after no upload n={run['detail_frame_after_no_upload_samples']}, p95 {fmt(p95_after_no_upload)}; "
                f"capture/classification/recording CPU p95 {fmt(run['detail_visibility_capture_p95_ms'])}/"
                f"{fmt(run['detail_visibility_classify_p95_ms'])}/{fmt(run['detail_visibility_record_p95_ms'])}; "
                f"dropped/unclassified/pending {run['detail_visibility_dropped']:.0f}/{run['detail_visibility_unclassified']:.0f}/{run['detail_visibility_pending']:.0f}"
            )
            for reason in run["detail_visibility_invalid_reasons"]:
                extras.append(f"  - INVALID evidence: {reason}")
            if run.get("detail_visibility_threshold_failure"):
                extras.append(f"  - Threshold: {run['detail_visibility_threshold_failure']}")
    flights, warnings = [], []
    for run in all_runs:
        if "flight_blocks" not in run:
            continue
        flights.append(
            f"- {run['run']}: measured from {run['start_pos']}, {run['flight_blocks']:.0f} blocks at {run['speed_blocks_per_s']:.1f} blocks/s, "
            f"altitude {run['altitude_min']:.1f} to {run['altitude_max']:.1f}, off the line by at most {run.get('max_lateral_blocks', 0.0):.1f} blocks"
            + (f", terrain loaded ahead {run['ahead_min_blocks']:.0f} to mean {run['ahead_mean_blocks']:.0f} blocks" if "ahead_min_blocks" in run else "")
            + (f", {run['chunks_received']:.0f} chunks received, {run['columns_generated']:.0f} columns loaded by the server, {run['windows_without_chunks']} windows without a chunk" if "chunks_received" in run else ""))
        if run.get("ahead_min_blocks", 1.0) <= 0:
            warnings.append(f"- {run['run']}: the player outran chunk loading (no loaded terrain ahead in at least one window), so part of the flight rendered the void")
        if run.get("windows_without_chunks", 0) > 0:
            warnings.append(f"- {run['run']}: {run['windows_without_chunks']} flight windows received no chunk, the flight was not streaming all the time")
    text = ["## Chunk flight benchmark", "", *lines, ""]
    if flights:
        text += ["### Flights (meter positions)", "", *flights, ""]
    if warnings:
        text += ["### Warnings", "", *warnings, ""]
    if comparisons:
        text += ["### Welch t-test per metric", "", *comparisons, ""]
    if extras:
        text += ["### Streaming detail (Optimum builds)", "", *extras, "", "Queue-age and frame percentiles are upper-bound estimates from fixed histogram buckets. The <250 ms fraction is exact and uses raw elapsed ticks.", ""]
    visibility_arms = {}
    for run in all_runs:
        if "detail_visibility_threshold_pass" in run:
            visibility_arms.setdefault(run["arm"], []).append(run)
    if visibility_arms:
        summaries = []
        for arm, items in visibility_arms.items():
            valid = [item for item in items if not item.get("invalid")]
            if len(items) != 6 or len(valid) != 6:
                summaries.append(f"- {arm}: INCOMPLETE, {len(valid)}/6 runs valid; no issue decision")
                continue
            passed = all(item["detail_visibility_threshold_pass"] for item in valid)
            below = sum(item["detail_in_frustum_below250_count"] for item in valid)
            samples = sum(item["detail_in_frustum_queue_age_samples"] for item in valid)
            fraction = below / samples if samples else 0.0
            p95_values = [item["detail_in_frustum_queue_age_p95_ms_estimate"] for item in valid if item["detail_in_frustum_queue_age_p95_ms_estimate"] is not None]
            range_text = f"{min(p95_values):.2f} to {max(p95_values):.2f} ms" if p95_values else "n/a"
            summaries.append(
                f"- {arm}: {'PASS' if passed else 'FAIL'} all six per-run gates; pooled exact <250 ms fraction "
                f"{fraction:.2%} ({below}/{samples}); per-run p95 estimate range {range_text}"
            )
            pooled_labels = (
                ("inFrustumQueueAgeBuckets", "in-frustum queue age"),
                ("outOfFrustumQueueAgeBuckets", "out-of-frustum queue age"),
                ("frameAfterUploadBuckets", "frames after uploads"),
                ("frameAfterNoUploadBuckets", "frames after no uploads"),
            )
            for bucket_key, label in pooled_labels:
                pooled_buckets = {}
                for item in valid:
                    for index, count in item.get("detail_visibility_bucket_sets", {}).get(bucket_key, {}).items():
                        pooled_buckets[index] = pooled_buckets.get(index, 0) + count
                count = sum(pooled_buckets.values())
                if not count:
                    summaries.append(f"  - pooled {label}: no samples")
                    continue
                percentiles = []
                for fraction_value in (0.50, 0.95, 0.99):
                    value, _ = percentile_with_censoring(pooled_buckets, fraction_value)
                    percentiles.append("n/a" if value is None else f"{value / 1000.0:.2f} ms")
                summaries.append(
                    f"  - pooled {label}: n={count}; p50/p95/p99 upper-bound estimates "
                    + "/".join(percentiles)
                )
        text += ["### Issue #550 six-run gate", "", *summaries, ""]
    if invalid:
        text += ["### INVALID runs, excluded from the statistics", "", *[f"- {run['run']}: INVALID, {run['invalid']}" for run in invalid], ""]
    renderers = sorted({run["renderer"] for run in all_runs if run["renderer"]})
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


def synthetic_log(path, start, frame_us_by_window, blocks_per_window=None, ahead=512):
    """Write a client log with one chunk stream window per entry, and one meter window when a speed is given."""
    with open(path, "w", encoding="utf-8") as handle:
        if blocks_per_window is not None:
            # Warm-up windows before the flight: the player stands at the start.
            for i in (-1, 0):
                stamp = datetime.datetime.fromtimestamp(start + WINDOW_SECONDS * i).strftime("%d.%m.%Y %H:%M:%S")
                handle.write(f"{stamp} [Notification] ChunkFlightMeter: frameUs=n1/p50 9000/p95 9000/p99 9000/max 9000, over33ms=0, over50ms=0, over100ms=0, "
                             f"hitchMs=0, pos=512000.0,160.0,512000.0, ahead=512, chunks=0, cols=0, load1=1.00, cpuMhz=3000, frameMeanUs=9000, frameBuckets=52:1\n")
            handle.write("11.11.2026 09:59:00 [Notification] ChunkFlightMeter: setup complete pos=512000.0,160.0,512000.0 yawDeg=0.0\n")
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
            if blocks_per_window is not None:
                hitch = sum(v for v in samples if v > 50000) // 1000
                handle.write(
                    f"{stamp} [Notification] ChunkFlightMeter: frameUs=n{len(samples)}/p50 1/p95 1/p99 1/max {max(samples)}, over33ms=0, over50ms={over50}, over100ms=0, "
                    f"hitchMs={hitch}, pos=512000.0,160.0,{512000.0 + blocks_per_window * (i + 1):.1f}, ahead={ahead}, chunks={80 if blocks_per_window else 0}, cols=10, "
                    f"load1=1.50, cpuMhz=3000, frameMeanUs={sum(samples) // len(samples)}, frameBuckets={bucket_text}\n")


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
        assert "INVALID" not in text, text

    # Flights with the meter: distance, normalised metrics, INVALID runs and the outrun warning.
    with tempfile.TemporaryDirectory() as root:
        start = datetime.datetime(2026, 11, 11, 10, 0, 0).timestamp()
        cases = (("vanilla-01", "vanilla", 130.0, 512), ("vanilla-02", "vanilla", 131.0, 512), ("optimum-01", "optimum", 130.0, 512),
                 ("optimum-02", "optimum", 132.0, 0), ("optimum-03", "optimum", 5.0, 512), ("optimum-04", "optimum", None, 512))
        for name, arm, blocks, ahead in cases:
            run = os.path.join(root, "runs", name)
            os.makedirs(run)
            with open(os.path.join(run, "markers.env"), "w", encoding="utf-8") as handle:
                handle.write(f"ARM={arm}\nFLIGHT_START_EPOCH={start}\nFLIGHT_END_EPOCH={start + 20}\nRENDERER=test gpu\nMIN_BLOCKS=400\n")
            windows = [[10000 + k for k in range(100)] + [60000, 70000] for _ in range(4)]
            synthetic_log(os.path.join(run, "client-main.log"), start, windows, blocks, ahead)
        runs = [summarise_run(d) for d in sorted(glob.glob(os.path.join(root, "runs", "*")))]
        metered = [r for r in runs if r["run"] != "optimum-04"]
        unmetered = [r for r in runs if r["run"] == "optimum-04"][0]
        source, warning = choose_source(metered)
        assert source == "meter" and not warning, (source, warning)
        # The run without meter lines still has the client source, so the meter is no common ruler for all six.
        assert choose_source(runs)[0] == "client"
        flat = {r["run"]: r for r in flatten(metered, source)}
        good = flat["vanilla-01"]
        assert abs(good["distance_blocks"] - 520.0) < 0.01 and abs(good["speed_blocks_per_s"] - 26.0) < 0.01, good
        assert abs(good["flight_blocks"] - 520.0) < 0.01 and good["start_pos"] == "512000.0,160.0,512000.0", good
        assert good["altitude_min"] == 160.0 and good["max_lateral_blocks"] == 0.0, good
        assert good["stutters"] == 8 and good["hitch_ms"] == 520, good
        assert abs(good["stutters_per_100_blocks"] - 8 * 100 / 520.0) < 1e-9, good
        assert abs(good["hitch_ms_per_100_blocks"] - 100.0) < 1e-9, good
        assert good["chunks_received"] == 320 and good["windows_without_chunks"] == 0 and good["ahead_min_blocks"] == 512, good
        assert "invalid" not in good and "invalid" not in flat["optimum-02"]
        assert "below the 400 block minimum" in flat["optimum-03"]["invalid"], flat["optimum-03"]
        missing = flatten([unmetered], "client")[0]
        assert "no player positions" in missing["invalid"], missing
        text = render(list(flat.values()) + [missing])
        assert "optimum-03: INVALID, flew 20 blocks" in text and "optimum-04: INVALID, no player positions" in text, text
        assert "| optimum | 2 | meter |" in text and "| vanilla | 2 | meter |" in text, text
        assert "optimum-02: the player outran chunk loading" in text, text
        assert "frames over 50 ms per 100 blocks" in text and "vanilla-01: measured from 512000.0,160.0,512000.0, 520 blocks at 26.0 blocks/s" in text, text

    # Visibility telemetry excludes partial flight-edge windows and decides the 250 ms rule exactly.
    with tempfile.TemporaryDirectory() as root:
        start = datetime.datetime(2026, 11, 12, 10, 0, 0).timestamp()
        end = start + 60

        def bucket_text(values):
            counts = {}
            for value in values:
                if value < SUB_BUCKETS:
                    index = value
                else:
                    msb = value.bit_length() - 1
                    index = ((msb - 1) << SUB_BUCKET_BITS) + ((value >> (msb - SUB_BUCKET_BITS)) & (SUB_BUCKETS - 1))
                counts[index] = counts.get(index, 0) + 1
            return ",".join(f"{index}:{count}" for index, count in sorted(counts.items()))

        def visibility_line(window_start, window_end, below, logged_delay=0):
            inside = [10_000] * below + [300_000] * (100 - below)
            outside = [300_000] * 5
            after_upload = [16_000] * 20
            after_no_upload = [20_000] * 20
            time_text = datetime.datetime.fromtimestamp(window_end + logged_delay).strftime("%d.%m.%Y %H:%M:%S")
            return (
                f"{time_text} [Notification] streamWindowStartEpochMs={round(window_start * 1000)}, "
                f"streamWindowEndEpochMs={round(window_end * 1000)}, Optimum chunk stream: "
                f"frameUs=n1/p50 16000/p95 16000/p99 16000/max 16000, frameBuckets={bucket_text([16_000])}, "
                f"inFrustumQueueAgeUs=n100/p50 10000/p95 300000/p99 300000/max 300000, "
                f"inFrustumQueueAgeBuckets={bucket_text(inside)}, "
                f"outOfFrustumQueueAgeUs=n5/p50 300000/p95 300000/p99 300000/max 300000, "
                f"outOfFrustumQueueAgeBuckets={bucket_text(outside)}, "
                f"frameAfterUploadUs=n20/p50 16000/p95 16000/p99 16000/max 16000, "
                f"frameAfterUploadBuckets={bucket_text(after_upload)}, "
                f"frameAfterNoUploadUs=n20/p50 20000/p95 20000/p99 20000/max 20000, "
                f"frameAfterNoUploadBuckets={bucket_text(after_no_upload)}, "
                f"visibilityCaptureUs=n1/p50 1/p95 1/p99 1/max 1, visibilityCaptureBuckets=1:1, "
                f"visibilityClassifyUs=n1/p50 1/p95 1/p99 1/max 1, visibilityClassifyBuckets=1:1, "
                f"visibilityRecordUs=n1/p50 1/p95 1/p99 1/max 1, visibilityRecordBuckets=1:1, "
                f"visibilityAttempted=105, visibilityCaptured=105, visibilityInside=100, visibilityOutside=5, "
                f"visibilityCapacityDropped=0, visibilityUnclassified=0, visibilityPending=0, "
                f"visibilityBelow250Ms={below}, visibilityClassificationFailures=0, visibilityContractFailures=0\n"
            )

        log_path = os.path.join(root, "client-main.log")
        with open(log_path, "w", encoding="utf-8") as handle:
            handle.write(visibility_line(start - 0.001, start + 4.999, 0))  # Rounded log time, partial warm-up window.
            for i in range(12):
                window_start = start + i * WINDOW_SECONDS
                handle.write(visibility_line(window_start, window_start + WINDOW_SECONDS, 95, logged_delay=2))
            handle.write(visibility_line(end - 4.999, end + 0.001, 0))  # Rounded log time, partial post-flight window.
        complete = stream_windows(log_path, start, end, complete_only=True)
        assert len(complete) == 12, len(complete)
        passing = summarize_visibility_windows(complete, 60)
        assert passing["coverage_seconds"] == 60 and not passing["invalid_reasons"], passing
        assert passing["below_250_fraction"] == 0.95 and passing["threshold_pass"], passing
        assert covered_seconds([
            {"start_epoch": 0, "end_epoch": 5},
            {"start_epoch": 4, "end_epoch": 10},
            {"start_epoch": 12, "end_epoch": 13},
        ]) == 11
        gapped = complete[:5] + complete[6:]
        gap_summary = summarize_visibility_windows(gapped, 60, required=True)
        assert any("internal gap" in reason for reason in gap_summary["invalid_reasons"]), gap_summary
        edge_missing = summarize_visibility_windows(complete[2:], 60, required=True, flight_start=start, flight_end=end)
        assert any("starts more than one window" in reason for reason in edge_missing["invalid_reasons"]), edge_missing
        bucket_mismatch = [{**window, "hist": dict(window["hist"]), "buckets": dict(window["buckets"])} for window in complete]
        bucket_mismatch[0]["buckets"]["frameAfterUploadBuckets"] = {}
        mismatch_summary = summarize_visibility_windows(bucket_mismatch, 60, required=True)
        assert any("frameAfterUploadUs reports 20 samples but frameAfterUploadBuckets accounts for 0" in reason
                   for reason in mismatch_summary["invalid_reasons"]), mismatch_summary

        failing_windows = [{**window, "values": dict(window["values"])} for window in complete]
        for window in failing_windows:
            window["values"]["visibilityBelow250Ms"] = 94
        failing = summarize_visibility_windows(failing_windows, 60)
        assert failing["below_250_fraction"] == 0.94 and not failing["threshold_pass"], failing
        assert not failing["invalid_reasons"] and failing["threshold_failure"], failing
        short_required = summarize_visibility_windows(complete, 30, required=True)
        assert any("must be 60s" in reason for reason in short_required["invalid_reasons"]), short_required
        missing_required = summarize_visibility_windows([], 60, required=True)
        assert missing_required["present"] and missing_required["invalid_reasons"], missing_required

        # A valid below-threshold run is a FAIL within the six-run decision, not invalid evidence.
        runs_root = os.path.join(root, "runs")
        os.makedirs(runs_root)
        for i in range(6):
            run_dir = os.path.join(runs_root, f"optimum-{i + 1:02d}")
            os.makedirs(run_dir)
            with open(os.path.join(run_dir, "markers.env"), "w", encoding="utf-8") as handle:
                handle.write(f"ARM=optimum\nFLIGHT_START_EPOCH={start}\nFLIGHT_END_EPOCH={end}\nREQUIRE_UPLOAD_VISIBILITY=1\nMIN_BLOCKS=0\n")
            with open(os.path.join(run_dir, "client-main.log"), "w", encoding="utf-8") as handle:
                for window_index in range(12):
                    window_start = start + window_index * WINDOW_SECONDS
                    below = 94 if i == 2 else 95
                    handle.write(visibility_line(window_start, window_start + WINDOW_SECONDS, below, logged_delay=2))
        required_runs = [summarise_run(path) for path in sorted(glob.glob(os.path.join(runs_root, "*")))]
        required_flat = flatten(required_runs, choose_source(required_runs)[0])
        failing = next(run for run in required_flat if run["run"] == "optimum-03")
        assert "invalid" not in failing and not failing["detail_visibility_threshold_pass"], failing
        report = render(required_flat)
        assert "optimum: FAIL all six per-run gates" in report and "INCOMPLETE" not in report, report
        assert "optimum-03: visibility windows 12 / 60.000 s" in report and "Threshold: only 94.000%" in report, report
        assert "pooled in-frustum queue age: n=7200; p50/p95/p99 upper-bound estimates" in report, report

    with tempfile.TemporaryDirectory() as root:
        run_dir = os.path.join(root, "required-01")
        os.makedirs(run_dir)
        with open(os.path.join(run_dir, "markers.env"), "w", encoding="utf-8") as handle:
            handle.write("ARM=optimum\nFLIGHT_START_EPOCH=1794400000\nFLIGHT_END_EPOCH=1794400060\nREQUIRE_UPLOAD_VISIBILITY=1\nMIN_BLOCKS=0\n")
        missing = summarise_run(run_dir)
        report = render(flatten([missing], None))
        assert "required-01: INVALID" in report and "INCOMPLETE, 0/6 runs valid" in report, report
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
