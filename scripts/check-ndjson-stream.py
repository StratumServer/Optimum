#!/usr/bin/env python3
"""Validate an Optimum engine NDJSON stream on stdin against INSTALLER-PLAN.md
section 4. Exits non-zero with a message on the first violation. The C# twin is
Optimum.Cli.Tests/NdjsonStream.cs; keep them in step."""
import json
import sys

KNOWN_TYPES = {"progress", "log", "result"}
KNOWN_PHASES = {"decompile", "patch", "verify", "assemble"}
KNOWN_LEVELS = {"info", "warn", "error"}
KNOWN_REASONS = {
    "bad-input", "unsupported-version", "patch-conflict", "decompile-failed",
    "assemble-failed", "verification-failed", "output-exists", "cancelled",
    "engine-internal",
}


def fail(message):
    print(f"NDJSON contract violation: {message}", file=sys.stderr)
    sys.exit(1)


def main():
    lines = [line for line in sys.stdin.read().split("\n") if line]
    if not lines:
        fail("the stream is empty")

    last_progress = 0
    result_count = 0
    for index, raw in enumerate(lines):
        try:
            obj = json.loads(raw)
        except json.JSONDecodeError as error:
            fail(f"line {index + 1} is not JSON ({error}): {raw!r}")
        if not isinstance(obj, dict):
            fail(f"line {index + 1} is not an object")

        kind = obj.get("type")
        if kind not in KNOWN_TYPES:
            fail(f"line {index + 1} has unknown type {kind!r}")

        if kind == "progress":
            if obj.get("phase") not in KNOWN_PHASES:
                fail(f"line {index + 1} has unknown phase {obj.get('phase')!r}")
            progress = obj.get("progress")
            if not isinstance(progress, int) or not (last_progress <= progress <= 99):
                fail(f"line {index + 1} progress {progress} is out of range or decreased from {last_progress}")
            last_progress = progress
        elif kind == "log":
            if obj.get("level") not in KNOWN_LEVELS:
                fail(f"line {index + 1} has unknown level {obj.get('level')!r}")
        elif kind == "result":
            result_count += 1
            if index != len(lines) - 1:
                fail("the result line is not the last line")
            if obj.get("ok") is True:
                if not obj.get("runtimePath"):
                    fail("an ok result has no runtimePath")
            elif obj.get("ok") is False:
                if obj.get("reason") not in KNOWN_REASONS:
                    fail(f"a failed result has unknown reason {obj.get('reason')!r}")
                if not obj.get("message"):
                    fail("a failed result has no message")
            else:
                fail("a result line has no boolean ok field")

    if result_count != 1:
        fail(f"expected exactly one result line, saw {result_count}")

    print(f"NDJSON stream ok: {len(lines)} lines, terminal result present")


if __name__ == "__main__":
    main()
