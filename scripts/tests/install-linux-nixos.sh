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

# --- Interpreter check ---
# On the default glibc host a downloaded SDK would run.
unset OPTIMUM_GLIBC_INTERPRETER
if ! downloaded_dotnet_runnable; then
    echo "downloaded_dotnet_runnable returned false on the default glibc host" >&2
    exit 1
fi

# Simulate a non-FHS host: override the interpreter to a missing path.
export OPTIMUM_GLIBC_INTERPRETER="$TEST_ROOT/missing-ld-linux"
[[ "$(glibc_interpreter_path)" == "$TEST_ROOT/missing-ld-linux" ]]
if downloaded_dotnet_runnable; then
    echo "downloaded_dotnet_runnable returned true with a missing interpreter" >&2
    exit 1
fi

# --- NixOS detection ---
unset NIX_STORE
if detect_nixos; then
    echo "detect_nixos returned true without NIX_STORE or /etc/NIXOS" >&2
    exit 1
fi
export NIX_STORE="$TEST_ROOT/nix/store"
detect_nixos || { echo "detect_nixos returned false with NIX_STORE set" >&2; exit 1; }

[[ "$(nixos_dotnet_install_cmd)" == *"nixpkgs"* ]]
[[ "$(nixos_dotnet_install_cmd)" == *"dotnet-sdk_10"* ]]

# --- NixOS prerequisite routing ---
detect_prereqs
[[ "${PREREQ_STATUS[dotnet]}" == "missing" ]]
[[ "${PREREQ_LABEL[dotnet]}" == *"nixpkgs"* ]]
[[ "${PREREQ_INSTALL_CMD[dotnet]}" == "nix profile install nixpkgs#dotnet-sdk_10" ]]

# install_dotnet10 must refuse the glibc installer on NixOS.
if ( install_dotnet10 ) 2>/dev/null; then
    echo "install_dotnet10 succeeded on NixOS; expected it to refuse" >&2
    exit 1
fi

# --- non-NixOS, non-FHS routing ---
unset NIX_STORE
detect_prereqs
[[ "${PREREQ_STATUS[dotnet]}" == "missing" ]]
[[ "${PREREQ_LABEL[dotnet]}" == *"non-FHS"* ]]
[[ -z "${PREREQ_INSTALL_CMD[dotnet]}" ]]

# install_dotnet10 must refuse the glibc installer when the interpreter is
# missing even outside NixOS.
if ( install_dotnet10 ) 2>/dev/null; then
    echo "install_dotnet10 succeeded without a glibc interpreter; expected it to refuse" >&2
    exit 1
fi

echo "Linux NixOS and non-FHS prerequisite tests passed."
