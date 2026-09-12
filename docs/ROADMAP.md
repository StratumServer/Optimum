# Optimum roadmap

What is done, what is being worked on, and what is planned. The detailed design lives in
[`docs/vulkan-native-plan.md`](vulkan-native-plan.md); acceptance numbers live
in `docs/vulkan-acceptance.md` and `docs/taa-acceptance.md`; the temporal rules live in
`docs/temporal-frame-contract.md`.

Status: **done** = merged to `main` and accepted in game · **in progress** = on a branch · **planned** = not started.

## Done

| | What | Evidence |
|---|---|---|
| done | **TAA** with a jitter-stable resolve: 3x3 nearest-depth disocclusion, motion from the nearest-depth tap, luminance anti-flicker weighting | `docs/taa-acceptance.md`, `scripts/dev/taa-rejection.py` (distant-leaf rejection 1.05 %, was 3.7 %) |
| done | **Native Vulkan backend, Milestone 1**: platform substitution (`VulkanClientPlatform : ClientPlatformWindows`), timeline semaphores, asynchronous uploads, split present, usage-derived barriers, streaming frame graph, transient allocator | `docs/vulkan-acceptance.md` "Milestone 1 exit results"; blocking uploads 0, passes == scopes, validation clean |
| done | **Latency reduction**, on by default: the sleep moved before input sampling, NVIDIA Reflex (`VK_NV_low_latency2`), AMD anti-lag (`VK_AMD_anti_lag`) and Optimum's own completion pacing | `docs/vulkan-acceptance.md` "Latency acceptance"; input-to-present 7.67 ms -> 1.85 ms on an RTX 4070, free with Reflex |

## Landed on `feat/dlss`, not yet seen in game

Built, tested and merged into the branch by the 2026-09-12 roadmap wave. Every one of these is proven by
the GPU suite on a real device and by source coverage, and **none of it has been run in the client** - the
wave's stages had no launch permission. That is the gap between this table and the one above, and it is why
these rows are not "done": by this file's own legend, done means accepted in game.

| | What | Evidence | What is unproven |
|---|---|---|---|
| landed | **A headless render harness**: `OPTIMUM_HEADLESS` runs the real client and the real renderer with the window never mapped, a chat-command script sets the scene and drives vanilla's keyframed camera, and the selected in-world frames are written as PPM through the backend-agnostic readback | `scripts/dev/headless-capture.sh`, `Optimum.Render.Vulkan.Tests/HeadlessCaptureTests.cs` (frames off a device with no surface at all, and display-resolution frames while an upscaler runs), `Optimum.Tests/headless-harness-coverage-tests.cs` | The whole end-to-end run. Nobody has yet driven a live client through it; the hidden-window presentation path is proven by the GPU suite and by inspection. No camera path is checked in |
| landed | **The orchestrator's slot coupling**: the latency backend follows the pair (upscaler vendor, GPU vendor), not the device alone - DLSS on NVIDIA takes Reflex, FSR on AMD anti-lag, XeSS on Intel means XeLL and so Native on every Vulkan path, every cross-vendor pair and the vendor-less passthrough slot take Optimum's own pacing, and no upscaler at all leaves the device auto order untouched | `Optimum.Render.Vulkan/Latency/LatencySlotCoupling.cs`; the full vendor table plus the override precedence in `LatencySlotCouplingTests` (53 cases) | Whether the chosen backend is the right one for frame times in a real session, on anything but this dev box |
| landed | **A passthrough upscaler**: the slot that plans exactly like DLSS - same render size, same jitter, same LOD bias - and reconstructs with a magnifying blit instead. Both the diagnostic that separates our rendering from the vendor's and the fallback upscaler for a GPU with no vendor path | `Optimum.Render.Vulkan/Upscale/PassthroughUpscaler.cs`, `PassthroughUpscalerTests` (the plan matches NGX's own answer size for size at all five presets) | The magnified frame on screen, and the overlays' depth upscale behind it - shared code that has never run behind a magnifying blit |
| landed | **SSAO temporal dither** (the GTAO item's step 2): vanilla's screen-locked Bayer-128 dither advances by the golden ratio per frame under `TAAMOTION`, so successive frames rotate the kernel instead of re-rolling the same screen-fixed one | `sources/shaders/ssao.fsh`, `SsaoTemporalDitherTests` (AO differs per frame with the temporal pipeline on, bit-identical with it off, bit-identical when the frame index repeats) | The pay-off. AO that converges instead of fighting the accumulator is the claim; it has not been judged in game or on a parity dump |

The next session's first job, before adding anything: run the client on both backends, confirm the renderer
from the log, and look. The harness exists precisely so that is a script invocation now.

### The headless render harness, honestly

What it covers. `OPTIMUM_HEADLESS=1` creates the window with `StartVisible=false` and `StartFocused=false`:
a real window with a real surface and a real swapchain, never mapped and never focused, on both backends
(there is no surfaceless GL path in this client, so this is the only offscreen mode that is symmetric).
The frame loop, the swapchain and every rendering path are unchanged - the harness is one call in
`window_RenderFrame`, beside the parity dump, after the post chain and the final blit. Frames come from
`ReadDefaultFramebuffer`, the same polymorphic call the in-game screenshot makes and a device-side copy on
Vulkan, so no OS window capture is involved and no compositor is needed; they are written as
`frame-NNNNNN.ppm` at a chosen frame list or cadence, which `scripts/dev/ssim.py` reads and which pairs
between two captures by name. `OPTIMUM_HEADLESS_COMMANDS` feeds a file of chat lines on an in-world frame,
routed the way the chat HUD routes what a human types, so `/time` and `/weather` fix the scene and `.cam
load` / `.cam play` drive vanilla's own keyframed camera (`SystemCinematicCamera`, which nothing in this
repo used before). `OPTIMUM_HEADLESS_FIXED_DT` pins `ClientMain.DeltaTimeLimiter`, the field vanilla's own
recorder sets, so the simulated step is constant. A permanently unfocused window falls under the existing
30 FPS background cap, so a run does not take the machine. The renderer line is still logged and
`scripts/dev/headless-capture.sh` refuses to report a capture it cannot attribute to the backend it asked
for.

What it does not cover. A display server is still required - real, nested or Xvfb - because GLFW asks for
the screen size before any window exists and Vulkan needs a WSI surface; "headless" here means no visible
window, not no display. Reproducibility is frame-for-frame repeatable, not bit-exact: a fixed step does not
pin chunk streaming, particle or mob RNG, which is the same standard `docs/vulkan-acceptance.md` already
sets for GL-vs-GL noise. The per-attachment dump `scripts/dev/taa-rejection.py` reads is still
`OPTIMUM_PARITY_DUMP` (composed in by `--parity-dump`, not replaced). Presenting to a never-mapped window
has been exercised on this project's dev box and in the GPU suite, not on every driver, Wayland or Xvfb
combination. No camera path is checked in yet - one has to be authored per scene with `.cam p` and
`.cam save`.

## In progress

| | What | Where |
|---|---|---|
| in progress | **DLSS Super Resolution**: NGX through a native shim, the upscaler slot with the post chain at display resolution, its own settings tab, presets and live switching | branch `feat/dlss`, PR #3 |

Open on that branch: shimmer at Performance and Ultra Performance (the raster jitter sign was one cause and
is fixed; SSAO placement was another; a distance-dependent "swimming" remains under investigation), and the
AO work below.

## Planned

### Next

- **DLSS Frame Generation (DLSS-G)** - reports available on native Linux; Optimum owns the pacing
  (present the generated frame when evaluate returns, the retained real frame at equal spacing from a
  present thread), `FramesInFlight = 3`, HUD-less colour and UI inputs. Requires Reflex active.
  Design and order of work: the "DLSS frame generation: the design" section below.
- **XeSS and FSR** - the second and third upscaler backends behind the same slot. XeSS ships Windows-only
  libraries; AMD's current SDK has no Vulkan backend, so FSR is a shader port. The coupling above already
  maps their setting tokens, so they need no latency work when they land - only `UpscalerNames` grows.

(The slot coupling that used to sit here has landed; see "Landed on `feat/dlss`".)

### Quality and tooling

- **GTAO (XeGTAO) replaces the vanilla SSAO.** Vanilla is hemisphere SSAO with a screen-locked Bayer-128
  dither at half render resolution - a dither fixed to the pixel grid re-rolls every surface point's kernel
  under a jittered camera, which no temporal accumulator can average. Order of work: (1) composite AO
  before the upscaler at render resolution, (2) make the dither temporally varying, (3) only then port
  XeGTAO (MIT, HLSL compute -> GLSL port; 0.56 ms at 1080p on an RTX 2060). Judge it against fixed SSAO,
  not against today's.
- **Acceptance runs driven by the headless harness.** The harness itself is done (above); what is still
  manual is using it - an authored camera path per scene checked in as data, a shimmer number computed
  from a capture rather than judged by eye, and the harness wired into `docs/vulkan-acceptance.md` so a
  backend comparison is a script invocation. See "what it does not cover" under the harness for the gaps.

  *The determinism guard the shimmer number needs.* A shimmer number is a difference between consecutive
  captured frames, so anything that moves for a reason other than the effect under test is measured as
  shimmer. Two captures are comparable only when all of this is pinned and recorded beside the number:
  the world (save file and seed), the camera path (the checked-in `.cam` file, played from a fixed
  in-world frame), the step (`--fixed-dt`, same value), the frame list (same indices, not the same
  count), the graphics settings that change what is drawn (`ssaa`, `fxaa`, `ssaoQuality`, `bloom`,
  `godRays`, `mipMapLevel`, render and display resolution, upscaler and quality preset), the time and
  weather the command script sets, and the backend and GPU/driver the run actually used (from the
  renderer line, not from what was asked for). What is *not* pinned, and therefore may never be read as
  a signal: chunk streaming order and the pop-in it causes, particle and mob RNG, wind phase, and
  anything before the first frame the world has finished loading - so the capture starts well after the
  command script, and mobs and weather are commanded off rather than hoped away.

  A run that drifted is rejected, not reported. The check is mechanical: capture the same scene twice on
  the same backend and settings, and compare the two runs frame by frame (`scripts/dev/ssim.py`). That
  GL-vs-GL (or Vulkan-vs-Vulkan) self-pair is the noise floor, and the shimmer number is only meaningful
  above it. A run whose self-pair falls below the floor agreed in `docs/vulkan-acceptance.md`, or whose
  recorded settings, camera file, frame list or renderer line differ from the reference run's, is thrown
  away and re-run - it is not published with a caveat. Frames that fail to pair by name (a short or
  ragged capture) are the same failure and get the same treatment.

### Bigger, in dependency order

- **Native Vulkan shaders (Phase 3)** - Vulkan-native GLSL for the vanilla program set, explicit sets and
  bindings, compiled offline to SPIR-V; the rewriter stays for mod shaders.
- **Performance (Phase 4)** - disk pipeline cache, per-pass GPU timestamps, transient aliasing on by
  default. Carries the open Milestone 1 gap: Vulkan costs about 25 % more frame time than OpenGL on the
  fixed scene and is GPU-bound.
- **Mod API (Phase 5)** - the pass and motion-writer API in the contracts, fork ports, launcher scan v2.
- **HDR output** - not a swapchain-format switch: the scene colour gains range, a real tone mapper appears
  at the end of the chain, the UI moves to display-referred space, and the swapchain gains HDR10/scRGB with
  its metadata. The upscaler contract changes with it (DLSS switches to `IsHDR = 1`, exposure stops being
  optional) and DLSS-G forbids FP16/scRGB, so an HDR path that wants frame generation must be RGB10A2.
  After Phase 3, alongside the frame-generation format decision.
- **Auto-PBR materials** - a prerequisite for anything specular, and useful on its own. Minecraft shader
  packs (Complementary's Integrated PBR and friends) generate normals from luminance differences in the
  albedo and guess specular from colour, because a resource pack is only pixels. Vintage Story gives us
  more: every block carries a material class (stone, wood, metal, glass, plant, liquid) and its light
  emission, so roughness, metalness and emissive masks come from a curated table keyed on what the block
  *is*, with generated normals filling in surface detail. Generate it at atlas build time into companion
  atlas layers (normal, roughness/metalness, emissive), never per frame; hand-authored PBR layers from a
  resource pack override the generated ones where present. Wanted by the user, 2026-09-12: "I want to use
  some autopbr like some minecraft shaderpacks do when possible." Sits after native shaders (the material
  set convention lands there) and before ray tracing, which needs roughness and normals to be worth
  anything.
  Read from Iris (cloned 2026-09-12, `net.irisshaders.iris.pbr`): Iris itself generates nothing - it is the
  plumbing, and that plumbing is what we copy. Hand-authored `_n` and `_s` textures are loaded per sprite
  and assembled into companion atlases beside the albedo atlas (`PBRAtlasTexture`, `PBRAtlasHolder`,
  `AtlasPBRLoader`), with LabPBR channel packing and, importantly, **per-channel mipmap generation**
  (`ChannelMipmapGenerator`, `DiscreteBlendFunction`) - averaging a normal or a packed specular channel
  across mips the way colour is averaged is wrong. The auto-generation the user is after lives in the shader
  packs instead (Complementary's Integrated PBR derives normals from albedo luminance differences when a
  pack ships no PBR layers); take the technique, not their code - those packs carry restrictive licences.
- **Ray tracing** - last. Acceleration structures over a world the player edits (BLAS per chunk, TLAS over
  loaded chunks, refit as chunks stream) are the hard part; `VK_KHR_ray_query` in the existing fragment
  shaders is the cheaper entry than a full ray-tracing pipeline. Spend rays in this order: ambient
  occlusion (the honest end of the GTAO item), shadows, then water and glass reflections. Every one needs a
  denoiser - DLSS Ray Reconstruction on NVIDIA (`libnvidia-ngx-dlssd.so`, already bound), a spatiotemporal
  denoiser as the cross-vendor fallback. Depends on native shaders, a stable temporal contract, HDR range
  and the headless harness.

## DLSS frame generation: the design

Written from a read-only map of the present path, the frame ring and the render-stage call order against
the DLSS-FG Programming Guide v310.7.0 (2026-09-12). Nothing here is implemented. The point of this section
is that the next session starts from the order of work and the three hazards, not from the guide.

### What the present path can and cannot do today

`VulkanDevice.Present()` is single-shot: one submit, one acquire, one present-command submit, one
`vkQueuePresentKHR`, return - called once per game-loop tick from the lib's `EndFrame()`. Frame generation
needs that call site to present **N times per rendered frame at even wall-clock spacing**, generated frame
first, then the retained real one.

Three of the four pieces underneath it are already shaped for that, which is the good news:

- `Swapchain`'s `PresentIdCounter` and `PresentIdMap` were built many-present-ids-to-one-frame-id on
  purpose; the class comment says outright that frame generation will present a frame more than once.
- `VulkanContext.QueueLock` exists because `vkQueueSubmit`/`vkQueuePresentKHR` from two threads is
  undefined behaviour, and the client already submits from two threads for uploads. A present thread can
  share it.
- `FrameTimeline.ReserveFrame`/`NoteFrameSubmitted` are already `Interlocked`, so the clock itself is
  thread-safe even though its doc comment still says "render thread".
- `LatencyMarker` already reserves `OutOfBandRenderSubmitStart/End` and `OutOfBandPresentStart/End`
  explicitly for async present paths. Nothing stamps them yet; they are the seam.

Two are not:

- `BlitPresentPath`'s blit source is a constructor-captured delegate hardwired to `_defaultColor`. There is
  no way to present a *different* image - the generated frame, or NGX's `OutputReal` copy - without giving
  `IPresentPath.Record` a per-call source.
- `FrameSlot.BeginPresentCommands`/`SubmitPresent` record into the current render frame's own command
  pool, which the render thread resets in the next `BeginFrame`. A real present thread cannot borrow it;
  it needs its own pools.

### HUD-less colour and the UI, today

The real stage order is `RenderFinalComposition` -> `RenderAfterFinalComposition` -> `BlitPrimaryToDefault`
-> `RenderAfterBlit` -> Ortho (the 2D GUI). `RenderFinalComposition` writes `OptimumCompositeFrameBuffer`,
which *is* the guide's `pHudless` state - but nothing snapshots it, and the very next stage draws
world-space overlays (selection boxes, work-item guides) straight onto the same image. The composited
backbuffer the guide wants exists continuously as `_defaultColor` once Ortho finishes, so that half is
free. `pUI` - premultiplied UI colour plus alpha - has no analogue at all: the GUI is alpha-blended onto
the same buffer as the world and is never isolated. `docs/temporal-frame-contract.md` §8 reached the same
conclusion independently and reserves the rows.

### Order of work

Smallest independently verifiable step first, rising risk. Do not reorder: each step exists to isolate one
unknown from the next.

0. **Two presents per frame, no NGX anywhere.** Present `_defaultColor` a second time inside
   `VulkanDevice.Present()`, tagged Generated against the same latency frame id and stamped through the
   already-reserved out-of-band markers. This isolates the one open mechanical question - whether the
   acquire-semaphore free list and the timeline bookkeeping survive 2x present pressure - from every
   vendor and threading question. Verifiable with a GPU test under `sync,best` (zero new hazards) plus a
   check that both presents carry the identical image. If this breaks, it breaks here, before any vendor
   code exists.
1. **A per-call source on `IPresentPath.Record`**, replacing the captured delegate. Mechanical; behaviour
   is unchanged while every caller still passes `_defaultColor`.
2. **The `SceneNoHud` snapshot**: copy `OptimumCompositeFrameBuffer`'s colour right after
   `RenderFinalComposition` and before `RenderAfterFinalComposition`, published the way
   `MotionAttachmentIndex` is. Verifiable alone - it equals the composited image on a frame with no
   overlays and no GUI, and provably differs once either draws.
3. **Decide how `pUI` is built.** A second premultiplied-alpha GUI pass (roughly 2x GUI draw cost) or
   differencing the composite against `SceneNoHud` (cheaper, wrong wherever GUI and world colours
   coincide). This is a design decision for the user, not an engineering unknown - settle it before code.
4. **`FramesInFlight` 2 -> 3, on its own.** Every arena already sizes off `_frames.FramesInFlight`, so this
   is close to a constant flip - except `AcquireSemaphoreFreeList`'s `imageCount + 1`, which is derived
   from "at most FramesInFlight - 1 presents outstanding", an assumption frame generation breaks. Re-derive
   it, do not just recompile it. Land it before any FG code so `pacing-gate.sh` separates the cost of
   three-deep buffering from the cost of generation.
5. **NGX DLSS-FG bring-up**, mirroring the proven `DlssUpscaler`/`NgxDlssFeature`/`NgxSession`/`NgxLifetime`
   shim pattern for `NVSDK_NGX_Feature_FrameGeneration`. Feed it the composited image as a stand-in for
   `pHudless`/`pBackbuffer` until step 3 lands `pUI` for real.
6. **The present thread and its spacing pacer, last.** It has the least existing scaffolding and is the
   hardest thing here to verify.

### The three things most likely to go wrong

1. **Resource lifetime past Present.** The generated frame and the retained real frame must survive until
   an out-of-band present thread actually presents them, but every transient and every frame-slot resource
   is retired against the timeline value of the *render* frame that produced it - which assumes it was
   consumed by the time that value completes. A present thread that lags the render thread by one
   `BeginFrame` lets the retire queue recycle an image before it is blitted. That is flicker or garbage
   that no single-frame readback can see; it needs a multi-frame GPU test, and poison mode.
2. **Command-pool threading, not GPU synchronisation.** A present thread needs its own command pools,
   synchronised with the render thread only through `QueueLock` and timeline waits. Getting this wrong is a
   CPU data race on command-buffer state, which `sync,best` validation does *not* reliably catch under
   light interleaving - unlike the GPU hazards it is good at.
3. **Spacing measured at the wrong place.** DLSS-FG wants the generated and real presents of one interval
   evenly spaced in wall-clock time, but nothing today measures present-to-present spacing - the pacing
   model and the frame-interval tracker both measure `BeginFrame` to `BeginFrame`. This is exactly the
   "jitter invisible to screenshots" failure class: it needs `pacing-gate.sh` stddev and percentile numbers
   against the OpenGL baseline of the same scene, never a screenshot.

### Open questions to settle first

- Whether `native/optimum-ngx` already forwards the DLSS-FG entry points (`NGX_VK_CREATE_DLSSG` /
  `NGX_VK_EVALUATE_DLSSG`) or only the super-resolution ones. The plan names them as the target, not as
  implemented.
- Whether `NvLowLatency2Backend`'s current sleep and marker pattern mis-times Reflex once a generated
  present is interleaved. DLSS-G requires Reflex active, so this is not optional.
- The temporal contract needs a v2 for the presentation-lifetime and HUD-less rows (§8 already reserves
  them). Motion vectors and depth (§7.1, §7.3) need no new row - FG consumes the same per-pixel semantics
  super resolution already does.
- HDR interacts: DLSS-G forbids FP16/scRGB, so an HDR path that wants frame generation must be RGB10A2.
  That decision belongs with the HDR item, not this one.

## Known debt

- `TransientAllocator` is implemented but not driven by the frame graph (aliasing is off by default).
- `ClearDepth` ignores the depth write mask (predates the frame graph).
- Vulkan `BuildMipMaps` keeps the texture LOD bias where OpenGL resets it to 0.
- Shaders are whole-file overrides, not patches; a shader patch system against the vanilla archive is
  planned (`CLAUDE.md`, Known debt).
- The item atlas is not LOD-biased under an upscaler, because the GUI draws inventory icons from it at
  display resolution; fixing it properly needs a per-draw or per-unit bias.
