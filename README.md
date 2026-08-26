<div align="center">
  <img src="logo.svg" width="128" height="128" alt="Optimum"/>
  <h1>Optimum</h1>
</div>

[![License](https://img.shields.io/badge/license-composite-blue)](LICENSE)
[![VS Version](https://img.shields.io/badge/Vintage%20Story-1.22.7-green)](https://www.vintagestory.at)
[![Stars](https://img.shields.io/github/stars/StratumServer/Optimum?logo=github&style=flat)](https://github.com/StratumServer/Optimum/stargazers)

Optimum is a high-performance, client-side fork of [Vintage Story](https://www.vintagestory.at).

## Features

- Background FPS limiter (30 FPS when alt-tabbed)
- Precise frame pacing (hybrid sleep/yield/spin, fixes stutter)
- Entity shadow distance culling (skip draws beyond 80 blocks)
- Shadow far vegetation skip (skip foliage in far cascade)
- Entity render distance pre-cull (skip render before matrix work)
- Dynamic light radius scaling (35-60 blocks based on view distance)
- Chiseled block LOD (solid cube beyond threshold, 83x vertex reduction)
- Entity repulsion distance gate (skip physics beyond 64 blocks)
- Weather wind throttle (cache lookups for 4 frames)
- Particle distance gate (skip emitters beyond 48 blocks)
- FSR 1.0 upscaling (EASU + RCAS; Quality/Balanced/Performance render scale)
- Smart chunk culling (scale the occlusion-culling threshold with view distance)
- Map page cache (8x8-chunk GPU texture array, single draw call, BC7, disk-persisted)
- Dynamic light scan reuse (reuse the previous frame's scan while standing still)
- Entity light batching (group visible entity light samples per chunk)
- Entity shader state cache (share view/water uniforms and animation UBO per pass)
- Ambient sound position throttling (skip updates when stationary)
- Fly sound volume deduplication (skip updates below 1% change)
- Name-tag frustum reuse (IsRendered flag instead of recomputing)
- Animation check reorder (distance before frustum)
- Lock contention reduction (10 locks to System.Threading.Lock)
- BlockPos reuse in particle ticks (99.9% GC reduction in that path)
- Mouse wheel fix at low sensitivity (#9710)
- Creative search cache crash containment (a mod exception no longer kills the client)

Some optimizations in this repository do not yet reach the shipped game. See
[`docs/patch-shipping-audit-0.3.0.md`](docs/patch-shipping-audit-0.3.0.md) for the
per-patch shipping status.

See [`docs/releases/optimum-0.3.5.md`](docs/releases/optimum-0.3.5.md) for the
release fixes and validation record.

## Getting Started

Optimum compiles from source because Vintage Story is proprietary. The first build downloads the official client (~570MB) and decompiles it. Subsequent builds reuse the cache.

### Linux

**Interactive installer** (guided, checks and installs prerequisites):

```bash
git clone https://github.com/StratumServer/Optimum.git
cd Optimum
./scripts/install-linux.sh
```

The installer shows a ✓/✗ checklist of required tools, offers to install anything missing, asks where to install (default: `~/.local/share/optimum`), and creates a menu entry. Run the game from the menu or with `~/.local/share/optimum/optimum-launch.sh`.

**AppImage** (single portable executable, no install):

```bash
git clone https://github.com/StratumServer/Optimum.git
cd Optimum
make package-appimage
chmod +x Optimum-v0.3.13-linux-x64.AppImage
./Optimum-v0.3.13-linux-x64.AppImage
```

If `appimagetool` is missing, the script downloads it (14MB, once) into `.tools/`.

**Manual build** (for development or full control):

```bash
git clone https://github.com/StratumServer/Optimum.git
cd Optimum
make check    # report which tools are installed (installs nothing)
make build    # bootstrap + build
make run      # build, deploy, and launch client
```

Requires .NET 10 SDK, bash, python3, git, curl, perl.

**NixOS** (non-FHS distribution):

The interactive installer detects NixOS and routes the .NET 10 SDK prerequisite
through nixpkgs, since the SDK from dot.net is a glibc build that cannot run
there:

```sh
nix profile install nixpkgs#dotnet-sdk_10
```

The packaged launcher and game are glibc binaries as well, so run the AppImage
through `appimage-run` with the runtime dependencies exposed. In the NixOS
configuration:

```nix
programs.appimage.enable = true;
programs.appimage.binfmt = true;
programs.appimage.package = pkgs.appimage-run.override {
  extraPkgs = pkgs: [
    pkgs.dotnet-runtime
    pkgs.openal
    pkgs.gtk3
  ];
};
```

Then run the `.AppImage` normally. `dotnet-runtime` exposes the .NET runtime
for the launcher, and `openal` and `gtk3` cover the game's audio and UI native
libraries. Add or remove entries if your build needs a different native set.

### Windows

**GUI installer** (checks prerequisites, offers downloads, choose install folder):

```powershell
git clone https://github.com/StratumServer/Optimum.git
cd Optimum
.\install-windows.cmd
```

The installer detects .NET 10 SDK, Git, ilspycmd, and a local Vintage Story install. Missing tools show with a "Download" checkbox that opens the install page. Choose the install directory, click Install. Done.

**Manual build** (PowerShell):

```powershell
.\scripts\bootstrap.ps1                        # download, decompile, clone forks, patch
dotnet build VintageStory.slnx -c Release      # compile optimized DLLs
.\scripts\package.ps1                          # build Optimum-v0.3.13-win-x64/ folder
.\scripts\package.ps1 -Zip                     # folder + portable zip
```

Requires .NET 10 SDK, Git for Windows, and PowerShell 5.1+.

### macOS

```bash
git clone https://github.com/StratumServer/Optimum.git
cd Optimum
make build
./scripts/package-macos.sh --arch arm64        # Apple Silicon .dmg
./scripts/package-macos.sh --arch x64          # Intel .dmg
```

Open the .dmg and drag Optimum.app to Applications. Requires .NET 10 SDK, bash, python3, git, curl, perl.

## Settings

Optimum persists its runtime settings to `ModConfig/optimum.json` inside your
Vintage Story data path. The file is created with defaults on first run.
The `.optimum/` directory under the same data path holds launcher state
(donor assemblies, the patched-assembly cache, and the shader compatibility
report), not user settings.

The data path depends on the platform:

| Platform | Data path |
|---|---|
| Windows | `%APPDATA%\VintagestoryData` |
| Linux | `~/.config/VintagestoryData` |
| macOS | `~/Library/Application Support/VintagestoryData` |

Older macOS installs may still have `~/.config/VintagestoryData`; the game
moves that folder to the new location on first run.

The client reads the file once at startup, so a full restart is required
after editing it. When troubleshooting world-generation problems, the four
relevant keys are `ChunkReadPoolEnabled`, `ChunkReadPoolWorkers`,
`ChunkDeserializeParallel`, and `ChunkDeserializeParallelMinY`.

## Build

### Targeting a Vintage Story version

`forks.json`'s `vintageStoryVersion` picks the default; override per build
with `VERSION`:

```bash
make bootstrap VERSION=1.22.7     # official source throughout (default)
make bootstrap VERSION=1.22.6     # decompiles 1.22.6 engine, forks from 1.22.7 source (compatible)
make bootstrap VERSION=1.22.5     # decompiles 1.22.5 engine, forks from 1.22.7 source (compatible)
dotnet build VintageStory.slnx -c Release
```

When Anego ships a new client version, see
[`docs/vintage-story-version-updates.md`](docs/vintage-story-version-updates.md)
for the two ways to target it - bumping `forks.json` to real upstream source
(preferred, always try this first) versus a temporary bridge-patch
reconstruction from the compiled client (stopgap, only when upstream source
isn't public yet).

### Packaging for distribution

The managed patches use platform-agnostic IL. Each package obtains the official
client for the target platform, keeps the official files intact, and ships the
Optimum launcher, Cecil patcher, runtime donors and optimized shaders. The
launcher patches selected assembly copies at startup. A patch reaches players
only when a Cecil target, an API rule or a runtime donor manifest owns it.

The complete 0.3.0 shipping inventory appears in
[`docs/patch-shipping-audit-0.3.0.md`](docs/patch-shipping-audit-0.3.0.md).

```bash
make package              # all targets this host can produce
make package-linux        # tar.gz
make package-appimage     # single .AppImage executable
make package-macos        # .dmg (ARCH=arm64 or x64)
make package-win          # Windows zip (native Windows or off-platform with innoextract >= 1.11)
```

Or call the scripts directly:

```bash
./scripts/package-linux.sh                     # Optimum-v0.3.13-linux-x64.tar.gz
./scripts/package-linux.sh --format zip
./scripts/package-linux.sh --format appimage   # Optimum-v0.3.13-linux-x64.AppImage
./scripts/package-macos.sh --arch arm64        # Apple Silicon .dmg
./scripts/package-macos.sh --arch x64          # Intel .dmg
./scripts/package-all.sh                       # all capable targets at once
./scripts/package-all.sh --targets linux-x64,osx-arm64
```

The Linux script renames the launcher to Optimum, repoints run.sh, swaps the window icon, and brands the .desktop entry. The macOS script assembles Optimum.app (renamed launcher, Icon.icns from the logo, rebranded Info.plist) and builds a drag-to-Applications .dmg. Off-Windows Windows packaging downloads the official `vs_install_win-x64_<version>.exe` into `.vanilla/archives/` and extracts it with `innoextract` 1.11 or newer when no matching `.vanilla/win-x64/package-client` cache exists. A matching package cache is reused without the extractor, and a fresh extraction leaves the bootstrap/decompile cache at `.vanilla/win-x64/vintagestory` intact for `make run` and `make patch-il`. Pass `-ClientArchive` to supply the installer when no matching package cache exists.

### Host prerequisites for packaging

Beyond the build requirements (.NET 10 SDK, bash, git, curl, perl), packaging needs:

| Tool | What it does | Install |
|---|---|---|
| `appimagetool` | Builds .AppImage (downloaded to .tools/ on first use) | auto or `sudo apt install appimagetool` |
| `pwsh` | Windows packaging off-platform (win-x64 target only) | [Install PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell) |
| `innoextract` >= 1.11 | Extracts the official Inno Setup 6.4.3 Windows client when no matching package cache exists | [Current releases](https://github.com/crazy-max/innoextract/releases) (distro 1.9 is too old) |
| Windows interoperability (`wslpath` + Windows PowerShell) | Runs the official Inno 6.4.3 installer into a disposable directory when bootstrapping win-x64 from WSL | included with WSL |
| `mkisofs` / `genisoimage` | Creates hybrid HFS image for .dmg on Linux | `sudo apt install cdrtools` or `genisoimage` |
| `cmake` + `git` | Build libdmg-hfsplus (compiled once into .tools/) | `sudo apt install cmake git` |

Linux and macOS packaging runs with bash. No PowerShell required for those targets.

### Host x target matrix

| Produce ↓ \ on → | Linux host | macOS host | Windows host |
|---|---|---|---|
| **linux-x64** | ✅ tar.gz / AppImage | ✅ tar.gz | ✅ tar.gz |
| **osx-x64 / osx-arm64** | ✅ unsigned .dmg | ✅ signed .dmg (hdiutil) | ⚠️ .tar.gz fallback |
| **win-x64** | ✅ pwsh + innoextract >= 1.11, or package-client cache | ✅ pwsh + innoextract >= 1.11, or package-client cache | ✅ native |

The .dmg files built on Linux are unsigned. macOS Gatekeeper shows a warning on first open; users right-click > Open to accept. For a notarizable .dmg, build on macOS with an Apple Developer certificate.

**ARM note.** Vintage Story ships native ARM clients only for macOS (`osx-arm64`). Linux and Windows have no native ARM client. Those packages are x64-only; ARM hardware runs them via emulation ([box64](https://github.com/ptitSeb/box64) on Linux, Windows-on-ARM x64 emulation).

## How It Works

Optimum decompiles the official Vintage Story client locally and compiles those sources only as patch donors. It then transplants the tracked changes into the exact official assemblies and writes matching PDBs from the same official DLL/PDB pairs. Your vanilla client or official archive provides the proprietary runtime and assets; no game binaries or symbols are stored in this repository or produced by GitHub CI.

No Harmony overhead. The launcher caches the patched assembly copies for later
launches. The game runs native compiled IL.

## Acknowledgments

Optimum drew on published techniques from these projects. No source code from any of them appears in this repository; all patches are original implementations against the decompiled vanilla baseline.

- [Stratum](https://github.com/StratumServer/Stratum) (MIT) by imtsubaki(tsu), tehtelev, & contributors - multithreaded worldgen architecture, Anego version manifest for downloads, decompile-patch-recompile build model.

## License

Optimum is a composite work. Original Optimum tools and project files in [LICENSE-SCOPE.md](LICENSE-SCOPE.md) use the [MIT License](LICENSE-MIT). Patch files, source overlays, and other paths outside that scope retain the historical terms and applicable upstream notices.

Vintage Story and the Anego-derived material remain subject to their upstream terms. See [LICENSE](LICENSE), [NOTICE](NOTICE), and the preserved historical license in [LICENSE-OPTIMUM-LEGACY-GPL-COMMONS](LICENSE-OPTIMUM-LEGACY-GPL-COMMONS).
