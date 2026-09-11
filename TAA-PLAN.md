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
| Liquid | OIT / Transparent, motion via a second Primary pass | chunkliquid, chunkliquidmotion | exact (P4): dedicated velocity pass, depth test AND write on, reactive 0.3 |
| LiquidDepth prepass | Before / LiquidDepth quarter res | chunkliquiddepth | jittered NDC shear, no motion |
| Entities (skinned) | Opaque, OIT, AfterOIT / Primary, Transparent | entityanimated(_oit) | exact opaque (P3); OIT gets the merge's `1 - revealage` reactive only (P4) |
| Held items, dropped items, block-entity models | Opaque / Primary | standard | exact (P3, standard writer) |
| First-person hands | Opaque / Primary, hand FOV, depthOffset | fp hands program | exact with hand-FOV previous (P3) |
| Instanced mechanical power | Opaque / Primary | instanced | exact with previous instance transforms (P3) |
| Particles cube | Opaque / Primary, blend on | particlescube | camera-only vector, reactive 1, replace-blend on motion (P4) |
| Particles quad | OIT / Transparent | particlesquad | no vector; reactive `1 - revealage` added by the merge (P4) |
| Night sky, sun/moon, sky colour | Opaque / Primary, no depth write | nightsky, sky, celestialobject, standard | no writer needed: depth stays 1, the resolve's infinite-direction fallback is exact (P4, verified) |
| Clouds (volumetric), aurora | OIT / Transparent | cloudvolumetric, aurora, taa-skymotion | camera-rotation-only vector + coverage-gated reactive from `taa-skymotion` on the sky pixels; no reactive of their own where they sit in front of terrain (P4) |
| Decals | AfterOIT / Primary | decals | exact: writes the terrain previous path itself, with its own depth, because the z-offset moves the depth buffer out from under the block's writer depth; crack progress rejected by colour clipping (P4) |
| Work-item guides, selection boxes, wireframes | AfterFinalComposition / Primary | various | outside window, unjittered (P1); the motion window is refused there on `JitterActive` (P4) |
| Rifts | AfterBlit / Default | rift | outside window, default framebuffer, motion window refused; noted as FG gap (P4) |
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

P2 status (2026-09-10): done and accepted in game by the user on both backends ("TAA is CHEFSKISS now").
Commits 1257117..9c32acb on `feat/taa`. Findings to carry: (a) Vulkan `GlEnums` lacked GL_R32F, so the
history depth target silently became RGBA8 (b4d58a2); (b) `ClearColor` on a masked-out attachment is a
no-op on Vulkan, so the motion clear must enable the attachment first (8e4a970); (c) the history lookup
must be anchored at the pixel centre plus mv, not at the unjittered current position (7e1b9bd);
(d) matched-camera luminance-diff measurements (Codex) are the acceptance tool for "jitter" reports.

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

P3 status (2026-09-10): writers landed for all five opaque classes on `feat/taa`
(ce3cc1f terrain, abba239 skinned entities, c6da92f standard shader, 3126098 instanced,
58bc11d review fixes). **Not verified in game on either backend** - no phase of P3 ran
`make deploy` or the client, so by rule 3 none of this is done until someone deploys, runs
on Vulkan and OpenGL, confirms the renderer from the log and compares the debug views.
GPU proof is Vulkan-only (`Optimum.Render.Vulkan.Tests` is the only GPU harness), and every
GPU test so far runs with `taaJitterPx = 0`, so the jittered case is untested everywhere.

Exact vs fallback, per class:

| Class | Status | Why |
|---|---|---|
| chunkopaque (passes 0, 1, 2, 8) and chunktopsoil | exact | prevRel = truePos + cameraPosDelta, warp replayed with `previousWarpState()`, z-offset applied to both clips |
| chunkopaque pass 7 (AfterOIT overlay) | exact | own window in `RenderAfterOIT` |
| LiquidDepth prepass | no motion, by design | own target, no writer, attachment never in its mask |
| Skinned entities, batched opaque pass | exact | previous model matrix + `AnimationPrev` bones, hooked on the one bone upload every entity draw makes |
| First-person hands, echo chamber | exact | own programs/windows; hands reproject through `GetPrevProjection(Hand)` |
| Skinned entities, OIT | no motion | six OIT outputs on Transparent; reactive policy is P4 |
| Skinned entities, AfterOIT (`DoRender3DAfterOIT`) | fallback | that loop draws arbitrary per-renderer shaders, so it must stay outside a window |
| Held items (both hands + FP item), dropped items, quern top | exact | `OptimumStandardMotion.Apply` + a narrow window per draw |
| Every other standard-shader user | fallback | uninstrumented and outside the window, so nothing is written and the resolve camera-reprojects. Exact for the static ones (signs, molds, knapping, ground storage, support beams); **wrong-but-bounded for the moving ones**: HelveHammer, FruitpressContents, Resonator, EntityBlockFalling, Bloomery/Forge/Firepit contents will ghost until instrumented (one `Apply` call plus a `Begin`/`End` pair each) |
| Instanced mechanical power | exact | per-instance previous transform + metadata in the instance stream, history keyed on the device object |
| ClothManager (shares the instanced program) | fallback | 20-float instance mesh, draws outside the window; its missing attributes read (0,0,0,1), i.e. no history |
| Mod geometry | fallback | writer-depth mismatch, as designed |

Findings to carry:

(a) **Where the frame contract reads the camera decides whether the delta is this frame's.**
`PlayerCamera.OnBeforeRenderFrame3D` is the only writer of `EntityPlayer.CameraPos` and
`shUniforms.PlayerPos`, and it runs inside the Before render stage - after `Advance`, before
`CaptureCamera`. Reading them in `Advance` paired a one-frame-stale translation with a fresh
previous rotation; the difference is the camera's acceleration, and it painted motion onto
static ground. `CaptureCameraPosition` now takes both next to `CaptureCamera` (58bc11d). Any
future value the contract snapshots has to be placed against the stage that writes it, not
against the top of the loop.

(b) **A draw-buffer window is per target, and the two backends disagree about that for free.**
`SetDrawBuffers` names the framebuffer, `GL.DrawBuffers` uses the bound one. `BeginMotionWrite`
now refuses unless Primary is bound.

(c) **A new asset directory needs every packager, and the list must be derived.** Two of the five
packaging scripts were missed; with TAA on those builds fail to compile every writer program,
because the shipped overrides call `WarpState` overloads the vanilla `vertexwarp.vsh` does not
declare. The coverage test now enumerates `scripts/package*` instead of listing three by name.

(d) **The shader corpus only covers configurations its variant rows produce.** `USEOIT 0` and
`ALLOWDEPTHOFFSET` are stamped per program by the client, not globally, so the entity writer and
the `gl_FragCoord.z + depthOffset` writer depth were outside the translation gate entirely.
`ShaderVariant.ExtraPrefix` plus explicit per-program cases now cover them.

(e) **Instance buffers doubled in stride unconditionally** (20 -> 40 floats), TAA on or off,
because shaders recompile on a TAA toggle and instance buffers do not. Roughly 1.6 MB per mech
buffer. Deliberate; revisit only together with a buffer-rebuild-on-toggle.

(f) **Installed-runtime gap, all three mod-fork stages.** `mod-patcher` transplants from the
runtime donor assemblies patched by `patches/runtime/**`, not from the `VSEssentials`/
`VSSurvivalMod` forks. `Methods` entries were added, but until the matching
`patches/runtime/**` patches exist the installed runtime keeps the vanilla bodies, so first-person
hands, the echo chamber, held/dropped items, the quern and every mech renderer get no motion
there (camera fallback; for the mech renderers also the 20-float layout under a 40-float shader,
whose unbacked attributes read (0,0,0,1) = no history - believed safe, untested).
`Optimum.Tests/mod-patcher-manifest-consistency-tests.cs` only cross-checks `Members`/`Types`/
`Interfaces`, which is why the additions pass today.

(g) **`Entityanimated_Oit` compiles the `AnimationPrev` block with no buffer behind it.**
`USEOIT` is a fragment-only define, so the vertex shader cannot gate on it. On GL the block keeps
the default binding point 0 and aliases `Animation`; on Vulkan `_boundUniformBuffers` is keyed by
block name and `Use()` re-binds, so it resolves to the last-bound buffer of that name. Never read,
because `taaHistoryValid` is never set for that program and defaults to 0. Harmless, but it is a
declared-and-unfed block.

(h) **`reactive` is read by the resolve whether or not the pixel was written.** A rejected pixel
still contributes `motion.b` from whatever surface last wrote there this frame. Bounded (the
attachment is cleared to zero each frame), worth a look when P4 starts writing reactive in anger.

(i) **Per-instance CWT churn.** `OptimumInstanceMotion.WriteInstance` does two
`ConditionalWeakTable` lookups per instance per frame (buffer, then device). Fine at realistic
gear counts, measurable at the 10100-instance capacity. Not optimised, because a single-slot memo
would hold a strong reference to a ~1.6 MB buffer.

(j) **The vertex-warp and UBO cost measurements P3 asks for were not taken** - no crowd, no dense
foliage, no gear-network numbers. Still owed before P5's performance matrix.

**P4. Transparency, particles, volumetrics, sky, decals, late overlays.**
- Liquid velocity pass; OIT revealage reactive; particle writers; cloud/aurora/sky policies with the
  infinite-direction reprojection; decals inherit motion; AfterFinalComposition/AfterBlit content
  verified outside the window. State per class exact vs fallback in the inventory test.

P4 status, sky / volumetrics / decals / late overlays (2026-09-10): landed on `feat/taa`.
**Not verified in game on either backend** - no phase of P4 ran `make deploy` or the client, so
by rule 3 none of it is done. GPU proof is Vulkan-only.

| Class | Status | Why |
|---|---|---|
| Sky colour, night sky | fallback, and exact | depth test off for the whole pass, so depth stays 1 and the resolve's infinite-direction reprojection is the right answer; no writer, by design |
| Sun, moon, celestial objects | fallback, bounded | depth tested but never written (`GlDepthMask(false)`), so the same fallback applies; it ignores the celestial rotation itself, which is ~0.004 deg per frame |
| Volumetric clouds, aurora | vector exact for the camera, reactive by coverage | drawn into Transparent, so they cannot write Primary's attachment; the new `taa-skymotion` pass claims the sky pixels (depth 1, GL_LEQUAL, depth writes off), writes the rotation-only vector and `mix(coverage, taaCloudReactive, coverage)` from the Transparent revealage. Their own scrolling is not in the vector - the reactive value is what stops the smear |
| Clear sky under a cloudless view | exact, full history | coverage 0 means reactive 0, so the dithered gradient keeps converging |
| Decals | exact | own writer: terrain previous path + `previousWarpState()` + both z-offsets, with `a = gl_FragCoord.z`, which is what the decal itself puts in the depth buffer |
| AfterFinalComposition overlays | outside the window | jitter closed in `RenderAfterPostProcessing`; `BeginMotionWrite` now refuses on `JitterActive` |
| Rifts (AfterBlit) | outside the window | default framebuffer; the Primary-is-bound guard refuses on its own. Still the FG gap the plan records |

Findings to carry:

(k) **Clouds were already getting a reactive value, from the merge.** The particle stage recorded
that they were not, because `SystemRenderOITLayers` rebinds Transparent's attachment **0** to its
private revealage texture. Attachment **1** - `oit.fsh`'s `outReveal`, the one
`transparentcompose.Revealage2D` reads - is untouched by that rebind, and `cloudvolumetric.fsh`
writes `outReveal = vec4(1.0 - k.a)` into it under the multiplicative blend factors `BeforeOIT`
sets. So cloud coverage does reach `anet`. What the sky pass adds is a reactive value at the
sky's own strength rather than the cloud's alpha, plus an explicit vector and writer depth.

(l) **`BeginMotionWrite` had no window guard, only a target guard.** `RenderFinalComposition`
leaves Primary bound, so an `AfterFinalComposition` renderer could have opened a motion window
after the resolve had already read the attachment. It is now refused on
`OptimumTemporal.Frame.JitterActive`, which is the temporal window itself.

(m) **The sky pass needs GL_LEQUAL and the client runs GL_LESS.** A fullscreen triangle at the far
plane draws nothing at all under the default. The pass sets and restores the depth func through
`GlDepthFunc`; anything else that ever wants to draw at exactly the far plane has the same problem.

(n) **The cloud vector is camera-only.** `taa-skymotion` reprojects the view direction, not the
cloud: a cloud scrolling across a still camera has mv 0 and is carried entirely by the reactive
value. That is correct for the resolve (reactive 1 discards the history) but it is wrong data for
the later consumers the plan is built for - FSR/XeSS reactive+mv, and frame generation especially.
A real cloud vector needs `cloudOffset`'s previous value and the ray-marched hit position, i.e. a
motion output from `cloudvolumetric.fsh` itself, which cannot reach Primary's attachment without a
second pass over the cloud volume.

(o) **Decals near the camera were the actual bug.** With the block's vector left in place, the
decal's z-offset moves the depth buffer by ~1.3e-3 in window depth at one block's distance against
a tolerance of ~7.2e-4, so every close decal silently demoted its pixel to the camera fallback.
Mid- and far-range decals stayed inside the tolerance, which is why "leave it untouched" looks
correct until you measure it.

(p) **The GL path of the new pass has never executed.** `Optimum.Render.Vulkan.Tests` is the only
GPU harness; the sky pass's GL branch is the shared `GlDepthFunc`/`GlToggleBlend` helpers plus
`BeginMotionOnlyWrite`'s existing GL branch, all of which are still unproven on OpenGL.

P4 status, movers (the P3 carry-over) (2026-09-10): landed on `feat/taa`.
**Not verified in game on either backend** - no phase of P4 ran `make deploy` or the client.
Every standard-shader user in the mod forks now either writes motion or is on an explicit
exemption list with a reason, enumerated by scan in
`Optimum.Tests/taa-mover-motion-coverage-tests.cs` rather than listed by hand.

| Class | Status | Why |
|---|---|---|
| Helve hammer, resonator disc, fruitpress mash, pot lid | exact | continuous per-frame animation; `OptimumStandardMotion.Apply` keyed on the renderer (the pot lid on `lidRef`, because the pot body already holds the renderer's key) plus a narrow `Begin`/`End` window |
| Bloomery, forge and firepit contents | exact | their model matrices track fuel level, voxel height and the cooking transform, all of which move between frames |
| Falling blocks | exact | history keyed on the `EntityBlockFalling` entity, not on the renderer, which is shared by every falling block in view; one window around the whole loop |
| Anvil parts, molds, signs, chest labels, knapping, clay forming, ground storage, crucible, support-beam preview | fallback, and exact | static in the world, so the resolve's camera reprojection is the right answer; each carries a written reason on the exemption list |
| Forge work item, anvil work item | fallback | drawn on the mod's own `smithingWorkItemShader`, which declares no motion output at all; a writer there is a separate shader override |

Findings to carry:

(q) **One renderer can be two drawn things.** `PotInFirepitRenderer` draws a static pot body and a
rattling lid from one `OnRenderFrame`, through one `Matrixf`. Keying both on `this` would have
handed the lid the body's previous matrix - a zero vector on the only part of the pot that moves -
and the `ConditionalWeakTable` would have silently accepted it. The identity has to mean "this
drawn thing", not "this renderer": the lid keys on `lidRef`.

(r) **A shared renderer must key on the drawn object.** `ModSystemRenderFallingBlocksFast` is one
`IRenderer` for every falling block in view, so its identity is the entity. The window, by
contrast, is per target and not per draw, so it opens once around the loop.

(s) **The jittered case is now covered, and it needed a perspective-shaped projection.**
`Optimum.Render.Vulkan.Tests/TaaMoverMotionTests` drives the standard writer with
`taaJitterPx != 0`, which every P3 GPU test left at zero. With the identity projection those tests
use, the NDC shear `P[8] -= 2*jx/W` is a no-op on a quad at z = 0, so a jittered case there would
have asserted nothing. Zeroing `taaJitterPx` while leaving the projection sheared fails 7 of the 8
new cases, which is what makes them evidence.

(t) **P3 finding (f) is unchanged and now covers eight more methods.** `mod-patcher` `Methods`
entries were added for every mover, but `patches/runtime/**` still has no donor for any of them,
so the installed runtime keeps the vanilla bodies and every one of these renderers ghosts there
while the build tree is correct. `check-patches.sh` reports 0 problems either way.

P4 status, whole phase, after the adversarial review (2026-09-10): landed on `feat/taa`
(8f64e11 liquid, 13d9eb3 particles, 957f4e0 sky/clouds/decals/late overlays, 8d09ef1 movers,
b1c293f review fixes). **Not verified in game on either backend** - no phase of P4 ran
`make deploy` or the client, so by rule 3 none of it is done. GPU proof is Vulkan-only
(`Optimum.Render.Vulkan.Tests` is the only GPU harness), and every GL branch added in this phase
has never executed.

Exact vs fallback vs reactive, per class (the inventory table above is the ships-with-it form):

| Class | Vector | Reactive | Why |
|---|---|---|---|
| Liquid surfaces | exact (P4) | 0.3, constant | `chunkliquidmotion` re-draws the liquid pools into Primary through the motion-only window, replaying `chunkliquid.vsh`'s position path verbatim - same liquid warp through `applyLiquidWarpingState`, same `w += 0.0008/max(0.1, z)` offset on both clips. Depth test GL_LESS and depth **write on**, so `a` matches the buffer and the resolve accepts the pixel |
| Cube particles | fallback, camera-only | 1 | the instance stream carries position and scale only (stride 16, sized by MaxCubeParticles for four pools), so there is no previous per-particle position; reactive 1 makes the resolve ignore the history anyway. Wrong data for FSR/XeSS mv and for frame generation |
| Quad particles, OIT entities, liquid shading, aurora | none | `1 - revealage`, additive | six oit.fsh outputs already fill Transparent; the merge adds `anet` into `b` alone under FUNC_ADD (ONE, ONE) with rg and a written as zero, so the opaque vector underneath survives bit-for-bit |
| Sky colour, night sky | none, by design | 0 | depth test off for the whole pass, so depth stays 1 and the resolve's infinite-direction fallback is the exact answer |
| Sun, moon, celestial objects | none, by design | 0 | depth tested, never written (`GlDepthMask(false)`); the fallback ignores only the celestial rotation, ~0.004 deg per frame |
| Volumetric clouds | camera-rotation-only | `mix(coverage, 1, coverage)` on sky pixels | `taa-skymotion` claims depth-1 pixels under GL_LEQUAL and writes rg, b and `a = 1.0`. A cloud in front of terrain keeps the terrain's vector and gets only the merge's `anet` |
| Clear sky | exact | 0 | coverage 0, so the dithered gradient keeps full history weight |
| Decals | exact (P4) | 0 | own writer: chunk previous path + `previousWarpState()` + both z-offsets, `a = gl_FragCoord.z`. Crack progress is left to the resolve's colour clipping |
| Helve hammer, resonator, fruitpress, pot lid, bloomery/forge/firepit contents, falling blocks | exact (P4) | 0 | `OptimumStandardMotion.Apply` keyed on the drawn thing plus a narrow window |
| Static standard-shader users (anvil parts, molds, signs, knapping, clay forming, ground storage, ...) | fallback | 0 | camera reprojection is the right answer; each on the scanned exemption list with a reason |
| Forge/anvil work items | fallback | 0 | the mod's own `smithingWorkItemShader` declares no motion output |
| AfterFinalComposition overlays, AfterBlit rifts | none | none | outside the temporal window; `BeginMotionWrite`/`BeginMotionOnlyWrite` refuse on `JitterActive` |

Findings to carry:

(u) **A writer that bails out must still deliver its reactive value.** The liquid and cube-particle
writers wrote a flat `vec4(0.0)` whenever the previous clip position landed behind the previous
camera. `taa-resolve.fsh` reads `motion.b` whether or not the writer-depth test accepted the pixel
(finding (h)), so that gave an animating water surface or a particle FULL history weight in exactly
the frames the camera swung hardest. Both now keep `b` and zero only the vector and the alpha, as
`taa-skymotion.fsh` already did. GPU regression:
`TaaLiquidMotionTests.APreviousPositionBehindThePreviousCameraStillCarriesTheReactiveValue`.

(v) **The translation gate only covers combinations some corpus row produces, and P4 added three
it did not.** `decals` with `USESSBO 1` (the branch where `vertexPos` and `renderFlagsIn` are
locals unpacked from the face buffer - the one the client actually runs, `UseSSBOs` defaults on),
`particlescube` with `VEC3SCALE 1` (stamped by `VSEssentials`' `EntityParticleSystem`, so **not**
the dead branch the particle stage recorded), and any writer at `WAVINGSTUFF 0`, which gates the
body of every `vertexwarp` function the writers replay. All three are now in
`ShaderTranslationTests`, the third as a `taa-no-waving` corpus row that applies to every program,
and coverage tests pin the two explicit cases so they cannot be dropped as redundant.

(w) **The liquid velocity pass writes depth into Primary, and more things read that than the
liquid stage recorded.** Besides the AfterFinalComposition overlays (a block outline on a submerged
block is now occluded by the water surface), the SSAO **bilateral blur** takes its depth-guided
weights from `frameBuffers[0].DepthTextureId`, and the AfterBlit **rift renderer** samples it
directly. All three change with TAA on and are unmeasured. The alternative - keep depth writes off
and output `a` = the depth sampled from Primary's own depth texture - was not taken because rule 7
prescribes writing the surface's depth; it stays the fallback if the overlays look wrong in game.

(x) **Replace blending on the motion attachment is set but never restored, and that is correct only
because the attachment leaves the draw-buffer mask.** Per-attachment blend state is global pipeline
state, not per-framebuffer, on both backends. The discipline everything relies on is: set the
global blend mode first, then the per-attachment overrides. `SetBlend` on the Vulkan device resets
every attachment exactly as GL's non-indexed `glBlendFunc` does (`GlStateTracker.SetBlend`), so a
`GlToggleBlend` inside an open window re-applies the motion override at the end and the two
backends agree. A caller that sets a per-attachment factor and then a global mode has it silently
undone.

(y) **The merge's reactive is written first and overwritten by everything after it.** The OIT merge
adds `anet` over the whole screen, and then the AfterOIT terrain overlay, the AfterOIT entities,
the decals, the liquid velocity pass and the sky pass all write the attachment with **replace**
blending. So a transparent thing in front of a decal, of pass-7 terrain or of water contributes no
reactive at those pixels. Bounded and deliberate (each of those writers owns a better answer for
its own pixel), but it means the merge's value only survives where nothing later claimed the pixel.

(z) **`RenderOptimumSkyMotion` allocates five small arrays per frame** (one `double[16]`, four
`float[16]`) to build its two matrices, exactly as `RenderOptimumTaaResolve` already does. Per
frame, not per draw, so it is inside the rule - but the phase doubled that churn and neither is
cached. Fold both into fields when P5 measures.

(aa) **The revealage the reactive policy rests on has two different blend meanings.** With Optimum's
`SystemRenderOITLayers` active, `BeforeOIT` puts Transparent's attachment 1 on `(GL_DST_COLOR,
GL_ZERO)`, which composes `oit.fsh`'s `outReveal = 1 - alpha` multiplicatively - the correct
revealage. The vanilla factors that `LoadFrameBuffer` and `RestoreVanillaTransparentState` set,
`(GL_ZERO, GL_ONE_MINUS_SRC_COLOR)`, compose it as a product of alphas instead. Both
`transparentcompose.fsh` and `taa-skymotion.fsh` read that one texture, so they always agree with
each other - but if the OIT layer system ever falls back, the reactive value flips meaning along
with the composite alpha. Pre-existing, unmeasured, and now load-bearing for TAA.

Still owed for P4 (rule 3), in the game, on both backends, with the renderer confirmed from the log:
- whether the liquid velocity pass removes the water ghosting it was built for, and what its depth
  write does to block outlines on submerged blocks, to SSAO near water, and to rifts;
- whether reactive 1 on cloud-covered sky visibly costs the sky's own anti-aliasing, and whether
  `mix(coverage, 1, coverage)` is the right curve (it is a chosen shape, not a measured one);
- whether the decal writer removed the near-decal ghosting finding (o) identified;
- whether the movers (helve hammer head, resonator disc, pot lid, a falling block through its
  tumble) are clean;
- whether reactive 1 on faint cube particles costs the temporal AA of the geometry behind them;
- the vertex-warp cost of evaluating the warp twice on liquid, particles and decals - finding (j)
  is still unpaid;
- the GL path of everything P4 added: `BeginMotionOnlyWrite`'s `GL_NONE` draw-buffer array,
  `ApplyOptimumMotionAccumulateBlendState`'s `glBlendFunci`, and the sky pass's depth-func dance.
- `taaLiquidReactive` (0.3) and `taaCloudReactive` (1.0) are hard-coded constants, not settings;
  wiring them to `OptimumConfig` is P5 work.

P4 status addendum (2026-09-11): P4 accepted in game by the user on Vulkan after commit 95bf71d
("that fixed the instability issue fully"). The Vulkan-only frame-to-frame shimmer that survived every
single-frame probe was not in the resolve: fullscreen passes on Primary left the SSAO normal/position
attachments write-enabled without storing to them, so Vulkan wrote undefined values into the G-buffer
every frame (GL keeps the old values) and SSAO's dark outlines flickered per frame. Found only after
the validation log was made readable (`OPTIMUM_VULKAN_VALIDATION=1` used to log nowhere) and
synchronization + best-practices validation were enabled. Fixed by masking unwritten fragment outputs
in the pipeline; the present path also got a correct wait stage and per-image semaphores.
Lessons: screenshots cannot capture one-frame alternation; read the layer's log, not the client log;
"looks identical per frame on both backends" says nothing about what alternates between frames.

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

P5 status (2026-09-11): landed on `feat/taa` (13b4cd0 sharpen + mip bias, cd72089 settings
rows / scanner rules / packaging, c897e23 runtime donors, 8a3c33f acceptance and performance
harness, f1a6300 integrate, 77f0c6d adversarial-review fixes). **The acceptance matrix has not
been run** - no phase of P5 ran `make deploy` or the client, so by rule 3 P5 is not done, and
the default-on decision is not takeable yet. Everything below is what the code now does and
what the tests prove about it, which is a different claim from "it looks right".

What P5 built:

- **Sharpen**. `taa-sharpen.vsh/.fsh`, an RCAS variant with the lobe strength as a uniform
  instead of the baked `exp2(-0.2)`, running on the resolved RGBA16F colour into its own
  render-resolution target (frame buffer slot 21), placed immediately after the resolve so
  bloom, god rays and the Luma copy Final reads all see the same image.
  `TaaSharpness <= 0` is a true bypass - the shader returns the centre texel before the first
  ring tap, and the pass does not run at all - and the HDR upper clamp is dropped (RCAS's own
  lobe already passes anything above 1 through unsharpened). A target that fails to allocate
  costs the sharpening only, never TAA.
- **No double sharpening**. `OptimumFsrBlitActive()` is one shared condition asked by both
  `RenderOptimumTaaSharpen` and `BlitPrimaryToDefault`: below render scale 1 the blit finishes
  the frame with FSR's own RCAS at native resolution, so the TAA sharpen skips itself entirely.
- **Mip bias**. `OptimumConfig.EffectiveTerrainLodBias` = the render scale's `log2` term (as
  before) plus `TaaMipBias` while `EffectiveTaa`, clamped. Applied on both backends at both
  call sites: the atlas texture parameter in `ChunkRenderer` and the chunkopaque/chunktopsoil
  sampler objects in `ShaderRegistry`. Zero total makes no call at all.
- **Settings**. Three rows in the Optimum tab (`optTaa`, `optTaaSharpness`, `optTaaMipBias`)
  with lang strings and hover texts, persisted in `optimum.json` and clamped on load. The
  toggle rebuilds the frame buffers, reloads the shaders and raises
  `EnumTemporalResetReason.Toggle`; both sliders apply live. The toggle refuses on
  `IsFeatureExplicitlyDisabled("Taa")`, never on `IsShaderFeatureDisabled`, so a missing
  launcher scan cannot veto TAA.
- **Scanner**. TAA is vetoed by an external copy of any stage it owns - the `taa-` prefix
  (so a stage added later is covered without editing the decision), the liquid velocity pass,
  the FSR pair the sharpen shares its maths with, and **any** file in a `shaderincludes/`
  directory, because `ShaderRegistry` merges them all into the one dictionary every motion
  writer compiles against.
- **Packaging**. `make deploy` and all five `scripts/package*` copy the two asset directories
  by wildcard and then **verify every source file arrived**, failing the deploy or the package
  instead of shipping vanilla's shader under Optimum's name; `package.ps1`'s required-file list
  names the TAA stages one by one so a reviewer can read what a release contains.
- **Installed-runtime donors**. P3 finding (f) and P4 finding (t) are closed: 23
  `patches/runtime/**` donors now carry the movers, `check-patches.sh` reports 43 runtime
  patches applied with exact donors compiled, and two test families keep it that way -
  `TaaRuntimeDonorCoverageTests` (marker parity fork patch vs donor patch, plus "every
  instrumented fork patch is mapped") and `ModPatcherManifestConsistencyTests.
  EveryTransplantedMethodHasARuntimeDonor` (every `Methods` entry's declaring type has a donor
  or an Optimum-authored overlay, with the two FluffyClouds gaps listed explicitly).
- **Harness**. `docs/taa-acceptance.md` is the runnable matrix (18 rows plus performance and
  memory, tooling, preconditions, and the decision gate); `scripts/dev/perf-capture.sh` drives
  a launch-warmup-measure-close cycle; `scripts/dev/luma-diff.py` is the still-frame luminance
  measurement; `ClientMain.OptimumLogFrameTime` writes a per-second frame-time line, inert
  unless `OPTIMUM_FPS_LOG` names a file, so the numbers are backend-neutral.

Exact vs fallback, updated for P5 (the P4 table is otherwise unchanged):

| Class | Vector | Reactive | Change in P5 |
|---|---|---|---|
| Resolved colour, post-resolve | - | - | new: optional RCAS sharpen at `TaaSharpness`, bypassed at 0 and skipped whole when FSR's RCAS will run at native resolution |
| chunkopaque, chunktopsoil | exact | 0 | unchanged vectors; mip selection now carries `TaaMipBias` through the sampler objects, live |
| Every other terrain pass (liquid, transparent, shadow) | as P4 | as P4 | mip selection now carries the same bias through the atlas texture parameter, live and in step with the samplers |
| Liquid surfaces | exact | **0.3, still a compile-time constant** | `taaLiquidReactive` was on P4's "still owed" list to become a setting in P5; it did not |
| Volumetric clouds | camera-rotation-only | **`mix(coverage, 1, coverage)`, still a compile-time constant** | same: `taaCloudReactive` is still `ClientPlatformWindows.OptimumCloudReactive = 1f` |
| Helve hammer, resonator, fruitpress, pot lid, bloomery/forge/firepit contents, falling blocks, FP hands, echo chamber, held/dropped items, quern, every mech renderer | exact | 0 | **now exact on the installed-launcher path too**, not only in a from-source build: `patches/runtime/**` donors exist for all of them |
| Forge/anvil work items, static standard-shader users, mod geometry | fallback | 0 | unchanged |

Findings to carry:

(ab) **A sampler object hides a texture parameter, and the setting was on the wrong side of
it.** `chunkopaque`/`chunktopsoil` sample the atlas through sampler objects, and a bound
sampler object overrides the texture object's state on that unit for everything it carries,
LOD bias included. The bias was written into those samplers once per shader load and into the
atlas texture every frame, so dragging the mip-bias slider moved liquid, transparent and
shadow terrain and left opaque terrain and topsoil on the bias they were compiled with - two
mip selections of the same atlas in one frame, while the tooltip said "applies immediately".
`ShaderRegistry.ApplyOptimumTerrainSamplerLodBias` is now the single writer and
`ChunkRenderer.SetOptimumTextureLodBias` calls it, so both halves move together or neither
does. The backend half is not free either: the live change only reaches the GPU because
`VulkanDevice` resolves the unit's sampler at draw time and the descriptor set is keyed on the
resolved `VkSampler`, not on the sampler id - pinned by
`VulkanDeviceIntegrationTests.ALodBiasWrittenToAnAlreadyBoundSamplerChangesTheMipTheGpuReads`.

(ac) **"No call at all" needs a cache value that means "never".**
`ChunkRenderer.optimumTextureLodBias` started at `0f`, so the first frame of a TAA-off
native-scale session saw "0 wanted, cache not NaN" and wrote an explicit LOD bias of 0 over
the driver default on every atlas - the one call that configuration is documented never to
make, and with (ab) fixed it would have reached every terrain sampler too. `float.NaN` is the
only initialiser that says "Optimum has never touched this".

(ad) **The translation gate picked the new stage up for free, and that is worth knowing.**
`taa-sharpen` needed no corpus row: `ShaderCorpus.LoadShaderFiles` overlays `sources/shaders`
and `ProgramNames` takes every base name with both stages, so
`EveryVanillaProgramTranslatesToSpirv` translates it in every variant. That is only true for a
stage that is a plain `.vsh`/`.fsh` pair under `sources/shaders`; the P3/P4 writers needed
explicit rows precisely because they live inside define combinations no row produced
(findings (d) and (v)).

(ae) **The sharpen target is allocated whenever TAA is on, including at render scale below 1
where the pass can never run** - 16 MiB at 1080p, ~64 MiB at 4K, on the handheld this plan
targets. Deliberately left: gating it on the render scale would put a second copy of the
"is FSR going to run" condition next to the shared `OptimumFsrBlitActive()` the phase
introduced to stop exactly that drift. Revisit with the memory numbers from the matrix.

(af) **Finding (z) is unpaid.** `RenderOptimumTaaResolve` and `RenderOptimumSkyMotion` still
allocate five small arrays each per frame; P4 said "fold both into fields when P5 measures",
and P5 did not measure.

Still owed for P5 (rule 3), in the game, on both backends, with the renderer confirmed from
the log - all of it is `docs/taa-acceptance.md`:
- the 18 acceptance rows (A1-A18), each twice per backend, TAA on and off, with the
  seven-pair luminance medians recorded; A18 is the TAA-off byte-identity check, which so far
  exists only as a code argument and a coverage test, never as a measured frame;
- everything P4 left owed and P5 did not close: the liquid velocity pass's depth write against
  block outlines, SSAO near water and rifts; whether reactive 1 on cloud-covered sky costs the
  sky's own AA and whether `mix(coverage, 1, coverage)` is the right curve; the near-decal
  ghosting finding (o) identified; the movers through their animations; faint cube particles;
  and the vertex-warp cost of evaluating the warp twice (finding (j), still unpaid);
- the GL path of everything P3/P4/P5 added: `Optimum.Render.Vulkan.Tests` is still the only
  GPU harness, so every GL branch - the sky pass's depth-func dance,
  `BeginMotionOnlyWrite`'s `GL_NONE` array, `glBlendFunci`, and now the sharpen pass and the
  `GL.SamplerParameter` half of the live mip bias - has never executed;
- performance on the Arc 140V: frame delta, GPU pass timestamps, CPU frame time, 1% lows, with
  renderer name, power mode and thermals; and the measured memory against the plan's estimate
  (motion 15.8 + two colour histories 31.6 + aux 7.9 + prev-depth 15.8 MiB, plus the sharpen
  target's 15.8 MiB, which the plan's figure does not include);
- `taaLiquidReactive` and `taaCloudReactive` as settings rather than constants, which P4
  assigned to P5 and P5 did not do. They are uniforms already, so this is a config field and a
  row each, not a shader change - but they should be tuned by measurement in the matrix first,
  which is why leaving them until the matrix runs is defensible.

**Default-on is not decided.** The plan says decide only after the matrix passes, and the
matrix has not been run. The decision belongs to the user, with the evidence paths recorded
here; until then `OptimumConfig.Taa` stays `false` and TAA is opt-in from the settings tab.

P5 in-game verification (2026-09-11, Fable): deployed c9758ce+5b952da; both backends start, log their
renderer, load `taa-sharpen`, no exceptions; Vulkan under synchronization + best-practices validation
shows no backend hazards (only MangoHud's external overlay hazard). Frame times via
`scripts/dev/perf-capture.sh` (30 s at spawn in "serene cave world", ssaa 0.5, 2755x1727 window):

| backend | TAA | mean ms | 1% low ms | frames/30 s |
|---|---|---|---|---|
| Vulkan | on | 6.87 | 15.8 | 4397 |
| Vulkan | off | 6.09 | 12.0 | 4941 |
| OpenGL | on | 6.06 | 8.1 | 4963 |
| OpenGL | off | 6.07 | 10.0 | 4962 |

Caveat: the Wayland compositor caps presentation at the 165 Hz refresh even with `vsyncMode 0`
(`--vsync off` added to the script), so every row except Vulkan+TAA sits on the cap; the only
cost visible is Vulkan TAA >= 0.8 ms at half render resolution. A real cost number needs GPU
timestamps or an uncapped surface; the Arc 140V run in the plan's P5 matrix remains the target
measurement. The 18-row acceptance matrix (docs/taa-acceptance.md) and the default-on decision are
the user's; TAA stays default-off until then.

**P6. Freeze the contract.**
- Document the immutable frame input record, resource formats/conventions, per-class motion status
  and the adapter tests; reserve backend-native execution, presentation lifetime and extra ray
  signals for the vendor plan.

**Contract** (2026-09-11): frozen as **v1** in **`docs/temporal-frame-contract.md`**. That document,
not this plan, is what every temporal consumer is written against - the in-house resolve today,
FSR 3.1 / XeSS 2 / DLSS next, frame generation and ray reconstruction after that. It specifies the
per-frame input record member by member (type, units, coordinate convention, the point in the frame
after which each value is this frame's), every resource with its format, resolution, sampler state
and channel semantics (the motion attachment's `rg`/`b`/`a` including the writer-depth validity
tolerance, the history colour/glow/linear-depth slots, the sharpen target), the jitter definition
and sequence, the reset reasons and their triggers, the per-class exact/fallback/reactive status,
and the adapter formulas for FSR 3.1, XeSS 2 and DLSS. Native handles, extension negotiation,
presentation lifetime and ray-reconstruction guides are explicitly reserved for the vendor plan.

`Optimum.Tests/temporal-contract-tests.cs` is the stability test: it pins the public surface of
`IOptimumTemporalContext` and `OptimumTemporalFrame` against a checked-in list, and pins the
conventions the document states - the shear formula, the motion-vector scale and sign per adapter,
the writer-depth tolerance expression in `taa-resolve.fsh`, the history slot indices and the
attachment formats in `ClientPlatformWindows` - so a change to any of them fails a test that names
the document. Changing the contract means changing the code, the document, its version, that list,
and this section, in that order.

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

## Follow-up (not part of this plan): shader patch system
Shaders ship as whole-file overrides (`sources/shaders/*` copied over vanilla by name, since v0.1.0;
P3 adds chunktopsoil, entityanimated and the vertexwarp include). A game update that changes a vanilla
shader is silently shadowed. Needed later: emit `patches/shaders/*.patch` against the vanilla archive
(`.vanilla/archives/vs_client_*.tar.gz`) from `scripts/extract-patches.sh`, verify in
`scripts/check-patches.sh`, and keep overrides additive (vanilla functions untouched, Optimum twins
beside them) so patches stay small. Raised by the user on 2026-09-10 during P3.
