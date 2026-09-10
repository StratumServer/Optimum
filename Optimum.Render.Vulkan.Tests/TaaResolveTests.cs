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

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Drives the real <c>taa-resolve</c> shader pair (see TAA-PLAN.md P2) on the
/// Vulkan backend with synthetic inputs, the way <see cref="WorldRenderPathTests" />
/// and <see cref="ChunkRenderPathTests" /> drive the world programs: load through
/// <see cref="ShaderCorpus" />, build a real pipeline against a three-attachment
/// MRT framebuffer (colour history RGBA16F, aux/glow RGBA8, linear depth R32F),
/// draw the fullscreen triangle and read back inside the frame.
///
/// This goes one level lower than the seam (<c>IOptimumGraphicsDevice</c>): the
/// public seam's <c>EnumTextureInternalFormat</c> has no R32F, and
/// <c>ReadDefaultFramebuffer</c> always assumes 4 bytes per pixel, neither of
/// which fits an HDR history or a float depth target. So this talks to
/// <see cref="TextureManager" />, <see cref="RenderTargetManager" /> and
/// <see cref="ShaderProgramResources" /> directly and builds descriptor sets by
/// hand with a private <see cref="DescriptorCache" />, mirroring what
/// <c>VulkanDevice</c> does per draw but scoped to one fullscreen pass with named
/// uniforms and named samplers.
/// </summary>
public class TaaResolveTests
{
    private readonly ITestOutputHelper _output;

    public TaaResolveTests(ITestOutputHelper output) => _output = output;

    private const uint Size = 32;

    private static readonly float[] Identity4 =
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f,
    };

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

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// With <c>resetHistory=1</c> the resolve must ignore whatever the history
    /// holds entirely: the output is the current frame's colour, unmodified.
    /// </summary>
    [SkippableFact]
    public unsafe void ResetHistoryIgnoresTheHistoryEntirely()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        using (var commands = new VulkanCommands(context!))
        using (var textures = new TextureManager(context!, commands))
        {
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();
            using var descriptors = new DescriptorCache(context!);
            ShaderProgramResources program = LoadProgram(context!, compiler, state);

            const float currentR = 0.7f, currentG = 0.3f, currentB = 0.2f;
            var inputs = CreateInputSet(textures);
            UploadFlatRgba16F(textures, inputs.SceneTex, currentR, currentG, currentB, 1f);
            UploadFlatRgba8(textures, inputs.GlowTex, 0, 0, 0, 255);
            UploadFlatR32F(textures, inputs.DepthTex, 0.5f);
            // Motion says "written, no displacement" so the resolve does not
            // fall back to a camera-reprojection path that would need a valid
            // matrix; irrelevant here since reset overrides alpha regardless.
            UploadFlatRgba16F(textures, inputs.MotionTex, 0f, 0f, 0f, 0.5f);
            // History: a completely different colour. If this leaks into the
            // output at all, reset is not doing its job.
            UploadFlatRgba16F(textures, inputs.HistoryColor, 0.1f, 0.9f, 0.1f, 1f);
            UploadFlatRgba8(textures, inputs.HistoryGlow, 0, 0, 0, 255);
            UploadFlatR32F(textures, inputs.HistoryDepth, 0.5f);

            TaaAttachmentSet output = CreateAttachmentSet(textures, targets);

            var uniforms = new TaaUniforms { ResetHistory = 1 };
            ResolveOnce(context!, commands, textures, state, targets, pipelines, program, descriptors,
                inputs, uniforms, output);

            byte[] colorBytes = ReadTextureBytes(context!, commands, textures, output.Color, 8);
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                Assert.InRange(ReadHalf(colorBytes, x, y, 0, 8), currentR - 0.02f, currentR + 0.02f);
                Assert.InRange(ReadHalf(colorBytes, x, y, 1, 8), currentG - 0.02f, currentG + 0.02f);
                Assert.InRange(ReadHalf(colorBytes, x, y, 2, 8), currentB - 0.02f, currentB + 0.02f);
            }

            ValidationAssert.NoErrors(messages);
        }
    }

    /// <summary>
    /// A perfectly static scene (identity camera, zero jitter, zero motion)
    /// converges to the constant current colour as the two history sets are
    /// ping-ponged across many resolves.
    /// </summary>
    [SkippableFact]
    public unsafe void StaticSceneConvergesToTheCurrentColour()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        using (var commands = new VulkanCommands(context!))
        using (var textures = new TextureManager(context!, commands))
        {
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();
            using var descriptors = new DescriptorCache(context!);
            ShaderProgramResources program = LoadProgram(context!, compiler, state);

            const float currentValue = 0.6f;
            var inputs = CreateInputSet(textures);
            UploadFlatRgba16F(textures, inputs.SceneTex, currentValue, currentValue, currentValue, 1f);
            UploadFlatRgba8(textures, inputs.GlowTex, 0, 0, 0, 255);
            UploadFlatR32F(textures, inputs.DepthTex, 0.5f);
            UploadFlatRgba16F(textures, inputs.MotionTex, 0f, 0f, 0f, 0.5f);

            TaaAttachmentSet setA = CreateAttachmentSet(textures, targets);
            TaaAttachmentSet setB = CreateAttachmentSet(textures, targets);
            // Seed A far from the current colour so twenty resolves are a real
            // convergence, not a no-op.
            UploadFlatRgba16F(textures, setA.Color, 0f, 0f, 0f, 1f);
            UploadFlatRgba8(textures, setA.Glow, 0, 0, 0, 255);
            UploadFlatR32F(textures, setA.Depth, 0.5f);

            var uniforms = new TaaUniforms { ResetHistory = 0, BlendAlpha = 0.1f };

            TaaAttachmentSet history = setA, current = setB;
            for (int i = 0; i < 20; i++)
            {
                inputs.HistoryColor = history.Color;
                inputs.HistoryGlow = history.Glow;
                inputs.HistoryDepth = history.Depth;

                ResolveOnce(context!, commands, textures, state, targets, pipelines, program, descriptors,
                    inputs, uniforms, current);

                (history, current) = (current, history);
            }

            // 'history' now holds the last write, since the pair swapped once
            // more than it resolved.
            byte[] colorBytes = ReadTextureBytes(context!, commands, textures, history.Color, 8);
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                Assert.InRange(ReadHalf(colorBytes, x, y, 0, 8), currentValue - 0.02f, currentValue + 0.02f);
                Assert.InRange(ReadHalf(colorBytes, x, y, 1, 8), currentValue - 0.02f, currentValue + 0.02f);
                Assert.InRange(ReadHalf(colorBytes, x, y, 2, 8), currentValue - 0.02f, currentValue + 0.02f);
            }

            ValidationAssert.NoErrors(messages);
        }
    }

    /// <summary>
    /// A uniform +2px motion field reprojects the history two pixels: a bright
    /// band in the history shows up two columns earlier in the output.
    ///
    /// The current frame cannot be perfectly flat here. The resolve rectifies
    /// history against the current frame's own 3x3 neighbourhood before
    /// blending it in (see <c>clipToBox</c> in taa-resolve.fsh) - against a
    /// genuinely flat current, that neighbourhood box has zero width and any
    /// history value that disagrees with it, however it got there, is clipped
    /// back to (effectively) the current colour. That is the clip working as
    /// designed, not a test bug, so the current frame here carries a fine
    /// per-column checker instead of a flat fill: it keeps the local box open
    /// (both a low and a high value are always present in every 3x3 window)
    /// without giving the resolve any large-scale feature of its own, so a
    /// reprojected history feature is what a regional average actually shows.
    /// </summary>
    [SkippableFact]
    public unsafe void UniformMotionReprojectsTheHistoryByThatOffset()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        using (var commands = new VulkanCommands(context!))
        using (var textures = new TextureManager(context!, commands))
        {
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();
            using var descriptors = new DescriptorCache(context!);
            ShaderProgramResources program = LoadProgram(context!, compiler, state);

            var inputs = CreateInputSet(textures);
            // Per-column checker: every 3-wide window has both 0.3 and 0.7, so
            // clipToBox never degenerates to a point, but no column carries a
            // feature of its own for the assertions to confuse with history's.
            UploadRgba16F(textures, inputs.SceneTex, (x, _) => (x % 2 == 0) ? 0.3f : 0.7f,
                (x, _) => (x % 2 == 0) ? 0.3f : 0.7f, (x, _) => (x % 2 == 0) ? 0.3f : 0.7f, (_, _) => 1f);
            UploadFlatRgba8(textures, inputs.GlowTex, 0, 0, 0, 255);
            UploadFlatR32F(textures, inputs.DepthTex, 0.5f);
            // Uniform +2px motion, written (matches depth) everywhere.
            UploadFlatRgba16F(textures, inputs.MotionTex, 2f, 0f, 0f, 0.5f);

            const int stripeStart = 14, stripeWidth = 4;
            const float background = 0.5f, stripe = 1.0f;
            UploadRgba16F(textures, inputs.HistoryColor,
                (x, _) => x is >= stripeStart and < stripeStart + stripeWidth ? stripe : background,
                (x, _) => x is >= stripeStart and < stripeStart + stripeWidth ? stripe : background,
                (x, _) => x is >= stripeStart and < stripeStart + stripeWidth ? stripe : background,
                (_, _) => 1f);
            UploadFlatRgba8(textures, inputs.HistoryGlow, 0, 0, 0, 255);
            UploadFlatR32F(textures, inputs.HistoryDepth, 0.5f);

            TaaAttachmentSet output = CreateAttachmentSet(textures, targets);
            // Mostly history, so the reprojected band dominates the blend.
            var uniforms = new TaaUniforms { ResetHistory = 0, BlendAlpha = 0.05f };

            ResolveOnce(context!, commands, textures, state, targets, pipelines, program, descriptors,
                inputs, uniforms, output);

            byte[] colorBytes = ReadTextureBytes(context!, commands, textures, output.Color, 8);

            // Reading history at (pixelCentre + mv) means an output column c
            // sees history column c + 2: the bright band at history columns
            // [14,18) should appear at output columns [12,16).
            float expectedBandAverage = AverageRed(colorBytes, stripeStart - 2, stripeStart - 2 + stripeWidth);
            // A control window far from both the source and destination bands,
            // still reading flat history background wherever it samples.
            float controlAverage = AverageRed(colorBytes, 24, 28);

            _output.WriteLine($"reprojected band average={expectedBandAverage}, control average={controlAverage}");
            Assert.True(expectedBandAverage > controlAverage + 0.1f,
                $"expected the reprojected band (avg {expectedBandAverage}) to read clearly brighter " +
                $"than the control window (avg {controlAverage})");

            ValidationAssert.NoErrors(messages);
        }
    }

    /// <summary>
    /// A history pixel far outside the current frame's neighbourhood range -
    /// a stale ghost, a lighting spike - is clipped back toward that
    /// neighbourhood rather than blended in at full strength.
    /// </summary>
    [SkippableFact]
    public unsafe void AnOutlierHistoryValueIsClippedTowardTheNeighbourhood()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        using (var commands = new VulkanCommands(context!))
        using (var textures = new TextureManager(context!, commands))
        {
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var compiler = new ShaderCompiler();
            using var descriptors = new DescriptorCache(context!);
            ShaderProgramResources program = LoadProgram(context!, compiler, state);

            const float currentValue = 0.5f;
            var inputs = CreateInputSet(textures);
            UploadFlatRgba16F(textures, inputs.SceneTex, currentValue, currentValue, currentValue, 1f);
            UploadFlatRgba8(textures, inputs.GlowTex, 0, 0, 0, 255);
            UploadFlatR32F(textures, inputs.DepthTex, 0.5f);
            UploadFlatRgba16F(textures, inputs.MotionTex, 0f, 0f, 0f, 0.5f);

            // A magenta outlier, nothing like the current frame's flat grey.
            const float outlierR = 1.0f, outlierG = 0.0f, outlierB = 1.0f;
            UploadFlatRgba16F(textures, inputs.HistoryColor, outlierR, outlierG, outlierB, 1f);
            UploadFlatRgba8(textures, inputs.HistoryGlow, 0, 0, 0, 255);
            UploadFlatR32F(textures, inputs.HistoryDepth, 0.5f);

            TaaAttachmentSet output = CreateAttachmentSet(textures, targets);
            // Even with heavy history weight, the clip should dominate.
            var uniforms = new TaaUniforms { ResetHistory = 0, BlendAlpha = 0.5f };

            ResolveOnce(context!, commands, textures, state, targets, pipelines, program, descriptors,
                inputs, uniforms, output);

            byte[] colorBytes = ReadTextureBytes(context!, commands, textures, output.Color, 8);
            float r = ReadHalf(colorBytes, (int)Size / 2, (int)Size / 2, 0, 8);
            float g = ReadHalf(colorBytes, (int)Size / 2, (int)Size / 2, 1, 8);
            float b = ReadHalf(colorBytes, (int)Size / 2, (int)Size / 2, 2, 8);
            _output.WriteLine($"resolved=({r},{g},{b}) current=({currentValue}) outlier=({outlierR},{outlierG},{outlierB})");

            Assert.InRange(r, currentValue - 0.1f, currentValue + 0.1f);
            Assert.InRange(g, currentValue - 0.1f, currentValue + 0.1f);
            Assert.InRange(b, currentValue - 0.1f, currentValue + 0.1f);
            // Clearly not the raw outlier, in at least the channel it disagrees
            // with the current frame the most.
            Assert.True(MathF.Abs(g - outlierG) > 0.3f, "the outlier's green channel should have been clipped away");

            ValidationAssert.NoErrors(messages);
        }
    }

    // ------------------------------------------------------------------ setup

    private static ShaderProgramResources LoadProgram(
        VulkanContext context, ShaderCompiler compiler, GlStateTracker state)
    {
        Dictionary<string, string> files = ShaderCorpus.LoadShaderFiles();
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();
        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
            "taa-resolve", files, includes, ShaderCorpus.Variants().First());
        Assert.NotEmpty(stages);

        TranslatedProgram translated = ShaderTranslator.Translate(stages, compiler);
        Assert.True(translated.Success, string.Join("; ", translated.Errors));

        var program = new ShaderProgramResources(context, 1, translated);
        state.SetProgram(1);
        return program;
    }

    /// <summary>The seven sampler inputs the resolve declares.</summary>
    private sealed class TaaInputSet
    {
        public int SceneTex;
        public int GlowTex;
        public int MotionTex;
        public int DepthTex;
        public int HistoryColor;
        public int HistoryGlow;
        public int HistoryDepth;
    }

    /// <summary>One MRT write target: colour history, aux/glow, linear depth.</summary>
    private sealed class TaaAttachmentSet
    {
        public int Color;
        public int Glow;
        public int Depth;
        public int Framebuffer;
    }

    private sealed class TaaUniforms
    {
        public float[] RenderSize = { Size, Size };
        public float[] JitterPx = { 0f, 0f };
        public float[] InvViewProjJittered = Identity4;
        public float[] PrevViewProj = Identity4;
        public float[] ViewMatrix = Identity4;
        public float[] CameraDelta = { 0f, 0f, 0f };
        public int ResetHistory;
        public float BlendAlpha = 0.1f;
        public float VarianceGamma = 1.25f;
    }

    private static TaaInputSet CreateInputSet(TextureManager textures) => new()
    {
        SceneTex = textures.Create(Size, Size, Format.R16G16B16A16Sfloat),
        GlowTex = textures.Create(Size, Size, Format.R8G8B8A8Unorm),
        MotionTex = textures.Create(Size, Size, Format.R16G16B16A16Sfloat),
        DepthTex = textures.Create(Size, Size, Format.R32Sfloat),
        HistoryColor = textures.Create(Size, Size, Format.R16G16B16A16Sfloat),
        HistoryGlow = textures.Create(Size, Size, Format.R8G8B8A8Unorm),
        HistoryDepth = textures.Create(Size, Size, Format.R32Sfloat),
    };

    private static TaaAttachmentSet CreateAttachmentSet(TextureManager textures, RenderTargetManager targets)
    {
        var set = new TaaAttachmentSet
        {
            Color = textures.Create(Size, Size, Format.R16G16B16A16Sfloat),
            Glow = textures.Create(Size, Size, Format.R8G8B8A8Unorm),
            Depth = textures.Create(Size, Size, Format.R32Sfloat),
        };
        set.Framebuffer = targets.Create(Size, Size);
        targets.Attach(set.Framebuffer, 0, set.Color);
        targets.Attach(set.Framebuffer, 1, set.Glow);
        targets.Attach(set.Framebuffer, 2, set.Depth);
        targets.SetDrawBuffers(set.Framebuffer, 0b111);
        return set;
    }

    // ------------------------------------------------------------------- draw

    /// <summary>
    /// One resolve pass: writes the shadow buffer, uploads it to a dedicated
    /// dynamic-uniform-buffer, builds set 0 (uniforms) and set 1 (samplers) by
    /// hand through a private <see cref="DescriptorCache" />, and draws the
    /// fullscreen triangle - the same three-step shape <c>VulkanDevice</c>'s own
    /// draw path follows, scoped to a single named-uniform, named-sampler pass.
    /// </summary>
    private static unsafe void ResolveOnce(
        VulkanContext context, VulkanCommands commands, TextureManager textures, GlStateTracker state,
        RenderTargetManager targets, GraphicsPipelineCache pipelines, ShaderProgramResources program,
        DescriptorCache descriptors, TaaInputSet inputs, TaaUniforms uniforms, TaaAttachmentSet output)
    {
        SetUniformFloats(program, "renderSize", uniforms.RenderSize);
        SetUniformFloats(program, "jitterPx", uniforms.JitterPx);
        SetUniformFloats(program, "invViewProjJittered", uniforms.InvViewProjJittered);
        SetUniformFloats(program, "prevViewProj", uniforms.PrevViewProj);
        SetUniformFloats(program, "viewMatrix", uniforms.ViewMatrix);
        SetUniformFloats(program, "cameraDelta", uniforms.CameraDelta);
        SetUniformInt(program, "resetHistory", uniforms.ResetHistory);
        SetUniformFloats(program, "blendAlpha", new[] { uniforms.BlendAlpha });
        SetUniformFloats(program, "varianceGamma", new[] { uniforms.VarianceGamma });

        ulong shadowSize = (ulong)Math.Max(program.UniformShadow.Length, 16);
        using var uniformBuffer = new VulkanBuffer(context, shadowSize,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        fixed (byte* source = program.UniformShadow)
        {
            System.Buffer.MemoryCopy(source, (void*)uniformBuffer.Mapped,
                (long)shadowSize, program.UniformShadow.Length);
        }

        var textureByName = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sceneTex"] = inputs.SceneTex,
            ["glowTex"] = inputs.GlowTex,
            ["motionTex"] = inputs.MotionTex,
            ["depthTex"] = inputs.DepthTex,
            ["historyColor"] = inputs.HistoryColor,
            ["historyGlow"] = inputs.HistoryGlow,
            ["historyDepth"] = inputs.HistoryDepth,
        };

        var samplerState = SamplerState.Default with
        {
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressU = SamplerAddressMode.ClampToEdge,
            AddressV = SamplerAddressMode.ClampToEdge,
        };

        var samplerBindings = new SamplerBindingValue[program.Interface.Samplers.Count];
        var sampledTextures = new VulkanTexture[samplerBindings.Length];
        for (int i = 0; i < samplerBindings.Length; i++)
        {
            SamplerBinding declared = program.Interface.Samplers[i];
            VulkanTexture texture = textures.Get(textureByName[declared.Name])
                ?? throw new InvalidOperationException("no texture bound for sampler '" + declared.Name + "'");
            sampledTextures[i] = texture;
            Sampler samplerHandle = textures.Samplers.Get(samplerState);
            samplerBindings[i] = new SamplerBindingValue((uint)declared.Binding, texture.View, samplerHandle, texture.Id);
        }

        VulkanFramebuffer bound = targets.Get(output.Framebuffer)!;
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

            // Layout transitions cannot happen inside a rendering scope, so
            // every sampled texture - including a previous iteration's output,
            // still in ColorAttachmentOptimal - is put right before it opens.
            foreach (VulkanTexture texture in sampledTextures)
            {
                textures.TransitionTexture(commandBuffer, texture, ImageLayout.ShaderReadOnlyOptimal);
            }

            targets.Bind(commandBuffer, output.Framebuffer);
            targets.EnsureRendering(commandBuffer);

            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

            var viewport = new Viewport(0, 0, Size, Size, 0, 1);
            api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(Size, Size));
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

            if (program.Interface.HasUniformBlock)
            {
                var uniformContents = new DescriptorSetContents(
                    program.ProgramId, ProgramInterfaceLayout.DefaultBlockSet,
                    Array.Empty<SamplerBindingValue>(),
                    new[]
                    {
                        new BufferBindingValue(ProgramInterfaceLayout.DefaultBlockBinding,
                            uniformBuffer.Handle, 0, (ulong)program.UniformShadow.Length, uniformBuffer.Id),
                    });
                DescriptorSet uniformSet = descriptors.Get(
                    uniformContents, program.SetLayouts[ProgramInterfaceLayout.DefaultBlockSet]);
                uint dynamicOffset = 0;
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                    ProgramInterfaceLayout.DefaultBlockSet, 1, &uniformSet, 1, &dynamicOffset);
            }

            if (samplerBindings.Length > 0)
            {
                var samplerContents = new DescriptorSetContents(
                    program.ProgramId, ProgramInterfaceLayout.SamplerSet,
                    samplerBindings, Array.Empty<BufferBindingValue>());
                DescriptorSet samplerSet = descriptors.Get(
                    samplerContents, program.SetLayouts[ProgramInterfaceLayout.SamplerSet]);
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                    ProgramInterfaceLayout.SamplerSet, 1, &samplerSet, 0, null);
            }

            api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            targets.EndRendering(commandBuffer);
        });
    }

    private static void SetUniformFloats(ShaderProgramResources program, string name, float[] values)
    {
        int location = program.LocationOf(name);
        if (location < 0) return;

        var bytes = new byte[values.Length * sizeof(float)];
        for (int i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float), sizeof(float)), values[i]);
        }
        program.SetUniform(location, bytes);
    }

    private static void SetUniformInt(ShaderProgramResources program, string name, int value)
    {
        int location = program.LocationOf(name);
        if (location < 0) return;
        program.SetUniform(location, BitConverter.GetBytes(value));
    }

    // --------------------------------------------------------------- textures

    private static unsafe void UploadFlatRgba16F(
        TextureManager textures, int textureId, float r, float g, float b, float a) =>
        UploadRgba16F(textures, textureId, (_, _) => r, (_, _) => g, (_, _) => b, (_, _) => a);

    private static unsafe void UploadRgba16F(
        TextureManager textures, int textureId,
        Func<int, int, float> r, Func<int, int, float> g, Func<int, int, float> b, Func<int, int, float> a)
    {
        var data = new Half[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int i = (y * (int)Size + x) * 4;
            data[i] = (Half)r(x, y);
            data[i + 1] = (Half)g(x, y);
            data[i + 2] = (Half)b(x, y);
            data[i + 3] = (Half)a(x, y);
        }
        fixed (Half* pixels = data)
        {
            textures.Upload(textureId, 0, 0, 0, Size, Size, (IntPtr)pixels, 8);
        }
    }

    private static unsafe void UploadFlatRgba8(
        TextureManager textures, int textureId, byte r, byte g, byte b, byte a)
    {
        var data = new byte[Size * Size * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }
        fixed (byte* pixels = data)
        {
            textures.Upload(textureId, 0, 0, 0, Size, Size, (IntPtr)pixels, 4);
        }
    }

    private static unsafe void UploadFlatR32F(TextureManager textures, int textureId, float value)
    {
        var data = new float[Size * Size];
        Array.Fill(data, value);
        fixed (float* pixels = data)
        {
            textures.Upload(textureId, 0, 0, 0, Size, Size, (IntPtr)pixels, 4);
        }
    }

    // --------------------------------------------------------------- readback

    private static unsafe byte[] ReadTextureBytes(
        VulkanContext context, VulkanCommands commands, TextureManager textures, int textureId, int bytesPerPixel)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)Size * Size * (ulong)bytesPerPixel;

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(texture.Aspect, 0, 0, 1),
                ImageExtent = new Extent3D(Size, Size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }

    private static float ReadHalf(byte[] data, int x, int y, int channel, int bytesPerPixel)
    {
        int offset = (y * (int)Size + x) * bytesPerPixel + channel * 2;
        return (float)BitConverter.ToHalf(data, offset);
    }

    /// <summary>Average red channel over columns [startX, endX) across every row.</summary>
    private static float AverageRed(byte[] colorBytes, int startX, int endX)
    {
        float sum = 0f;
        int count = 0;
        for (int y = 0; y < Size; y++)
        for (int x = startX; x < endX; x++)
        {
            sum += ReadHalf(colorBytes, x, y, 0, 8);
            count++;
        }
        return sum / count;
    }
}
