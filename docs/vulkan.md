# Vulkan renderer

Optimum's Vulkan backend delivers native rendering and shaders, TAA and GTAO, with
separate scene/UI images and frame timing. It also supports Optimum's existing FSR 1
render-scale option through the native final blit. New upscaler integrations, frame
generation and Vulkan-to-DX12 interop are outside this PR.

Final acceptance of the refactor is pending. Historical test totals and captures do
not establish correctness of the current build. Select hardware by queried identity
and capabilities: Intel UHD Graphics 770 is Xe-LP integrated graphics, not Arc.

On 2026-09-24, the current Release solution passed 1,554 tests with zero failures:
504 Vulkan tests on Intel UHD 770, 744 core tests (plus 34 existing skips),
and 306 launcher, CLI, bootstrap and installer tests. All 25 consolidated
terrain, object, entity, liquid, particle and sky motion cases also passed on
NVIDIA. Both GPUs loaded Khronos validation with synchronization and
best-practices checks. The Vulkan test project now has 41 C# files, including
34 test suites. The optional scripted headless capture harness is maintained on
the separate `codex/headless-capture` branch. Strict donor
validation applied all 93 source and 68 Cecil patches; all 43 runtime patches
compiled against the pinned donor. All 286 packaged SPIR-V modules remained byte
identical to the pre-refactor baseline. AMD and a real-client scene/performance
comparison remain unverified.

## Architecture

`Optimum.Render.Vulkan/Platform` adapts the game's graphics API and owns game state,
settings and render-system seams. `StatedRenderState` converts general client/mod
calls into native draws. Dedicated terrain, entity, particle, sky, GUI and post-process
paths provide explicit pipeline state and resources to `VulkanDevice`.

- Native draw setup and program placements live in `VulkanDevice.Native.cs`; mesh
  recording and resources share `VulkanDevice.Resources.cs` and `Core/MeshManager.cs`.
- `Core/RenderTargetManager.cs` and `Graph/` own rendering scopes, image usage,
  synchronization, feedback snapshots and transient image lifetimes.
- `Core/FrameRing.cs`, `Frame/` and `Transfer/` own submission, frame-slot storage,
  uploads, readbacks and retirement. Retained draw data must survive GPU completion.
- `Present/` owns swapchain acquisition, the final vertically flipped blit and
  presentation resources. Offscreen images keep the game's GL orientation.
- `Latency/FrameTiming.cs` records CPU phases. One frame identity begins before input;
  partial submits retain that association. Present IDs survive swapchain recreation.
  `stats.latency` reports `frames` and mean/p99 milliseconds for `input`, `sim`,
  `render_submit`, `present` and `total`. Total ends when presentation returns on
  the CPU. This foundation does not change the client's frame limiter.

Game seams must preserve their neutral OpenGL bodies and dispatch virtually. Required
transplanted members belong in the patcher's explicit list. Injected fields must not
depend on initializers transplantation does not execute. Avoid early access to vanilla
static classes. Document each seam's output, corresponding OpenGL entry point, targets
and non-obvious state beside its code.

Compatible draws share rendering scopes. Cached frame plans supply load operations
only while pass prefixes match; stores preserve contents. Transient reuse compares
full image descriptions and inclusive pass lifetimes. Discarding contents still needs
ordering against earlier uses. Sampling a writable color target uses a feedback copy.
Retirement must wait for every relevant frame/transfer submission. Swapchain retirement
also needs presentation completion evidence, not merely a later render submission.
Use maintenance present fences when the device supports them; otherwise defer retirement
until successor-image reacquisition completes. Shutdown without that extension retains
the conventional device-idle fallback, which lacks explicit presentation-fence guarantees.

## Shader contract

Maintain one `sources/shaders-vk/<program>.glsl` per native program. Shared declarations
appear once; `OPTIMUM_VERTEX` and `OPTIMUM_FRAGMENT` select independently compilable
stages. The current inventory is 50 programs, 22 includes and four GTAO files. SPIR-V
outputs remain separate per stage/variant and belong in build output.

The offline compiler, `tools/shader-compiler`, discovers variants, resolves includes,
compiles stages and writes the reflected manifest and binaries. `NativeShaderLibrary`
loads that package. Preserve program identities, sampler order, resource layouts and
runtime uniform semantics when changing source structure. An include change must
invalidate affected artifacts; failed compilation must preserve the last good package.

- GLSL 450 uses scalar block layout. Native vertex stages remap clip depth with
  `gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5`. Do not add another Y flip.
- `include/bindings.glsl` and `Shaders/SetConvention.cs` define descriptor bindings.
  Set 0 holds frame data and fixed frame textures; set 1 holds typed bindless texture
  arrays; set 2 holds chunk face data, animation buffers, program records and named blocks.
- Push constants are limited to 128 bytes. Declare non-frame sampler indices through
  `OPTIMUM_SAMPLER_SLOT(type, name)` so their intended image type survives reflection.
- Frame-owner macros expose the appropriate `FrameGlobals` members. Avoid reusing an
  active frame-member macro as a field, local or parameter name.
- Match vertex inputs and fragment outputs by explicit location and type. Shared
  varyings use the reserved locations in `include/varyings.glsl`.
- Interface-changing defines select binary variants. Settings that preserve interfaces
  use the specialization constants declared in `SetConvention.cs` and its GLSL include.
- Linked uniform/sampler metadata is reused and invalidated on relink. Sampler-unit
  changes must be visible to the next draw; per-draw values are frame-owned snapshots.

### Bindless descriptors

Choose support from queried descriptor-indexing features and limits. Arrays use
partially-bound, update-after-bind descriptors. Slot zero is a placeholder. Resource
kind, integer/float sampling and depth-comparison state must agree with the sampler.
Draw-uniform indices need no nonuniform qualifier; divergent indices do. Descriptors
and their backing images remain alive until submissions using them finish.

### Caches

Shader binary identity includes source/dependencies, options and compiler-library
identity. Driver pipeline caches also require matching GPU, driver and cache UUID.
Corruption is a cache miss. Persist atomically so failed writes preserve valid data.
Serialize driver-cache access; background compilation uses its own cache and publishes
completed results. A missing asynchronous pipeline can skip a draw while it compiles;
correctness/capture runs must use synchronous pipelines where deterministic coverage
is required. Warmup keys include program and settings identity.

## Temporal contract

`IOptimumTemporalContext` exposes the render-thread-owned frame record read-only.
Its matrix arrays are live storage: copy values that must outlive the frame. Advance
once per rendered frame, but capture camera/projection values at their actual producers.
Keep world and hand projections separate. Preserve previous warp counters directly;
they wrap and cannot be reconstructed by subtracting delta time.

Jitter is raster displacement in render-resolution pixels, Y up. Apply one NDC shear:
`P[8] -= 2*jx/renderWidth`, `P[9] -= 2*jy/renderHeight`. Use the Halton(2,3) sequence
from `OptimumTemporalMath`. Only the active perspective temporal window is jittered;
shadow/orthographic targets, caller-pushed projections and late overlays are excluded.

The motion attachment is RGBA16F, cleared to zero:

- `rg`: previous unjittered pixel minus current unjittered pixel, in render pixels.
- `b`: reactive value in [0,1], reducing history weight.
- `a`: window depth actually written by the draw, including its depth offset.

A missing previous position or previous clip W at/below 1e-6 writes zero motion/depth
but preserves reactive coverage. The resolve trusts a writer only when
`a > 0 && abs(a-depth) <= max(2e-4, 8e-4*depth)`; otherwise it uses camera reprojection.
Motion windows use replace blending. OIT reactive composition adds its contribution
while preserving underlying vector/depth channels. Motion lives at Primary slot 2
without the AO G-buffer, slot 4 with it, and is absent when disabled.

History targets 19/20 ping-pong: RGBA16F color, RGBA8 glow and R32F positive linear
view depth. Color/glow use linear filtering, depth nearest. Reject nonfinite history,
reset on invalid/missing resources or discontinuities, and preserve world/hand history
ownership. Resolve uses nearest-depth disocclusion over 3x3 with motion from the same
tap, reactive handling and luminance anti-flicker weighting.
At distant depth edges, retain colour-clipped scene history when a subpixel leaf
leaves the previous 3x3 window, while taking the current glow to avoid bloom ghosts.
Keep the hard depth reset for flat regions and near geometry. The headless parity
gate measures both raw depth mismatches and the remaining hard resets.

AO composition precedes resolve. Bloom and god rays consume the unsharpened scene.
Sharpening writes slot 21 after final composition and late scene overlays; zero
sharpness bypasses it. Preserve the game's existing final-blit choice and avoid double
sharpening. UI is excluded from temporal reconstruction: render it over transparent
black with coverage alpha and its own depth, then compose once with premultiplied alpha
before Done. Screenshots/video capture the completed composition.

## Ambient occlusion

`AmbientOcclusion/GtaoRenderer.cs` owns depth prefiltering, visibility integration and
edge-aware denoising; `GtaoSettings.cs` owns settings. Compute sources live in
`sources/shaders-vk/gtao`. Keep their upstream copyright and license notices intact.
The visibility-bitmask method handles thin occluders; projection reconstruction and
sampling must follow the same coordinate/depth conventions as the scene.

Composite visibility into scene color before TAA, preserving glow and the intended
water/fog/OIT attenuation. Keep vanilla SSAO and AO-disabled behavior available.
When TAA is active, GTAO holds its sample pattern fixed and spatially denoises the
visibility before the scene resolve. The default Medium choice uses the 18-sample
High kernel and two edge-aware passes; explicit Low remains available for slower GPUs.
Cross-quad and wind geometry is thin; snow layers remain solid. Blocks can declare
`optimumAoThin` for other thin chunk geometry. Classification travels with the chunk
metadata; do not infer a whole block's class from unrelated geometry.

## Mod compatibility

Render API meshes, textures, framebuffer operations, state, uniforms and registered
renderers go through the platform adapter. New GLSL 330 programs use the runtime
rewriter. Overriding a native program bypasses its packaged binary; overriding shared
shader includes sends all programs through the rewriter. A failed compatibility scan
also uses the rewriter. Direct GL calls or patches of GL platform internals require the
launcher compatibility fallback because the Vulkan window has no GL context.

The rewriter supports at most 32 sampler slots, no sampler arrays, loose-uniform
initializers, Animation/AnimationPrev blocks and up to four additional named blocks.
`OPTIMUM_VK_NATIVE_SHADERS=0` forces the rewriter for diagnostics.

Register `OptimumPassDecl` with `RegisterOptimumPass` on the main thread. Give it a
mod-unique name, stage, read/write handles and Draw callback. Registration copies the
declaration; re-register to replace it. Passes execute after regular renderers in their
slot, in registration order, and restore the previous target/scope afterward.

- Writes must share Primary, Transparent or the default target. PrimaryDepth may
  accompany Primary/Transparent writes. Default writes are restricted to AfterBlit,
  Ortho and Done; scene writes occur before those stages. Shadow stages are excluded.
- Do not declare feedback reads of written attachments or write read-only handles.
  Missing reads are omitted; missing required write targets skip the pass with a log.
- Declare motion through `MotionWriter`, not a PrimaryMotion write. It is permitted
  on Primary in Opaque/AfterOIT within the temporal window. WithColor adds motion to
  scene writes; MotionOnly is a velocity pass. Registered motion writers can also
  bracket draws with BeginMotionWriter/EndMotionWriter; end only if begin succeeded.
- Unregister on unload or earlier when needed. Leaving the world removes registrations.
  Draw exceptions are logged and do not leave the host scope active.
- On OpenGL these registrations are inert. Effects needing OpenGL support must use
  the regular renderer API there. `OptimumRender.IsVulkan` identifies the active backend.

## Validation and acceptance

Use Release with `DonorAvailable=true`. Run the full solution suite after production
refactoring, replacement tests and the rebase onto StratumServer/main. Preserve
unrelated upstream tests. Replace development-only assertions with a compact behavioral
suite and independent expected values; historical test counts are not acceptance gates.

Correctness runs must identify the actual selected GPU and verify Khronos validation
and requested synchronization checks loaded. Use `OPTIMUM_VULKAN_VALIDATION=1` and
`OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best`. GPU-assisted validation is a separate
run when needed. `OPTIMUM_RENDER_TRACE=<file>` records passes and splits; pass timing
uses `OPTIMUM_VULKAN_PASS_TIMES=1`. Performance runs disable validation.

Required evidence:

- Compile, reflect, package and load every shipped shader variant; test cache failure
  and invalidation. Verify actual source patch application and Cecil transplantation.
- Independent pixels for terrain, entities, particles, sky, GUI and post-processing;
  actual draws must execute. Cover sampler relink, missing attributes and OIT bindings.
- Multi-frame TAA motion/reprojection, disocclusion, reactive handling, resets, sharpen
  placement and UI exclusion. Include moving silhouettes, transparent foreground,
  fences/foliage, hand/world FOV, animated/dropped items, weather, clouds, underwater
  transitions, camera mode/rebase, chunk replacement, shader reload and missing targets.
- GTAO flat/occluded scenes, thin geometry, quality changes and composition order.
- Upload/readback ordering, frame-slot reuse, aliasing, feedback copies, descriptor
  lifetime and teardown. Preserve the Intel stale post-chain and TAA-output regressions.
- Real client startup, UI alpha composition, normal/scaled screenshots, video capture,
  resize/minimize/restore, vsync changes, frame/submit/present identity and clean shutdown.
- Separate verified Intel UHD 770 and NVIDIA runs. Report unavailable AMD verification
  explicitly. Headless results do not establish presentation or pacing correctness.
- Compare equivalent GL/Vulkan scenes/settings without validation; record mean, tails,
  variability, CPU/GPU timing and memory. Claim only measured improvements.

Useful existing tools are `scripts/dev/parity-capture.sh`, `ssim.py`, `perf-capture.sh`,
`pacing-gate.sh`, `luma-diff.py` and `taa-rejection.py`. Confirm the launched renderer
from logs and close clients after capture. Keep revision, device/driver, configuration,
failures and skips with results. Shell helpers may require their supported environment.

### Parity allowlist

`ssim.py --allowlist docs/vulkan.md` reads the following table. It is intentionally
empty. Add an exception only with an explained mechanism and independent evidence,
using the tightest measured `ssim>=x` or `mad<=x` bound. Never waive a sampled attachment
with `any`. Remove exceptions when fixed; do not use this list for GL-vs-GL baselines.

| attachment | reason | max accepted deviation |
|---|---|---|
