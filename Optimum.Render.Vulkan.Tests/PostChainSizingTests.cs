using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS plan, Phase 1: the post chain's viewports now come from the target being drawn
/// into (its <c>FrameBufferRef.Width/Height</c>) or from the platform's render size,
/// instead of being recomputed as <c>ClientSize * SSAA</c> - or that product divided by
/// 2 or 4 for the blur chain.
///
/// This phase is allowed to change nothing on screen, and "nothing" is a pixel claim, so
/// it is made with pixels: the same fullscreen post pass is drawn into the same target
/// twice, once with the viewport the new rule produces and once with the expression the
/// old code evaluated, and the two readbacks have to be byte-identical. Run at render
/// scale 1.0 and 0.5, and at odd window sizes, which is where a truncation difference
/// would show up if the replacement were not exact.
///
/// The fragment shader is a function of <c>gl_FragCoord</c> alone, so a viewport that is
/// one pixel off changes both the covered area and the values inside it.
/// </summary>
public class PostChainSizingTests
{
    private readonly ITestOutputHelper _output;

    public PostChainSizingTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    // A post pass whose output depends on where the fragment landed, so the viewport is
    // load-bearing: shrink it and the right/top of the target keeps its seeded value.
    private const string GradientFragment = """
        #version 330 core
        out vec4 outColor;
        void main(void)
        {
            outColor = vec4(fract(gl_FragCoord.x / 17.0), fract(gl_FragCoord.y / 13.0), 0.25, 1.0);
        }
        """;

    /// <summary>
    /// Primary, the med-res blur pair (render size / 2) and the low-res blur pair
    /// (render size / 4), at render scale 1.0 and 0.5 and at odd window sizes. Reading the
    /// target's own size must produce exactly the pixels the old expression produced.
    /// </summary>
    [SkippableTheory]
    // window 64x64 at SSAA 1: render 64x64
    [InlineData(64, 64, 1.0f)]
    // window 64x64 at SSAA 0.5: render 32x32 - the render scale the FSR/DLSS path uses
    [InlineData(64, 64, 0.5f)]
    // odd window, half scale: render 33x25, blur chain 16x12 and 8x6 - the case where
    // truncating before or after the division could disagree
    [InlineData(67, 51, 0.5f)]
    // odd window, native scale: render 65x33
    [InlineData(65, 33, 1.0f)]
    // supersampling, as the mega-screenshot path drives it
    [InlineData(33, 21, 2.0f)]
    public unsafe void ReadingTheTargetSizeDrawsTheSamePixelsAsTheOldFormula(
        int clientWidth, int clientHeight, float ssaa)
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context),
            "No usable Vulkan device.");

        using (context)
        using (var commands = new SetupQueue(context!))
        using (var textures = new TextureManager(context!, commands.Uploads))
        {
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader, Code = FullscreenVertex, Filename = "post.vsh",
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader, Code = GradientFragment, Filename = "post.fsh",
                },
            }, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));
            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            // SetupDefaultFrameBuffers' own arithmetic: the render size is the truncated
            // product, and the blur chain is that integer divided by 2 and by 4.
            int renderWidth = (int)((float)clientWidth * ssaa);
            int renderHeight = (int)((float)clientHeight * ssaa);
            Assert.True(renderWidth > 0 && renderHeight > 0);

            foreach (int divisor in new[] { 1, 2, 4 })
            {
                // What the allocator gives the target, and therefore what the new code reads.
                uint targetWidth = (uint)(renderWidth / divisor);
                uint targetHeight = (uint)(renderHeight / divisor);

                // What LoadFrameBuffer / RenderPostprocessingEffects computed before Phase 1.
                int legacyWidth = divisor == 1
                    ? (int)(ssaa * (float)clientWidth)
                    : (int)(ssaa * (float)clientWidth / (float)divisor);
                int legacyHeight = divisor == 1
                    ? (int)(ssaa * (float)clientHeight)
                    : (int)(ssaa * (float)clientHeight / (float)divisor);

                _output.WriteLine(
                    $"client {clientWidth}x{clientHeight} ssaa {ssaa} /{divisor}: " +
                    $"target {targetWidth}x{targetHeight}, legacy viewport {legacyWidth}x{legacyHeight}");

                byte[] fromTarget = DrawWithViewport(context!, commands, textures, state, targets,
                    pipelines, program, targetWidth, targetHeight, (int)targetWidth, (int)targetHeight);
                byte[] fromFormula = DrawWithViewport(context!, commands, textures, state, targets,
                    pipelines, program, targetWidth, targetHeight, legacyWidth, legacyHeight);

                Assert.Equal(fromFormula, fromTarget);

                // And the pass really did cover the target - an all-seed readback would make
                // the comparison above vacuous.
                Assert.Contains(fromTarget, b => b != 0x5A);
            }

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The other half of the change: the default framebuffer keeps the display size while
    /// the world target keeps the render size. At render scale 0.5 the two differ, and a
    /// pass that took the wrong one would cover a quarter of the target or overrun it.
    /// </summary>
    [SkippableFact]
    public unsafe void TheDisplaySizedTargetIsNotDrawnAtTheRenderSize()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context),
            "No usable Vulkan device.");

        using (context)
        using (var commands = new SetupQueue(context!))
        using (var textures = new TextureManager(context!, commands.Uploads))
        {
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader, Code = FullscreenVertex, Filename = "post.vsh",
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader, Code = GradientFragment, Filename = "post.fsh",
                },
            }, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));
            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            const uint display = 64;
            const uint render = 32;   // display * 0.5

            byte[] correct = DrawWithViewport(context!, commands, textures, state, targets,
                pipelines, program, display, display, (int)display, (int)display);
            byte[] wrong = DrawWithViewport(context!, commands, textures, state, targets,
                pipelines, program, display, display, (int)render, (int)render);

            Assert.NotEqual(correct, wrong);

            // The bottom-left quadrant is covered either way; the rest keeps the seed when the
            // viewport is the render size, which is exactly the bug this phase makes impossible.
            int stride = (int)display * 4;
            int topRight = (int)(display - 1) * stride + ((int)display - 1) * 4;
            Assert.Equal(0x5A, wrong[topRight]);
            Assert.NotEqual(0x5A, correct[topRight]);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    // ------------------------------------------------------------------ harness

    /// <summary>
    /// Seeds a target, draws the fullscreen post pass with the given viewport and reads the
    /// target back inside the frame. The seed is a value the shader can never produce.
    /// </summary>
    private static unsafe byte[] DrawWithViewport(
        VulkanContext context, SetupQueue commands, TextureManager textures, GlStateTracker state,
        RenderTargetManager targets, GraphicsPipelineCache pipelines, ShaderProgramResources program,
        uint width, uint height, int viewportWidth, int viewportHeight)
    {
        int color = textures.Create(width, height, Format.R8G8B8A8Unorm);
        var seed = new byte[width * height * 4];
        Array.Fill(seed, (byte)0x5A);
        fixed (byte* data = seed)
        {
            textures.Upload(color, 0, 0, 0, width, height, (IntPtr)data, 4);
        }

        int framebuffer = targets.Create(width, height);
        targets.Attach(framebuffer, 0, color);
        targets.SetDrawBuffers(framebuffer, 0b1);

        VulkanFramebuffer bound = targets.Get(framebuffer)!;
        int formatsId = targets.FormatsIdOf(bound);
        RenderTargetFormats formats = state.TargetFormats(formatsId);
        int attachmentCount = targets.EnabledAttachmentCount(bound);

        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++) blend[i] = state.BlendFor(i);

        Pipeline pipeline = pipelines.Get(
            state.BuildKey(0, formatsId, attachmentCount),
            new GraphicsPipelineCache.PipelineRequest
            {
                Program = program,
                VertexLayout = VertexLayoutDescription.Empty,
                Targets = formats,
                Blend = blend,
                PolygonMode = state.PolygonMode,
                Topology = state.Topology,
            });

        commands.SubmitAndWait(commandBuffer =>
        {
            targets.Bind(commandBuffer, framebuffer);
            targets.EnsureRendering(commandBuffer);

            Vk api = context.Api;
            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

            var viewport = new Viewport(0, 0, viewportWidth, viewportHeight, 0, 1);
            api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            var scissor = new Rect2D(new Offset2D(0, 0),
                new Extent2D((uint)viewportWidth, (uint)viewportHeight));
            api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

            api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
            api.CmdSetFrontFace(commandBuffer, GlStateTracker.FrontFace);
            api.CmdSetPrimitiveTopology(commandBuffer, PrimitiveTopology.TriangleList);
            api.CmdSetDepthTestEnable(commandBuffer, false);
            api.CmdSetDepthWriteEnable(commandBuffer, false);
            api.CmdSetDepthCompareOp(commandBuffer, CompareOp.Always);
            api.CmdSetStencilTestEnable(commandBuffer, false);
            api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
                StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, CompareOp.Always);
            api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
            api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
            api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0);
            api.CmdSetLineWidth(commandBuffer, 1.0f);

            api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            targets.EndRendering(commandBuffer);
        });

        return ReadTexture(context, commands, textures, color, width, height);
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, SetupQueue commands, TextureManager textures,
        int textureId, uint width, uint height)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)width * height * 4;

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(width, height, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }
}
