# Optimum 0.3.6

Optimum 0.3.6 targets Vintage Story 1.22.7 and fixes community-reported rendering and installer bugs from the mod page and Discord.

## Fixes

- Map shader crashes on graphics settings change, Alt+F1 reload, SSAO toggle, or any trigger that calls `ShaderRegistry.ReloadShaders()`. The instanced-quad map renderer now subscribes to the shader reload event and recreates its program. The `Ready` guard rejects disposed shader handles.
- TerraTag map overlay incompatibility. The instanced page renderer renders as a background layer, then calls every loaded map component's `Render()` method. Harmony patches on `MultiChunkMapComponent.Render()` (TerraTag seam-fix, mipmap-clamp) fire correctly again.
- Dark or transparent rendering at non-native resolution. The OIT system cached a stale framebuffer reference after `RebuildFrameBuffers()`. It now checks reference identity against the engine's current FBO and recreates textures on the new object.
- Shader errors after window resize or mega screenshot. Same root cause as the dark rendering bug: framebuffer identity check catches the rebuild.
- Windows installer "not of a legal form" error when installing to a bare drive root (e.g. `D:\Optimum` where the user typed `D:\`). Both the GUI handler and the silent-mode entry point normalize bare drive letters before `GetFullPath`.
- Runtime `TypeLoadException` for `OptimumBoundedHandoff` when entering a world. The api-patcher now injects type-forward metadata (ExportedType rows) into the patched VintagestoryAPI.dll for all types living in `Optimum.Api.Contracts.dll`. The CLR follows the forward chain without requiring the types compiled into the API assembly.

## Known Incompatibility

Coria Ender Shaders patches the same OIT render stage that Optimum guards. The combination crashes with `NullReferenceException` inside `BeforeOIT.OnRenderFrame`. Use Ancestral Bliss or Sheyder as shader-mod alternatives.

## Validation

The official .NET 10 SDK and ilspycmd 10.1.1.8388 produced the donor tree. Patch validation reported 138 source patches, zero conflicts. The solution built with zero warnings and errors. The test suite passed 631 tests with 34 skips. The runtime patcher applied 126 of 126 required methods and 1 hook. The api-patcher injected 27 type forwards to `Optimum.Api.Contracts`. Full Linux installation (bootstrap, build, package, install, launch) completed with all 44 shaders compiled and a validated session.
