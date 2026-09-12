# Optimum roadmap

What is done, what is being worked on, and what is planned. The detailed design lives in the Vulkan-native
rebuild plan (local, `~/.claude/plans/i-never-wanted-this-sequential-kernighan.md`); acceptance numbers live
in `docs/vulkan-acceptance.md` and `docs/taa-acceptance.md`; the temporal rules live in
`docs/temporal-frame-contract.md`.

Status: **done** = merged to `main` and accepted in game · **in progress** = on a branch · **planned** = not started.

## Done

| | What | Evidence |
|---|---|---|
| done | **TAA** with a jitter-stable resolve: 3x3 nearest-depth disocclusion, motion from the nearest-depth tap, luminance anti-flicker weighting | `docs/taa-acceptance.md`, `scripts/dev/taa-rejection.py` (distant-leaf rejection 1.05 %, was 3.7 %) |
| done | **Native Vulkan backend, Milestone 1**: platform substitution (`VulkanClientPlatform : ClientPlatformWindows`), timeline semaphores, asynchronous uploads, split present, usage-derived barriers, streaming frame graph, transient allocator | `docs/vulkan-acceptance.md` "Milestone 1 exit results"; blocking uploads 0, passes == scopes, validation clean |
| done | **Latency reduction**, on by default: the sleep moved before input sampling, NVIDIA Reflex (`VK_NV_low_latency2`), AMD anti-lag (`VK_AMD_anti_lag`) and Optimum's own completion pacing | `docs/vulkan-acceptance.md` "Latency acceptance"; input-to-present 7.67 ms -> 1.85 ms on an RTX 4070, free with Reflex |

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
- **XeSS and FSR** - the second and third upscaler backends behind the same slot. XeSS ships Windows-only
  libraries; AMD's current SDK has no Vulkan backend, so FSR is a shader port.
- **The orchestrator's slot coupling** - a vendor latency backend only when the upscaler's vendor matches
  the GPU, otherwise Optimum's own pacing (DLSS -> Reflex, FSR on AMD -> anti-lag, XeSS+XeFG on Intel ->
  XeLL on the Windows D3D12 bridge, every cross-vendor pair -> ours).

### Quality and tooling

- **GTAO (XeGTAO) replaces the vanilla SSAO.** Vanilla is hemisphere SSAO with a screen-locked Bayer-128
  dither at half render resolution - a dither fixed to the pixel grid re-rolls every surface point's kernel
  under a jittered camera, which no temporal accumulator can average. Order of work: (1) composite AO
  before the upscaler at render resolution, (2) make the dither temporally varying, (3) only then port
  XeGTAO (MIT, HLSL compute -> GLSL port; 0.56 ms at 1080p on an RTX 2060). Judge it against fixed SSAO,
  not against today's.
- **A headless render harness.** The real renderer and client path without a visible window, writing frames
  to disk, with a scripted camera and fixed world state so a sequence is reproducible frame for frame, and
  low GPU priority so it runs while the machine is in use. This is what turns shimmer into a number instead
  of an argument, and it is why in-game checks are currently rationed. Pieces already exist: the GPU suite
  creates real devices with hidden windows, `OPTIMUM_PARITY_DUMP` writes every attachment at a chosen
  frame, `scripts/dev/parity-capture.sh` drives an unattended run.

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

## Known debt

- `TransientAllocator` is implemented but not driven by the frame graph (aliasing is off by default).
- `ClearDepth` ignores the depth write mask (predates the frame graph).
- Vulkan `BuildMipMaps` keeps the texture LOD bias where OpenGL resets it to 0.
- Shaders are whole-file overrides, not patches; a shader patch system against the vanilla archive is
  planned (`CLAUDE.md`, Known debt).
- The item atlas is not LOD-biased under an upscaler, because the GUI draws inventory icons from it at
  display resolution; fixing it properly needs a per-draw or per-unit bias.
