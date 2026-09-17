# Vulkan-native mod support

This page is for mod authors. It covers what runs on Optimum's Vulkan renderer without changes, what
makes the launcher start a session on OpenGL instead, how shaders are handled, and the opt-in API
for declaring your own frame-graph passes and motion writers. A mod that ignores the API keeps
working. The API is for a mod that wants its drawing scheduled correctly on Vulkan and its geometry
stable under TAA.

The fixture mod in `Optimum.Render.Vulkan.Tests/Fixtures/ModPassFixture/` follows this page step by
step. `ModPassHostingTests` runs it on a real device.

## 1. What works unchanged

Everything you reach through the game API lands on the platform's graphics members. On Vulkan those
members are backed by the native renderer. None of this needs code changes:

- **Render API:** `ICoreClientAPI.Render` (`IRenderAPI`): meshes (`UploadMesh`, `RenderMesh`,
  multi-texture meshes), textures, render-to-texture (`FrameBufferRef`, `LoadFrameBuffer`), fixed
  function state (`GlToggleBlend`, `GlEnableDepthTest`, scissor, cull), the GUI and 2D helpers, and
  screenshots.
- **Shaders:** `ICoreClientAPI.Shader` (`IShaderAPI`): `NewShaderProgram`, `RegisterFileShaderProgram`
  and uniforms, compiled from GLSL 330 through the rewriter (section 3).
- **Renderers:** anything registered with `Event.RegisterRenderer(renderer, stage)`. Every stage is
  bracketed by the platform (`BeginRenderStage`/`EndRenderStage`), and inside a mod-hosted stage the
  frame graph lets your renderer sample any render target and rebind targets freely. Every stage
  other than `Before`, the shadow stages, `Opaque` and `OIT` uses `OpenSampling` and `AllowSplit`.
- **TAA fallback:** geometry drawn without a motion writer is still temporally resolved. The resolve
  sees no valid motion vector for your pixels and reprojects them with the camera. That is exact for
  static geometry and wrong but bounded for geometry that moves on its own. Section 5 removes the
  ghosting.

## 2. What routes to OpenGL

The launcher scans installed mods before the session starts (scanner v2, schema 2; the result is
stored in `<data>/.optimum/shader-compatibility.json`). A mod that does any of the following makes
the whole session start on OpenGL. The log line and a one-time notice name the mod:

| Mod does | Why |
| --- | --- |
| References `OpenTK.Graphics.OpenGL*` or P/Invokes GL directly | There is no GL context on the Vulkan path, so every direct GL call would fail. |
| Harmony-patches `ClientPlatformWindows` or `ShaderProgramBase` (platform internals) | `VulkanClientPlatform` overrides those members and links programs itself, so a patched GL body never runs. |

A failed scan never vetoes Vulkan. It sends every program through the rewriter instead, because it
cannot tell which programs a mod replaced.

To stay on Vulkan, move direct GL calls to the render API. If you need something the API does not
offer, declare a pass (section 4) and draw through the API inside it.

## 3. Shaders: native programs, overrides and the rewriter

Vanilla and Optimum programs ship as precompiled SPIR-V ("native"). Mod shaders take the rewriter,
which compiles GLSL 330 to Vulkan GLSL against the same shared pipeline layout:

| Mod ships | Behaviour on Vulkan |
| --- | --- |
| A shader under a new program name | Built through the rewriter. |
| `assets/<domain>/shaders/<program>.vsh\|.fsh` for a vanilla or Optimum program | That program alone is built from your GLSL through the rewriter. The native blob is bypassed for it only. |
| Any `assets/<domain>/shaderincludes/*` file | Every program takes the rewriter, because every program compiles against the merged include dictionary. |

What the rewriter needs from GLSL 330:

- **Samplers:**
  - A sampler named like a frame texture (the depth and shadow maps) reads that texture directly.
  - Every other sampler becomes an index into the bindless tables.
  - At most 32 sampler slots per program. Sampler arrays are not supported.
- **Loose uniforms** become members of a per-program record. A uniform initializer
  (`uniform float x = 1;`) is honoured.
- **Named uniform blocks** become std140 storage buffers: `Animation` and `AnimationPrev`, plus up to
  four others. A fifth block fails the link.
- **Failure:** a program that fails to translate degrades per mod, the same as a failed GLSL compile
  on OpenGL.

`OPTIMUM_VK_NATIVE_SHADERS=0` forces every program through the rewriter, for comparing the two paths.

## 4. Declaring a pass

A declared pass is a unit of drawing the frame graph schedules at a fixed slot. The platform:

1. binds the declared target;
2. declares the pass with the attachments you write and the textures you read, so every barrier is
   known when the pass opens;
3. opens the motion window if the pass is a motion writer;
4. calls your draw;
5. ends the pass and restores the target and pass that were active before.

Passes run at the end of their slot's stage, after that stage's `RegisterRenderer` renderers, in
registration order.

### Slots: `EnumOptimumPass`

The values equal `EnumRenderStage`: `Before`, `Opaque`, `OIT`, `AfterOIT`, `AfterPostProcessing`,
`AfterBlit`, `Ortho`, `AfterFinalComposition`, `Done`. The shadow stages are refused, because their
targets are not mod handles.

### Attachments: `EnumOptimumAttachment`

| Handle | Target | Use |
| --- | --- | --- |
| `PrimaryColor`, `PrimaryGlow` | Primary colour 0, 1 | read or write |
| `PrimaryGBufferPosition`, `PrimaryGBufferNormal` | Primary colour 2, 3 (SSAO on) | read or write |
| `PrimaryMotion` | Primary motion attachment (TAA on) | read only; writing it takes a motion writer |
| `PrimaryDepth` | Primary depth (shared with Transparent) | read or write |
| `TransparentAccumulation`, `TransparentRevealage`, `TransparentGlow` | Transparent colour 0, 1, 2 | read or write |
| `LiquidDepth`, `ShadowFarDepth`, `ShadowNearDepth`, `GodRays`, `BloomLowRes`, `Luma`, `SsaoBlurred` | none | read only |
| `DefaultColor` | the window | write only |

A read whose texture does not exist this session (SSAO or TAA off) is dropped. A pass that writes
one is skipped, and the skip is logged once.

### Rules, checked at registration (`OptimumPassContract.Validate`)

- The pass needs a `Name` (unique within your mod; registering it again replaces it) and a `Draw`
  callback.
- **Writes:**
  - All writes belong to one target: Primary, Transparent or the window.
  - `PrimaryDepth` may accompany Primary or Transparent writes.
  - The window is written only in `AfterBlit`, `Ortho` and `Done`, and Primary and Transparent only
    before them.
  - Read-only handles cannot be written.
  - `PrimaryMotion` is never a declared write. Declare a `MotionWriter` instead.
- **Reads:** a pass cannot read what it writes, because that would be a feedback loop. A colour
  attachment of your target that you do not write leaves the rendering scope, so you may sample it.
- **Motion writers:** only in `Opaque` and `AfterOIT`, on Primary.

### Example

```csharp
public class MyModSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        var pass = new OptimumPassDecl
        {
            Name = "glow-tint",
            Slot = EnumOptimumPass.AfterOIT,
            Reads = new[] { EnumOptimumAttachment.PrimaryGlow },
            Writes = new[] { EnumOptimumAttachment.PrimaryColor, EnumOptimumAttachment.PrimaryDepth },
            Draw = decl => DrawTint(capi),                      // draw through capi.Render
            MotionWriter = new OptimumMotionWriterDecl { Name = "glow-tint" },
        };
        if (!capi.RegisterOptimumPass(this, pass, out string reason))
            capi.Logger.Warning("glow-tint not registered: " + reason);
    }

    public override void Dispose()
    {
        OptimumModPasses.UnregisterMod(OptimumModRenderExtensions.OptimumModId(this));
    }
}
```

### Lifecycle

- **Registration:** register from the main thread, normally in `StartClientSide`. Registration copies
  the declaration, so edit and register again to change it.
- **Storage:** registrations are stored per mod, under the mod id (or the assembly name when there is
  none).
- **Removal:** everything a mod registered is removed when the client leaves the world (`LeaveWorld`,
  which is when client mods unload). `OptimumModPasses.UnregisterMod` and `Unregister` remove
  registrations earlier.
- **Draw exceptions:** an exception thrown by your draw is logged once and the frame continues.

## 5. Motion writers

A motion writer tells the renderer that your draws write motion vectors, so TAA can keep their
history instead of ghosting or smearing them. The rules are those of the frozen temporal frame
contract (`docs/temporal-frame-contract.md`, section 3.2). Your shader writes one `vec4` into the
motion attachment:

| Channel | Meaning |
| --- | --- |
| `rg` | `previousPixel - currentPixel`, in render-resolution pixels, both positions from **unjittered** projections, not normalised |
| `b` | reactive value in [0,1]: 0 for opaque, higher lowers the history weight (transparent or animated surfaces) |
| `a` | the window depth your draw puts in the depth buffer, **including any depth offset** (`gl_FragCoord.z` in the plain case) |

Two further rules apply:

- **No previous position.** A draw with no previous position (it just spawned, or its previous clip
  `w` is at or below 1e-6) still writes `b` and writes zero into `rg` and `a`.
- **Validity.** The resolve trusts a pixel only when `a` matches the depth buffer within a half-float
  tolerance. A stale or offset-less `a` falls back to camera reprojection.

Both defines are stamped into every program, yours included:

```glsl
#if TAAMOTION > 0
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;
#endif
...
#if TAAMOTION > 0
    vec2 renderSize = ...;            // the render resolution
    vec2 cur  = (currentClip.xy / currentClip.w * 0.5 + 0.5) * renderSize;   // unjittered
    if (prevClip.w <= 1e-6) outMotion = vec4(0.0, 0.0, reactive, 0.0);
    else outMotion = vec4((prevClip.xy / prevClip.w * 0.5 + 0.5) * renderSize - cur, reactive, gl_FragCoord.z);
#endif
```

The window is replace-blended and exists only inside the temporal window: `Opaque` and `AfterOIT`,
with Primary bound and TAA on. Anywhere else the begin call refuses and your pixels take the camera
fallback. That is also the right answer for the post-composition overlays.

There are two ways to open the window:

- **On a declared pass:** set `OptimumPassDecl.MotionWriter`. The platform opens the window around
  `Draw`. `Mode = WithColor` keeps Primary's colour set and adds the motion attachment.
  `Mode = MotionOnly` writes only the motion attachment, for a velocity pass over geometry that is
  already shaded.
- **In a `RegisterRenderer` renderer:** register the writer once, then bracket the draws:

  ```csharp
  writer = new OptimumMotionWriterDecl { Name = "my-renderer" };
  capi.RegisterOptimumMotionWriter(this, writer, out _);
  ...
  bool motion = OptimumModPasses.BeginMotionWriter(writer);   // false: not open, do not End
  try { /* draws */ }
  finally { if (motion) OptimumModPasses.EndMotionWriter(); }
  ```

## 6. OpenGL

On OpenGL the whole API is inert:

- registration validates and stores, but nothing reads the registry;
- declared passes never run;
- `BeginMotionWriter` returns false.

A mod written against this page therefore needs no backend check. If your effect must also exist on
OpenGL, draw it from a `RegisterRenderer` renderer there. `OptimumRender.IsVulkan` tells you which
backend started.

## 7. Diagnostics

- **Render trace.** `OPTIMUM_RENDER_TRACE=<file>` writes one `pass` line per opened pass. Your passes
  are named `Mod/<modid>/<name>/<target>`. A `pass split` line naming one of them means your draw
  rebound its target mid-pass. That is allowed, but it costs a second rendering scope.
- **Validation.** `OPTIMUM_VULKAN_VALIDATION=1` with `OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best`
  reports undefined reads and synchronisation hazards. A hazard inside your pass is a real finding.
- **Logs.** `[Optimum] mod pass '<name>' of <mod> skipped: ...` names a write whose texture does not
  exist this session. `... threw: ...` is an exception from your draw.
