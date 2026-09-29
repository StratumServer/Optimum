using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan.md), Phase 3b decision 5
// stage 2: the first world system on the native device API, and the proof that its mesh-draw
// entry points work end to end.
//
// What it draws: the sky dome - the 250-unit icosahedron SystemRenderSkyColor renders the sky
// gradient onto, once per frame, first in the Opaque stage.
// Where the other side is: ClientPlatformAbstract.RenderSkyDome's neutral body, which is the
// RenderMesh(MeshRef) call this seam replaced and which the OpenGL path still runs
// (ClientPlatformWindows.RenderMesh -> GL.DrawElements). NativeSkyEnabled false takes that
// route on the Vulkan device too, which is what the differential test compares against.
// Target and slots: the framebuffer the Opaque stage bound (Primary), every bound colour slot
// in the pass so the scope is the one the emulated draw opens; sky.frag writes outColor at 0
// and outGlow at 1 and the pipeline masks every other slot off, so the motion attachment and
// the G-buffer slots keep their contents exactly as they do on GL.
// State that is not obvious:
//   - no depth test and no depth write: the caller has already called GlDisableDepthTest, and
//     the dome is meant to sit behind everything;
//   - no culling: the OpenGL body draws with whatever cull state the stage before it left (a
//     shadow pass leaves it off, no shadow pass leaves back-face culling on), and the dome is
//     a closed hull drawn without depth test, so None is the state that draws every triangle
//     the GL path can draw;
//   - blend disabled: sky.frag writes alpha 1 into both its outputs, so the blend the stage
//     happens to have on makes no difference to the result;
//   - the two textures are passed as handles, because a native pass resolves what it samples
//     from handles rather than from the texture units the program's setters bound them to.
// What pins it: NativeSkyTests (old route against native route, pixels) and
// Optimum.Tests/native-sky-coverage-tests.cs (the lib seam).
public partial class VulkanClientPlatform
{
    /// <summary>
    /// False runs the seam's neutral body - the OpenGL body's RenderMesh - on the Vulkan device
    /// instead of the native pass: the old route the differential test compares against, in the
    /// pattern of <see cref="NativeBlitEnabled" />.
    /// </summary>
    internal bool NativeSkyEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_SKY") != "0";

    /// <summary>
    /// The sky program's pipeline and the placements its draw writes through. "modelViewMatrix"
    /// is the one value written per draw; everything else the client system set through the
    /// program's own setters is already in the program's record shadow when the draw binds set 2.
    /// </summary>
    private readonly NativeMeshPass nativeSky =
        new("sky", new[] { "modelViewMatrix" }, new[] { "sky", "glow" });

    /// <summary>
    /// A native program drawn with a mesh: its pipeline for one target and one mesh shape, and
    /// the placements its draws write through. The fullscreen twin is NativeFullscreenPass in
    /// VulkanClientPlatform.NativeBlit.cs; this one also keys on the mesh's vertex layout,
    /// because that is part of the pipeline.
    /// </summary>
    private sealed class NativeMeshPass
    {
        public NativeMeshPass(string passName, string[] uniforms, string[] samplers)
        {
            PassName = passName;
            UniformNames = uniforms;
            SamplerNames = samplers;
            Uniforms = new NativeUniform[uniforms.Length];
            Samplers = new NativeSamplerSlot[samplers.Length];
        }

        public string PassName { get; }
        public string[] UniformNames { get; }
        public string[] SamplerNames { get; }

        /// <summary>Resolved once per pipeline, never by name per draw.</summary>
        public NativeUniform[] Uniforms;
        public NativeSamplerSlot[] Samplers;

        public NativePipeline? Pipeline;
        public RenderTargetFormats? Formats;
        public int LayoutId = -1;
        public bool Reported;

        public void Adopt(NativePipeline pipeline, RenderTargetFormats formats, int layoutId)
        {
            Pipeline = pipeline;
            Formats = formats;
            LayoutId = layoutId;
            for (int i = 0; i < UniformNames.Length; i++) Uniforms[i] = pipeline.Uniform(UniformNames[i]);
            for (int i = 0; i < SamplerNames.Length; i++) Samplers[i] = pipeline.Sampler(SamplerNames[i]);
        }
    }

    /// <summary>
    /// The pipeline for one mesh program against one target and one mesh shape, rebuilt only
    /// when the program was relinked, the target's formats changed or the mesh's layout did.
    /// </summary>
    private NativePipeline? NativeMeshPipelineFor(NativeMeshPass pass, ShaderProgramBase program, int framebufferId,
        uint colorSlots, int layoutId, NativePipelineDescription description)
    {
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, colorSlots);
        if (formats == null) return null;

        if (pass.Pipeline != null && pass.Pipeline.ProgramId == program.ProgramId &&
            formats.Equals(pass.Formats) && pass.LayoutId == layoutId &&
            SameFixedState(pass.Pipeline.Description, description) && device.IsNativePipelineLive(pass.Pipeline))
        {
            return pass.Pipeline;
        }

        description.ProgramId = program.ProgramId;
        description.PassName = pass.PassName;
        description.VertexLayoutId = layoutId;
        description.Targets = formats;

        NativePipeline? pipeline = device.RequestNativePipeline(description, out string error);
        if (pipeline == null)
        {
            if (!pass.Reported)
            {
                pass.Reported = true;
                Logger.Warning("Optimum: no native pipeline for '{0}': {1}", pass.PassName, error);
            }
            pass.Pipeline = null;
            return null;
        }

        pass.Reported = false;
        pass.Adopt(pipeline, formats, layoutId);
        return pipeline;
    }

    /// <summary>
    /// Whether two descriptions ask for the same fixed state, which is what makes the cached
    /// pipeline of a <see cref="NativeMeshPass" /> usable for the next draw through it.
    ///
    /// Without this the one-entry cache answered any request for the same program, target and
    /// mesh shape with the pipeline it happened to build first: the aiming reticle's 0.5 and
    /// 1.0 line widths would then both rasterize at whichever came first, and a system that
    /// turns blending on and off between draws would blend both or neither. The device's own
    /// table keys on all of it (NativePipelineCacheKey), so falling through to
    /// <see cref="VulkanDevice.RequestNativePipeline" /> costs a dictionary lookup, not a
    /// pipeline.
    /// </summary>
    private static bool SameFixedState(NativePipelineDescription cached, NativePipelineDescription wanted)
    {
        if (cached.DepthTest != wanted.DepthTest || cached.DepthWrite != wanted.DepthWrite ||
            cached.DepthCompare != wanted.DepthCompare || cached.Cull != wanted.Cull ||
            cached.FrontFace != wanted.FrontFace || cached.Topology != wanted.Topology ||
            cached.PolygonMode != wanted.PolygonMode || cached.SamplesBoundDepth != wanted.SamplesBoundDepth ||
            !cached.LineWidth.Equals(wanted.LineWidth) || cached.Blend.Length != wanted.Blend.Length)
        {
            return false;
        }
        for (int i = 0; i < cached.Blend.Length; i++)
        {
            if (!cached.Blend[i].Equals(wanted.Blend[i])) return false;
        }
        return true;
    }

    /// <summary>Every bound colour slot of a target: the scope the emulated draw would open.</summary>
    private static uint NativeAllColorSlots(FrameBufferRef target)
    {
        int count = target.ColorTextureIds?.Length ?? 0;
        return count >= 32 ? uint.MaxValue : (1u << count) - 1u;
    }

    /// <summary>
    /// Opaque-stage fixed state for a world pass that draws over everything: no depth, no
    /// culling, no blending, one entry per colour attachment so the slots the program does not
    /// write are masked off rather than left to Vulkan's undefined contents (rule 9).
    /// </summary>
    private static AttachmentBlend[] OpaqueSlots(RenderTargetFormats formats)
    {
        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++) blend[i] = AttachmentBlend.Default;
        return blend;
    }

    /// <summary>The sky dome's draw: the native pass, or the neutral body's RenderMesh.</summary>
    public override void RenderSkyDome(MeshRef skyDome, int skyTextureId, int glowTextureId, float[] modelViewMatrix)
    {
        if (!NativeSkyEnabled || device == null || skyDome == null)
        {
            base.RenderSkyDome(skyDome!, skyTextureId, glowTextureId, modelViewMatrix);
            return;
        }

        FrameBufferRef target = CurrentFrameBuffer;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        var vao = skyDome as VAO;
        if (target == null || program == null || vao == null || vao.VaoId == 0 || vao.Disposed)
        {
            base.RenderSkyDome(skyDome, skyTextureId, glowTextureId, modelViewMatrix);
            return;
        }

        int layoutId = device.NativeMeshLayoutId(vao.VaoId);
        if (layoutId < 0)
        {
            base.RenderSkyDome(skyDome, skyTextureId, glowTextureId, modelViewMatrix);
            return;
        }

        uint slots = NativeAllColorSlots(target);
        RenderTargetFormats? formats = device.NativeTargetFormats(target.FboId, slots);
        if (formats == null)
        {
            base.RenderSkyDome(skyDome, skyTextureId, glowTextureId, modelViewMatrix);
            return;
        }

        NativePipeline? pipeline = NativeMeshPipelineFor(nativeSky, program, target.FboId, slots, layoutId,
            new NativePipelineDescription
            {
                Blend = OpaqueSlots(formats),
                DepthTest = false,
                DepthWrite = false,
                Cull = CullModeFlags.None,
                Topology = PrimitiveTopology.TriangleList,
            });
        if (pipeline == null)
        {
            base.RenderSkyDome(skyDome, skyTextureId, glowTextureId, modelViewMatrix);
            return;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        Rect2D viewport = StatedViewport();
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = "Sky/" + target.FboId,
            FramebufferId = target.FboId,
            ColorSlots = slots,
            Reads = new[] { skyTextureId, glowTextureId },
            Flags = PassFlags.AllowSplit,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
        }))
        {
            // The one per-draw write: the dome follows the player, so the model-view matrix is
            // this draw's and nothing else's. Everything else the system set is already in the
            // program's record, which the draw snapshots into this frame's uniform ring.
            if (modelViewMatrix != null && modelViewMatrix.Length >= 16)
            {
                device.WriteNative(pipeline, nativeSky.Uniforms[0], modelViewMatrix.AsSpan(0, 16));
            }
            device.DrawNativeMesh(pipeline, vao.VaoId, new[]
            {
                new NativeTexture(nativeSky.Samplers[0], skyTextureId),
                new NativeTexture(nativeSky.Samplers[1], glowTextureId),
            });
        }
        device.EndNativePass();

        SetPassContext(outer, outerFlags);
    }
}

// The cloud renderers of the forked VSEssentials (Systems/Weather/Newclouds), drawn natively.
//
// Both renderers end in a plain capi.Render.RenderMesh(quad), which lands in RenderMesh here;
// their state goes through OptimumForkGraphics, whose Vulkan implementation (VulkanForkGraphics)
// records it on this platform next to forwarding it to the device. The OpenGL side is the same
// fork code's GL branch plus ClientPlatformWindows.RenderMesh.
//
// - cloudmap (CloudRendererMap.OnRenderFrame): a fullscreen quad into the renderer's own device
//   framebuffer (tile and colour attachments), bound by id through the fork surface, blending
//   and depth test off. The target has no FrameBufferRef; the pass names the device id.
// - cloudvolumetric (CloudRendererVolumetric.OnRenderFrame, OIT stage): a fullscreen quad onto
//   the Transparent target under the OIT contract, depth test off, sampling Primary's depth -
//   which the Transparent target borrows as its own depth attachment, so the pipeline declares
//   SamplesBoundDepth and writes no depth (GL writes none with the depth test off either).
//
// Pinned by Optimum.Tests/native-world-systems-coverage-tests.cs.
public partial class VulkanClientPlatform
{
    /// <summary>False sends both cloud draws to the generic stated route (<c>OPTIMUM_VK_NATIVE_CLOUDS=0</c>).</summary>
    internal bool NativeCloudsEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_CLOUDS") != "0";

    private readonly NativeMeshPass nativeCloudMap =
        new("cloudmap", Array.Empty<string>(), Array.Empty<string>());

    private readonly NativeMeshPass nativeCloudVolumetric =
        new("cloudvolumetric", Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// The device framebuffer a fork renderer bound through OptimumForkGraphics.BindFramebuffer
    /// and has not unbound yet; 0 when the fork's binding is the platform's current target again
    /// (its restore) or the default framebuffer.
    /// </summary>
    private int forkFramebuffer;

    internal void NoteForkFramebuffer(int framebufferId)
    {
        FrameBufferRef current = CurrentFrameBuffer;
        forkFramebuffer = current != null && current.FboId == framebufferId ? 0 : framebufferId;
    }

    internal void NoteForkDepthTest(bool enabled)
    {
        statedDepthTest = enabled;
        stated.DepthTest = enabled;
    }

    internal void NoteForkBlend(bool enabled)
    {
        statedBlendOn = enabled;
        stated.SetBlendEnabled(enabled);
    }

    /// <summary>A cloud renderer's RenderMesh: the native pass, or false for the generic stated draw.</summary>
    private bool TryRenderCloudsNative(MeshRef mesh)
    {
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (!NativeWorldEnabled || !NativeCloudsEnabled || device == null || mesh == null || program == null)
        {
            return false;
        }

        string? name = program.PassName;
        if (name != "cloudmap" && name != "cloudvolumetric") return false;
        // The program the registry holds under that name, which is the fork's own registration.
        if (!IsRegistryProgram(program)) return false;

        return name == "cloudmap" ? DrawCloudMapNative(program, mesh) : DrawCloudVolumetricNative(program, mesh);
    }

    private bool DrawCloudMapNative(ShaderProgramBase program, MeshRef mesh)
    {
        var vao = mesh as VAO;
        int framebufferId = forkFramebuffer;
        if (vao == null || vao.VaoId == 0 || vao.Disposed || framebufferId <= 0) return false;

        int layoutId = device.NativeMeshLayoutId(vao.VaoId);
        if (layoutId < 0) return false;
        // The attachments the fork's SetDrawBuffers enabled decide the scope's formats.
        RenderTargetFormats? all = device.NativeTargetFormats(framebufferId, uint.MaxValue);
        if (all == null || all.ColorFormats.Length == 0) return false;
        uint slots = all.ColorFormats.Length >= 32 ? uint.MaxValue : (1u << all.ColorFormats.Length) - 1u;
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, slots);
        if (formats == null) return false;

        NativePipeline? pipeline = NativeMeshPipelineFor(nativeCloudMap, program, framebufferId, slots, layoutId,
            new NativePipelineDescription
            {
                Blend = OpaqueSlots(formats),
                DepthTest = false,
                DepthWrite = false,
                Cull = CullModeFlags.None,
                Topology = device.NativeMeshTopology(vao.VaoId),
            });
        if (pipeline == null) return false;

        ResolveDeclaredSamplers(program, pipeline, out NativeTexture[] textures, out int[] reads);

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        Rect2D viewport = StatedViewport();
        bool drawn = false;
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = "CloudMap/" + framebufferId,
            FramebufferId = framebufferId,
            ColorSlots = slots,
            Reads = reads,
            Flags = PassFlags.AllowSplit,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
        }))
        {
            drawn = device.DrawNativeMesh(pipeline, vao.VaoId, textures);
        }
        device.EndNativePass();
        SetPassContext(outer, outerFlags);
        return drawn;
    }

    private bool DrawCloudVolumetricNative(ShaderProgramBase program, MeshRef mesh)
    {
        FrameBufferRef bound = CurrentFrameBuffer;
        if (forkFramebuffer != 0 || bound == null || nativeTransparentBlend == null || !IsTransparentTarget(bound) ||
            statedDepthTest)
        {
            return false;
        }

        if (!NativeWorldPrepare(nativeCloudVolumetric, mesh, blending: statedBlendOn, depth: false,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline,
                count => StatedWorldBlend(bound, count), depthWrite: false, samplesBoundDepth: true))
        {
            return false;
        }

        ResolveDeclaredSamplers(program, pipeline, out NativeTexture[] textures, out int[] reads);
        // liquidDepth is not bound through the program: SystemRenderOITLayers points the sampler
        // at unit 4 by value, and the GL draw reads whatever the liquid pass left there - the
        // LiquidDepth target's depth texture. The native draw names that texture, since a
        // frame-bound sampler resolved to 0 would replace the frame's liquid depth with a
        // placeholder and cut every cloud off at the near plane.
        string[] names = pipeline.SamplerNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] != "liquidDepth" || textures[i].TextureId != 0) continue;
            FrameBufferRef? liquid = FrameBuffers is { Count: > (int)EnumFrameBuffer.LiquidDepth }
                ? FrameBuffers[(int)EnumFrameBuffer.LiquidDepth]
                : null;
            if (liquid == null) return false;
            textures[i] = new NativeTexture(textures[i].Sampler, liquid.DepthTextureId);
            reads[i] = liquid.DepthTextureId;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        bool drawn = false;
        if (NativeWorldBeginPass("CloudVolumetric", target, slots, reads))
        {
            drawn = device.DrawNativeMesh(pipeline, vao.VaoId, textures);
        }
        NativeWorldEndPass(target, outer, outerFlags);
        return drawn;
    }

    /// <summary>Every sampler the pipeline declares, resolved from the program's declared textures.</summary>
    private void ResolveDeclaredSamplers(ShaderProgramBase program, NativePipeline pipeline,
        out NativeTexture[] textures, out int[] reads)
    {
        string[] names = pipeline.SamplerNames;
        textures = new NativeTexture[names.Length];
        reads = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int id = DeclaredProgramTexture(program.ProgramId, names[i]);
            textures[i] = new NativeTexture(pipeline.Sampler(names[i]), id);
            reads[i] = id;
        }
    }
}
