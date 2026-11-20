#!/usr/bin/env bash
# Tests the post-decompile InlineArray<N> global:: normalization fixup across
# both scripts/bootstrap.sh and scripts/bootstrap.ps1.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

PATTERN='(?<!struct )(?<!global::)\b_003C_003Ey__InlineArray(\d+)<'
REPLACEMENT='global::_003C_003Ey__InlineArray$1<'

# 1. Assert both bootstrap scripts contain the exact normalization rule
grep -qF "$PATTERN" "$REPO_ROOT/scripts/bootstrap.sh" || {
  echo "Expected InlineArray normalization pattern in scripts/bootstrap.sh" >&2
  exit 1
}
grep -qF "$REPLACEMENT" "$REPO_ROOT/scripts/bootstrap.sh" || {
  echo "Expected InlineArray normalization replacement in scripts/bootstrap.sh" >&2
  exit 1
}
grep -qF "$PATTERN" "$REPO_ROOT/scripts/bootstrap.ps1" || {
  echo "Expected InlineArray normalization pattern in scripts/bootstrap.ps1" >&2
  exit 1
}
grep -qF "$REPLACEMENT" "$REPO_ROOT/scripts/bootstrap.ps1" || {
  echo "Expected InlineArray normalization replacement in scripts/bootstrap.ps1" >&2
  exit 1
}

seed_fixtures() {
  local root="$1"
  rm -rf "$root/build"
  mkdir -p "$root/build/VintagestoryLib/Vintagestory.Common"
  mkdir -p "$root/build/VintagestoryLib/Vintagestory.Client.NoObf"
  mkdir -p "$root/build/Vintagestory"

  cat > "$root/build/VintagestoryLib/Vintagestory.Common/ModContainer.cs" << 'EOF'
namespace Vintagestory.Common;

public class ModContainer
{
	private void Load()
	{
		_003C_003Ey__InlineArray6<string> buffer = default(_003C_003Ey__InlineArray6<string>);
		_003C_003Ey__InlineArray5<object> other = default(_003C_003Ey__InlineArray5<object>);
	}
}
EOF

  cat > "$root/build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs" << 'EOF'
namespace Vintagestory.Client.NoObf;

public class ChunkTesselator
{
	private void Build()
	{
		global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
	}
}
EOF

  cat > "$root/build/VintagestoryLib/--y__InlineArray6.cs" << 'EOF'
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Auto)]
[InlineArray(6)]
internal struct _003C_003Ey__InlineArray6<T>
{
	[CompilerGenerated]
	private T _element0;
}
EOF
}

verify_fixtures() {
  local root="$1"
  local label="$2"

  grep -qF 'global::_003C_003Ey__InlineArray6<string> buffer = default(global::_003C_003Ey__InlineArray6<string>);' \
    "$root/build/VintagestoryLib/Vintagestory.Common/ModContainer.cs" || {
    echo "[$label] ModContainer.cs InlineArray6 was not qualified with global::" >&2
    exit 1
  }
  grep -qF 'global::_003C_003Ey__InlineArray5<object> other = default(global::_003C_003Ey__InlineArray5<object>);' \
    "$root/build/VintagestoryLib/Vintagestory.Common/ModContainer.cs" || {
    echo "[$label] ModContainer.cs InlineArray5 was not qualified with global::" >&2
    exit 1
  }
  if grep -qF 'global::global::' "$root/build/VintagestoryLib/Vintagestory.Common/ModContainer.cs" \
    "$root/build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs"; then
    echo "[$label] Double global:: prefix introduced" >&2
    exit 1
  fi
  grep -qFx 'internal struct _003C_003Ey__InlineArray6<T>' \
    "$root/build/VintagestoryLib/--y__InlineArray6.cs" || {
    echo "[$label] InlineArray struct declaration was corrupted" >&2
    exit 1
  }
}

run_bash_fixup() {
  local root="$1"
  local decompiled_dirs=("$root/build/VintagestoryLib" "$root/build/Vintagestory")
  find "${decompiled_dirs[@]}" -name '*.cs' -print0 2>/dev/null | xargs -0 perl -pi -e '
    s/(?<!struct )(?<!global::)\b_003C_003Ey__InlineArray(\d+)</global::_003C_003Ey__InlineArray$1</g;
  '
}

# 2. Test Bash/Perl fixup and idempotency
seed_fixtures "$TEST_ROOT"
run_bash_fixup "$TEST_ROOT"
verify_fixtures "$TEST_ROOT" "bash-pass-1"
run_bash_fixup "$TEST_ROOT"
verify_fixtures "$TEST_ROOT" "bash-pass-2"

# 3. Test PowerShell fixup and idempotency if pwsh is available
if command -v pwsh >/dev/null 2>&1; then
  seed_fixtures "$TEST_ROOT"
  pwsh -NoProfile -NonInteractive -Command "
    \$ErrorActionPreference = 'Stop'
    \$Utf8NoBom = New-Object System.Text.UTF8Encoding(\$false)
    function Update-TreeInPlace {
        param([string[]]\$Roots, [scriptblock]\$Transform)
        foreach (\$root in \$Roots) {
            if (-not (Test-Path \$root)) { continue }
            Get-ChildItem -Path \$root -Recurse -Filter '*.cs' -File | ForEach-Object {
                \$text = [System.IO.File]::ReadAllText(\$_.FullName)
                \$new = & \$Transform \$text
                if (\$new -ne \$text) { [System.IO.File]::WriteAllText(\$_.FullName, \$new, \$Utf8NoBom) }
            }
        }
    }
    \$decompiledDirs = @('$TEST_ROOT/build/VintagestoryLib', '$TEST_ROOT/build/Vintagestory')
    for (\$pass = 0; \$pass -lt 2; \$pass++) {
        Update-TreeInPlace \$decompiledDirs {
            param(\$t) [regex]::Replace(\$t, '(?<!struct )(?<!global::)\b_003C_003Ey__InlineArray(\d+)<', 'global::_003C_003Ey__InlineArray\$1<')
        }
    }
  "
  verify_fixtures "$TEST_ROOT" "pwsh"
  echo "PowerShell InlineArray fixup test passed."
fi

echo "All InlineArray fixup tests passed."
