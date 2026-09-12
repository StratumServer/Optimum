#!/usr/bin/env bash
# Builds Optimum's NGX shim (see optimum_ngx.h for why it exists).
#
#   build.sh <output directory>
#
# The shim is the only call site NGX will accept: libnvidia-ngx.so.1 resolves
# its caller's module from the return address, and a .NET P/Invoke stub lives in
# anonymous JIT memory, which aborts the process inside the driver.
#
# Degrades, never fails the build: with no C compiler on the host it prints one
# line and exits 0, and the managed side then reports NGX as unavailable
# (NgxShim.Availability). That is the documented behaviour - a machine without
# cc can still build and run Optimum, just without DLSS.
set -u

out="${1:-}"
if [ -z "$out" ]; then
    echo "usage: build.sh <output directory>" >&2
    exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="$here/optimum_ngx.c"

case "$(uname -s)" in
    Darwin) name="libOptimumNgx.dylib" ;;
    MINGW*|MSYS*|CYGWIN*) name="OptimumNgx.dll" ;;
    *) name="libOptimumNgx.so" ;;
esac

cc="${CC:-cc}"
if ! command -v "$cc" >/dev/null 2>&1; then
    echo "optimum-ngx: no C compiler ($cc) on this host; skipping the NGX shim - DLSS will report unavailable."
    exit 0
fi

mkdir -p "$out" || exit 0
target="$out/$name"

# Only relink when the source is newer, so an incremental dotnet build does not
# shell out to the compiler on every invocation.
if [ -f "$target" ] && [ "$target" -nt "$src" ] && [ "$target" -nt "$here/optimum_ngx.h" ]; then
    exit 0
fi

libs=""
[ "$(uname -s)" = "Linux" ] && libs="-ldl"

# shellcheck disable=SC2086
# -fno-optimize-sibling-calls is load-bearing, not tidiness: a tail call pops
# the shim's frame before entering NGX, and NGX then reads the *managed*
# caller's return address and aborts - exactly the failure the shim exists to
# prevent (measured 2026-09-12). The source also stores every forwarded result
# in a volatile local, so this flag is the second of two guards.
if ! "$cc" -std=c99 -O2 -fPIC -shared -fvisibility=hidden \
        -fno-optimize-sibling-calls \
        -Wall -Wextra -Wno-unused-parameter \
        "$src" -o "$target" $libs; then
    echo "optimum-ngx: $cc failed to build the NGX shim; DLSS will report unavailable." >&2
    rm -f "$target"
    exit 0
fi

echo "optimum-ngx: built $target"
exit 0
