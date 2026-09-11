#!/usr/bin/env python3
"""Per-attachment parity comparison for OPTIMUM_PARITY_DUMP directories.

Pairs the files of two dump directories by name (the file-name format both
backends share, OptimumParityDump.FileNameFormat:
"<slotIndex>-<slotName>-<color<i>|depth>-<format>.<ext>"), computes SSIM and
the mean absolute difference per attachment, prints a markdown table and exits
non-zero when an attachment is below the threshold and not allowlisted, or
exists on one side only.

Encodings read (rows are compared in file order; both backends write GL row
order, bottom-up, so no flip is applied):
  .ppm  binary P6, SSIM on luminance (0.2126 R + 0.7152 G + 0.0722 B), L = 255
  .pgm  binary P5, one channel, L = 255
  .pfm  PF (RGB) or Pf (grey), float32, negative scale = little-endian;
        SSIM per channel, the reported value is the lowest channel;
        L = max(1, peak-to-peak of both images' finite values in that channel)

SSIM: Gaussian window 11x11, sigma 1.5, C1 = (0.01 L)^2, C2 = (0.03 L)^2, mean
over the valid window positions (images smaller than the window use one global
window). Mean absolute difference is in native units: 0-255 for PPM/PGM, raw
values for PFM. A difference in the pattern of non-finite values (NaN/Inf) is a
failure on its own. numpy only.

Allowlist (docs/parity-allowlist.md): a markdown table whose first column is
the attachment file name or an fnmatch glob (backticks are stripped) and whose
third column bounds the accepted deviation with one or more of
  ssim>=<x>   mad<=<x>   missing   any
separated by commas. "missing" accepts a file that exists on one side only;
"any" accepts everything. An allowlisted attachment outside its bounds fails.

Usage:
  scripts/dev/ssim.py <dirA> <dirB> [--allowlist docs/parity-allowlist.md]
                      [--threshold 0.98] [--csv out.csv]
  scripts/dev/ssim.py --self-test
Exit: 0 all attachments pass, 1 a failure, 2 usage or allowlist error.
"""
import argparse
import csv
import fnmatch
import math
import os
import sys
import tempfile

import numpy as np

EXTENSIONS = (".ppm", ".pgm", ".pfm")
WINDOW = 11
SIGMA = 1.5


class ParityError(Exception):
    """A usage or input error (exit 2)."""


# ------------------------------------------------------------------ file io

def _header_tokens(data, count):
    tokens = []
    pos = 0
    size = len(data)
    while len(tokens) < count:
        while pos < size and data[pos:pos + 1].isspace():
            pos += 1
        if pos < size and data[pos:pos + 1] == b"#":
            while pos < size and data[pos:pos + 1] not in (b"\n", b"\r"):
                pos += 1
            continue
        start = pos
        while pos < size and not data[pos:pos + 1].isspace():
            pos += 1
        if start == pos:
            raise ParityError("truncated header")
        tokens.append(data[start:pos].decode("ascii"))
    # exactly one whitespace byte separates the header from the raster
    return tokens, pos + 1


def read_image(path):
    """Returns (array HxWxC float64, kind) with kind in ppm/pgm/pfm."""
    with open(path, "rb") as handle:
        data = handle.read()
    magic = data[:2]
    tokens, offset = _header_tokens(data, 4)
    width, height = int(tokens[1]), int(tokens[2])
    if magic in (b"P6", b"P5"):
        channels = 3 if magic == b"P6" else 1
        if int(tokens[3]) != 255:
            raise ParityError("%s: only maxval 255 is supported" % path)
        raster = np.frombuffer(data, dtype=np.uint8, count=width * height * channels, offset=offset)
        return raster.reshape(height, width, channels).astype(np.float64), ("ppm" if channels == 3 else "pgm")
    if magic in (b"PF", b"Pf"):
        channels = 3 if magic == b"PF" else 1
        scale = float(tokens[3])
        dtype = np.dtype("<f4") if scale < 0 else np.dtype(">f4")
        raster = np.frombuffer(data, dtype=dtype, count=width * height * channels, offset=offset)
        return raster.reshape(height, width, channels).astype(np.float64), "pfm"
    raise ParityError("%s: not a PPM, PGM or PFM file" % path)


def write_netpbm(path, array):
    array = np.asarray(array, dtype=np.uint8)
    channels = 1 if array.ndim == 2 else array.shape[2]
    height, width = array.shape[:2]
    with open(path, "wb") as handle:
        handle.write(("%s\n%d %d\n255\n" % ("P6" if channels == 3 else "P5", width, height)).encode("ascii"))
        handle.write(array.tobytes())


def write_pfm(path, array):
    array = np.asarray(array, dtype="<f4")
    channels = 1 if array.ndim == 2 else array.shape[2]
    height, width = array.shape[:2]
    with open(path, "wb") as handle:
        handle.write(("%s\n%d %d\n-1.0\n" % ("PF" if channels == 3 else "Pf", width, height)).encode("ascii"))
        handle.write(array.tobytes())


# ---------------------------------------------------------------------- ssim

def _kernel():
    x = np.arange(WINDOW, dtype=np.float64) - (WINDOW - 1) / 2.0
    g = np.exp(-(x * x) / (2.0 * SIGMA * SIGMA))
    return g / g.sum()


KERNEL = _kernel()


def _filter_valid(plane):
    height, width = plane.shape
    rows = np.zeros((height, width - WINDOW + 1), dtype=np.float64)
    for i in range(WINDOW):
        rows += KERNEL[i] * plane[:, i:width - WINDOW + 1 + i]
    out = np.zeros((height - WINDOW + 1, width - WINDOW + 1), dtype=np.float64)
    for i in range(WINDOW):
        out += KERNEL[i] * rows[i:height - WINDOW + 1 + i, :]
    return out


def _ssim_terms(mu_a, mu_b, var_a, var_b, cov, c1, c2):
    return ((2 * mu_a * mu_b + c1) * (2 * cov + c2)) / ((mu_a * mu_a + mu_b * mu_b + c1) * (var_a + var_b + c2))


def ssim_plane(a, b, dynamic_range):
    c1 = (0.01 * dynamic_range) ** 2
    c2 = (0.03 * dynamic_range) ** 2
    height, width = a.shape
    if height < WINDOW or width < WINDOW:
        mu_a, mu_b = a.mean(), b.mean()
        var_a, var_b = a.var(), b.var()
        cov = ((a - mu_a) * (b - mu_b)).mean()
        return float(_ssim_terms(mu_a, mu_b, var_a, var_b, cov, c1, c2))
    total = 0.0
    count = 0
    step = 1024
    for top in range(0, height - WINDOW + 1, step):
        bottom = min(height, top + step + WINDOW - 1)
        sa, sb = a[top:bottom], b[top:bottom]
        mu_a, mu_b = _filter_valid(sa), _filter_valid(sb)
        var_a = _filter_valid(sa * sa) - mu_a * mu_a
        var_b = _filter_valid(sb * sb) - mu_b * mu_b
        cov = _filter_valid(sa * sb) - mu_a * mu_b
        values = _ssim_terms(mu_a, mu_b, var_a, var_b, cov, c1, c2)
        total += float(values.sum())
        count += values.size
    return total / count


def compare_arrays(a, b, kind):
    """Returns (ssim, mad, note). ssim 0 and mad nan when the shapes differ."""
    if a.shape != b.shape:
        return 0.0, float("nan"), "shape %s vs %s" % ("x".join(map(str, a.shape)), "x".join(map(str, b.shape)))
    finite_a, finite_b = np.isfinite(a), np.isfinite(b)
    nonfinite_mismatch = int(np.count_nonzero(finite_a != finite_b))
    both = finite_a & finite_b
    a = np.where(both, a, 0.0)
    b = np.where(both, b, 0.0)
    mad = float(np.abs(a - b).mean())
    note = "" if nonfinite_mismatch == 0 else "%d non-finite texels differ" % nonfinite_mismatch
    if np.array_equal(a, b):
        return (1.0 if nonfinite_mismatch == 0 else 0.0), mad, note
    if kind == "ppm":
        luma_a = 0.2126 * a[:, :, 0] + 0.7152 * a[:, :, 1] + 0.0722 * a[:, :, 2]
        luma_b = 0.2126 * b[:, :, 0] + 0.7152 * b[:, :, 1] + 0.0722 * b[:, :, 2]
        value = ssim_plane(luma_a, luma_b, 255.0)
    elif kind == "pgm":
        value = ssim_plane(a[:, :, 0], b[:, :, 0], 255.0)
    else:
        value = 1.0
        for channel in range(a.shape[2]):
            pa, pb = a[:, :, channel], b[:, :, channel]
            span = max(float(pa.max()), float(pb.max())) - min(float(pa.min()), float(pb.min()))
            value = min(value, ssim_plane(pa, pb, max(1.0, span)))
    if nonfinite_mismatch:
        value = 0.0
    return value, mad, note


# ----------------------------------------------------------------- allowlist

def parse_allowlist(path):
    """Returns a list of (pattern, bounds dict)."""
    rows = []
    with open(path, encoding="utf-8") as handle:
        for number, line in enumerate(handle, 1):
            stripped = line.strip()
            if not stripped.startswith("|"):
                continue
            cells = [cell.strip() for cell in stripped.strip("|").split("|")]
            if not cells or cells[0].lower() == "attachment":
                continue
            if all(set(cell) <= set("-: ") for cell in cells):
                continue
            if len(cells) < 3:
                raise ParityError("%s:%d: an allowlist row needs attachment, reason and max accepted deviation" % (path, number))
            pattern = cells[0].strip("`").strip()
            bounds = {}
            for token in cells[2].replace(";", ",").split(","):
                token = token.strip().strip("`").replace(" ", "").lower()
                if not token:
                    continue
                if token in ("any", "missing"):
                    bounds[token] = True
                elif token.startswith("ssim>="):
                    bounds["ssim"] = float(token[6:])
                elif token.startswith("mad<="):
                    bounds["mad"] = float(token[5:])
                else:
                    raise ParityError("%s:%d: unknown deviation bound '%s'" % (path, number, token))
            if not pattern or not bounds:
                raise ParityError("%s:%d: allowlist row without an attachment or a bound" % (path, number))
            rows.append((pattern, bounds))
    return rows


def allowlist_row(rows, name):
    for pattern, bounds in rows:
        if fnmatch.fnmatchcase(name, pattern):
            return bounds
    return None


def within(bounds, ssim, mad):
    if bounds.get("any"):
        return True
    if "ssim" not in bounds and "mad" not in bounds:
        return False
    if "ssim" in bounds and not (ssim >= bounds["ssim"]):
        return False
    if "mad" in bounds and not (not math.isnan(mad) and mad <= bounds["mad"]):
        return False
    return True


# ---------------------------------------------------------------------- main

def list_dump(directory):
    if not os.path.isdir(directory):
        raise ParityError("not a directory: %s" % directory)
    return sorted(name for name in os.listdir(directory) if name.endswith(EXTENSIONS))


def run(dir_a, dir_b, allowlist=None, threshold=0.98, csv_path=None, out=sys.stdout):
    rows = parse_allowlist(allowlist) if allowlist else []
    names_a, names_b = set(list_dump(dir_a)), set(list_dump(dir_b))
    results = []
    failed = 0
    for name in sorted(names_a | names_b):
        bounds = allowlist_row(rows, name)
        if name not in names_a or name not in names_b:
            side = "B" if name in names_a else "A"
            ok = bounds is not None and (bounds.get("missing") or bounds.get("any"))
            status = ("allowlisted: " if ok else "FAIL: ") + "missing in " + side
            results.append((name, float("nan"), float("nan"), status))
            failed += 0 if ok else 1
            continue
        a, kind = read_image(os.path.join(dir_a, name))
        b, kind_b = read_image(os.path.join(dir_b, name))
        if kind != kind_b:
            ssim, mad, note = 0.0, float("nan"), "encoding %s vs %s" % (kind, kind_b)
        else:
            ssim, mad, note = compare_arrays(a, b, kind)
        if ssim >= threshold and not note:
            status = "ok"
        elif bounds is not None and within(bounds, ssim, mad):
            status = "allowlisted"
        else:
            status = "FAIL" if bounds is None else "FAIL: outside allowlist bound"
            failed += 1
        if note:
            status += " (" + note + ")"
        results.append((name, ssim, mad, status))

    out.write("| attachment | ssim | mean abs diff | status |\n")
    out.write("|---|---|---|---|\n")
    for name, ssim, mad, status in results:
        out.write("| %s | %s | %s | %s |\n" % (
            name, "-" if math.isnan(ssim) else "%.4f" % ssim, "-" if math.isnan(mad) else "%.6g" % mad, status))
    out.write("\n%d attachments, %d failed, threshold %.4f\n" % (len(results), failed, threshold))
    if csv_path:
        with open(csv_path, "w", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["attachment", "ssim", "mad", "status"])
            for name, ssim, mad, status in results:
                writer.writerow([name, "" if math.isnan(ssim) else "%.6f" % ssim,
                                 "" if math.isnan(mad) else "%.9g" % mad, status])
    return 1 if failed else 0


def self_test():
    import io

    rng = np.random.default_rng(1234)
    with tempfile.TemporaryDirectory(prefix="optimum-ssim-self-test-") as root:
        a, b = os.path.join(root, "a"), os.path.join(root, "b")
        os.makedirs(a)
        os.makedirs(b)
        scene = rng.integers(0, 256, size=(40, 48, 3), dtype=np.uint8)
        depth = rng.random((33, 29)).astype(np.float32)
        write_netpbm(os.path.join(a, "0-Primary-color0-rgba8.ppm"), scene)
        write_netpbm(os.path.join(b, "0-Primary-color0-rgba8.ppm"), scene)
        write_pfm(os.path.join(a, "0-Primary-depth-depth.pfm"), depth)
        write_pfm(os.path.join(b, "0-Primary-depth-depth.pfm"), depth)

        # 1. identical inputs: 1.0 everywhere, exit 0
        sink = io.StringIO()
        assert run(a, b, out=sink) == 0, sink.getvalue()
        assert "| 0-Primary-color0-rgba8.ppm | 1.0000 | 0 | ok |" in sink.getvalue(), sink.getvalue()
        assert "| 0-Primary-depth-depth.pfm | 1.0000 | 0 | ok |" in sink.getvalue(), sink.getvalue()
        value, mad, _ = compare_arrays(read_image(os.path.join(a, "0-Primary-depth-depth.pfm"))[0],
                                       read_image(os.path.join(b, "0-Primary-depth-depth.pfm"))[0], "pfm")
        assert value == 1.0 and mad == 0.0

        # PFM round trip keeps floats exactly, including HDR values
        hdr = np.array([[[1000.5, -2.25, 0.0], [3.0, 4.0, 5.0]]], dtype=np.float32)
        write_pfm(os.path.join(root, "hdr.pfm"), hdr)
        assert np.array_equal(read_image(os.path.join(root, "hdr.pfm"))[0], hdr.astype(np.float64))

        # 2. noise: below 1 and below the threshold, exit 1
        noisy = np.clip(scene.astype(np.int32) + rng.integers(-60, 61, size=scene.shape), 0, 255).astype(np.uint8)
        write_netpbm(os.path.join(b, "0-Primary-color0-rgba8.ppm"), noisy)
        sink = io.StringIO()
        assert run(a, b, out=sink) == 1, sink.getvalue()
        value, mad, _ = compare_arrays(read_image(os.path.join(a, "0-Primary-color0-rgba8.ppm"))[0],
                                       read_image(os.path.join(b, "0-Primary-color0-rgba8.ppm"))[0], "ppm")
        assert value < 1.0 and mad > 0.0, (value, mad)
        write_netpbm(os.path.join(b, "0-Primary-color0-rgba8.ppm"), scene)

        # 3. a file on one side only fails
        write_netpbm(os.path.join(a, "4-FindBright-color0-rgba16f.pgm"), scene[:, :, 0])
        sink = io.StringIO()
        assert run(a, b, out=sink) == 1, sink.getvalue()
        assert "FAIL: missing in B" in sink.getvalue(), sink.getvalue()
        os.remove(os.path.join(a, "4-FindBright-color0-rgba16f.pgm"))

        # 4. an allowlisted miss passes, and the same miss outside its bound fails
        write_netpbm(os.path.join(b, "0-Primary-color0-rgba8.ppm"), noisy)
        allowlist = os.path.join(root, "allowlist.md")
        with open(allowlist, "w", encoding="utf-8") as handle:
            handle.write("| attachment | reason | max accepted deviation |\n|---|---|---|\n")
            handle.write("| `0-Primary-color0-*.ppm` | self-test noise | ssim>=0.0, mad<=255 |\n")
        sink = io.StringIO()
        assert run(a, b, allowlist=allowlist, out=sink) == 0, sink.getvalue()
        assert "| allowlisted |" in sink.getvalue(), sink.getvalue()
        with open(allowlist, "w", encoding="utf-8") as handle:
            handle.write("| attachment | reason | max accepted deviation |\n|---|---|---|\n")
            handle.write("| 0-Primary-color0-rgba8.ppm | self-test noise | ssim>=0.9999 |\n")
        sink = io.StringIO()
        assert run(a, b, allowlist=allowlist, out=sink) == 1, sink.getvalue()
        assert "outside allowlist bound" in sink.getvalue(), sink.getvalue()

    print("ssim.py self-test: ok")
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dir_a", nargs="?")
    parser.add_argument("dir_b", nargs="?")
    parser.add_argument("--allowlist")
    parser.add_argument("--threshold", type=float, default=0.98)
    parser.add_argument("--csv")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.self_test:
            return self_test()
        if not args.dir_a or not args.dir_b:
            parser.print_usage(sys.stderr)
            return 2
        return run(args.dir_a, args.dir_b, args.allowlist, args.threshold, args.csv)
    except ParityError as error:
        print("ssim.py: %s" % error, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
