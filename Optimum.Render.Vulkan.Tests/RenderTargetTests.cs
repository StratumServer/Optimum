using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Covers render targets, and specifically the semantics of glDrawBuffers.
///
/// This is the least obvious behaviour in the whole backend. Since Phase 2 (C4)
/// glDrawBuffers is a write mask inside a scope that keeps every bound attachment;
/// the game's one read of an excluded slot (the final composition pass renders into
/// Primary 0 while sampling Primary 1) takes that slot out of the scope instead of
/// tripping a feedback-loop error. MotionWindowTests covers the pixels per tier.
/// </summary>
public class RenderTargetTests
{
    private readonly ITestOutputHelper _output;

    public RenderTargetTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(
        ITestOutputHelper output, List<string> messages, out VulkanContext? context) =>
        GpuTest.TryCreateContext(output, messages, out context);

    private const string SingleOutputVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The composition case: one output, two attachments, only the first
    /// selected. Attachment 1 must come through untouched.
    /// </summary>
    [SkippableFact]
    public unsafe void AnAttachmentLeftOutOfDrawBuffersIsNotWritten()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int colorTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int glowTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);

            // Seed both so "unchanged" is distinguishable from "cleared".
            FillTexture(context!, commands, textures, colorTexture, size, 0x11);
            FillTexture(context!, commands, textures, glowTexture, size, 0x77);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, colorTexture);
            targets.Attach(framebuffer, 1, glowTexture);
            targets.SetDrawBuffers(framebuffer, 0b01);   // attachment 0 only

            TranslatedProgram translated = Translate(compiler, SingleOutputVertex, """
                #version 330 core
                out vec4 outColor;
                void main(void) { outColor = vec4(1.0, 0.0, 0.0, 1.0); }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            byte[] color = ReadTexture(context!, commands, textures, colorTexture, size);
            byte[] glow = ReadTexture(context!, commands, textures, glowTexture, size);

            // Attachment 0 was drawn into.
            Assert.Equal(255, color[0]);
            Assert.Equal(0, color[1]);

            // Attachment 1 kept every byte it started with.
            Assert.All(glow, b => Assert.Equal(0x77, b));

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// With both attachments selected, a two-output shader writes both. This is
    /// the ordinary MRT case the opaque pass uses for colour and glow.
    /// </summary>
    [SkippableFact]
    public unsafe void SelectedAttachmentsAllReceiveTheirMatchingOutput()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int colorTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int glowTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);
            FillTexture(context!, commands, textures, colorTexture, size, 0x00);
            FillTexture(context!, commands, textures, glowTexture, size, 0x00);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, colorTexture);
            targets.Attach(framebuffer, 1, glowTexture);
            targets.SetDrawBuffers(framebuffer, 0b11);

            TranslatedProgram translated = Translate(compiler, SingleOutputVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                layout(location = 1) out vec4 outGlow;
                void main(void)
                {
                    outColor = vec4(1.0, 0.0, 0.0, 1.0);
                    outGlow  = vec4(0.0, 1.0, 0.0, 1.0);
                }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 2, translated);
            state.SetProgram(2);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            byte[] color = ReadTexture(context!, commands, textures, colorTexture, size);
            byte[] glow = ReadTexture(context!, commands, textures, glowTexture, size);

            Assert.Equal(255, color[0]);   // red
            Assert.Equal(0, color[1]);
            Assert.Equal(0, glow[0]);
            Assert.Equal(255, glow[1]);    // green

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// Phase 2 (C4): a draw-buffer change is a write-mask change. The scope keeps
    /// every bound attachment, so changing the mask never restarts it; sampling a
    /// slot whose draw buffer is off takes that slot out (one feedback split), and
    /// selecting it again lets it rejoin (another).
    /// </summary>
    [SkippableFact]
    public unsafe void ChangingTheDrawBufferMaskKeepsTheRenderingScope()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);

            int a = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int b = textures.Create(size, size, Format.R8G8B8A8Unorm);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, a);
            targets.Attach(framebuffer, 1, b);
            targets.SetDrawBuffers(framebuffer, 0b01);

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.EnsureRendering(commandBuffer);
                Assert.Equal(1, targets.ScopesOpened);

                // Same mask: the scope stands.
                targets.EnsureRendering(commandBuffer);
                Assert.Equal(1, targets.ScopesOpened);

                // Mask changes, back and forth: still the one scope.
                targets.SetDrawBuffers(framebuffer, 0b11);
                targets.EnsureRendering(commandBuffer);
                targets.SetDrawBuffers(framebuffer, 0b01);
                targets.EnsureRendering(commandBuffer);
                targets.SetDrawBuffers(framebuffer, 0b10);
                targets.EnsureRendering(commandBuffer);
                Assert.Equal(1, targets.ScopesOpened);
                Assert.Equal(0, targets.MaskRestarts);
                Assert.Equal(0, targets.FeedbackSplits);

                // Sampling b while its draw buffer is off takes it out of the scope.
                targets.SetDrawBuffers(framebuffer, 0b01);
                targets.ExcludeSampledAttachment(commandBuffer, b);
                Assert.False(targets.RenderingActive);
                textures.TransitionTexture(commandBuffer, textures.Get(b)!, ImageLayout.ShaderReadOnlyOptimal);
                targets.EnsureRendering(commandBuffer);
                Assert.Equal(2, targets.ScopesOpened);
                Assert.Equal(1, targets.FeedbackSplits);
                Assert.Equal(1, targets.EnabledAttachmentCount(targets.Get(framebuffer)!));

                // Selecting b again lets it rejoin.
                targets.SetDrawBuffers(framebuffer, 0b11);
                targets.EnsureRendering(commandBuffer);
                Assert.Equal(3, targets.ScopesOpened);
                Assert.Equal(2, targets.FeedbackSplits);
                Assert.Equal(2, targets.EnabledAttachmentCount(targets.Get(framebuffer)!));
                Assert.Equal(0, targets.MaskRestarts);

                targets.EndRendering(commandBuffer);
            });

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The attachment formats fed to the pipeline must match the attachments the
    /// scope was opened with. Since Phase 2 (C4) that is every bound slot whatever
    /// its draw buffer, so the formats id is stable across mask toggles; only a
    /// sample-excluded or unbound slot is Undefined, keeping output N aimed at slot N.
    /// </summary>
    [SkippableFact]
    public void DrawBufferMasksDoNotChangeTheFormatsThePipelineSees()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);

            int framebuffer = targets.Create(8, 8);
            for (int i = 0; i < 4; i++)
            {
                targets.Attach(framebuffer, i, textures.Create(8, 8, Format.R8G8B8A8Unorm));
            }

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            targets.SetDrawBuffers(framebuffer, 0b1111);
            int allSelected = targets.FormatsIdOf(bound);

            // The OIT pass draws to 0 and 3 while leaving 1 and 2 out.
            targets.SetDrawBuffers(framebuffer, 0b1001);
            RenderTargetFormats formats = state.TargetFormats(targets.FormatsIdOf(bound));

            Assert.Equal(allSelected, targets.FormatsIdOf(bound));
            Assert.Equal(4, formats.ColorFormats.Length);
            Assert.All(formats.ColorFormats, format => Assert.Equal(Format.R8G8B8A8Unorm, format));

            // A slot a draw samples with its draw buffer off leaves the scope: Undefined.
            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.ExcludeSampledAttachment(commandBuffer, bound.Color[2].TextureId);
            });
            RenderTargetFormats excluded = state.TargetFormats(targets.FormatsIdOf(bound));
            Assert.Equal(4, excluded.ColorFormats.Length);
            Assert.Equal(Format.R8G8B8A8Unorm, excluded.ColorFormats[1]);
            Assert.Equal(Format.Undefined, excluded.ColorFormats[2]);
            Assert.Equal(Format.R8G8B8A8Unorm, excluded.ColorFormats[3]);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static TranslatedProgram Translate(ShaderCompiler compiler, string vertex, string fragment) =>
        ShaderTranslator.Translate(new[]
        {
            new ShaderStageSource { Stage = EnumShaderType.VertexShader, Code = vertex, Filename = "t.vsh" },
            new ShaderStageSource { Stage = EnumShaderType.FragmentShader, Code = fragment, Filename = "t.fsh" },
        }, compiler);

    private static unsafe void RenderFullscreen(
        VulkanContext context, SetupQueue commands, RenderTargetManager targets,
        GraphicsPipelineCache pipelines, GlStateTracker state, ShaderProgramResources program,
        int framebuffer, uint size)
    {
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

            var viewport = new Viewport(0, 0, size, size, 0, 1);
            api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(size, size));
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
    }

    private static unsafe void FillTexture(
        VulkanContext context, SetupQueue commands, TextureManager textures,
        int textureId, uint size, byte value)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, value);
        fixed (byte* data = pixels)
        {
            textures.Upload(textureId, 0, 0, 0, size, size, (IntPtr)data, 4);
        }
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, SetupQueue commands, TextureManager textures, int textureId, uint size)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)size * size * 4;

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }

}
