using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

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
