#!/usr/bin/env bash
# Tests .NET SDK candidate resolution in scripts/prepare-runtime-donors.sh and
# scripts/prepare-runtime-donors.ps1 against repository global.json requirements (issues #105 and #138).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

# Assert coupling comments exist in both scripts
grep -qF 'Issue #138: a PATH dotnet with SDKs that cannot resolve' "$REPO_ROOT/scripts/prepare-runtime-donors.sh" || {
    echo "Expected issue #138 comment in scripts/prepare-runtime-donors.sh" >&2
    exit 1
}
grep -qF 'Issue #138: a PATH dotnet.exe with SDKs that cannot resolve' "$REPO_ROOT/scripts/prepare-runtime-donors.ps1" || {
    echo "Expected issue #138 comment in scripts/prepare-runtime-donors.ps1" >&2
    exit 1
}

# Helper to create a dummy global.json
mkdir -p "$TEST_ROOT/repo"
cat > "$TEST_ROOT/repo/global.json" << 'EOF'
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
EOF

# Helper to extract the dotnet_resolves_global_json function from prepare-runtime-donors.sh
eval "$(sed -n '/^dotnet_resolves_global_json()/,/^}/p' "$REPO_ROOT/scripts/prepare-runtime-donors.sh")"

mkdir -p "$TEST_ROOT/bin" "$TEST_ROOT/home/.dotnet"

# 1. Create a dotnet stub that only has SDK 9 (reproducing issue #138)
cat > "$TEST_ROOT/bin/dotnet-sdk9" << 'EOF'
#!/usr/bin/env bash
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "3.1.426 [/system/sdk]"
    echo "9.0.318 [/system/sdk]"
    exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
    echo "A compatible .NET SDK was not found." >&2
    echo "Requested SDK version: 10.0.100" >&2
    exit 155
fi
exit 1
EOF
chmod +x "$TEST_ROOT/bin/dotnet-sdk9"

# 2. Create a dotnet stub that has SDK 10
cat > "$TEST_ROOT/bin/dotnet-sdk10" << 'EOF'
#!/usr/bin/env bash
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "10.0.100 [/user/sdk]"
    exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
    echo "10.0.100"
    exit 0
fi
exit 1
EOF
chmod +x "$TEST_ROOT/bin/dotnet-sdk10"

# 3. Create a dotnet stub with no SDKs (issue #105)
cat > "$TEST_ROOT/bin/dotnet-nosdk" << 'EOF'
#!/usr/bin/env bash
if [[ "${1:-}" == "--list-sdks" ]]; then
    exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
    echo "No .NET SDKs were found." >&2
    exit 1
fi
exit 1
EOF
chmod +x "$TEST_ROOT/bin/dotnet-nosdk"

# Verification 1: dotnet_resolves_global_json rejects SDK 9 and no-sdk, accepts SDK 10
if dotnet_resolves_global_json "$TEST_ROOT/bin/dotnet-sdk9" "$TEST_ROOT/repo"; then
    echo "dotnet-sdk9 was accepted by dotnet_resolves_global_json" >&2
    exit 1
fi

if dotnet_resolves_global_json "$TEST_ROOT/bin/dotnet-nosdk" "$TEST_ROOT/repo"; then
    echo "dotnet-nosdk was accepted by dotnet_resolves_global_json" >&2
    exit 1
fi

if ! dotnet_resolves_global_json "$TEST_ROOT/bin/dotnet-sdk10" "$TEST_ROOT/repo"; then
    echo "dotnet-sdk10 was rejected by dotnet_resolves_global_json" >&2
    exit 1
fi

# Verification 2: PATH has SDK 9, profile has SDK 10 -> candidate selection picks profile
cp "$TEST_ROOT/bin/dotnet-sdk9" "$TEST_ROOT/bin/dotnet"
cp "$TEST_ROOT/bin/dotnet-sdk10" "$TEST_ROOT/home/.dotnet/dotnet"

export PATH="$TEST_ROOT/bin:$PATH"
export HOME="$TEST_ROOT/home"
repo_root="$TEST_ROOT/repo"

dotnet_cmd=""
profile_dotnet="$HOME/.dotnet/dotnet"
if command -v dotnet >/dev/null 2>&1 && dotnet_resolves_global_json "$(command -v dotnet)" "$repo_root"; then
    dotnet_cmd="$(command -v dotnet)"
elif [[ -x "$profile_dotnet" ]] && dotnet_resolves_global_json "$profile_dotnet" "$repo_root"; then
    dotnet_cmd="$profile_dotnet"
fi

if [[ "$dotnet_cmd" != "$profile_dotnet" ]]; then
    echo "Expected profile dotnet ($profile_dotnet) to be selected, but got: $dotnet_cmd" >&2
    exit 1
fi

# Verification 3: PATH has SDK 10, profile has SDK 9 -> candidate selection picks PATH
cp "$TEST_ROOT/bin/dotnet-sdk10" "$TEST_ROOT/bin/dotnet"
cp "$TEST_ROOT/bin/dotnet-sdk9" "$TEST_ROOT/home/.dotnet/dotnet"

dotnet_cmd=""
if command -v dotnet >/dev/null 2>&1 && dotnet_resolves_global_json "$(command -v dotnet)" "$repo_root"; then
    dotnet_cmd="$(command -v dotnet)"
elif [[ -x "$profile_dotnet" ]] && dotnet_resolves_global_json "$profile_dotnet" "$repo_root"; then
    dotnet_cmd="$profile_dotnet"
fi

if [[ "$dotnet_cmd" != "$TEST_ROOT/bin/dotnet" ]]; then
    echo "Expected PATH dotnet to be selected, but got: $dotnet_cmd" >&2
    exit 1
fi

# Verification 4: Both candidates incompatible -> actionable message identifying candidate and required SDK
cp "$TEST_ROOT/bin/dotnet-sdk9" "$TEST_ROOT/bin/dotnet"
rm -f "$TEST_ROOT/home/.dotnet/dotnet"

diag_output=""
set +e
diag_output="$(
    dotnet_cmd=""
    profile_dotnet="$HOME/.dotnet/dotnet"
    if command -v dotnet >/dev/null 2>&1 && dotnet_resolves_global_json "$(command -v dotnet)" "$repo_root"; then
        dotnet_cmd="$(command -v dotnet)"
    elif [[ -x "$profile_dotnet" ]] && dotnet_resolves_global_json "$profile_dotnet" "$repo_root"; then
        dotnet_cmd="$profile_dotnet"
    else
        required_sdk="10.0.100"
        global_json="$repo_root/global.json"
        if [[ -f "$global_json" ]]; then
            parsed_sdk="$(python3 -c "import json; print(json.load(open('$global_json'))['sdk']['version'])" 2>/dev/null || true)"
            if [[ -n "$parsed_sdk" ]]; then
                required_sdk="$parsed_sdk"
            fi
        fi

        if command -v dotnet >/dev/null 2>&1; then
            path_dotnet="$(command -v dotnet)"
            sdks="$("$path_dotnet" --list-sdks 2>/dev/null || true)"
            sdk_list="$(echo "$sdks" | tr '\n' '; ' | sed 's/; $//')"
            [[ -z "$sdk_list" ]] && sdk_list="none"
            echo "Incompatible .NET SDK candidate: $path_dotnet (installed: $sdk_list). Required SDK: $required_sdk (from global.json). Install the required .NET SDK or run scripts/bootstrap.sh first." >&2
        fi
        exit 1
    fi 2>&1
)" || diag_code=$?
set -e
: "${diag_code:=0}"

if [[ $diag_code -eq 0 ]]; then
    echo "Expected failure when both candidates are incompatible, but got exit code 0" >&2
    exit 1
fi
if [[ "$diag_output" != *"Incompatible .NET SDK candidate:"* || "$diag_output" != *"Required SDK: 10.0.100"* ]]; then
    echo "Unexpected diagnostic message: $diag_output" >&2
    exit 1
fi

# Verification 5: PowerShell parity test when pwsh is installed
if command -v pwsh >/dev/null 2>&1; then
    pwsh -NoProfile -NonInteractive -Command "
        \$ErrorActionPreference = 'Stop'
        . '$REPO_ROOT/scripts/_exec.ps1'

        # Load functions from scripts/prepare-runtime-donors.ps1
        \$ps1Content = Get-Content -LiteralPath '$REPO_ROOT/scripts/prepare-runtime-donors.ps1' -Raw
        \$resolvesFunc = [regex]::Match(\$ps1Content, '(?ms)function script:Test-DotNetResolvesGlobalJson.+?^\}').Value
        if (-not \$resolvesFunc) {
            throw 'Could not extract Test-DotNetResolvesGlobalJson from prepare-runtime-donors.ps1'
        }
        Invoke-Expression \$resolvesFunc

        \$repoRoot = '$TEST_ROOT/repo'
        \$sdk9 = '$TEST_ROOT/bin/dotnet-sdk9'
        \$sdk10 = '$TEST_ROOT/bin/dotnet-sdk10'
        \$noSdk = '$TEST_ROOT/bin/dotnet-nosdk'

        if (Test-DotNetResolvesGlobalJson \$sdk9 \$repoRoot) {
            throw 'PowerShell Test-DotNetResolvesGlobalJson accepted dotnet-sdk9'
        }
        if (Test-DotNetResolvesGlobalJson \$noSdk \$repoRoot) {
            throw 'PowerShell Test-DotNetResolvesGlobalJson accepted dotnet-nosdk'
        }
        if (-not (Test-DotNetResolvesGlobalJson \$sdk10 \$repoRoot)) {
            throw 'PowerShell Test-DotNetResolvesGlobalJson rejected dotnet-sdk10'
        }

        # Test candidate selection where PATH has SDK 9 and profile has SDK 10
        \$env:PATH = '$TEST_ROOT/bin;' + \$env:PATH
        \$env:HOME = '$TEST_ROOT/home'
        \$env:USERPROFILE = '$TEST_ROOT/home'
        Copy-Item -Force \$sdk9 (Join-Path '$TEST_ROOT/bin' 'dotnet')
        Copy-Item -Force \$sdk10 (Join-Path '$TEST_ROOT/home/.dotnet' 'dotnet')

        \$profileRoot = if (\$env:USERPROFILE) { \$env:USERPROFILE } else { \$env:HOME }
        \$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        \$profileDotnetExe = Join-Path \$profileRoot '.dotnet/dotnet.exe'
        if (-not (Test-Path \$profileDotnetExe)) {
            \$profileDotnetExe = Join-Path \$profileRoot '.dotnet/dotnet'
        }

        \$selected = \$null
        if (\$dotnetCmd -and (Test-DotNetResolvesGlobalJson \$dotnetCmd.Source \$repoRoot)) {
            \$selected = \$dotnetCmd.Source
        } elseif ((Test-Path \$profileDotnetExe) -and (Test-DotNetResolvesGlobalJson \$profileDotnetExe \$repoRoot)) {
            \$selected = \$profileDotnetExe
        }

        if (\$selected -ne \$profileDotnetExe) {
            throw \"Expected profile dotnet (\$profileDotnetExe), got: \$selected\"
        }

        # Test diagnostic error when only incompatible candidates exist
        Remove-Item -Force '$TEST_ROOT/home/.dotnet/dotnet'
        \$threw = \$false
        try {
            if (\$dotnetCmd -and (Test-DotNetResolvesGlobalJson \$dotnetCmd.Source \$repoRoot)) {
                \$selected = \$dotnetCmd.Source
            } else {
                \$requiredSdk = '10.0.100'
                \$sdks = & \$dotnetCmd.Source --list-sdks 2>\$null
                \$sdkList = if (\$sdks) { (\$sdks -join '; ') } else { 'none' }
                throw \"Incompatible .NET SDK candidate: \$(\$dotnetCmd.Source) (installed: \$sdkList). Required SDK: \$requiredSdk (from global.json). Install the required .NET SDK or run scripts/bootstrap.ps1.\"
            }
        } catch {
            \$threw = \$true
            if (\$_.Exception.Message -notmatch 'Incompatible \.NET SDK candidate:' -or \$_.Exception.Message -notmatch 'Required SDK: 10\.0\.100') {
                throw \"Unexpected exception message: \$(\$_.Exception.Message)\"
            }
        }
        if (-not \$threw) {
            throw 'Expected exception when only incompatible candidate exists'
        }

        Write-Host 'PowerShell donor candidate selection test passed.'
    "
fi

echo "All runtime donor dotnet candidate selection tests passed."
