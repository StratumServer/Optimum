using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan.md), Phase 3b decision 5
// stage 2: the GUI and text systems whose fixed state is stated at their call site.
//
// Two of the systems in that group draw through the native device API here. The rest of the
// group - Render2DTexture's gui quads, guigear, the block highlights, the wireframe cube and
// the camera path - take the generic stated route (NativeStated.cs), and the reason is written down in
// docs/vulkan.md: their blend and depth state is not the
// caller's, it is whatever the frame left on the tracker, and the same Render2DTexture call is
// reached both with standard alpha and with premultiplied alpha (RenderAPIGame's
// Render2DTexturePremultipliedAlpha brackets it with GlToggleBlend). Decision 3 forbids a
// native pass from reading that back off tracked GL state, so those systems move once their
// blend mode is stated at the seam, which is a change across the GUI element tree and its own
// piece of work.
//
// What these two draw:
//   - RenderTextureQuad: the unit quad ClientMain.RenderTextureIntoFrameBuffer stretches one
//     texture's rectangle over another's - how every Cairo-drawn GUI and text surface is baked
//     into a texture. It is the highest-frequency GUI draw there is, and the one the stage
//     brief flags for descriptor churn: a native draw resolves its texture into the device's
//     per-frame bindless arena (VulkanDevice.BeginNativeDraw -> BindlessTextureTable.Resolve),
//     so a fresh Cairo texture costs one slot in this frame's arena and no permanent
//     descriptor, which is exactly what the emulated unit route could not promise.
//   - RenderOverlayLines: SystemRenderPlayerAimAcc's aiming reticle, five line-topology draws
//     through the gui program with noTexture set, at two line widths.
// Where the other side is: ClientPlatformAbstract.RenderTextureQuad and
// ClientPlatformAbstract.RenderOverlayLines, whose neutral bodies are the RenderMesh calls
// these seams replaced and which the OpenGL path still runs (ClientPlatformWindows.RenderMesh
// -> GL.DrawElements). NativeGuiEnabled false takes that route on the Vulkan device too, which
// is what the differential tests compare against.
// Target and slots: CurrentFrameBuffer, which for the texture blit is the framebuffer the
// caller just bound over the destination texture and for the reticle is the default
// framebuffer the Ortho stage draws into. Every bound colour slot is in the pass, so the scope
// is the one the emulated draw opens; both fragment shaders write outColor at 0 only and the
// pipeline masks every other slot off (rule 9). Neither writes depth, and neither writes the
// motion attachment - the Ortho stage runs after the TAA resolve, so there is no motion slot
// to mask in the first place.
// State that is not obvious:
//   - no depth test and no depth write: RenderTextureIntoFrameBuffer calls GlDisableDepthTest
//     itself, and the reticle is a 2D overlay whose ortho projection puts it in front;
//   - blend: the value the caller computed, through the same factor table the tracker uses
//     (AttachmentBlend.For), never read back off the tracker;
//   - no culling: both meshes are screen-facing quads and lines, which GL rasterizes whatever
//     the cull state, and lines are not culled at all;
//   - the topology is the mesh's own draw mode (VulkanDevice.NativeMeshTopology), because that
//     is where the tesselator put it - EnumDrawMode.Lines for the reticle - not a state toggle;
//   - the line width is the caller's, and it is the one piece of dynamic state a fullscreen
//     pass never needed; it is in the native pipeline key, so the 0.5 and the 1.0 draws of one
//     frame do not share a pipeline.
// What pins it: NativeGuiTests (old route against native route, both systems, both line
// widths, blend on and off) and Optimum.Tests/native-world-systems-coverage-tests.cs (the lib
// seams).
public partial class VulkanClientPlatform
{
    /// <summary>
    /// False runs the seams' neutral bodies - the OpenGL bodies' RenderMesh - on the Vulkan
    /// device instead of the native passes: the old route the differential tests compare
    /// against, in the pattern of <see cref="NativeSkyEnabled" />.
    /// </summary>
    internal bool NativeGuiEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_GUI") != "0";

    /// <summary>
    /// The texture-into-texture blit's pipeline and placements. Nothing is written per draw:
    /// the source rectangle, the destination rectangle and the alpha test are all in the
    /// program's own shadow by the time the seam is reached, put there by the setters
    /// RenderTextureIntoFrameBuffer calls.
    /// </summary>
    private readonly NativeMeshPass nativeTextureQuad =
        new("texture2texture", Array.Empty<string>(), new[] { "tex2d" });

    /// <summary>
    /// The 2D line overlay's pipeline and placements, on the gui program. "tex2dOverlay" is
    /// resolved as well as "tex2d" so neither sampler slot keeps a stale bindless index out of
    /// the push shadow; the reticle sets noTexture, so gui.fsh samples neither.
    /// </summary>
    private readonly NativeMeshPass nativeOverlayLines =
        new("gui", Array.Empty<string>(), new[] { "tex2d", "tex2dOverlay" });

    /// <summary>
    /// The GUI quads' pipeline and placements, on the gui program - a pass of its own so the
    /// reticle's line pipeline and these triangle pipelines do not evict each other.
    /// </summary>
    private readonly NativeMeshPass nativeGuiQuad =
        new("gui", Array.Empty<string>(), new[] { "tex2d", "tex2dOverlay" });

    /// <summary>
    /// One Render2DTexture quad: the native pass, or the neutral body's RenderMesh.
    /// What it draws: a GUI element's texture. The other side: ClientPlatformAbstract.RenderGuiQuad.
    /// Target and slots: the caller's target, slot 0. State: the blend mode, depth test, depth
    /// mask, depth function and scissor the caller last stated through this platform's virtuals
    /// (VulkanClientPlatform.State.cs), so premultiplied-alpha blits and scrolled, clipped lists
    /// draw as they do on GL. Only the vanilla gui program is taken.
    /// </summary>
    public override void RenderGuiQuad(MeshRef quad, int textureId)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (!NativeGuiEnabled || device == null || quad == null || program == null ||
            !ReferenceEquals(program, ShaderPrograms.Gui) ||
            !DrawNativeGuiMesh(nativeGuiQuad, quad, textureId,
                DeclaredProgramTexture(program.ProgramId, "tex2dOverlay"), 1.0f,
                statedBlendOn, statedBlendMode, statedDepthTest, statedDepthWrite,
                GlEnums.CompareOpFrom(statedDepthFunc), scissorEnabled ? statedScissor : null, "GuiQuad"))
        {
            base.RenderGuiQuad(quad!, textureId);
        }
    }

    /// <summary>
    /// Any other draw under the vanilla gui program - block highlights, the wireframe, gear and
    /// progress overlays, mods drawing with the gui shader through IRenderAPI.RenderMesh - through
    /// its own pass cache, so line and triangle pipelines of different callers do not evict the
    /// quads'.
    /// </summary>
    private readonly NativeMeshPass nativeParticles2d =
        new("particlesquad2d", Array.Empty<string>(), new[] { "particleTex" });

    /// <summary>
    /// The main menu's 2D particle pool (ParticleRenderer2D.Render -> RenderMeshInstanced under the
    /// vanilla particlesquad2d program) as a native instanced pass on whatever target is bound -
    /// the default framebuffer in the menu. The OpenGL side is ClientPlatformWindows.RenderMeshInstanced.
    /// State is what the client stated: blend on in the non-OIT mode (GlToggleBlend), depth test and
    /// mask as left by the menu. particleTex resolves from the program's declared texture.
    /// </summary>
    private bool TryRenderParticles2dNative(MeshRef mesh, int quantity)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (!NativeGuiEnabled || device == null || mesh == null || program == null || quantity <= 0 ||
            !ReferenceEquals(program, ShaderPrograms.Particlesquad2d))
        {
            return false;
        }

        return DrawNativeGuiMesh(nativeParticles2d, mesh,
            DeclaredProgramTexture(program.ProgramId, "particleTex"), 0,
            statedLineWidth, statedBlendOn, statedBlendMode, statedDepthTest, statedDepthWrite,
            GlEnums.CompareOpFrom(statedDepthFunc), scissorEnabled ? statedScissor : null, "Particles2d",
            CullModeFlags.None, quantity);
    }

    private readonly NativeMeshPass nativeMinimalGui =
        new("", Array.Empty<string>(), new[] { "tex2d" });

    private readonly NativeMeshPass nativeGuiGear =
        new("guigear", Array.Empty<string>(), new[] { "tex2d" });

    /// <summary>
    /// Single-sampler GUI quads drawn through plain RenderMesh. The temporal stability gear:
    /// HudHotbar draws capi.Gui.QuadMeshRef under the vanilla guigear program in the Ortho stage.
    /// The early loading screen's quads: MainMenuRenderAPI.Render2DTexture draws through the
    /// platform's hardcoded ShaderProgramMinimalGui (no pass name, no asset) until the shader
    /// registry is up, and that RenderMesh lands here. The OpenGL side is
    /// ClientPlatformWindows.RenderMesh. One sampler (tex2d), the state the client stated.
    /// </summary>
    private bool TryRenderMinimalGuiNative(MeshRef mesh)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (!NativeGuiEnabled || device == null || mesh == null || program == null) return false;

        NativeMeshPass pass;
        string label;
        if (ReferenceEquals(program, MinimalGuiShader))
        {
            pass = nativeMinimalGui;
            label = "MinimalGui";
        }
        else if (ReferenceEquals(program, ShaderPrograms.Guigear))
        {
            pass = nativeGuiGear;
            label = "GuiGear";
        }
        else
        {
            return false;
        }

        return DrawNativeGuiMesh(pass, mesh,
            DeclaredProgramTexture(program.ProgramId, "tex2d"), 0,
            statedLineWidth, statedBlendOn, statedBlendMode, statedDepthTest, statedDepthWrite,
            GlEnums.CompareOpFrom(statedDepthFunc), scissorEnabled ? statedScissor : null, label);
    }

    private readonly NativeMeshPass nativeGuiMesh =
        new("gui", Array.Empty<string>(), new[] { "tex2d", "tex2dOverlay" });

    /// <summary>
    /// A plain RenderMesh under the vanilla gui program, recorded natively under the state the
    /// client stated: blend, depth, depth function, scissor, cull, line width. The sampled
    /// textures are the program's declared ones. False: the caller runs the generic stated draw.
    /// Called from VulkanClientPlatform.RenderMesh; the seams' neutral bodies reach it too, which
    /// is why it honours <see cref="NativeGuiEnabled" /> itself.
    /// </summary>
    private bool TryRenderGuiMeshNative(MeshRef mesh)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (!NativeGuiEnabled || device == null || mesh == null || program == null ||
            !ReferenceEquals(program, ShaderPrograms.Gui))
        {
            return false;
        }
        CullModeFlags cull = statedCull
            ? (statedCullBack ? CullModeFlags.BackBit : CullModeFlags.FrontBit)
            : CullModeFlags.None;
        return DrawNativeGuiMesh(nativeGuiMesh, mesh,
            DeclaredProgramTexture(program.ProgramId, "tex2d"),
            DeclaredProgramTexture(program.ProgramId, "tex2dOverlay"),
            statedLineWidth, statedBlendOn, statedBlendMode, statedDepthTest, statedDepthWrite,
            GlEnums.CompareOpFrom(statedDepthFunc), scissorEnabled ? statedScissor : null, "GuiMesh", cull);
    }

    /// <summary>The texture-into-texture blit's draw: the native pass, or the neutral body's RenderMesh.</summary>
    public override void RenderTextureQuad(MeshRef quad, int textureId, bool blend)
    {
        if (!NativeGuiEnabled || device == null || quad == null)
        {
            base.RenderTextureQuad(quad!, textureId, blend);
            return;
        }

        if (!DrawNativeGuiMesh(nativeTextureQuad, quad, textureId, 0, 1.0f, blend, "TextureQuad"))
        {
            base.RenderTextureQuad(quad, textureId, blend);
        }
    }

    /// <summary>The 2D line overlay's draw: the native pass, or the neutral body's RenderMesh.</summary>
    public override void RenderOverlayLines(MeshRef lines, int textureId, float lineWidth, bool blend)
    {
        if (!NativeGuiEnabled || device == null || lines == null)
        {
            base.RenderOverlayLines(lines!, textureId, lineWidth, blend);
            return;
        }

        if (!DrawNativeGuiMesh(nativeOverlayLines, lines, textureId, 0, lineWidth, blend, "OverlayLines"))
        {
            base.RenderOverlayLines(lines, textureId, lineWidth, blend);
        }
    }

    /// <summary>
    /// One GUI mesh recorded as its own native pass: the target the caller bound, every bound
    /// colour slot in scope, the fixed state the caller stated, the mesh's own vertex layout
    /// and topology, and the sampled textures resolved from handles.
    ///
    /// False means nothing was recorded and the caller must run the seam's neutral body - a
    /// missing target, a program that is not the one this pass is for, a mesh the device does
    /// not know, or a pipeline that is still compiling.
    /// </summary>
    private bool DrawNativeGuiMesh(NativeMeshPass pass, MeshRef mesh, int textureId, int overlayTextureId,
        float lineWidth, bool blend, string passLabel)
        => DrawNativeGuiMesh(pass, mesh, textureId, overlayTextureId, lineWidth, blend, EnumBlendMode.Standard,
            depthTest: false, depthWrite: false, CompareOp.Less, scissor: null, passLabel, CullModeFlags.None);

    private bool DrawNativeGuiMesh(NativeMeshPass pass, MeshRef mesh, int textureId, int overlayTextureId,
        float lineWidth, bool blend, EnumBlendMode blendMode, bool depthTest, bool depthWrite, CompareOp depthCompare,
        Rect2D? scissor, string passLabel, CullModeFlags cull = CullModeFlags.None, int instanceCount = 1)
    {
        FrameBufferRef target = CurrentFrameBuffer;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        var vao = mesh as VAO;
        if (program == null || vao == null || vao.VaoId == 0 || vao.Disposed) return false;
        // ShaderProgramMinimalGui has no pass name; its pass is named "".
        if (!string.Equals(program.PassName ?? "", pass.PassName, StringComparison.Ordinal)) return false;

        // The Ortho stage draws into the default framebuffer, which has no FrameBufferRef of
        // its own (ClientPlatformWindows.LoadFrameBuffer sets CurrentFrameBuffer null for it);
        // the pass API names it the same way the post chain does.
        int framebufferId = target?.FboId ?? PassDeclaration.DefaultFramebuffer;
        uint slots = target != null ? NativeAllColorSlots(target) : 1u;

        int layoutId = device.NativeMeshLayoutId(vao.VaoId);
        if (layoutId < 0) return false;

        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, slots);
        if (formats == null) return false;

        NativePipeline? pipeline = NativeMeshPipelineFor(pass, program, framebufferId, slots, layoutId,
            new NativePipelineDescription
            {
                Blend = GuiSlots(formats, blend, framebufferId, blendMode),
                DepthTest = depthTest,
                DepthWrite = depthWrite,
                DepthCompare = depthCompare,
                Cull = cull,
                Topology = device.NativeMeshTopology(vao.VaoId),
                LineWidth = lineWidth,
            });
        if (pipeline == null) return false;

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        Rect2D viewport = StatedViewport();
        bool recorded = false;
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = passLabel + "/" + framebufferId,
            FramebufferId = framebufferId,
            ColorSlots = slots,
            Reads = pass.SamplerNames.Length > 1
                ? new[] { textureId, overlayTextureId }
                : new[] { textureId },
            Flags = PassFlags.AllowSplit,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
            Scissor = scissor,
        }))
        {
            Span<NativeTexture> textures = stackalloc NativeTexture[pass.Samplers.Length];
            textures[0] = new NativeTexture(pass.Samplers[0], textureId);
            if (textures.Length > 1) textures[1] = new NativeTexture(pass.Samplers[1], overlayTextureId);
            recorded = device.DrawNativeMeshInstanced(pipeline, vao.VaoId, instanceCount, textures);
        }
        device.EndNativePass();

        // Whatever the stage was drawing into before this pass keeps drawing into it through
        // the generic stated route, so its own pass context is restored - the same restore the
        // sky pass and the TAA resolve do.
        SetPassContext(outer, outerFlags);
        return recorded;
    }

    /// <summary>
    /// A 2D overlay's fixed state per colour attachment: the caller's blend on slot 0 through
    /// the tracker's own factor table, and every other slot masked off so an attachment the
    /// fragment shader never writes keeps its contents as it does on GL (rule 9).
    /// </summary>
    private AttachmentBlend[] GuiSlots(RenderTargetFormats formats, bool blend, int framebufferId,
        EnumBlendMode mode = EnumBlendMode.Standard)
    {
        var slots = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        slots[0] = AttachmentBlend.For(blend, mode);
        // World/UI separation: straight-alpha GUI accumulates coverage in the UI image.
        if (stated.IsUiImage(framebufferId)) slots[0] = slots[0].ForUiImage();
        for (int i = 1; i < slots.Length; i++)
        {
            slots[i] = AttachmentBlend.Default;
            slots[i].WriteMask = 0;
        }
        return slots;
    }
}
