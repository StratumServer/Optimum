---
name: vulkan-parity-debug
description: Debug a rendering difference between the OpenGL path and the Vulkan backend (missing post-processing, wrong filtering, transparency, colours). Baseline capture, trace and dump analysis, GL-vs-device state diff, GPU regression test, in-game verification.
---

# Vulkan rendering parity debugging

The Vulkan backend reproduces GL state through `IOptimumGraphicsDevice`; every bug so far was a
state difference between a method's GL branch and its device branch, not shader maths.

## 1. Baseline before touching code
- `RENDERER=vulkan OPTIMUM_VULKAN_VALIDATION=1 OPTIMUM_RENDER_TRACE=/tmp/before.trace scripts/dev/run-client.sh`
- confirm `scripts/dev/client-renderer.sh` says Vulkan; screenshot to `/tmp/vulkan-before.png`
- same scene on `RENDERER=opengl`, screenshot `/tmp/opengl.png`; Read both and write down the differences in words.
- Trace summary (python): map `program N 'name'` lines to ids, count `fullscreen program=` per name,
  list `validation:` lines with `[error]`. Passes that never run are one class; passes that run but
  produce nothing are the other.
- Dump the intermediates from a live frame: `OPTIMUM_DUMP_TEXTURES=<ids from the trace's bind lines>
  OPTIMUM_DUMP_DIR=/abs/dir OPTIMUM_DUMP_AFTER_SECONDS=60`; build a contact sheet with PIL and Read it.
  Texture ids: `bind unit=U texture=T` lines right before a pass's `fullscreen` line.

## 2. Diff the two paths, do not theorise
For the pass that is wrong, open the method in `build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs`
(or the mod renderer) and read the `if (optimumDevice != null) {...}` branch next to the GL branch,
plus the framebuffer setup pair `SetupOptimumFrameBuffers` / `SetupDefaultFrameBuffers`. Check every
item in this list on both sides:
- texture create: format, mip levels, `TexParameter` min/mag filter, mipmap mode, wrap S/T, border colour, compare mode
- samplers: `GenSampler`/`BindSampler` semantics (the "linear" flag changes magnification only; min is NEAREST_MIPMAP_LINEAR)
- blend: `glEnable(BLEND)` vs `SetBlend(enabled, mode)` (the latter rewrites per-attachment factors; use `SetBlendEnabled` to toggle only), `glBlendFunci` per attachment
- draw buffers: `glDrawBuffers` vs `SetDrawBuffers(fbo, mask)`; an enabled-but-unwritten attachment is undefined
- clears per attachment, depth mask/test/func, cull, viewport for sub-resolution targets, scissor
- attachment indices and texture-id bookkeeping (`FrameBufferRef.ColorTextureIds`)
Write the list of mismatches first; then fix them all, not the first one.

## 2b. Instrument the shader instead of guessing (Codex's method, 2026-09-10)
When a pass "does nothing" or "wobbles" and the inputs are hard to inspect, temporarily rewrite the
pass's fragment shader to OUTPUT ITS INTERNAL SIGNALS AS COLOUR and look at the screen:
- Save the original: `cp sources/shaders/<pass>.fsh /tmp/<pass>-original.fsh`.
- Patch the deployed copy directly (no rebuild needed): edit `sources/shaders/<pass>.fsh` and copy it to
  `.vanilla/win-x64/vintagestory/assets/game/shaders/<pass>.fsh`; the game loads it at start.
  Example for the TAA resolve: `outColor = vec4(alpha, clamp(length(mv)/4.0, 0, 1), resetHistory != 0 ? 1 : 0, 1)`
  shows blend weight, motion magnitude and reset per pixel; early-out branches get a fixed colour
  (`vec4(0,0,1,1)`) so you can see which path ran.
- Replace real inputs with CONTROLLED ones to split the chain: a checkerboard or diagonal pattern as
  "current" proves the resolve+display copy are identical on both backends; a static pattern under the
  live jitter proves accumulation on its own, independent of wind, lighting and foliage.
- Freeze the world for comparisons: `/time set 12:00`, `/weather set clearsky`, `/weather setprecip -1`,
  still camera, screenshot pairs 1 s apart, numeric diff of a crop.
- Test allocator luck explicitly: fill a suspect texture with deliberately non-zero data before the pass
  (cold-start dumps that happen to read zero hide a missing clear).
- Restore the original shader afterwards and re-deploy; never commit the instrumented version.

## 3. Fix, test, verify
- Backend changes in `Optimum.Render.Vulkan/`, seam additions in `VintagestoryApi/Client/optimum-render-device.cs`
  (then contracts csproj), lib changes in `build/` + Cecil list (see patch-workflow skill).
- Add a GPU readback test per fix in `Optimum.Render.Vulkan.Tests` (draw with a translated shader,
  read the pixel, assert; readbacks must happen inside a frame).
- `make deploy`, run Vulkan with validation, screenshot after; run OpenGL; compare live. Then
  `dotnet test Optimum.Render.Vulkan.Tests`, `dotnet test Optimum.Tests -c Release`, `bash scripts/check-patches.sh`.
- Keep evidence (before/after PNGs, logs) in the scratchpad and cite it in the report and commit.
