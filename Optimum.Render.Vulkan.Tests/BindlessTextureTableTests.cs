using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Plan decision 9's set 1: the bindless texture table and the shared pipeline
/// layout. The slot bookkeeping is judged without a device (LIFO reuse, slot 0
/// reserved, retirement held until its Frame value completes, keys and kinds); the
/// device tests sample slots from a shader that includes bindings.glsl, across
/// presented frames, and read back only at the end.
/// </summary>
public class BindlessTextureTableTests
{
    private readonly ITestOutputHelper _output;

    public BindlessTextureTableTests(ITestOutputHelper output) => _output = output;

    private sealed class FakeClock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    private static uint[] Capacities(uint each)
    {
        var capacities = new uint[BindlessKinds.Count];
        Array.Fill(capacities, each);
        return capacities;
    }

    private static BindlessSlotKey Key(ulong texture, SamplerState? state = null,
        ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal, TextureKind kind = TextureKind.Texture2D) =>
        new(texture, kind, state ?? SamplerState.Default, layout);

    // ------------------------------------------------------------ allocator

    [Fact]
    public void SlotZeroIsNeverHandedOutAndFreedSlotsComeBackLastInFirstOut()
    {
        var allocator = new BindlessSlotAllocator(8);
        var handed = new List<uint>();
        for (int i = 0; i < 7; i++)
        {
            Assert.True(allocator.TryAllocate(out uint slot));
            handed.Add(slot);
        }
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7 }, handed);
        Assert.False(allocator.TryAllocate(out uint none));
        Assert.Equal(0u, none);

        allocator.Free(3);
        allocator.Free(6);
        Assert.True(allocator.TryAllocate(out uint first));
        Assert.True(allocator.TryAllocate(out uint second));
        Assert.Equal(6u, first);
        Assert.Equal(3u, second);
        Assert.Equal(7, allocator.Live);

        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Free(0));
    }

    // ----------------------------------------------------------------- book

    [Fact]
    public void ARetiredSlotIsNotReusedBeforeItsFrameValueCompletes()
    {
        var clock = new FakeClock { FrameRecorded = 5, FrameCompleted = 3 };
        var book = new BindlessSlotBook(clock, Capacities(4));

        uint retired = book.Acquire(Key(100), out bool created);
        Assert.True(created);
        Assert.Equal(1u, retired);
        Assert.Equal(1, book.Release(100));
        Assert.Equal(1, book.PendingRetirements);

        var freed = new List<(TextureKind, uint)>();
        clock.FrameCompleted = 4;
        Assert.Equal(0, book.Collect(freed));
        Assert.Empty(freed);

        // Frame 5 may still sample slot 1: a new texture gets another slot.
        uint other = book.Acquire(Key(101), out _);
        Assert.NotEqual(retired, other);
        Assert.NotEqual(0u, other);

        clock.FrameCompleted = 5;
        Assert.Equal(1, book.Collect(freed));
        Assert.Equal(new[] { (TextureKind.Texture2D, retired) }, freed);
        Assert.Equal(0, book.PendingRetirements);

        Assert.Equal(retired, book.Acquire(Key(102), out bool reused));
        Assert.True(reused);
    }

    [Fact]
    public void AChangedSamplerStateOrLayoutGetsANewSlotAndAnUnchangedKeyKeepsItsSlot()
    {
        var clock = new FakeClock();
        var book = new BindlessSlotBook(clock, Capacities(16));

        uint plain = book.Acquire(Key(7), out bool created);
        Assert.True(created);
        Assert.Equal(plain, book.Acquire(Key(7), out bool again));
        Assert.False(again);

        uint linear = book.Acquire(Key(7, SamplerState.Default with { MagFilter = Filter.Linear }), out bool linearCreated);
        uint depthReadOnly = book.Acquire(Key(7, layout: ImageLayout.DepthReadOnlyOptimal), out bool layoutCreated);
        Assert.True(linearCreated);
        Assert.True(layoutCreated);
        Assert.Equal(3, new HashSet<uint> { plain, linear, depthReadOnly }.Count);

        // The same state in another kind's array is another slot of that array.
        uint arrayed = book.Acquire(Key(7, kind: TextureKind.Texture2DArray), out _);
        Assert.Equal(1, book.LiveSlots(TextureKind.Texture2DArray));
        Assert.Equal(1u, arrayed);
        Assert.Equal(0, book.PendingRetirements);
    }

    [Fact]
    public void PastTheVariantCapTheLeastRecentlyUsedKeyOfATextureRetires()
    {
        var clock = new FakeClock { FrameRecorded = 1 };
        var book = new BindlessSlotBook(clock, Capacities(32));

        var states = new SamplerState[BindlessSlotBook.MaxVariantsPerTexture + 1];
        for (int i = 0; i < states.Length; i++) states[i] = SamplerState.Default with { LodBias = i };

        uint oldest = book.Acquire(Key(9, states[0]), out _);
        for (int i = 1; i < BindlessSlotBook.MaxVariantsPerTexture; i++) book.Acquire(Key(9, states[i]), out _);
        // Touch the oldest: it becomes the most recently used, states[1] the least.
        Assert.Equal(oldest, book.Acquire(Key(9, states[0]), out _));
        Assert.Equal(0, book.PendingRetirements);

        book.Acquire(Key(9, states[^1]), out _);
        Assert.Equal(1, book.PendingRetirements);
        Assert.Equal(oldest, book.Acquire(Key(9, states[0]), out bool kept));
        Assert.False(kept);
        book.Acquire(Key(9, states[1]), out bool recreated);
        Assert.True(recreated);
    }

    [Fact]
    public void AFullArrayResolvesToThePlaceholderAndCountsIt()
    {
        var book = new BindlessSlotBook(new FakeClock(), Capacities(2));
        Assert.Equal(1u, book.Acquire(Key(1), out _));
        Assert.Equal(0u, book.Acquire(Key(2), out bool created));
        Assert.False(created);
        Assert.Equal(1, book.Exhausted);
    }

    // ---------------------------------------------------------------- kinds

    [Fact]
    public void KindsFollowTheConventionTable()
    {
        Assert.Equal(SetConvention.TextureArrays.Length, BindlessKinds.Count);
        Assert.Equal(BindlessKinds.Count, Enum.GetValues<TextureKind>().Length);
        for (int i = 0; i < SetConvention.TextureArrays.Length; i++)
        {
            SetConvention.Binding binding = SetConvention.TextureArrays[i];
            Assert.True(BindlessKinds.TryFromGlslType(binding.GlslType, out TextureKind kind));
            Assert.Equal((TextureKind)i, kind);
            Assert.Equal((uint)binding.Value, BindlessKinds.BindingOf(kind));
            Assert.Equal(binding.GlslType.Contains("Shadow", StringComparison.Ordinal), BindlessKinds.IsShadow(kind));
            Assert.Equal(binding.GlslType[0] is 'u' or 'i', BindlessKinds.IsInteger(kind));
        }
        Assert.False(BindlessKinds.TryFromGlslType("sampler1D", out _));
        Assert.False(BindlessKinds.TryFromGlslType("sampler2DMS", out _));
    }

    [Theory]
    // Colour 2D.
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, false, (int)TextureKind.Texture2D, true)]
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, false, (int)TextureKind.Texture2DArray, false)]
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, false, (int)TextureKind.TextureCube, false)]
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, false, (int)TextureKind.Texture3D, false)]
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, false, (int)TextureKind.Shadow2D, false)]
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, false, (int)TextureKind.UnsignedTexture2D, false)]
    // Arrays, cubes, volumes.
    [InlineData(Format.R16G16B16A16Sfloat, 3u, false, false, (int)TextureKind.Texture2DArray, true)]
    [InlineData(Format.R16G16B16A16Sfloat, 3u, false, false, (int)TextureKind.Texture2D, false)]
    [InlineData(Format.R8G8B8A8Unorm, 6u, true, false, (int)TextureKind.TextureCube, true)]
    [InlineData(Format.R8G8B8A8Unorm, 6u, true, false, (int)TextureKind.Texture2DArray, false)]
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, true, (int)TextureKind.Texture3D, true)]
    [InlineData(Format.R8G8B8A8Unorm, 1u, false, true, (int)TextureKind.Texture2D, false)]
    // Depth: through sampler2D as on GL, and behind every shadow kind of its shape.
    [InlineData(Format.D32Sfloat, 1u, false, false, (int)TextureKind.Texture2D, true)]
    [InlineData(Format.D32Sfloat, 1u, false, false, (int)TextureKind.Shadow2D, true)]
    [InlineData(Format.D32Sfloat, 1u, false, false, (int)TextureKind.Shadow2DArray, false)]
    [InlineData(Format.D24UnormS8Uint, 2u, false, false, (int)TextureKind.Shadow2DArray, true)]
    [InlineData(Format.D32Sfloat, 6u, true, false, (int)TextureKind.ShadowCube, true)]
    [InlineData(Format.D32Sfloat, 1u, false, false, (int)TextureKind.SignedTexture2D, false)]
    // Integer formats only behind the matching signedness.
    [InlineData(Format.R32Uint, 1u, false, false, (int)TextureKind.UnsignedTexture2D, true)]
    [InlineData(Format.R32Uint, 1u, false, false, (int)TextureKind.SignedTexture2D, false)]
    [InlineData(Format.R32Uint, 1u, false, false, (int)TextureKind.Texture2D, false)]
    [InlineData(Format.R16Sint, 1u, false, false, (int)TextureKind.SignedTexture2D, true)]
    [InlineData(Format.R16Sint, 1u, false, false, (int)TextureKind.UnsignedTexture2D, false)]
    public void KindDerivationFollowsFormatAndViewShape(Format format, uint layers, bool cube, bool volume,
        int kind, bool suits)
    {
        Assert.Equal(suits, BindlessKinds.Suits(new TextureShape(format, layers, cube, volume), (TextureKind)kind));
    }

    [Fact]
    public void TheSlotStateComparesExactlyWhenTheDeclarationDoesAndSamplesIntegersNearest()
    {
        SamplerState linearCompare = SamplerState.Default with
        {
            MagFilter = Filter.Linear, MinFilter = Filter.Linear, MipmapMode = SamplerMipmapMode.Linear,
            CompareEnable = true, MaxAnisotropy = 8f,
        };
        Assert.False(BindlessKinds.EffectiveState(linearCompare, TextureKind.Texture2D).CompareEnable);
        Assert.True(BindlessKinds.EffectiveState(SamplerState.Default, TextureKind.Shadow2D).CompareEnable);
        Assert.Equal(Filter.Linear, BindlessKinds.EffectiveState(linearCompare, TextureKind.Shadow2D).MagFilter);

        SamplerState integer = BindlessKinds.EffectiveState(linearCompare, TextureKind.UnsignedTexture2D);
        Assert.Equal(Filter.Nearest, integer.MagFilter);
        Assert.Equal(Filter.Nearest, integer.MinFilter);
        Assert.Equal(SamplerMipmapMode.Nearest, integer.MipmapMode);
        Assert.Equal(1f, integer.MaxAnisotropy);
        Assert.False(integer.CompareEnable);
    }

    [Fact]
    public void CapacitiesAreTheConventionSizesAtTheFloorAndScaleDownBelowIt()
    {
        var atFloor = new DescriptorIndexingSupport(true, true, true, true,
            DescriptorIndexingFloor.RequiredSampledImages, DescriptorIndexingFloor.RequiredSampledImages,
            DescriptorIndexingFloor.RequiredSampledImages, DescriptorIndexingFloor.RequiredSampledImages, 1, 128);
        uint[] full = BindlessKinds.ClampCapacities(atFloor, DescriptorIndexingFloor.FrameTextures);
        for (int i = 0; i < full.Length; i++) Assert.Equal(SetConvention.TextureArrays[i].Capacity, full[i]);

        uint[] halved = BindlessKinds.ClampCapacities(
            atFloor with { MaxDescriptorSetUpdateAfterBindSamplers = DescriptorIndexingFloor.RequiredSampledImages / 2 },
            DescriptorIndexingFloor.FrameTextures);
        ulong total = 0;
        foreach (uint capacity in halved)
        {
            Assert.True(capacity >= 2);
            total += capacity;
        }
        Assert.True(total <= DescriptorIndexingFloor.RequiredSampledImages / 2 - DescriptorIndexingFloor.FrameTextures);
        Assert.True(halved[(int)TextureKind.Texture2D] > halved[(int)TextureKind.ShadowCube]);
    }

    // ---------------------------------------------------------------- device

    private const int Size = 4;
    private const int GlRgba8 = 0x8058;
    private const int GlDepth32F = 0x8CAC;

    private static readonly byte[] Red = { 255, 0, 0, 255 };
    private static readonly byte[] Green = { 0, 255, 0, 255 };
    private static readonly byte[] Blue = { 0, 0, 255, 255 };
    private static readonly byte[] Magenta = { 255, 0, 255, 255 };
    private static readonly byte[] OpaqueBlack = { 0, 0, 0, 255 };

    private const string VertexSource = """
        #version 450
        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string FragmentBody = """

        layout(push_constant) uniform Push
        {
            uint slot;
            uint kind;
            float reference;
        } pc;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            if (pc.kind == 6u)
            {
                float lit = texture(optimumTextures2DShadow[pc.slot], vec3(0.5, 0.5, pc.reference));
                outColor = vec4(lit, 0.0, 0.0, 1.0);
            }
            else
            {
                outColor = texture(optimumTextures2D[pc.slot], vec2(0.5));
            }
        }
        """;

    /// <summary>A headless device with a pipeline on the shared layout that samples set 1 by push-constant slot.</summary>
    private sealed unsafe class Harness : IDisposable
    {
        public VulkanDevice Device = null!;
        public Pipeline Pipeline;
        private ShaderModule _vertex;
        private ShaderModule _fragment;

        public BindlessTextureTable Table => Device.BindlessForTests;

        public void Dispose()
        {
            VulkanContext context = Device.ContextForTests;
            if (context != null)
            {
                // The last frame may still name the pipeline.
                VulkanStats.WaitDeviceIdle(context.Api, context.Device);
                if (Pipeline.Handle != 0) context.Api.DestroyPipeline(context.Device, Pipeline, null);
                if (_vertex.Handle != 0) context.Api.DestroyShaderModule(context.Device, _vertex, null);
                if (_fragment.Handle != 0) context.Api.DestroyShaderModule(context.Device, _fragment, null);
            }
            Device.Dispose();
        }

        public void CreatePipeline(ShaderCompiler compiler)
        {
            string include = File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, SetConvention.IncludePath))
                .Replace("\r\n", "\n");
            ShaderCompileResult vertex = compiler.Compile(VertexSource, "bindless-probe.vert", EnumShaderType.VertexShader);
            Assert.True(vertex.Success, vertex.Error);
            ShaderCompileResult fragment = compiler.Compile("#version 450\n" + include + FragmentBody,
                "bindless-probe.frag", EnumShaderType.FragmentShader);
            Assert.True(fragment.Success, fragment.Error);

            VulkanContext context = Device.ContextForTests;
            _vertex = Module(context, vertex.Spirv);
            _fragment = Module(context, fragment.Spirv);

            byte* entry = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit, Module = _vertex, PName = entry,
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit, Module = _fragment, PName = entry,
                };
                var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
                };
                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise, LineWidth = 1f,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var attachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                };
                var blend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment,
                };
                DynamicState* dynamics = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamic = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamics,
                };
                Format colour = Format.R8G8B8A8Unorm;
                var rendering = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &colour,
                };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &rendering,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewport,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PColorBlendState = &blend,
                    PDynamicState = &dynamic,
                    Layout = Device.SharedLayoutForTests.Layout,
                };
                Pipeline pipeline;
                Assert.Equal(Result.Success,
                    context.Api.CreateGraphicsPipelines(context.Device, default, 1, &info, null, &pipeline));
                Pipeline = pipeline;
            }
            finally
            {
                SilkMarshal.Free((nint)entry);
            }
        }

        private static ShaderModule Module(VulkanContext context, byte[] spirv)
        {
            fixed (byte* code = spirv)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code,
                };
                ShaderModule module;
                Assert.Equal(Result.Success, context.Api.CreateShaderModule(context.Device, &info, null, &module));
                return module;
            }
        }

        public int Texture(byte[] texel)
        {
            fixed (byte* pixels = texel) return Device.CreateTexture2DRaw(1, 1, GlRgba8, (IntPtr)pixels, 4);
        }

        public int DepthTexture(float depth) => Device.CreateTexture2DRaw(1, 1, GlDepth32F, (IntPtr)(&depth), 4);

        /// <summary>A colour texture with a framebuffer around it; returns both.</summary>
        public (int Texture, int Framebuffer) Target()
        {
            int texture = Device.CreateTexture2DRaw(Size, Size, GlRgba8, IntPtr.Zero, 4);
            int framebuffer = Device.CreateFramebuffer(Size, Size);
            Device.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            Device.SetDrawBuffers(framebuffer, 1);
            return (texture, framebuffer);
        }

        /// <summary>Clears the target and draws the probe sampling <paramref name="slot" /> of <paramref name="kind" />'s array.</summary>
        public void Draw((int Texture, int Framebuffer) target, uint slot, TextureKind kind = TextureKind.Texture2D,
            float reference = 0f, params int[] sampled)
        {
            Device.BindFramebuffer(target.Framebuffer);
            Device.ClearColor(0, 0f, 0f, 0f, 1f);
            var push = new byte[12];
            BitConverter.TryWriteBytes(push.AsSpan(0, 4), slot);
            BitConverter.TryWriteBytes(push.AsSpan(4, 4), (uint)kind);
            BitConverter.TryWriteBytes(push.AsSpan(8, 4), reference);
            Device.DrawBindlessForTests(Pipeline, Size, Size, push, sampled);
        }

        public byte[] Pixel((int Texture, int Framebuffer) target) => Device.ReadBackLevel0ForTests(target.Texture)[..4];
    }

    private bool TryCreateHarness(bool poison, out Harness? harness, out ShaderCompiler? compiler)
    {
        harness = null;
        compiler = null;
        try
        {
            compiler = new ShaderCompiler();
        }
        catch (Exception error) when (error is DllNotFoundException or InvalidOperationException)
        {
            _output.WriteLine("shaderc unavailable: " + error.Message);
            return false;
        }

        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? configure = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options =>
        {
            configure?.Invoke(options);
            options.Poison = poison;
        };
        if (!device.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            _output.WriteLine("Vulkan unavailable: " + failureReason);
            device.Dispose();
            compiler.Dispose();
            compiler = null;
            return false;
        }

        harness = new Harness { Device = device };
        harness.CreatePipeline(compiler);
        return true;
    }

    /// <summary>
    /// Two textures resolve to two slots of the same set, and one frame samples
    /// both through the same pipeline and bound set: each target shows its own
    /// texture. The table comes up at the convention's sizes with one layout per set.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoTexturesInOneSetSampleTheirOwnColoursInOneFrame(bool poison)
    {
        Skip.IfNot(TryCreateHarness(poison, out Harness? harness, out ShaderCompiler? compiler), "No usable Vulkan device or shaderc.");
        using (compiler)
        using (harness)
        {
            VulkanDevice device = harness!.Device;
            BindlessTextureTable table = harness.Table;
            Assert.Equal(poison, device.ContextForTests.PoisonFreshResources);
            foreach (TextureKind kind in Enum.GetValues<TextureKind>())
            {
                Assert.Equal(BindlessKinds.ConventionCapacity(kind), table.CapacityOf(kind));
            }
            SharedPipelineLayout shared = device.SharedLayoutForTests;
            Assert.NotEqual(0ul, shared.Layout.Handle);
            Assert.Equal(table.Layout.Handle, shared.TextureSetLayout.Handle);

            int red = harness.Texture(Red);
            int green = harness.Texture(Green);
            var first = harness.Target();
            var second = harness.Target();

            device.BeginFrame();
            uint redSlot = table.Resolve(red, TextureKind.Texture2D, SamplerState.Default);
            uint greenSlot = table.Resolve(green, TextureKind.Texture2D, SamplerState.Default);
            Assert.NotEqual(0u, redSlot);
            Assert.NotEqual(0u, greenSlot);
            Assert.NotEqual(redSlot, greenSlot);
            Assert.Equal(2, table.PendingWrites);
            harness.Draw(first, redSlot, sampled: red);
            harness.Draw(second, greenSlot, sampled: green);
            device.Present();
            Assert.Equal(0, table.PendingWrites);

            Assert.Equal(Red, harness.Pixel(first));
            Assert.Equal(Green, harness.Pixel(second));
            Assert.Equal(2, table.LiveSlots(TextureKind.Texture2D));
            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// A texture is deleted and its GL id goes to a new texture. The new texture gets
    /// a new slot; the old slot keeps serving nothing new until the Frame value of the
    /// deletion completes, then holds the placeholder, over several presented frames
    /// with no readback among them; and the next texture reuses it.
    /// </summary>
    [SkippableFact]
    public void ADeletedTexturesSlotHoldsThePlaceholderUntilReusedWhileItsGlIdServesTheNewTexture()
    {
        Skip.IfNot(TryCreateHarness(false, out Harness? harness, out ShaderCompiler? compiler), "No usable Vulkan device or shaderc.");
        using (compiler)
        using (harness)
        {
            VulkanDevice device = harness!.Device;
            BindlessTextureTable table = harness.Table;
            var retiredTarget = harness.Target();
            var liveTarget = harness.Target();

            int red = harness.Texture(Red);
            device.BeginFrame();
            uint redSlot = table.Resolve(red, TextureKind.Texture2D, SamplerState.Default);
            harness.Draw(retiredTarget, redSlot, sampled: red);
            device.Present();

            device.DeleteTexture(red);
            Assert.Equal(1, table.PendingRetirements);

            int green = harness.Texture(Green);
            Assert.Equal(red, green);
            uint greenSlot = table.Resolve(green, TextureKind.Texture2D, SamplerState.Default);
            Assert.NotEqual(0u, greenSlot);
            Assert.NotEqual(redSlot, greenSlot);

            int placeholderFrames = 0;
            for (int frame = 0; frame < 6; frame++)
            {
                device.BeginFrame();
                harness.Draw(liveTarget, greenSlot, sampled: green);
                // A retired slot is only sampled once the table has freed it: before
                // that its descriptor names an image the timeline is about to destroy.
                if (table.PendingRetirements == 0)
                {
                    harness.Draw(retiredTarget, redSlot);
                    placeholderFrames++;
                }
                device.Present();
            }
            Assert.True(placeholderFrames >= 3, "the retired slot was freed after " + (6 - placeholderFrames) + " frames");

            Assert.Equal(OpaqueBlack, harness.Pixel(retiredTarget));
            Assert.Equal(Green, harness.Pixel(liveTarget));

            int blue = harness.Texture(Blue);
            uint blueSlot = table.Resolve(blue, TextureKind.Texture2D, SamplerState.Default);
            Assert.Equal(redSlot, blueSlot);
            device.BeginFrame();
            harness.Draw(retiredTarget, blueSlot, sampled: blue);
            device.Present();
            Assert.Equal(Blue, harness.Pixel(retiredTarget));

            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// A depth texture behind the shadow array compares with the slot's sampler
    /// (its own compare mode off: the declaration decides), and the shadow
    /// placeholder at slot 0 is the far plane, which every reference passes.
    /// </summary>
    [SkippableFact]
    public void AShadowSlotComparesAgainstTheStoredDepth()
    {
        Skip.IfNot(TryCreateHarness(false, out Harness? harness, out ShaderCompiler? compiler), "No usable Vulkan device or shaderc.");
        using (compiler)
        using (harness)
        {
            VulkanDevice device = harness!.Device;
            BindlessTextureTable table = harness.Table;
            int depth = harness.DepthTexture(0.5f);
            var nearer = harness.Target();
            var farther = harness.Target();
            var placeholder = harness.Target();

            device.BeginFrame();
            uint slot = table.Resolve(depth, TextureKind.Shadow2D, SamplerState.Default);
            Assert.NotEqual(0u, slot);
            Assert.Equal(slot, table.Resolve(depth, TextureKind.Shadow2D, SamplerState.Default with { CompareEnable = true }));
            harness.Draw(nearer, slot, TextureKind.Shadow2D, 0.25f, depth);
            harness.Draw(farther, slot, TextureKind.Shadow2D, 0.75f, depth);
            harness.Draw(placeholder, 0, TextureKind.Shadow2D, 0.9f);
            device.Present();

            Assert.Equal(new byte[] { 255, 0, 0, 255 }, harness.Pixel(nearer));
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, harness.Pixel(farther));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, harness.Pixel(placeholder));
            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// A texture asked for as a kind it cannot sit behind, or no texture at all,
    /// resolves to slot 0 and allocates nothing; slot 0 samples the placeholder:
    /// opaque black, as OpenGL reads an unbound texture, and magenta only under
    /// poison mode, where an undefined read is meant to be loud.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWrongKindRequestResolvesToThePlaceholderSlot(bool poison)
    {
        Skip.IfNot(TryCreateHarness(poison, out Harness? harness, out ShaderCompiler? compiler), "No usable Vulkan device or shaderc.");
        using (compiler)
        using (harness)
        {
            VulkanDevice device = harness!.Device;
            BindlessTextureTable table = harness.Table;
            int colour = harness.Texture(Red);
            int depth = harness.DepthTexture(0.5f);
            var target = harness.Target();

            long before = table.PlaceholderResolutions;
            Assert.Equal(0u, table.Resolve(colour, TextureKind.Shadow2D, SamplerState.Default));
            Assert.Equal(0u, table.Resolve(colour, TextureKind.Texture2DArray, SamplerState.Default));
            Assert.Equal(0u, table.Resolve(colour, TextureKind.TextureCube, SamplerState.Default));
            Assert.Equal(0u, table.Resolve(colour, TextureKind.Texture3D, SamplerState.Default));
            Assert.Equal(0u, table.Resolve(colour, TextureKind.UnsignedTexture2D, SamplerState.Default));
            Assert.Equal(0u, table.Resolve(depth, TextureKind.SignedTexture2D, SamplerState.Default));
            Assert.Equal(0u, table.Resolve(0, TextureKind.Texture2D, SamplerState.Default));
            Assert.Equal(before + 7, table.PlaceholderResolutions);
            foreach (TextureKind kind in Enum.GetValues<TextureKind>()) Assert.Equal(0, table.LiveSlots(kind));
            Assert.Equal(0, table.PendingWrites);

            device.BeginFrame();
            harness.Draw(target, 0);
            device.Present();

            Assert.Equal(poison ? Magenta : OpaqueBlack, harness.Pixel(target));
            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// The shared layout declares every binding of the convention: set 2 carries the
    /// program record (a dynamic uniform buffer at <see cref="SetConvention.ProgramRecordBinding" />)
    /// beside the storage buffers, and the layout's dynamic uniform buffers are the
    /// ones the device floor requires. A native shader reading the record would
    /// otherwise not build a pipeline on the shared layout.
    /// </summary>
    [Fact]
    public void TheSharedLayoutDeclaresTheProgramRecordInSetTwo()
    {
        DescriptorSetLayoutBinding[] storage = SharedPipelineLayout.StorageBindings();
        int records = 0;
        foreach (DescriptorSetLayoutBinding binding in storage)
        {
            if (binding.Binding != (uint)SetConvention.ProgramRecordBinding) continue;
            records++;
            Assert.Equal(DescriptorType.UniformBufferDynamic, binding.DescriptorType);
            Assert.Equal(1u, binding.DescriptorCount);
            Assert.Equal(SharedPipelineLayout.Stages, binding.StageFlags);
        }
        Assert.Equal(1, records);
        foreach (SetConvention.Binding buffer in SetConvention.StorageBuffers)
        {
            Assert.Contains(storage, b => b.Binding == (uint)buffer.Value && b.DescriptorType == DescriptorType.StorageBuffer);
        }

        uint dynamicUniforms = 0;
        foreach (DescriptorSetLayoutBinding binding in SharedPipelineLayout.FrameBindings())
        {
            if (binding.DescriptorType == DescriptorType.UniformBufferDynamic) dynamicUniforms += binding.DescriptorCount;
        }
        foreach (DescriptorSetLayoutBinding binding in storage)
        {
            if (binding.DescriptorType == DescriptorType.UniformBufferDynamic) dynamicUniforms += binding.DescriptorCount;
        }
        Assert.Equal(DescriptorIndexingFloor.RequiredDynamicUniformBuffers, dynamicUniforms);
    }

    /// <summary>
    /// A lookup that took the texture before another thread deleted it and reaches
    /// the table after the deletion released its slots must not allocate: nothing
    /// would ever retire that slot, and its descriptor would name a view the frame
    /// ring destroys. It resolves to the placeholder instead.
    /// </summary>
    [SkippableFact]
    public void AResolveOfATextureAlreadyReleasedAllocatesNothing()
    {
        Skip.IfNot(TryCreateHarness(false, out Harness? harness, out ShaderCompiler? compiler), "No usable Vulkan device or shaderc.");
        using (compiler)
        using (harness)
        {
            VulkanDevice device = harness!.Device;
            BindlessTextureTable table = harness.Table;
            int red = harness.Texture(Red);
            VulkanTexture held = device.TexturesForTests.Get(red)!;

            device.DeleteTexture(red);
            Assert.Equal(0, table.PendingRetirements);

            long before = table.PlaceholderResolutions;
            Assert.Equal(0u, table.Resolve(held, TextureKind.Texture2D, SamplerState.Default));
            Assert.Equal(before + 1, table.PlaceholderResolutions);
            Assert.Equal(0, table.LiveSlots(TextureKind.Texture2D));
            Assert.Equal(0, table.PendingWrites);

            device.BeginFrame();
            device.Present();
            GpuTest.AssertClean(device);
        }
    }
}
