using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
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
    /// The composition case: one output, two attachments. The pipeline masks off the
    /// attachment the program never writes, so attachment 1 must come through untouched.
    /// </summary>
    [SkippableFact]
    public unsafe void AnAttachmentTheProgramDoesNotWriteKeepsItsContents()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
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
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int colorTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int glowTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);
            FillTexture(context!, commands, textures, colorTexture, size, 0x00);
            FillTexture(context!, commands, textures, glowTexture, size, 0x00);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, colorTexture);
            targets.Attach(framebuffer, 1, glowTexture);

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
    /// The attachment formats fed to the pipeline must match the attachments the scope was
    /// opened with: every bound slot, except one the declared pass leaves out, which is
    /// Undefined so output N stays aimed at slot N (the final composition writes Primary 0
    /// and samples Primary 1).
    /// </summary>
    [SkippableFact]
    public void ASlotTheDeclaredPassLeavesOutIsUndefinedInTheFormats()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            using var targets = new RenderTargetManager(context!, textures);

            int framebuffer = targets.Create(8, 8);
            for (int i = 0; i < 4; i++)
            {
                targets.Attach(framebuffer, i, textures.Create(8, 8, Format.R8G8B8A8Unorm));
            }

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            RenderTargetFormats all = targets.FormatsOf(targets.FormatsIdOf(bound));
            Assert.Equal(4, all.ColorFormats.Length);
            Assert.All(all.ColorFormats, format => Assert.Equal(Format.R8G8B8A8Unorm, format));

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.DeclarePass(commandBuffer, new PassDeclaration { Name = "Compose", ColorSlots = ~(1u << 2) }, framebuffer);
            });
            RenderTargetFormats excluded = targets.FormatsOf(targets.FormatsIdOf(bound));
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
        GraphicsPipelineCache pipelines, PipelineKeyState state, ShaderProgramResources program,
        int framebuffer, uint size)
    {
        VulkanFramebuffer bound = targets.Get(framebuffer)!;
        int formatsId = targets.FormatsIdOf(bound);
        RenderTargetFormats formats = targets.FormatsOf(formatsId);
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
            api.CmdSetFrontFace(commandBuffer, PipelineKeyState.FrontFace);
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
