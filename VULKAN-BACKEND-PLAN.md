# Vulkan renderer backend: implementation plan

Vintage Story renders through OpenGL 3.3 (4.3 with SSBOs) via OpenTK. Every
upscaler and frame-generation SDK worth shipping (XeSS-SR/FG, DLSS-SR/FG, FSR 2/3)
speaks D3D12 or Vulkan, and frame generation additionally has to own presentation.
This plan adds a Vulkan renderer to Optimum as a second, runtime-selectable
graphics backend behind the existing `ClientPlatformAbstract` seam. OpenGL stays
exactly as it is and remains the default until the Vulkan path reaches parity;
every render system, every shader, and every mod that talks to `IRenderAPI` /
`IShaderAPI` keeps working unchanged, because the backend emulates the GL state
machine the game and its mods were written against. The design is the same shape
as Zink and ANGLE's Vulkan backend, specialised to the 102 GL entry points this
one game actually uses.

Line references below point at the vanilla 1.22.7 decompile in `_ref/` (raw
`ilspycmd` output). The bootstrapped, patched tree in `build/` has different line
numbers.

## 0. Status

**The client runs on Vulkan and its interface renders correctly.** It reaches the
main menu, holds ~165 FPS / 6.1 ms, logs zero errors and zero validation messages,
and translates and links all 45 shader programs the menu needs at runtime. The
login screen is pixel-comparable with the OpenGL path. 872 tests passing (137
renderer, 714 Optimum, 21 launcher). Cecil: 230/230 required methods patched, 189
members injected. 121 patches, 0 conflicts.

Phase 1 was previously called complete at 187 methods; running the client showed
that was premature. Roughly thirty more entry points were still on raw GL and only
reachable at runtime - the two `CurrentFrameBuffer` property setters that bind on
assignment, framebuffer lifecycle and per-pass state, the texture loaders, uniform
buffers, error checking, `ShaderProgramBase.Use/Stop/Dispose`, the post-process
chain, `ScreenManager`'s depth clear, and `GameWindowNative`'s constructor. A
static sweep for `GL.` call sites missed property accessors and anything a static
reading cannot prove is reached. **The lesson is in section 12: parity claims come
from running the client, not from auditing call sites.**

Three bugs found only by running, each invisible to the validation layer:

1. **The default render target did not follow the window.** `Install` received
   `ClientSettings.ScreenWidth/Height` - the *windowed* size - while the window
   had already opened fullscreen, and nothing called `Resize`. The target stayed
   1280x850 while the viewport and swapchain were 2561x1601, so every draw was
   clipped to the top-left corner. The background survived only because it is a
   tiling texture that the present blit stretched over the screen, which is what
   made this look like a GUI problem rather than a sizing one.
2. **Vertex attributes the mesh does not supply.** GL answers a read of an unbound
   attribute with the constant generic attribute, defaulting to (0, 0, 0, 1);
   Vulkan has no equivalent. The GUI quad carries positions and UVs while
   `gui.vsh` declares six inputs, and `gui.fsh` discards a fragment based on one
   of the missing ones. See section 6a.
3. **shaderc enforces its `#version` floor during preprocessing**, before the
   rewriter can raise the version to 450. Targeting OpenGL instead is worse - that
   floor is 330. `ShaderCompiler.RaiseVersionForPreprocessing` lifts sources below
   140, which in vanilla is only the hardcoded `#version 130` minimal-GUI program.

**Phase 2, world rendering: the GL leak sites are closed.** Every render system
that reached past `ClientPlatformWindows` to GL directly now routes through the
device — `ChunkRenderer` (atlas LOD bias, sampler unbinding), `SystemRenderOITLayers`
(the layered accumulation target, six-attachment blending, its own textures),
`SystemRenderSunMoon` (occlusion queries, colour mask), `SystemRenderFrameBufferDebug`
(shadow-map compare mode), `SvgLoader`, `ShaderRegistry` (terrain sampler bias),
`ClientMain`, `InventoryItemRenderer`, `ClientSystemStartup` and `Screenshot`.
242/242 Cecil methods, 192 members injected, 126 patches, 0 conflicts.

Two more defects came out of it, both silent:

4. **Sampler uniforms had no locations.** `LocationOf` searched only the generated
   uniform block, and samplers are descriptor bindings, so every texture uniform
   in the game resolved to -1 — which the client reads as "the shader does not use
   this". Sampler names now get locations from a disjoint negative range, and an
   int written to one assigns its texture unit rather than landing in the block.
5. **Attachment layer indices were dropped.** A colour attachment used
   `texture.View`, the whole-image view, so the OIT accumulation array — one
   texture attached three times, once per layer — sent all three attachments to
   layer 0. Attachments now take a per-layer view. Caught by a test, not by the
   validation layer, which had nothing to complain about.

Reaching a world in the real client needs a signed-in account - the session key is
RSA-signed by the vendor and `--rndWorld` does not bypass the check - so the world
paths are covered by GPU tests instead, against the real game shaders:

- `WorldRenderPathTests`: the layered OIT accumulation target, per-attachment
  blend factors, a depth-only shadow target, and an occlusion query. The layer
  bug above is exactly what this suite was written to catch.
- `ChunkRenderPathTests`: the real `chunkopaque` program across all four define
  variants, with the mesh built to match each variant (SSBO on means positions
  leave the vertex input, so the mesh has to follow); every world-facing program -
  `chunkopaque`, `chunkliquid`, `chunktransparent`, `chunktopsoil`,
  `chunkshadowmap`, `entityanimated`, `particlesquad`, `particlescube`,
  `standard` - built into a real `VkPipeline` against a real mesh layout; the SSBO
  chunk path; and an instanced draw verified by reading back both instances.
- `ChunkTerrainRenderTests`: **terrain actually drawn.** A tesselated block face
  in the real vertex format - positions, UVs, per-vertex colour and the packed
  render-flags word, each in its own buffer - goes through `chunkopaque` and
  `chunkshadowmap` via `IOptimumGraphicsDevice` and nothing else, and the pixels
  come back. The target is cleared to magenta rather than black, because the chunk
  shader legitimately shades to black with no lighting bound, and "the pixel is
  lit" would be indistinguishable from "nothing drew"; asserting the pixel
  *changed* detects rasterisation whatever the shader emits.

  Worth recording, because it took a while to see: the first version of that test
  drew nothing at all, with a clean validation log and a correctly issued
  six-index draw. The cause was the test, not the device - chunkopaque computes
  `aTest = outColor.a + ... - lod0Fade` and discards below `alphaTest`, and
  `lod0Fade` comes from the view distances. Left at zero, every fragment in the
  world fades out. The client sets those uniforms every frame. That the discard
  fires correctly on translated SPIR-V is itself evidence the translation
  preserves the shader's semantics.

**Vendor matrix, both rows green.** All 173 renderer tests - terrain rendering
included - pass on NVIDIA (proprietary driver) and on Intel UHD (Mesa), selected
with `VK_ICD_FILENAMES`. The client itself also runs on both and renders
identically: same interface, same layout, same 165 FPS, zero errors and zero
validation messages on each. That covers the Arc row's driver family ahead of the
target handheld.

**`auto` backend selection is implemented** (section 4's allow-list). `auto` and
`vulkan` are no longer the same decision: an explicit `vulkan` is a choice the
player made and is honoured wherever the backend runs at all, while `auto` is a
default nobody chose and takes Vulkan only on driver families the backend is
exercised against - NVIDIA, Mesa (Intel and radv), the Windows Intel driver and
AMD's proprietary one. Anything else stays on OpenGL with a reason that names the
driver and says how to override it. The list keys on the driver rather than the
GPU model, because the behaviour that breaks a backend lives in the driver.

What is still untested rather than unimplemented: actual terrain, entities and
particles drawn from a loaded world, and the SSIM comparison against GL
screenshots. Both need a signed-in session on the test data path - the session key
is RSA-signed by the vendor in `SessionManager.IsCachedSessionKeyValid`, the gate
sits in `ScreenManager` init ahead of every screen, and neither `--rndWorld` nor
`-c` reaches `HandleArgs` without passing it. The offline path
(`DoGameInitStage3`) still requires a previously cached valid session, so it does
not help a machine that has never signed in.

The packaging scripts now ship `Optimum.Render.Vulkan.dll`, the Silk.NET
assemblies and native shaderc on all three platforms; the renderer project sets
`CopyLocalLockFileAssemblies` so its dependencies reach the output directory at
all, since nothing in the tree references it.

The riskiest assumption in this plan was that the 84 shaders could be translated
automatically rather than hand-ported, because hand-porting does not extend to mod
shaders, which are GLSL authored by third parties and only exist at runtime.
`Optimum.Render.Vulkan` now translates **all 84 shaders across 4 define
permutations - 336 program/variant combinations - to valid SPIR-V**.

That SPIR-V has been proven against real drivers, not just the validator: a
GLSL 330 pair goes through translation, becomes a `VkPipeline`, renders offscreen
through dynamic rendering, and reads back correct pixels with **zero validation
messages**. The same test pins the coordinate convention empirically - framebuffer
row 0 carries `texCoord.y` near 0 and the last row near 1, which is GL's
orientation - so a future change that introduces a Y flip fails a test rather than
inverting every render-to-texture pass silently.

Both physical devices on the development machine are accepted by the backend's
feature requirements:

| Device | Driver | API | Verdict |
| --- | --- | --- | --- |
| Intel UHD (ADL-S GT1) | Mesa 26.2.2 | 1.4.354 | usable |
| NVIDIA RTX 4070 Laptop | NVIDIA 610.57 | 1.4.341 | usable |

Shipped so far:

| Piece | File |
| --- | --- |
| Backend seam | `sources/VintagestoryApi/Client/optimum-render-device.cs` |
| GLSL type model, scalar layout rules | `Optimum.Render.Vulkan/Shaders/GlslType.cs` |
| Declaration parser | `Optimum.Render.Vulkan/Shaders/GlslParser.cs` |
| Program interface layout | `Optimum.Render.Vulkan/Shaders/ProgramInterfaceLayout.cs` |
| Reserved-word and built-in renaming | `Optimum.Render.Vulkan/Shaders/GlslReservedWords.cs` |
| Rewriter | `Optimum.Render.Vulkan/Shaders/ShaderRewriter.cs` |
| shaderc wrapper | `Optimum.Render.Vulkan/Shaders/ShaderCompiler.cs` |
| Orchestrator | `Optimum.Render.Vulkan/Shaders/ShaderTranslator.cs` |
| Instance, device selection, feature negotiation | `Optimum.Render.Vulkan/Core/VulkanContext.cs` |
| Buffers, images, memory, commands, barriers | `Optimum.Render.Vulkan/Core/VulkanResources.cs` |
| GL constant translation | `Optimum.Render.Vulkan/Core/GlEnums.cs` |
| Emulated GL state machine, pipeline key, interning | `Optimum.Render.Vulkan/Core/GlStateTracker.cs` |
| Vertex layouts and attribute format mapping | `Optimum.Render.Vulkan/Core/VertexLayout.cs` |
| Per-program modules, descriptor layouts, uniform shadow | `Optimum.Render.Vulkan/Core/ShaderProgramResources.cs` |
| Pipeline cache with on-disk driver blob | `Optimum.Render.Vulkan/Core/PipelineCache.cs` |
| Frame ring, uniform ring, deferred deletion | `Optimum.Render.Vulkan/Core/FrameRing.cs` |
| Descriptor set cache | `Optimum.Render.Vulkan/Core/DescriptorCache.cs` |
| Textures, sampler cache, mipmaps | `Optimum.Render.Vulkan/Core/TextureManager.cs` |
| Render targets and dynamic rendering scopes | `Optimum.Render.Vulkan/Core/RenderTargetManager.cs` |
| Meshes, vertex buffers, indexed and indirect draws | `Optimum.Render.Vulkan/Core/MeshManager.cs` |
| Window surface creation via the client's own GLFW | `Optimum.Render.Vulkan/Core/WindowSurface.cs` |
| Swapchain, present, resize, vsync, the single Y flip | `Optimum.Render.Vulkan/Core/Swapchain.cs` |
| **The device implementing the seam** | `Optimum.Render.Vulkan/VulkanDevice.cs` |

Verified against real hardware, not just the validator: pixels round-trip through
texture upload and readback; an indexed mesh renders with its vertex colours; a
multi-attachment target honours `glDrawBuffers` selection, including the
composition case where attachment 0 is written while attachment 1 is left
untouched and readable. Every GPU test runs with validation layers on and asserts
the message log is clean.

### Further corrections the build forced

Beyond the six shader-translation fixes above, building the device found three
more design errors:

7. **The uniform ring must be one buffer for the whole frame ring**, not one per
   slot. Descriptor sets are only reusable across frames if the set names a
   buffer that does not change; the per-draw offset then travels as a dynamic
   offset. A buffer per slot would mean rewriting every set every frame, which is
   the exact cost the descriptor cache exists to avoid.
8. **A frame that begins must submit.** `BeginFrame` resets the slot's fence, so a
   slot begun and never submitted leaves the fence unsignalled and deadlocks the
   ring on its next rotation. The ring now has an explicit `EndFrame`.
9. **A custom mesh part claims its attribute location by being declared, not by
   holding data.** `CustomMeshDataPart.AllocationSize` returns `Count`, which is
   zero for a part allocated now and filled later, and the GL allocator adds the
   attribute pointers regardless. Gating on size shifted every subsequent
   location and would have misfed the chunk shaders.
10. **The empty vertex layout needs a reserved id.** The fullscreen
    post-processing passes bind no vertex buffers but still need a layout id for
    the pipeline key, and asking the interner for id 0 before any mesh existed
    indexed past the end of it.
11. **The GPU tests cannot run in parallel.** Three of them drive GLFW's
    process-global init and terminate, which is not thread safe, and xunit runs
    collections concurrently by default. The full suite crashed the test host
    outright until parallelisation was disabled for the assembly.

The presentation design settled as: the client renders into an ordinary offscreen
target that stands in for the default framebuffer, and presenting blits that into
the acquired swapchain image **with the source rows read bottom-to-top**. That
inverted blit is the entire Y-flip story - one image copy, at the very end -
which is what leaves every intermediate target, render-to-texture round trip and
screenshot byte-identical to the OpenGL path. Rendering straight into a swapchain
image would have put the flip in the middle of the pipeline instead.

`VulkanDevice` is exercised only through `IOptimumGraphicsDevice` by
`VulkanDeviceIntegrationTests`, because that is all `ClientPlatformWindows` will
ever see: compile a GLSL 330 pair, link, create a target, set a uniform, draw,
read the pixels back, and check the value survived. Sampler units, uniform
persistence across frames, and GL-style id reuse are covered the same way.

`SwapchainTests` brings the device up against a real hidden GLFW window created
with `ClientApi.NoApi` - the one change the client's window creation needs - and
presents frames through it, across resizes and vsync toggles, on Wayland and
with validation on.

### Client integration: the Cecil question is settled

The plan listed "can Cecil transplant the window-creation region" as the
integration risk with no precedent (Phase 0 spike 2). It is answered, with a
working artifact rather than an argument:

- `ClientProgram::Start` was **already** a transplant target, and transplanted
  bodies already reference contracts types in twenty places through the FSR work.
- Backend selection now lives in that method and survives the transplant:
  `126/126 required methods patched`, and the decompiled patched assembly
  contains `OptimumRenderBootstrap.ShouldTryVulkan` and
  `OptimumRender.FallBackToOpenGL` at the right points.
- The whole change set reconstructs from tracked files: a fresh
  `scripts/bootstrap.sh` restores it, then build, Cecil patch and every suite
  pass.

The order is forced by the window. A window created with `ContextAPI.NoAPI`
cannot be handed back to OpenGL, so the decision is final before it opens:
`ShouldTryVulkan` loads the backend assembly and creates a throwaway device to
answer it. If the device still fails afterwards - the probe passed but the
surface or swapchain did not - the window is closed and reopened for OpenGL.

Guardrails, in `Optimum.Tests/vulkan-backend-integration-tests.cs`: the decision
precedes the API choice, a failed install reopens the window, the transplanted
body stays lambda-free, the client never names the renderer assembly, the fork
excludes the contracts types (CS0433 only shows up on a full build), and OpenGL
remains the default.

### Two things the repository taught along the way

- **New Optimum-owned API source belongs in the working tree, not `sources/`.**
  `extract-patches.sh` wipes `sources/` and regenerates it from the tree, so
  files authored directly into `sources/` are deleted on the next extraction.
  The tree is the input; `sources/` is the output.
- **`VintagestoryAPI.csproj` is carried as a `sources/` overlay, not a patch.**
  Bootstrap applies patches and *then* copies `sources/` over them, so an overlay
  silently wins over a patch for the same file. The exclusion entries go in the
  overlay.

### The branch pattern, in the client

`ClientPlatformWindows` now routes 28 methods to the device, all in the shape
section 3 describes:

```csharp
IOptimumGraphicsDevice optimumDevice = OptimumRender.Device;
if (optimumDevice != null) { optimumDevice.SetViewport(x, y, width, height); return; }
GL.Viewport(x, y, width, height);   // vanilla body, untouched
```

Covered so far: the whole fixed-function state group (viewport, scissor, depth,
cull, blend including the per-attachment SSAO overrides, colour mask, stencil,
wireframe, line width), texture binding and deletion, sampler creation, the
capability strings, and the frame lifecycle - `window_RenderFrame` now brackets
`frameHandler.OnNewFrame` with `BeginFrame`/`Present` on the device path and
still reaches `SwapBuffers` on the OpenGL one.

Three invariants are pinned by test, because each fails silently rather than
loudly:

- **Off is vanilla.** Every routed method keeps its original GL body; the branch
  is inserted in front of it, never in place of it.
- **Every routed method is a registered transplant target.** One that is not
  compiles into the donor and then ships nothing, since Optimum patches the
  vanilla assembly rather than replacing it.
- **The branches stay lambda-free**, for the same Cecil reason as everywhere else.

Shaders route too. `CompileShader` only *stages* a stage on the device path,
because GL matches uniforms and varyings by name across the whole program and
nothing is final until link; `CreateShaderProgram` links, and the device returns
the program id the caller stores, exactly as `glCreateProgram` did.
`ShaderProgramBase`'s whole uniform and texture-binding surface follows - the
seventeen setters, both matrix forms, and `BindTexture2D`/`BindTextureCube`.

Two details there were worth getting right rather than pattern-matching:

- **`Vec2i` is a vec2 but `Vec3i` is an ivec3.** The GL body casts the first to
  float and leaves the second as integers. Scalar block layout stores an ivec3 as
  three consecutive 32-bit ints, so the components are written at separate
  offsets rather than through the float path.
- **A stale sampler override silently wins.** Binding a texture with no custom
  sampler now clears the unit's override, or the texture's own filtering would be
  ignored.

### A pre-existing test race this surfaced, and the stopgap for it

Twelve test classes mutate `OptimumDiagnostics`, whose counters are **plain
statics** shared by the process while the recording context is `[ThreadStatic]`.
Several also flip `StutterWatchEnabled` process-wide, which makes production
animation and launch-task code record into those same counters from whatever else
is running concurrently.

xunit runs collections in parallel, so those classes raced. The failure moved
between `EntityAnimationDiagnosticsCoverage`, `LaunchTaskTimeBudget` and
`OptimumStatus` across runs, always as a count that did not match. The race
predates this work, but adding test classes changed the scheduling enough to
surface it nearly every run - which made the suite effectively red.

`Optimum.Tests/AssemblyInfo.cs` therefore disables parallelisation for that
assembly. Six consecutive full runs are green and the suite went from 380 ms to
1 s, which is a fair price for removing the whole class of failure. **This is a
stopgap in shared test infrastructure, not the real fix** - scoping the counters
per context so the tests stop sharing global state would let it be removed.

### Meshes, and a third Cecil constraint

Mesh allocation, upload, update, deletion and all four draw forms now route.
`VAO.VaoId` carries the device's mesh handle, so `MeshRef` - public API that mods
hold - is unchanged, and `VAO.Dispose` is routed too: on the device path the vbo
fields are all zero and `VaoId` is not a GL vertex array, so falling through would
hand an unrelated integer to `glDeleteVertexArray`. That method also runs on the
finalizer thread, which is precisely why the device defers deletion rather than
destroying inline.

Routing `UpdateMesh` exposed a **third** transplant constraint, alongside cached
lambdas and closure classes: the decompiled body contained a `string.Format` call
rendered as a params-span, which the compiler lowers through a generated
`<PrivateImplementationDetails>::InlineArrayFirstElementRef` helper. Cecil clones
only the named method, so the transplant referenced a helper absent from the
vanilla assembly and the verifier refused to write any output at all. Passing the
four arguments directly emits no helper. Worth remembering: **any C# lowering that
generates a hidden helper type breaks a transplant**, not just lambdas.

`check-patches.sh` also earned its keep here - it caught `VAO.cs.patch` as an
orphan before it could ship as a patch that applies but transplants nothing.

### The framebuffer set

`SetupDefaultFrameBuffers` is four hundred lines of raw GL that generates its own
names and attaches its own textures - there is no seam inside it to route
through. So the device path is a separate `SetupOptimumFrameBuffers` that mirrors
the layout: every slot index, size and format, the Primary G-buffer widening to
four attachments under SSAO, the Transparent target sharing Primary's depth
texture, the shadow maps sized by quality, and Optimum's own FSR intermediate at
native resolution.

The SSAO block needed care beyond attachments. Its noise texture and 64-sample
kernel are drawn from one `Random(5)` in a fixed order, so the device path draws
them in the same order from the same seed - reordering changes the occlusion
pattern without failing. The noise texture also forced a small seam extension:
`EnumTextureInternalFormat` names only four formats, and this setup uses GL_RGB
and GL_RGBA32F, so `CreateTexture2DRaw` takes the GL constant directly, matching
how the seam already accepts GL constants for texture parameters.

Guardrails: every `EnumFrameBuffer` slot is asserted populated, the shared depth
texture is asserted shared, the SSAO attachment widening is pinned, and the noise
and kernel are pinned to their seed and order.

### Next

Phase 1 is done: the client can select the backend, and every graphics call it
makes reaches the device. Phase 2 is world rendering, and it is a different kind
of work - running the real client on Vulkan and fixing what renders wrong. That
needs the game in front of a person rather than a test, and it is where the
remaining time in the estimate sits.

### What the build corrected in this plan

Six things were wrong or missing in the design as first written. All are fixed in
code and in section 6 below.

1. **Varyings need explicit locations.** SPIR-V requires a `location` on every
   user-defined input and output; GLSL 330 leaves them implicit and the vanilla
   shaders declare bare `out vec2 texCoord;`. Vertex output and fragment input
   must agree, so this is program-wide state, not a per-stage rewrite. The
   original plan did not mention it.
2. **The generated uniform block must be emitted per stage, not whole.**
   `bilateralblur.vsh` declares `uniform vec2 frameSize` while its fragment
   shader declares `in vec2 frameSize`. Emitting the program's whole uniform
   union into both stages redefines the varying. Each stage now gets only the
   members it declared, with `layout(offset = N)` on each so they still share one
   buffer layout.
3. **GLSL 4.x reserved words have to be renamed.** Vulkan forces `#version 450`,
   and `ssao.fsh` uses `sample` as a local, which 4.00 turned into a qualifier.
4. **`gl_VertexID` is not `gl_VertexIndex`.** The plan assumed glslang would map
   the GL spelling under Vulkan semantics; it does not, and 17 shaders use it.
   The values differ in principle - the Vulkan built-ins count from the draw's
   vertex offset and first instance - but this client issues no draw with either
   set, so they agree wherever they are used.
5. **Array sizes can be constant expressions.** `fogandlight.vsh` declares
   `uniform vec4 fogSpheres[3 * 8];`, which needs evaluating, not just parsing.
6. **Memory qualifiers must be stepped over.** `chunkopaque.vsh` declares
   `readonly buffer faceDataBuf`, and not skipping `readonly` left the storage
   block unclassified, which silently put it in the wrong descriptor set. It
   still compiled, which is why this needed a test rather than a run.

### Cross-checks against current practice

- **Dynamic rendering over render-pass objects** is what NVIDIA recommends for
  Vulkan 1.3 and removes the render-pass and framebuffer object graph entirely.
  Confirmed; section 5 unchanged.
- **Cached descriptor sets** remain the right default. Arm's measurements put
  descriptor-set caching at roughly a third off frame time in CPU-heavy scenes,
  and `VK_EXT_descriptor_buffer` is a later, optional refinement rather than a
  starting point. Confirmed; section 5.8 unchanged.
- **Scalar block layout** behaves as the plan assumed and is the reason array
  uploads stay a memcpy. Confirmed and now covered by tests.
- **No Y flip** survived scrutiny. The common advice for porting an application
  is a negative viewport height, but that is wrong for an emulation layer: it
  would invert every render-to-texture round trip unless texture coordinates were
  flipped to match. GL and Vulkan actually agree on how clip space maps to
  framebuffer memory and to texture coordinates; they differ only in which corner
  they call the origin, and in depth range. Flipping nothing reproduces GL bit for
  bit, and scanout is corrected once in `Present`.

## 1. Background and problem statement

### Why a backend and not a bridge

`tools/InteropProbe` run on the target device (MSI Claw 8 AI+ A2VM, Arc 140V,
driver 32.0.101.8992, Windows 11 26200) shows every cross-API sharing route open:
`WGL_NV_DX_interop2` with all four entry points, `GL_EXT_memory_object_win32` and
`GL_EXT_semaphore_win32`. Mesa on the Linux dev box exposes the `_fd` variants. So
a GL-rendered scene *can* be handed to a Vulkan or D3D device for super
resolution without a backend, and that remains the cheapest way to get XeSS-SR on
the Claw (section 13).

The backend buys three things the bridge cannot:

1. **Presentation ownership.** Frame generation hooks `Present`/`vkQueuePresentKHR`.
   With Vulkan owning the swapchain, DLSS-FG runs natively and XeSS-FG runs behind
   a D3D12 proxy fed through `VK_KHR_external_memory_win32`, the same proxy shape
   Skyrim Community Shaders uses in front of a D3D11 renderer.
2. **Escaping Intel's OpenGL driver.** `ClientSystemStartup.cs:1076` already
   special-cases `"Arc(TM)"` to work around it. On the Claw, Intel's Vulkan driver
   is the well-maintained one; its GL driver is the weak link in every interop
   chain.
3. **A modern API under the renderer** for later work: multithreaded command
   recording, explicit memory, and native SR integration at the final-blit seam
   rather than through a second device.

### What the renderer looks like today

Measured on the vanilla decompile:

| Quantity | Value | Where |
| --- | --- | --- |
| `ClientPlatformAbstract` abstract members | 147 | `VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs` |
| `ClientPlatformWindows.cs` size | 3,755 lines, 519 GL call sites | `…/ClientPlatformWindows.cs` |
| GL call sites in the whole client | 700 in 19 files | 74% inside `ClientPlatformWindows` |
| Distinct GL entry points used | **102** | Appendix A |
| Shader programs / files / includes | 42 / 84 (`.vsh`+`.fsh`) / 19 | `assets/game/shaders`, `shaderincludes` |
| Geometry shaders | 0 | |
| `#version` | `330 core` in all 84 files | rewritten to 430 for the 5 SSBO vertex shaders |
| Default-block `uniform` declarations | 488 | must move into a block for Vulkan |
| `layout(std140)` uniform blocks | 3 | `entityanimated.vsh`, `gui.vsh`, `shadowmapentityanimated.vsh` |
| SSBO vertex shaders | 5 | `chunkopaque`, `chunktransparent`, `chunktopsoil`, `chunkshadowmap`, `decals` |
| Max vertex attribute location | 9 | `clouds.vsh` |
| Max fragment output location | 3 (4 MRTs) | Primary FBO with SSAO |
| Framebuffer slots | 18 vanilla (0–17), 18 claimed by Optimum FSR | `EnumFrameBuffer` |
| GL threads | 1 (the GLFW window thread) | tesselators hand off via `EnqueueMainThreadTask` |

The frame is a fixed linear sequence in `ScreenManager.Render`
(`VintagestoryLib/Vintagestory.Client/ScreenManager.cs:707`):

```
ClearFrameBuffer(Default) → ClearFrameBuffer(Primary) → LoadFrameBuffer(Primary)
→ CurrentScreen.RenderToPrimary (shadow maps, opaque, OIT/transparent, entities, particles…)
→ RenderPostprocessingEffects (bloom, godrays, SSAO)
→ RenderFinalComposition (final.fsh into Primary attachment 0)
→ BlitPrimaryToDefault (Optimum FSR1 lives here)
→ GUI into the default framebuffer
→ SwapBuffers (ClientPlatformWindows.cs:508)
```

### The seam is real but leaks

`ClientPlatformAbstract` is a genuine backend interface: windowing, input, audio,
bitmaps, screenshots, meshes, textures, framebuffers, shaders and the post chain
all go through it. But 19 files call `GL.*` directly (section 7), and two things
outside the platform class are graphics-critical:

- `ShaderProgramBase` (`…/ShaderProgramBase.cs`) issues `GL.Uniform*`,
  `GL.UseProgram`, `GL.ActiveTexture/BindTexture/BindSampler` itself, 38 sites.
  This is the mod-facing `IShaderProgram` implementation.
- `VAO` and `UBO` (`…/VAO.cs`, `…/UBO.cs`) own GL buffer handles and delete them
  in `Dispose`, from finalizers too.

Everything the game and mods do with graphics reduces to an immediate-mode,
GL-shaped protocol: set state, set named uniforms on the active program, bind
textures to units, draw a `MeshRef`. That protocol is the contract this plan
preserves.

## 2. Constraints, principles and non-goals

### Licensing

`LICENSE-SCOPE.md` keeps `patches/**`, `sources/**` and `Vintagestory/**` outside
the MIT grant, and `NOTICE` forbids redistributing Anego-owned material. The
Vulkan device is new code and can be MIT. Anything that is a modified copy of the
decompile (transplanted method bodies, the branch points in `ClientPlatformWindows`)
stays in `patches/` under the existing scope. The plan is arranged so the GL
implementation is never *extracted* into an MIT assembly: it stays where it is.

### The Cecil transplant rules

Optimum does not ship a recompiled `VintagestoryLib.dll`. `Optimum.Patcher`
transplants method bodies, injects members and whole types from the compiled
donor into the vanilla assembly at launch (`Optimum.Launcher/Program.cs:31`,
`Optimum.Patcher/Program.cs`). Two rules from `Optimum.Tests/cecil-transplant-lambda-tests.cs`
shape everything below:

- A transplanted method must not contain a lambda that the compiler caches in a
  `<>c` class (LINQ predicates, non-capturing lambdas). The `ClientProgram.cs`
  patch already carries the comment "written lambda-free so the body survives the
  Mono.Cecil transplant"; the FSR patch replaced
  `ArrayUtil.CreateFilled(…, n => GL.GenTexture())` with a loop for this reason.
- Whole injected types are cloned with their nested types
  (`Optimum.Patcher/MemberInjector.cs:356-382`) but are still verified against
  the vanilla target; large, generic-heavy code is a poor fit for injection.

Consequence: **the Vulkan device lives in its own normal assembly** with
unrestricted C#, and only thin, lambda-free branch points are transplanted into
`VintagestoryLib`.

### Mod compatibility contract

Mods reach graphics through `IRenderAPI` (85 methods, `VintagestoryAPI/Vintagestory.API.Client/IRenderAPI.cs`),
`IShaderAPI`, `IShaderProgram`, `MeshRef`, `UBORef`, `LoadedTexture`,
`FrameBufferRef`, all of which expose GL integer ids (`LoadedTexture.TextureId`,
`FrameBufferRef.ColorTextureIds`, `UBORef.Handle`). Mods author GLSL 330 and
register it with `RegisterFileShaderProgram`. Some mods (and the bundled
FluffyClouds in `VSEssentials`) call `GL.*` directly.

The contract: everything reachable through the API works on Vulkan unchanged,
including mod GLSL. Raw-GL mods are detected before the window opens and force
the GL backend for that session (section 9).

### Principles

- **OFF is vanilla.** With `Renderer = "opengl"` the client executes the vanilla
  GL code, byte for byte where Optimum has not already patched it. The Vulkan
  path is a branch that is never taken, the same rule `GREEDYMESH 0` follows.
- **Additive, selectable, self-disabling.** A Vulkan initialisation failure, a
  GL-bound mod, or a crash marker from the previous session falls back to GL with
  a logged reason, mirroring `DisableOptimumFsr`.
- **Emulate, don't refactor.** No render system is rewritten. The device
  implements the GL state machine; render systems keep calling
  `GlToggleBlend`, `Uniform("name", …)`, `BindTexture2D`, `RenderMesh`.

### Non-goals

- Rewriting render systems or mods against an explicit modern API.
- Removing OpenGL. Hardware without Vulkan 1.3 (pre-2016 GPUs, macOS without
  MoltenVK work) keeps the GL path indefinitely.
- Multithreaded command recording in the first release. The device is
  single-threaded by construction; the seams for recording chunk draws on
  workers are left open but not built.
- macOS. MoltenVK's Vulkan 1.3 coverage is incomplete and Optimum's macOS builds
  are archival. macOS is GL-only in this plan.
- A D3D12 backend. The abstraction is API-neutral so one could follow, but the
  decision on 2026-09-08 was Vulkan as the renderer with a D3D12 proxy only for
  XeSS-FG.

### Decisions taken 2026-09-08

1. Vulkan is the renderer API, not D3D12. XeSS-SR, DLSS-SR/FG and FSR 2/3 run
   natively; XeSS-FG/XeLL (D3D12-only) come later through a D3D12 presentation
   proxy fed by Vulkan external memory.
2. Primary target device is the Arc 140V handheld; dev box is CachyOS. Both
   platforms are first-class; Windows and Linux ship together.
3. OpenGL remains available and default until parity; Vulkan is opt-in, then
   `auto` per vendor once the vendor matrix is clean.
4. TAA with a real velocity buffer is built regardless of backend; it is
   shader and matrix work and proceeds on GL in parallel (section 11).

## 3. Architecture

```
ClientProgram (window: GLFW via OpenTK, ContextAPI.NoAPI when Vulkan)
   └─ ClientPlatformWindows  (windowing, input, audio, single-player server: unchanged)
        ├─ graphics methods (transplanted): if (device != null) device.X(…) else { vanilla GL }
        └─ IOptimumGraphicsDevice device   ← null = OpenGL, vanilla code runs
                └─ Optimum.Render.Vulkan.dll : VulkanDevice
                     ├─ GL state emulation (blend, depth, cull, scissor, units, current program, current FBO)
                     ├─ handle tables (textures, buffers/meshes, framebuffers, samplers, queries, programs)
                     ├─ shader pipeline (preprocess → rewrite → shaderc → SPIR-V cache → link)
                     ├─ pipeline cache, descriptor set cache, uniform ring buffer
                     ├─ frame lifecycle (2 frames in flight, deferred deletion, uploads with pass break)
                     └─ swapchain + present (the only Y flip in the system)
ShaderProgramBase / VAO / UBO / 15 leak sites: same branch, same device
```

### The additive-branch pattern

Every graphics method that Optimum must route gets one shape:

```csharp
public override void GlToggleBlend(bool on, EnumBlendMode blendMode = EnumBlendMode.Standard)
{
    IOptimumGraphicsDevice device = OptimumRender.Device;
    if (device != null) { device.ToggleBlend(on, blendMode); return; }
    // vanilla body, untouched
    if (on) { GL.Enable((EnableCap)3042); … }
}
```

The static `OptimumRender.Device` is null on the GL path. The cost is one null
check per call; the benefit is that the GL body stays vanilla and every method is
a trivially lambda-free transplant. Where a vanilla method has a lambda, the
existing FSR precedent applies: replace it with a loop or a named method in the
same patch.

### Where the abstraction lives

`IOptimumGraphicsDevice`, its handle structs and enums go into
`optimum-api-contracts`, which the API patcher merges into `VintagestoryAPI.dll`
(`Optimum.Patcher/api-patcher.cs`, namespace convention `Vintagestory.API.Config`
per `optimum-api-bridge.cs:11`). Rationale: transplanted bodies in
`VintagestoryLib`, `VSEssentials` and `VSSurvivalMod` can all reference it without
adding a new `AssemblyRef` to a vanilla assembly, which is the one Cecil path
this repository has not exercised. The interface takes only API-level types
(`MeshData`, `MeshRef`, `IShader`, `IShaderProgram`, `IBitmap`/`BitmapRef`,
`SKBitmap`, Cairo `ImageSurface`, `FrameBufferRef`, `FramebufferAttrs`) so
`Optimum.Render.Vulkan.dll` references `VintagestoryAPI.dll` and Silk.NET, never
`VintagestoryLib.dll`. It is documented as an internal contract, not a mod API.

### Threading

All GL calls today happen on the GLFW thread; tesselators and the atlas manager
marshal uploads via `EnqueueMainThreadTask` (`ClientMain.cs:1032`,
`TextureAtlasManager.cs:151-166`). The device inherits that: one thread, one
command buffer in flight per frame, a debug-build assertion on the owning thread
id. `VAO`/`UBO` finalizers call `Dispose` from the finalizer thread; the device
queues those into a lock-free deferred-deletion list drained on the render
thread, which also solves the in-flight-resource problem (section 5.3).

### Backend selection

`OptimumConfig.Renderer` ∈ `opengl` (default) | `vulkan` | `auto`. Resolution
happens in the launcher-injected hook before `ClientProgram` builds
`NativeWindowSettings` (`ClientProgram.cs:281-295`):

1. `opengl` → nothing changes.
2. `vulkan`/`auto` → run the mod scan (section 9). Any GL-bound mod → `opengl`,
   with a launcher log line and a one-time in-game notice.
3. Crash-loop guard: `.optimum/vulkan-session.lock` written at device creation,
   deleted on clean shutdown. Present at startup → this session runs `opengl`
   and records the fallback; the next clean GL session clears it.
4. Create the Vulkan instance and pick a device (section 5.2). Any failure →
   `opengl`.
5. Only then does the window open with `ContextAPI.NoAPI`.

`auto` additionally consults a vendor/driver allow-list that starts empty and
grows as the matrix in section 10 goes green. The settings tab shows the active
backend and the fallback reason.

## 4. The device contract

The interface mirrors the graphics half of `ClientPlatformAbstract` plus the
operations that leak around it. Grouped, with the GL entry points each group
absorbs (full mapping in Appendix A):

| Group | Operations | Absorbs |
| --- | --- | --- |
| Capabilities | `Init`, `Shutdown`, `MaxTextureSize`, `SupportsThickLines`, `Renderer/Vendor/Version` strings, `ShaderVersionString` | `GetString`, `GetInteger`, `GetFloat`, `GetError` |
| Fixed-function state | viewport, scissor+flag, depth test/mask/func, cull enable/face, blend enable+mode, per-attachment blend func/equation, color mask, stencil test/func/op/mask, polygon mode, line width | `Enable`, `Disable`, `DepthFunc`, `DepthMask`, `CullFace`, `BlendFunc`, `BlendFuncSeparate`, `BlendEquation`, `ColorMask`, `Stencil*`, `Scissor`, `Viewport`, `PolygonMode`, `LineWidth`, `IsEnabled`, `Hint` (ignored), `DepthRange` (no-op, see below) |
| Meshes | `AllocateEmptyMesh`, `AllocateEmptySSBOMesh`, `UploadMesh`, `UpdateMesh`, `UpdateSSBOMesh`, `DeleteMesh`, `Map` (persistent pointer), `RenderMesh`, `RenderMeshMulti` (starts/sizes/groupCount, ssbo flag), `RenderMeshInstanced`, `RenderFullscreenTriangle` | `Gen/Bind/DeleteBuffer(s)`, `BufferData`, `BufferSubData`, `BufferStorage`, `MapBufferRange`, `Gen/Bind/DeleteVertexArray`, `VertexAttrib(I)Pointer`, `VertexAttribDivisor`, `EnableVertexAttribArray`, `BindBufferBase`, `DrawElements`, `DrawElementsInstanced`, `DrawArrays`, `MultiDrawElements` |
| Textures | create 2D/2D-array/cube, upload (rect, mip), sub-upload, generate mipmaps, delete, `SetTexParameter(handle, pname, value)`, bind to unit, `BindSampler(unit, sampler)`, `GenSampler`, `SamplerParameter` | `GenTexture`, `BindTexture`, `TexImage2D/3D`, `TexSubImage2D`, `TexParameter`, `GetTexParameter`, `GenerateMipmap`, `ActiveTexture`, `DeleteTexture`, `Gen/Bind/DeleteSampler`, `SamplerParameter`, `ReadPixels` |
| Framebuffers | `CreateFramebuffer(attrs)`, `AttachTexture(fbo, attachment, tex, layer)`, `SetDrawBuffers(fbo, mask)`, `Bind(fbo)`, `ClearColor(attachment, rgba)`, `ClearDepth`, `ClearStencil`, `CheckStatus`, `Delete`, `CurrentFramebuffer` (read) | `Gen/Bind/DeleteFramebuffer`, `FramebufferTexture2D`, `FramebufferTextureLayer`, `DrawBuffer(s)`, `ReadBuffer(s)`, `Clear`, `ClearColor`, `ClearBuffer`, `GetInteger(FRAMEBUFFER_BINDING/VIEWPORT)` |
| Shaders | `CompileShader(IShader)`, `LinkProgram(IShaderProgram)`, `UseProgram`, `GetUniformLocation`, `SetUniform*` (float/int/vec/mat, arrays), `SetSamplerUnit(program, name, unit)`, `BindUniformBlock`, `CreateUBO`/`UpdateUBO`/`DeleteUBO`, `DeleteProgram` | `Create/Compile/Delete/Attach/DetachShader`, `ShaderSource`, `GetShader(InfoLog)`, `Create/Link/Use/DeleteProgram`, `GetProgram(InfoLog)`, `BindAttribLocation`, `GetUniformLocation`, `Uniform1/2/3/4`, `UniformMatrix4`, `UniformMatrix4x3`, `GetUniformBlockIndex`, `UniformBlockBinding` |
| Queries | `CreateOcclusionQuery`, `Begin`, `End`, `IsResultAvailable`, `GetResult`, `Delete` | `GenQueries`, `BeginQuery`, `EndQuery`, `GetQueryObject`, `DeleteQuery` |
| Frame | `BeginFrame`, `Present`, `Resize`, `SetVSync`, `ReadbackDefaultFramebuffer` (screenshots, AVI) | `SwapBuffers`, `ReadPixels` |
| Debug | `SetDebugMode` (validation layers), `CheckError` (validation message drain), wireframe | `DebugMessageCallback`, `GetError` |

### Handles

Every id the game stores stays an `int`. The device keeps dense handle tables
(`textures[id]`, `buffers[id]`, `framebuffers[id]`, `samplers[id]`, `programs[id]`,
`queries[id]`) with generation counters in debug builds to catch use-after-delete.
`LoadedTexture.TextureId`, `FrameBufferRef.FboId/DepthTextureId/ColorTextureIds`,
`UBORef.Handle`, `VAO.*VboId` all remain valid opaque ints; mods that pass them
through the API never notice. `VAO` gains no new fields: the device keeps its own
`VkMesh` record keyed by `VaoId`.

### The emulated state machine

The device tracks exactly the GL state the game touches: blend enable + per
attachment (factor pairs, equation), depth test/mask/func, cull enable/face,
scissor rect + flag, viewport, color mask, stencil, polygon mode, line width,
`DrawBuffers` mask of the bound framebuffer, current program, 16 texture units
(target, texture, override sampler), current framebuffer. State changes are
recorded, not executed; a draw resolves them into a pipeline key, dynamic state
commands and descriptor sets (section 5.8). This is precisely how Zink and ANGLE
work and it is why render systems need no changes.

Texture parameter state is **per texture**, as in GL: `TexParameter` on the
texture bound to the active unit mutates that texture's min/mag filter, wrap,
LOD bias and compare mode; `BindSampler(unit, s)` overrides it for that unit.
Both resolve to an immutable `VkSampler` from a cache keyed by the parameter
tuple. The 107 `TexParameter` call sites and the FSR mip-bias
`SamplerParameter` calls in `ShaderRegistry.cs.patch` therefore work unchanged.

### Coordinate conventions

This is the part most GL-on-Vulkan ports get wrong, so the rule is stated once:

- **No Y flip anywhere except the final present.** Rendering with an unflipped
  viewport puts NDC y = −1 at image row 0 in both APIs, so every intermediate
  render target, every CPU-uploaded texture, every sampled UV and every
  `gl_FragCoord` read is bit-identical to GL. Screenshots and `ReadPixels` come
  back bottom-up, exactly as the GL path produces them
  (`Vintagestory.ClientNative/Screenshot.cs:73`).
- **Front face = `VK_FRONT_FACE_CLOCKWISE`.** GL's default CCW winding in a
  y-up framebuffer is CW in Vulkan's y-down framebuffer with unflipped NDC. The
  game never calls `GL.FrontFace`, so this is a constant. `GlCullFaceBack/Front`
  map 1:1.
- **Depth range.** GL clip z ∈ [−w, w], Vulkan z ∈ [0, w]. The shader rewriter
  wraps every vertex `main` and appends `gl_Position.z = (gl_Position.z +
  gl_Position.w) * 0.5;` (section 6). Projection matrices, the frustum culler,
  shadow orthos (`SystemRenderShadowMap.cs:163`) and mod matrices stay untouched.
- **The present pass** samples the internal default-framebuffer image with v
  flipped into the swapchain image. This is also the seam where upscalers and a
  frame-generation proxy attach later.
- `GL.DepthRange(0, 20000)` (`ScreenManager.cs:738`, `ClientMain.cs:1570`) and
  `ClearBuffer(GL_DEPTH, 20000)` are clamped to [0, 1] by GL and are therefore
  no-ops / clear-to-1.0. The device treats them the same; nobody should port
  them literally.

## 5. The Vulkan device

### 5.1 Bindings, versions, features

- **Silk.NET 2.23.0**: `Silk.NET.Vulkan`, `.Extensions.KHR`, `.Extensions.EXT`,
  `Silk.NET.Shaderc` + `.Native` (runtime GLSL→SPIR-V), `Silk.NET.SPIRV.Cross.Native`
  (debug-only reflection cross-check). All MIT/Apache-2.0, compatible with the
  MIT half of the repository.
- **Vulkan 1.3 minimum.** Dynamic rendering, synchronization2, the 1.3-core
  dynamic states (viewport/scissor with count, cull mode, front face, topology
  within class, depth test/write/compare, stencil) and `maintenance4` remove
  render-pass objects and most pipeline permutations. Arc 140V, RDNA, Turing+,
  and Mesa ANV/RADV all report 1.3 or 1.4. Anything lower runs GL.
- **Required features:** `independentBlend` (OIT and SSAO use per-attachment
  blend), `multiDrawIndirect` (chunk multidraw), `scalarBlockLayout` (1.2 core;
  makes the generated uniform block match GL client memory byte for byte),
  `timelineSemaphore` (1.2 core). **Optional:** `fillModeNonSolid` (wireframe
  debug), `wideLines` (sets `SupportsThickLines`), `samplerAnisotropy`,
  `VK_EXT_line_rasterization`.
- **Extensions:** `VK_KHR_swapchain`, surface extensions from
  `glfwGetRequiredInstanceExtensions`, `VK_EXT_debug_utils` in debug. Phase 5
  adds `VK_KHR_external_memory_{win32,fd}` and `VK_KHR_external_semaphore_{win32,fd}`.

### 5.2 Instance, device, surface, window

OpenTK 4.9.4's GLFW bindings already expose `glfwVulkanSupported`,
`glfwGetRequiredInstanceExtensions`, `glfwCreateWindowSurface`,
`glfwGetPhysicalDevicePresentationSupport` and `glfwGetInstanceProcAddress`
(verified in the package). The window is created by the existing
`NativeWindowSettings` block with `API = ContextAPI.NoAPI`;
`GameWindowNative`'s constructor (`GameWindowNative.cs:24-26`) does
`GL.ClearColor/Clear/SwapBuffers` and is patched to skip them when
`OptimumRender.Device != null`. `window_RenderFrame`'s `SwapBuffers`
(`ClientPlatformWindows.cs:508`, already a transplant target for frame pacing)
becomes `device.Present()`.

Device selection: prefer the adapter that presents to the surface; among those,
discrete over integrated unless `OptimumConfig.VulkanDeviceIndex` pins one.
Queue: one graphics+present queue (transfers on the same queue keep ordering
trivial). Validation layers and a debug messenger when `GlDebugMode` is on, with
messages routed through the existing `DebugCallback` logging shape
(`ClientPlatformWindows.cs:2017`).

### 5.3 Frame lifecycle

Two frames in flight (three later for frame generation). Per frame slot: a
command pool reset at frame start, one primary command buffer, a host-visible
**uniform ring** (16 MB), a **staging ring** (32 MB, grows), an **indirect-draw
ring**, a descriptor pool reset, and a **deferred deletion list** drained when
that slot's fence signals. `VAO.Dispose`/`UBO.Dispose`/`GLDeleteTexture` push
onto the current slot's list, so a resource used this frame is never destroyed
before its fence. Finalizer-thread disposes go through a concurrent queue into
the same list on the render thread.

`Present` ends any open rendering, transitions the default-framebuffer image,
records the flip-blit into the acquired swapchain image, submits with the slot's
fence and a timeline semaphore, presents, and acquires the next image lazily on
first use of the default framebuffer in the following frame.

### 5.4 Memory

An own allocator rather than VMA: Silk.NET does not ship VMA, Optimum's
packaging is deliberately native-light, and the allocation pattern is simple.
Three pools:

- **Device-local block pool** (128 MB blocks, first-fit free list with
  coalescing) for static meshes, textures, render targets. Chunk mesh churn is
  the load; free-list coalescing keeps fragmentation bounded, and a per-frame
  defragmentation budget is a later option.
- **Host-visible, coherent ring buffers** for uniforms, staging, indirect
  records; reset per frame slot.
- **Host-visible persistent buffers** for dynamic meshes (below).
- **Dedicated allocations** for images above 64 MB and for swapchain-sized targets.

**Persistent mapping.** `AllocateEmptyMesh` maps dynamic (`!staticDraw`) buffers
with `MAP_WRITE|PERSISTENT|COHERENT` and the game writes straight into the
pointer (`ClientPlatformWindows.cs:2800-2843`, `updateVAO`). GL offers no
protection against writing a buffer the GPU is still reading and the game relies
on that being fine. The device reproduces the semantics with host-visible mapped
buffers (device-local + host-visible when ReBAR exposes it) and the same lack of
sync, so behaviour matches GL. An opt-in per-mesh double buffer (alternate by
frame slot) is the fix if tearing appears; it costs memory, not code paths.

### 5.5 Meshes and draws

`VAO`'s one-buffer-per-attribute layout (`xyz`, `normals`, `uv`, `rgba`, `flags`,
four custom parts with interleave stride/offsets, optional per-instance
divisor; `ClientPlatformWindows.cs:2787-2998`) maps to one `VkVertexInputBinding`
per buffer and one attribute per slot. The device derives a **vertex layout
signature** at allocation (formats, strides, rates, slot count) and interns it;
there are roughly fifteen distinct layouts in the game (Appendix C) and the
signature is part of the pipeline key.

- `RenderMesh` → bind vertex buffers + index buffer (always `UNSIGNED_INT`,
  `VK_INDEX_TYPE_UINT32`), resolve pipeline/descriptors, `vkCmdDrawIndexed`.
- `RenderMeshInstanced` → `instanceCount`.
- `RenderMesh(starts, sizes, groupCount)` (`MultiDrawElements`,
  `ClientPlatformWindows.cs:1041-1062`) → write `groupCount` records into the
  indirect ring, one `vkCmdDrawIndexedIndirect`.
- The SSBO path binds the mesh's `xyz` buffer as storage binding 3 plus the shared
  `singleIndexBufferId`; the chunk shaders fetch vertices by `gl_VertexID`. The
  device binds the same buffer as a storage descriptor (set 2, binding 3) and
  the shared index buffer; nothing else changes.
- `RenderFullscreenTriangle` (`DrawArrays(3)`) → `vkCmdDraw(3)` with an empty
  vertex-input state.
- `UpdateMesh` non-persistent → staging ring + `vkCmdCopyBuffer` with a pass
  break if rendering is open (section 5.6); persistent → memcpy through the
  mapped pointer exactly as today.

### 5.6 Textures and samplers

`VkTexture { image, memory, default view, per-layer views, format, mips, layers,
glState (filters, wrap, lodBias, compareMode), layout }`. Format map from the
GL enums the API exposes (`EnumTextureInternalFormat`: `Rgba8`, `Rgba16f`, `R16f`,
`DepthComponent32`; plus the vanilla-internal `RGB8` reveal, `RGB`, `RGBA32F`):

| GL | Vulkan |
| --- | --- |
| `RGBA8` (32856) | `R8G8B8A8_UNORM` (BGRA uploads swizzled in the staging copy) |
| `RGBA16F` (34842) | `R16G16B16A16_SFLOAT` |
| `R16F` (33325) | `R16_SFLOAT` |
| `RGB8` (32849) / `RGB` (6407) | `R8G8B8A8_UNORM` (alpha ignored; RGB is not a guaranteed colour-attachment format) |
| `RGBA32F` (34836) | `R32G32B32A32_SFLOAT` |
| `DEPTH_COMPONENT32F` (33191) | `D32_SFLOAT` |
| default framebuffer depth/stencil | `D24_UNORM_S8_UINT` or `D32_SFLOAT_S8_UINT` (first supported) |

- Uploads (`LoadTexture*`, `LoadIntoTexture`, `TexSubImage2D` from
  `InventoryItemRenderer.cs:194` and `CloudRendererMap.cs:320`, Cairo/Skia
  surfaces) copy into the staging ring and record `vkCmdCopyBufferToImage` with
  layout transitions **inline in the frame command buffer at the point of the
  call**. GL guarantees an upload between two draws is visible to the second
  one; to keep that, the device ends the open `vkCmdBeginRendering` scope,
  records the copy and barriers, and resumes with `LOAD_OP_LOAD`. Dynamic
  rendering makes the break cheap; it happens a handful of times per frame
  (atlas updates, GUI textures).
- `GenerateMipmap` → blit chain, as DXVK/Zink do.
- Cube maps (`Load3DTextureCube`) → 6-layer image with a cube view.
- 2D array textures with layered attachments (OIT accumulation, 3 layers of
  `RGBA16F`, `SystemRenderOITLayers.cs:89-99`) → per-layer views attached as
  colour attachments 3–5.
- `TEXTURE_COMPARE_MODE` toggling on shadow maps
  (`SystemRenderFrameBufferDebug.cs:141-184`) → the texture's sampler state
  gains `compareEnable`; the sampler cache resolves it.
- Samplers: `GenSampler(linear)` → handle over an immutable `VkSampler`;
  `SamplerParameter(LOD_BIAS)` replaces the underlying object via the handle
  table, so Optimum's FSR mip bias works.

### 5.7 Render targets

`FrameBufferRef.FboId` indexes an FBO record: attachment list (texture handle,
layer), draw-buffer mask, size. `LoadFrameBuffer` only records "current"; the
next draw begins rendering. Two GL behaviours are load-bearing:

- **`DrawBuffers` selects the attachment subset, not just write masks.**
  `RenderFinalComposition` (`ClientPlatformWindows.cs:1947-1996`) renders into
  Primary attachment 0 with `DrawBuffers(1)` while sampling Primary attachment 1
  (`GlowParts2D`). Legal in GL because they are different textures. In Vulkan
  attachment 1 must not be part of the rendering scope, so `vkCmdBeginRendering`
  receives only the enabled attachments and attachment 1 is transitioned to
  `SHADER_READ_ONLY_OPTIMAL`. `DrawBuffers(0)` (`EntityBehaviorHideWaterSurface.cs:114`)
  is a depth-only scope. A change of the mask restarts the scope.
- **Clears.** `ClearFrameBuffer` variants and `ClearBuffer(COLOR, i, …)` map to
  `vkCmdClearAttachments` inside the scope (start a scope if none is open).
  Promoting a clear that immediately precedes a scope into `LOAD_OP_CLEAR` is a
  later optimisation; correctness first.
- **Layouts** are tracked per image with synchronization2 barriers:
  `COLOR/DEPTH_ATTACHMENT_OPTIMAL` while attached, `SHADER_READ_ONLY_OPTIMAL`
  when bound to a unit, `TRANSFER_*` around uploads/readbacks. A texture that is
  both currently attached and bound (a true GL feedback loop) is undefined in GL
  too; the device logs it once in debug and proceeds.
- The vanilla default-framebuffer inventory is in Appendix B; the device
  allocates identically sized images from the same `SetupDefaultFrameBuffers`
  patch, which is already a transplant target for FSR.

### 5.8 Pipelines and descriptors

**Pipeline key** = program id · vertex layout id · colour formats[] · depth
format · enabled-attachment mask · per-attachment blend (enable, src/dst colour
and alpha factors, equations) · polygon mode · topology (`Triangles`, `Lines`,
`LineStrip` — outside one dynamic class, so keyed). Everything else (viewport,
scissor, cull, front face, depth test/write/func, stencil, line width) is 1.3
dynamic state. Estimate: 42 programs × ~4 layouts × ~5 target sets × ~7 blend
states, in practice a few hundred pipelines, warmed from the on-disk
`VkPipelineCache`.

**Descriptor sets**, one layout per program produced by the shader rewriter:

- set 0, binding 0: the generated uniform block (`UNIFORM_BUFFER_DYNAMIC`);
  bindings 1..n: the program's declared `std140` blocks (`CreateUBO`).
- set 1: combined image samplers, one binding per `sampler2D`/`sampler2DShadow`/
  `samplerCube` in declaration order (the same order `ShaderProgram.collectUniformNames`
  assigns `textureLocations`, `ShaderProgram.cs:56-65`).
- set 2: storage buffers (SSBO vertex fetch at binding 3, entity animation data).

Per draw: the program's uniform shadow buffer, if dirty, is copied into the
uniform ring and bound through the dynamic offset; sampler bindings resolve unit
→ (view, sampler) and hit a **descriptor-set cache** keyed by that tuple list
(chunks bind the same atlas thousands of times; hit rate is effectively 100%).
Bindless descriptor indexing would remove even that lookup but requires
rewriting sampler *use sites* in GLSL, which is out of scope for an automatic
rewriter; it is a later optimisation, not a requirement.

### 5.9 Queries, readback, screenshots

`SystemRenderSunMoon` uses one occlusion query (`SAMPLES_PASSED`, result
availability polled, `SystemRenderSunMoon.cs:57-127`) → a small query pool with
`vkGetQueryPoolResults(…AVAILABILITY_BIT)` mirroring `QUERY_RESULT_AVAILABLE`.
`GrabScreenshot`/`SaveScreenshot`/AVI recording read the default framebuffer →
copy the internal default image to a host-visible buffer, wait the frame fence,
return rows bottom-up as GL does.

### 5.10 Caches

- SPIR-V per stage keyed by SHA-256 of the *rewritten* source plus rewriter
  version, stored under `.optimum/cache/spirv/`.
- `VkPipelineCache` blob keyed by device UUID and driver version, stored next to
  it. Both are advisory; a miss recompiles.

### 5.11 Swapchain, present, resize

`B8G8R8A8_UNORM` (GL's default framebuffer is linear; the game never enables
`FRAMEBUFFER_SRGB`), `FIFO` when `VsyncMode != 0`, `MAILBOX` if available else
`IMMEDIATE` otherwise; `SetVSync` recreates. `Window_Resize`
(`ClientPlatformWindows.cs:745`) already calls `RebuildFrameBuffers`; the device
recreates the swapchain and default images in the same place. Minimised (0×0)
windows skip acquire. `OUT_OF_DATE`/`SUBOPTIMAL` recreate on the next frame.

## 6. Shaders

### The pipeline

Today (`ClientPlatformWindows.cs:3691-3713`): `Shader.Code` is the include-expanded
source (`ShaderRegistry.HandleIncludes`), `PrefixCode` is the `#define` block
from `registerDefaultShaderCodePrefixes` spliced after the `#version` line, the
version is rewritten to 430 for SSBO shaders, and each stage compiles
independently; `CreateShaderProgram` links and `ShaderProgram.Compile` collects
uniform names with a regex and calls `GetUniformLocation`.

On Vulkan, `CompileShader(IShader)` cannot produce SPIR-V by itself because GL
links uniforms *by name across stages*: `zNear` in both stages is one uniform.
The generated block must therefore be identical in every stage of a program.
So:

1. `CompileShader`: splice prefix as today → **shaderc preprocess** (resolves
   the `#define FXAA 1 … #if` structure so the rewriter never sees
   conditionals) → parse declarations → stage a `StagedShader`. Returns true if
   preprocessing succeeded; errors surface through the same `Shader compile
   error in {file}` log line.
2. `LinkProgram`: union the stages' declarations → build one **program uniform
   layout** → emit the rewritten source per stage → **shaderc compile** to
   SPIR-V (cache hit or miss) → create shader modules and descriptor-set layouts
   → `GetUniformLocation(name)` returns the byte offset into the block or −1.
   Link errors surface as `Link error in shader program for pass {name}`.

### The rewriter

Runs on preprocessed GLSL, works on top-level declarations only, never on
function bodies except for one wrapper. Transforms:

| Input | Output |
| --- | --- |
| `#version 330 core` / `#version 130` (`ShaderProgramMinimalGui.cs`) | `#version 450` + `#extension GL_EXT_scalar_block_layout : require`; other `#extension` lines dropped |
| `uniform float x; uniform vec3 v[8]; uniform float g = 1.0;` (default block) | members of `layout(scalar, set=0, binding=0) uniform OptimumUniforms { … };`, each carrying `layout(offset = N)`. Emitted **per stage**, holding only what that stage declared: a name that is a uniform in one stage can be a varying in another (`bilateralblur`), and the explicit offsets let the subsets share one buffer layout. Initialisers are pre-filled into the shadow buffer (`final.fsh` relies on `extraGamma = 1.0`) |
| `uniform sampler2D t;` | `layout(set=1, binding=N) uniform sampler2D t;` |
| `layout(std140) uniform Block { … };` | `layout(std140, set=0, binding=1+k) uniform Block { … };` - the memory-layout qualifier is preserved, since those blocks are filled by UBO uploads whose striding already matches |
| `layout(binding=3, std430) readonly buffer B { … };` | `layout(binding=3, std430, set=2) readonly buffer B { … };` - a binding the shader declared is kept, because the mesh path binds the vertex buffer to that exact index |
| `out vec2 texCoord;` (varying, no location) | `layout(location=N) out vec2 texCoord;`, with the matching `in` in the next stage given the same N. SPIR-V requires locations that GLSL 330 left implicit, so they are assigned program-wide |
| `out vec4 outColor;` without location (`blit.fsh`) | `layout(location=0) out vec4 outColor;`, filling the lowest slot left free by any explicit ones |
| vertex `void main() { … }` | renamed `_optimum_main`; new `main` calls it then applies the depth remap |
| `sample`, `patch`, `subroutine`, … as identifiers (`ssao.fsh`) | renamed `_optimum_kw_*`. 4.x reserved words that 330 allowed as names. `buffer` and `shared` are deliberately excluded: they are storage qualifiers in these shaders |
| `gl_VertexID`, `gl_InstanceID` | rewritten to `gl_VertexIndex` / `gl_InstanceIndex`. glslang does **not** accept the GL spellings for Vulkan. The Vulkan built-ins count from the draw's vertex offset and first instance, but this client sets neither |
| `gl_FragCoord` | unchanged; identical semantics under the no-flip convention |

`scalar` layout is what makes `Uniform1(count, float[])`, `Uniforms3(count,
float[])`, `UniformMatrix4x3` and the `fogSpheres`/`pointLights`/`colorMapRects`
arrays pack exactly as GL client memory does (`float[]` stride 4, `vec3[]`
stride 12, `mat4x3` 48 bytes). With `std140` the CPU side would have to re-stride
every array upload; with `scalar` the setter is a `memcpy` at the recorded
offset. Shadow buffers are per program; the `Uniform(name, …)` overloads in
`ShaderProgramBase` become `device.SetUniform(programId, offset, span)`.

Sampler uniforms: `BindTexture2D(name, texId, unit)` (`ShaderProgramBase.cs:207-219`)
does `Uniform1(loc, unit); ActiveTexture(unit); BindTexture(id)` in GL. The device
records name→unit on the program and unit→texture on the context; at draw the
program's set-1 bindings resolve through both maps. `SystemRenderOITLayers.cs:33`
setting a sampler to unit 7 directly is the same `SetSamplerUnit` operation.

`program.attributes` / `BindAttribLocation` (`ClientPlatformWindows.cs:3737-3740`):
every shipped shader already declares explicit locations (Appendix C). The
rewriter honours `attributes` by rewriting the `in` declaration's location when
a mod uses the API instead of `layout(location)`.

### Mod shaders

Mod GLSL goes through the identical path from `RegisterFileShaderProgram`. What
fails is what would fail on a stricter GL driver: syntax glslang rejects, or
constructs the rewriter does not recognise at top level. Failure sets
`LoadError` and logs, exactly as a GL compile error does today, and the mod's
own fallback logic runs. Shader hot reload (`ReloadShaders`, the `.rs` command)
disposes programs, which invalidates their pipelines.

### CI coverage

A test compiles all 84 vanilla shaders, the 19 includes' users, Optimum's own
`sources/shaders/*` overlays and `ShaderProgramMinimalGui` through
preprocess → rewrite → shaderc on Linux CI (`Silk.NET.Shaderc.Native` runs
headless). Golden files pin the rewriter output for a handful of representative
shaders so a rewriter change is a visible diff.

## 6a. Constant defaults for unsupplied vertex attributes

GL guarantees a value for every vertex attribute a shader reads, whether or not
the draw supplies one: an unbound attribute reads the *current generic vertex
attribute*, which starts at `(0, 0, 0, 1)`. Vulkan has no equivalent. A pipeline
declares exactly the attributes its vertex input state names, and a shader input
with no matching attribute reads undefined values.

The game relies on the GL behaviour constantly, because meshes are built from
whichever `MeshData` parts a call site needs while shaders declare the full set.
The GUI quad is the clearest case: `QuadMeshUtilExt.GetQuadModelData()` builds it
with `withRgba: false` and no flags, so it carries positions and UVs alone, while
`gui.vsh` declares six inputs - adding `colorIn`, `renderFlagsIn`,
`damageEffectIn` and `jointId`. Three of those feed real logic:

- `renderFlagsIn` supplies the glow level and the packed normal.
- `jointId` indexes the animation transform array.
- `damageEffectIn` becomes `damageEffectV`, and `gui.fsh` opens with
  `if (def > 0) { ... if (f < def - 1.3) discard; }`.

Undefined there does not warn, does not fail validation, and does not crash. It
discards every fragment, so the whole interface renders as nothing over a correct
background - which reads as a texture or blending fault and is not.

The device therefore supplies the defaults itself, the way Zink and ANGLE do.
`ProgramInterfaceLayout.VertexInputs` records every input a program declares with
its location and type. At draw time
`VertexLayoutDescription.WithDefaultsFor(declared)` merges the mesh's layout with
one extra attribute per missing location, all pointing at a reserved binding
(`DefaultAttributeBinding`, 15 - meshes number from zero and the Vulkan minimum
for `maxVertexInputBindings` is 16, so it never collides) whose stride is zero, so
every vertex reads the same constant. The buffer behind it is 32 bytes:
`(0, 0, 0, 1)` as floats, then again as integers, because an integer attribute has
to read integer zeros rather than reinterpret float bits.

The pipeline key already names both the program and the mesh layout, so the merged
result is stable per cache entry and costs nothing per draw beyond one extra
`vkCmdBindVertexBuffers`.

## 7. Render systems and GL leak sites

Every direct `GL.*` use outside `ClientPlatformWindows`, with its treatment:

| File | Lines | What it does | Treatment |
| --- | --- | --- | --- |
| `ShaderProgramBase.cs` | 38 sites | uniforms, use/stop, bind texture/sampler, dispose | branch every method to the device; already the mod-facing implementation |
| `VAO.cs` | `Dispose` | deletes buffers/VAO | branch to `device.DeleteMesh(VaoId)` (deferred) |
| `UBO.cs` | all | bind base 0, `BufferData`/`SubData` | branch to `device.UpdateUBO` (ring copy) |
| `SystemRenderOITLayers.cs` | 33–115 | array texture, layered attachments, `BlendFunci`, `ClearBuffer` per attachment, direct unit binds | branch inside the already-patched file (Optimum owns it, with `optimumOitDisabled` fallback) |
| `ChunkRenderer.cs` | 323–331 | `BindSampler(unit, 0)` ×9 | `device.BindSampler(unit, 0)` |
| `SystemRenderSunMoon.cs` | 57–127, 425 | occlusion query, `ColorMask` | query group |
| `SystemRenderFrameBufferDebug.cs` | 141–184 | `TEXTURE_COMPARE_MODE` on shadow maps | `device.SetTexParameter(id, COMPARE_MODE, …)` |
| `SvgLoader.cs` | 87–93 | texture from raw pointer | `device.CreateTexture2D(rgba8, ptr)` |
| `ScreenManager.cs` | 737–738 | `ClearBuffer(depth, 20000)`, `DepthRange` | clear depth 1.0; no-op |
| `ClientMain.cs` | 1570, 1580 | `DepthRange` | no-op |
| `InventoryItemRenderer.cs` | 194 | `TexSubImage2D` clear of an atlas region | sub-upload |
| `ClientSystemStartup.cs` | 1076 | `GetString(RENDERER).Contains("Arc(TM)")` gate for SSBOs | `device.Renderer` string; the Arc SSBO workaround is a GL-driver bug and is skipped on Vulkan |
| `Screenshot.cs` | 73 | `ReadPixels` BGRA | readback group |
| `GameWindowNative.cs` | 24–26 | clear + swap in ctor | skipped on Vulkan |
| `VSEssentials/FluffyClouds/CloudRendererMap.cs` | 181–381 | own FBO with 2 MRTs, save/restore of `FRAMEBUFFER_BINDING`/`VIEWPORT`, `TexSubImage2D` of 16-bit data, `Uniform3` via `GetUniformLocation`, blend/depth toggles | runtime patch (`patches/runtime/VSEssentials`): use `IRenderAPI.CreateFramebuffer`/`LoadFrameBuffer` and `device` getters for the save/restore; this is the largest port outside the platform class (36 sites) |
| `VSEssentials/FluffyClouds/CloudRendererVolumetric.cs` | 79–82 | depth/blend toggles | `IRenderAPI` equivalents |
| `VSSurvivalMod/…/EntityBehaviorHideWaterSurface.cs` | 114, 131 | `DrawBuffers(0)` then `DrawBuffers(6)` | `device.SetDrawBuffers` |

Render systems that only use the platform API (`SystemRenderTerrain`, `Entities`,
`Particles`, `Decals`, `NightSky`, `SkyColor`, `ShadowMap`, `Aim`, `InsideBlock`,
`PlayerEffects`, `DebugWireframes`, `PlayerAimAcc`, `RiftTest`, `InventoryItemRenderer`
beyond line 194, `ChunkRenderer` beyond the sampler resets) need no change. The
post chain (`RenderPostprocessingEffects` 1818–1949, `RenderFinalComposition`
1951–2016, `MergeTransparentRenderPass` 1795–1816, `BlitPrimaryToDefault` 2036)
is inside `ClientPlatformWindows` and becomes transplant targets with the branch.

## 8. Shipping it in Optimum's framework

### New projects

| Project | Licence scope | Contents |
| --- | --- | --- |
| `optimum-api-contracts` (extended) | MIT | `IOptimumGraphicsDevice`, handle/enum types, `OptimumRender` static holder, `OptimumRenderBackend` enum |
| `Optimum.Render.Vulkan` | MIT | the device, allocator, shader pipeline, caches; references `VintagestoryAPI`, Silk.NET |
| `Optimum.Render.Vulkan.Tests` | MIT | rewriter goldens, allocator, pipeline-key, state-machine, lavapipe smoke |

### Patches (scope: `patches/`)

- `ClientPlatformWindows.cs.patch`: the branch in every graphics method (the
  method index at `ClientPlatformWindows.cs:1004-3755`, roughly 95 methods) plus
  `Start` (line-width probe → `device.SupportsThickLines`), `window_RenderFrame`
  (present), `Window_Resize`, `LogAndTestHardwareInfosStage2`.
- `ClientProgram.cs.patch`: backend resolution before `NativeWindowSettings`
  (`ClientProgram.cs:281`), `ContextAPI.NoAPI`, skip `AttemptToOpenWindow`'s GL
  version fallback loop on Vulkan, `AllowSSBOs` decided by the device.
- `GameWindowNative.cs.patch`, `ShaderProgramBase.cs.patch`, `ShaderProgram.cs.patch`
  (uniform collection stays; location lookup goes through the device),
  `Shader.cs.patch` (`EnsureVersionSupported` short-circuits on Vulkan),
  `ShaderRegistry.cs.patch` (existing; sampler parameter calls branch),
  `VAO.cs.patch`, `UBO.cs.patch`, and the leak sites in section 7.
- `Optimum.Patcher/Program.cs`: new `typesToInject` (none for the device; the
  holder lives in contracts), `membersToInject` for the new fields, and the
  transplant `targets`. `patches/cecil-owned.list` updated accordingly;
  `check-patches.sh` keeps them honest.

### Launcher

- `AssemblyLoader` already resolves from the launcher directory;
  `Optimum.Render.Vulkan.dll` and the shaderc native library ship next to
  `Optimum.exe`. The device is instantiated by name through
  `Assembly.Load` + `Activator.CreateInstance` from the contracts holder, so
  `VintagestoryLib` never references the Vulkan assembly.
- `ShaderCompatibilityScanner` (`Optimum.Launcher/ShaderCompatibilityScanner.cs`)
  gains a **backend scan**: metadata-only inspection of mod assemblies for
  references to `OpenTK.Graphics.OpenGL`, `OpenTK.Graphics.OpenGL4`,
  `OpenTK.Graphics.ES30` and P/Invokes into `opengl32`/`libGL`. Results land in
  the existing `shader-compatibility.json` as a `glBoundMods` list;
  `OptimumConfig` exposes `IsShaderFeatureDisabled("Vulkan")` in the same style.
  A second, advisory token scan flags Harmony mods that name
  `ClientPlatformWindows`, `ShaderProgramBase` or `VAO`, because they may patch
  internals the branch bypasses; those produce a warning, not a fallback.

### Config and settings

`OptimumConfig`: `Renderer`, `VulkanValidation`, `VulkanDeviceIndex`,
`VulkanPresentMode`, `VulkanDynamicMeshDoubleBuffer`. Persisted through
`OptimumConfigData` like `RenderScale`. Settings tab: a "Renderer" dropdown
(restart required), the active backend and fallback reason, a validation
toggle. Same `GuiCompositeSettings` injection pattern as the existing entries.

### Build, deploy, package

- `Directory.Build.props` unchanged; the new projects join `VintageStory.slnx`
  (the solution-integrity test checks listed projects exist).
- `Makefile deploy` copies `Optimum.Render.Vulkan.dll`, `Silk.NET.*.dll` and the
  shaderc native library into `VANILLA_DIR`/`INSTALL_DIR` alongside
  `Optimum.Api.Contracts.dll`.
- Packaging: the Vulkan loader is system-provided (`vulkan-1.dll` from the
  driver on Windows; `libvulkan.so.1` on Linux). The AppImage lists it as a
  runtime dependency; the installer's prerequisite scan reports its absence as
  "Vulkan backend unavailable, OpenGL will be used", never as a hard failure.
- CI: `ci-installer.yml`/`ci-scripts.yml` gain the shader-corpus and lavapipe
  jobs (section 10).

### Keeping up with upstream

Each Vintage Story release re-verifies transplant targets. The branch pattern
keeps every graphics-method transplant a two-line delta over vanilla, so a
rebase is a `check-patches.sh` run plus re-reading the vanilla body for new GL
calls. Appendix A's entry-point list is the checklist: a new `GL.*` symbol in the
decompile means a new device operation.

## 9. Compatibility policy

| Situation | Behaviour |
| --- | --- |
| `Renderer = opengl` | vanilla GL path; no Vulkan code loads |
| Mod references `OpenTK.Graphics.OpenGL*` or P/Invokes GL | session forced to `opengl`; log + one-time notice naming the mod |
| Mod uses only `IRenderAPI`/`IShaderAPI` and GLSL 330 | works on Vulkan; shader failures degrade per mod as today |
| Harmony mod patching platform internals | warning; runs on Vulkan; user can pin `opengl` |
| Vulkan < 1.3, missing required feature, no presentable queue | `opengl` with reason |
| Previous Vulkan session left a crash marker | one `opengl` session, marker cleared |
| macOS | `opengl` |
| Wayland/X11 | both via GLFW surfaces; Wayland tested explicitly (the code base already special-cases `IsWaylandSession`) |

The GL SSBO gate for Arc (`ClientSystemStartup.cs:1076`) and the 4.3-fallback
loop in `AttemptToOpenWindow` are GL-driver workarounds and are bypassed on
Vulkan; `UseSSBOs` on Vulkan is true whenever `multiDrawIndirect` is present.

## 10. Testing strategy

- **Unit (no GPU):** rewriter goldens; uniform layout offsets against
  hand-computed `scalar` rules; pipeline-key hashing and equality; allocator
  free-list behaviour; state-machine → pipeline/dynamic-state resolution;
  deferred deletion ordering; format map; the GL-shape parity of `Appendix A`
  (a test that greps the decompile for `GL.` symbols and fails on one the
  mapping table lacks).
- **Shader corpus:** every vanilla and Optimum shader through the full pipeline
  in CI; failure is a CI failure.
- **Headless device smoke in CI:** lavapipe (`VK_ICD_FILENAMES` → Mesa's
  `lvp_icd.json`) creates the device, compiles the corpus, renders a triangle
  and the GUI quad off-screen and checks pixels. No window needed.
- **Parity harness (local, per phase gate):** launch with `--rndWorld -p creativebuilding`
  on a fixed seed and position (reuse `scripts/trace-teleport.sh`'s mechanism),
  capture `GrabScreenshot` on both backends, compare with SSIM per settings
  permutation (SSAO 0/1/2, shadows 0/1/2, bloom, godrays, FXAA, render scale).
  Threshold starts loose (0.95) and tightens to 0.99 by Phase 3; known,
  explained differences (blend precision, mip selection) are listed.
- **Validation-clean:** debug runs with layers enabled must produce zero errors
  through a scripted session (menu → world → weather → water → night → exit).
- **Performance:** `scripts/benchmark-frametime.sh` and
  `scripts/benchmark-renderscale.sh` on both backends; the Phase 3 gate is
  Vulkan ≥ GL mean FPS and ≤ GL p99 frame time on each vendor.
- **Vendor matrix:** Intel Arc 140V (Windows), Intel iGPU (Mesa ANV), AMD
  (RADV + Windows), NVIDIA (Windows + Linux proprietary), lavapipe. Each is a
  row in the release checklist before `auto` includes it.
- **Patch-shape tests** in `Optimum.Tests`, following
  `fsr-pipeline-coverage-tests.cs`: the transplanted methods contain the branch,
  contain no `<>c` lambdas, and the cecil-owned list matches `Program.cs`.

### Definition of done per phase

Each phase below ends with: its tests green in CI, the parity harness at its
threshold, validation-clean, and a written entry in `docs/releases/` naming
what still falls back to GL.

## 11. Rollout plan

Estimates assume one experienced graphics engineer, full-time-ish, and are
ranges because the unknowns are driver behaviour and Cecil surprises, not
design.

### Phase 0 — spikes (2–3 weeks)

1. `ContextAPI.NoAPI` window + Vulkan surface through OpenTK's GLFW on CachyOS
   (X11 and Wayland) and the Claw; swapchain clear-to-colour at 120 Hz.
2. Cecil: transplant the `ClientProgram` constructor region (or IL-hook the
   `NativeWindowSettings` construction via `Optimum.Patcher/ILHook.cs`) and
   confirm transplanted bodies can call into the contracts holder. This is the
   one integration risk with no precedent for the size involved.
3. Rewriter prototype over the full corpus; report compile rate. Target: 84/84.
4. Allocator decision confirmed by a chunk-churn simulation.

Exit: a Vulkan window shows the main menu background colour; the shader corpus
compiles; the patch pipeline produces a launchable DLL with the branch in one
method.

### Phase 1 — device core and GUI (3–4 weeks)

Frames in flight, uniform ring, textures (Cairo/Skia uploads, mipmaps),
samplers, default framebuffer + present flip, pipeline/descriptor caches, the
GUI and `MinimalGui` programs, screenshots.

Exit: main menu and settings screens are pixel-identical to GL; the loading
screen works; resize and fullscreen toggles work.

### Phase 2 — world rendering (6–10 weeks)

Chunk VAO and SSBO paths with indirect multidraw, shadow maps, entities
(animation UBO/SSBO), particles (instanced streams), sky/night sky/celestial
objects with the occlusion query, clouds (FluffyClouds port), decals, block
highlights, held item, wireframe debug, dynamic meshes with persistent
mapping.

Exit: in-world parity ≥ 0.97 SSIM without post effects; no validation errors;
frame time within 20% of GL.

### Phase 3 — post-processing and parity (4–6 weeks)

OIT layers (layered attachments, per-attachment blend), transparent merge,
bloom, god rays, SSAO (4-MRT primary, `BlendEquation` on attachments 2/3),
luma/final composition with the attachment-subset rule, FXAA, Optimum's FSR1
at the blit, AVI recording, frame-buffer debug view.

Exit: parity ≥ 0.99 across the settings permutations; Vulkan ≥ GL on mean FPS
and ≤ GL on p99 per vendor; `Renderer = vulkan` ships opt-in.

### Phase 4 — hardening and `auto` (4–8 weeks)

Mod scan and crash-loop guard, caches, device-lost and low-memory handling,
monitor/DPI/present-mode changes, Wayland, AMD/NVIDIA rows of the matrix, the
per-vendor allow-list for `auto`, installer/packaging, documentation.

Exit: `auto` selects Vulkan on the green rows; GL remains default elsewhere.

### Phase 5 — the payoff (sized separately)

TAA resolve moves to the device's present seam; XeSS-SR, DLSS-SR and FSR 2 run
natively on the Vulkan device with the velocity buffer and jitter from the
parallel TAA work; the D3D12 presentation proxy for XeSS-FG/XeLL imports the
Vulkan-exported final image and depth/motion vectors through
`VK_KHR_external_memory_win32`, which is the direction of interop DXVK-NVAPI
and vkd3d already exercise daily.

### In parallel, on OpenGL, from now

Jitter in `ClientMain.Set3DProjection` (`ClientMain.cs:1419`, the single
projection choke point), a velocity MRT on the Primary FBO, previous-frame
matrices, and a TAA resolve at `BlitPrimaryToDefault`. None of it is
API-specific; it lands on GL first and ports to the device as a shader set.

Total to an opt-in, parity-level Vulkan renderer: roughly **5–8 months**.

## 12. Risks and open questions

### What running the client taught, and how to debug it

A Vulkan frame can be entirely legal and entirely wrong. All three bugs that stood
between "the backend initialises" and "the interface renders" produced **zero
validation messages**: a render target smaller than the viewport, undefined vertex
attributes, and a `#version` floor applied a step earlier than expected. The
validation layer answers "is this API usage legal", never "is this the frame the
GL path would have produced".

Two things follow.

First, **parity is claimed by running the client, not by auditing call sites.** The
audit that produced "187/187, Phase 1 complete" swept for `GL.` and missed every
property accessor plus everything a static reading cannot prove is reached. Thirty
more entry points surfaced the moment a real frame ran.

Second, the backend carries its own trace, because the validation layer cannot
answer the questions that matter here. Setting `OPTIMUM_RENDER_TRACE` to a file
path turns on `Optimum.Render.Vulkan.Core.RenderTrace`, which records per draw:
the mesh and program, the resolved index count, the bound texture, the target,
depth/blend/cull/scissor/viewport state, whether the uniform ring allocation
succeeded, and whether constant defaults were merged into the vertex layout. It
also writes each program's uniform block layout with byte offsets, a checksum of
every texture upload, the reason any draw was skipped, and each stage's rewritten
GLSL beside the trace file. It is off unless the variable is set, so it costs one
static null check in a release build.

The single most useful line proved to be the one comparing the default framebuffer
against the swapchain extent - `default framebuffer id=1 1280x850
swapchain=2561x1601` located in seconds what hours of reasoning about blending and
depth had not.

| Risk | Likelihood | Mitigation |
| --- | --- | --- |
| Intel Windows Vulkan driver quirks on Arc (descriptor limits, dynamic rendering corner cases) | medium | validation-clean gate; Arc is a first-class matrix row from Phase 1; `auto` allow-list |
| Cecil cannot transplant the `ClientProgram` constructor cleanly | medium | Phase 0 spike; fallback is an IL hook at the window-settings site |
| Third-party GL-bound mods (Volumetric Shading-class mods) | certain | detected before the window opens; forced GL; clear notice |
| Harmony mods touching platform internals | medium | advisory scan; `opengl` pin; document the branch pattern for mod authors |
| Persistent-mapped dynamic meshes tear worse than GL | low–medium | per-mesh double buffer option |
| Pipeline/descriptor churn regresses frame time | low | caches, dynamic state, indirect multidraw; benchmark gate |
| Memory fragmentation from chunk churn in the block pool | medium | coalescing free list; defragmentation budget; telemetry in the stutter watch |
| Wayland surface/present edge cases | medium | explicit Wayland row in the matrix |
| Upstream release moves GL code | certain, per release | two-line branch deltas; Appendix A checklist |
| Native dependency size (shaderc ~10 MB per platform) | low | acceptable; matches existing SkiaSharp footprint |

Open questions for the maintainer:

1. Vulkan 1.3 floor — confirm nothing you care about sits below it.
2. Abstraction in API contracts (mod-visible, one Cecil path) versus a private
   assembly (cleaner, unexercised Cecil path). This plan chooses contracts.
3. Hand-maintained Vulkan-GLSL copies of the 84 shaders instead of the rewriter?
   The rewriter is recommended because it is the only way mod GLSL works, but a
   hybrid (rewriter by default, hand-written override per shader) is cheap to
   allow.
4. Whether Phase 5's D3D12 proxy should reuse Community Shaders' `DX12SwapChain`
   design directly, given you wrote it.

## 13. Considered alternatives

- **GL sidecar + interop (no backend).** Probe-proven on both platforms. Gets
  XeSS-SR/DLSS-SR/FSR 2 with a fraction of the work by sharing the Primary
  textures out at `BlitPrimaryToDefault`. Cannot own presentation, so no frame
  generation, and stays on Intel's GL driver. Remains the right fallback for
  hardware that never reaches Vulkan 1.3 and is not precluded by this plan.
- **Zink.** Mesa's GL-on-Vulkan would move the game off Intel's GL driver on
  Linux with an environment variable, and is a useful sanity check for the
  parity harness. It gives no control over the device or swapchain and does not
  exist as a shipping path on Windows.
- **D3D12 backend.** Native XeSS-FG, but Linux only through VKD3D-Proton, which
  undoes the native Linux client. The `IOptimumGraphicsDevice` boundary is
  API-neutral; if a D3D12 device is ever wanted, it slots in beside the Vulkan
  one without touching the game.

## 14. Files and surfaces touched

### New

- `optimum-api-contracts/optimum-render-device.cs` — `IOptimumGraphicsDevice`,
  `OptimumRender`, handle and enum types.
- `Optimum.Render.Vulkan/` — `VulkanDevice.cs`, `VulkanContext.cs` (instance,
  device, queues), `Swapchain.cs`, `FrameSlots.cs`, `Allocator.cs`,
  `Resources/{Textures,Buffers,Meshes,Framebuffers,Samplers,Queries}.cs`,
  `State/{StateTracker,PipelineKey,PipelineCache,DescriptorCache}.cs`,
  `Shaders/{Preprocessor,Rewriter,UniformLayout,Compiler,SpirvCache}.cs`,
  `Present.cs`, `Readback.cs`, `Diagnostics.cs`.
- `Optimum.Render.Vulkan.Tests/`.
- `sources/shaders/optimum-present.{vsh,fsh}` — the flip-blit.
- Documentation: this plan; a per-phase entry in `docs/releases/`.

### Modified

- `patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch`
  (the bulk), `ClientProgram.cs.patch`, `GameWindowNative.cs.patch` (new),
  `ShaderProgramBase.cs.patch` (new), `ShaderProgram.cs.patch` (new),
  `Shader.cs.patch` (new), `ShaderRegistry.cs.patch`, `VAO.cs.patch` (new),
  `UBO.cs.patch` (new), `SystemRenderOITLayers.cs.patch`, `ChunkRenderer.cs.patch`,
  `SystemRenderSunMoon.cs.patch` (new), `SystemRenderFrameBufferDebug.cs.patch`
  (new), `SvgLoader.cs.patch`, `ScreenManager.cs.patch` (new), `ClientMain.cs.patch`,
  `InventoryItemRenderer.cs.patch` (new), `ClientSystemStartup.cs.patch`,
  `Vintagestory.ClientNative/Screenshot.cs.patch` (new).
- `patches/runtime/VSEssentials/…/CloudRendererMap.cs.patch`,
  `CloudRendererVolumetric.cs.patch` (new); `patches/runtime/VSSurvivalMod/…/EntityBehaviorHideWaterSurface.cs.patch` (new).
- `Optimum.Patcher/Program.cs`, `patches/cecil-owned.list`.
- `Optimum.Launcher/ShaderCompatibilityScanner.cs`, `Program.cs` (backend
  resolution, crash marker), `AssemblyLoader.cs` (no change expected; verify).
- `sources/VintagestoryApi/Config/OptimumConfig.cs`, `GuiCompositeSettings.cs.patch`.
- `Makefile`, `scripts/package-*.sh|ps1`, installer prerequisite list,
  `VintageStory.slnx`, CI workflows, `README.md` feature list.

### Kept as-is

Every render system not named in section 7, all vanilla GLSL, the FSR1 shaders,
the frame-pacing and background-FPS logic, the whole server side.

---

## Appendix A — GL entry points → device operations

All 102 distinct `GL.*` symbols in the client, from the decompile
(`grep -rhoE '\bGL\.[A-Za-z0-9_]+' _ref`). Counts are call sites.

| Device group | Entry points (count) |
| --- | --- |
| Texture state | `TexParameter` (107), `BindTexture` (40), `GenTexture` (29), `TexImage2D` (25), `DeleteTexture` (11), `BindSampler` (11), `TexSubImage2D` (7), `ActiveTexture` (7), `TexImage3D` (1), `GetTexParameter` (1), `GenerateMipmap` (2), `GenSampler` (1), `DeleteSampler` (1), `SamplerParameter` (2) |
| Buffers / meshes | `BindBuffer` (43), `GenBuffer` (16), `BufferData` (16), `VertexAttribPointer` (16), `BindVertexArray` (14), `VertexAttribDivisor` (13), `DeleteBuffer` (11), `BufferSubData` (9), `VertexAttribIPointer` (8), `BindBufferBase` (4), `GenVertexArray` (3), `EnableVertexAttribArray` (3), `BufferStorage` (3), `MapBufferRange` (2), `DeleteVertexArray` (1), `DeleteBuffers` (1) |
| Draws | `MultiDrawElements` (2), `DrawElementsInstanced` (1), `DrawElements` (1), `DrawArrays` (1) |
| Framebuffers | `FramebufferTexture2D` (19), `DrawBuffer` (18), `ClearBuffer` (18), `GenFramebuffer` (16), `DrawBuffers` (13), `BindFramebuffer` (8), `FramebufferTextureLayer` (3), `DeleteFramebuffer` (3), `ReadBuffer` (3), `ReadBuffers` (1), `Clear` (3), `ClearColor` (2), `ReadPixels` (1) |
| Fixed-function state | `Viewport` (18), `Enable` (15), `Disable` (14), `BlendFunc` (14), `DepthFunc` (6), `BlendEquation` (5), `ColorMask` (3), `DepthRange` (3, no-op), `BlendFuncSeparate` (3), `LineWidth` (2), `CullFace` (2), `Hint` (2, ignored), `Scissor` (1), `PolygonMode` (1), `DepthMask` (1), `StencilOp` (1), `StencilMask` (1), `StencilFunc` (1), `IsEnabled` (1) |
| Shaders / uniforms | `Uniform3` (6), `Uniform1` (6), `Uniform2` (4), `GetUniformLocation` (4), `UniformMatrix4` (3), `Uniform4` (3), `DetachShader` (3), `DeleteShader` (3), `AttachShader` (3), `UseProgram` (2), `UniformMatrix4x3` (1), `UniformBlockBinding` (1), `ShaderSource` (1), `LinkProgram` (1), `GetUniformBlockIndex` (1), `GetShaderInfoLog` (1), `GetShader` (1), `GetProgramInfoLog` (1), `GetProgram` (1), `DeleteProgram` (1), `CreateShader` (1), `CreateProgram` (1), `CompileShader` (1), `BindAttribLocation` (1) |
| Queries | `GetQueryObject` (2), `GenQueries` (1), `EndQuery` (1), `DeleteQuery` (1), `BeginQuery` (1) |
| Capabilities / debug | `GetString` (12), `GetInteger` (7), `GetError` (4), `GetFloat` (2), `MaxVertexUniformComponents` (1), `MaxUniformBlockSize` (1), `DebugMessageCallback` (1) |

Notable absences that simplify the device: no `FrontFace`, no `PolygonOffset`,
no `ClipControl`, no multisample state, no `TexStorage`, no compute, no
transform feedback, no `PrimitiveRestart`.

## Appendix B — vanilla framebuffer inventory

From `SetupDefaultFrameBuffers` (`ClientPlatformWindows.cs:1155-1561`);
`num × num2` is the window size × `ssaaLevel`.

| Slot | Enum | Size | Attachments |
| --- | --- | --- | --- |
| 0 | Primary | full | D32F depth; RGBA8 colour; RGBA8 glow; + RGBA16F position, RGBA16F normal when SSAO |
| 1 | Transparent | full | RGBA16F accumulation; R16F revealage; RGBA8 glow (Optimum OIT adds a 3-layer RGBA16F array + RGB8 reveal) |
| 2, 3 | BlurHorizontal/VerticalMedRes | ½ | RGBA8 |
| 4 | FindBright | full | RGBA16F |
| 5 | LiquidDepth | ¼ | D32F |
| 7 | GodRays | ½ | RGBA16F |
| 8, 9 | BlurVertical/HorizontalLowRes | ¼ | RGBA8 |
| 10 | Luma | full | RGBA16F |
| 11, 12 | ShadowmapFar/Near | by shadow quality | D32F |
| 13–17 | SSAO and blurs | full or ½ | RGB / RGBA32F / RGBA8 |
| 18 | Optimum FSR intermediate | native window | RGBA8 |

## Appendix C — vertex attribute contracts

Explicit `layout(location)` declarations in the vanilla vertex shaders; the
device's vertex-layout signatures are derived from the `MeshData` parts that
feed each one.

| Program | Locations |
| --- | --- |
| chunkopaque / chunktransparent / chunkshadowmap | 0 xyz vec3 · 1 uv vec2 · 2 rgbaLight vec4 (u8 norm) · 3 renderFlags int · 4 colormapData int |
| chunktopsoil | … · 4 uv2 vec2 · 5 colormapData int |
| chunkliquid | 0 xyz · 1 uv · 2 rgbaLight · 3 renderFlags · 4 flowVector vec2 · 5 colormapData · 6 waterFlags int |
| entityanimated / gui | 0 pos vec3 · 1 uv · 2 color vec4 · 3 flags int · 4 damageEffect float · 5 jointId int |
| standard | 0 pos · 1 uv · 2 color · 3 flags · 4 glowSub float |
| helditem | 0 pos · 1 uv · 2 modelColor · 3 flags |
| particlesquad | 0 vertexPosition · 1 uv · 2 baseColor · 3 renderFlags · 4 particlePosition (inst) · 5 scale (inst) · 6 particleDir (inst) · 7 rgbaLight (inst) · 8 rgbaBlock (inst) |
| particlescube | 0 pos · 1 normal vec4 (int-2-10-10-10 norm) · 2 uv · 3 renderFlags · 4–8 instanced as above |
| instanced | 0 pos · 1 uv · 2 rgbaBlock · 3 renderFlags · 4 rgbaLight · 5–8 transform mat4 (inst) |
| decals | 0 pos · 1 decalUv · 2 rgbaLight · 3 renderFlags · 4 blockUv · 5 decalUvSize · 6 decalUvStart |
| lines | 0 quadCoord · 1 uv · 2 pointA · 3 pointB |
| sky | 0 pos · 1 color |
| clouds | 0 pos · 1 rgbaBase · 2 flags · 3 cloudTileOffset · 4 neibCloudThickness vec4 · 5–9 floats |
| fullscreen passes (blit, final, blur, fsr-*, ssao, godrays, luma, findbright, transparentcompose) | none; `gl_VertexID` triangle |
