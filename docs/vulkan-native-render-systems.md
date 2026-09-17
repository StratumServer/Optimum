# Vulkan-native render systems (Phase 3b)

Design for handoff item 5. Every vanilla render system on the Vulkan path draws through a renderer that owns its
pipelines, its descriptor use on the set convention and its per-draw data, instead of reaching the device through
the GL-shaped platform virtuals. OpenGL keeps `ClientPlatformWindows` unchanged ("OFF is vanilla").

Inputs:
- plan decision 7 and Phase 3b of the Vulkan-native plan;
- `docs/research/vulkan-descriptor-model.md`, "Native architecture for this renderer";
- the shader contract (`docs/vulkan-native-shaders.md`): all 49 vanilla programs are native, and the runtime seam
  links them from the manifest;
- three read-only maps of the tree (2026-09-16): the post and TAA chain, the world render systems, and the
  GL-emulation layer. Their findings are restated below where they decide something.

## 1. What the maps established

- **Post and TAA are not native yet.** Every post and TAA override in `Platform/VulkanClientPlatform.Graph.cs` calls
  `base.<Method>()`. The OpenGL body in `ClientPlatformWindows` runs on Vulkan, and each call reaches the device
  through a GL-shaped virtual:
  - state toggles land in `GlStateTracker`, which the device resolves into a pipeline key on every draw;
  - textures bind by unit;
  - uniforms are set by name through a location;
  - draw-buffer masks select attachments.

  The graph declarations around those calls take the union of every texture a pass might read (both TAA history
  parities, raw, resolved and sharpened scene), because the platform cannot see which one the OpenGL body picked.
- **What already has the target shape:**
  - the frame graph (`FrameGraph`, `PassRecorder`, `BarrierBatcher`, plans, promoted clears);
  - handle-based textures, meshes and targets;
  - the bindless resolve (`BindlessTextureTable.Resolve` with kind checks, depth-read-only layouts and feedback
    copies);
  - the upload shadows (frame block, record, push) with dirty snapshots into the uniform ring;
  - the native programs and their placement tables.

  None of that is OpenGL emulation, and it all stays.
- **What is emulation and leaves the Vulkan path system by system:**
  - `GlStateTracker`'s setters and the per-draw key resolve;
  - the texture-unit tables (`_boundTextures`, `_unitSamplerOverrides`, `SamplerUnits`);
  - uniform dispatch by name and location;
  - `RenderTargetManager`'s draw-buffer-mask and single-bound-target model as the way a pass says what it
    writes;
  - the GL-enum translators;
  - the platform overrides that only forward those calls (`.State.cs`, and the GL-shaped parts of
    `.Shaders.cs`, `.FrameBuffers.cs` and `.Textures.cs`).

## 2. Decisions

1. **The runtime rewriter stays, permanently, as the mod-shader adapter.** Plan decision 2 promises that mods
   rendering through the game API keep working. Those mods set state, bind units and set uniforms by name. The
   GL-shaped adapter therefore stays reachable for mod renderers and for any vanilla system not yet moved, retargeted
   to the shared layout as it already is. Phase 3b's exit is that no *vanilla* render system uses it. The adapter
   shrinks to exactly what mod programs need.
2. **Seams are the existing virtuals, overridden without calling base.**
   - Post and TAA: `RenderPostprocessingEffects`, `RenderOptimumTaaResolve`, `RenderOptimumTaaSharpen`,
     `RenderOptimumSkyMotion`, `MergeTransparentRenderPass`, `RenderFinalComposition` and
     `BlitPrimaryToDefault` are virtual on `ClientPlatformAbstract`.
   - World systems: they get transplanted seams in the library where no virtual exists. For example
     `ChunkRenderer`'s pass methods call a platform virtual whose OpenGL body is today's body.
   - Every new seam follows the decision-5 rules: Cecil listing, `callvirt`, no injected field initializers, and no
     early touch of vanilla static classes.
3. **A native system reads client state, never GL state.**
   - It takes the values the OpenGL body computes: settings, ambient manager, temporal frame, framebuffer list, SSAO
     kernel. Where a value is private to `ClientPlatformWindows`, the seam passes it as an argument or a
     transplanted accessor exposes it; fields are never widened.
   - It computes its uniform values exactly as the OpenGL body does, so both backends produce the same image.
4. **Device API for native systems (`NativePasses`, device side).**
   - **Pipelines.** A native renderer asks for a pipeline by (program, variant defines, fixed state: blend per
     attachment, depth test/write/compare, cull, topology, colour write masks, target formats).
     - The pipeline comes from the manifest program and the pipeline cache, and is created at load or on first
       use, never re-derived from tracked GL state.
     - The key is `GlStateTracker.PipelineKey`'s shape without the tracker.
   - **Passes.** A pass is declared with explicit writes (target handle, colour slots, depth) and explicit reads
     (texture handles, the one texture the system chose this frame).
     - Load/store and transient hints follow the existing plan machinery.
     - Passes still render into the existing framebuffer objects created by `SetupDefaultFrameBuffers`, so target
       ownership and resize handling do not move in this phase.
     - The draw-buffer mask is not consulted for a native pass.
   - **Draws.** A native draw writes the program's push block and record through typed setters by placement: the
     placement table resolved once at pipeline creation, not by name per draw. It resolves sampled textures to
     bindless slots directly from the texture handle and sampler state, then records the draw: fullscreen
     triangle, mesh, multi-draw or instanced.
   - Motion windows are write masks of the pass or pipeline (the colour-write tiers), not `SetDrawBuffers` calls.
5. **Order and parallelism.**
   1. The device API plus the post and TAA chain, one stage: Optimum owns the chain end to end, and the chain proves
      the API.
   2. Then, in parallel on the stable API: chunks (all passes including shadow and liquid motion); entities (animated,
      shadow, held items through `standard`, `instanced`); particles, decals, sky, night sky, celestial, aurora and
      clouds; GUI and text (`gui`, `guigear`, `guitopsoil`, `helditem`, `lines`, `texture2texture`, block
      highlights, wireframe, autocamera).
   3. Last, removal: the emulation with no vanilla caller left leaves the Vulkan path, and the adapter that mods need
      stays, under tests that pin the mod path.
6. **Behavioural identity is the acceptance rule.**
   - Each moved system gets GPU readback tests that render the same inputs through the old route (the OpenGL body on
     the Vulkan device) and the native route, and compare pixels. They are bitwise where both paths are, within 1/255
     where filtering allows.
   - The TAA resolve keeps its rule-11 invariants and their tests.
   - The motion attachment stays identical, per the temporal contract.
   - Validation stays clean with `sync,best`.
   - Temporal claims need multi-frame tests, and the in-game check runs on both backends with the headless harness.
7. **FSR input (open item from the post and TAA map).** `BlitPrimaryToDefault` upsamples `Primary` colour 0. The
   native chain keeps exactly that input and does not reinterpret "the scene" per stage. Any change is a separate,
   measured decision.

## 3. Stage 1 scope: device API plus post and TAA chain

- **Device:**
  - native pipeline creation from manifest programs with explicit fixed state;
  - native pass declaration with explicit reads and writes;
  - typed push and record writes by placement;
  - direct bindless resolution by texture handle;
  - native draw recording;
  - motion write masks per pass;
  - stats: native passes, native draws, pipelines.
- **Platform:** a `NativePostChain` on the Vulkan platform implementing the seven post and TAA virtuals plus
  `ApplyOptimumSceneSsao`'s composite, with the frame order and the conditions of the OpenGL body.
  1. OIT merge.
  2. Sky motion.
  3. SSAO and blur, then the AO composite into the scene.
  4. TAA resolve and sharpen.
  5. The bloom chain.
  6. God rays.
  7. FXAA luma or blit.
  8. Final composition.
  9. Debug view, FSR (EASU and RCAS) or blit.

  Each pass reads the one physical texture the chain chose. The motion-writer hosting for mod passes and the AO
  compute pass keep working at their slots.
- **Tests:**
  - old-route versus native-route pixel comparison for every pass under the settings sweep (SSAO 0/1/2, bloom, god
    rays, FXAA, TAA on/off, render scale below 1 with FSR, debug view);
  - a multi-frame TAA accumulation test through the native chain;
  - declared reads that no longer union candidates;
  - validation clean;
  - the full suites.
- **Not in stage 1:** world systems, GUI, and removal of the emulation layer.

## 3b. Stage 2 scope: mesh draws on the device API, proved on the sky

Stage 1's device API records fullscreen draws only. World systems are mesh draws, so stage 2
extends the API and ports the simplest system through it.

- **Device** (`VulkanDevice.NativeMesh.cs`, plus the description and key in `VulkanDevice.Native.cs`):
  - `DrawNativeMesh`, `DrawNativeMeshInstanced`, `DrawNativeMeshArrays` (non-indexed) and
    `DrawNativeMeshMulti`, all sharing `BeginNativeDraw` - the old `DrawNativeFullscreen` body - and
    all recording through the existing `MeshManager` and the existing per-slot indirect ring. There is
    no second mesh path.
  - The real mesh id reaches `BindProgramSets`, so a chunk's storage-buffer vertex fetch and an
    entity's `Animation` block resolve per draw instead of against the fullscreen path's hardcoded 0.
    Bone matrices therefore need no new API: `UBO.Update("Animation", ...)` keeps working as it does.
  - `NativePipelineDescription` gains what a mesh draw needs and a fullscreen draw did not: the vertex
    layout (`VertexLayoutId`, from `VulkanDevice.NativeMeshLayoutId`), polygon mode, line width, front
    face, and `SamplesBoundDepth` - the explicit declaration that the pass reads the depth attachment
    it draws into with writes off, which is the one case where sampling its own target is legal and
    which puts the scope's depth in the read-only layout. Every one of those is in the native pipeline
    cache key, and the vertex layout is in `PipelineKey`, so a mesh pipeline never collides with the
    fullscreen pipeline of the same program.
  - Per-draw writes stay `WriteNative` by placement, with a float-run overload for a matrix. A record
    member is snapshotted into the frame's uniform ring when the draw binds set 2, a push member is
    pushed with the draw's push block: both are per draw.
  - Stats count the kinds apart: `native_fullscreen_draws`, `native_mesh_draws`,
    `native_instanced_draws`, `native_indirect_draws`, summing to `native_draws`.
- **Platform:** the sky dome (`VulkanClientPlatform.NativeSky.cs`). Its lib seam is
  `ClientPlatformAbstract.RenderSkyDome(MeshRef, int skyTextureId, int glowTextureId, float[] modelViewMatrix)`,
  whose neutral body is the `RenderMesh` call it replaced - so OpenGL is unchanged - and which hands
  the native pass the values it cannot read off GL state. `NativeSkyEnabled` keeps the old route
  reachable.
- **Tests:** `NativeSkyTests` (old route against native route, scene and glow pixels, no emulation
  inside the pass), `NativeMeshDrawTests` (a native mesh draw against the emulated draw of the same
  mesh, pipeline-key uniqueness across the new dimensions, two multi-draws taking two regions of the
  indirect ring, the layout-mismatch refusal) and `Optimum.Tests/native-world-systems-coverage-tests.cs`
  for the lib seam. The four system stages add their systems to those files rather than to files named
  after the stage.

### Chunks: the terrain on the mesh-draw API

The first of the four system stages, and the heaviest draw path in the game.

- **Seam:** `ClientPlatformAbstract.BeginChunkPass(string chunkPass, bool blend, bool depthTest,
  bool depthWrite, bool cullFace)` / `EndChunkPass()`, with neutral bodies that do nothing at all -
  so the OpenGL path keeps drawing today's bodies under today's `GlToggleBlend` / depth / cull
  calls. Every `ChunkRenderer` draw group brackets its pool loop with the pair in a `try` /
  `finally`: the two shadow cascades' four groups, the Opaque stage's five, the OIT liquid and
  transparent groups, the liquid velocity redraw and the AfterOIT terrain overlay - thirteen in all,
  each naming itself and stating the fixed state it runs under.
- **Platform** (`VulkanClientPlatform.NativeChunks.cs`): the scope opens a declared pass on the
  bound target at its first draw, and the pool's multi-draw reaches
  `VulkanDevice.DrawNativeMeshMulti` from the existing `RenderMesh(MeshRef, int[], int[], int, bool)`
  seam. It stays multi-draw indirect over per-chunk meshes with the FaceData storage buffer, because
  the real mesh id reaches `BindProgramSets`. The greedy-mesh and NoSSBO variants are just other
  vertex layouts and other program variants, and the pipeline is keyed on both.
- **The motion window is a colour-write mask.** With a window open the motion attachment joins the
  pass's colour slots with replace blending; the liquid velocity redraw's motion-only window is the
  same slots with the write mask zeroed on every other one. No `SetDrawBuffers` appears in the native
  chunk path.
- **State the platform states rather than reads:** the standard blend mode, replace blending on the
  SSAO G-buffer slots and the motion slot (what `GlToggleBlend` and `ApplyOptimumMotionBlendState`
  apply), and - for the Transparent target - whichever contract the client last applied, recorded at
  `ApplyTransparentPassBlendState` and `BeginOitAccumulation` rather than read back out of
  `GlStateTracker`. The texture behind each sampler is recorded at `BindProgramTexture2D`, the seam
  where the client states it, so the draw resolves handles rather than units. `chunkliquid` samples
  the depth attachment it draws against with writes off, which the pipeline declares through
  `SamplesBoundDepth`.
- **Still emulated inside a chunk group:** the uniform values the group sets by name (the per-pool
  `origin` from `MeshDataPoolManager` and the generated program setters). They land in the same
  per-program record and push shadows the native draw snapshots, so the image is identical; removing
  that dispatch means moving `MeshDataPoolManager`, which is API-fork code and a later stage.
- **Tests:** `NativeChunkTests` (old route against native route per group: blend and cull
  combinations, the motion attachment bit for bit, the motion-only mask, the shadow cascade, one
  declared pass and one indirect draw per group, pipelines built once) and the chunk facts in
  `Optimum.Tests/native-world-systems-coverage-tests.cs`.

### Entities (stage 2, one of the four system stages)

- **Seam:** `ClientPlatformAbstract.RenderEntityMesh(MeshRef, string samplerName, int textureId)`,
  whose neutral body is the `RenderMesh(MeshRef)` call it replaced, drawn through by
  `RenderAPIBase.RenderMultiTextureMesh` - the one place every entity sub-mesh already went. The
  seam is generic, so the GUI and block-entity stages widen its native side rather than adding
  another seam.
- **Native:** `VulkanClientPlatform.NativeEntities.cs` takes the native route for the two programs
  SystemRenderEntities itself drives, `entityanimated` (Opaque, into Primary) and
  `shadowmapentityanimated` (the two shadow stages, into colourless targets), and hands every other
  caller of the seam to the neutral body. `NativeEntitiesEnabled` keeps the old route reachable.
  - The motion window is a **colour write mask on the pipeline**, from the platform's own
    `OptimumMotionWriteActive` and `MotionAttachmentIndex`: replace-blend on the motion slot while
    it is open, no write at all while it is shut. No `SetDrawBuffers` anywhere on this path.
  - The draw is recorded **inside the stage's own declared pass** - it names `BoundPassName()` with
    every slot, so `RenderTargetManager.DeclarePass` coalesces - and closed with
    `EndNativePass(keepScope: true)`. A pass of its own per entity would end and restart the
    rendering scope hundreds of times a frame. The native-pass bookkeeping still clears, so the
    uniforms the renderers set by name between draws stay outside a native pass and the
    "no emulation inside a native pass" invariant holds exactly as before.
  - Bone matrices need no new API and are not copied into the draw: `UBO.Update("Animation", ...)`
    keeps feeding the device's ring with its per-(frame, version) snapshot dedup, and the native
    draw passes its real mesh id into `BindProgramSets` so that snapshot resolves for it.
  - A native draw resolves **every** sampler its program declares, from what the client declared
    for it by name (`BindProgramTexture2D` records it), because the emulated resolve that fills the
    push block's sampler slots from the texture units never runs for a program whose draws are all
    native. That table is a name-keyed record of the client's own declaration, not the unit table.
- **Not taken native here, and why:** held items through `standard` - their cull mode is decided per
  draw by `renderInfo.CullFaces` inside `EntityShapeRenderer.RenderItem` in the VSEssentials fork and
  no seam carries it, so a native pipeline would have to read the tracker back, which decision 3
  forbids; it needs a seam in the fork. And the `instanced` program, which has no vanilla call site
  in this tree.
- **Tests:** `NativeEntityDrawTests` (old route against native route across the SSAO G-buffer and
  motion-window sweep, the motion attachment on its own, the emulation boundary, pipeline identity)
  and the entity section of `Optimum.Tests/native-world-systems-coverage-tests.cs`.

## 3c. Stage 2, second wave: the remaining sky systems, the particles and the decals

The sky dome proved the mesh-draw API; this wave takes the systems whose shape it did not exercise
- a cube map, an instanced pool, an indirect multi-draw - and the first two systems that write the
motion attachment.

- **Seams** (`ClientPlatformAbstract`, each with the neutral body of the draw it replaced, each
  listed in `Optimum.Patcher/Program.cs` and in `VulkanClientPlatform.ExpectedVirtuals`):
  - `RenderNightSkyBox(MeshRef, int cubeTextureId)` - `SystemRenderNightSky`'s star cube;
  - `RenderCelestialQuad(MeshRef, int bodyTextureId, int skyTextureId, int glowTextureId)` - the
    moon, under `celestialobject`;
  - `RenderParticles(MeshRef, int quantity, int particleTextureId)` - one particle pool's
    instanced draw;
  - `BeginDecalPass(int decalTextureId, int blockTextureId)` / `EndDecalPass()` - the decal pool's
    scope, both neutral bodies empty. This one is a **scope** seam rather than a draw seam:
    `MeshDataPool.modelRef` is `internal` in the vanilla API, and a new public member on a vanilla
    API type never ships (the shipped `VintagestoryAPI-patched.dll` is vanilla plus the hooks in
    `Optimum.Patcher/api-patcher.cs`), so a `MeshRef` parameter is not available to the lib at all -
    an earlier `RenderDecalPool(MeshRef, ...)` seam fed by a fork-only `MeshDataPool.ModelRef` threw
    `MissingMethodException` in the shipped client on both backends. `SystemRenderDecals` therefore
    opens the scope, runs the **vanilla** `decalPool.Draw(game.api, game.frustumCuller,
    EnumFrustumCullMode.CullInstant)` - which culls and issues the pool's own
    `RenderMesh(MeshRef, int[], int[], int)` multi-draw - and closes the scope in a `finally`. The
    Vulkan platform's `RenderMesh(MeshRef, int[], int[], int, bool)` override routes that multi-draw
    to `TryDrawDecalPoolNative` while the scope is open, the same shape `BeginChunkPass`/
    `EndChunkPass` uses for the chunk pools.
- **Platform:** `VulkanClientPlatform.NativeWorld.cs`, one `NativeWorldEnabled` switch keeping
  every neutral body reachable. Two derivations are shared by all four passes and are the reason
  none of them reads `GlStateTracker`:
  - `NativeWorldPassColorSlots` - the colour slots of the pass are the set the emulated route's
    draw-buffer mask would hold, computed from `MotionAttachmentIndex` and
    `OptimumMotionWriteActive`: every bound slot with TAA off, Primary's default colour set with
    TAA on, plus the motion attachment exactly while a motion window is open.
  - `NativeWorldBlend` - the caller's blend mode per attachment, with replace-blending
    (ONE, ZERO, ADD) forced on the motion attachment inside a window, which is what
    `ApplyOptimumMotionBlendState` does for an emulated draw.
- **Tests:** `Optimum.Render.Vulkan.Tests/NativeWorldSystemsTests.cs` (old route against native
  route on every attachment of Primary for all four systems, the motion attachment compared bit
  for bit for the two systems that write it, the draw kinds counted apart, and the slot
  derivation checked against all three window states) and the seam coverage in
  `Optimum.Tests/native-world-systems-coverage-tests.cs`.
- **Deliberately not in this wave, with the reason:**
  - **the sun.** `SystemRenderSunMoon` draws it under `standard`, the shared program the entity
    stage owns (held items, dropped items); porting it means porting `standard`'s whole sampler
    set, so it belongs to that stage and keeps `RenderMesh`.
  - **the quad particle pool.** It draws into `Transparent` in the OIT stage, whose
    per-attachment weighted-blend state belongs to the OIT pass rather than to the particle
    system and is not something this seam can state. The pipeline request names `particlescube`,
    so the quad pool falls through to the neutral body by construction rather than by a check.
  - **aurora and the two cloud renderers.** They live in the `VSEssentials` fork and reach the
    device through a second emulation surface, `OptimumForkGraphics` / `VulkanForkGraphics`
    (`VintagestoryApi/Client/optimum-render-device.cs`,
    `Optimum.Render.Vulkan/Platform/VulkanForkGraphics.cs`), which has no native counterpart and
    is not in section 1's emulation inventory. `CloudRendererMap` also renders into a target the
    mod creates itself rather than one of `SetupDefaultFrameBuffers`'. Deciding whether that
    surface gets a native equivalent or stays permanently emulated mod-adapter surface is its
    own call, and it blocks those three systems until it is made.

## 3d. Stage 2 scope: GUI and text

The GUI and text group of decision 5 step 2 (`gui`, `guigear`, `guitopsoil`, `helditem`, `lines`,
`texture2texture`, block highlights, wireframe, autocamera) splits in two on decision 3, and only one
half can move yet.

**Moved, because the caller states the fixed state:**

- **The texture-into-texture blit** (`texture2texture`), `ClientMain.RenderTextureIntoFrameBuffer` -
  how every Cairo-drawn GUI and text surface is baked into a texture, and the highest-frequency GUI
  draw there is. That method computes both pieces of state itself (`GlDisableDepthTest`, and
  `GlToggleBlend(alphaTest >= 0f)`), so the seam
  `ClientPlatformAbstract.RenderTextureQuad(MeshRef, int textureId, bool blend)` carries them and the
  native pipeline states them rather than reading them back. Descriptor churn is answered by
  construction: a native draw resolves its texture into the frame's bindless arena, so a fresh Cairo
  texture costs one slot in that frame and no permanent descriptor.
- **The aiming reticle's lines**, `SystemRenderPlayerAimAcc` on the `gui` program with `noTexture` set.
  It sets `GLLineWidth` and `GlToggleBlend(on: true)` immediately before each draw, so the seam
  `ClientPlatformAbstract.RenderOverlayLines(MeshRef, int textureId, float lineWidth, bool blend)`
  carries both. These are the first native draws with line topology (taken from the mesh's own draw
  mode through `VulkanDevice.NativeMeshTopology`) and with a caller-chosen line width, which is in the
  native pipeline key - the 0.5 and the 1.0 draws of one frame are two pipelines.

Two device-level corrections came out of it, both places where the two routes could have disagreed:

- `vkCmdSetLineWidth` refuses a width outside `VkPhysicalDeviceLimits::lineWidthRange`, where
  `glLineWidth` silently clamps, and the game asks for 0.5. Both routes now clamp through
  `VulkanCapabilities.ClampLineWidth`.
- A `NativeMeshPass`'s one-entry pipeline cache keyed only on program, target formats and vertex
  layout, so it answered any request with the pipeline it built first. A system that changes blend or
  line width between draws - which is exactly what the reticle does - would have drawn both with the
  first one's state. It now compares the fixed state and falls through to the device's own table.

**Not moved, and why.** `Render2DTexture`'s `gui` quads, `guigear`, the block highlights, the wireframe
cube and the camera path (`autocamera`) all draw with whatever blend and depth state the frame left on
the tracker; none of them sets it. The same `ClientMain.Render2DTexture` body is reached with standard
alpha and with premultiplied alpha, because `RenderAPIGame.Render2DTexturePremultipliedAlpha` brackets
it with `GlToggleBlend`. Decision 3 forbids a native pass from reading that back off tracked GL state,
so these move when their blend mode is stated at the seam - a change that reaches through the GUI
element tree in the API fork, and its own piece of work. `guitopsoil`, `helditem` and `lines` have no
vanilla call site in this tree at all and should be confirmed with `OPTIMUM_RENDER_TRACE` before anyone
ports them.

Tests: `NativeGuiTests` (old route against native route for both systems, blending on and off, both
line widths, the pipeline identity across line widths, and twenty fresh textures through one pipeline)
and the GUI section of `Optimum.Tests/native-world-systems-coverage-tests.cs` for the lib seams.

## 3e. Stage 3: the emulation layer is gone

Every draw is native. The dedicated routes (chunks, entities, sky, clouds, particles, decals, GUI,
the post and TAA chain) run first; everything else - mod renderers, the vanilla programs without a
route, the seams' neutral bodies behind the route switches - takes the generic stated draw.

- **State:** the platform records what the client states, with OpenGL's semantics, in
  `Platform/StatedRenderState.cs`: blend (a disabled blend keeps its functions, `glBlendFunc` sets
  every draw buffer), the global colour mask, draw buffers per framebuffer as write masks, one
  texture and one sampler object per unit, depth, cull, scissor, viewport, line width, and the
  program `glUseProgram` named. Nothing reaches the device as state.
- **Draw:** `Platform/StatedDraw.cs` builds a native pipeline from that record and opens a pass on
  the target the client addressed (a fork's raw bind, else `CurrentFrameBuffer`, else the default),
  with every colour slot attached on the device. A slot the draw samples while its draw buffer is
  off leaves the pass; with its draw buffer on the device samples a ReadSelf copy. The scope is
  kept, so consecutive stated draws on one target coalesce into one pass. A mod pass hands its
  declaration to the draws inside it.
- **Clears:** a colour clear honours the stated draw buffers and colour mask, a depth clear the
  depth mask, and both name their target.
- **Device:** `VulkanDevice` has no GL state machine, no bound target, no texture-unit tables and
  no draw that is not a native one. `RenderTargetManager` has no draw-buffer mask and no
  sampled-slot exclusion; a declared pass's slots are the only exclusion, applied with the frame
  graph on or off. The shared pipeline types live in `Core/PipelineState.cs`.
- **Frame textures:** a program's set-0 texture that a draw does not name keeps the value the last
  draw left (GL's "whatever the unit holds"); the draw puts it back into the read layout, or uses
  the placeholder when it is gone or an attachment of the draw's own target.
- **Tests:** the GPU tests keep their GL-shaped calls through `Optimum.Render.Vulkan.Tests/GlShapedDevice.cs`,
  which records into a `StatedRenderState` and draws through `StatedDraw` - on a platform's device,
  through the platform's own record and target. The component tests that drive the pipeline cache
  directly build their keys with `PipelineKeyState.cs`. The differential tests compare each
  dedicated route with the stated route; `NativeStatedTests` compares the stated route with a
  hand-written native draw.

## 4. Documentation that makes a map of the tree unnecessary

Every piece of work on this backend has started by rediscovering where things are, and the result was
thrown away when the work ended. The fix is not a map document - that is a second source of truth and
it rots. The fix is that the code answers the question at the declaration, so an implementer greps and
reads instead of mapping.

**The convention.** Every render seam - a platform virtual a system draws through, a native pass, a
device API entry point - carries a doc comment that answers, in this order:

1. **What it draws**, in one sentence, in the game's vocabulary ("the far shadow map for chunk meshes",
   not "a draw call").
2. **Where the other side is**: the OpenGL body's type and method, so the two paths can be diffed
   without searching. For a native pass, also the pass it replaced.
3. **Target and slots**: which framebuffer and which colour slots it writes, whether it writes depth,
   and any colour-write mask that matters (motion windows are masks, never draw-buffer toggles).
4. **State that is not obvious**: blend mode per attachment, depth compare, cull, and anything the pass
   deliberately does differently from the tracked GL state, with the reason.
5. **What pins it**: the test that fails if this changes - the differential test name for a native pass,
   the coverage test for a lib seam.

**Greppability is the point.** A system's name appears in the comment of every member that serves it, so
`rg -n "shadow map"` finds the seam, the native pass, the pipeline and the test in one search. When a
system moves to a native pass, its old body keeps its comment and gains the pointer to the new one.

**Applies to:** `Optimum.Render.Vulkan/Platform/VulkanClientPlatform.*.cs`, `VulkanDevice.Native.cs` and
the transplanted seams in `build/VintagestoryLib/**`. A change documents the seams it touches as part of
the change, not afterwards; a change that adds a seam without this comment is incomplete, and review
should send it back.

A survey of the tree is then only for questions the code genuinely cannot answer - measured behaviour,
vendor documentation, or a tree the repository does not contain.

