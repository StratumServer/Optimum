#!/usr/bin/env bash
# Tests the pinned official client checks in scripts/bootstrap.sh: an archive is accepted only when its
# size and SHA-256 match vintageStoryPins in forks.json, an unpinned name only warns, and the extracted
# pristine VintagestoryLib.vanilla.dll is checked the same way. It also checks that the forks.json of this
# repository pins every client archive of the version it builds against.
set -euo pipefail

real_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# The helpers are shared by bootstrap.sh, package-linux.sh and package-macos.sh.
helpers="$real_root/scripts/_vanilla_pins.sh"
grep -q 'verify_pinned_archive()' "$helpers" || fail "scripts/_vanilla_pins.sh has no pin helpers"
for script in bootstrap.sh package-linux.sh package-macos.sh; do
  grep -q '_vanilla_pins.sh' "$real_root/scripts/$script" || fail "$script does not use the shared pin helpers"
done

# 1. This repository pins all four client archives and the two key files of its version.
python3 - "$real_root/forks.json" <<'PY' || fail "forks.json does not pin every client archive"
import json, sys
forks = json.load(open(sys.argv[1], encoding="utf-8"))
version = forks["vintageStoryVersion"]
pins = forks["vintageStoryPins"][version]
names = [f"vs_install_win-x64_{version}.exe", f"vs_client_linux-x64_{version}.tar.gz",
         f"vs_client_osx-x64_{version}.tar.gz", f"vs_client_osx-arm64_{version}.tar.gz"]
for name in names:
    pin = pins["archives"][name]
    assert len(pin["sha256"]) == 64 and pin["bytes"] > 100_000_000, name
assert set(pins["extractedFiles"]) == {"VintagestoryAPI.dll", "VintagestoryLib.vanilla.dll"}
PY

# 2. The helpers, against a throwaway source tree with a tiny pinned archive.
repo_root="$tmp/repo"
mkdir -p "$repo_root"
version="1.0.0"
archive="$tmp/vs_client_linux-x64_$version.tar.gz"
printf 'official client bytes' > "$archive"
vanilla_lib_pristine="$tmp/VintagestoryLib.vanilla.dll"
printf 'official library' > "$vanilla_lib_pristine"

# shellcheck disable=SC1091
source "$helpers"

archive_size="$(wc -c < "$archive" | tr -d '[:space:]')"
archive_sha="$(hash_files "$archive" | awk '{print $1}')"
lib_sha="$(hash_files "$vanilla_lib_pristine" | awk '{print $1}')"
cat > "$repo_root/forks.json" <<JSON
{
  "vintageStoryVersion": "$version",
  "vintageStoryPins": {
    "_comment": "ignored",
    "$version": {
      "archives": { "$(basename "$archive")": { "bytes": $archive_size, "sha256": "$archive_sha" } },
      "extractedFiles": { "VintagestoryLib.vanilla.dll": "$lib_sha" }
    }
  }
}
JSON

[[ "$(vanilla_pin archive "$(basename "$archive")")" == "$archive_size $archive_sha" ]] || fail "archive pin lookup"
[[ "$(vanilla_pin extracted VintagestoryLib.vanilla.dll)" == "$lib_sha" ]] || fail "extracted pin lookup"
[[ -z "$(vanilla_pin archive unknown.tar.gz)" ]] || fail "an unpinned name must have no pin"

verify_pinned_archive "$archive" 2>/dev/null || fail "the pinned archive was rejected"

printf 'official client bytes, tampered' > "$tmp/tampered"
if verify_pinned_archive "$tmp/tampered" "$(basename "$archive")" 2>/dev/null; then
  fail "an archive with another size and hash was accepted"
fi

# Same size, different content: only the SHA-256 can tell.
printf 'official client bytez' > "$tmp/same-size"
if verify_pinned_archive "$tmp/same-size" "$(basename "$archive")" 2>/dev/null; then
  fail "an archive with the right size and another hash was accepted"
fi

# A download is checked under its real name, not under the .partial name it has while downloading.
cp "$archive" "$tmp/download.partial"
verify_pinned_archive "$tmp/download.partial" "$(basename "$archive")" 2>/dev/null || fail "a partial file was not checked under its archive name"

# An unpinned name is accepted with a warning, and the warning says why.
warning="$(verify_pinned_archive "$tmp/tampered" other.tar.gz 2>&1 >/dev/null || true)"
[[ "$warning" == *"not pinned"* ]] || fail "an unpinned archive gave no warning"

verify_pinned_extraction 2>/dev/null || fail "the pinned library was rejected"
printf 'patched library' > "$vanilla_lib_pristine"
if verify_pinned_extraction 2>/dev/null; then
  fail "a library that is not the official build was accepted"
fi

# No pins at all (a source tree that predates them): nothing is rejected.
rm "$repo_root/forks.json"
verify_pinned_archive "$tmp/tampered" 2>/dev/null || fail "a tree without pins rejected an archive"
verify_pinned_extraction 2>/dev/null || fail "a tree without pins rejected the library"

echo "vanilla pins: all checks passed"
