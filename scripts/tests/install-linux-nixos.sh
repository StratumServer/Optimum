#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

export HOME="$TEST_ROOT/home"
export PATH="$TEST_ROOT/bin:/usr/bin:/bin"
mkdir -p "$HOME" "$TEST_ROOT/bin"

# Shadow any host dotnet with an SDK-9 stub so check_dotnet10 reports the .NET
# 10 prerequisite as missing regardless of the build machine.
cat > "$TEST_ROOT/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "9.0.100 [/system/sdk]"
    exit 0
fi
exit 1
EOF
chmod +x "$TEST_ROOT/bin/dotnet"

# A non-existent override suppresses the default candidate list (which on a
# dev host would otherwise reach a real system .NET 10 SDK).
export OPTIMUM_DOTNET_CANDIDATES="$TEST_ROOT/absent/dotnet"

source "$REPO_ROOT/scripts/install-linux.sh"

# Without any Nix signal the host is not treated as NixOS.
unset NIX_STORE
if detect_nixos; then
    echo "detect_nixos returned true without NIX_STORE or /etc/NIXOS" >&2
    exit 1
fi

# With NIX_STORE set the installer treats the host as NixOS.
export NIX_STORE="$TEST_ROOT/nix/store"
if ! detect_nixos; then
    echo "detect_nixos returned false with NIX_STORE set" >&2
    exit 1
fi

[[ "$(nixos_dotnet_install_cmd)" == *"nixpkgs"* ]]
[[ "$(nixos_dotnet_install_cmd)" == *"dotnet-sdk_10"* ]]

detect_prereqs
[[ "${PREREQ_STATUS[dotnet]}" == "missing" ]]
[[ "${PREREQ_LABEL[dotnet]}" == *"nixpkgs"* ]]
[[ "${PREREQ_INSTALL_CMD[dotnet]}" == "nix profile install nixpkgs#dotnet-sdk_10" ]]

# install_dotnet10 must refuse to run the glibc dotnet-install.sh on NixOS.
if ( install_dotnet10 ) 2>/dev/null; then
    echo "install_dotnet10 succeeded on NixOS; expected it to refuse" >&2
    exit 1
fi

echo "Linux NixOS prerequisite tests passed."
