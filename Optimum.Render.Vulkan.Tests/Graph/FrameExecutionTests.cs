// Source: Optimum.Render.Vulkan.Tests/ComputePassTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The frame graph's compute pass kind on a device, validation with sync and best
/// practices on: a dispatch stores into a storage image in the format the device
/// supports (or its fallback) with its specialization constants and push constants; a
/// mip chain samples level n and stores level n + 1 of one image; a compute pass
/// followed by a raster pass sampling its output, frame after frame with Present
/// between frames and no readback in the loop; and a declaration that does not fit is
/// refused without recording anything.
/// </summary>
public class ComputePassTests(ITestOutputHelper output)
{
    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private static string Qualifier(Format format) => format switch
    {
        Format.R8Unorm => "r8",
        Format.R8G8Unorm => "rg8",
        Format.R8G8B8A8Unorm => "rgba8",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static int Channels(Format format) => format switch
    {
        Format.R8Unorm => 1,
        Format.R8G8Unorm => 2,
        _ => 4,
    };

    private int Program(VulkanDevice seam, string code, string name, ComputeSlot[] slots, uint push = 0)
    {
        int program = seam.CreateComputeProgram(code, name, slots, push);
        Assert.True(program > 0, seam.GetError());
        return program;
    }

    [SkippableFact]
    public void ADispatchStoresIntoAStorageImageWithItsSpecializationAndPushConstants()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            VulkanDevice seam = device!;
            const int width = 13, height = 11;

            // The AO working term's format: the device's choice, or its fallback.
            int target = seam.CreateStorageTexture(width, height, Format.R8Unorm);
            VulkanTexture texture = seam.TexturesForTests.Get(target)!;
            Assert.Equal(StorageFormats.Choose(Format.R8Unorm, seam.ContextForTests.OptimalFormatFeatures), texture.Format);
            Assert.NotEqual((ImageUsageFlags)0, texture.Usage & ImageUsageFlags.StorageBit);
            output.WriteLine("R8_UNORM storage texture created as " + texture.Format);
            int channels = Channels(texture.Format);

            string code = $$"""
                #version 450
                layout(local_size_x = 8, local_size_y = 8) in;
                layout(constant_id = 0) const uint LEVEL = 0u;
                layout(push_constant) uniform Push { uint offset; } push;
                layout(binding = 3, {{Qualifier(texture.Format)}}) uniform writeonly image2D target;
                void main()
                {
                    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
                    if (any(greaterThanEqual(p, imageSize(target)))) return;
                    uint value = (uint(p.x) * 16u + uint(p.y) + LEVEL + push.offset) & 255u;
                    imageStore(target, p, vec4(float(value) / 255.0));
                }
                """;
            int program = Program(seam, code, "store", new[] { new ComputeSlot(3, ComputeSlotKind.Storage) }, 4);

            uint[] levels = { 7, 100, 7 };
            long dispatchesBefore = seam.FrameGraphForTests.Dispatches;
            for (int frame = 0; frame < levels.Length; frame++)
            {
                seam.BeginFrame();
                uint offset = (uint)frame * 3;
                Assert.True(seam.RecordComputePass(new ComputePassDeclaration
                {
                    Name = "store",
                    ProgramId = program,
                    Specialization = new[] { levels[frame] },
                    Bindings = new[] { new ComputeBinding(3, target, ComputeAccess.StorageWrite) },
                    Dispatches = new[] { ComputeDispatch.Covering(0, BitConverter.GetBytes(offset)) },
                }), seam.GetError());

                byte[] pixels = seam.ReadBackLevel0ForTests(target);
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int expected = (int)((x * 16 + y + levels[frame] + offset) & 255);
                    Assert.Equal(expected, pixels[(y * width + x) * channels]);
                }
                seam.Present();
            }

            // Two distinct specialization values: two pipelines, the third frame a hit.
            Assert.Equal(2, seam.ComputeForTests.Get(program)!.PipelineCount);
            Assert.Equal(1, seam.ComputeForTests.Hits);
            // 13x11 at 8x8 is one dispatch of 2x2 groups per frame.
            Assert.Equal(levels.Length, seam.FrameGraphForTests.Dispatches - dispatchesBefore);
            GpuTest.AssertClean(seam);
        }
    }

    [SkippableFact]
    public void AMipChainSamplesLevelNAndStoresLevelNPlusOneOfOneImage()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16, levels = 3;
            int chain = seam.CreateStorageTexture(size, size, Format.R8G8B8A8Unorm, levels);
            Assert.Equal((uint)levels, seam.TexturesForTests.Get(chain)!.MipLevels);

            int seed = Program(seam, """
                #version 450
                layout(local_size_x = 8, local_size_y = 8) in;
                layout(push_constant) uniform Push { uint salt; } push;
                layout(binding = 0, rgba8) uniform writeonly image2D level0;
                void main()
                {
                    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
                    if (any(greaterThanEqual(p, imageSize(level0)))) return;
                    uint value = (uint(p.x) * 37u + uint(p.y) * 11u + push.salt) & 255u;
                    imageStore(level0, p, vec4(float(value) / 255.0, 0.0, 0.0, 1.0));
                }
                """, "seed", new[] { new ComputeSlot(0, ComputeSlotKind.Storage) }, 4);

            // The prefilter shape: the sampled binding's view starts at level n, so lod 0 is level n.
            int reduce = Program(seam, """
                #version 450
                layout(local_size_x = 8, local_size_y = 8) in;
                layout(binding = 0) uniform sampler2D source;
                layout(binding = 1, rgba8) uniform writeonly image2D destination;
                void main()
                {
                    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
                    if (any(greaterThanEqual(p, imageSize(destination)))) return;
                    float m = 0.0;
                    for (int i = 0; i < 4; i++) m = max(m, texelFetch(source, 2 * p + ivec2(i & 1, i >> 1), 0).r);
                    imageStore(destination, p, vec4(m, 0.0, 0.0, 1.0));
                }
                """, "reduce", new[]
            {
                new ComputeSlot(0, ComputeSlotKind.Sampled), new ComputeSlot(1, ComputeSlotKind.Storage),
            });

            for (uint frame = 0; frame < 3; frame++)
            {
                seam.BeginFrame();
                uint salt = frame * 29;
                Assert.True(seam.RecordComputePass(new ComputePassDeclaration
                {
                    Name = "seed",
                    ProgramId = seed,
                    Bindings = new[] { new ComputeBinding(0, chain, ComputeAccess.StorageWrite) },
                    Dispatches = new[] { ComputeDispatch.Covering(0, BitConverter.GetBytes(salt)) },
                }), seam.GetError());
                for (uint n = 0; n + 1 < levels; n++)
                {
                    Assert.True(seam.RecordComputePass(new ComputePassDeclaration
                    {
                        Name = "reduce",
                        ProgramId = reduce,
                        Bindings = new[]
                        {
                            new ComputeBinding(0, chain, ComputeAccess.Sampled, BaseMip: n),
                            new ComputeBinding(1, chain, ComputeAccess.StorageWrite, BaseMip: n + 1),
                        },
                        Dispatches = new[] { ComputeDispatch.Covering(1) },
                    }), seam.GetError());
                }

                // Only after the whole chain is recorded: the expected chain on the CPU.
                var expected = new int[levels][];
                expected[0] = new int[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    expected[0][y * size + x] = (int)((x * 37 + y * 11 + salt) & 255);
                for (int n = 1; n < levels; n++)
                {
                    int s = size >> n, parent = size >> (n - 1);
                    expected[n] = new int[s * s];
                    for (int y = 0; y < s; y++)
                    for (int x = 0; x < s; x++)
                    {
                        int m = 0;
                        for (int i = 0; i < 4; i++)
                            m = Math.Max(m, expected[n - 1][(2 * y + (i >> 1)) * parent + 2 * x + (i & 1)]);
                        expected[n][y * s + x] = m;
                    }
                }

                for (uint n = 0; n < levels; n++)
                {
                    int s = size >> (int)n;
                    byte[] pixels = seam.ReadBackLevelForTests(chain, n);
                    Assert.Equal(s * s * 4, pixels.Length);
                    for (int i = 0; i < s * s; i++)
                    {
                        Assert.Equal(expected[n][i], pixels[i * 4]);
                        Assert.Equal(255, pixels[i * 4 + 3]);
                    }
                }
                seam.Present();
            }
            GpuTest.AssertClean(seam);
        }
    }

    [SkippableFact]
    public void ARasterPassSamplesTheComputePassOutputOfTheSameFrameFrameAfterFrame()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 8, frames = 8;

            int computed = seam.CreateStorageTexture(size, size, Format.R8G8B8A8Unorm);
            int store = Program(seam, """
                #version 450
                layout(local_size_x = 4, local_size_y = 4) in;
                layout(push_constant) uniform Push { uint frame; } push;
                layout(binding = 0, rgba8) uniform writeonly image2D target;
                void main()
                {
                    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
                    if (any(greaterThanEqual(p, imageSize(target)))) return;
                    imageStore(target, p, vec4(float((push.frame + 1u) * 20u) / 255.0, float(p.x * 16) / 255.0,
                        float(p.y * 16) / 255.0, 1.0));
                }
                """, "store", new[] { new ComputeSlot(0, ComputeSlotKind.Storage) }, 4);

            int background = GpuTest.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = vec4(1.0, 0.0, 1.0, 1.0); }
                """, "background");
            int sample = GpuTest.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D computed;
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = texelFetch(computed, ivec2(gl_FragCoord.xy), 0); }
                """, "sample");

            var colours = new int[frames];
            var targets = new int[frames];
            for (int i = 0; i < frames; i++)
            {
                colours[i] = seam.CreateTexture2DRaw(size, size, 0x8058, IntPtr.Zero, 4);
                targets[i] = seam.CreateFramebuffer(size, size);
                seam.AttachTexture(targets[i], EnumFramebufferAttachment.ColorAttachment0, colours[i], 0);
                seam.SetDrawBuffers(targets[i], 1);
            }

            seam.SetSamplerUnit(sample, "computed", 0);
            long passesBefore = seam.FrameGraphForTests.ComputePasses;
            long dispatchesBefore = seam.FrameGraphForTests.Dispatches;

            for (int i = 0; i < frames; i++)
            {
                seam.BeginFrame();
                // A draw first, so the frame has a rendering scope open when the compute pass comes.
                seam.BindFramebuffer(targets[i]);
                seam.SetViewport(0, 0, size, size);
                seam.SetCullFace(false);
                seam.SetDepthTest(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.UseProgram(background);
                seam.DrawFullscreenTriangle();

                Assert.True(seam.RecordComputePass(new ComputePassDeclaration
                {
                    Name = "store",
                    ProgramId = store,
                    Bindings = new[] { new ComputeBinding(0, computed, ComputeAccess.StorageWrite) },
                    Dispatches = new[] { ComputeDispatch.Covering(0, BitConverter.GetBytes((uint)i)) },
                }), seam.GetError());

                seam.UseProgram(sample);
                seam.BindTexture(0, computed);
                seam.DrawFullscreenTriangle();
                seam.BindTexture(0, 0);
                seam.Present();
            }

            // The work group size comes from the module (4x4), not the description's default 8x8.
            ComputeProgram stored = seam.ComputeForTests.Get(store)!;
            Assert.Equal((4u, 4u), (stored.LocalSizeX, stored.LocalSizeY));
            Assert.Equal(frames, seam.FrameGraphForTests.ComputePasses - passesBefore);
            Assert.Equal(frames, seam.FrameGraphForTests.Dispatches - dispatchesBefore);

            // The sequence completes before any readback or CPU wait.
            seam.BeginFrame();
            for (int i = 0; i < frames; i++)
            {
                byte[] pixels = seam.ReadBackLevel0ForTests(colours[i]);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int offset = (y * size + x) * 4;
                    Assert.Equal((i + 1) * 20, pixels[offset]);
                    Assert.Equal(x * 16, pixels[offset + 1]);
                    Assert.Equal(y * 16, pixels[offset + 2]);
                    Assert.Equal(255, pixels[offset + 3]);
                }
            }
            seam.Present();
            GpuTest.AssertClean(seam);
        }
    }

    [SkippableFact]
    public void ADeclarationThatDoesNotFitItsProgramIsRefusedWithoutRecording()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            VulkanDevice seam = device!;
            int plain = seam.CreateTexture2DRaw(4, 4, 0x8058, IntPtr.Zero, 4);
            int storage = seam.CreateStorageTexture(4, 4, Format.R8G8B8A8Unorm, 2);
            int program = Program(seam, """
                #version 450
                layout(local_size_x = 4, local_size_y = 4) in;
                layout(binding = 0, rgba8) uniform writeonly image2D target;
                void main() { imageStore(target, ivec2(gl_GlobalInvocationID.xy), vec4(1.0)); }
                """, "store", new[] { new ComputeSlot(0, ComputeSlotKind.Storage) });

            Assert.False(seam.RecordComputePass(new ComputePassDeclaration
            {
                ProgramId = program,
                Bindings = new[] { new ComputeBinding(0, storage, ComputeAccess.StorageWrite) },
                Dispatches = new[] { ComputeDispatch.Covering(0) },
            }), "no frame is open");

            long passesBefore = seam.FrameGraphForTests.ComputePasses;
            seam.BeginFrame();
            void Refused(ComputePassDeclaration pass, string reason)
            {
                Assert.False(seam.RecordComputePass(pass));
                Assert.Contains(reason, seam.GetError());
            }
            Refused(new ComputePassDeclaration
            {
                Name = "plain", ProgramId = program,
                Bindings = new[] { new ComputeBinding(0, plain, ComputeAccess.StorageWrite) },
                Dispatches = new[] { ComputeDispatch.Covering(0) },
            }, "not created as a storage texture");
            Refused(new ComputePassDeclaration
            {
                Name = "kind", ProgramId = program,
                Bindings = new[] { new ComputeBinding(0, storage, ComputeAccess.Sampled) },
                Dispatches = new[] { ComputeDispatch.Covering(0) },
            }, "is declared Storage");
            Refused(new ComputePassDeclaration
            {
                Name = "unbound", ProgramId = program,
                Bindings = Array.Empty<ComputeBinding>(),
                Dispatches = new[] { ComputeDispatch.Explicit(1, 1) },
            }, "binding 0 is not bound");
            Refused(new ComputePassDeclaration
            {
                Name = "push", ProgramId = program,
                Bindings = new[] { new ComputeBinding(0, storage, ComputeAccess.StorageWrite) },
                Dispatches = new[] { ComputeDispatch.Covering(0, new byte[4]) },
            }, "pushes 4 bytes");
            Refused(new ComputePassDeclaration
            {
                Name = "missing", ProgramId = 999,
                Bindings = new[] { new ComputeBinding(0, storage, ComputeAccess.StorageWrite) },
                Dispatches = new[] { ComputeDispatch.Covering(0) },
            }, "no compute program 999");
            Assert.Equal(passesBefore, seam.FrameGraphForTests.ComputePasses);
            Assert.Equal(0, seam.CreateComputeProgram("#version 450\nvoid main() { broken }", "broken",
                Array.Empty<ComputeSlot>()));
            Assert.Contains("failed to compile", seam.GetError());
            seam.Present();

            // A deleted program retires on the timeline, after the frames that could bind it.
            seam.DeleteComputeProgram(program);
            Assert.Null(seam.ComputeForTests.Get(program));
            GpuTest.AssertClean(seam);
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/FrameGraphFrameTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;

/// <summary>
/// Phase 2 step 2 on a real device: the streaming frame graph. A declared TAA-shaped
/// frame (opaque scene with a motion window, history resolve, final composition that
/// writes Primary 0 while sampling Primary 1, blit) records exactly one rendering scope
/// per pass for five frames with the history accumulating (Present between frames, no
/// readback in the loop); the same frames are byte-identical with
/// OPTIMUM_VULKAN_FRAMEGRAPH off; clears are promoted, kept in the pass or landed
/// standalone as the plan describes, and a masked-out clear stays a no-op.
/// </summary>
public class FrameGraphFrameTests
{
    private readonly ITestOutputHelper _output;

    public FrameGraphFrameTests(ITestOutputHelper output) => _output = output;

    private const int Size = 8;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.5, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private sealed class TaaScene
    {
        public int Primary, Colour, Glow, Motion, Depth;
        public int HistoryAFb, HistoryA, HistoryBFb, HistoryB;
        public int OutputFb, Output;
        public int Scene, Resolve, Final, Blit;
    }

    private bool TryCreate(bool frameGraph, out VulkanDevice? device)
    {
        VulkanDevice created = NewDevice();
        created.FrameGraphEnabled = frameGraph;
        if (!created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            _output.WriteLine("Vulkan unavailable: " + failureReason);
            created.Dispose();
            device = null;
            return false;
        }
        device = created;
        return true;
    }

    private static TaaScene CreateTaaScene(VulkanDevice seam)
    {
        int Texture(EnumTextureInternalFormat format) =>
            seam.CreateTexture2D(Size, Size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

        var s = new TaaScene
        {
            Colour = Texture(EnumTextureInternalFormat.Rgba8),
            Glow = Texture(EnumTextureInternalFormat.Rgba8),
            Motion = Texture(EnumTextureInternalFormat.Rgba16f),
            Depth = Texture(EnumTextureInternalFormat.DepthComponent32),
            HistoryA = Texture(EnumTextureInternalFormat.Rgba16f),
            HistoryB = Texture(EnumTextureInternalFormat.Rgba16f),
            Output = Texture(EnumTextureInternalFormat.Rgba8),
        };
        s.Primary = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(s.Primary, EnumFramebufferAttachment.ColorAttachment0, s.Colour, 0);
        seam.AttachTexture(s.Primary, EnumFramebufferAttachment.ColorAttachment1, s.Glow, 0);
        seam.AttachTexture(s.Primary, EnumFramebufferAttachment.ColorAttachment2, s.Motion, 0);
        seam.AttachTexture(s.Primary, EnumFramebufferAttachment.DepthAttachment, s.Depth, 0);
        seam.SetDrawBuffers(s.Primary, 0b011);
        s.HistoryAFb = SingleTarget(seam, s.HistoryA);
        s.HistoryBFb = SingleTarget(seam, s.HistoryB);
        s.OutputFb = SingleTarget(seam, s.Output);

        s.Scene = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            layout(location = 1) out vec4 outGlow;
            layout(location = 2) out vec4 outMotion;
            void main(void)
            {
                outColor = vec4(1.0, 0.2, 0.0, 1.0);
                outGlow = vec4(0.4, 0.0, 0.0, 1.0);
                outMotion = vec4(0.25, 0.5, 0.0, 1.0);
            }
            """, "fg-scene");
        s.Resolve = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D sceneTex;
            uniform sampler2D historyTex;
            uniform sampler2D motionTex;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void)
            {
                outColor = vec4(mix(texture(historyTex, uv).r, texture(sceneTex, uv).r, 0.5),
                    texture(motionTex, uv).g, 0.0, 1.0);
            }
            """, "fg-resolve");
        s.Final = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D glowTex;
            uniform sampler2D historyTex;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = vec4(texture(historyTex, uv).r, texture(glowTex, uv).r, 0.2, 1.0); }
            """, "fg-final");
        s.Blit = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D sceneTex;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = texture(sceneTex, uv); }
            """, "fg-blit");
        seam.SetSamplerUnit(s.Resolve, "sceneTex", 0);
        seam.SetSamplerUnit(s.Resolve, "historyTex", 1);
        seam.SetSamplerUnit(s.Resolve, "motionTex", 2);
        seam.SetSamplerUnit(s.Final, "glowTex", 0);
        seam.SetSamplerUnit(s.Final, "historyTex", 1);
        seam.SetSamplerUnit(s.Blit, "sceneTex", 0);
        return s;
    }

    private static int SingleTarget(VulkanDevice seam, int texture)
    {
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        return framebuffer;
    }

    private static void BaseState(VulkanDevice seam)
    {
        seam.SetViewport(0, 0, Size, Size);
        seam.SetScissorEnabled(false);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.SetColorMask(true, true, true, true);
    }

    /// <summary>Frame 0: both histories cleared, nothing drawn.</summary>
    private static void SeedFrame(VulkanDevice seam, TaaScene s)
    {
        seam.BeginFrame();
        BaseState(seam);
        seam.DeclarePass(new PassDeclaration { Name = "Seed", FramebufferId = s.HistoryAFb });
        seam.ClearColor(0, 0f, 0f, 0f, 0f);
        seam.DeclarePass(new PassDeclaration { Name = "Seed", FramebufferId = s.HistoryBFb });
        seam.ClearColor(0, 0f, 0f, 0f, 0f);
        seam.Present();
    }

    /// <summary>
    /// One TAA-shaped frame. Odd frames write history A and read B, even frames the reverse.
    /// Without TAA the resolve is skipped and the final pass reads the seeded history A.
    /// </summary>
    private static void TaaFrame(VulkanDevice seam, TaaScene s, int frame, bool taa)
    {
        bool writeA = frame % 2 == 1;
        int writeFb = writeA ? s.HistoryAFb : s.HistoryBFb;
        int writeTex = taa ? writeA ? s.HistoryA : s.HistoryB : s.HistoryA;
        int readTex = writeA ? s.HistoryB : s.HistoryA;

        seam.BeginFrame();
        BaseState(seam);

        // Opaque: every clear issued before the first draw, the motion one inside a motion window.
        seam.DeclarePass(new PassDeclaration { Name = "Opaque", FramebufferId = s.Primary });
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDrawBuffers(s.Primary, 0b011);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.SetDrawBuffers(s.Primary, 0b111);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.SetDrawBuffers(s.Primary, 0b011);
        seam.SetDepthMask(true);
        seam.ClearDepth(1f);
        seam.SetDepthTest(true);
        seam.SetDepthFunc(0x203);
        seam.UseProgram(s.Scene);
        seam.SetDrawBuffers(s.Primary, 0b111);
        seam.DrawFullscreenTriangle();
        seam.SetDrawBuffers(s.Primary, 0b011);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);

        if (taa)
        {
            seam.DeclarePass(new PassDeclaration
            {
                Name = "TaaResolve", FramebufferId = writeFb, Reads = new[] { s.Colour, readTex, s.Motion },
            });
            seam.SetViewport(0, 0, Size, Size);
            seam.UseProgram(s.Resolve);
            seam.BindTexture(0, s.Colour);
            seam.BindTexture(1, readTex);
            seam.BindTexture(2, s.Motion);
            seam.DrawFullscreenTriangle();
            seam.BindTexture(0, 0);
            seam.BindTexture(1, 0);
            seam.BindTexture(2, 0);
        }

        // Final composition: writes Primary 0, samples Primary 1.
        seam.DeclarePass(new PassDeclaration
        {
            Name = "FinalComposition", FramebufferId = s.Primary, ColorSlots = ~(1u << 1),
            Reads = new[] { s.Glow, writeTex },
        });
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDrawBuffers(s.Primary, 0b001);
        seam.UseProgram(s.Final);
        seam.BindTexture(0, s.Glow);
        seam.BindTexture(1, writeTex);
        seam.DrawFullscreenTriangle();
        seam.BindTexture(0, 0);
        seam.BindTexture(1, 0);
        seam.EndPass();
        seam.SetDrawBuffers(s.Primary, 0b011);

        // Blit: a plain full overwrite, so an exactly matching plan loads it DONT_CARE.
        seam.DeclarePass(new PassDeclaration
        {
            Name = "Blit", FramebufferId = s.OutputFb, Reads = new[] { s.Colour }, TransientSlots = 1,
        });
        seam.SetViewport(0, 0, Size, Size);
        seam.UseProgram(s.Blit);
        seam.BindTexture(0, s.Colour);
        seam.DrawFullscreenTriangle();
        seam.BindTexture(0, 0);

        seam.Present();
    }

    private static Dictionary<string, byte[]> ReadAll(VulkanDevice seam, TaaScene s)
    {
        seam.BeginFrame();
        var result = new Dictionary<string, byte[]>
        {
            ["colour"] = seam.ReadBackLevel0ForTests(s.Colour),
            ["glow"] = seam.ReadBackLevel0ForTests(s.Glow),
            ["motion"] = seam.ReadBackLevel0ForTests(s.Motion),
            ["depth"] = seam.ReadBackLevel0ForTests(s.Depth),
            ["historyA"] = seam.ReadBackLevel0ForTests(s.HistoryA),
            ["historyB"] = seam.ReadBackLevel0ForTests(s.HistoryB),
            ["output"] = seam.ReadBackLevel0ForTests(s.Output),
        };
        seam.Present();
        return result;
    }

    private static float HalfAt(byte[] texels, int pixel, int channel) =>
        (float)BitConverter.ToHalf(texels, pixel * 8 + channel * 2);

    [SkippableFact]
    public void ADeclaredTaaFrameOpensOneScopePerPassAndAccumulatesHistory()
    {
        Skip.IfNot(TryCreate(frameGraph: true, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            FrameGraph graph = seam.FrameGraphForTests;
            TaaScene scene = CreateTaaScene(seam);

            long standaloneBefore = graph.StandaloneClears;
            SeedFrame(seam, scene);
            // Nothing attached the seeded histories this frame: the clears landed as clear-image commands.
            Assert.Equal(2, graph.StandaloneClears - standaloneBefore);

            long hitsBefore = graph.PlanHits;
            long missesBefore = graph.PlanMisses;
            long dontCareBefore = graph.PlannedDontCareLoads;
            for (int frame = 1; frame <= 5; frame++)
            {
                long scopes = seam.ScopesOpenedForTests;
                long passes = graph.Passes;
                long splits = graph.Splits;
                long promoted = graph.PromotedClears;
                long inPass = graph.InPassClears;
                long standalone = graph.StandaloneClears;

                TaaFrame(seam, scene, frame, taa: true);

                long scopesOpened = seam.ScopesOpenedForTests - scopes;
                long passCount = graph.Passes - passes;
                _output.WriteLine($"frame {frame}: passes={passCount} scopes={scopesOpened} splits={graph.Splits - splits} " +
                                  $"promoted={graph.PromotedClears - promoted} inPass={graph.InPassClears - inPass} " +
                                  $"standalone={graph.StandaloneClears - standalone}");
                Assert.Equal(4, passCount);
                Assert.Equal(passCount, scopesOpened);
                Assert.Equal(0, graph.Splits - splits);
                // Colour, glow, motion and depth: every Opaque clear became LOAD_OP_CLEAR.
                Assert.Equal(4, graph.PromotedClears - promoted);
                Assert.Equal(0, graph.InPassClears - inPass);
                Assert.Equal(0, graph.StandaloneClears - standalone);
            }

            // Frames 1 and 2 have no frame of the same parity to match; 3 to 5 do.
            Assert.Equal(3, graph.PlanHits - hitsBefore);
            Assert.Equal(2, graph.PlanMisses - missesBefore);
            Assert.Equal(3, graph.PlannedDontCareLoads - dontCareBefore);

            Dictionary<string, byte[]> texels = ReadAll(seam, scene);
            int centre = Size / 2 * Size + Size / 2;
            // h_n = (h_(n-1) + 1) / 2 from 0: A was written on frames 1, 3, 5, B on 2 and 4.
            Assert.Equal(0.96875f, HalfAt(texels["historyA"], centre, 0));
            Assert.Equal(0.9375f, HalfAt(texels["historyB"], centre, 0));
            Assert.Equal(0.5f, HalfAt(texels["historyA"], centre, 1));
            // Final composition read the history it just wrote and Primary 1 while writing Primary 0.
            _output.WriteLine($"centre: colour={texels["colour"][centre * 4]},{texels["colour"][centre * 4 + 1]},{texels["colour"][centre * 4 + 2]} " +
                              $"glow={texels["glow"][centre * 4]},{texels["glow"][centre * 4 + 1]},{texels["glow"][centre * 4 + 2]} " +
                              $"output={texels["output"][centre * 4]},{texels["output"][centre * 4 + 1]},{texels["output"][centre * 4 + 2]}");
            Assert.Equal(247, texels["output"][centre * 4]);
            Assert.Equal(102, texels["output"][centre * 4 + 1]);
            Assert.Equal(51, texels["output"][centre * 4 + 2]);
            Assert.Equal(texels["colour"], texels["output"]);
            AssertClean(seam);
        }
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDeclaredFrameIsPixelIdenticalWithTheFrameGraphOff(bool taa)
    {
        Dictionary<string, byte[]>? on = RenderFrames(frameGraph: true, taa);
        Skip.If(on == null, "No usable Vulkan device.");
        Dictionary<string, byte[]> off = RenderFrames(frameGraph: false, taa)!;

        foreach ((string name, byte[] texels) in on!)
        {
            Assert.True(texels.AsSpan().SequenceEqual(off[name]), name + " differs between the frame graph and scope inference");
        }
    }

    private Dictionary<string, byte[]>? RenderFrames(bool frameGraph, bool taa)
    {
        if (!TryCreate(frameGraph, out VulkanDevice? device)) return null;
        using (device)
        {
            VulkanDevice seam = device!;
            TaaScene scene = CreateTaaScene(seam);
            SeedFrame(seam, scene);
            for (int frame = 1; frame <= 5; frame++) TaaFrame(seam, scene, frame, taa);
            Dictionary<string, byte[]> texels = ReadAll(seam, scene);
            _output.WriteLine((frameGraph ? "graph" : "inference") + ": scopes=" + seam.ScopesOpenedForTests +
                              " passes=" + seam.FrameGraphForTests.Passes);
            AssertClean(seam);
            return texels;
        }
    }

    /// <summary>
    /// One frame of clears on two targets: a masked-out clear and an all-false colour-mask
    /// clear (no-ops, nothing pending), a clear with no pass open under an additive draw
    /// (LOAD_OP_CLEAR), a clear after the pass opened (vkCmdClearAttachments, counted) and a
    /// clear of a texture sampled before any pass attaches it (a clear-image command first).
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClearsArePromotedKeptInThePassOrLandedBeforeARead(bool frameGraph)
    {
        Skip.IfNot(TryCreate(frameGraph, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            FrameGraph graph = seam.FrameGraphForTests;
            int Texture() => seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int c0 = Texture(), c1 = Texture(), source = Texture(), sink = Texture();
            int target = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, c0, 0);
            seam.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment1, c1, 0);
            int sourceFb = SingleTarget(seam, source);
            int sinkFb = SingleTarget(seam, sink);

            int add = GpuTest.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = vec4(0.4, 0.0, 0.0, 0.0); }
                """, "fg-add");
            int copy = GpuTest.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D tex;
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = texture(tex, uv); }
                """, "fg-copy");
            seam.SetSamplerUnit(copy, "tex", 0);

            // Seed: c1 grey. The target has both draw buffers.
            seam.BeginFrame();
            BaseState(seam);
            seam.DeclarePass(new PassDeclaration { Name = "Seed", FramebufferId = target });
            seam.SetDrawBuffers(target, 0b11);
            seam.ClearColor(1, 0.2f, 0.2f, 0.2f, 1f);
            seam.Present();

            seam.BeginFrame();
            BaseState(seam);
            seam.DeclarePass(new PassDeclaration { Name = "Target", FramebufferId = target });
            seam.SetDrawBuffers(target, 0b01);

            (long promoted, long inPass, long standalone) Counters() =>
                (graph.PromotedClears, graph.InPassClears, graph.StandaloneClears);
            var before = Counters();
            // Masked out by the draw buffers, then by an all-false colour mask: no-ops on every path.
            seam.ClearColor(1, 1f, 1f, 1f, 1f);
            seam.SetColorMask(false, false, false, false);
            seam.ClearColor(0, 1f, 1f, 1f, 1f);
            seam.SetColorMask(true, true, true, true);
            Assert.Equal(before, Counters());
            Assert.False(graph.HasPendingClears);

            // No pass open: promoted into the scope the additive draw opens.
            long scopes = seam.ScopesOpenedForTests;
            seam.ClearColor(0, 0.2f, 0f, 0f, 1f);
            seam.SetBlend(true, EnumBlendMode.Standard);
            seam.SetBlendFuncSeparate(0, 1, 1, 1, 1);
            seam.UseProgram(add);
            seam.DrawFullscreenTriangle();
            seam.SetBlend(false, EnumBlendMode.Standard);
            Assert.Equal(1, seam.ScopesOpenedForTests - scopes);

            // The pass is open: this clear stays in it.
            seam.SetDrawBuffers(target, 0b11);
            seam.ClearColor(1, 0f, 1f, 0f, 1f);
            seam.SetDrawBuffers(target, 0b01);
            if (frameGraph)
            {
                Assert.Equal(before.promoted + 1, graph.PromotedClears);
                Assert.Equal(before.inPass + 1, graph.InPassClears);
                Assert.Equal(before.standalone, graph.StandaloneClears);
            }

            // Cleared with no pass open, then sampled by a pass that does not attach it.
            seam.DeclarePass(new PassDeclaration { Name = "ClearSource", FramebufferId = sourceFb });
            seam.ClearColor(0, 0f, 0f, 1f, 1f);
            seam.DeclarePass(new PassDeclaration { Name = "Sink", FramebufferId = sinkFb, Reads = new[] { source } });
            seam.UseProgram(copy);
            seam.BindTexture(0, source);
            seam.DrawFullscreenTriangle();
            seam.BindTexture(0, 0);
            if (frameGraph)
            {
                Assert.Equal(before.standalone + 1, graph.StandaloneClears);
                Assert.Equal(0, graph.Splits);
            }
            seam.Present();

            seam.BeginFrame();
            byte[] first = seam.ReadBackLevel0ForTests(c0);
            byte[] second = seam.ReadBackLevel0ForTests(c1);
            byte[] sampled = seam.ReadBackLevel0ForTests(sink);
            seam.Present();

            int centre = (Size / 2 * Size + Size / 2) * 4;
            // 0.2 cleared + 0.4 added = 0.6 (153); alpha 1 + 0.
            Assert.Equal(new byte[] { 153, 0, 0, 255 }, first.AsSpan(centre, 4).ToArray());
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, second.AsSpan(centre, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, sampled.AsSpan(centre, 4).ToArray());
            AssertClean(seam);
        }
    }
}
}
