#!/usr/bin/env python3
"""Generate an inventory of Cairo-backed GUI composition overrides.

Run from the Optimum repository root. This is an inventory only: it never
marks an override as rendered merely because the source declaration exists.
"""

from __future__ import annotations

import csv
import re
from pathlib import Path


ROOTS = {
    "VintagestoryApi": Path("VintagestoryApi"),
    "VSEssentials": Path("VSEssentials"),
    "VSSurvivalMod": Path("VSSurvivalMod"),
}
OUTPUT = Path("tools/Cairo.ParityHarness/gui-fixture-manifest.csv")
TYPE_RE = re.compile(r"\b(?:class|struct|record)\s+(\w+)")
METHOD_RE = re.compile(
    r"\bpublic\s+override\s+(?P<return>[\w.<>,?\[\]]+)\s+"
    r"(?P<name>Compose(?:Text)?Elements)\s*\((?P<parameters>[^)]*)\)",
    re.MULTILINE,
)


def normalize_signature(match: re.Match[str]) -> str:
    parameters = re.sub(r"\s+", " ", match.group("parameters").strip())
    return f"public override {match.group('return')} {match.group('name')}({parameters})"


def main() -> int:
    rows: list[dict[str, str]] = []
    for assembly, root in ROOTS.items():
        for source in sorted(root.rglob("*.cs")):
            if any(part in {"bin", "obj", ".git", "Generated"} for part in source.parts):
                continue
            content = source.read_text(encoding="utf-8-sig")
            types = list(TYPE_RE.finditer(content))
            relative = source.as_posix()
            for method in METHOD_RE.finditer(content):
                parent_type = next((item.group(1) for item in reversed(types) if item.start() < method.start()), "<unknown>")
                rows.append(
                    {
                        "assembly": assembly,
                        "source": relative,
                        "containing_type": parent_type,
                        "signature": normalize_signature(method),
                        "fixture_status": "not-rendered",
                        "required_runtime_state": "unassessed",
                    }
                )

    rows.sort(key=lambda row: (row["assembly"], row["source"], row["containing_type"], row["signature"]))
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT.open("w", newline="", encoding="utf-8") as output:
        writer = csv.DictWriter(output, fieldnames=list(rows[0]), lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)
    print(f"Wrote {len(rows)} GUI override inventory rows to {OUTPUT}.")
    print("All rows remain not-rendered until an executable fixture produces an artifact.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
