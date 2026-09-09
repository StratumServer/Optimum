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
/// The device features the world passes need, which the menu never touches.
///
/// Reaching a world in the real client needs a signed-in account, so these drive
/// the same device paths directly instead: the layered accumulation target and
/// six-attachment blending that weighted-blended OIT sets up, the depth-only
/// shadow map, and the occlusion query the sun uses to size its glare. Each runs
/// with validation on and asserts a clean message log, because the failures these
/// guard against are silent - a legal frame that draws the wrong thing.
/// </summary>
public class WorldRenderPathTests
{
    private readonly ITestOutputHelper _output;

    public WorldRenderPathTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private static bool TryCreateContext(
        ITestOutputHelper output, List<string> messages, out VulkanContext? context)
    {
        var options = new VulkanContextOptions
        {
            Headless = true,
            EnableValidation = true,
            DebugCallback = messages.Add,
        };

        bool created = VulkanContext.TryCreate(options, out context, out string? failureReason);
        if (!created)
        {
            output.WriteLine("Vulkan unavailable: " + failureReason);
        }
        return created;
    }

    /// <summary>
    /// OIT accumulates into three layers of one 2D array texture, attached a
    /// layer at a time to colour attachments 3, 4 and 5. Each attachment has to
    /// reach its own layer: a layer index dropped somewhere in the attach path
    /// would have all three writing over each other, which still renders and
    /// still validates.
    /// </summary>
    [SkippableFact]
    public unsafe void EachOitAccumulationLayerIsWrittenSeparately()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            const uint layers = 3;
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int accumulation = textures.Create(size, size, Format.R8G8B8A8Unorm, layers);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, accumulation, 0);
            targets.Attach(framebuffer, 1, accumulation, 1);
            targets.Attach(framebuffer, 2, accumulation, 2);
            targets.SetDrawBuffers(framebuffer, 0b111);

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outA;
                layout(location = 1) out vec4 outB;
                layout(location = 2) out vec4 outC;
                void main(void)
                {
                    outA = vec4(1.0, 0.0, 0.0, 1.0);
                    outB = vec4(0.0, 1.0, 0.0, 1.0);
                    outC = vec4(0.0, 0.0, 1.0, 1.0);
                }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            // Red into layer 0, green into layer 1, blue into layer 2.
            Assert.Equal(new byte[] { 255, 0, 0 }, FirstPixel(context!, commands, textures, accumulation, size, 0));
            Assert.Equal(new byte[] { 0, 255, 0 }, FirstPixel(context!, commands, textures, accumulation, size, 1));
            Assert.Equal(new byte[] { 0, 0, 255 }, FirstPixel(context!, commands, textures, accumulation, size, 2));

            AssertNoValidationErrors(messages);
        }
    }

    /// <summary>
    /// The OIT pass gives each attachment its own blend factors in one draw:
    /// revealage multiplies down from one while accumulation adds up from zero.
    /// A per-attachment blend state collapsed into a single shared one would
    /// produce a plausible-looking but wrong composite.
    /// </summary>
    [SkippableFact]
    public unsafe void AttachmentsKeepIndependentBlendFactors()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int reveal = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int accum = textures.Create(size, size, Format.R8G8B8A8Unorm);

            // Revealage starts at one, accumulation at zero.
            FillTexture(textures, reveal, size, 255);
            FillTexture(textures, accum, size, 0);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, reveal);
            targets.Attach(framebuffer, 1, accum);
            targets.SetDrawBuffers(framebuffer, 0b11);

            // Attachment 0: dst * src (GL_ZERO, GL_SRC_COLOR reversed as the OIT
            // pass writes it - factor pair 774/0 is DST_COLOR, ZERO).
            state.SetBlend(true, EnumBlendMode.Standard);
            state.SetAttachmentBlendFunc(0, 774, 0, 774, 0);
            // Attachment 1: additive.
            state.SetAttachmentBlendFunc(1, 1, 1, 1, 1);

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outReveal;
                layout(location = 1) out vec4 outAccum;
                void main(void)
                {
                    outReveal = vec4(0.5, 0.5, 0.5, 1.0);
                    outAccum  = vec4(0.25, 0.25, 0.25, 1.0);
                }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            byte[] revealPixels = ReadTexture(context!, commands, textures, reveal, size);
            byte[] accumPixels = ReadTexture(context!, commands, textures, accum, size);

            // dst(1.0) * src(0.5) = 0.5, so revealage came down rather than
            // being replaced.
            Assert.InRange(revealPixels[0], 120, 136);
            // 0 + 0.25 = 0.25, so accumulation added rather than multiplying.
            Assert.InRange(accumPixels[0], 56, 72);

            AssertNoValidationErrors(messages);
        }
    }

    /// <summary>
    /// The shadow passes render depth with no colour attachment at all. A target
    /// that quietly requires one would fail to build a pipeline, and a depth
    /// attachment that never got stored would leave every shadow lookup reading
    /// the clear value.
    /// </summary>
    [SkippableFact]
    public unsafe void ADepthOnlyTargetStoresWhatWasDrawn()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int depth = textures.Create(size, size, Format.D32Sfloat);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, -1, depth);
            targets.SetDrawBuffers(framebuffer, 0);

            // Draws at a fixed clip depth; after the Vulkan remap that is 0.75.
            TranslatedProgram translated = Translate(compiler, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.5, 1.0);
                }
                """, """
                #version 330 core
                void main(void) { }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);
            state.SetDepthTest(true);
            state.SetDepthWrite(true);
            state.SetDepthFunc(0x203);   // GL_LEQUAL

            // The shadow pass clears depth to one before drawing; without that
            // the comparison runs against undefined contents and rejects
            // everything, which is a property of the test rather than the device.
            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.ClearDepth(commandBuffer, 1f);
                targets.EndRendering(commandBuffer);
            });

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size,
                depthTest: true);

            float stored = ReadDepth(context!, commands, textures, depth, size);

            // (0.5 + 1.0) * 0.5 = 0.75 - the GL-to-Vulkan depth remap, measured
            // rather than assumed.
            Assert.InRange(stored, 0.74f, 0.76f);

            AssertNoValidationErrors(messages);
        }
    }

    /// <summary>
    /// The sun's glare is sized by an occlusion query counting the samples its
    /// quad passed. A query that never produced a result would leave the glare
    /// pinned at its last value forever, which looks like a lighting bug rather
    /// than a query one.
    /// </summary>
    [SkippableFact]
    public unsafe void AnOcclusionQueryCountsTheSamplesThatPassed()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int color = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, color);
            targets.SetDrawBuffers(framebuffer, 0b1);

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = vec4(1.0); }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            var poolInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Occlusion,
                QueryCount = 1,
            };
            Assert.Equal(Result.Success,
                context!.Api.CreateQueryPool(context.Device, &poolInfo, null, out QueryPool pool));

            ulong passed;
            try
            {
                RenderFullscreen(context, commands, targets, pipelines, state, program, framebuffer, size,
                    depthTest: false, queryPool: pool);

                Assert.Equal(Result.Success, context.Api.GetQueryPoolResults(
                    context.Device, pool, 0, 1, (nuint)sizeof(ulong), &passed, sizeof(ulong),
                    QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit));
            }
            finally
            {
                context.Api.DestroyQueryPool(context.Device, pool, null);
            }

            // The triangle covers the whole 8x8 target.
            Assert.Equal((ulong)(size * size), passed);

            AssertNoValidationErrors(messages);
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
        VulkanContext context, VulkanCommands commands, RenderTargetManager targets,
        GraphicsPipelineCache pipelines, GlStateTracker state, ShaderProgramResources program,
        int framebuffer, uint size, bool depthTest = false, QueryPool queryPool = default)
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
            Vk api = context.Api;
            if (queryPool.Handle != 0)
            {
                api.CmdResetQueryPool(commandBuffer, queryPool, 0, 1);
            }

            targets.Bind(commandBuffer, framebuffer);
            targets.EnsureRendering(commandBuffer);

            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

            var viewport = new Viewport(0, 0, size, size, 0, 1);
            api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(size, size));
            api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

            api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
            api.CmdSetFrontFace(commandBuffer, GlStateTracker.FrontFace);
            api.CmdSetPrimitiveTopology(commandBuffer, PrimitiveTopology.TriangleList);
            api.CmdSetDepthTestEnable(commandBuffer, depthTest);
            api.CmdSetDepthWriteEnable(commandBuffer, depthTest);
            api.CmdSetDepthCompareOp(commandBuffer, depthTest ? CompareOp.LessOrEqual : CompareOp.Always);
            api.CmdSetStencilTestEnable(commandBuffer, false);
            api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
                StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, CompareOp.Always);
            api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
            api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
            api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0);
            api.CmdSetLineWidth(commandBuffer, 1.0f);

            if (queryPool.Handle != 0)
            {
                api.CmdBeginQuery(commandBuffer, queryPool, 0, 0);
            }
            api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            if (queryPool.Handle != 0)
            {
                api.CmdEndQuery(commandBuffer, queryPool, 0);
            }

            targets.EndRendering(commandBuffer);
        });
    }

    private static unsafe void FillTexture(TextureManager textures, int textureId, uint size, byte value)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, value);
        fixed (byte* data = pixels)
        {
            textures.Upload(textureId, 0, 0, 0, size, size, (IntPtr)data, 4);
        }
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, VulkanCommands commands, TextureManager textures,
        int textureId, uint size, uint layer = 0)
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
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, layer, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }

    private static byte[] FirstPixel(
        VulkanContext context, VulkanCommands commands, TextureManager textures,
        int textureId, uint size, uint layer) =>
        ReadTexture(context, commands, textures, textureId, size, layer).Take(3).ToArray();

    private static unsafe float ReadDepth(
        VulkanContext context, VulkanCommands commands, TextureManager textures, int textureId, uint size)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)size * size * sizeof(float);

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new float[size * size];
        fixed (float* destination = result)
        {
            System.Buffer.MemoryCopy((void*)readback.Mapped, destination, (long)bytes, (long)bytes);
        }
        return result[0];
    }

    private static void AssertNoValidationErrors(List<string> messages)
    {
        // Only what the layers reported at error severity. Advisories - a
        // fragment output with no attachment, say - are prefixed as warnings and
        // are not failures; treating every message as one made these assertions
        // fire on notes about correct frames.
        var errors = messages
            .Where(m => m.StartsWith(Optimum.Render.Vulkan.Core.VulkanContext.ErrorPrefix,
                                     StringComparison.Ordinal))
            .ToList();
        Assert.True(errors.Count == 0, "validation errors:\n" + string.Join("\n", errors));
    }
}
