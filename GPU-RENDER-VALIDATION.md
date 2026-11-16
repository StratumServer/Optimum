# Skia GPU GUI validation on Intel and NVIDIA

This report follows [PR #595](https://github.com/StratumServer/Optimum/pull/595) and covers [issue #596](https://github.com/StratumServer/Optimum/issues/596) and [issue #597](https://github.com/StratumServer/Optimum/issues/597) on Optimum `main` at `d3a587b16d6210e66eb03226d404410d93e2836d`. The tests ran on physical adapters under Ubuntu 26.04. Intel drove the desktop by default. NVIDIA ran through PRIME offload.

## Hardware and client OpenGL identity

| Adapter | Driver | Renderer in client log | OpenGL in client log |
| --- | --- | --- | --- |
| Intel UHD Graphics 620 | Mesa 26.0.8-1ubuntu0.3 | Mesa Intel(R) UHD Graphics 620 (KBL GT2) | 4.6 Core Profile, Mesa 26.0.8-1ubuntu0.3 |
| NVIDIA GeForce MX150 | 580.178.04 | NVIDIA GeForce MX150/PCIe/SSE2 | 4.3.0 NVIDIA 580.178.04 |

`glxinfo -B` also confirmed the selected physical renderer before each run. The NVIDIA run used `__NV_PRIME_RENDER_OFFLOAD=1` and `__GLX_VENDOR_LIBRARY_NAME=nvidia`.

## OpenGL upload tests

Both runs used the command requested by the issues:

```sh
OPTIMUM_REQUIRE_GL_TESTS=1 dotnet test Optimum.Tests/Optimum.Tests.csproj -c Release --filter "FullyQualifiedName~GuiSkiaCpuUploadGl|FullyQualifiedName~GuiSkiaGpuUploadGl"
```

| Adapter | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Intel UHD Graphics 620 | 5 | 0 | 0 |
| NVIDIA GeForce MX150 | 5 | 0 | 0 |

The test suite creates its small surface fixtures in memory. Each run used the named physical GPU for its OpenGL context. No software renderer was used.

## Client startup and visual check

The Release client was patched from a clean bootstrap. Both authenticated data profiles were copies of the default datapath. The original datapath was left unchanged. On both adapters, the startup log reported `Optimum Skia GL probe passed`, `ModConfig/optimum.json` retained `GuiRenderBackend=skia-gpu`, and the log contained no `GUI GPU render candidate failed` or `Downgrading to Skia CPU` message. The main menu remained legible after resizing the window to 1280 by 850 pixels. I saw no missing text, black rectangles or incorrect colors in that menu check.

The bootstrap applied 247 patches. `make build` passed with four compiler or analyzer warnings. `make patch-il` patched all 207 required methods. `make check-patches` reported 173 applied patches, 74 Cecil entries, 40 exact runtime donors compiled and zero conflicts. Patch syntax validation found 287 valid patches. Compatibility validation found zero cast divergences and skipped 20 allowlisted targets. Shader validation passed.

All six Release test projects passed:

| Project | Passed | Skipped |
| --- | ---: | ---: |
| Optimum.Tests | 2,614 | 15 |
| Optimum.Launcher.Tests | 92 | 0 |
| Optimum.Cli.Tests | 38 | 0 |
| Optimum.Bootstrap.Core.Tests | 261 | 0 |
| Optimum.Installer.Tests | 106 | 0 |
| Optimum.Update.Tests | 97 | 0 |
| **Total** | **3,208** | **15** |

## In-world session remains open

The default datapath contains no saved world. I did not create a new world. The ten-minute in-world session with the inventory, character dialog and chat has not been run. These results cover the physical OpenGL tests and client startup only. Issues #596 and #597 remain open pending that real-world session.
