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
- Lock contention reduction (11 locks to System.Threading.Lock)
- BlockPos reuse in particle ticks (99.9% GC reduction in that path)
- Mat4f.Multiply inlining (13 hot methods, 50k+ calls/frame)
- SSAO bilateral blur tap reduction (11 to 7 taps, 8 fewer reads/pass)
- Water foam grid reduction (5x5 to 3x3, 16 fewer depth reads/fragment)
- Mouse wheel fix at low sensitivity (#9710)
- Prospecting dialog mouse fix (#8874)
- Health tooltip decimal fix (#8901)

## Build

### Linux, WSL, or Git Bash

Requires .NET 10 SDK, bash, python3, git, curl, perl.

```bash
make check    # report which build/packaging tools are installed (installs nothing)
make build    # bootstrap + build (first run downloads ~570MB client archive)
make test     # run 81 unit tests
make run      # build, deploy, and launch client
make package  # build every package this host can produce (see matrix below)
```

### Windows (PowerShell)

Requires .NET 10 SDK, Git for Windows, and PowerShell 5.1+. The bootstrap downloads the official installer and extracts it with innounp (fetched on first run).

```powershell
.\install-windows.cmd                         # GUI installer
.\scripts\bootstrap.ps1                        # download, decompile, clone forks, patch
dotnet build VintageStory.slnx -c Release      # compile optimized DLLs
.\scripts\package.ps1                          # build Optimum-v0.1.2-win-x64/ folder
.\scripts\package.ps1 -Zip                     # folder + portable zip
```

The package script copies a vanilla install, applies the optimized DLLs and shaders, and installs the built launcher as Optimum.exe (carrying the Optimum icon). It writes a ready-to-run folder; pass -Zip to produce the portable archive. The GUI installer wraps this build and package flow. No runtime patching.

### Packaging for Linux and macOS

The optimized DLLs are platform-agnostic IL, so one build packages for every OS. Each script downloads the official client for that platform, overlays the DLLs and optimized shaders, and rebrands the launcher and icon. Run with PowerShell 7+ (`pwsh`).

```powershell
pwsh ./scripts/package-linux.ps1               # Optimum-v0.1.2-linux-x64.tar.gz
pwsh ./scripts/package-linux.ps1 -Format zip
pwsh ./scripts/package-macos.ps1 -Arch arm64   # Apple Silicon .dmg
pwsh ./scripts/package-macos.ps1 -Arch x64     # Intel .dmg
```

The Linux script renames the launcher to Optimum, repoints run.sh, swaps the window icon, and brands the .desktop entry. The macOS script assembles Optimum.app (renamed launcher, Icon.icns from the logo, rebranded Info.plist) and builds a drag-to-Applications .dmg.

### Build everything at once

```powershell
pwsh ./scripts/package-all.ps1                       # all capable targets
pwsh ./scripts/package-all.ps1 -Targets linux-x64,osx-arm64
```

### Host prerequisites for packaging

Beyond the build requirements (.NET 10 SDK, bash, git, curl, perl), packaging needs:

| Tool | What it does | Install |
|---|---|---|
| `pwsh` | Runs packaging scripts (all platforms) | [Install PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell) |
| `wine` | Runs `innounp.exe` to extract the Windows Inno installer on Linux/macOS | `sudo apt install wine64` |
| `hfsprogs` | Creates HFS+ filesystem for .dmg on Linux | `sudo apt install hfsprogs` |
| `libbz2-dev` | Build dependency for libdmg-hfsplus | `sudo apt install libbz2-dev` |
| `cmake` + `git` | Build libdmg-hfsplus (compiled once into .tools/) | `sudo apt install cmake git` |

On first macOS .dmg build, the script clones and compiles [mozilla/libdmg-hfsplus](https://github.com/mozilla/libdmg-hfsplus) into `.tools/`. This provides the `dmg` and `hfsplus` commands needed to create .dmg files without macOS.

On first Windows package build off-Windows, the script downloads `innounp.exe` and runs it via `wine` to extract the Inno Setup installer. The script cross-builds `Optimum.exe` with `dotnet build -r win-x64`.

### Host x target matrix

| Produce ↓ \ on → | Linux host | macOS host | Windows host |
|---|---|---|---|
| **linux-x64** | ✅ tar.gz | ✅ tar.gz | ✅ tar.gz |
| **osx-x64 / osx-arm64** | ✅ unsigned .dmg | ✅ signed .dmg (hdiutil) | ⚠️ .tar.gz fallback |
| **win-x64** | ✅ needs wine + innounp | ✅ needs wine + innounp | ✅ native |

The .dmg files built on Linux are unsigned. macOS Gatekeeper shows a warning on first open; users right-click > Open to accept. For a notarizable .dmg, build on macOS with an Apple Developer certificate.

**ARM note.** Vintage Story ships native ARM clients only for macOS (`osx-arm64`). Linux and Windows have no native ARM client. Those packages are x64-only; ARM hardware runs them via emulation ([box64](https://github.com/ptitSeb/box64) on Linux, Windows-on-ARM x64 emulation).

## How It Works

Optimum decompiles the official Vintage Story client, applies performance patches at compile time, and produces optimized DLLs and shaders. Your vanilla client install provides the runtime, assets, and account. Optimum replaces the engine DLLs and select shaders with faster versions.

No Harmony overhead. No runtime patching. Native compiled performance.

## License

GPL-3.0 with the Commons Clause. The source is open: read it, modify it, share it. Copyleft applies, so any work that includes Optimum source carries the same license. The Commons Clause adds one rule on top: you may not sell Optimum or a product whose value derives from it.

**Vendor Exception.** The Licensor grants [Anego Studios](https://anegostudios.com) a perpetual, irrevocable, royalty-free license to use, modify, and incorporate Optimum patches into their products. The Commons Clause restriction does not apply to Anego Studios. This exception follows the GPL-3.0 Section 7 additional permissions mechanism.

See [LICENSE](LICENSE) for the full terms.
