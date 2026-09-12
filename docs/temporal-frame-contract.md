# Optimum temporal frame contract — v1 (frozen 2026-09-11)

**Status:** frozen. **Version:** `v1`. **Owner:** `TAA-PLAN.md` P6.
**Stability test:** `Optimum.Tests/temporal-contract-tests.cs`. Every clause below that a test can
reach is pinned there; a change to any of them fails a test that names this document. Adding a
member, a resource, a reset reason or an adapter is a **v2** change: bump the version here, update
the checked-in surface list in the test, and say in `TAA-PLAN.md` P6 what moved.

This is the specification of what every temporal consumer receives from Optimum: the in-house TAA
resolve today, FSR 3.1 / XeSS 2 / DLSS super resolution next, frame generation and ray
reconstruction after that. It describes only what the engine **produces**. How a vendor library is
created, fed native handles and presented is explicitly out of scope — see
[Reserved for the vendor plan](#8-reserved-for-the-vendor-plan).

Sources of truth, in this order: the code, then this document, then `TAA-PLAN.md`.

| Thing | Source of truth |
|---|---|
| Input record, reset reasons, jitter sequence | `VintagestoryApi/Client/Render/OptimumTemporalFrame.cs` |
| Jitter shear, Halton, phase count, mv adapters | `VintagestoryApi/Client/Render/OptimumTemporalMath.cs` |
| Resource formats, sampler state, history slots | `build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs` |
| Channel semantics and the validity tolerance | `sources/shaders/taa-resolve.fsh`, `taa-skymotion.fsh`, `taa-sharpen.fsh` |
| Per-class motion status | §6 below, and the P3/P4/P5 status tables in `TAA-PLAN.md` |

---

## 1. The per-frame input record

One engine-owned instance, `OptimumTemporal.Frame` (`OptimumTemporalFrame`), exposed read-only as
`OptimumTemporal.Context` (`IOptimumTemporalContext`). It is **mutable and single-instance by
design**: it lives on the render thread only and is read by shader-uniform setters in the hot path,
so it must not allocate per frame. "Immutable" in this contract means *immutable to consumers*:
a consumer reads it, never writes it, and treats every `float[16]` it hands out as read-only.
Values that must outlive the frame have to be copied — the matrix arrays are the live per-frame
arrays, not copies.

`Advance()` is called once per real rendered frame in `ClientMain.MainRenderLoop`, immediately
after `shUniforms.Update()` and before the `Before` render stage.

### 1.1 Members

Types are the declared C# types. "Valid from" names the point in the frame after which the value is
this frame's; before that point it still holds the previous frame's value (or zero).

| Member | Type | Units / space | Valid from |
|---|---|---|---|
| `FrameIndex` | `long` | count, +1 per **real** rendered frame; generated/present ids are a separate counter reserved for frame generation | `Advance` |
| `JitterActive` | `bool` | the temporal window itself: true from `Advance` until `RenderAfterPostProcessing` closes it | `Advance` (set by the caller right after) |
| `JitterPx` | `Vec2f` | render pixels, the offset **actually applied**; `(0,0)` whenever the window is closed | `Advance` |
| `PrevJitterPx` | `Vec2f` | render pixels, the offset the previous frame really rendered with | `Advance` |
| `JitterSequencePx` | `Vec2f` | render pixels, this frame's Halton offset whether or not it is applied | `Advance` |
| `RenderWidth`, `RenderHeight` | `int` | render-resolution pixels (Primary's size, i.e. window size × SSAA/render scale), clamped to ≥ 1 | `Advance` |
| `GetProjection(view)` | `float[16]` | column-major **unjittered** perspective matrix last loaded for that view | that view's `Set3DProjection` |
| `GetPrevProjection(view)` | `float[16]` | the same for the previous frame | `Advance` |
| `IsViewCaptured(view)` | `bool` | whether the view was set up this frame (the hand view is absent in third person) | per `Set3DProjection` |
| `ActiveView` | `EnumTemporalView` | the view the currently loaded projection belongs to; a draw issued now is under this FOV | per `Set3DProjection`; reset to `World` by `Advance` |
| `CameraMatrix` / `PrevCameraMatrix` | `float[16]` | column-major view matrix, **entity view** (camera at the player) | `CaptureCamera`, at `GlLoadMatrix(CameraMatrix)` |
| `CameraMatrixOrigin` / `PrevCameraMatrixOrigin` | `float[16]` | column-major view matrix, **terrain view** (camera at the chunk-relative origin). This is the space the resolve works in | `CaptureCamera` |
| `CameraPosDelta` | `Vec3f` | blocks; `cameraPos(this frame) − cameraPos(previous frame)`, differenced from `EntityPlayer.CameraPos` in **double** precision and then narrowed. Forced to `(0,0,0)` on any reset frame | `CaptureCameraPosition` |
| `Playerpos` / `PrevPlayerpos` | `Vec3f` | blocks, camera relative to the slowly rebased reference position (`DefaultShaderUniforms.PlayerPos`) — the space the warp noise is sampled in | `CaptureCameraPosition` |
| `Warp` / `PrevWarp` | `OptimumWarpState` | every uniform the `vertexwarp.vsh` functions read (see §1.2) | `Advance` (`Warp`), `Advance` (`PrevWarp`, rolled) |
| `Reset` | `bool` | `ResetReason != None` | `Advance`, possibly upgraded by `CaptureCameraPosition` |
| `ResetReason` | `EnumTemporalResetReason` | see §5 | as above |
| `ZNear`, `ZFar` | `float` | blocks (world units), the world camera's near/far planes | `Advance` |
| `Fov` | `float` | **radians**, vertical, the world FOV (`ClientSettings.FieldOfView * π/180`). The hand view's FOV is not in the record — only its projection matrix is | `Advance` |
| `DeltaTimeMs` | `float` | milliseconds since the previous frame | `Advance` |

`OptimumTemporalFrame` additionally exposes, beyond the read-only interface:
`TeleportThresholdBlocks` (`const double`, 8.0), `WasViewCaptured(view)`, `RequestReset(reason)`,
`Advance(...)`, `CaptureCameraPosition(...)`, `RecordProjection(...)`, `CaptureCamera(...)`,
`ApplyJitterCopy(double[])`, `ApplyMotionUniforms(IShaderProgram)`, and a settable `JitterActive`.
Consumers use the interface; only the client owns the mutators.

### 1.2 `OptimumWarpState`

`TimeCounter`, `WindWaveCounter`, `WindWaveCounterHighFreq`, `WaterWaveCounter`, `WindSpeed`,
`GlobalWarpIntensity` (= `DefaultShaderUniforms.GlobalWorldWarp`), `GlitchWaviness`,
`WindWaveIntensity`, `WaterWaveIntensity` (all `float`), `PerceptionEffectId` (`int`),
`PerceptionEffectIntensity` (`float`). Built by `OptimumWarpState.FromUniforms(DefaultShaderUniforms)`.

The counters **wrap** (`DefaultShaderUniforms.Update` takes them modulo 6000), so the previous value
is stored, never derived as current − dt. Some of these are overridden per entity or per pass; the
two that are (`windWaveIntensity`, `waterWaveCounter`) are recorded per draw by
`OptimumEntityMotion` / `OptimumStandardMotion`, not taken from this struct.

### 1.3 Placement in the frame (where each value becomes true)

```
MainRenderLoop
  shUniforms.Update()
  Advance()                      -> FrameIndex, jitter, RenderW/H, ZNear/ZFar/Fov, DeltaTimeMs,
                                    Warp/PrevWarp, rotate cur->prev for every captured value
  JitterActive = EffectiveTaa || TaaJitterDev     (opens the temporal window)
  Before stage                   -> PlayerCamera writes EntityPlayer.CameraPos and shUniforms.PlayerPos
  CaptureCamera(...)             -> CameraMatrix, CameraMatrixOrigin
  CaptureCameraPosition(...)     -> CameraPosDelta, Playerpos, + Teleport / Rebase reset detection
  Set3DProjection(world|hand)    -> RecordProjection: GetProjection(view), ActiveView
  ... jittered scene passes, SSAO ...
  taa-resolve, taa-sharpen
  RenderAfterPostProcessing      -> JitterActive = false     (closes the temporal window)
```

**The rule that produced this layout** (P3 finding (a)): a value is snapshotted against the stage
that *writes* it, not against the top of the loop. Reading `CameraPos` in `Advance` paired a
one-frame-stale translation with a fresh previous rotation, and the difference — the camera's
acceleration — painted motion onto static ground.

---

## 2. Jitter

**Definition.** `JitterPx` is the **raster displacement of a static point**: with the jittered
projection, a point that projects to pixel `p` unjittered projects to `p + JitterPx`. Y is up, in
render pixels, in `[-0.5, 0.5]`, never exactly `(0, 0)`.

**Shear.** Optimum's perspective matrices come from `Mat4d.Perspective` (`clip.w = -z_view`,
column-major `float[16]`). The jitter is one NDC shear on that matrix:

```
P[8] -= 2 * jx / renderWidth;
P[9] -= 2 * jy / renderHeight;
```

`OptimumTemporalMath.ApplyProjectionJitter` is the only implementation; `OptimumTemporalFrame.ApplyJitterCopy`
and `ClientMain.CurrentProjectionMatrix` both go through that convention. **One NDC shear per
frame**: auxiliary targets of other sizes (the quarter-resolution LiquidDepth prepass) inherit the
same NDC shift and never get a per-target pixel offset.

**Sequence.** Halton(2, 3), one-indexed:

```
phaseCount = max(1, ceil(8 * upscale^2))      where upscale = 1 / renderScale
phase      = FrameIndex % phaseCount
jx         = Halton(phase + 1, 2) - 0.5
jy         = Halton(phase + 1, 3) - 0.5
if (jx == 0 && jy == 0) jx = 0.25             // a frame must contribute a new sub-pixel sample
```

At native resolution that is 8 phases; at render scale 0.5 it is 32.

> **Note (2026-09-12, not a v1 change).** `renderScale` is the scale the frame is *really*
> rendered at, which since the DLSS plan's Phase 2 is not always the config's `ssaa` /
> `OptimumConfig.RenderScale`: with an upscaler owning the resolve it is that upscaler's own ratio,
> `renderWidth / displayWidth` from the vendor's optimal-settings query, published as
> `OptimumConfig.UpscalerRenderScale` and read through `OptimumConfig.EffectiveTemporalRenderScale`.
> The formula, the sequence and the definition of `JitterPx` are unchanged; only the number fed into
> them follows the frame instead of a setting, which is what §7.2 already allows ("the sequence length
> can be taken from the SDK"). The temporal window (`JitterActive`) likewise opens for an upscaler
> exactly as it does for the in-house resolve — an upscaler needs the jitter just as much — and the
> motion attachment is allocated for either consumer, while the history slots (§3.3) and the sharpen
> target (§3.4) stay the in-house resolve's alone.

> **Note (2026-09-12, not a v1 change): where an upscaler sits in the frame.** The DLSS plan's
> Phase 3 places the vendor evaluate exactly where the in-house resolve runs (DLSS Programming
> Guide §3.1: during post processing, before tone mapping, as early in it as possible). What that
> means for this contract's producers is nothing at all - the world, its G-buffer, the motion
> attachment, SSAO and the LiquidDepth prepass are still render-resolution, the jitter is still one
> NDC shear on the world projection, and §3.1/§3.2 describe the same images. What changes is
> downstream of the resolve: the evaluate writes a **display-resolution** scene colour into its own
> framebuffer slot (22, `OptimumUpscaledScene`, storage-usage colour plus a depth attachment), and
> FindBright, the blur chain, god rays, luma, the final composition, the AfterFinalComposition
> overlays, the blit and the screenshots all work at the display size from there on. The late
> overlays depth-test against a **nearest-neighbour upscale** of Primary's depth, produced once per
> frame, so their silhouettes are quantised to the render grid by up to one render pixel; that is the
> accepted cost of keeping the evaluate early, and it is measured by
> `UpscalePlacementTests.TheOverlayDepthIsAPointUpscaleAndItsEdgeErrorIsOneRenderPixel`.
> Colour mode: Primary colour 0 is 8-bit LDR and already perceptually encoded, so the feature runs
> with `IsHDR = 0` (guide §3.1.2), which is what §7.5's "no exposure path" implies.

**Scope.** The jitter reaches **only** the perspective matrix `Set3DProjection` last loaded, and
only while `JitterActive`. `ClientMain.CurrentProjectionMatrix` compares the top of the projection
stack element-by-element against that matrix and hands back the sheared copy only on an exact match,
so an ortho stack, a shadow ortho matrix or a caller-pushed matrix is returned unchanged. Shadow,
ortho, offscreen and every post-window pass (AfterFinalComposition overlays, the HUD, AfterBlit
rifts, the blit) are never jittered. `CurrentProjectionMatrixUnjittered` is the escape hatch.

**Writers.** A motion-vector writer's current pixel is `gl_FragCoord.xy - taaJitterPx` on **both**
backends: the Vulkan device renders offscreen unflipped and flips only in the present blit, which is
the entire Y-flip story for the backend.

---

## 3. Resources

All at render resolution unless stated. Render resolution = Primary's size = window size × the
effective render scale (`ssaa`). Sampler state is given as (min/mag filter, wrap).

### 3.1 Primary (frame buffer slot 0) — the producer side

| Attachment | Format | Sampler | Contents |
|---|---|---|---|
| depth | `GL_DEPTH_COMPONENT32` (GL) / `D32_SFLOAT` (Vulkan) | NEAREST, CLAMP_TO_EDGE | window depth, `[0,1]`, **0 = near**, not reversed, `GL_LESS` |
| colour 0 | `RGBA8` | NEAREST (LINEAR when ssaa > 1), REPEAT | jittered scene colour |
| colour 1 | `RGBA8` | NEAREST (LINEAR when ssaa > 1), REPEAT | jittered glow |
| colour 2, 3 | `RGBA16F` | LINEAR, CLAMP_TO_BORDER, white border | SSAO G-buffer (position, normal); present only when SSAO is on |
| colour `MotionAttachmentIndex` | `RGBA16F` | NEAREST (GL); device path leaves the default — the resolve reads it with `texelFetch`, so filtering is not load-bearing | **motion**, see §3.2 |

`ClientPlatformWindows.MotionAttachmentIndex` is **2 without the SSAO G-buffer, 4 with it**, and
`-1` when TAA is off or the attachment failed to allocate. It is appended after every existing
attachment so no existing index moves, and it is **never in the default draw-buffer mask**: a pass
that writes it opens a window explicitly (`BeginMotionWrite` / `EndMotionWrite`, or
`BeginMotionOnlyWrite` for the liquid velocity pass). A window is refused unless Primary is bound
and `JitterActive` is true.
Since 2026-09-11 (Vulkan-native plan, Phase 1A step 2) these members are declared virtual on
`ClientPlatformAbstract` with neutral bodies and `ClientPlatformWindows` overrides them with the
bodies described here; the move changes no semantics of v1.

Cleared to `vec4(0)` each frame — which is what makes `a == 0` mean "nothing wrote here".

### 3.2 Motion attachment channel semantics

```
rg = mv         motion vector, RENDER PIXELS
b  = reactive   [0,1]
a  = writerDepth  window depth in [0,1] at write time
```

- **`rg` — motion vector.** `mv = previousPixel − currentPixel` (current pixel → where it was),
  in render-resolution pixels, **jitter excluded** (both positions come from unjittered
  projections), **undilated**, **not** normalized. History lookup is
  `historyUV = (pixelCentre + mv) / renderSize` — anchored at the unjittered pixel-centre grid,
  because that is the grid the history lives on (P2 finding (c)).
- **`b` — reactive.** `0` opaque, `1 − revealage` for OIT transparents (added by the merge), `1`
  for cube particles, `0.3` for liquid surfaces, `mix(coverage, taaCloudReactive, coverage)` on
  cloud-covered sky. It lowers the history weight in the resolve (`alpha = max(alpha, reactive)`)
  and is the value that maps to the FSR reactive mask / XeSS responsive mask. **Read whether or not
  the pixel passed the validity test** (P3 finding (h)), so a writer that bails out of its vector
  must still deliver `b` and zero only `rg` and `a` (P4 finding (u)).
- **`a` — writer depth, and the validity rule.** `a` is the **window depth in `[0,1]`** the writer
  put in the depth buffer (`gl_FragCoord.z`, plus any depth offset the draw applies) — the same
  space as the depth attachment, **never NDC depth**. The resolve treats the pixel as validly
  written only when

  ```glsl
  bool written = motion.a > 0.0 && abs(motion.a - depth) <= max(2e-4, 8e-4 * depth);
  ```

  The tolerance is half-float aware: the attachment is `RGBA16F`, whose ULP near 1.0 is already
  ~5e-4, so a fixed absolute epsilon rejects every legitimate distant writer. The relative term
  covers precision; the floor covers depths near the near plane. Where the test fails, the resolve
  falls back to camera reprojection. **This is the contract for unknown writers**: mod geometry,
  uninstrumented renderers and sky all land in the fallback without relying on undefined
  unwritten-output contents. The reference resolve evaluates this rule at the nearest-depth tap of
  its 3x3 (note under §4, 2026-09-11); the rule itself is unchanged.

  Consequence, measured (P4 finding (o)): a draw whose depth offset moves the depth buffer further
  than the tolerance — a decal at one block's distance moves it ~1.3e-3 against a tolerance of
  ~7.2e-4 — must write its **own** depth into `a`, not the depth of the geometry it sits on.

- **Blend state.** The motion attachment is **replace**-blended in a window
  (`SetBlendFuncSeparate(MV_LOCATION, 1, 0, 1, 0)`), except in the OIT merge, which is additive
  `(ONE, ONE)` under `FUNC_ADD` with `rg` and `a` written as zero so the opaque vector underneath
  survives bit-for-bit. Per-attachment blend state is **global pipeline state, not per-framebuffer**,
  on both backends: set the global blend mode first, then the per-attachment override (P4 finding (x)).
- **Write order.** The merge's reactive is written first and overwritten by everything after it
  (AfterOIT terrain, AfterOIT entities, decals, the liquid velocity pass, the sky pass), all with
  replace blending. The merge's value survives only where nothing later claimed the pixel
  (P4 finding (y)).

### 3.3 History slots (frame buffer slots 19 and 20)

Two render-resolution slots, selected by frame parity: `TaaHistory(parity)` returns slot **19** when
`(parity & 1) == 0` and slot **20** otherwise. The resolve writes `TaaHistory(_taaFrameParity)` and
reads `TaaHistory(_taaFrameParity + 1)`, then flips the parity. Both are allocated together and both
are null when TAA is off or allocation failed (`TaaTargetsReady`).

| Attachment | Format | Sampler | Contents |
|---|---|---|---|
| 0 | `RGBA16F` | **LINEAR**, CLAMP_TO_EDGE | resolved colour (rgb) + resolved scene alpha (a) |
| 1 | `RGBA8` | **LINEAR**, CLAMP_TO_EDGE | resolved glow. The plan's `b = ssao` slot is **allocated and reserved, written as zero, read by nobody** — resolving SSAO temporally is deferred (P2) |
| 2 | `R32F` (raw GL token `0x822E`) | **NEAREST**, CLAMP_TO_EDGE | previous **linear view depth**, positive, in blocks: `-(viewMatrix * vec4(world,1)).z` in the terrain (camera-relative-origin) space |

The filters are load-bearing and identical on both backends: colour and glow are read at a
fractional reprojected offset (Catmull-Rom over a bilinear sampler for colour, a plain `texture()`
for glow), while an interpolated linear depth across a silhouette belongs to neither surface and
would defeat the disocclusion test. A freshly allocated slot holds **undefined** contents, so the
resolve treats a NaN/Inf history sample as a reset.

### 3.4 Sharpen target (frame buffer slot 21)

One `RGBA16F` render-resolution colour attachment, LINEAR + CLAMP_TO_EDGE, allocated and released
with the two history slots. Holds the sharpened copy of the slot the resolve just wrote. Null when
TAA is off or when its allocation failed — in which case the post chain simply reads the unsharpened
resolve. `TaaSharpness <= 0` is a true bypass and the pass does not run; the pass also skips itself
entirely when `OptimumFsrBlitActive()` says FSR 1's RCAS will finish the frame at native resolution.
Known cost (P5 finding (ae)): the target is allocated whenever TAA is on, including at render scales
where the pass can never run.

### 3.5 Other slots the contract touches

`18` = FSR 1, `19`/`20` = history, `21` = TAA sharpen. Primary is `0`, Transparent is `1`,
LiquidDepth is quarter resolution with its own depth-only target.

---

## 4. The resolve's own inputs (the reference consumer)

`taa-resolve.fsh` is the first consumer and the permanent OpenGL fallback. It consumes exactly the
contract and nothing else:

| Uniform | Source |
|---|---|
| `sceneTex`, `glowTex` | Primary colour 0 and 1 |
| `motionTex` | Primary colour `MotionAttachmentIndex` |
| `depthTex` | Primary depth |
| `historyColor`, `historyGlow`, `historyDepth` | the read history slot's attachments 0, 1, 2 |
| `renderSize` | `RenderWidth`, `RenderHeight` |
| `jitterPx` | `JitterPx` |
| `invViewProjJittered` | `inverse(jitter(GetProjection(World)) * CameraMatrixOrigin)` |
| `prevViewProj` | `GetPrevProjection(World) * PrevCameraMatrixOrigin` |
| `viewMatrix` | `CameraMatrixOrigin` |
| `cameraDelta` | `CameraPosDelta` |
| `resetHistory` | `Reset \|\| !historyValid \|\| !WasViewCaptured(World) \|\| the inverse failed` |
| `blendAlpha`, `varianceGamma` | 0.1, 1.25 |

MRT outputs: `outColor` (colour), `outGlow` (glow), `outDepth` (linear view depth) — the three
history attachments. The camera fallback reprojects a finite surface as `world + cameraDelta` and
sky (`depth >= 0.999999`) as a **direction** with `w = 0`, so camera translation cannot move it.

**Note (2026-09-11): anti-flicker weighting and nearest-depth disocclusion.** A change inside the
reference consumer only: the contract stays **v1** (motion-vector semantics, the §3.2 validity rule,
history formats and slot layout are unchanged). Root cause of the distant-foliage jitter, measured on
parity dumps of both backends: the resolve's single-sample disocclusion test (this pixel's linear
depth against the one history depth under `historyUv`) rejected history on **~3.7%** of distant leaf
pixels per frame, because a sub-pixel leaf hits the leaf in one jitter phase and the far background in
the next; and a fixed current weight let the neighbourhood clip box, moved every frame by that leaf,
drag the history with it. What the resolve does since:

- **Nearest-depth tap.** The 3x3 loop keeps the tap with the smallest window depth (`closestPixel`,
  `closestDepth`). The motion vector comes from that tap: the validity rule is evaluated there as
  `bool written = motion.a > 0.0 && abs(motion.a - closestDepth) <= max(2e-4, 8e-4 * closestDepth);`
  (`motion` and `closestDepth` are read at the same tap, so it is still a writer against its own
  pixel), and the camera fallback, the sky test and the far-minus-near sky direction use that tap's
  reconstructed point. `historyUv` stays anchored at this pixel's centre, reactive stays this pixel's
  own `motion.b`, and the history still stores this pixel's own linear depth.
- **3x3 nearest-depth disocclusion.** The nearest finite history depth in the 3x3 around `historyUv`
  against the nearest tap's linear depth, tolerance `0.5 + 0.08 * closestLinearDepth`.
- **Anti-flicker current weight** (Playdead INSIDE TAA). For pixels not rejected (reset, off-screen,
  NaN history, disocclusion): `alpha = mix(blendAlpha * 1.2, blendAlpha * 0.3, w * w)` with
  `w = 1 - |lumCur - lumHist| / max(lumCur, max(lumHist, 0.2))` on the rectified YCoCg luminance,
  then `alpha = max(alpha, reactive)`. Rejected pixels keep `alpha = 1`.

Measured: leaf-far rejection **~3.7% -> ~1.1%** per frame; the user confirmed on Vulkan that the
distant-foliage flicker is gone. **Never revert** to a single-sample depth test or a fixed blend
weight. Pinned by `TaaResolveTests.AntiFlickerWeightsFollowTheLuminanceDifference`,
`FlippingSubPixelLeafKeepsItsHistory`, `DisocclusionLargerThanTheNeighbourhoodStillResets` and
`MotionComesFromTheNearestDepthTapAtAnEdge` (GPU), `Optimum.Tests/taa-antiflicker-coverage-tests.cs`
(source), and gated in the game by `python3 scripts/dev/taa-rejection.py <parity dump dir>` (3x3
leaf-far rejection <= 1.5 percent; `docs/taa-acceptance.md` row A19). External consumers (FSR, XeSS,
DLSS) do their own dilation and rejection and are not bound by this.

---

## 5. Reset

`EnumTemporalResetReason`, in declaration order: `None`, `WorldLoad`, `Dimension`, `Teleport`,
`Rebase`, `Resize`, `ShaderReload`, `FovChange`, `RenderScale`, `Toggle`, `Screenshot`,
`CameraHistoryLost`.

| Reason | Trigger |
|---|---|
| `WorldLoad` | a world is loaded |
| `Dimension` | dimension change |
| `Teleport` | `\|CameraPosDelta\| > TeleportThresholdBlocks` (8.0 blocks in one frame), detected in `CaptureCameraPosition` |
| `Rebase` | `DefaultShaderUniforms.playerReferencePos` changed — the reference the warp noise and `playerpos` are relative to moved under the world |
| `Resize` | the render size changed, or a framebuffer rebuild invalidated the history (raised by `RebuildFrameBuffers`); also detected inside `Advance` by comparing the previous render size |
| `ShaderReload` | SSAO or shader reload |
| `FovChange` | FOV change |
| `RenderScale` | render-scale change |
| `Toggle` | the TAA setting was toggled (rebuilds the frame buffers and reloads the shaders) |
| `Screenshot` | mega-screenshot capture |
| `CameraHistoryLost` | a frame advanced without reaching `CaptureCameraPosition`, so the next capture would difference across two frames while the history was rendered with a zero delta |

Rules a consumer can rely on:

- `RequestReset` is safe to call several times before the next `Advance`; **the first non-`None`
  reason wins**, so the earliest cause is the one reported.
- A reset frame has `CameraPosDelta == (0,0,0)`.
- A reset clears **both** history sets; the reason exists because the remedies differ (a resize
  reallocates targets, a teleport only clears colour).
- Beyond the enum, the resolve treats two more conditions as a reset **per pixel**: the reprojected
  history sample is off screen, or the history sample is NaN/Inf (freshly allocated slot). NaN
  survives any weighted blend, so it would poison a pixel forever.

---

## 6. Per-class motion status

`exact` = the writer computes the true previous position for that surface. `fallback` = nothing
wrote a valid vector, so the resolve's camera reprojection owns the pixel (exact for static
geometry, wrong-but-bounded for anything that moves on its own). `reactive` = no usable vector; the
reactive value is what prevents the smear. Consolidated from the P3, P4 and P5 status tables in
`TAA-PLAN.md`.

| Class | Vector | Reactive | Note |
|---|---|---|---|
| Chunk opaque (passes 0, 1, 2, 8), chunk topsoil | exact | 0 | `prevRel = truePos + cameraPosDelta`, warp replayed from `PrevWarp`, z-offset applied to both clips |
| Chunk opaque pass 7 (AfterOIT overlay) | exact | 0 | own window in `RenderAfterOIT` |
| LiquidDepth prepass | none, by design | – | own quarter-res target; jittered by the shared NDC shear, never in the motion mask |
| Liquid surfaces | exact | 0.3, compile-time constant | dedicated `chunkliquidmotion` velocity pass into Primary, depth test on **and depth write on**, so `a` matches the buffer |
| Skinned entities, batched opaque | exact | 0 | previous model matrix + `AnimationPrev` bones, hooked on the one bone upload every entity draw makes |
| Skinned entities, OIT | none | `1 − revealage` | six OIT outputs already fill Transparent |
| Skinned entities, AfterOIT (`DoRender3DAfterOIT`) | fallback | 0 | arbitrary per-renderer shaders; stays outside a window |
| First-person hands, echo chamber | exact | 0 | own programs; hands reproject through `GetPrevProjection(Hand)` |
| Held items, dropped items, block-entity models, quern top | exact | 0 | `OptimumStandardMotion.Apply` + a narrow per-draw window |
| Movers (helve hammer, resonator disc, fruitpress mash, pot lid, bloomery/forge/firepit contents, falling blocks) | exact | 0 | keyed on the **drawn thing**, not the renderer (P4 findings (q), (r)) |
| Static standard-shader users (anvil parts, molds, signs, chest labels, knapping, clay forming, ground storage, crucible, support-beam preview) | fallback | 0 | static in the world, so camera reprojection is the right answer; each on a scanned exemption list with a reason |
| Forge / anvil work items | fallback | 0 | drawn on the mod's own `smithingWorkItemShader`, which declares no motion output |
| Instanced mechanical power | exact | 0 | per-instance previous transform in the instance stream, history keyed on the device object |
| ClothManager (shares the instanced program) | fallback | 0 | 20-float instance mesh, draws outside the window; missing attributes read `(0,0,0,1)` = no history |
| Cube particles | fallback, camera-only | 1 | the instance stream carries position and scale only, so there is no previous per-particle position. **Wrong data for FSR/XeSS mv and for frame generation** |
| Quad particles, OIT entities, liquid shading, aurora | none | `1 − revealage`, additive | the merge adds `anet` into `b` alone; `rg`/`a` written as zero |
| Sky colour, night sky | none, by design | 0 | depth test off for the whole pass, so depth stays 1 and the resolve's infinite-direction fallback is the **exact** answer |
| Sun, moon, celestial objects | fallback, bounded | 0 | depth tested, never written; the fallback ignores only the celestial rotation, ~0.004° per frame |
| Volumetric clouds, aurora | camera-rotation-only | `mix(coverage, taaCloudReactive, coverage)` on sky pixels | `taa-skymotion` claims depth-1 pixels under `GL_LEQUAL`. **The cloud's own scrolling is not in the vector** (P4 finding (n)) — correct for the resolve, wrong data for FSR/XeSS and especially frame generation |
| Clear sky (no cloud coverage) | exact | 0 | coverage 0, so the dithered gradient keeps full history weight |
| Decals | exact | 0 | own writer: chunk previous path + `PrevWarp` + both z-offsets, `a = gl_FragCoord.z` |
| AfterFinalComposition overlays (work-item guides, selection boxes, wireframes) | none | none | outside the temporal window; the motion window is refused on `JitterActive` |
| Rifts (AfterBlit) | none | none | default framebuffer, outside the window. Recorded as the frame-generation gap |
| Mod geometry via `IRenderAPI` | fallback | 0 | writer-depth mismatch, by design. An opt-in writer API is future work |

Two classes are known-wrong data for a **vendor** consumer even though they are right for the
in-house resolve: cube particles (camera-only vector at reactive 1) and volumetric clouds
(camera-rotation-only vector). Both are listed here so an upscaler or frame generator adapter does
not discover them by looking at smeared output.

### 6.1 Cloud pixels are UNSUPPORTED for external motion consumers

This is a contract term, not a caveat. `taa-skymotion` writes a **camera-rotation-only** vector on
cloud pixels: it reprojects the view direction, never the cloud. A cloud scrolling across a still
camera therefore carries `mv = 0`, which is indistinguishable from static geometry in the motion
attachment. The in-house resolve is unaffected because `taaCloudReactive` raises `b` on those
pixels and the resolve discards the history there.

Consequently, for every external consumer (FSR, XeSS, DLSS, any frame generator):

- **Cloud pixels must be rejected using the reactive mask** (`motion.b`, section 3.2). Treating
  their `rg` as a valid motion vector produces a static cloud layer under a moving camera and
  duplicated/stuttering clouds in generated frames.
- A **real** cloud vector is future work and requires the previous frame's `cloudOffset` together
  with the ray-marched hit position from `cloudvolumetric.fsh`, i.e. a motion output from the cloud
  volume itself; it cannot reach Primary's attachment without a second pass over that volume.
- Until that exists, no adapter may claim cloud motion support, and a v1 adapter that needs correct
  cloud motion is out of contract rather than a bug in this document.

---

## 7. Adapters

Optimum's stored form is one thing; each vendor wants its own units. These are the conversions the
vendor plan implements, derived from this contract's definitions. **Only the motion-vector scale is
executable today** — `OptimumTemporalMath.AdaptMotionVector`, pinned by the stability test. The
remaining rows are the specification a vendor adapter is written against and **must be validated
against the SDK headers when that adapter lands**; the Y-axis question in particular is real, since
Optimum renders Y-up offscreen on both backends and each SDK assumes its own raster convention.

### 7.1 Motion vectors

Stored: `mv = previousPixel − currentPixel`, render pixels, Y up, jitter excluded, undilated.

| Consumer | Scale | Sign | Flags |
|---|---|---|---|
| **FSR 3.1** | `motionVectorScale = (1, 1)` — the value is already in render pixels | unchanged; FSR wants current → previous, which is our stored direction | no dilation flag; vectors are render-resolution, not display-resolution |
| **XeSS 2** | identity — pixel mode | unchanged | `XESS_INIT_FLAG_USE_NDC_VELOCITY` **not** set (we hand over pixels); `XESS_INIT_FLAG_HIGH_RES_MV` **not** set (render-resolution vectors); `XESS_INIT_FLAG_JITTERED_MV` **not** set (our vectors exclude jitter) |
| **DLSS** (Streamline) | `mvecScale = (1 / renderWidth, 1 / renderHeight)` | unchanged | `motionVectorsJittered = false`, `motionVectorsDilated = false` |

`OptimumTemporalMath.AdaptMotionVector(x, y, w, h, adapter)` implements exactly this: identity for
`Fsr` and `Xess`, `(x/w, y/h)` for `Dlss`.

> **Note (2026-09-12, not a v1 change).** The DLSS row above is Streamline's convention, and that is
> the layer the row names. Optimum drives **raw NGX** instead (`NgxDlssFeature`, no Streamline), where
> `NVSDK_NGX_Parameter_MV_Scale_X/Y` multiplies the sampled vector into *render pixels*. Our vectors
> are already render pixels, so raw NGX gets `MV.Scale = (1, 1)` and no per-vector scaling — measured
> against the SDK headers, as §7 requires of an adapter when it lands. The stored vectors, their sign
> and their units are unchanged; only the constant handed to the vendor differs between the two layers.
> Raw NGX takes `+JitterPx`, in render pixels (the corrected mapping in §7.2).

### 7.2 Jitter

Stored: `JitterPx` = the raster displacement of a static point, Y up, applied by
`P[8] -= 2*jx/W; P[9] -= 2*jy/H`.

**Raw NGX (verified on 2026-09-12): pass `+JitterPx` in both axes.** The input is
raster displacement in the input image's pixel coordinates. Optimum's offscreen Vulkan
viewport has positive height, so increasing either component moves the raster content
towards increasing image coordinates. The GL-style Y-up interpretation does not require
an additional flip for an image consumed directly by NGX.

The previous adapter negated both components based on the projection coefficient's sign.
That was incorrect: `clip.w = -view.z` means subtracting the coefficient moves raster
content by **positive** jitter. NVIDIA's DLSS Programming Guide §3.7.3 specifies pixel
coordinates, not matrix coefficient signs. Full-cycle NGX readback tests at Performance
and Ultra Performance pin this mapping (`DlssJitterConventionTests`), including an
intentionally mirrored control. This corrects the adapter, not the v1 stored contract.
Other vendor adapters must still verify their own coordinate conventions.

The engine-side invariants remain:

- the applied offset is `JitterPx`, and it is `(0,0)` whenever `JitterActive` is false;
- the phase count is `max(1, ceil(8 * upscale^2))` — FSR's `ffxFsr2GetJitterPhaseCount` and XeSS's
  recommended phase count are both of that shape, so the sequence length can be taken from the SDK
  instead if a vendor requires it, at the cost of diverging from the in-house resolve's sequence.

### 7.3 Depth

Stored: window depth `[0,1]`, **0 = near**, not reversed, `GL_LESS`, `GL_DEPTH_COMPONENT32` /
`D32_SFLOAT`.

| Consumer | Setting |
|---|---|
| FSR 3.1 | the inverted-depth flag **not** set |
| XeSS 2 | `XESS_INIT_FLAG_INVERTED_DEPTH` **not** set |
| DLSS | `depthInverted = false` |

Linear view depth is available separately as history attachment 2 (`R32F`, positive, in blocks),
should a consumer want it; it is the previous frame's, not this frame's.

### 7.4 Reactive and transparency masks

Stored: `motion.b`, `[0,1]`, per pixel, in the motion attachment.

| Consumer | Mapping |
|---|---|
| FSR 3.1 | reactive mask ← `motion.b` directly, single channel. The transparency-and-composition mask is **not** produced today; FSR's auto-generation path is the intended starting point |
| XeSS 2 | responsive-pixel mask ← `motion.b`. XeSS reads it as "1 = responsive", the same polarity |
| DLSS | no first-class reactive input; `motion.b` is the signal an exposure/bias texture or a DLSS-RR guide would be built from |

The semantics of `b` per class are in §6. The caveat in §3.2 applies to every consumer: `b` is
present even on pixels whose vector was rejected.

### 7.5 Exposure

Optimum has **no HDR exposure path**. Primary colour 0 is `RGBA8` scene colour before bloom, god
rays and final composition; there is no engine-side exposure scalar or texture.

| Consumer | Setting |
|---|---|
| FSR 3.1 | `preExposure = 1.0`, no exposure texture, auto-exposure enabled |
| XeSS 2 | `exposureScale = 1.0`, no exposure-scale texture, `XESS_INIT_FLAG_EXPOSURE_SCALE_TEXTURE` not set |
| DLSS | `preExposure = 1.0`, auto-exposure on |

An HDR colour path is a renderer change, reserved (§8).

### 7.6 Camera constants

| Quantity | Contract member |
|---|---|
| near / far | `ZNear`, `ZFar` (blocks) |
| vertical FOV | `Fov` (**radians**; SDKs that want degrees convert) |
| view matrix (terrain / camera-relative-origin space) | `CameraMatrixOrigin`, `PrevCameraMatrixOrigin` |
| view matrix (entity space) | `CameraMatrix`, `PrevCameraMatrix` |
| projection, unjittered | `GetProjection(view)`, `GetPrevProjection(view)` |
| camera translation | `CameraPosDelta` (blocks, double-differenced) |
| render / display size | `RenderWidth`, `RenderHeight`; display size is the window size |
| frame time | `DeltaTimeMs` (milliseconds) |
| reset | `Reset`, `ResetReason` |
| frame id | `FrameIndex` (real frames only) |

Two things every adapter must handle rather than assume:

1. **World space is camera-relative and rebased.** The resolve works in the
   `CameraMatrixOrigin` space, not in absolute world coordinates, and the reference position moves
   (`Rebase`). Any consumer that wants a world-space camera has to account for that.
2. **Two views.** A draw under the hand FOV must be reprojected through the hand FOV's previous
   projection. `ActiveView` says which view the currently loaded projection belongs to;
   `IsViewCaptured` / `WasViewCaptured` say whether that view existed this frame and last frame.
   A vendor upscaler that takes a single camera matrix pair gets the **World** view, and the
   first-person hands are then a known approximation.

---

## 8. Reserved for the vendor plan

Explicitly **not** part of this contract, and not to be added to it without a version bump:

- **Native handles.** `VkImage`/`VkImageView`/`VkDevice`/`VkQueue`/`ID3D12Resource` and the
  command-buffer the library records into. The contract is backend-neutral and speaks in
  `FrameBufferRef` / texture ids through the `IOptimumGraphicsDevice` seam; a backend-native
  capability interface is the vendor plan's first deliverable.
- **Extension negotiation.** Device and instance extensions have to be requested at device
  creation, before anything in this contract exists.
- **Presentation lifetime.** Completion-based resource lifetimes past `Present`, a replaceable
  present path, and the generated-vs-real frame id split that frame generation needs.
- **HUD-less colour and late world content.** A HUD-less image exists today (Primary after Final,
  before the blit), but AfterBlit rifts and AfterFinalComposition guides are world content **outside**
  it; frame generation needs them moved before the boundary or composited after, and the UI needs
  its own alpha target with defined premultiplication.
- **Ray-reconstruction guides.** Linear HDR noisy colour, separate diffuse and specular albedo,
  normals + roughness, specular motion or hit distance. Optimum's SSAO gposition/gnormal are **not**
  those guides. This contract only keeps the attachment scheme and the input record extensible.
- **Availability.** XeSS-FG and AMD Ray Regeneration are D3D12-only today; DLSS SR/FG/RR and
  FSR 3.1 have Vulkan paths. Which of these Optimum can run is a vendor-plan question.

---

## 9. Changing this contract

1. Change the code.
2. Update this document and bump the version at the top.
3. Update the checked-in surface list in `Optimum.Tests/temporal-contract-tests.cs` — the test
   prints the actual list on failure, so the new list is the failure message.
4. Record the change in `TAA-PLAN.md` P6 under **Contract**.
