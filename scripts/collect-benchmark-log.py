#!/usr/bin/env python3
"""Copy server output and timestamp benchmark markers with a monotonic clock."""

import re
import sys
import time


MARKERS = (
    ("spawn_start_ns", re.compile(r"Loading \d+x\d+x\d+ spawn chunks\.\.\.")),
    ("spawn_end_ns", re.compile(r"Entering runphase RunGame")),
    ("pregen_start_ns", re.compile(r"Ok, added .* columns")),
    ("pregen_end_ns", re.compile(r"Ok, \d+ columns, generated!")),
)


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} LOG_PATH MARKERS_PATH", file=sys.stderr)
        return 64

    log_path, markers_path = sys.argv[1:]
    seen = set()
    with open(log_path, "w", buffering=1) as log, open(
        markers_path, "w", buffering=1
    ) as markers:
        for line in sys.stdin:
            log.write(line)
            for name, pattern in MARKERS:
                if name not in seen and pattern.search(line):
                    markers.write(f"{name}={time.monotonic_ns()}\n")
                    seen.add(name)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
