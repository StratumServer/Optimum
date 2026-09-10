# TAA for Optimum: plan (revised after Codex review, 2026-09-10)

## Context

Optimum renders on OpenGL and on the new Vulkan backend behind the `IOptimumGraphicsDevice`
seam. The roadmap is TAA first, then FSR/XeSS/DLSS upscaling, then frame generation, and possibly
path tracing with ray reconstruction. Target hardware includes an Arc 140V handheld. Today the game
has FXAA plus a spatial FSR 1 render-scale path (EASU + RCAS).

Every temporal upscaler, frame generator and neural denoiser consumes an overlapping set of
per-frame data: jittered colour, depth, motion vectors, jitter offsets, camera constants, exposure,
reactive/transparency masks, a reset flag, and (denoisers) material guides. Their exact resource
requirements, resolutions and colour stages differ, so this plan builds one engine-owned superset
("temporal frame contract") with explicit adapters per consumer, and treats the resolve as a
replaceable stage. The in-house GLSL resolve is the first implementation and the permanent fallback
on OpenGL.

The plan was reviewed by Codex (gpt-6-astra, high effort) against `build/`, `_ref/`, the mod forks
and the Vulkan backend; its review is at
`/tmp/claude-1000/-home-n1ght-Projekte-Optimum/73ffc57c-d773-4311-8bb2-b42cbc432943/scratchpad/codex-taa-review.md`.
Its major corrections were verified against the code and are folded in below.

## Decisions (agreed with the user)

1. **Placement**: the resolve runs after all scene geometry (including the AfterOIT decals/terrain
   overlay pass) and after SSAO, and before bloom, god rays, Final composition and the HUD. Verified
   against AMD's FSR "Placement in the frame" (SSAO/SSR/denoisers before, bloom/DOF/tonemap/grain
   after) and Unreal's temporal-upscaler position. Bloom, god rays and Final read *resolved* colour,
   glow and SSAO, so the resolve outputs those three signals, not only colour.
2. **Milestone = everything**: chunks, topsoil, liquid, standard-shaded items and block entities,
   instanced mechanical renderers, skinned entities, held items and first-person hands write motion
   vectors; OIT transparents, particles, clouds/aurora, sky and late overlays have explicit
   rejection or mask policies. The plan states per class whether motion is exact or a fallback.
3. **In-house resolve only** in this plan; vendor upscalers, frame generation and ray reconstruction
   are separate consumers in follow-up plans, each with its own capability requirements.
4. **Backend-neutral producers**: GLSL 330 through the existing translator and existing seam members
   (`SetDrawBuffers`, `SetBlendFuncSeparate(attachment, ...)`, `LoadFrameBuffer(FrameBufferRef, int)`,
   UBO members). Backend-native capabilities (device handles, extensions) are reserved for the
   vendor-library plan.
5. **Resolver behind an interface** (`ITemporalResolver` taking an immutable per-frame input record:
   resources, formats, rects, jitter, camera constants, exposure, reset reason, frame/view ids).

## Architecture

```
ClientMain.MainRenderLoop (ClientMain.cs:1154)
  shUniforms.Update (1182)  ->  [P1] TemporalFrame.Advance(): frame index, jitter N, rotate
                                     cur->prev for every captured view, reset detection
  Before stage: LiquidDepth prepass (quarter res, ChunkRenderer.OnRenderBefore) - jittered NDC shear
  shadows (own ortho matrices, never jittered)
  GlLoadMatrix(CameraMatrix) (1201): [P1] freeze "entity view" (CameraMatrix) and
                                     "terrain view" (CameraMatrixOrigin) for this frame
  Opaque stage: chunk (CameraMatrixOrigin), entity, standard, instanced, particle-cube draws with the
    JITTERED projection; motion-writing passes enable the Primary motion attachment via the
    draw-buffer mask and write (mv.xy px, reactive, writerDepth)
  OIT stage into Transparent (liquid, quad particles, OIT entities, clouds) - unchanged outputs
  MergeTransparentRenderPass, AfterOIT (decals, terrain pass 7, AfterOIT entities) - motion enabled
  [P4] liquid velocity pass: liquid geometry re-drawn into motion attachment only (depth test on,
    depth write off) so the water surface's motion and depth win where water is in front
RenderPostprocessingEffects (jittered projection for SSAO, as today)
  SSAO (unchanged) -> ssao texture
  [P2] taa-resolve: colour*1, glow, ssao, motion, depth, prev-depth, history -> MRT
       history colour RGBA16F | aux RGBA8 (glow.rg, ssao.b) | linear depth R32F
       camera-motion fallback computed inside the resolve for pixels whose motion is invalid
  [P5] optional RCAS (new uniform-driven variant) -> Luma; else Luma aliases the resolved colour
  bloom (Findbright) reads resolved colour + resolved glow; god rays read resolved glow
RenderFinalComposition: unchanged maths; primaryScene/glow/ssao inputs rebound to resolved textures
BlitPrimaryToDefault: unchanged (FSR1 EASU/RCAS or blit; RCAS not doubled when TAA sharpen is on)
AfterFinalComposition (work-item guides), Ortho HUD, AfterBlit (rifts): outside the temporal window,
  never jittered, never in history
```

Temporal window: jitter is applied only while `TemporalFrame.JitterActive` is true, from `Advance()`
until the resolve has run, and only to the perspective matrix last loaded by `Set3DProjection`
(world FOV or hand FOV, each with its own previous). Shadow, ortho, offscreen and late passes are
never jittered.

## Conventions (fixed, tested by a one-pixel adapter test)

- Motion vector `mv = previousPixel - currentPixel` (current pixel to where it was), render-resolution
  pixels, RG16F, jitter excluded (both positions via unjittered projections), undilated.
  History lookup: `historyUV = uv + mv / renderSize`. Adapters: FSR `motionVectorScale=(1,1)` for
  pixel input, XeSS pixel mode, DLSS `mvecScale = 1/renderSize`.
- Jitter `j` (px) is the raster displacement of a static point. With `Mat4d.Perspective`
  (`clip.w = -z_view`, `Mat4d.cs:923-944`) the shear is `P[8] -= 2*jx/W; P[9] -= 2*jy/H` in
  column-major float[16]. Halton(2,3), 8 phases at native (`ceil(8*scale^2)` when scaled), offsets
  in [-0.5,0.5], never (0,0). One NDC shear per frame; auxiliary targets of other sizes (LiquidDepth
  at quarter res) inherit the same NDC shift, never a per-target pixel offset.
- Current pixel in a writer: `gl_FragCoord.xy - j` (both backends: the Vulkan device renders
  offscreen unflipped and flips only in the present blit, `VulkanDevice.cs:690-715`). Previous pixel:
  interpolated previous clip position divided by its own w, then `(ndc*0.5+0.5)*renderSize`.
- Depth: Primary depth is `GL_DEPTH_COMPONENT32` on GL and `D32_SFLOAT` on Vulkan
  (`ClientPlatformWindows.cs:1571,1895`, `GlEnums.cs:107`), sampled as [0,1] via `sampler2D`; the
  contract records format and "0 = near" convention. Previous linear depth is an R32F resolve output.
- Motion attachment: RGBA16F on Primary at `MV_LOCATION` (2 without SSAO, 4 with; the OIT layer pass
  uses six outputs on Transparent, which is untouched). `rg = mv`, `b = reactive`, `a = writerDepth`
  = **window depth in [0,1]** at write time - the same space as the depth attachment the resolve
  compares it against (`gl_FragCoord.z`), never NDC depth. The resolve treats a pixel as validly
  written only when `a` matches the final depth buffer within
  `abs(motion.a - depth) <= max(2e-4, 8e-4 * depth)`. The tolerance is half-float aware: the
  attachment is RGBA16F, whose ULP near 1.0 is already ~5e-4, so a fixed absolute epsilon rejects
  every legitimate distant writer; the relative term covers precision and the floor covers depths
  near the near plane. Where the test fails the resolve uses the camera-motion fallback
  (static-surface reprojection from depth, infinite-direction reprojection where depth == 1). This
  defines behaviour for unknown writers, mod geometry and sky without relying on undefined
  unwritten-output contents.
- Blend state: passes that blend colour (particle cubes, `SystemRenderParticles.cs:132`) set the
  motion attachment to replace blending via `SetBlendFuncSeparate(MV_LOCATION, 1, 0, 1, 0)` (the
  seam already exposes per-attachment blend; OIT uses it). Fullscreen resolve/sharpen passes set
  blend off, depth test/write off, viewport and target explicitly, then restore.
- Reactive: opaque writers 0; alpha-tested foliage 0; OIT transparents `1 - revealage` from the merge;
  particles 1 via their own writer (quad particles in OIT are covered by revealage only, cube
  particles write directly); animated liquid textures 0.3 initial, tuned by measurement; the value
  lowers history weight in the resolve and maps to the FSR reactive / XeSS responsive mask later.
- Reset: world load, dimension change, teleport (camera delta above a threshold), reference-position
  rebase (`PlayerCamera.cs:74-76`), window resize or zero size, SSAO/shader reload, render-scale or
  FOV change, TAA toggle, mega-screenshot capture. Reset clears both history sets and marks the
  frame in the contract with a reason.

## Motion vectors: accuracy rules (all consumers depend on these)

1. Compute in the same draw as the colour, per pixel, perspective-correct (previous clip position
   interpolated, divided in the fragment shader). Apply the `chunkopaque.vsh` z-fighting w-offset
   identically to the previous clip position.
2. Previous position uses the same code path with previous inputs: previous model matrix, previous
   per-view view matrix, previous unjittered projection (world or hand FOV), previous warp state,
   previous bone matrices. Keep the previous *rendered* state, not the previous simulation tick;
   add history-valid flags for spawn, mesh/animator change, reappearance, first/third person switch.
3. Vertex animation: evaluate the `vertexwarp.vsh` functions twice through a `WarpState` struct that
   carries every uniform they read: `timeCounter`, `windWaveCounter`, `windWaveCounterHighFreq`,
   `waterWaveCounter`, `windSpeed`, `playerpos`, `globalWarpIntensity`, `glitchWaviness`,
   `windWaveIntensity`, `waterWaveIntensity`, `perceptionEffectId`, `perceptionEffectIntensity`.
   Some vary per entity or per pass (`EntityShapeRenderer.cs:641`, cloud perception multiplier), so
   previous values are captured per draw class, not only globally. Counters wrap
   (`DefaultShaderUniforms.cs:153-168`): store actual previous values, never current minus dt.
4. Terrain: `prevRel = truePos + (cameraPos_cur - cameraPos_prev)` using the exact
   `EntityPlayer.CameraPos` delta in double precision, then `prevAbsForWarp = prevRel + prevPlayerpos`
   (position relative to the slowly rebased reference, which is what the warp noise consumes), then
   `prevClip = prevProjUnjittered * prevCameraMatrixOrigin * warpPrev(prevRel)`.
5. Skinned entities: previous model matrix and previous bone matrices per renderer, skinned twice.
   Held items (standard shader), first-person hands (own program and FOV, `EntityPlayerShapeRenderer.cs:255-308`,
   `ModSystemFpHands.cs:24-38`), EchoChamber, dropped items (`EntityItemRenderer.cs`), quern/gears
   (`QuernTopRenderer.cs`, `MechNetworkRenderer.cs` instanced) each get their own previous-transform
   store and shader writer; instanced renderers keep previous instance transforms with stable identity.
6. Static world under camera motion is exact through rule 4; the resolve's camera fallback covers
   only depth-writing static surfaces and sky, and is labelled a fallback.
7. Liquid: the OIT liquid draw cannot write Primary's motion attachment (six OIT outputs already), so
   a dedicated liquid velocity pass re-draws liquid into the motion attachment with depth test against
   Primary depth and writes the surface's motion and depth. Foam/flow UV animation stays reactive.
8. Screen-space effects: SSAO uses the jittered projection matching its jittered G-buffer
   (`ssao.fsh:119-127` projects samples and reads gPosition); bloom/god rays/Final read resolved signals.
9. Validation must use frozen scenes with tolerances plus known directional displacements; "zero
   everywhere with wind blowing" is wrong (swaying leaves have real motion) and "zero" cannot
   distinguish a sign or axis error.

## Render-system inventory (maintained in the plan and as a test table)

| Class | Stage / target | Shader | Motion policy |
|---|---|---|---|
| Chunk opaque / topsoil / pass-7 overlay | Opaque, AfterOIT / Primary | chunkopaque, chunktopsoil | exact (P3) |
| Liquid | OIT / Transparent | chunkliquid | exact via liquid velocity pass (P4) + reactive foam |
| LiquidDepth prepass | Before / LiquidDepth quarter res | chunkliquiddepth | jittered NDC shear, no motion |
| Entities (skinned) | Opaque, OIT, AfterOIT / Primary, Transparent | entityanimated(_oit) | exact opaque (P3); OIT reactive |
| Held items, dropped items, block-entity models | Opaque / Primary | standard | exact (P3, standard writer) |
| First-person hands | Opaque / Primary, hand FOV, depthOffset | fp hands program | exact with hand-FOV previous (P3) |
| Instanced mechanical power | Opaque / Primary | instanced | exact with previous instance transforms (P3) |
| Particles cube | Opaque / Primary, blend on | particlescube | reactive 1, replace-blend on motion (P4) |
| Particles quad | OIT / Transparent | particlesquad | reactive via revealage (P4) |
| Clouds (volumetric, map), aurora, night sky, sun/moon, sky colour | OIT/Opaque | dedicated | fallback + reactive; sky uses infinite-direction reprojection (P4) |
| Decals | AfterOIT / Primary | decal shader | inherits surface motion; crack progress rejected by colour clipping (P4) |
| Work-item guides, selection boxes, wireframes | AfterFinalComposition / Primary | various | outside window, unjittered (P1) |
| Rifts | AfterBlit / Default | rift | outside window; noted as FG gap |
| Mod geometry via `IRenderAPI` | any | any | fallback via writerDepth mismatch; opt-in writer API later |

## Frame-generation and ray-reconstruction readiness (constraints, not built here)
- HUD-less colour exists today: Primary after Final, before the blit; the HUD is drawn into the
  default framebuffer afterwards, window-sized. Rifts (AfterBlit) and late guides are world content
  outside that image; FG needs them moved before the boundary or composited after. The UI later needs
  its own alpha target with defined premultiplication.
- Camera constants captured per frame: unjittered view/projection and previous, camera-relative
  origin, near/far, vertical FOV, position/forward/up/right, jitter px, frame id (+1 per real frame,
  distinct from generated/present ids), delta time ms, reset reason, render/display rects.
- Vendor libraries need native handles, negotiated extensions at device creation (`VulkanContext.cs:64,570-573`),
  completion-based resource lifetimes past Present, and a replaceable present path; that is a separate
  backend-native capability interface in the vendor plan. XeSS-FG and AMD Ray Regeneration are
  D3D12-only today; DLSS SR/FG/RR and FSR 3.1 have Vulkan paths.
- Ray reconstruction needs linear HDR noisy colour, separate diffuse and specular albedo, normals +
  roughness, specular motion or hit distance; Optimum's SSAO gnormal/gposition are not those guides.
  This plan only keeps the attachment scheme and the resolver input record extensible; an HDR path
  and PBR guides are a later renderer change.

## Implementation phases

Delivery rules for every phase: methods changed in `ClientMain`, `ClientPlatformWindows`, `ChunkRenderer`,
`ShaderRegistry`, `ShaderProgramEntityanimated`, `ScreenManager` go into `Optimum.Patcher/Program.cs`
(new members need injection entries, not only transplants); the patcher's check verifies references,
not behaviour, so each phase also runs `make deploy` and the game. New API files go into
`optimum-api-contracts.csproj` and the api-patcher export list. Mod-fork changes (VSEssentials,
VSSurvivalMod) need `mod-patcher` manifests for the installed-runtime path. New shader includes ship
via `sources/shaderincludes/` plus `make deploy` and all `scripts/package-*` copies, and get
`ShaderCompatibilityScanner` rules (`Optimum.Launcher/ShaderCompatibilityScanner.cs:22-26,294-309`).

**P0. Prerequisites (no TAA yet).**
- Fix Vulkan named uniform blocks: `UpdateUniformBuffer` overwrites one mapped allocation and every
  draw binds that allocation (`VulkanDevice.cs:1081-1087,1931-1942`), so all entities in a frame read
  the last uploaded bones. Snapshot named blocks per draw into the frame ring with dynamic offsets,
  as ordinary uniform blocks already are (`:1894-1925`); fix ring exhaustion (`:1908-1918`) to fail
  loudly. Tests: two entities with different poses in one command buffer; consecutive frames.
- Deterministic attachment writes: enable the motion attachment only for passes that write it
  (draw-buffer mask), define replace-blend for it, and add a device test for sparse outputs with five
  attachments, omitted fragment outputs and independent blending on both backends.
- Typed texture readback: `DumpRequestedTextures` allocates `w*h*4` bytes regardless of format
  (`VulkanDevice.cs:2341-2367`); make readback format-aware so RGBA16F/R32F targets can be dumped.
- Build the render-system inventory above as a checked test (renderer, stage, target, shader), and
  the sign/adapter unit tests (shear, mv, one-pixel adapter for FSR/XeSS/DLSS scales).
- Verify: Vulkan tests, `make deploy`, game runs identically with several animals in view on Vulkan
  (the UBO fix is visible), texture dump of an RGBA16F target round-trips.

P0 status (2026-09-10): done in commit on `feat/taa`. Findings to carry: (a) `VulkanDevice.BindFramebuffer`,
`BindDefaultFramebuffer` and `ClearColor` are no-ops outside an active frame, so between-frame readbacks
silently read the last bound target; readback code and tests must run inside a frame until that is
fixed. (b) `maxDescriptorSetUniformBuffersDynamic` is not read; more than seven named blocks in one
program would fail on a minimum-spec device (none exist). (c) `_uniformBuffers` is mutated from the
finalizer thread by `DeleteUniformBuffer` while the render thread reads it; pre-existing.

**P1. Frame contract, jitter (dev switch), camera reprojection, debug views. TAA default off.**
- `VintagestoryApi/Client/Render/OptimumTemporalFrame.cs` (immutable per-frame record + `Advance()`),
  captured in `MainRenderLoop` after `shUniforms.Update` and per view at the matrix loads; previous
  values for world and hand projections, `CameraMatrix`, `CameraMatrixOrigin`, camera position delta
  (double), `playerpos`, full `WarpState`. `OptimumConfig`: `Taa`, `TaaSharpness`, `TaaMipBias`,
  `TaaDebugView`, `EffectiveTaa`; `TaaJitterDev` hidden switch.
- Jitter through the `CurrentProjectionMatrix` getter within the temporal window only; separate
  jittered array; `CurrentProjectionMatrixUnjittered`; read-only temporal context exposed through a
  companion interface so mods and `RenderAPIGame` can read it without breaking `IRenderAPI`
  implementers.
- Primary motion attachment (both setup paths, clear, disposal, draw-buffer masks) and history/aux/
  prev-depth targets bound via `LoadFrameBuffer(FrameBufferRef, int)` with a dispose/recreate
  lifecycle; no enum dispatch changes.
- Debug: motion/reactive/validity/rejection views in `BlitPrimaryToDefault`; test scene protocol:
  frozen scene tolerance, +X/+Y/rotation/near-plane displacements on GL and Vulkan.

**P2. Minimal resolve early.**
- `taa-resolve` MRT (colour, glow+ssao aux, linear depth) with reset, validity via writerDepth,
  camera fallback, depth/velocity rejection, YCoCg variance clipping, luma weighting, Catmull-Rom for
  colour and nearest for depth/validity; explicit blend/depth/viewport state. Luma aliases the resolved
  colour; bloom/god rays/Final rebound to resolved textures. This makes every later producer's
  failure visible instead of being confused with raw jitter differences.
- Ordering in P2 only: the resolve runs at the top of `RenderPostprocessingEffects`, which is
  *before* the SSAO pass, not after it as Decision 1 requires. SSAO therefore stays exactly where it
  is - computed from the jittered G-buffer and consumed by Final from `frameBuffers[14]` - and the
  aux target's `b` (SSAO) channel is allocated but written as zero and read by nobody. Resolving
  SSAO temporally, and with it moving the SSAO pass in front of the resolve so Final reads a
  resolved occlusion term, is deferred: it needs the SSAO output routed through the resolve's MRT
  and Final rebound, which is a separate change from getting colour and glow stable. P2 rebinds only
  colour and glow (bloom, god rays, Final's `PrimaryScene2D`/`GlowParts2D`); Final's `SsaoScene2D`
  is untouched.
- History targets are LINEAR-filtered on colour and glow (the reprojected read is fractional) and
  NEAREST on linear depth (interpolating across a silhouette invents a depth on neither surface),
  on both the GL and the device path. A framebuffer rebuild invalidates the history and raises
  `EnumTemporalResetReason.Resize`; the resolve additionally treats a NaN/Inf history sample as a
  reset, because freshly allocated slots hold undefined contents and NaN survives any blend.
- Tests: GPU harness on `WorldRenderPathTests` with synthetic inputs (static convergence, known
  offset reprojection, outlier clip, reset); coverage test for ordering and FXAA-off; in-game the
  whole scene converges with camera-only motion vectors.

**P3. Opaque coverage.**
- `sources/shaderincludes/vertexwarp.vsh` with `WarpState`; `chunkopaque`, `chunktopsoil` writers;
  `standard.vsh/.fsh` writer (items, block entities, dropped items, quern) with previous transforms
  in `EntityShapeRenderer` (item and body), `EntityItemRenderer`, `QuernTopRenderer`; `entityanimated`
  writer with previous bones and model matrix (`AnimationPrev` UBO from `initUbos`, FP hands' own
  UBO in `ModSystemFpHands`, `EchoChamberRenderer`); instanced mech-network previous instance
  transforms (`MechNetworkRenderer`, `GenericMechBlockRenderer`); hand-FOV previous projection and
  depthOffset handling. Uniforms set in `ChunkRenderer.RenderOpaque/RenderAfterOIT` and the
  LiquidDepth prepass.
- Verify per class with the debug views and directional tests; measure vertex-warp cost on dense
  foliage and the UBO snapshot cost with crowds.

**P4. Transparency, particles, volumetrics, sky, decals, late overlays.**
- Liquid velocity pass; OIT revealage reactive; particle writers; cloud/aurora/sky policies with the
  infinite-direction reprojection; decals inherit motion; AfterFinalComposition/AfterBlit content
  verified outside the window. State per class exact vs fallback in the inventory test.

**P5. Integration, sharpen, settings, fallback, acceptance.**
- RCAS variant with a sharpness uniform and true bypass; no double sharpening with FSR1 render
  scale; `TaaMipBias` optional and measured; settings rows in `GuiCompositeSettings.cs.patch`;
  runtime fallback to FXAA when compile/FBO creation fails; scanner rules; packaging.
- Acceptance matrix on both backends: moving silhouettes on contrast, transparent foreground and
  background motion, thin fences, hand/world FOV, quern/gear, dropped items, fire, rain, clouds,
  aurora, underwater transitions, camera modes (shake, third person, mounted), reference rebase,
  chunk replacement, shader reload, missing-resource fallback, normal/scaled/mega screenshots (mega
  capture uses warm-up or spatial-only).
- Performance on the Arc 140V: total frame delta, GPU pass timestamps, CPU frame time, 1% lows,
  with renderer name, power mode and thermals recorded. Memory at 1080p: motion 15.8 MiB + two colour
  histories 31.6 MiB + aux 7.9 MiB + prev-depth 15.8 MiB.
- Decide default-on only after the matrix passes.

**P6. Freeze the contract.**
- Document the immutable frame input record, resource formats/conventions, per-class motion status
  and the adapter tests; reserve backend-native execution, presentation lifetime and extra ray
  signals for the vendor plan.

## Verification (end to end)
1. `dotnet build VintageStory.slnx -c Release`; `dotnet test Optimum.Tests`;
   `dotnet test Optimum.Render.Vulkan.Tests`; `scripts/check-patches.sh`; patcher run.
2. `make deploy`; run on Vulkan and OpenGL (`Renderer` in
   `~/.config/OptimumVintagestoryData/ModConfig/optimum.json`) with `prime-run`, confirming the
   selected GPU in the log.
3. Debug views and the directional/frozen-scene protocol on both backends after each phase.
4. Acceptance matrix in P5; TAA off must be byte-identical to today's chain.

## Sources
- https://alextardif.com/TAA.html
- https://www.elopezr.com/temporal-aa-and-the-quest-for-the-holy-trail/
- https://interplayoflight.wordpress.com/2020/05/30/a-survey-of-temporal-antialiasing-techniques-presentation-notes/
- https://github.com/playdeadgames/temporal
- https://github.com/GameTechDev/TAA
- https://github.com/DiligentGraphics/DiligentFX/tree/master/PostProcess/TemporalAntiAliasing
- https://gpuopen.com/manuals/fsr_sdk/techniques/super-resolution-upscaler/
- https://github.com/GPUOpen-Effects/FidelityFX-FSR2 (README "Placement in the frame")
- https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-interpolation/
- https://gpuopen.com/amd-fsr-rayregeneration/
- https://github.com/GPUOpen-LibrariesAndSDKs/Capsaicin
- https://gpuopen.com/learn/fsr-2-1-unreal-engine-plugin-part1/
- https://juandiegomontoya.github.io/porting_fsr2.html
- https://github.com/BoyBaykiller/FidelityFX-FSR2-CSharpBindings
- https://github.com/intel/xess/blob/main/doc/xess_sr_developer_guide_english.md
- https://github.com/intel/xess/blob/main/doc/xess_fg_developer_guide_english.md
- https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS.md
- https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS_G.md
- https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS_RR.md
- https://dev.epicgames.com/documentation/unreal-engine/temporal-upscalers-in-unreal-engine
- https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@16.0/manual/features/motion-vectors.html
- https://ogldev.org/www/tutorial41/tutorial41.html
- https://github.com/godotengine/godot/pull/61319
- https://mods.vintagestory.at/show/mod/35005
