#!/usr/bin/env bash
# Tests the post-decompile AssemblyInfo.cs Guid and ComVisible resolution fixup.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

# Assert coupling with both bootstrap scripts
grep -qF 'AssemblyInfo.cs - ensure Guid/ComVisible attributes resolve' "$REPO_ROOT/scripts/bootstrap.sh" || {
  echo "Expected AssemblyInfo.cs fixup comment in scripts/bootstrap.sh" >&2
  exit 1
}
grep -qF 'AssemblyInfo.cs - ensure Guid/ComVisible attributes resolve' "$REPO_ROOT/scripts/bootstrap.ps1" || {
  echo "Expected AssemblyInfo.cs fixup comment in scripts/bootstrap.ps1" >&2
  exit 1
}

# Assert Vintagestory.csproj clean rewrite template contains AnyCPU and ProjectReference
for script in "$REPO_ROOT/scripts/bootstrap.sh" "$REPO_ROOT/scripts/bootstrap.ps1"; do
  grep -qF '<PlatformTarget>AnyCPU</PlatformTarget>' "$script" || {
    echo "Expected <PlatformTarget>AnyCPU</PlatformTarget> in $script" >&2
    exit 1
  }
  grep -qF '<GenerateAssemblyInfo>False</GenerateAssemblyInfo>' "$script" || {
    echo "Expected <GenerateAssemblyInfo>False</GenerateAssemblyInfo> in $script" >&2
    exit 1
  }
done

# 1. Test Bash snippet on an AssemblyInfo file lacking using System.Runtime.InteropServices
mkdir -p "$TEST_ROOT/build/Vintagestory/Properties"
cat > "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs" << 'EOF'
using System.Reflection;

[assembly: AssemblyTitle("Vintage Story Client")]
[assembly: ComVisible(false)]
[assembly: Guid("782e0587-e211-4a27-a5ac-3ecb057860dc")]
EOF

repo_root="$TEST_ROOT"
find "$repo_root/build" -type f -name "AssemblyInfo.cs" 2>/dev/null | while read -r ai; do
  if grep -qE '\[assembly:\s*(System\.Runtime\.InteropServices\.)?(Guid|ComVisible)(Attribute)?\(' "$ai"; then
    if ! grep -qE '^\s*using\s+System\.Runtime\.InteropServices\s*;' "$ai"; then
      sed -i '1s/^/using System.Runtime.InteropServices;\n/' "$ai"
    fi
    perl -pi -e 's/\[assembly:\s*(?:System\.Runtime\.InteropServices\.)?Guid(?:Attribute)?\(/\[assembly: System.Runtime.InteropServices.Guid\(/g' "$ai"
    perl -pi -e 's/\[assembly:\s*(?:System\.Runtime\.InteropServices\.)?ComVisible(?:Attribute)?\(/\[assembly: System.Runtime.InteropServices.ComVisible\(/g' "$ai"
  fi
done

grep -qFx 'using System.Runtime.InteropServices;' "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs" || {
  echo "using System.Runtime.InteropServices; missing after fixup" >&2
  exit 1
}
grep -qF '[assembly: System.Runtime.InteropServices.Guid("782e0587-e211-4a27-a5ac-3ecb057860dc")]' "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs" || {
  echo "Guid attribute not qualified after fixup" >&2
  exit 1
}
grep -qF '[assembly: System.Runtime.InteropServices.ComVisible(false)]' "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs" || {
  echo "ComVisible attribute not qualified after fixup" >&2
  exit 1
}

# 2. Test idempotency (running again should not duplicate using or prepend qualification)
find "$repo_root/build" -type f -name "AssemblyInfo.cs" 2>/dev/null | while read -r ai; do
  if grep -qE '\[assembly:\s*(System\.Runtime\.InteropServices\.)?(Guid|ComVisible)(Attribute)?\(' "$ai"; then
    if ! grep -qE '^\s*using\s+System\.Runtime\.InteropServices\s*;' "$ai"; then
      sed -i '1s/^/using System.Runtime.InteropServices;\n/' "$ai"
    fi
    perl -pi -e 's/\[assembly:\s*(?:System\.Runtime\.InteropServices\.)?Guid(?:Attribute)?\(/\[assembly: System.Runtime.InteropServices.Guid\(/g' "$ai"
    perl -pi -e 's/\[assembly:\s*(?:System\.Runtime\.InteropServices\.)?ComVisible(?:Attribute)?\(/\[assembly: System.Runtime.InteropServices.ComVisible\(/g' "$ai"
  fi
done

count=$(grep -c 'using System.Runtime.InteropServices;' "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs" || true)
if [ "$count" -ne 1 ]; then
  echo "using directive duplicated on second pass (count: $count)" >&2
  exit 1
fi
if grep -qF 'System.Runtime.InteropServices.System.Runtime.InteropServices' "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs"; then
  echo "Attribute qualification duplicated on second pass" >&2
  exit 1
fi

# 3. Test PowerShell snippet if pwsh is present
if command -v pwsh >/dev/null 2>&1; then
  cat > "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs" << 'EOF'
using System.Reflection;

[assembly: AssemblyTitle("Vintage Story Client")]
[assembly: Guid("782e0587-e211-4a27-a5ac-3ecb057860dc")]
EOF

  pwsh -NoProfile -NonInteractive -Command "
    \$ErrorActionPreference = 'Stop'
    . '$REPO_ROOT/scripts/_exec.ps1'
    \$Utf8NoBom = New-Object System.Text.UTF8Encoding(\$false)
    function Update-FileInPlace(\$path, [scriptblock]\$mutator) {
        \$text = [System.IO.File]::ReadAllText(\$path)
        \$new = & \$mutator \$text
        if (\$new -ne \$text) { [System.IO.File]::WriteAllText(\$path, \$new, \$Utf8NoBom) }
    }
    \$repoRoot = '$TEST_ROOT'
    Get-ChildItem -Path (Join-Path \$repoRoot 'build') -Recurse -Filter 'AssemblyInfo.cs' -File | ForEach-Object {
        Update-FileInPlace \$_.FullName {
            param(\$t)
            if (\$t -match '\[assembly:\s*(?:System\.Runtime\.InteropServices\.)?(?:Guid|ComVisible)(?:Attribute)?\(') {
                if (\$t -notmatch '(?m)^\s*using\s+System\.Runtime\.InteropServices\s*;') {
                    \$t = \"using System.Runtime.InteropServices;\`n\" + \$t
                }
                \$t = [regex]::Replace(\$t, '\[assembly:\s*(?:System\.Runtime\.InteropServices\.)?Guid(?:Attribute)?\(', '[assembly: System.Runtime.InteropServices.Guid(')
                \$t = [regex]::Replace(\$t, '\[assembly:\s*(?:System\.Runtime\.InteropServices\.)?ComVisible(?:Attribute)?\(', '[assembly: System.Runtime.InteropServices.ComVisible(')
            }
            \$t
        }
    }
  "
  grep -qFx 'using System.Runtime.InteropServices;' "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs"
  grep -qF '[assembly: System.Runtime.InteropServices.Guid("782e0587-e211-4a27-a5ac-3ecb057860dc")]' "$TEST_ROOT/build/Vintagestory/Properties/AssemblyInfo.cs"
  echo "PowerShell AssemblyInfo fixup test passed."
fi

echo "All AssemblyInfo fixup tests passed."
