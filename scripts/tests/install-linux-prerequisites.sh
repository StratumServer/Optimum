#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

export HOME="$TEST_ROOT/home"
export PATH="$TEST_ROOT/bin:/usr/bin:/bin"
mkdir -p "$HOME/.dotnet/tools" "$TEST_ROOT/bin"

cat > "$TEST_ROOT/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "9.0.100 [/system/sdk]"
    exit 0
fi
exit 1
EOF
chmod +x "$TEST_ROOT/bin/dotnet"

cat > "$HOME/.dotnet/dotnet" <<'EOF'
#!/usr/bin/env bash
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "10.0.100 [/user/sdk]"
    exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
    echo "10.0.100"
    exit 0
fi
if [[ "${1:-}" == "tool" ]]; then
    printf '%s\n' "$*" >> "$HOME/dotnet-tool.log"
    cat > "$HOME/.dotnet/tools/ilspycmd" <<'TOOL'
#!/usr/bin/env bash
echo "ilspycmd: 10.1.1.8388"
TOOL
    chmod +x "$HOME/.dotnet/tools/ilspycmd"
    exit 0
fi
exit 1
EOF
chmod +x "$HOME/.dotnet/dotnet"
cp "$HOME/.dotnet/dotnet" "$TEST_ROOT/user-dotnet"

source "$REPO_ROOT/scripts/install-linux.sh"

rm -f "$HOME/.dotnet/dotnet" "$HOME/.dotnet/tools/ilspycmd"
hash -r
install_dotnet10() {
    cp "$TEST_ROOT/user-dotnet" "$HOME/.dotnet/dotnet"
    chmod +x "$HOME/.dotnet/dotnet"
    activate_user_dotnet
    check_dotnet10
}
detect_prereqs
[[ "$(get_missing_prereqs)" == "dotnet ilspycmd" ]]
offer_install_missing <<< "Y"
[[ -z "$(get_missing_prereqs)" ]]

check_dotnet10
[[ "$DOTNET_BIN" == "$HOME/.dotnet/dotnet" ]]

ilspycmd_version_supported "10.1.0.8386"
ilspycmd_version_supported "10.1.0.8387"
ilspycmd_version_supported "10.1.1.0"
ilspycmd_version_supported "10.1.1.8387"
ilspycmd_version_supported "10.1.1.8388"
if ilspycmd_version_supported "10.1.0.8385"; then
    echo "version below the ilspycmd range passed validation" >&2
    exit 1
fi
if ilspycmd_version_supported "10.1.1.8389"; then
    echo "unvalidated ilspycmd revision passed validation" >&2
    exit 1
fi
if ilspycmd_version_supported "10.1.2.9000"; then
    echo "future ilspycmd version passed validation" >&2
    exit 1
fi
if ilspycmd_version_supported "10.0.1.8346"; then
    echo "old ilspycmd line passed validation" >&2
    exit 1
fi
if ilspycmd_version_supported "10.2.0.1"; then
    echo "unsupported ilspycmd version passed validation" >&2
    exit 1
fi
if ilspycmd_version_supported "10.0.0.8323-preview3"; then
    echo "preview ilspycmd version passed validation" >&2
    exit 1
fi
if ilspycmd_version_supported "10.1.1.8388-rc1"; then
    echo "prerelease ilspycmd version passed validation" >&2
    exit 1
fi

cat > "$HOME/.dotnet/tools/ilspycmd" <<'EOF'
#!/usr/bin/env bash
echo "ilspycmd: 10.1.0.8386"
EOF
chmod +x "$HOME/.dotnet/tools/ilspycmd"
export PATH="$HOME/.dotnet/tools:$PATH"
hash -r
check_ilspycmd

rm -f "$HOME/.dotnet/tools/ilspycmd" "$HOME/dotnet-tool.log"
hash -r
install_ilspycmd
grep -Fx "tool update -g ilspycmd --version 10.1.1.8388 --allow-downgrade" "$HOME/dotnet-tool.log" >/dev/null
check_ilspycmd

echo "Linux prerequisite tests passed."
