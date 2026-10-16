# Pharos SVG GPU diagnostic

This opt-in test exercises the patched public `SvgLoader.LoadSvg` method on a
live OpenGL client thread. It compares complete RGBA texture readbacks of the
same 48×32 SVG at 1x, 1.5x and 2x output sizes with native NanoSVG output.
Tiled, translucent, tinted and stroked solid shapes must take the candidate path with counters
`1/1/0`, within one channel value on flat pixels (32 on the stroke's antialiased edge pixels).
Gradient and dashed-stroke cases must use byte-exact native fallback with counters `0/0/0`.

The test grants the live device identity in memory so it can exercise the GPU
path. It does not write a GUI certificate or certification evidence and cannot
approve a device. Use a disposable staged client and datapath. Do not point it
at an authenticated game datapath.

## Prerequisites

- .NET 10 and a physical Intel, AMD or NVIDIA adapter with an accelerated GL
  driver.
- A staged Optimum client whose patched `VintagestoryLib.dll`, patched API,
  `Optimum.Api.Contracts.dll`, Cairo, Skia and shaders all come from the same
  candidate build.
- Pharos v0.5.0 at commit
  `3b8b5581839904346df87f9683aa4c751bae6717`.
- One fresh datapath and a report directory for this adapter. The report path
  must not resolve to either certification filename in the datapath.

Build the staged runtime from the matching Optimum checkout with `make build`
and `make patch-il`. Copy the resulting patched assemblies and Cairo output into
a disposable copy of the installed client, then apply the matching shader
overlay. The JSON records candidate and Pharos commits when supplied, OS,
architecture, GL identity/context and hashes for loaded runtime assemblies
(including API, game, contracts, Pharos, OpenTK and loaded Cairo/Skia
assemblies).

## Linux

```sh
export VINTAGE_STORY=/path/to/staged-client
export PHAROS_ROOT=/path/to/Pharos-v0.5.0
export OPTIMUM_PHAROS_DATA_PATH=/path/to/fresh/diagnostic-datapath
export OPTIMUM_PHAROS_REPORT_PATH=/path/to/evidence/svg-gpu-diagnostic.json
export OPTIMUM_CANDIDATE_COMMIT='<candidate-commit>'
export PHAROS_COMMIT=3b8b5581839904346df87f9683aa4c751bae6717
LD_LIBRARY_PATH="$VINTAGE_STORY/Lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet test Optimum.Pharos.SvgDiagnostic/Optimum.Pharos.SvgDiagnostic.csproj -c Release \
  -p:VintageStoryPath="$VINTAGE_STORY" -p:PharosRoot="$PHAROS_ROOT"
```

## Windows PowerShell

```powershell
$env:VINTAGE_STORY = 'C:\path\to\staged-client'
$env:PHAROS_ROOT = 'C:\path\to\Pharos-v0.5.0'
$env:OPTIMUM_PHAROS_DATA_PATH = 'C:\path\to\fresh\diagnostic-datapath'
$env:OPTIMUM_PHAROS_REPORT_PATH = 'C:\path\to\evidence\svg-gpu-diagnostic.json'
$env:OPTIMUM_CANDIDATE_COMMIT = '<candidate-commit>'
$env:PHAROS_COMMIT = '3b8b5581839904346df87f9683aa4c751bae6717'
$env:PATH = "$env:VINTAGE_STORY\Lib;$env:PATH"
dotnet test .\Optimum.Pharos.SvgDiagnostic\Optimum.Pharos.SvgDiagnostic.csproj -c Release `
  -p:VintageStoryPath="$env:VINTAGE_STORY" -p:PharosRoot="$env:PHAROS_ROOT"
```

Run one GPU test process at a time and keep each adapter's report separate.
The JSON identifies the actual GL vendor, renderer, version, context, OS,
candidate commits and loaded managed assembly hashes. A pass is SVG-path
diagnostic evidence only. It does not cover whole-GUI certification, draw-list
composition, or launcher validation.
