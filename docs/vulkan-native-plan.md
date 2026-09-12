<!-- A copy of the working plan kept in the repository so the design and its history travel with
     the code. The living copy is the session plan file; when they differ, this one is the record of
     what was decided and why. Roadmap and current status: docs/ROADMAP.md. -->

# Plan: from OpenGL-under-Vulkan emulation to a proper Vulkan backend

## Context

Optimum's Vulkan backend (`Optimum.Render.Vulkan/**`, ~12k lines) sits behind the GL-shaped
seam `IOptimumGraphicsDevice` and reproduces OpenGL semantics call by call: state toggles are
recorded and resolved per draw, rendering scopes are inferred from framebuffer/draw-buffer
changes, every layout transition is an `ALL_COMMANDS` barrier, every texture upload is a
synchronous submit-and-wait that first flushes the half-recorded frame, presentation is a single
submission that waits for the swapchain image at `ALL_COMMANDS`, and the indirect scratch is a
wrapping ring sized by heuristic. It passes sync validation and renders the same pixels as
OpenGL, but it cannot pipeline: the CPU and GPU serialise on uploads and on presentation, frame
delivery is uneven, and the TAA work (jittered frames, history ping-pong, motion windows that
toggle draw-buffer masks dozens of times per frame) multiplies the scope restarts and barriers.

The user's intent (2026-09-11): this was never meant to be an OpenGL emulator. It must become a
proper Vulkan backend: explicit frame structure, explicit synchronisation, asynchronous resource
streaming, decoupled presentation, and a design that the planned temporal work (FSR/XeSS/DLSS,
frame generation) can attach to.

Built from three exploration reports (seam usage, backend internals and tests, frame/temporal/
packaging constraints) and three design passes (platform integration, renderer core, shaders/
testing/phasing), reconciled below. Every file:line fact quoted was re-checked in the tree.

## Decisions taken with the user (2026-09-11)

1. **Scope: the client drives a frame graph.** The patched client announces frame and stage
   boundaries; the platform declares passes, uploads and readbacks; the GL-shaped seam is not
   the design centre any more.
2. **Mods: Vulkan-aware mods only.** Mods rendering through the game API land inside declared
   passes and work; mods touching raw GL or Harmony-patching the platform's graphics members are
   routed to OpenGL by the launcher scan. A mod-facing pass API is part of the new contract.
3. **Shaders: Vulkan-native GLSL for the vanilla program set**, explicit sets and bindings,
   compiled offline to SPIR-V. The runtime rewriter stays only for mod shaders.
4. **Milestone 1 = stable frame delivery with TAA**: explicit sync, asynchronous uploads,
   decoupled presentation and a declared post/TAA graph, measured by frame-time variance and a
   zero blocking-upload counter, then judged in game.
5. **Integration shape: substitute the platform, do not branch the calls.** The game's own
   graphics boundary is `ClientPlatformAbstract` (147 abstract/virtual members, ~100 graphics).
   `ScreenManager.Platform` is a public static field typed to the abstract class
   (`build/VintagestoryLib/Vintagestory.Client/ScreenManager.cs:29`); the sealed OpenGL class
   `ClientPlatformWindows` is instantiated at one line (`ClientProgram.cs:214`). The patcher
   unseals it and `Optimum.Render.Vulkan.dll` ships `VulkanClientPlatform : ClientPlatformWindows`
   overriding the graphics virtuals; windowing, input, audio, frame pacing and the embedded
   server stay in the base. OpenGL runs the base class, so "OFF is vanilla" is checkable.
6. **Why not leave Optimum:** any alternative against the closed client re-creates the same
   Cecil/Harmony layer; the patcher, launcher scan, packaging, contracts, frozen temporal
   contract and the GPU test suite carry over.

## Constraints

- **Cecil transplants** (`VULKAN-BACKEND-PLAN.md` §0, `Optimum.Tests/cecil-transplant-lambda-tests.cs`):
  no lambdas cached in compiler-generated classes, no LINQ predicates, no non-capturing lambdas,
  no hidden-helper lowering; injected types only as simple data holders; every changed member
  listed in `Optimum.Patcher/Program.cs`. Hence all renderer logic lives in
  `Optimum.Render.Vulkan` and the contracts assembly; the lib gains only virtual calls.
- **Temporal contract v1 is frozen** (`docs/temporal-frame-contract.md`,
  `Optimum.Tests/temporal-contract-tests.cs`). A change is a v2 bump with tests, never silent.
- **Hardware floor**: Vulkan 1.3 + dynamicRendering, synchronization2, timelineSemaphore,
  scalarBlockLayout, independentBlend, multiDrawIndirect. Targets: Intel Arc 140V (Windows),
  NVIDIA and Intel Mesa (Linux), AMD RDNA. Everything above the floor is an optional tier with a
  fallback and an env override that forces the fallback, so every tier is testable.
- **Verification rules** (`CLAUDE.md`): a launch is not a verification; diff both paths; verify
  in game on both backends at phase exits only; every fix has a GPU readback test and a
  source-coverage test; temporal and pacing claims are proven by numbers and logs, never by
  screenshot pairs.
- **Assembly identity (verified)**: vanilla and donor `VintagestoryLib.dll` are both unsigned and
  both `AssemblyVersion 1.22.7.0` (`build/VintagestoryLib/Properties/AssemblyInfo.cs:17`), so
  the renderer can compile against the donor and bind to the patched DLL at runtime, exactly as
  it already does for `VintagestoryAPI`.

## Step 0: branching (state after the user merged feat/taa)

`origin/main` = `94e2cc0`; checkout is `feat/taa` at `15a033c` with six uncommitted files (the
sky-direction fix: `sources/shaders/taa-skymotion.fsh`, `taa-resolve.fsh`,
`Optimum.Render.Vulkan.Tests/TaaSkyMotionTests.cs`, `TaaResolveTests.cs`,
`Optimum.Tests/taa-pipeline-coverage-tests.cs`, `taa-sky-decal-motion-coverage-tests.cs`).

```
git fetch origin
git checkout -b fix/taa-sky-direction origin/main      # commit the six files here (own small PR)
git checkout -b feat/vulkan-native origin/main         # everything below
```
Never `git stash`. WIP commits use the `wip:` prefix.

---

## Architecture (recommended approach)

### A. Integration: `VulkanClientPlatform`

**Shape.** `Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs`,
`public class VulkanClientPlatform : ClientPlatformWindows`, owning the native renderer
privately. It overrides every graphics virtual (framebuffers, fixed-function state, meshes,
textures, shaders/uniforms/UBOs, post chain, TAA/FSR members, screenshots/queries/diagnostics)
and inherits windowing, input, audio, assets/logging, the singleplayer server, CPU bitmaps, AVI
and the frame-pacing block of `window_RenderFrame`.

**No base-field widening is needed** (checked member by member): every private field of the base
is either exposed through an abstract property the subclass overrides (`CurrentFrameBuffer`,
`FrameBuffers`), or read only by methods the subclass overrides, or produced by a virtual the
subclass overrides (`frameBuffers = SetupDefaultFrameBuffers()` in `Start()`). If a step ever
needs `protected`, the design has drifted; the escape is a transplanted accessor, never
attribute surgery.

**Base edits** (all in methods that are already Cecil targets; everything else in
`ClientPlatformWindows` reverts to vanilla and its 87 `OptimumRender.Device` branches are deleted):

| Site | Edit |
|---|---|
| `ClientPlatformWindows.window_RenderFrame` | device branch becomes `BeginFrame(); frameHandler.OnNewFrame(dt); EndFrame();` (`BeginFrame` empty virtual on the abstract; `EndFrame` base override = `SwapBuffers`) |
| `ClientPlatformWindows.Start()` | thick-line GL probe becomes `SupportsThickLines = ProbeThickLineSupport();` |
| `ClientPlatformWindows.Window_Resize()` | `OnWindowSizeChanged(w, h)` before `RebuildFrameBuffers()` |
| `ScreenManager.Render` | the `GL.ClearBuffer`/`GL.DepthRange` pair becomes `Platform.ClearDefaultDepth(1f); Platform.SetDepthRange(0f, 20000f);` |
| `ClientMain.TriggerRenderStage` | `Platform.BeginRenderStage(stage)` / `EndRenderStage(stage)` around `eventManager?.TriggerRenderStage` (brackets every vanilla and mod renderer) |
| `ClientProgram.Start` | probe before construction; `OptimumRenderBootstrap.CreatePlatform(logger)` returns `object`, `as ClientPlatformWindows`; new injected `ConfigureClientPlatform(p)` holds the wiring now inlined at lines 215-255; after the window opens, `p.InitializeGraphics(hwnd, w, h, out reason)`; on failure reopen the window for OpenGL, construct the base platform, `ConfigureClientPlatform`, and assign `ScreenManager.Platform` (static, and `screenManager.Start` has not run yet); `p.ShutdownGraphics()` in the `finally` |

**Virtualized in place** (GL bodies stay in `ClientPlatformWindows`): `SetupDefaultFrameBuffers`,
`DisposeFrameBuffers`, `RenderFullscreenTriangle`, `GetGraphicsCardRenderer`.

**New virtuals on `ClientPlatformAbstract`** (base bodies: the verbatim GL lines they replace,
placed as overrides in `ClientPlatformWindows`; empty where GL has nothing to do):
`BeginFrame`, `EndFrame`, `InitializeGraphics`, `ShutdownGraphics`, `OnWindowSizeChanged`,
`ProbeThickLineSupport`, `BeginRenderStage`/`EndRenderStage`, `SetDepthRange`,
`ClearDefaultDepth`, `DeleteMeshHandle`; UBO ops (`UpdateUBO`, `BindUBO`, `UnbindUBO`,
`DeleteUBO`); program ops (`UseShaderProgram`, `DisposeShaderProgram`, `BindSampler`, 14
`SetUniform*` primitives, `BindProgramTexture2D/Cube`); texture leaf ops (`SetTextureLodBias`,
`SetSamplerLodBias`, `SetTextureDepthCompare`, `ClearTextureRegion`,
`LoadTextureFromRgbaPointer`, `CreateTexture2DArray`); occlusion queries (`Gen`, `Begin`, `End`,
`TryGetResult`, `Delete`); `ReadDefaultFramebuffer`; `GraphicsBackendName`. These retire the
48 seam sites outside the platform class (`ShaderProgramBase.cs` 23, `UBO.cs` 6,
`SystemRenderOITLayers.cs` 5, `SystemRenderSunMoon.cs` 3, `ClientMain.cs` 2, `ChunkRenderer.cs`
2, and one each in `ScreenManager`, `VAO`, `SystemRenderFrameBufferDebug`, `SvgLoader`,
`ShaderRegistry`, `InventoryItemRenderer`, `ClientSystemStartup`, `Screenshot`).

**TAA/FSR members** (`BeginMotionWrite`, `EndMotionWrite`, `BeginMotionOnlyWrite`,
`EndMotionOnlyWrite`, `RenderOptimumSkyMotion`, `RenderOptimumTaaResolve`,
`RenderOptimumTaaSharpen`, `DisableOptimumTaa`, `OptimumFsrBlitActive`, `TaaHistory`, and their
state fields) are declared virtual on the abstract class with the state fields injected there;
the GL bodies stay in `ClientPlatformWindows` as overrides. The seven cast sites
(`ChunkRenderer.cs:379,633,708`, `SystemRenderEntities.cs:315`, `SystemRenderDecals.cs:440`,
`SystemRenderParticles.cs:140`, `ClientMain.cs:1402`) become plain virtual calls; zero
`as/is ClientPlatformWindows` remain in the lib.

**Patcher capabilities** (`Optimum.Patcher/Program.cs`, `MemberInjector.cs`,
`SelfConsistencyVerifier.cs`): `typesToUnseal` (clears `TypeAttributes.Sealed`),
`methodsToVirtualize` (sets `Virtual|NewSlot|HideBySig`, keeps visibility), and a verifier that
fails the patch if any method body still reaches a virtualized method with `call` instead of
`callvirt` (the one silent failure mode: a non-transplanted caller would bypass the override).
`MemberInjector.CloneMethod` already copies `MethodAttributes`, so injected virtuals arrive
virtual and cross-assembly overrides bind by name and signature.

**What stays in the contracts** (`VintagestoryApi/Client/optimum-render-device.cs` shrinks to
this): `OptimumRender.ActiveBackend/FallbackReason/IsVulkan/FallBackToOpenGL`,
`OptimumRender.NoGraphicsApiWindow` (the window is created before any platform exists),
`OptimumRenderBootstrap` (`ShouldTryVulkan` unchanged, device-level, before the window;
`CreatePlatform` new), `OptimumMotionWrite.BeginHook/EndHook` (the mod forks call them and
reference only the API), `OptimumTemporal*` (contract v1). `IOptimumGraphicsDevice` and
`OptimumRender.Device` are deleted at the end of Phase 1A.

**Build wiring.** `Optimum.Render.Vulkan.csproj` gains a `ProjectReference` to
`build/VintagestoryLib/VintagestoryLib.csproj` with `Private=false` (compile against the donor
where `sealed` is removed and the virtuals exist; bind to the patched vanilla DLL at runtime).
`InitializeGraphics` reflects over the expected virtual set once and fails the install (OpenGL
fallback) rather than throwing `MissingMethodException` mid-frame. The reflective load through
`OptimumRenderBootstrap` (`Assembly.LoadFrom`) is unchanged.

### B. Renderer core (`Optimum.Render.Vulkan`)

Layout: `Device/` (the platform-facing entry points, `DeviceCaps` tier table), `Frame/`
(`FrameTimeline`, `FrameSlot`, `FrameRing`, `RingArena`, `RetireQueue`), `Transfer/`
(`UploadManager`, `ReadbackManager`, `ITransferBackend`), `Graph/` (`FrameGraph`,
`PassRecorder`, `ResourceStateTracker`, `BarrierBatcher`, `FramePlan`, `TransientAllocator`,
`GraphValidation`), `Present/` (`Swapchain`, `SwapchainRetirement`, `IPresentPath`),
`Pipelines/`, `Descriptors/`, `Resources/` (`TextureStore`, `SamplerCache`, `MeshStore`),
`State/`, `Shaders/`, `Diagnostics/`.

**Keep verbatim** (hard-won, tested): `VulkanAllocator`'s free-range coalescing, the per-slot
uniform-ring with dynamic offsets, `DescriptorCache` (content-keyed, never-reused ids),
`MeshManager`'s layout derivation (`PruneCustomInts`, `FillQuadIndices`,
`WriteIndirectCommands`), `PipelineCache`'s write-mask masking of undeclared outputs,
`Swapchain.ChooseFormat`, `SamplerState.LodCeiling`, `RenderTrace`, `TextureDump`,
`GpuCheckpoints`, `GlEnums`, `VertexLayout`, the whole `Shaders/` rewriter path (mod shaders).
**Replace**: synchronous upload path (`VulkanCommands.SubmitAndWait`, `TextureManager.Upload`,
`FlushFrame`), `RenderTargetManager` (scope inference), the wrapping indirect ring, swapchain
recreation, `FrameRing`'s fence pacing, `AccessForLayout` guesswork.

**Synchronisation.** Two timeline semaphores are the only clock: `Frame` (every graphics submit
signals `n`) and `Transfer`. `FramesInFlight` fixed at init (2 now; 3 later for frame
generation), every arena sized by it. `BeginFrame(n)`: `vkWaitSemaphores(Frame, n - FIF)` is the
**only CPU wait in steady state**; reset the slot's pool, arenas, descriptor arena, query range;
drain `RetireQueue` (entries keyed on both timeline values, destroyed exactly when both passed).
`vkQueuePresentKHR` cannot wait on a timeline, so `renderFinished[image]` stays binary.

**Transfer.** `ITransferBackend`: (A, default) a second command buffer per slot from the graphics
pool, recorded from any thread under a lock, submitted first in the same `vkQueueSubmit`: zero
ownership transfers, zero extra semaphores, zero blocking. (B, opt-in after measurement) a
dedicated transfer queue with exclusive-mode release/acquire barriers and a `Transfer` timeline
wait folded into the frame submit. Staging: one persistently mapped slice per slot
(`FIF × 32 MiB`), bump-allocated; oversized or overflow uploads take a dedicated staging buffer
retired on the timeline and are counted. **No upload ever waits.** Mip generation is a blit
chain on the graphics upload buffer. Persistent-mapped meshes keep the contract's "reproduce the
GL race" default; `OPTIMUM_VULKAN_MESH_DOUBLE_BUFFER=1` gives a per-slot copy with dirty-range
replay for validation-clean test runs. Static meshes move off ReBAR to device-local memory via
staging (the named defect).

**Readback in a frame.** `ReadbackManager.CopyToHost`: end the open pass, barrier, copy to the
slot's readback arena, `SubmitPartial()` (ends and submits the command buffer, begins a new one
**in the same slot**, arenas keep their cursors). `FlushFrame` and the mid-frame
`_frameCounter++` are deleted. Only the screenshot path waits, on that one timeline value.

**Presentation.** Split submission: Submit A (upload CB + frame CB, signals `Frame@v_render`);
**then** `vkAcquireNextImageKHR`; Submit B (FSR or the flipped blit into the acquired image,
waits `Frame@v_render` at `COLOR_ATTACHMENT_OUTPUT` and the acquire semaphore at `TRANSFER` or
`COLOR_ATTACHMENT_OUTPUT`, signals the binary present semaphore and `Frame@v_present`);
present. The `ALL_COMMANDS` wait disappears and the CPU blocks on acquire only after the whole
frame is in flight. Present policy `BlitFromOwned` (default: the frame including GUI renders
into the owned default image, acquire at the end) or `DirectToSwapchain` (experiment, negative
viewport flip). Present mode: FIFO with FIFO_RELAXED promotion on missed vsyncs; MAILBOX or
IMMEDIATE with vsync off; `minImageCount = max(caps.min + 1, mailbox ? 3 : 2)`. Recreation
follows the Khronos `swapchain_recreation` sample: `oldSwapchain` always passed, no
`DeviceWaitIdle`; a `SwapchainSlot` owns its images, views, acquire-semaphore free list
(`imageCount + 1`) and per-image present semaphores and retires as one unit after the last
present submission that referenced it; `SUBOPTIMAL` rebuilds before the next acquire,
`OUT_OF_DATE` rebuilds and re-acquires once; zero extent parks the present path.

**Frame graph.** Passes are declared and recorded **in frame order** (the client's frame is
imperative and pass existence is dynamic: bloom, SSAO, transparent pass, mod stages). Barriers
and layouts derive immediately from a per-subresource state tracker (layout, last write
stage/access, visibility, read stages, queue family; one entry per image, interval list only
when a pass touches a sub-range). Load/store ops, discards and transient aliasing come from a
**plan** computed from the previous frame's signature (ordered pass signatures: attachments,
depth usage, read set, extent, formats); a plan is applied only on an exact signature match, a
mismatch costs one conservative frame (LOAD/STORE, no aliasing). All barriers of a pass go into
one `vkCmdPipelineBarrier2` before `vkCmdBeginRendering`; stage/access come from the pass usage
table (colour write, depth write, depth read-only sampled, fragment/vertex sample, transfer,
indirect, vertex/index, present), never from the layout alone. Pass kinds: raster, blit,
compute (reserved), present. Mod-hosted stages (`AfterOIT`, `AfterFinalComposition`,
`AfterBlit`, `Ortho`) use `OpenSampling` (pre-transition every sampled-capable non-attachment)
and `AllowSplit`.

The platform derives the fixed frame from `(render stage, bound target)` plus its own post
methods. M1 pass set: ShadowFar, ShadowNear, Before, Opaque (Primary with all attachments
declared once, motion mask 0 by default), OIT (Transparent target), MergeTransparent, AfterOIT,
LiquidMotion and SkyMotion (motion-only write masks), TaaResolve, TaaSharpen, SSAO, Bloom
chain, GodRays, Luma, FinalComposition (attachment-subset pass: writes Primary 0, samples
Primary 1, one barrier each way per frame), Blit (FSR or plain), AfterBlit, Ortho, Present.

**Invariants pinned by tests**: one `vkCmdBeginRendering` per pass (`ScopesOpened == PassCount`);
no layout transition inside a scope; every sampled texture is in the pass's read set or the
pass is `OpenSampling`; a plan applies only on exact match; a clear on a zero-write-mask
attachment is a no-op on every path (`CLAUDE.md` rule 9 bug class); a resource is destroyed
only after every timeline value recorded against it passed; a swapchain's semaphores die with
it; ReBAR holds only per-frame dynamic data and a fall-through is logged and counted; the Y
flip happens exactly once.

**Motion windows and draw-buffer masks are write masks, never scope restarts.** Effective mask
per attachment = `drawBufferEnabled ? colorMask : 0`, then masked by the program's written
outputs. Tiers: `VK_EXT_color_write_enable` (exact `glDrawBuffers`, zero extra pipelines) →
`VK_EXT_extended_dynamic_state3` (`ColorWriteMask`, and `ColorBlendEquation` collapses the blend
key) → write-mask set interned into the pipeline key (bounded: programs used inside a window
× 2). Each tier forceable by env and tested.

**Clears** issued with no pass open become the next pass's `LOAD_OP_CLEAR` (standalone
`vkCmdClearColorImage` if read first); inside a pass they stay `vkCmdClearAttachments` and are
counted. `SnapshotColorAttachment`'s permanent shadow copies become pooled per-pass transient
copies (`ReadSelf`). Transient aliasing (post chain slots) is off by default
(`OPTIMUM_VULKAN_ALIAS=1`) until sync validation is clean on all targets.

**Memory.** Pool classes: `DeviceImages` (128 MiB blocks), `DeviceBuffers` (64), `Staging`
(32), `ReBar` (16, per-frame dynamic data only, capped at min(192 MiB, budget × 0.25), miss =
logged fall-through), `Transient` (64), `Dedicated` (via `VkMemoryDedicatedRequirements` or
size ≥ block/4). `VK_EXT_memory_budget` reported per heap, pressure callback drops spare
blocks and cold descriptor entries; without it budget = heap × 0.7. No general defrag: empty
blocks freed after 120 empty frames; optional bounded relocation of static geometry only.

**Draw submission.** Per-slot indirect ring in ReBAR, reset at `BeginFrame`, grown at frame
boundaries (replaces the wrapping ring). Bone matrices move to a storage-buffer ring with
dynamic offsets (lifts the 64 KiB UBO limit, tight packing under `scalarBlockLayout`); the
per-(frame, version) snapshot dedup stays; ring exhaustion grows and reports instead of
dropping. Dynamic state is dirty-masked (today 12 commands on every draw). `GetError()` becomes
a volatile counter read. Occlusion queries: per-slot pool + `vkCmdCopyQueryPoolResults` into a
host buffer, polled without any API wait (one frame late, like GL's availability polling).

**Descriptors and pipelines.** M1 keeps the existing rewriter layout and `DescriptorCache`, and
adds a per-slot `DescriptorArena` for short-lived resources (GUI text, atlas tasks) reset
wholesale per frame. Pipeline key gains `RenderingFormatsId` from the **pass** (stable across
mask toggles) and loses the blend/write-mask dimensions where the dynamic tiers exist. Disk
pipeline cache (`vkGetPipelineCacheData`, keyed on device/driver/pipelineCacheUUID/build id),
SPIR-V cache for mod shaders, a manifest of used keys and a background warm-up with
`pipelineCreationCacheControl` land in Phase 4.

**Diagnostics.** `VulkanStats` gains: blocking uploads (uploads that really waited), blocking
waits by site, acquire/present/fence wait ms, frame-time p50/p95/p99/stddev and stutter count
(>2 × p50) over the last 512 frames, passes vs BeginRendering, barriers, self-read copies,
transient/aliased bytes, plan hits/misses, heap used/budget, ReBAR fallbacks, pipeline and
descriptor hits/misses, dynamic-state commands, push-constant flushes, uniform ring use, and a
per-pass GPU time table from timestamp queries (Phase 4). `RenderTrace` gains pass/barrier/
submit/acquire/present lines. `OPTIMUM_VULKAN_POISON=1` fills fresh images and buffers with
NaN/`0xDEADBEEF` so undefined reads are loud. The GPU test suite runs sync + best-practices
validation **by default** with a `NoSyncHazards` assertion.

### C. Shaders

**Sources.** `sources/shaders-vk/<program>.vert|.frag` (GLSL 450, Optimum-authored, never an
asset; `.vert/.frag` so no packager glob over `sources/shaders/*` can pick them up) plus
`sources/shaders-vk/include/` (`bindings.glsl` single source of truth for sets, `globals.glsl`,
`warp.glsl`, `motion.glsl`, `fog/shadow/colormap/sky/oit/noise/vertexflagbits.glsl`), resolved
by glslc `-I`. The GLSL 330 assets in `sources/shaders/` keep shipping and keep being read:
`ShaderProgram.collectUniformNames` (`ShaderProgram.cs:56-66`) regexes `Shader.Code` for the
uniform-name set and texture declaration order, which is the client's oracle. The native path
supplies only placements and bindings; no `ShaderRegistry` change is needed.

**Set convention** (frequency-ordered; mirrored in `Shaders/SetConvention.cs`, a test asserts
`bindings.glsl` and the C# agree):

| Set | Update | Contents |
|---|---|---|
| 0 frame | once per frame | `FrameGlobals` UBO (every uniform `ShaderProgramBase.Use()` auto-binds plus the `OptimumTemporal` record) and the fixed frame textures `shadowMapFar/Near`, `sky`, `glow`, `liquidDepth` |
| 1 pass | once per pass | `PassParams` UBO (dynamic offset) and pass inputs (scene, glow, depth, motion, history×3, gbuffer, bloom, godrays) |
| 2 material | bound once | sampled textures and samplers as a plain array; bindless (partially bound, update-after-bind) is a Phase 4 option decided by the measured descriptor miss rate |
| 3 draw | dynamic offset per draw | `DrawData` UBO (model/prev-model matrices, per-draw warp/tint overrides, flags), `FaceData` SSBO, `Animation`/`AnimationPrev` SSBOs |
| push (≤128 B) | per draw | the few scalars written between draws of one program (material index, origin, z-offset, flags), chosen per program from a measured write-frequency profile |

**Uniform placement.** `GetUniformLocation(program, name)` returns an index into the program's
placement table `(home: Push | Frame | Pass | Draw | SamplerUnit, offset, size)`, `-1` when the
variant compiled the name out (`HasUniform` keeps returning true, as GL does). `SetUniform*`
is a table lookup and a memcpy into the right shadow, flushed once per draw (push) or per
frame (frame). `Use()` is not touched in Phase 3; its ~50 frame-global writes per program use
land in the frame shadow (a debug tripwire flags a frame-global written with two different
values in one frame). Skipping the include block in `Use()` is a Phase 4 optimisation,
measured first.

**Define matrix.** Code-path flags (FXAA, BLOOM, NORMALVIEW, FOAMEFFECT, SHINYEFFECT,
WAVINGSTUFF, GREEDYMESH*) and quality values (GODRAYS, SSAOLEVEL, SHADOWQUALITY, MINBRIGHT)
become specialization constants with gated varyings/samplers/outputs declared unconditionally
(outputs masked by `writtenOutputs`); DYNLIGHTS is removed (array fixed at `MAX_DYNLIGHTS`,
loop bound = the existing `pointLightQuantity` uniform); MAXANIMATEDELEMENTS is fixed;
TAAMOTION+TAAMOTIONLOCATION stays a **variant axis** (off / on@2 / on@4; output locations
cannot be specialized); USEOIT a variant on the two OIT programs; USESSBO fixed to 1 (the
`Chunkshadowmap_NoSSBOs` registration is the one 0 variant). Result ≤ 6 variants per program,
~600 SPIR-V blobs. A settings change becomes a pipeline-key change, not a shader reload.

**Offline compile.** `tools/shader-compiler/Optimum.Shaders.Compiler.csproj`: `--build`
(glslc `--target-env=vulkan1.3 -O`, then SPIR-V reflection into `shaders.manifest.json`:
schema version, toolchain, per program per variant the defines, spec constants, stage blobs with
sha256, uniform placements, samplers, blocks, vertex inputs, fragment outputs,
`writtenOutputs`), `--verify` (recompile, compare hashes; the `make check-shaders-vk` gate),
`--single`. MSBuild target on `Optimum.Render.Vulkan.csproj` with a content-hash cache.
Deploy to `<game>/Optimum/shaders-vk/` beside the DLL, never into `assets/` (the asset manager
must not read SPIR-V, the scanner scans `assets/*/shaders`, and a mod must not shadow engine
SPIR-V by asset priority); `Makefile` and every `scripts/package-*` copy it with the existing
`cmp -s` completeness check. Load once, verify hashes lazily, **fall back per program** to the
rewriter; one log line `[Optimum] shaders: N native, M rewritten, K failed`.
`OPTIMUM_VK_SHADER_SOURCE=<dir>` compiles the tree at runtime through shaderc for the dev loop;
a test asserts runtime and offline SPIR-V are byte-identical for a sample program.

**Mod-shader adapter.** The rewriter targets the same four sets: loose uniforms → set 3 per-draw
block, samplers → set 2 plain array, SSBOs → set 3, and any loose uniform whose name matches a
`FrameGlobals` member → set 0 (so a mod shader including `fogandlight.fsh` keeps working
unchanged). Confined to `ProgramInterfaceLayout.Build` plus a frame-global name map; a test
compares the descriptor-set layouts of a native and an adapter program.

**Temporal contract.** One writer: `include/motion.glsl` with
`optimumWriteMotion(mv, reactive, writerDepth)` and `optimumWriteReactiveOnly(reactive)`
(the "b without rg" rule becomes a signature property); a source test fails any other
assignment to `outMotion`. Every TAAMOTION variant's manifest entry lists the motion output at
`TAAMOTIONLOCATION` in `writtenOutputs`. The eight `Taa*Motion*Tests` gain native-vs-rewriter
differential cases (same inputs, motion attachment equal within 1 ULP of RGBA16F). Phase 3
records explicitly whether the contract gets a dated v1 addendum (provenance only) or a v2.

**Launcher scan v2** (`Optimum.Launcher/ShaderCompatibilityScanner.cs`): new
`ShaderAssetOverride` class reporting overridden vanilla program names (those use the rewriter;
an overridden `shaderincludes/*` forces all programs); new `PlatformInternals` indicator
(Harmony + `ClientPlatformWindows`/`ShaderProgramBase` strings) → `openGlRequired`; `RawOpenGL`
unchanged; `CurrentSchemaVersion` 2. `VULKAN-BACKEND-PLAN.md` §9 states that a Harmony patch on
a platform graphics member is not honoured on Vulkan.

### D. Mod policy (decision 2 made concrete)

Free, no mod change: everything through `IRenderAPI`/`IShaderAPI`/`ICoreClientAPI` (meshes,
textures, render-to-texture, GUI, fixed-function state, screenshots, shaders through the
rewriter), because it lands on the platform virtuals inside a declared pass; `RegisterRenderer`
renderers sit inside `BeginRenderStage`/`EndRenderStage`. Unsupported (launcher routes to
OpenGL): direct OpenTK GL; Harmony patches on platform graphics members. Vanilla-shader
overrides by mods bypass the native blob for that program only. Phase 5 adds the opt-in
mod-facing pass and motion-writer API in the contracts (`EnumOptimumPass`, `OptimumPassDecl`
data holders; no lib types).

---

## Phases and exit criteria

Every phase: `dotnet build VintageStory.slnx -c Release`; `dotnet test Optimum.Tests -c Release`;
`dotnet test Optimum.Render.Vulkan.Tests`; `bash scripts/extract-patches.sh && bash
scripts/check-patches.sh`; `make deploy`; then the in-game check listed, once per backend,
renderer confirmed with `scripts/dev/client-renderer.sh`, game closed with the kill script.
In-game runs happen only at phase exits.

### Phase 0: foundations (no behaviour change)

- Step 0 branches.
- Patcher: `typesToUnseal`, `methodsToVirtualize`, the `call`→`callvirt` verifier.
  Tests: `Optimum.Tests/member-injector-tests.cs` (flags preserved, synthetic stray `call`
  fails), `platform-substitution-coverage-tests.cs` (entries present).
- Diagnostics: `VulkanStats` counters above, `OPTIMUM_FPS_LOG` gains `stddev`,
  `scripts/dev/perf-capture.sh` captures, `scripts/dev/pacing-gate.sh --renderer vulkan --fps <fps.log>
  --stats <stats.log> --baseline <gl fps.log>` judges (exit non-zero unless blocking uploads = 0 in
  every sample, median window stddev ≤ baseline × 1.25, median window p99 ≤ 1.5 × median mean,
  dropped mesh writes = 0, uniform overflows = 0), format-coverage test for the stats line,
  `docs/taa-acceptance.md` §3 and `perf-capture.sh` parser updated.
- GPU tests default to `sync,best` with `ValidationAssert.NoSyncHazards`.
- GL-side attachment dump (`glGetTexImage` in `ClientPlatformWindows`, env
  `OPTIMUM_PARITY_DUMP=<dir> OPTIMUM_PARITY_FRAME=<n>`, Vulkan side reuses `TextureDump`),
  `scripts/dev/parity-capture.sh`, `scripts/dev/ssim.py` (per attachment SSIM + mean abs diff),
  `docs/parity-allowlist.md`, coverage test that the dumped slot list matches
  `SetupDefaultFrameBuffers`.
- `docs/vulkan-acceptance.md` skeleton (preconditions, renderer line per row, rows per
  milestone, methods, decision record, vendor matrix).

Exit: builds and suites green; deploy output unchanged except stats; both dump paths executed in
the real client; GL-vs-GL noise floor recorded per attachment (two launches of one save are not
bit-deterministic: world time, weather, entities and particles move, so this is a floor that
Milestone 1's 0.98 threshold must stand above, not a 1.000 gate); Vulkan-vs-GL table recorded as
Milestone 1's starting point; baseline pacing numbers for both backends on the fixed scene recorded
in `docs/vulkan-acceptance.md`.

**Phase 0 status (2026-09-11).** Merged on `feat/vulkan-native` at `cdd7412` (stages 1dcbb29,
3e1170c, 75a984f, 9588f3e; integration 4d1089f; review cdd7412). Build 0 errors; Optimum.Tests
1056 passed; GPU suite 386 passed with `sync,best` and zero sync hazards; real Cecil patch 257/257
methods, 1 type unsealed, 4 methods virtualized, 0 non-virtual call sites. Deferred from the
Diagnostics list to the phase that builds the subsystem: heap used/budget (1B step 5), pipeline
and descriptor hits/misses and push-constant flushes (Phase 4), RenderTrace pass/barrier/submit/
acquire/present lines (Phase 2). Known: poison-mode image clears count as blocking uploads
(diagnostic mode only); the GL `glGetTexImage` dump had never executed before the exit run.

**Phase 0 exit run (2026-09-11, RTX 4070, driver 615.71.09, both backends; recorded in
`docs/vulkan-acceptance.md` and `docs/gpu-verification-2026-09-11/phase0/`).** Both dump paths
executed. Pacing, median per-second windows: OpenGL 6.08 ms mean / 8.51 p99 / 0.55 stddev; Vulkan
9.90 / 20.08 / 4.96, gate fails on p99, stddev and blocking uploads (median 50/s, max 193). Vulkan
per-second medians: flush-frame 151 (occlusion-query reads 101: `GetQueryResult` flushes every
frame), rendering scopes 4040, barriers 6766, dynamic-state commands 172256. Parity: GL-vs-GL
launches differ (far shadow map 0.947, Primary colour 0.968), so Milestone 1's parity rule is now
relative to the same session's GL-vs-GL floor. Real gap found: SSAO g-buffer colour1 alpha is 1.0
on GL and 0.0 on Vulkan (unwritten channel, rule 9); it goes into Phase 2 with the write-mask work.
User judged these two Vulkan runs free of the earlier distance jitter (sky-direction fix not deployed); in every later Vulkan run the jitter was back, and OpenGL never shows it, so the jitter is Vulkan-only and intermittent between sessions and remains the Milestone 1 target.
Phase 1B priority from these numbers: `QueryRing` (removes ~1 flush per frame) and the upload path
first, then present.

### Phase 1A: platform substitution (re-plumbing; pixels unchanged)

Runs in parallel with 1B (disjoint files).

1. `VulkanClientPlatform` as a forwarding subclass over the existing `VulkanDevice`;
   `SetupOptimumFrameBuffers` (lines 1629-1916) moves out as the `SetupDefaultFrameBuffers`
   override; `ClientProgram.Start` per the table above; csproj donor reference.
2. TAA members to the abstract class; the seven casts become virtual calls; the 14
   `Optimum.Tests` files that read `ClientPlatformWindows.cs` are re-pointed at the abstract
   class as a pure-move commit (bodies diffed textually).
3. Program/uniform/UBO virtuals; `ShaderProgramBase.cs` and `UBO.cs` revert to vanilla plus
   `ScreenManager.Platform.<virtual>`. Measure per-draw CPU on both backends before and after.
4. Remaining 19 leaf sites; delete `IOptimumGraphicsDevice`, `OptimumRender.Device`,
   `OptimumRenderBootstrap.Install`; `ClientPlatformWindows` is branch-free; `Program.cs` entries
   for it drop from 88 to the handful of edit sites.

Tests: every abstract graphics member and every GL-touching `ClientPlatformWindows` method has
an override in `VulkanClientPlatform.cs` or is on the base-edit list (source test that greps
`GL.` per method); no lambda in the new `ClientProgram.Start` region; the fallback block
re-assigns `ScreenManager.Platform`; no `OptimumRender.Device` and no `ClientPlatformWindows`
cast under `build/VintagestoryLib/**`; `ClientPlatformWindows.cs` differs from `_ref/` only in
the listed regions; `PlatformSubstitutionTests` (construct headless, `is ClientPlatformWindows`,
`InitializeGraphics` on a hidden NoAPI window brings up the swapchain); every existing GPU
readback test driven through the platform gives identical pixels.

Exit: all of the above green; in game, one screenshot per backend identical to Phase 0's; the
reopen-and-swap fallback exercised once with `OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE=1` and the
log showing `[Optimum] Vulkan unavailable, reopening for OpenGL`.

### Phase 1B: synchronisation foundation (behind the current entry points)

1. `FrameTimeline` + `RetireQueue`; `FrameRing` on timelines. Gate: existing multi-frame tests;
   blocking waits = 1 per frame.
2. `UploadManager` + per-slot upload command buffer (backend A); texture, mip and bulk mesh
   uploads route through it; delete `SubmitAndWait` and `FlushFrame`. Gate: **blocking
   uploads = 0** during world load and a 10-minute session (the M1 headline number).
3. `ReadbackManager` + `SubmitPartial`; `QueryRing`. Gate: screenshot readback test; readback
   mid-frame then more draws then present stays correct; sun glare still varies in game.
4. `Swapchain`/`SwapchainRetirement`/`IPresentPath` split submission. Gate: resize, alt-tab,
   minimise loop clean under `sync,best`; acquire wait stage never `ALL_COMMANDS`; frame-time
   stddev before/after recorded.
5. `VulkanAllocator` pool classes + budget; static meshes off ReBAR. Gate: allocator policy
   tests; heap report; no chunk-streaming regression.
6. Per-slot indirect ring; descriptor arena; dirty-masked dynamic state; free `GetError`.
   Gate: CPU frame time drop measured; draw counters unchanged.

Tests (GPU, `Optimum.Render.Vulkan.Tests`): `AsyncTransferTests` (upload from a worker thread
while frames record, Present between frames, read back on N+2, `BlockingUploads == 0` over 60
frames with atlas inserts, Cairo updates and chunk meshes interleaved), `PresentDecouplingTests`
(`VulkanContextOptions.AcquireDelayForTests`; recording time does not grow with the delay),
`SwapchainRecreationVisualTests`, `ConcurrentDeviceAccessTests`, `ReadbackMidFrameTests`,
`QueryRingTests`, `AllocatorPolicyTests`; pure unit tests `IndirectRingWrapTests`,
`TimelineLifetimeTests`, `PresentWaitStageTests`, `SwapchainRetirementTests`.

**Phase 1 exit (2026-09-11, f373c4a).** 1A and 1B merged and reviewed (Optimum.Tests 1128, GPU 494,
patch run 197/197, dispatch verifier clean). In game on the RTX 4070: both renderers start; forced
install failure falls back to OpenGL and renders; sync,best validation 0 errors; Vulkan blocking
uploads 0 in all samples; Vulkan pacing 8.21 ms mean / 18.07 p99 / 3.74 stddev (Phase 0: 9.90 /
20.08 / 4.96). OpenGL pacing is bimodal between launches regardless of build (A/B/A), so M1.1
interleaves runs. Carried to Milestone 1: 10-minute session, window loop, sun glare and fork bridge
on screen, the SSAO alpha gap, foliage NaN normals (Phase 3). User direction after this exit: less
testing, more parallel workflow stages.

**TAA distant-foliage jitter resolved (2026-09-11, 22:33).** Root cause was the resolve shader, not the
backend: a single-sample depth disocclusion test dropped history on ~3.7% of distant leaf pixels per
frame (sub-pixel leaf vs far background across jitter phases), and a fixed blend weight let the moving
clip box drag history. Fix in `sources/shaders/taa-resolve.fsh`: 3x3 nearest-depth disocclusion with
motion from the nearest-depth tap, and luminance-based anti-flicker weighting (0.3x..1.2x blendAlpha).
User judged Vulkan "perfectly stable, better than it ever was" at the default two frames in flight.
Ported with GPU and source tests on `fix/taa-antiflicker-disocclusion`, merged after Phase 2. Also
found, unfixed: Vulkan `BuildMipMaps` keeps the atlas texture LOD bias where OpenGL resets it to 0
(affects shadow, liquid and transparent terrain passes; shadow maps measured identical, so not visible).

### Phase 2: frame graph → **Milestone 1**

1. `ResourceStateTracker` + `BarrierBatcher` driving the existing immediate path (derived
   stages replace `ALL_COMMANDS`; no graph yet). Gate: sync clean; barrier count reported and
   reduced.
2. `FrameGraph` streaming recorder + `PassRecorder`; the platform declares the M1 pass set from
   `(stage, target)` and its post methods; lib gains `BeginRenderStage`/`EndRenderStage`.
   Both paths coexist behind `OPTIMUM_VULKAN_FRAMEGRAPH`; a declared-reads violation splits.
   Gate: pixel-identical readbacks vs the non-graph path at four settings combinations;
   `ScopesOpened == PassCount`.
3. Write-mask motion windows (all three tiers), clear promotion, `FramePlan` load/store
   solving; TAA resolve/sharpen/sky-motion/liquid-motion through the graph, contract unchanged.
   Gate: motion-attachment bit-exactness; history accumulates over 8+ frames in a multi-frame
   test with no readback inside the loop.
4. Transient aliasing implemented, default off.

Tests: pure `FrameGraphBarrierTests` (RAW/WAR/WAW/layout table, swapchain ends `PRESENT_SRC`,
aliased first use `UNDEFINED`, no reader → no barrier), `FramePlanTests` (signature match,
load/store solve, alias intervals never overlap); GPU `FrameGraphFrameTests` (the real declared
frame for 5 frames, TAA accumulates, scopes == passes, zero `SYNC-` messages),
`MotionWindowTests` per tier, `FeedbackPassTests` (final composition write-0/sample-1; ReadSelf
copy), `ClearPromotionTests` (masked-out clear is a no-op), `AttachmentSemanticsTests` and
`WorldRenderPathTests` stay green; `Optimum.Tests`: `TriggerRenderStage` brackets the event
and is a Cecil target.

**Milestone 1 definition of done** (all numbers, then eyes):
- `pacing-gate.sh` passes against the OpenGL baseline of the same scene.
- Blocking uploads 0 during load and a 10-minute session; blocking waits 1 per frame.
- Acquire happens after the render submit; acquire wait stage is `TRANSFER` or
  `COLOR_ATTACHMENT_OUTPUT`.
- `ScopesOpened == PassCount`; no transition inside a scope.
- `sync,best` validation: zero `[error]` over the scripted session (menu → world → weather →
  water → night → resize → shader reload → screenshot → exit).
- Per-attachment SSIM vs OpenGL, TAA off ≥ min(0.98, same-session GL-vs-GL SSIM − 0.01) on every
  attachment, or an allowlist row (launches of one save are not bit-identical).
- TAA on: still-frame luma-diff median over 7 pairs within 0.3 of the OpenGL median
  (reference VK 1.84 / GL 1.87, `docs/taa-acceptance.md:55`); `docs/taa-acceptance.md` rows
  A11, A13, A14, A15, A17, A18 re-pass.
- Then the user judges it in game on both backends, renderer line confirmed.

**Milestone 1 accepted (user, 2026-09-11, at `6568556`).** Phase 2 complete: barriers from usage, frame
graph (22.3 passes == 22.3 scopes per frame, 0 splits, 0 mask restarts, plan hits every frame), transient
allocator (implemented, not yet wired to the graph), clear promotion, SSAO alpha gap closed, TAA
anti-flicker resolve merged (distant-leaf rejection 1.05 % on both backends). Open and carried to Phase 4:
Vulkan costs ~25 % more frame time than OpenGL on the fixed scene (7.59 ms vs 6.08, stddev 0.37 vs 0.12)
and is GPU-bound (5.43 ms of the 7.67 ms frame in the frame-pacing wait), so the pacing gate fails its
stddev rule; per-pass timestamps come first. Also open: wire `TransientAllocator` into the graph,
`ClearDepth` ignores the depth write mask, `BuildMipMaps` LOD-bias parity. Testing policy tightened by the
user: no long sessions, no per-attachment SSIM matrices, one short run plus the cheap numbers.

**Branching at Milestone 1 (user, 2026-09-11):** once Milestone 1 is accepted, `feat/vulkan-native`
merges back into `main` (with `fix/taa-antiflicker-disocclusion` merged into it first), and the next
work (DLSS) starts on a new branch from the updated `main`. No DLSS or later-phase work lands on
`feat/vulkan-native`.

### Phase 3: native shaders

Set convention, placement table, manifest, compiler tool, adapter layout (rewriter retargeted
to sets 0-3 in the same commit as set 0 lands, so there is one layout change, not two), native
GLSL in seven worktree stages (includes + six fullscreen/post programs first; GUI/lines/
texture2texture; chunk family incl. `NoSSBOs`; entity family incl. OIT variant; particles/
decals/sky/clouds; SSAO/godrays/bloom/colorgrade/OIT compose/debug; the seven Optimum programs) (taa-resolve keeps the 2026-09-11 fix: 3x3 nearest-depth disocclusion with motion from the nearest-depth tap and luminance anti-flicker weighting, pinned by the GPU tests on fix/taa-antiflicker-disocclusion and by `scripts/dev/taa-rejection.py`; never a single-sample depth test),
scanner v2, the contract addendum-or-v2 decision, `ReloadShaders` no longer recompiling on a
settings change.

Tests: `vk-shader-parity-tests.cs` (per program per variant: uniform-name set, sampler name set
and order, vertex-input locations, fragment-output count equal to the GLSL 330 source through
the existing `ShaderCorpus`; a vanilla shader change in a game update fails here instead of on
screen), `vk-motion-writer-shape-tests.cs`, manifest schema and consistency tests,
`AdapterLayoutMatchesNativeLayoutTests`, the native-vs-rewriter differential motion tests,
`Optimum.Launcher.Tests` fixtures (a mod overriding `chunkopaque.fsh` marks only that program;
a `shaderincludes` override marks all; Harmony + platform string → `openGlRequired`), a test
that `sources/shaders-vk/` contains no `.vsh/.fsh`.

Exit: log line reports 48 native / 0 failed; parity and differential tests green; per-attachment
SSIM ≥ 0.99 or allowlisted; validation clean; in game the settings sweep (SSAO 0/1/2, shadows
0/1/2, bloom, god rays 0/1/2, FXAA, render scale 0.5/1.0/1.5, waving foliage) on both backends,
plus the full `docs/vulkan-acceptance.md` matrix; contract decision recorded.

### Phase 4: performance

Disk pipeline cache + used-key manifest + warm-up; push-constant placement from the measured
profile (`OPTIMUM_VULKAN_UNIFORM_PROFILE`) frozen into the manifest for the 48 programs;
animation SSBO ring; `Use()` include-block early-out (measured first); per-pass GPU timestamps
(`timestampValidBits` gated); transient aliasing default on after clean validation on all
targets; bindless set 2 only if `DescriptorCache.Misses` per frame in a loaded world justifies
it; `DirectToSwapchain` and transfer backend B measured, kept only where they win.

Exit: on the fixed scene Vulkan mean FPS ≥ OpenGL and p99 ≤ OpenGL on this machine, numbers in
`docs/vulkan-acceptance.md` §6 (Arc 140V row filled when the handheld is available); pipeline
cache hit rate ≥ 95 % on second launch; per-pass ms table sums to within 10 % of GPU frame time;
`perf-capture.sh` × 4 (both backends × TAA on/off) plus a 30-minute session.

### Phase 5: mod API and fork ports

Contracts pass API and opt-in motion-writer API (data holders only); `VSEssentials` /
`VSSurvivalMod` / `VSCreativeMod` renderers checked against declared passes; a fixture
shader-pack mod on the rewriter; three real mods from the user's library. Exit: forks run with
no concrete-cast fallback; fixture renders; scanner v2 launcher tests green.

**Sequencing note (user, 2026-09-11):** DLSS is the next goal once the native backend is ready. DLSS
depends only on Phase 2 (native device and extension enablement, named graph handles for jittered
colour, depth, motion and the HUD-less scene) and the frozen temporal contract's DLSS adapter row;
Phases 3 to 5 are not prerequisites. Proposed order: Milestone 1, then the Phase 6 upscaler seam with
DLSS (Streamline/NGX on the RTX 4070) as the first vendor, XeSS for the Arc 140V next, then Phases 3 to 5.

**Decision (user, 2026-09-11): Optimum builds its own vendor orchestrator; Streamline is not the
multi-vendor layer.** Evidence: Streamline 2.14.1 defines only NVIDIA features plus Microsoft DirectSR
(D3D12) and ships no Intel or AMD plugin; NVIDIA's answer in NVIDIA-RTX/Streamline issue #12 (2024-03)
was "implement the plugins yourself"; production builds load a plugin only when
`sl::security::verifyEmbeddedSignature` passes, which requires a secondary NVIDIA signature
(`include/sl_security.h`, `isSignedByNVIDIA`), so self-built XeSS/FSR/AntiLag plugins cannot run under
the shipped runtime; Streamline is Windows-only (`sl.interposer.dll`, Windows 10 RS3+). Optimum's
orchestrator owns three slots with one backend per vendor: upscaler (DLSS, XeSS, FSR), latency
(Reflex via VK_NV_low_latency2, XeLL, VK_AMD_anti_lag) and frame generation (DLSS-G, XeFG, FSR frame
interpolation).

**The slots are coupled: a vendor latency backend only when the upscaler's vendor matches the GPU, else
our own (user, 2026-09-12).**

| upscaler \ GPU | NVIDIA | AMD | Intel |
|---|---|---|---|
| DLSS / DLSS-G | Reflex (`VK_NV_low_latency2`) | - | - |
| FSR | Native | `VK_AMD_anti_lag` | Native |
| XeSS (+ XeFG) | Native | Native | XeLL on the Windows D3D12 bridge, Native on Vulkan/Linux |
| none | device-based auto: NV, AMD, Native, None |

A cross-vendor pair (FSR on NVIDIA or Intel, XeSS on AMD or NVIDIA) takes Optimum's own completion pacing:
the vendor stacks are only specified and tested against their own upscaler, and mixing them risks the
frame-attribution and pacing model each one builds. A match takes the vendor tech, which is also what the
vendors require (Reflex with DLSS-G, XeLL with XeFG) and keeps one marker stream per frame.
Implementation: the selector takes the active upscaler's vendor as an input, matches it against the GPU
vendor, logs the decision, and `OPTIMUM_VULKAN_LATENCY` still overrides for testing; it lands with the
upscaler slot on the DLSS branch, since `LatencyBackendSelector` is device-only today.

**NVIDIA backend goes direct, no Streamline on either OS (user decision, 2026-09-11: "go direct").** Reflex: `VK_NV_low_latency2` called by the renderer (NVIDIA's Linux driver guide:
native Linux Reflex is "not via the Reflex SDK but directly via the Vulkan extension
VK_NV_low_latency2"; `VK_NV_low_latency` is the legacy path for the Reflex SDK's `NvLowLatencyVk.dll`,
which is what driver 615.71.09's Proton note covers). Driver 615.71.09 on the RTX 4070 advertises
revision 2, so `VkLatencySubmissionPresentIdNV` attribution (revision 3+) is not honoured: query
the revision and rely on marker frame IDs plus `VK_KHR_present_id`. DLSS SR and DLSS-G: NGX Vulkan
helpers from the DLSS SDK 310.9.1 (`NGX_VK_CREATE_DLSSG` / `NGX_VK_EVALUATE_DLSSG`,
`libnvidia-ngx-dlssg.so` on Linux, `nvngx_dlssg.dll` on Windows), one code path for both OSes.
Cost of going direct: Optimum owns frame-generation pacing (DLSS-FG guide section 7: present the
generated frame when evaluate completes, present the retained real frame, via `OutputReal`, at
equal spacing from a present thread), which the XeFG and FSR backends need anyway. First DLSS-branch
step: a spike that initialises NGX on the 4070 and reads `FrameGeneration_Available` on native Linux
(no public native-Linux DLSS-G title to lean on). Licence (user, 2026-09-11): the NVIDIA feature
libraries ship as binary redistributables, never source, as OptiScaler (GPL-3.0) does. Binding: the
driver's `libnvidia-ngx.so.1` exports `NVSDK_NGX_VULKAN_*`, so the renderer P/Invokes it directly (no
native shim around `libnvsdk_ngx.a`); OptiScaler's `low_latency/` and `framegen/IFGFeature` are the
orchestrator design reference (its frame generation is D3D12-only).

**Intel on Windows: a D3D12 bridge present path with XeFG and XeLL (user, 2026-09-11).** XeSS-FG
has no Linux or Vulkan route: it is a D3D12 proxy swapchain only (Windows 10/11, DirectX 12, driver
32.0.101.7029+, Lunar Lake listed). XeLL comes with it: the XeFG proxy swapchain fails to initialise
without an XeLL context, XeLL markers and the XeFG present ID must share one frame counter, and "latency
reduction solutions other than XeLL are not supported" (`xess_fg_developer_guide_english.md:83-90,
218-255`); XeLL itself needs DirectX 12, DXGI flip model and sync interval 1, and on non-Intel GPUs it
only works with XeFG active (`xell_developer_guide_english.md:81-110, 188-201`). So on Windows the
Intel stack is a second `IPresentPath`: Vulkan renders as usual, the frame is handed to a D3D12 device
through shared images (`VK_KHR_external_memory_win32`, D3D12-created shared resources imported as
`VkImage`) and a shared D3D12 fence imported as a Vulkan timeline semaphore, and a DXGI flip swapchain
on the same window is wrapped by the XeFG proxy with XeLL attached (the user's SCS integration does the
same from D3D11). Consequences for the seams: the latency backend is chosen by the present path
(Vulkan swapchain: native tier, `VK_NV_low_latency2`, `VK_AMD_anti_lag`; D3D12 bridge: XeLL only); in
XeLL mode the native tier and the `FrameRing` pacing wait must not add waits ("should not implement any
additional submission logic, like waiting for previous frame to finish"), and the frame cap goes into
`xellSetSleepMode`. Linux Intel gets the native tier and no frame generation. Windows interop support
on the Arc driver is unverified: spike before building on it.

### Latency seams (after Milestone 1; branch `feat/latency`)

Planned 2026-09-11 from the touch-point map (workflow wf_4ff25509-50f) and the vendor research
synthesis. Branch `feat/latency` starts at `feat/vulkan-native` 6568556 so stages can run while
Milestone 1 is judged; it merges `main` once M1 lands there, so nothing lands on `feat/vulkan-native`.
Stages never run GPU tests while an in-game capture is running.

**Finding that drives it.** Today one frame is: OpenTK pumps OS events (`ClientProgram.cs:455`
`GameWindow.Run`, OpenTK 4.9.4) → `window_RenderFrame` (`ClientPlatformWindows.cs:716`) → the game FPS
cap (`:753-797`) → `UpdateMousePosition()` (`:815`, the mouse-delta gather) → `BeginFrame()` (`:828`,
where `FrameRing.BeginFrame` blocks on the Frame timeline, `FrameRing.cs:430`) → `OnNewFrame` (simulation
`ClientMain.cs:1299-1337`, camera consumes the delta in `PlayerCamera.OnBeforeRenderFrame3D` at stage
`Before`, `ClientMain.cs:1338`) → `EndFrame()` → `VulkanDevice.Present` (Submit A `:883`, acquire `:890`,
Submit B `:904`, `vkQueuePresentKHR` `Swapchain.cs:555`). Input is sampled, then the frame waits for the
GPU: up to FramesInFlight−1 frames of queued latency before any vendor feature. Every latency technology
wants the wait before input. All client work is on the one `GameWindow.Run` thread (the only other thread
is the singleplayer server), so simulation and render are coupled.

**Backends (one active at a time, chosen per present path).**
- `None`: markers recorded as CPU timestamps only.
- `Native` ("completion pacing", the Korthos `low_latency_layer` algorithm done in the renderer, no layer):
  before input, wait on the Frame timeline for the previous frame's Submit B value, then apply the frame
  cap measured release to release, then release. Works on every vendor and OS, including the Arc 140V on
  Vulkan and AMD (the Mesa anti-lag layer measured as a no-op). Cost: the GPU idles while the CPU records.
- `NvLowLatency2`: `vkSetLatencySleepModeNV` (mode, boost, `minimumIntervalUs`), `vkLatencySleepNV` plus a
  wait on its signal semaphore, `vkSetLatencyMarkerNV`, `vkGetLatencyTimingsNV`. Needs `VK_KHR_present_id`
  or `present_id2`. Revision read from `specVersion`; `VkLatencySubmissionPresentIdNV` tagging only at
  revision ≥ 3 and then on every submit of the frame (all or nothing); 615.71.09 is revision 2.
- `AmdAntiLag`: `vkAntiLagUpdateAMD` with stage INPUT (blocks; this is the sleep) and stage PRESENT
  immediately before `vkQueuePresentKHR`, same `frameIndex`, `maxFPS` = cap; feature
  `VkPhysicalDeviceAntiLagFeaturesAMD.antiLag`. The Intel iGPU here exposes it through
  `VK_LAYER_MESA_anti_lag`, which lets the code path run in tests.
- Later, Windows D3D12 bridge present path only: `XeLL` (see the Intel paragraph above); in that mode
  Optimum adds no waits and the cap goes to `xellSetSleepMode`.
- NV and AMD entry points load by address (`GetDeviceProcAddr` into `delegate* unmanaged`), as
  `VK_NV_device_diagnostic_checkpoints` already does (`VulkanContext.cs:180-184`); Silk.NET 2.23.0 has the
  structs but no NV/AMD wrapper classes, and no new NuGet package is added.
- Selection: `OPTIMUM_VULKAN_LATENCY=auto|off|native|nv|amd`, parsed like `DeviceCaps.FromEnvironment`
  (`ColorWriteTier.cs:37-74`: a forced backend the device lacks degrades); `auto` = NV on NVIDIA with the
  extension, AMD on AMD with the feature, else Native. Persisted setting `LatencyMode` (off|on|boost) in
  `OptimumConfig`, default off until the acceptance numbers exist. The chosen backend, revision and surface
  type (X11/Wayland) go into the "device up" log line (`VulkanDevice.cs:359-365`). An enabled
  `VK_LAYER_KORTHOS_low_latency` or `VK_LAYER_MESA_anti_lag` is logged, because it would pace on top.

**Seams.**
- **L0 types** (`Optimum.Render.Vulkan/Latency/`, namespace `Core`): `LatencyMarker` (values equal
  `VkLatencyMarkerNV`: SimulationStart/End, RenderSubmitStart/End, PresentStart/End, InputSample,
  TriggerFlash, OutOfBand RenderSubmit/Present Start/End), `LatencySettings` (mode, boost, one frame cap in
  microseconds, 0 = uncapped), `LatencyFrameReport` (the eight intervals every tool reports: input, sim,
  render submit, present, driver, OS queue, GPU, total), `ILatencyBackend` (requirements, `OnSwapchainCreated`,
  `Apply`, `Sleep(frameId)`, `Marker(frameId, marker)`, `TagSubmit`, `OnPresent(presentId, frameId)`,
  `TakeReports`), `NoneLatencyBackend`, a recording fake for tests.
- **S1 device requirements**: `IDeviceRequirementContributor` consulted in `VulkanContext.CreateInstance`
  (extension list `:254`) and `CreateDevice` (`:639-839`): instance extensions, device extensions with their
  `specVersion`, feature structs. The single-slot `optionalFeatures` chain (`:751-756`) becomes a pNext chain
  builder so colour-write and latency features chain together; `VK_KHR_get_surface_capabilities2` joins the
  instance list when available; `VulkanCapabilities` (`:53-80`) gains the latency backend, revision and
  present-id support. NGX's `GetFeatureInstance/DeviceExtensionRequirements` plug in here later.
- **S2 identity**: `LatencyFrameId` allocated once per frame at the sleep (replaces the private
  `_frameCounter`, `VulkanDevice.cs:57,726`, which stays the checkpoint source); a separate global
  `PresentId` incremented per `Swapchain.Present` and chained as `VkPresentIdKHR` on `PresentInfoKHR`
  (`Swapchain.cs:539-547`, pNext unset today) when `presentId` is enabled. One frame maps to one present
  until frame generation exists; the map is kept now. The Frame timeline stays the GPU clock only (it
  advances two or more values per frame).
- **S3 pre-input sleep** (lib): injected `public virtual void LatencySleep() { }` and
  `public virtual bool LatencyOwnsFrameCap => false;` on `ClientPlatformAbstract`; in
  `window_RenderFrame` the cap block (`:753-797`) runs only when `!LatencyOwnsFrameCap`, then
  `LatencySleep()` immediately before `UpdateMousePosition()` (`:815`), each call exactly once. GL has no
  override. Checklist from `BeginRenderStage`: `membersToInject` (`Program.cs:69-163`), `window_RenderFrame`
  already a transplant target, `ExpectedVirtuals` (`VulkanClientPlatform.cs:48-134`), a
  `latency-hooks-coverage-tests.cs` like `render-stage-hooks-coverage-tests.cs`, a headless test like
  `RenderStageHookTests.cs`. `VulkanClientPlatform.LatencySleep` → backend `Sleep(frameId)`, then markers
  InputSample and SimulationStart.
- **S4 markers** (renderer owns all of them, never double-stamped): SimulationEnd and RenderSubmitStart on
  the first `BeginRenderStage(Before)` through a second listener field beside `RenderStageListener`
  (`VulkanClientPlatform.Stages.cs:23-34`, wired at `VulkanClientPlatform.cs:284`); RenderSubmitEnd after
  Submit A (`VulkanDevice.cs:883`); PresentStart/End around `_swapchain.Present` (`:909`); AMD's PRESENT
  stage at PresentStart. A phase still open when the next frame starts is closed and logged once. Submit
  tagging (revision ≥ 3) goes in the shared `FrameSlot.Submit` (`FrameRing.cs:298-318`), which Submit A,
  Submit B and `SubmitPartial` all pass through; `UploadManager.SubmitStandalone` between frames stays
  untagged.
- **S5 swapchain**: in `Swapchain.Build` (`:296-366`) the capabilities query (`:300-301`) moves to
  `GetPhysicalDeviceSurfaceCapabilities2` when available, with `VkLatencySurfaceCapabilitiesNV` chained for
  the NV backend; `SwapchainCreateInfoKHR.PNext` (`:319-340`, null today) gets
  `VkSwapchainLatencyCreateInfoNV`; after `_current = new SwapchainSlot` (`:359`) the backend's
  `OnSwapchainCreated` re-applies the sleep mode, so resize, vsync toggle, OUT_OF_DATE and the FIFO_RELAXED
  promotion all re-apply it once; `SwapchainPolicy.ChoosePresentMode` (`SwapchainRetirement.cs:137-148`)
  intersects with the low-latency-capable modes.
- **S6 pacing policy**: `FramesInFlight` stays capacity; with a latency backend active the sleep is the
  pacing point and the `FrameRing.cs:430` wait should find its value already signalled
  (`WaitSite.FramePacing` near zero is an acceptance number). New `WaitSite.LatencySleep`.
- **S7 telemetry**: a `stats.latency` line on every sample (`VulkanStats.SampleIfDue`, emitted beside it in
  `VulkanDevice.BeginFrame` `:748-759`): `backend=`, `mode=`, `rev=`, sleep ms mean/p99, and the eight
  report intervals mean/p99 (NV from `vkGetLatencyTimingsNV`; Native and AMD from renderer timestamps plus
  the host-observed timeline completion); `pacing-gate.sh` parses it optionally.
- **S8 present path**: `IPresentPath` (`Present/IPresentPath.cs:13-22`) states which backends it supports;
  `BlitPresentPath`: None, Native, NV, AMD.

**Workflow.** Wave 1, serial: L0 (one opus-medium stage, owns `Latency/` types and the fake). Wave 2,
parallel worktrees: (A) lib hook S3 + patcher + coverage tests + `VulkanClientPlatform` override + config
setting; (B) S1 + capabilities + selection + NV/AMD entry-point bindings; (C) S2, S4, S5, S7 in
`VulkanDevice`, `FrameRing`, `Swapchain`, `VulkanStats` driven by the fake backend. Integrate. Wave 3,
parallel: Native, NV and AMD backends. Integrate, review.

**Acceptance (numbers, rule 10).**
- GPU tests with `sync,best`: marker order per frame id with the fake (sleep → InputSample →
  SimulationStart → SimulationEnd/RenderSubmitStart → RenderSubmitEnd → PresentStart → PresentEnd), ids and
  present ids strictly increasing across a resize, sleep mode re-applied once per swapchain creation,
  requirement contributors reach the created device without disturbing the colour-write tier; Native: the
  sleep returns only after the previous Submit B value completed (timeline query), FramePacing waits near
  zero over 60 frames; NV (gated on the extension, runs on the 4070): `vkGetLatencyTimingsNV` reports carry
  our frame ids; AMD (gated on the feature, runs on the Intel iGPU through the Mesa layer).
- Source coverage for S3 (limiter skipped only when the platform owns the cap, `LatencySleep` before
  `UpdateMousePosition`, patcher and self-check entries, no GL override).
- In game on the 4070, GPU-bound, vsync off, interleaved 60 s runs off / Native / NV: `stats.latency`
  input→present-end median and mean frame time per run, surface type and driver recorded, in
  `docs/vulkan-acceptance.md` (new section L). The user decides the default from those numbers.

### NGX on native Linux: spike result (2026-09-12, `feat/dlss` at bad1122)

Run on the RTX 4070 Laptop, driver 615.71.09, X11, DLSS SDK 310.9.1, no Proton.

- **Both features report available natively.** `SuperSampling.Available = 1` (min driver 470),
  `FrameGeneration.Available = 1` (min driver 520), both with `NeedsUpdatedDriver = 0` and
  `FeatureInitResult` Success. Only the `FrameGeneration.*` names exist in 310.9.1; the old
  `FrameInterpolation.*` aliases return `FAIL_UnsupportedParameter`. This is the first evidence that
  DLSS-G reports itself available on native Linux.
- **Optimal settings work**: at 2560x1490, Quality gives 1707x993, Performance 1280x745, sharpness 0.35,
  dynamic range 1280x745 to 2560x1490 (50-100 % of width).
- **The per-feature extension queries are not implemented on Linux** (`FAIL_NotImplemented`). The SDK's own
  wrapper substitutes fixed lists, which is what we use: instance `VK_KHR_get_physical_device_properties2`;
  device `VK_NVX_binary_import`, `VK_NVX_image_view_handle`, `VK_KHR_buffer_device_address`,
  `VK_KHR_push_descriptor`. All five already reach the device through the S1 requirement seam, verified in
  a GPU test; `VulkanDevice` needed no change beyond exposing the instance handle.
- **Blocker: NGX cannot be called directly from C#.** `libnvidia-ngx.so.1` resolves the calling module from
  its own return address; a .NET P/Invoke stub is JIT-compiled into anonymous memory, so the lookup yields
  a null path and the process aborts inside NGX (`std::logic_error`, `basic_string::_M_construct null not
  valid`). Isolated, not inferred: the same entry points with byte-identical structs succeed from a C
  executable and abort from that same executable through a trampoline in an anonymous mmap page
  (`scripts/dev/ngx-probe.c`). NGX only looks at the immediate caller, so no managed workaround exists.
  **Every NGX call therefore goes through a small native shim** (`libOptimumNgx.so` / `OptimumNgx.dll`,
  a few hundred lines of C forwarding the ~10 entry points plus the parameter vtable). The plan's earlier
  "no native shim needed" note is wrong and is superseded here. Whether Windows' `nvngx.dll` resolves the
  caller the same way is untested; the shim covers both regardless.
- **The shim landed the same day (`native/optimum-ngx`, `make native`, `NgxShim`), and it must never
  tail-call NGX.** `return ngx_entry(args);` compiles to `jmp *%rax` at -O2: the wrapper pops its own
  frame first, so NGX reads the *managed* caller's return address and aborts exactly as it does with no
  shim at all. The first build did this and the managed test still died with
  `basic_string::_M_construct null not valid`. Every forwarded result is now stored in a `volatile`
  local (a language-level guarantee) and `build.sh` also passes `-fno-optimize-sibling-calls`.
  With that, managed code brings NGX up for real on a headless `VulkanDevice`: `Init_ProjectID` Success,
  `SuperSampling.Available=1` (min driver 470), `FrameGeneration.Available=1` (min driver 520), optimal
  settings at 2560x1490 Quality 1707x993 and Performance 1280x745 (sharpness 0.35), clean
  `Shutdown1` and `GpuTest.AssertClean` - the same numbers the C probe got
  (`NgxAvailabilityTests.NgxComesUpFromManagedCodeThroughTheShimAndReportsWhatTheNativeProbeSaw`).
- **Three interop details that cost time**: the driver's exported `NVSDK_NGX_VULKAN_Init_ProjectID` is not
  the header prototype - it takes (ProjectId, EngineType, EngineVersion, AppDataPath, VkInstance,
  VkPhysicalDevice, VkDevice, SDKVersion, FeatureCommonInfo*), with no `vkGet*ProcAddr` arguments;
  `PathListInfo.Path` is `wchar_t**` on Linux too, so search paths are UTF-32; and the driver exports no C
  accessors for `NVSDK_NGX_Parameter`, so parameters go through the C++ vtable whose slot order is the
  declaration order in `nvsdk_ngx_params.h`, with no virtual destructor.
- NGX writes no log on Linux: `__NGX_LOG_LEVEL` and `__NGX_LOG_FILE_LOGGING` produced nothing (the
  documented mechanism is Windows registry keys).

**DLSS SR evaluates on the device (2026-09-12, `feat/dlss` at c1fb719).** Synthetic 1280x745 inputs to a
2560x1490 output at Quality: create, eight accumulating evaluates and release all Success, the pattern
survives (bright blocks 0.9996, dark 0.0000), and the eight-frame run produces zero validation messages.
Landed: `Upscale/Ngx/NgxResourceVk.cs` (our image plus view as an NGX resource), `NgxDlssFeature.cs`
(creation block, per-frame evaluate parameters, release on the frame timeline, rebuild on a size or preset
change) and `VulkanDevice.Dlss.cs` (seam D2: close the scope, place all four barriers through the existing
batcher, evaluate, invalidate the dynamic-state cache). Two more NGX rules, both found the hard way:
**NGX needs the `bufferDeviceAddress` feature**, not just the extension (otherwise every evaluate trips
VUID-vkGetBufferDeviceAddress-bufferDeviceAddress-03324, and NGX's own queries never ask for it); and
~~NGX allows exactly one lifetime per process~~ - **corrected 2026-09-12 (commit 19f9645)**: the crashes
were not a second shutdown. `NVSDK_NGX_VULKAN_Shutdown1` is declared with one parameter and implemented
with two in driver 615.71.09; the undeclared second is an `int*` out-parameter written without a null
check, so calling it through the header prototype from a .NET process (where that register holds 0x2000)
segfaults on the **first** shutdown. The shim now calls it as `(void*, int*)`; A/B on one test binary:
3/3 crashes before, 5/5 clean after. The order retire, drain, shut down, destroy the device is kept and
is now enforced by one process-wide owner (`NgxLifetime`), since `ReleaseFeature` after shutdown is still
untested. NGX's own
`vkCmdClearColorImage` trips a write-after-write hazard against its own barrier on the first evaluate in a
process; both sides are NGX-owned images, so it is pinned as a vendor entry in `KnownSyncHazards`.

### Roadmap item: HDR output

Added 2026-09-12 at the user's request. Today the whole chain is 8-bit: Primary colour 0 is `RGBA8`, the
composite target the upscaler writes is `R8G8B8A8_UNORM`, and DLSS runs in LDR mode (`IsHDR = 0`) because
that content is already perceptually encoded. HDR is therefore not a swapchain-format switch: it is a
pipeline-wide change of what "colour" means, and it touches every decision the temporal work depends on.

What it involves, in the order the dependencies fall:
- **A scene colour with range**: Primary colour 0 and the composite target become a float or 10-bit format,
  and every pass that reads them (bloom's FindBright threshold, god rays, luma, the final composition,
  FXAA) gets a defined input range instead of assuming 0..1.
- **A real tone mapper** at the end of the chain, where the game currently has gamma-space output, plus the
  UI drawn in display-referred space so it does not glow.
- **Swapchain and display**: `VK_EXT_swapchain_colorspace` / HDR10 (`RGB10A2` with PQ) or scRGB, the
  metadata extension, and a policy for when the display actually supports it - on Linux that also means
  the compositor path, which is far less settled than on Windows.
- **The upscaler contract changes with it**: DLSS switches to `IsHDR = 1` (its HDR mode takes unbounded
  luminance and no longer quantises to 8 bits), and the temporal contract's colour-space row (v2 item T2 in
  the vendor research) stops being "LDR, perceptually encoded" - the exposure question (T3) becomes real
  rather than optional, because auto-exposure on an HDR scene is what keeps the upscaler's clip box sane.
- **Frame generation cares too**: DLSS-G does not support FP16/scRGB, so an HDR path that wants FG must be
  RGB10A2/HDR10 (DLSS-FG guide section 11).
Sequencing: after Phase 3's native shaders, because that is where every pass's colour handling is rewritten
anyway, and before or alongside frame generation so the format decision is made once.

### Roadmap item: ray tracing

Added 2026-09-12 at the user's request, explicitly as the last item. The Vulkan foundations are in place -
the renderer is 1.3 with explicit synchronisation, a frame graph, timeline semaphores and a real memory
allocator, which is what a ray-tracing path needs underneath it - but everything above is a rasteriser with
no acceleration structures and a world that rebuilds its geometry constantly.

The real work, in dependency order:
- **Acceleration structures over a voxel world that changes**: a BLAS per chunk mesh and a TLAS over the
  loaded chunks, rebuilt or refitted as chunks stream and blocks change. This is the hard part in a game
  where the player edits the geometry: the budget is per-frame BLAS updates, not a static scene.
- **`VK_KHR_acceleration_structure` / `ray_query` or `ray_tracing_pipeline`**, added as an optional device
  tier with the existing fallback discipline (every tier forceable by env, the raster path always works).
  Ray queries in the existing fragment shaders are the cheaper entry than a full ray-tracing pipeline.
- **What to spend rays on, cheapest first**: ambient occlusion (RTAO - which is also the honest end of the
  GTAO roadmap item), then shadows or contact shadows, then reflections on water and glass, which is where
  this game would visibly gain. Global illumination is a different project.
- **Denoising**: every one of those needs a denoiser, and that is the point where the temporal contract,
  the motion vectors and the upscaler stop being separate concerns - DLSS Ray Reconstruction (the SDK ships
  `libnvidia-ngx-dlssd.so` and we already bind it) is the vendor answer for NVIDIA, with a hand-written
  spatiotemporal denoiser as the cross-vendor fallback.
Sequencing: last, after Phases 3-6 and the HDR item. Nothing else on the roadmap depends on it, and it
depends on almost everything: native shaders, a stable temporal contract, HDR range, and a headless
harness to measure noise objectively.

### Roadmap item: a headless render harness that does not take the machine

Added 2026-09-12 at the user's request: "A headless renderer you can run in the background and take frames
out so my PC is not blocked." Every visual verification so far has meant opening the real client on the
user's desktop, stealing focus and the GPU, which is why in-game checks are rationed and why an agent
cannot judge a temporal artefact at all without the user sitting in front of it.

What it has to be: the real renderer and the real client path (a mock proves nothing - the whole point of
`CLAUDE.md` rule 1 is that a launch on the wrong backend is not a verification), driven without a visible
window, writing frames to disk on demand, and runnable while the user works. The pieces already exist and
are the reason this is a roadmap item rather than a project: the GPU test suite creates real
`VulkanDevice`s and hidden GLFW windows today, `OPTIMUM_PARITY_DUMP` already writes every attachment at a
chosen in-world frame, `scripts/dev/parity-capture.sh` already drives a full client run unattended, and
`OptimumParityDump.WritePresentedFrame` already exists on the diagnostic branch.

Shape to aim for:
- An offscreen mode for the client (hidden window or a surfaceless device where the swapchain is replaced
  by an owned image), selected by an environment variable, with the frame loop otherwise untouched.
- Frame extraction: presented frames to disk at a chosen cadence or frame list, plus the existing
  per-attachment dump, in a format the existing tools already read (`ssim.py`, `taa-rejection.py`,
  `luma-diff.py`).
- A scripted camera and world state so a sequence is reproducible frame for frame: the fixed scene of
  `docs/vulkan-acceptance.md` section 0 plus a recorded camera path, so two runs differ only by the change
  under test. This is what finally makes temporal artefacts measurable without eyes - consecutive-frame
  differences on a *deterministic* sequence, which today's launches cannot provide.
- Low priority on the GPU (or an explicit "run only while idle" switch) so a capture can sit in the
  background while the user plays or works.
Acceptance: an agent can produce a 60-frame deterministic sequence on both backends, with the renderer
line confirmed, without a window appearing on the user's desktop; the shimmer class of bug (jitter,
disocclusion, AO noise) shows up as a number from that sequence.

### Roadmap item: GTAO (XeGTAO) replaces the vanilla SSAO

Added 2026-09-12 at the user's request, after DLSS exposed the ambient occlusion as the last frame-wide
shimmer source (Astra fixed the immediate jitter; the algorithm stays as it is for now).

Vanilla's AO is hemisphere SSAO, 20 samples (24 at `SSAOLEVEL 2`), radius 0.9, with the sample kernel
rotated by a **screen-locked Bayer-128 dither** (`bayer128(texcoord * screenSize)` mapped onto a golden
spiral) and a bilateral blur, computed at half render resolution
(`.vanilla/**/assets/game/shaders/ssao.fsh`). A dither fixed to the pixel grid under a jittered camera
re-rolls each surface point's kernel every frame, which no temporal accumulator can average, and in the
DLSS path the result is composited after the upscale, where the upscaler never sees it.

Target: **XeGTAO** (GameTechDev, MIT, Jimenez et al. 2016) - radiometrically correct horizon-slice
integral, a 5x5 depth-aware spatial denoiser, and controlled temporal noise designed to converge through a
temporal accumulator. Measured by Intel at 0.56 ms (1080p, RTX 2060) and 1.4 ms (4K, RTX 3070); bent
normals cost about 25 % more. It ships as HLSL compute for D3D12 Shader Model 6.3, so the work is a GLSL
port plus a compute path in the renderer - it belongs after Phase 3's native shaders, where compute and
the set convention already exist.

Order of work, because the cheap parts are prerequisites and may settle the symptom on their own:
1. Composite AO inside the scene, before the upscaler evaluate, at render resolution (NVIDIA's placement
   rule; also removes the magnification of a half-render-resolution buffer).
2. Make the dither temporally varying (rotate with the jitter phase) so any accumulator converges it.
3. Only then port XeGTAO, and judge it against the fixed SSAO rather than against today's.

### Phase 6: upscaler and frame-generation seams

Well-known graph handles `SceneNoHud` (Primary 0 after Final, before `AfterFinalComposition`)
and `Composited`; optional `PresentThread` behind `IPresentPath`; `FramesInFlight = 3`;
temporal contract v2 with the vendor surface; one upscaler (FSR 3.1 or XeSS 2) with a recorded
quality/perf table.

---

## Workflow shape (per `.claude/skills/workflow-policy`)

Each phase: `phase('Map')` one sonnet agent at xhigh, read-only, file:line touch points →
`parallel` opus-medium stages with `isolation: 'worktree'` (worktrees start at origin/main: first
`git merge --ff-only feat/vulkan-native`, then `bash scripts/dev/worktree-bootstrap.sh`, which
copies `.build` privately), each ≤ ~8 files / ~600 new lines,
committing `wip(<topic>):` and returning branch + commit → `phase('Integrate')` one opus-medium
agent merging into `feat/vulkan-native` (usual conflicts: `Optimum.Patcher/Program.cs`,
`ClientPlatformWindows.cs`, shader includes, later `shaders.manifest.json` and
`sources/shaders-vk/include/*`), re-running extract/check-patches, build, both suites →
`phase('Review')` one opus-medium adversarial pass with regression tests → Fable verifies in
game at low effort. Never an agent that inherits Fable; opus never above medium. Serial stages
only where dependent (Phase 2 step 2 needs 1A and 1B merged; Phase 3 stage (a) provides the
includes for the rest). Stage prompts: read `CLAUDE.md` and the phase section; sources-of-truth
table; never stash, launch the game, `make deploy`, or `pkill -f` with the process name;
mandatory `Optimum.Tests` coverage + GPU readback tests; structured return via `schema`.

## Verification and evidence rules

- A launch is a verification only with the `[Optimum] Vulkan renderer` / `[Optimum] OpenGL
  renderer:` line in the log; both backends at every phase exit; game closed afterwards.
- Accepted evidence for temporal and pacing claims (goes into `docs/vulkan-acceptance.md` §4 and
  `CLAUDE.md` as rule 10): the `sync,best` validation log; a multi-frame GPU test with Present
  between frames and no readback in the loop; the pacing-gate numbers; per-attachment numeric
  diffs; a 60 fps `ffmpeg -f x11grab` capture with consecutive-frame region diffs for anything
  called flicker; per-pass timestamps for anything called a stall; `OPTIMUM_VULKAN_POISON=1`
  for anything that might read undefined memory. Screenshot pairs are never evidence.
- Every fix: GPU readback test in `Optimum.Render.Vulkan.Tests` (patterns
  `VulkanDeviceIntegrationTests`, `AttachmentSemanticsTests`, multi-frame `TaaResolveTests`) and
  a source-coverage test in `Optimum.Tests` (pattern `fsr-pipeline-coverage-tests.cs`).
- "OFF is vanilla" is a test, not a claim: the lib diff against `_ref/` is limited to the listed
  regions and contains no renderer-specific code.

## Risks (ranked)

1. **Virtualization bypassed by `call`**: silent OpenGL-looking behaviour on Vulkan. Mitigated
   only by the Phase 0 verifier; Phase 1A does not start without it.
2. **Injected virtual missing at runtime** (`MissingMethodException` deep in a frame): the
   `InitializeGraphics` reflection self-check fails the install to the OpenGL fallback instead.
3. **The post-window fallback path is nearly untestable**: `OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE`
   exercises it in the real client once per phase exit.
4. **Mass re-pointing of 14 test files in 1A.2 could paper over a regression**: pure-move commit,
   bodies diffed textually, no behaviour change allowed in that commit.
5. **Write-mask semantics vs undefined attachment contents** (rule 9): keep
   `AttachmentSemanticsTests` and `WorldRenderPathTests` green through Phase 2, read the sync log
   before believing a picture, poison mode available.
6. **Driver tiers on Arc 140V** (color-write-enable, EDS3, descriptor indexing, timeline
   semaphores): every tier forceable by env and tested; the baked-into-pipeline tier always
   works; the Arc row of the vendor matrix is filled before Phase 4 exit.
7. **Streaming graph without foresight**: the plan cache; one conservative frame per settings
   change or resize (which already resets TAA).
8. **Temporal contract drift** across 48 rewritten shaders: single-writer include, differential
   tests, `temporal-contract-tests.cs`, explicit addendum-or-v2 decision in Phase 3.
9. **Manifest vs `collectUniformNames` disagreement**: the parity test diffs the name sets per
   program per variant; a mismatch means the native shader is wrong.
10. **Effort**: P0 ≈ 1 workflow week, 1A+1B ≈ 3-5, 2 ≈ 2-3, 3 ≈ 4-6, 4 ≈ 3-4, 5 ≈ 2-3. The game
    runs at every phase boundary, so the programme can stop at any of them and still ship.

## Documentation to update

`VULKAN-BACKEND-PLAN.md` → v2 (§6 native shaders and sets, §6a adapter only, §9 Harmony and
shader-pack rules, §10-§11 replaced by this plan's phases and tests, §14 file list);
`docs/vulkan-acceptance.md` (new); `docs/parity-allowlist.md` (new); `docs/temporal-frame-
contract.md` addendum or v2 (Phase 3); `CLAUDE.md` (sources-of-truth rows for
`sources/shaders-vk/`, build block gains `make check-shaders-vk`, rule 10 on evidence, the
diagnostics list gains the new env switches); `.claude/skills/{patch-workflow,run-optimum,
vulkan-parity-debug}` (shader manifest steps, `pacing-gate.sh`, `parity-capture.sh`, poison).

## Critical files

- `build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs`,
  `ClientPlatformWindows.cs` (`window_RenderFrame` 729, `Start` ~1086, `SetupOptimumFrameBuffers`
  1629-1916, post chain 3170-3599, `BlitPrimaryToDefault` 4008), `ClientMain.cs`
  (`TriggerRenderStage`, 1402), `Vintagestory.Client/ClientProgram.cs` (214-255, 356-410, 451),
  `Vintagestory.Client/ScreenManager.cs` (29, 121, `Render`), `ShaderProgramBase.cs`, `UBO.cs`
- `Optimum.Patcher/Program.cs`, `MemberInjector.cs`, `SelfConsistencyVerifier.cs`
- `VintagestoryApi/Client/optimum-render-device.cs`, `optimum-render-bootstrap.cs`
- `Optimum.Render.Vulkan/VulkanDevice.cs` (`Present` 695-733, `BlitToSwapchain` 744-799,
  `PrepareDraw` 1747-1843, `AllocateIndirect` 2356-2399, `FlushFrame` 2503-2512),
  `Core/FrameRing.cs`, `Core/Swapchain.cs`, `Core/TextureManager.cs`, `Core/RenderTargetManager.cs`,
  `Core/VulkanAllocator.cs`, `Core/MeshManager.cs`, `Core/PipelineCache.cs`,
  `Core/GlStateTracker.cs`, `Core/VulkanStats.cs`, `Shaders/ProgramInterfaceLayout.cs`
- `Optimum.Launcher/ShaderCompatibilityScanner.cs`
- `Optimum.Render.Vulkan.Tests/{VulkanDeviceIntegrationTests,AttachmentSemanticsTests,
  TaaResolveTests,SwapchainTests}.cs`, `ShaderCorpus.cs`; `Optimum.Tests/
  {cecil-transplant-lambda-tests,temporal-contract-tests,fsr-pipeline-coverage-tests}.cs`

## Not on the critical path (parallel follow-ups, own PRs)

Shader patch system for the GL-path overrides (`patches/shaders/*.patch` against the vanilla
archive, `extract/check-shader-patches.sh`; the known debt in `CLAUDE.md`); splitting
`Optimum.Shaders` out of the renderer and a lavapipe CI job; `vkCmdDrawIndexedIndirectCount`
with GPU culling (the per-slot indirect ring is shaped for it).
