#!/usr/bin/env python3
"""Still-frame luminance diff, the acceptance measurement from
.claude/skills/vulkan-parity-debug/SKILL.md section 2c.

Two screenshots of a STILL camera one second apart, mean absolute luminance
difference over the centre 60% crop. Repeat for ~7 pairs per backend and compare
the medians, never a single pair. Reference numbers from the TAA round:
Vulkan 1.84 vs OpenGL 1.87 (medians 1.74 / 1.72). Above ~3 on one backend only is
a real bug; equal-but-high on both means the scene is moving (wind, water,
temporal storm), so still the wind, clear the weather and use creative mode.

Usage:
  scripts/dev/luma-diff.py a.png b.png              one pair
  scripts/dev/luma-diff.py --median shots/*.png     consecutive pairs, prints the median
"""
import sys
import statistics

from PIL import Image
import numpy as np


def luma(path):
    image = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64)
    return 0.2126 * image[:, :, 0] + 0.7152 * image[:, :, 1] + 0.0722 * image[:, :, 2]


def centre_crop(plane, fraction=0.6):
    height, width = plane.shape
    ch, cw = int(height * fraction), int(width * fraction)
    top, left = (height - ch) // 2, (width - cw) // 2
    return plane[top:top + ch, left:left + cw]


def pair_diff(first, second):
    a, b = centre_crop(luma(first)), centre_crop(luma(second))
    if a.shape != b.shape:
        raise SystemExit("size mismatch: %s vs %s" % (first, second))
    return float(np.abs(a - b).mean())


def main(argv):
    args = argv[1:]
    want_median = "--median" in args
    files = [a for a in args if a != "--median"]
    if len(files) < 2:
        raise SystemExit(__doc__)
    diffs = []
    for first, second in zip(files, files[1:]) if want_median else [(files[0], files[1])]:
        value = pair_diff(first, second)
        diffs.append(value)
        print("%-40s %-40s %.3f" % (first, second, value))
    if want_median and diffs:
        print("median %.3f over %d pairs" % (statistics.median(diffs), len(diffs)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
