<div align="center">
  <img src="logo.svg" width="128" height="128" alt="Optimum"/>
  <h1>Optimum</h1>
</div>

[![License](https://img.shields.io/badge/license-GPL--3.0%20%2B%20Commons%20Clause-blue)](LICENSE)
[![VS Version](https://img.shields.io/badge/Vintage%20Story-1.22.3-green)](https://www.vintagestory.at)
[![Stars](https://img.shields.io/github/stars/Zaldaryon/Optimum?logo=github&style=flat)](https://github.com/Zaldaryon/Optimum/stargazers)

Optimum is a high-performance, client-side fork of [Vintage Story](https://www.vintagestory.at).

## Features

- Background FPS limiter (30 FPS when alt-tabbed)
- Precise frame pacing (hybrid sleep/yield/spin, fixes stutter)
- Entity shadow distance culling (skip draws beyond 80 blocks)
- Shadow far vegetation skip (skip foliage in far cascade)
- Entity render distance pre-cull (skip render before matrix work)
- Dynamic light radius scaling (35-60 blocks based on view distance)
- Animated block LOD (3-tier distance scaling for forges, querns, etc.)
- Chiseled block LOD (solid cube beyond threshold, 83x vertex reduction)
- Entity repulsion distance gate (skip physics beyond 64 blocks)
- Weather wind throttle (cache lookups for 4 frames)
- Particle distance gate (skip emitters beyond 48 blocks)
- Ambient sound position throttling (skip updates when stationary)
- Fly sound volume deduplication (skip updates below 1% change)
- Name-tag frustum reuse (IsRendered flag instead of recomputing)
- Animation check reorder (distance before frustum)
- Server GC + DATAS (73% fewer collections, zero Gen1 promotions)
- Lock contention reduction (10 locks to System.Threading.Lock)
- BlockPos reuse in particle ticks (99.9% GC reduction in that path)
- Mat4f.Multiply inlining (13 hot methods, 50k+ calls/frame)
- Mouse wheel fix at low sensitivity (#9710)
- Prospecting dialog mouse fix (#8874)
- Health tooltip decimal fix (#8901)
- Creative search cache crash containment (a mod exception no longer kills the client)

## Getting Started

Optimum compiles from source because Vintage Story is proprietary. The first build downloads the official client (~570MB) and decompiles it. Subsequent builds reuse the cache.

### Linux

**Interactive installer** (guided, checks and installs prerequisites):

```bash
git clone https://github.com/Zaldaryon/Optimum.git
cd Optimum
./scripts/install-linux.sh
```

The installer shows a ✓/✗ checklist of required tools, offers to install anything missing, asks where to install (default: `~/.local/share/optimum`), and creates a menu entry. Run the game from the menu or with `~/.local/share/optimum/optimum-launch.sh`.

**AppImage** (single portable executable, no install):

```bash
git clone https://github.com/Zaldaryon/Optimum.git
cd Optimum
make package-appimage
chmod +x Optimum-v0.2.5-linux-x64.AppImage
./Optimum-v0.2.5-linux-x64.AppImage
```

If `appimagetool` is missing, the script downloads it (14MB, once) into `.tools/`.

**Manual build** (for development or full control):

```bash
git clone https://github.com/Zaldaryon/Optimum.git
cd Optimum
make check    # report which tools are installed (installs nothing)
make build    # bootstrap + build
make run      # build, deploy, and launch client
```

Requires .NET 10 SDK, bash, python3, git, curl, perl.

### Windows

**GUI installer** (checks prerequisites, offers downloads, choose install folder):

```powershell
git clone https://github.com/Zaldaryon/Optimum.git
cd Optimum
.\install-windows.cmd
```

The installer detects .NET 10 SDK, Git, ilspycmd, and a local Vintage Story install. Missing tools show with a "Download" checkbox that opens the install page. Choose the install directory, click Install. Done.

**Manual build** (PowerShell):

```powershell
.\scripts\bootstrap.ps1                        # download, decompile, clone forks, patch
dotnet build VintageStory.slnx -c Release      # compile optimized DLLs
.\scripts\package.ps1                          # build Optimum-v0.2.5-win-x64/ folder
.\scripts\package.ps1 -Zip                     # folder + portable zip
```

Requires .NET 10 SDK, Git for Windows, and PowerShell 5.1+.

### macOS

```bash
git clone https://github.com/Zaldaryon/Optimum.git
cd Optimum
make build
./scripts/package-macos.sh --arch arm64        # Apple Silicon .dmg
./scripts/package-macos.sh --arch x64          # Intel .dmg
```

Open the .dmg and drag Optimum.app to Applications. Requires .NET 10 SDK, bash, python3, git, curl, perl.

## Build

### Packaging for distribution

The optimized DLLs are platform-agnostic IL, so one build packages for every OS. Each script downloads the official client for that platform, overlays the DLLs and optimized shaders, and rebrands the launcher and icon.

```bash
make package              # all targets this host can produce
make package-linux        # tar.gz
make package-appimage     # single .AppImage executable
make package-macos        # .dmg (ARCH=arm64 or x64)
make package-win          # Windows zip (needs pwsh + innoextract off-platform)
```

Or call the scripts directly:

```bash
./scripts/package-linux.sh                     # Optimum-v0.2.5-linux-x64.tar.gz
./scripts/package-linux.sh --format zip
./scripts/package-linux.sh --format appimage   # Optimum-v0.2.5-linux-x64.AppImage
./scripts/package-macos.sh --arch arm64        # Apple Silicon .dmg
./scripts/package-macos.sh --arch x64          # Intel .dmg
./scripts/package-all.sh                       # all capable targets at once
./scripts/package-all.sh --targets linux-x64,osx-arm64
```

The Linux script renames the launcher to Optimum, repoints run.sh, swaps the window icon, and brands the .desktop entry. The macOS script assembles Optimum.app (renamed launcher, Icon.icns from the logo, rebranded Info.plist) and builds a drag-to-Applications .dmg.

### Host prerequisites for packaging

Beyond the build requirements (.NET 10 SDK, bash, git, curl, perl), packaging needs:

| Tool | What it does | Install |
|---|---|---|
| `appimagetool` | Builds .AppImage (downloaded to .tools/ on first use) | auto or `sudo apt install appimagetool` |
| `pwsh` | Windows packaging off-platform (win-x64 target only) | [Install PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell) |
| `innoextract` | Extracts the Windows Inno installer on Linux/macOS | `sudo apt install innoextract` |
| `mkisofs` / `genisoimage` | Creates hybrid HFS image for .dmg on Linux | `sudo apt install cdrtools` or `genisoimage` |
| `cmake` + `git` | Build libdmg-hfsplus (compiled once into .tools/) | `sudo apt install cmake git` |

Linux and macOS packaging runs with bash. No PowerShell required for those targets.

### Host x target matrix

| Produce ↓ \ on → | Linux host | macOS host | Windows host |
|---|---|---|---|
| **linux-x64** | ✅ tar.gz / AppImage | ✅ tar.gz | ✅ tar.gz |
| **osx-x64 / osx-arm64** | ✅ unsigned .dmg | ✅ signed .dmg (hdiutil) | ⚠️ .tar.gz fallback |
| **win-x64** | ✅ needs innoextract + pwsh | ✅ needs innoextract + pwsh | ✅ native |

The .dmg files built on Linux are unsigned. macOS Gatekeeper shows a warning on first open; users right-click > Open to accept. For a notarizable .dmg, build on macOS with an Apple Developer certificate.

**ARM note.** Vintage Story ships native ARM clients only for macOS (`osx-arm64`). Linux and Windows have no native ARM client. Those packages are x64-only; ARM hardware runs them via emulation ([box64](https://github.com/ptitSeb/box64) on Linux, Windows-on-ARM x64 emulation).

## How It Works

Optimum decompiles the official Vintage Story client, applies performance patches at compile time, and produces optimized DLLs and shaders. Your vanilla client install provides the runtime, assets, and account. Optimum replaces the engine DLLs and select shaders with faster versions.

No Harmony overhead. No runtime patching. Native compiled performance.

## License

GPL-3.0 with the Commons Clause. The source is open: read it, modify it, share it. Copyleft applies, so any work that includes Optimum source carries the same license. The Commons Clause adds one rule on top: you may not sell Optimum or a product whose value derives from it.

**Vendor Exception.** The Licensor grants [Anego Studios](https://anegostudios.com) a perpetual, irrevocable, royalty-free license to use, modify, and incorporate Optimum patches into their products. The Commons Clause restriction does not apply to Anego Studios. This exception follows the GPL-3.0 Section 7 additional permissions mechanism.

See [LICENSE](LICENSE) for the full terms.
