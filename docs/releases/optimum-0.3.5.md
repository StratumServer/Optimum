# Optimum 0.3.5

Optimum 0.3.5 continues to target Vintage Story 1.22.5 by default and adds
experimental, opt-in support for building and installing against Vintage
Story 1.22.6, ahead of Anego publishing matching open-source updates for
`vsapi`/`vsessentialsmod`/`vssurvivalmod`.

## New

- `make bootstrap VERSION=1.22.6` (and `install-linux.sh --version 1.22.6`,
  `install-windows.ps1 -Version 1.22.6`, `install-macos.sh` with a
  version-mismatch confirmation) builds and installs Optimum against the real
  1.22.6 client. `patches-1.22.6-bridge/` reconstructs the confirmed 1.22.6
  source-level changes for the three affected fork repos (`vsapi`,
  `vsessentialsmod`, `vssurvivalmod`) on top of the pinned 1.22.5 refs, plus a
  one-line version-constant bump (`vscreativemod` and `Cairo` need no bridge -
  confirmed byte-identical to 1.22.6 at the decompiled level). The
  closed-source engine (`VintagestoryLib`/`Vintagestory`) needs no bridging at
  all: `bootstrap.sh`/`bootstrap.ps1` decompile it fresh from whichever
  client archive `--version`/`-Version` downloads, so it's already
  1.22.6-shaped the moment decompilation finishes.
- `install-linux.sh` prompts for the Vintage Story version interactively when
  a `patches-<version>-bridge/` directory offers an alternate to the pinned
  default, and shows the chosen version in the install summary.
- `install-macos.sh` (which overlays Optimum's compiled DLLs onto an existing
  local Vintage Story install rather than downloading one) now detects a
  version mismatch between that local install and what Optimum was built for,
  and asks for confirmation before installing a mismatched build instead of
  doing so silently.
- `install-windows.ps1` gained a `-Version` parameter, threaded through to
  `bootstrap.ps1` and the version-requirement check that previously only
  accepted the exact version pinned in `forks.json` with no override. Both
  the GUI and the headless path now also auto-detect: without an explicit
  `-Version`, they build against whichever supported version (pinned or
  bridge-patched) the locally installed Vintage Story actually is, matching
  `install-linux.sh`'s existing auto-detection instead of requiring an exact
  match to the pinned default.
- `docs/vintage-story-version-updates.md` documents both ways to handle a new
  Vintage Story release going forward, in order of preference: pin real
  upstream source once Anego publishes it (always try this first), or
  bridge-patch from the compiled client as a temporary stopgap (this
  release's 1.22.6 support).

## Fixes

- `bootstrap.sh`/`bootstrap.ps1` no longer silently reuse a stale
  `.vanilla/win-x64` client cached from a previous `--version`/`-Version`; the
  extracted client's own version marker is now checked before reuse, and a
  mismatch triggers a clean re-extraction.
- `package-linux.sh`/`package-macos.sh` had the same stale-cache bug in their
  own separate per-platform `.vanilla` caches, with a more serious effect:
  packaging for a new version after a prior version's package run silently
  grafted Optimum's patches onto the *old* version's vanilla
  `VintagestoryLib.dll` as the Cecil transplant donor, shipping a client whose
  engine IL didn't match the version its compiled mod-API assemblies expected.
  Both scripts now re-extract on a version-marker mismatch, matching the fix
  already applied to `bootstrap.sh`.
- `check-vanilla-compat.sh` no longer asserts a hardcoded game-version string;
  it now reads the actually-extracted client's own version marker.
- `bootstrap.sh`'s upstream-bridge directory lookup is generic
  (`patches-<version>-bridge/`) instead of hardcoding `1.22.6`, matching a
  new equivalent implementation added to `bootstrap.ps1` (which previously had
  no bridge-patch support at all - a Windows `-Version` build of a bridged
  version would have silently built unpatched, mismatched source).

## Validation

Verified end-to-end on Linux: `make bootstrap VERSION=1.22.6 && dotnet build`
succeeds against the real downloaded 1.22.6 client (95/95 patches applied, 0
build errors, 0 warnings). The packaged `install-linux.sh` output launches,
reaches the login screen with no crash, and completed a real online session
(server validation, joined a public multiplayer server, exchanged chat,
disconnected cleanly) with no exceptions in `client-debug.log` or
`server-debug.log`.

Windows and macOS builds are implemented and syntax-validated
(`pwsh`-parsed, `bash -n`-checked) but **not yet retested on those
platforms** - no Windows or macOS host was available to run them end to end
this session.

1.22.5 continues to build and install exactly as before this release
(`VERSION` defaults to `forks.json`'s pinned value).

## Hotfix

- `install-windows.ps1` silently dropped the real error when the runtime
  donor build (VSEssentials/VSSurvivalMod) failed, showing only a generic
  "Runtime donor build failed" message with no compiler output to diagnose
  it. The install script now streams that output into the log as it
  happens instead of buffering it until after the (failed) call returns,
  so the actual `dotnet build` error is visible. This restores
  diagnosability only; if your runtime donor build fails, please share the
  new log so the underlying cause can be found.
