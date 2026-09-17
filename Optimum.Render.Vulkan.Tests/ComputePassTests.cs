using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

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

            int background = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = vec4(1.0, 0.0, 1.0, 1.0); }
                """, "background");
            int sample = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
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
