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
/// Device-level proof of the multi-attachment behaviour a TAA motion attachment
/// will lean on: a fifth colour attachment carried alongside colour, glow and OIT
/// that a resolve pass writes selectively and reads back read-only, without
/// disturbing its neighbours.
///
/// Every test here runs with validation on and asserts a clean log, the same way
/// RenderTargetTests and WorldRenderPathTests do - the failures this guards
/// against render a legal, silently wrong frame rather than throwing.
/// </summary>
public class AttachmentSemanticsTests
{
    private readonly ITestOutputHelper _output;

    public AttachmentSemanticsTests(ITestOutputHelper output) => _output = output;

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
        ITestOutputHelper output, List<string> messages, out VulkanContext? context) =>
        GpuTest.TryCreateContext(output, messages, out context);

    /// <summary>
    /// Five colour attachments, a shader that declares outputs only at locations
    /// 0 and 4, and a draw-buffer mask that enables only 0 and 4. A motion
    /// attachment sitting at index 4 alongside colour, glow and two OIT layers
    /// must receive exactly its own write and leave 1-3 alone, the same way
    /// glow does at index 1 today.
    /// </summary>
    [SkippableFact]
    public unsafe void OnlyTheDeclaredLocationsAmongFiveAttachmentsAreWritten()
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
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            var attachment = new int[5];
            byte[] seeds = { 0x10, 0x30, 0x50, 0x70, 0x90 };
            for (int i = 0; i < 5; i++)
            {
                attachment[i] = textures.Create(size, size, Format.R8G8B8A8Unorm);
                FillTexture(textures, attachment[i], size, seeds[i]);
            }

            int framebuffer = targets.Create(size, size);
            for (int i = 0; i < 5; i++) targets.Attach(framebuffer, i, attachment[i]);
            targets.SetDrawBuffers(framebuffer, 0b10001); // 0 and 4 only

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                layout(location = 4) out vec4 outMotion;
                void main(void)
                {
                    outColor  = vec4(1.0, 0.0, 0.0, 1.0);
                    outMotion = vec4(0.0, 0.0, 1.0, 1.0);
                }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            byte[] color = ReadTexture(context!, commands, textures, attachment[0], size);
            byte[] motion = ReadTexture(context!, commands, textures, attachment[4], size);

            Assert.Equal(255, color[0]);
            Assert.Equal(0, color[2]);
            Assert.Equal(0, motion[0]);
            Assert.Equal(255, motion[2]);

            for (int i = 1; i <= 3; i++)
            {
                byte[] untouched = ReadTexture(context!, commands, textures, attachment[i], size);
                Assert.All(untouched, b => Assert.Equal(seeds[i], b));
            }

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// Same five-attachment framebuffer, but the mask enables all five while the
    /// shader statically writes only location 0. The Vulkan spec leaves the
    /// unwritten locations' contents undefined rather than promising they are
    /// preserved, so this documents what this driver actually does rather than
    /// asserting a guarantee the TAA design may not lean on. The unwritten
    /// attachments are still read back, but only to log whether they were
    /// preserved; the run fails only on validation errors.
    /// </summary>
    [SkippableFact]
    public unsafe void UnwrittenButEnabledAttachmentsKeepTheirContents()
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
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            var attachment = new int[5];
            byte[] seeds = { 0x10, 0x30, 0x50, 0x70, 0x90 };
            for (int i = 0; i < 5; i++)
            {
                attachment[i] = textures.Create(size, size, Format.R8G8B8A8Unorm);
                FillTexture(textures, attachment[i], size, seeds[i]);
            }

            int framebuffer = targets.Create(size, size);
            for (int i = 0; i < 5; i++) targets.Attach(framebuffer, i, attachment[i]);
            targets.SetDrawBuffers(framebuffer, 0b11111); // all five enabled

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = vec4(1.0, 0.0, 0.0, 1.0); }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            byte[] color = ReadTexture(context!, commands, textures, attachment[0], size);
            Assert.Equal(255, color[0]);
            Assert.Equal(0, color[2]);

            // Locations that are enabled in the draw-buffer mask but never
            // written by the fragment shader hold undefined contents per the
            // Vulkan spec, so this is an observation and not an assertion: a
            // conforming driver is free to leave anything there. It is recorded
            // so the behaviour of the machine the suite runs on is visible in
            // the log. The TAA resolve pass must not depend on it either way -
            // it has to name every attachment it touches in both the shader and
            // the draw-buffer mask, as the tests above do.
            bool preserved = true;
            for (int i = 1; i <= 4; i++)
            {
                byte[] untouched = ReadTexture(context!, commands, textures, attachment[i], size);
                bool attachmentPreserved = untouched.All(b => b == seeds[i]);
                preserved &= attachmentPreserved;
                _output.WriteLine(
                    $"attachment {i}: seed 0x{seeds[i]:X2}, first byte 0x{untouched[0]:X2}, " +
                    $"preserved={attachmentPreserved}");
            }

            _output.WriteLine($"unwritten-but-enabled attachments preserved: {preserved}");
            // No longer an observation: the pipeline zeroes the colour write
            // mask of every attachment the fragment shader does not store to,
            // so the driver cannot write undefined values into them.
            Assert.True(preserved, "an enabled attachment the shader never writes must keep its contents");

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// Attachment 0 blends normally (standard alpha) while attachment 4 - where
    /// a motion attachment would sit - is switched to replace blending via
    /// SetBlendFuncSeparate, and the shader writes alpha 0 to both. If the two
    /// attachments shared one blend state, alpha-0 would leave both at their
    /// destination colour; independent state must let attachment 4 come through
    /// as the plain source value regardless of the alpha the draw wrote.
    /// </summary>
    [SkippableFact]
    public unsafe void EachAttachmentBlendsWithItsOwnFactorsRegardlessOfSharedAlpha()
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
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int colorTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int motionTexture = textures.Create(size, size, Format.R8G8B8A8Unorm);
            FillTexture(textures, colorTexture, size, 0x33);
            FillTexture(textures, motionTexture, size, 0x33);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, colorTexture);
            targets.Attach(framebuffer, 4, motionTexture);
            targets.SetDrawBuffers(framebuffer, 0b10001); // 0 and 4, 1-3 unattached

            // Attachment 0: ordinary alpha blending.
            state.SetBlend(true, EnumBlendMode.Standard);
            // Attachment 4: GL_ONE, GL_ZERO on both channels - a plain replace,
            // independent of the alpha the fragment writes.
            state.SetAttachmentBlendFunc(4, 1, 0, 1, 0);

            TranslatedProgram translated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                layout(location = 4) out vec4 outMotion;
                void main(void)
                {
                    outColor  = vec4(0.8, 0.8, 0.8, 0.0);
                    outMotion = vec4(0.8, 0.8, 0.8, 0.0);
                }
                """);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            RenderFullscreen(context!, commands, targets, pipelines, state, program, framebuffer, size);

            byte[] color = ReadTexture(context!, commands, textures, colorTexture, size);
            byte[] motion = ReadTexture(context!, commands, textures, motionTexture, size);

            // Standard blend, source alpha 0: dst * 1 + src * 0, so the
            // destination colour (0x33 = 51) survives.
            Assert.InRange(color[0], 43, 59);
            // Replace blend ignores alpha entirely: the source value (0.8 * 255
            // = 204) lands regardless.
            Assert.InRange(motion[0], 196, 212);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// A resolve pass samples the depth attachment it is itself bound against,
    /// with depth writes off - exactly what a TAA resolve does to reconstruct
    /// world position. The scope has to drop the depth attachment into
    /// DEPTH_READ_ONLY_OPTIMAL rather than the write layout, or this is either a
    /// validation error (reading an attachment layout as a sampled image) or a
    /// feedback loop.
    /// </summary>
    [SkippableFact]
    public unsafe void TheBoundFramebuffersDepthCanBeSampledWithWritesOff()
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
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();

            int depth = textures.Create(size, size, Format.D32Sfloat);
            int color = textures.Create(size, size, Format.R8G8B8A8Unorm);

            // A depth-only framebuffer to draw the known depth into, the same
            // way the shadow / opaque passes do.
            int depthPass = targets.Create(size, size);
            targets.Attach(depthPass, -1, depth);
            targets.SetDrawBuffers(depthPass, 0);

            TranslatedProgram depthOnly = Translate(compiler, """
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
            Assert.True(depthOnly.Success, string.Join("; ", depthOnly.Errors));

            using var depthProgram = new ShaderProgramResources(context!, 1, depthOnly);
            state.SetProgram(1);
            state.SetDepthTest(true);
            state.SetDepthWrite(true);
            state.SetDepthFunc(0x203); // GL_LEQUAL

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, depthPass);
                targets.ClearDepth(commandBuffer, 1f);
                targets.EndRendering(commandBuffer);
            });

            RenderFullscreen(context!, commands, targets, pipelines, state, depthProgram, depthPass, size,
                depthTest: true);

            // The resolve target: the same depth texture attached read-only,
            // plus a colour attachment the sampled value is written into.
            int resolvePass = targets.Create(size, size);
            targets.Attach(resolvePass, -1, depth);
            targets.Attach(resolvePass, 0, color);
            targets.SetDrawBuffers(resolvePass, 0b1);

            TranslatedProgram resolveTranslated = Translate(compiler, FullscreenVertex, """
                #version 330 core
                uniform sampler2D depthTex;
                layout(location = 0) out vec4 outColor;
                void main(void)
                {
                    float d = texelFetch(depthTex, ivec2(gl_FragCoord.xy), 0).r;
                    outColor = vec4(d, 0.0, 0.0, 1.0);
                }
                """);
            Assert.True(resolveTranslated.Success, string.Join("; ", resolveTranslated.Errors));

            using var resolveProgram = new ShaderProgramResources(context!, 2, resolveTranslated);
            state.SetProgram(2);

            RenderFullscreenSamplingDepth(
                context!, commands, textures, targets, pipelines, state, resolveProgram, resolvePass, depth, size);

            byte[] resolved = ReadTexture(context!, commands, textures, color, size);

            // (0.5 + 1.0) * 0.5 = 0.75, the GL-to-Vulkan depth remap - measured
            // the same way ADepthOnlyTargetStoresWhatWasDrawn does, then read
            // back through the sampled copy rather than a direct depth readback.
            Assert.InRange(resolved[0], (byte)185, (byte)198);

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
        int framebuffer, uint size, bool depthTest = false)
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

            api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            targets.EndRendering(commandBuffer);
        });
    }

    /// <summary>
    /// Like <see cref="RenderFullscreen" />, but binds one combined-image-sampler
    /// descriptor referring to <paramref name="sampledDepthTextureId" /> at set 1,
    /// binding 0 - the shape a resolve pass reading its own depth attachment
    /// needs. The framebuffer's depth attachment is put in
    /// DEPTH_READ_ONLY_OPTIMAL for the scope rather than the write layout, and
    /// depth test/write stay off throughout.
    /// </summary>
    private static unsafe void RenderFullscreenSamplingDepth(
        VulkanContext context, SetupQueue commands, TextureManager textures, RenderTargetManager targets,
        GraphicsPipelineCache pipelines, GlStateTracker state, ShaderProgramResources program,
        int framebuffer, int sampledDepthTextureId, uint size)
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

        using var descriptors = new DescriptorCache(context);

        commands.SubmitAndWait(commandBuffer =>
        {
            Vk api = context.Api;

            targets.Bind(commandBuffer, framebuffer);
            targets.SetDepthReadOnly(true);
            targets.EnsureRendering(commandBuffer);

            VulkanTexture depthTexture = textures.Get(sampledDepthTextureId)!;
            var samplerBinding = new SamplerBindingValue(
                (uint)program.Interface.Samplers[0].Binding,
                depthTexture.View,
                textures.Samplers.Get(depthTexture.State),
                depthTexture.Id,
                ImageLayout.DepthReadOnlyOptimal);

            DescriptorSet samplerSet = descriptors.Get(
                new DescriptorSetContents(program.ProgramId, ProgramInterfaceLayout.SamplerSet,
                    new[] { samplerBinding }, Array.Empty<BufferBindingValue>()),
                program.SetLayouts[ProgramInterfaceLayout.SamplerSet]);

            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
            api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                ProgramInterfaceLayout.SamplerSet, 1, &samplerSet, 0, null);

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

        targets.SetDepthReadOnly(false);
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
