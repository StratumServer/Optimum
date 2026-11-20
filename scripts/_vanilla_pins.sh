#!/usr/bin/env bash
# Shared by bootstrap.sh, package-linux.sh and package-macos.sh. Source it after setting repo_root (the source
# tree, which holds forks.json) and version (the Vintage Story version being built against).
#
# The pinned fingerprints of the official client live in forks.json (vintageStoryPins). Nothing here reads a
# Vintage Story installed on this machine.

hash_files() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$@"
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$@"
  else
    echo "sha256sum or shasum is required for runtime donor snapshots." >&2
    exit 1
  fi
}

# Fingerprints of the official client this Optimum release is built against live in forks.json
# (vintageStoryPins). Print "<bytes> <sha256>" for an archive file name, or the sha256 of an extracted file
# (vanilla_pin extracted VintagestoryLib.vanilla.dll). Print nothing when the version is not pinned.
vanilla_pin() {
  # Prints nothing for "not pinned" (no forks.json, no pins for this version or name) and ERROR when the
  # pins exist but cannot be read, for example because python3 is missing or the block is malformed.
  python3 - "$repo_root/forks.json" "$version" "$1" "$2" <<'PY' 2>/dev/null || echo ERROR
import json, os, sys
forks, version, kind, name = sys.argv[1:5]
if not os.path.exists(forks):
    sys.exit(0)
try:
    data = json.load(open(forks, encoding="utf-8"))
    if "vintageStoryPins" not in data or version not in data["vintageStoryPins"]:
        sys.exit(0)
    pins = data["vintageStoryPins"][version]
    section = pins["archives"] if kind == "archive" else pins["extractedFiles"]
    if name not in section:
        sys.exit(0)
    pin = section[name]
    if kind == "archive":
        assert len(pin["sha256"]) == 64 and int(pin["bytes"]) > 0
        print(pin["bytes"], pin["sha256"])
    else:
        assert len(pin) == 64
        print(pin)
except Exception:
    print("ERROR")
PY
}

# Return 0 when the archive is the pinned official one, or when its name is not pinned (with a warning),
# and 1 when its size or SHA-256 differ. Never uses a game installed on this machine.
verify_pinned_archive() {
  local archive="$1" name pin bytes sha size actual
  name="${2:-$(basename "$archive")}"
  pin="$(vanilla_pin archive "$name")"
  if [[ "$pin" == "ERROR" ]]; then
    echo "The pinned checksums in forks.json could not be read (is python3 installed and forks.json valid?), so $name cannot be verified." >&2
    return 1
  fi
  if [[ -z "$pin" ]]; then
    echo "WARNING: $name is not pinned in forks.json, so its contents are not verified." >&2
    return 0
  fi
  read -r bytes sha <<<"$pin"
  size="$(wc -c < "$archive" | tr -d '[:space:]')"
  actual="$(hash_files "$archive" | awk '{print $1}')"
  if [[ "$size" != "$bytes" || "$actual" != "$sha" ]]; then
    echo "$name is not the official Vintage Story $version build this Optimum release was tested with" >&2
    echo "  size $size (expected $bytes), SHA-256 $actual (expected $sha)." >&2
    return 1
  fi
  echo "Verified $name: size and SHA-256 match the pinned official build." >&2
}

# Return 0 when the pristine VintagestoryLib.vanilla.dll is the official build (or is not pinned), else 1.
verify_pinned_extraction() {
  local expected actual
  expected="$(vanilla_pin extracted VintagestoryLib.vanilla.dll)"
  if [[ "$expected" == "ERROR" ]]; then
    echo "The pinned checksums in forks.json could not be read, so the extracted client cannot be verified." >&2
    return 1
  fi
  [[ -n "$expected" ]] || return 0
  actual="$(hash_files "$vanilla_lib_pristine" | awk '{print $1}')"
  if [[ "$actual" != "$expected" ]]; then
    echo "The extracted VintagestoryLib.vanilla.dll is not the official $version build (SHA-256 $actual, expected $expected)." >&2
    return 1
  fi
  echo "Verified the extracted VintagestoryLib.vanilla.dll against the pinned official build." >&2
}
