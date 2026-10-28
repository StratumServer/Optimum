#!/usr/bin/env bash
# Unit tests and integrity checks for release checksums and VirusTotal reports.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

# 1. Assert release-installer workflow couples to release-checksums.js
grep -qF 'scripts/release-checksums.js' "$REPO_ROOT/.github/workflows/release-installer.yml" || {
  echo "Expected release-checksums.js invocation in .github/workflows/release-installer.yml" >&2
  exit 1
}

# 2. Run Node.js unit tests
node "$SCRIPT_DIR/release-checksums-test.js"
