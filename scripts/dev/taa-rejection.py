#!/usr/bin/env python3
"""TAA history rejection rates from one OPTIMUM_PARITY_DUMP directory.

Guards the distant foliage rejection regression in docs/vulkan.md: a
single-sample disocclusion test in
taa-resolve.fsh threw the history away on ~3.7% of distant leaf pixels per
frame on both backends, because a sub-pixel leaf hits the leaf in one jitter
phase and the far background in the next. The 3x3 nearest-depth test that
replaced it measured ~1.1%. A later real-world capture exposed remaining
hard resets at sparse distant depth edges; the resolve now retains colour-clipped
history there. Solid silhouettes still reset. Do not revert to a single-sample depth test.

Inputs, by the file names both backends share (OptimumParityDump.FileNameFormat):
  19-OptimumTaaHistoryA-color2-r32f.pfm  linear view depth, one history slot
  20-OptimumTaaHistoryB-color2-r32f.pfm  linear view depth, the other slot
      (this frame's and last frame's, in an order set by the frame parity; both
       tests below are symmetric, so the order does not matter)
  0-Primary-depth-depth.pfm              window depth; >= 0.999999 is sky, excluded
  0-Primary-color2-rgba16f.alpha.pfm     leaf mask: alpha > 0.5
PFM, float32, negative scale = little-endian, GL row order; channel 0 is used.

Tests, per pixel, over the pixels where both history depths are finite and
positive and the window depth is finite and not sky:
  single-sample  |a - b| > 0.5 + 0.08 * min(a, b)
  3x3 nearest    the same on min3x3(a) and min3x3(b) (a 3x3 minimum filter on
                 both sides first; non-finite taps ignored, edges clamped)
  hard resets    3x3 nearest failures outside a sparse distant depth edge. The two
                 history depths approximate the resolve's current linear depth.

Regions, by the quantiles p50 and p90 of min(a, b) over those pixels:
  near <= p50 < mid <= p90 < far; leaf-mid and leaf-far are the leaf-masked
  parts of mid and far.

Usage:
  scripts/dev/taa-rejection.py <dump dir> [--max-leaf-far 1.5]
  scripts/dev/taa-rejection.py --self-test
Exit: 0 pass; 1 the distant leaf hard-reset rate (percent) is above
--max-leaf-far; 2 usage or input error (missing file, shape mismatch, no
leaf-far pixels to judge). numpy only.
"""
import argparse
import io
import math
import os
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ssim import ParityError, read_image, write_pfm  # noqa: E402  (same directory, PFM io)

HISTORY_A = "19-OptimumTaaHistoryA-color2-r32f.pfm"
HISTORY_B = "20-OptimumTaaHistoryB-color2-r32f.pfm"
DEPTH = "0-Primary-depth-depth.pfm"
LEAF = "0-Primary-color2-rgba16f.alpha.pfm"
REGIONS = ("near", "mid", "far", "leaf-mid", "leaf-far")
SKY_DEPTH = 0.999999


def filter3x3(plane, kind):
    """3x3 depth extrema; non-finite taps cannot win, edges clamp."""
    finite = np.where(np.isfinite(plane), plane, np.inf if kind == "min" else -np.inf)
    padded = np.pad(finite, 1, mode="edge")
    height, width = plane.shape
    out = np.full(plane.shape, np.inf if kind == "min" else -np.inf)
    for dy in range(3):
        for dx in range(3):
            op = np.minimum if kind == "min" else np.maximum
            out = op(out, padded[dy:dy + height, dx:dx + width])
    return out


def min3x3(plane):
    return filter3x3(plane, "min")


def rejected(a, b):
    """The resolve's disocclusion comparison: |a - b| > 0.5 + 0.08 * min(a, b)."""
    with np.errstate(invalid="ignore"):
        return np.abs(a - b) > 0.5 + 0.08 * np.minimum(a, b)


def analyse(a, b, depth, alpha):
    """Returns {pixels, p50, p90, rows: {region: (pixels, single hits, 3x3 hits)}}."""
    shapes = sorted({a.shape, b.shape, depth.shape, alpha.shape})
    if len(shapes) != 1:
        raise ParityError("attachment shapes differ: %s" % shapes)
    with np.errstate(invalid="ignore"):
        valid = (np.isfinite(a) & np.isfinite(b) & np.isfinite(depth)
                 & (a > 0) & (b > 0) & (depth < SKY_DEPTH))
    count = int(valid.sum())
    if count == 0:
        raise ParityError("no finite, non-sky history pixels")
    reference = np.minimum(a, b)
    p50, p90 = (float(v) for v in np.quantile(reference[valid], [0.5, 0.9]))
    leaf = np.isfinite(alpha) & (alpha > 0.5)
    mid = valid & (reference > p50) & (reference <= p90)
    far = valid & (reference > p90)
    masks = {
        "near": valid & (reference <= p50),
        "mid": mid,
        "far": far,
        "leaf-mid": mid & leaf,
        "leaf-far": far & leaf,
    }
    single = rejected(a, b) & valid
    nearest = rejected(min3x3(a), min3x3(b)) & valid
    # The shipped resolve keeps clipped history at distant depth edges. The
    # history slots approximate its closest current linear depth for this dump.
    nearest_depth = min3x3(depth)
    height, width = depth.shape
    padded = np.pad(depth, 1, mode="edge")
    near_taps = np.zeros(depth.shape, dtype=np.uint8)
    for dy in range(3):
        for dx in range(3):
            near_taps += (np.abs(padded[dy:dy + height, dx:dx + width] - nearest_depth)
                          <= 2e-4).astype(np.uint8)
    # Match the resolve's sparse-coverage exception. A broad solid silhouette
    # must stay in the hard-reset numerator, even when it is distant.
    distant_edge = ((reference > 20.0) & (filter3x3(depth, "max") - nearest_depth > 2e-4)
                    & (near_taps <= 2))
    hard_reset = nearest & ~distant_edge
    rows = {}
    for name in REGIONS:
        mask = masks[name]
        rows[name] = (int(mask.sum()), int((single & mask).sum()),
                      int((nearest & mask).sum()), int((hard_reset & mask).sum()))
    return {"pixels": count, "p50": p50, "p90": p90, "rows": rows}


def percent(hits, pixels):
    return float("nan") if pixels == 0 else 100.0 * hits / pixels


def _format(value):
    return "-" if math.isnan(value) else "%.2f%%" % value


def load(directory, name):
    path = os.path.join(directory, name)
    if not os.path.isfile(path):
        raise ParityError("missing %s" % path)
    return read_image(path)[0][:, :, 0]


def run(directory, max_leaf_far=1.5, out=sys.stdout):
    if not os.path.isdir(directory):
        raise ParityError("not a directory: %s" % directory)
    result = analyse(load(directory, HISTORY_A), load(directory, HISTORY_B),
                     load(directory, DEPTH), load(directory, LEAF))
    out.write("taa-rejection: %s\n" % directory)
    out.write("%d pixels judged; linear depth p50 %.4g, p90 %.4g blocks\n\n"
              % (result["pixels"], result["p50"], result["p90"]))
    out.write("| region | pixels | single-sample rejected | 3x3 nearest rejected | hard resets |\n")
    out.write("|---|---|---|---|---|\n")
    for name in REGIONS:
        pixels, single, nearest, hard_reset = result["rows"][name]
        out.write("| %s | %d | %s | %s | %s |\n" %
                  (name, pixels, _format(percent(single, pixels)),
                   _format(percent(nearest, pixels)), _format(percent(hard_reset, pixels))))
    pixels, _, _, hard_reset = result["rows"]["leaf-far"]
    if pixels == 0:
        raise ParityError("no leaf pixels beyond p90 (%.4g blocks): the dump has no distant foliage to judge"
                          % result["p90"])
    rate = percent(hard_reset, pixels)
    ok = rate <= max_leaf_far
    out.write("\nDistant leaf hard resets %.2f%% (max %.2f%%): %s\n"
              % (rate, max_leaf_far, "ok" if ok else "FAIL"))
    return 0 if ok else 1


# ----------------------------------------------------------------- self-test

def synthetic_dump(leaf_moves):
    """64x64: a near-to-mid ground gradient (2..100 blocks) under six far rows at
    300 blocks holding sub-pixel leaves at 250 blocks, a sky column, and a 12x12
    genuine disocclusion in the near field. With leaf_moves the other history slot
    has each leaf one pixel to the right (the next jitter phase); without it the
    leaves are gone from the other slot."""
    size = 64
    gradient = 2.0 + np.arange(size, dtype=np.float64) * (98.0 / 57.0)
    a = np.repeat(gradient[:, None], size, axis=1)
    a[58:, :] = 300.0
    b = a.copy()
    depth = np.full((size, size), 0.5)
    alpha = np.zeros((size, size))
    for y in (59, 62):
        for x in range(2, size - 4, 4):
            a[y, x] = 250.0
            alpha[y, x] = 1.0
            if leaf_moves:
                b[y, x + 1] = 250.0
    b[10:22, 10:22] = 1.0
    depth[:, size - 1] = 1.0
    a[:, size - 1] = np.nan
    b[:, size - 1] = np.nan
    return a, b, depth, alpha


def write_dump(directory, arrays):
    os.makedirs(directory)
    for name, array in zip((HISTORY_A, HISTORY_B, DEPTH, LEAF), arrays):
        write_pfm(os.path.join(directory, name), array.astype(np.float32))


def self_test():
    moving = synthetic_dump(leaf_moves=True)
    result = analyse(*moving)
    leaves = int(moving[3].sum())
    assert result["pixels"] == 63 * 64, result["pixels"]

    # 1. a flipping sub-pixel leaf fails the single-sample test and passes the 3x3 test
    pixels, single, nearest, hard_reset = result["rows"]["leaf-far"]
    assert pixels == leaves, (pixels, leaves)
    assert single == leaves, result["rows"]["leaf-far"]
    assert nearest == 0, result["rows"]["leaf-far"]
    assert hard_reset == 0, result["rows"]["leaf-far"]

    # 2. a disocclusion larger than 3x3 is rejected by both tests
    _, single, nearest, hard_reset = result["rows"]["near"]
    assert single >= 144, result["rows"]["near"]
    assert nearest >= 100, result["rows"]["near"]
    assert hard_reset >= 100, result["rows"]["near"]

    # A distant leaf that leaves both 3x3 windows retains clipped history
    # at a silhouette; a flat-depth mismatch still resets.
    vanished = synthetic_dump(leaf_moves=False)
    flat = analyse(*vanished)
    assert flat["rows"]["leaf-far"][3] == leaves, flat["rows"]["leaf-far"]
    a, b, depth, alpha = vanished
    edge_depth = depth.copy()
    edge_depth[alpha > 0.5] = 0.49
    edge = analyse(a, b, edge_depth, alpha)
    assert edge["rows"]["leaf-far"][2] == leaves, edge["rows"]["leaf-far"]
    assert edge["rows"]["leaf-far"][3] == 0, edge["rows"]["leaf-far"]

    solid_depth = depth.copy()
    solid_depth[58:, :32] = 0.49
    solid = analyse(a, b, solid_depth, alpha)
    assert solid["rows"]["leaf-far"][3] > 0, solid["rows"]["leaf-far"]

    with tempfile.TemporaryDirectory(prefix="optimum-taa-rejection-self-test-") as root:
        # 3. through the files: the moving leaf passes the gate
        passing = os.path.join(root, "moving")
        write_dump(passing, moving)
        sink = io.StringIO()
        assert run(passing, out=sink) == 0, sink.getvalue()
        assert "| leaf-far | %d | 100.00%% | 0.00%% | 0.00%% |" % leaves in sink.getvalue(), sink.getvalue()
        assert ": ok" in sink.getvalue(), sink.getvalue()

        # 4. a leaf that vanishes from the other slot is rejected by the 3x3 test too: exit 1
        failing = os.path.join(root, "vanishing")
        write_dump(failing, synthetic_dump(leaf_moves=False))
        sink = io.StringIO()
        assert run(failing, out=sink) == 1, sink.getvalue()
        assert "FAIL" in sink.getvalue(), sink.getvalue()
        sink = io.StringIO()
        assert run(failing, max_leaf_far=100.0, out=sink) == 0, sink.getvalue()

        # 5. no foliage to judge, and a missing file, are input errors
        a, b, depth, _ = moving
        bare = os.path.join(root, "bare")
        write_dump(bare, (a, b, depth, np.zeros_like(depth)))
        for directory in (bare, os.path.join(root, "absent")):
            try:
                run(directory, out=io.StringIO())
            except ParityError:
                pass
            else:
                raise AssertionError("expected an input error for %s" % directory)
        os.remove(os.path.join(bare, LEAF))
        try:
            run(bare, out=io.StringIO())
        except ParityError as error:
            assert "missing" in str(error), error
        else:
            raise AssertionError("expected a missing-file error")

    print("taa-rejection.py self-test: ok")
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dump_dir", nargs="?")
    parser.add_argument("--max-leaf-far", type=float, default=1.5,
                        help="maximum distant leaf hard-reset rate, percent (default 1.5)")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.self_test:
            return self_test()
        if not args.dump_dir:
            parser.print_usage(sys.stderr)
            return 2
        return run(args.dump_dir, args.max_leaf_far)
    except ParityError as error:
        print("taa-rejection.py: %s" % error, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
