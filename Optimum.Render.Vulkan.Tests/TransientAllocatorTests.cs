using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 2 step 4: <see cref="TransientAllocator" /> (placement, pooling, aliasing, discard on
/// first use), <see cref="FeedbackCopyPool" /> (ReadSelf copies retired on the timeline), and
/// the device path for both: a post chain reads back the same with aliasing on and off, and
/// stays free of synchronization hazards with aliasing on.
/// </summary>
public class TransientAllocatorTests
{
    private readonly ITestOutputHelper _output;

    public TransientAllocatorTests(ITestOutputHelper output) => _output = output;

    private sealed class FakeBacking : ITransientBacking
    {
        private int _next = 100;
        public readonly List<int> Created = new();
        public readonly List<int> Destroyed = new();
        public readonly List<int> Discards = new();
        public readonly Dictionary<int, int> Rebinds = new();
        public readonly Dictionary<int, TransientImageDesc> Logical = new();
        public int Restores;

        public int Create(TransientImageDesc desc)
        {
            int id = _next++;
            Created.Add(id);
            return id;
        }

        public void Destroy(int textureId) => Destroyed.Add(textureId);
        public ulong BytesOf(int textureId) => 1024;
        public bool TryDescribe(int textureId, out TransientImageDesc desc) => Logical.TryGetValue(textureId, out desc);
        public void Discard(int textureId) => Discards.Add(textureId);
        public void Rebind(int logicalTextureId, int physicalTextureId) => Rebinds[logicalTextureId] = physicalTextureId;

        public void RestoreBindings()
        {
            Restores++;
            Rebinds.Clear();
        }
    }

    private sealed class FakeClock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    private static readonly TransientImageDesc Half = new(960, 540, Format.R8G8B8A8Unorm);
    private static readonly TransientImageDesc Full = new(1920, 1080, Format.R16G16B16A16Sfloat);

    [Fact]
    public void ThePostChainSlotsAreTheTwelveNamedOnes()
    {
        Assert.Equal(new[] { 2, 3, 4, 7, 8, 9, 10, 13, 14, 15, 18, 21 }, TransientAllocator.PostChainSlots);
        Assert.False(TransientAllocator.IsPostChainSlot(0));
        Assert.False(TransientAllocator.IsPostChainSlot(19));
        Assert.True(TransientAllocator.IsPostChainSlot(21));
    }

    [Fact]
    public void AliasedTransientsNeverOverlapInPassLifetime()
    {
        var backing = new FakeBacking();
        var allocator = new TransientAllocator(backing, aliasing: true);
        var random = new Random(7);
        int aliasedTotal = 0;
        var descOf = new Dictionary<int, TransientImageDesc>();

        for (int frame = 0; frame < 200; frame++)
        {
            allocator.BeginFrame();
            int first = 0;
            for (int i = 0; i < 12; i++)
            {
                first += random.Next(0, 2);
                int last = first + random.Next(0, 4);
                TransientImageDesc desc = random.Next(2) == 0 ? Half : Full;
                TransientLease lease = allocator.Acquire(desc, first, last);
                if (descOf.TryGetValue(lease.TextureId, out TransientImageDesc seen)) Assert.Equal(seen, desc);
                else descOf.Add(lease.TextureId, desc);
            }

            IReadOnlyList<TransientLease> leases = allocator.Leases;
            for (int a = 0; a < leases.Count; a++)
            {
                for (int b = a + 1; b < leases.Count; b++)
                {
                    bool sameImage = leases[a].TextureId == leases[b].TextureId;
                    Assert.Equal(sameImage, leases[a].Slot == leases[b].Slot);
                    if (!sameImage) continue;
                    Assert.True(leases[a].LastPass < leases[b].FirstPass || leases[b].LastPass < leases[a].FirstPass,
                        "frame " + frame + ": leases " + a + " [" + leases[a].FirstPass + "," + leases[a].LastPass +
                        "] and " + b + " [" + leases[b].FirstPass + "," + leases[b].LastPass + "] share image " +
                        leases[a].TextureId);
                    Assert.True(leases[b].Aliased);
                }
            }
            aliasedTotal += allocator.AliasedLeaseCount;
        }

        Assert.True(aliasedTotal > 0, "the random frames never aliased");
        // Every lease starts a new lifetime from UNDEFINED.
        Assert.Equal(200 * 12, backing.Discards.Count);
    }

    [Fact]
    public void TheChainAliasesItsThirdLeaseOntoTheFirstLeasesImage()
    {
        var backing = new FakeBacking();
        var allocator = new TransientAllocator(backing, aliasing: true);

        for (int frame = 0; frame < 3; frame++)
        {
            allocator.BeginFrame();
            TransientLease first = allocator.Acquire(Half, 0, 1);
            TransientLease second = allocator.Acquire(Half, 1, 2);
            TransientLease third = allocator.Acquire(Half, 2, 3);

            Assert.NotEqual(first.TextureId, second.TextureId);
            Assert.Equal(first.TextureId, third.TextureId);
            Assert.False(first.Aliased);
            Assert.False(second.Aliased);
            Assert.True(third.Aliased);
            Assert.Equal(1024UL, allocator.AliasedBytes);
        }

        // Two images, created in the first frame and reused after.
        Assert.Equal(2, backing.Created.Count);
        Assert.Equal(2, allocator.PhysicalImageCount);
    }

    [Fact]
    public void WithAliasingOffEveryLeaseKeepsItsOwnImageAndItsContents()
    {
        var backing = new FakeBacking();
        var allocator = new TransientAllocator(backing, aliasing: false);
        int[]? previous = null;

        for (int frame = 0; frame < 3; frame++)
        {
            allocator.BeginFrame();
            int[] ids =
            {
                allocator.Acquire(Half, 0, 1).TextureId,
                allocator.Acquire(Half, 1, 2).TextureId,
                allocator.Acquire(Half, 2, 3).TextureId,
            };
            Assert.Equal(3, new HashSet<int>(ids).Count);
            Assert.Equal(0, allocator.AliasedLeaseCount);
            if (previous != null) Assert.Equal(previous, ids);
            previous = ids;
        }

        Assert.Equal(3, backing.Created.Count);
        Assert.Empty(backing.Discards);
    }

    [Fact]
    public void LeasesArriveInFrameOrder()
    {
        var allocator = new TransientAllocator(new FakeBacking(), aliasing: true);
        allocator.BeginFrame();
        allocator.Acquire(Half, 3, 4);
        Assert.Throws<InvalidOperationException>(() => allocator.Acquire(Half, 2, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Acquire(Half, 5, 4));
    }

    [Fact]
    public void BindRebindsOnlyWithAliasingOnAndTheNextFrameRestores()
    {
        var backing = new FakeBacking();
        backing.Logical[7] = Half;
        backing.Logical[8] = Half;

        var off = new TransientAllocator(backing, aliasing: false);
        off.BeginFrame();
        Assert.Equal(7, off.Bind(7, 0, 1));
        Assert.Empty(backing.Rebinds);

        var on = new TransientAllocator(backing, aliasing: true);
        on.BeginFrame();
        int physical7 = on.Bind(7, 0, 0);
        int physical8 = on.Bind(8, 1, 1);
        Assert.Equal(physical7, physical8);
        Assert.Equal(physical7, backing.Rebinds[7]);
        Assert.Equal(physical8, backing.Rebinds[8]);
        // Not describable (a cube or depth texture): served by itself.
        Assert.Equal(9, on.Bind(9, 2, 2));

        on.BeginFrame();
        Assert.Empty(backing.Rebinds);
    }

    [Fact]
    public void ImagesUnusedForIdleFramesAreReleased()
    {
        var backing = new FakeBacking();
        var allocator = new TransientAllocator(backing, aliasing: false);
        allocator.BeginFrame();
        allocator.Acquire(Half, 0, 0);
        int second = allocator.Acquire(Half, 1, 1).TextureId;

        for (int frame = 0; frame <= TransientAllocator.IdleFrames + 1; frame++)
        {
            allocator.BeginFrame();
            allocator.Acquire(Half, 0, 0);
        }

        Assert.Equal(new[] { second }, backing.Destroyed);
        Assert.Equal(1, allocator.PhysicalImageCount);
    }

    [Fact]
    public void TheFeedbackCopyPoolReleasesCopiesAfterTheTimelinePasses()
    {
        var clock = new FakeClock { FrameRecorded = 1 };
        int next = 1;
        var destroyed = new List<int>();
        var pool = new FeedbackCopyPool(clock, _ => next++, destroyed.Add, idleFrames: 3);
        var desc = new FeedbackCopyDesc(16, 16, Format.R8G8B8A8Unorm, 1, 1, false);
        var other = new FeedbackCopyDesc(8, 8, Format.R8G8B8A8Unorm, 1, 1, false);

        // Frame 1: two passes reuse one copy; the command stream orders them.
        int a = pool.Acquire(desc);
        pool.Release(a);
        Assert.Equal(a, pool.Acquire(desc));
        Assert.NotEqual(a, pool.Acquire(other));
        pool.Release(a);
        pool.EndFrame();

        // Frame 2: frame 1 has not completed, so its copies wait.
        clock.FrameRecorded = 2;
        pool.Collect();
        Assert.Equal(1, pool.Retiring);
        int b = pool.Acquire(desc);
        Assert.NotEqual(a, b);
        pool.Release(b);
        pool.EndFrame();

        // Frame 3: frame 1 completed, frame 2 did not.
        clock.FrameRecorded = 3;
        clock.FrameCompleted = 1;
        pool.Collect();
        Assert.Equal(1, pool.Retiring);
        Assert.Equal(a, pool.Acquire(desc));
        pool.Release(a);
        pool.EndFrame();
        Assert.Equal(3, pool.Created);

        // Nothing taken for more than idleFrames frames once everything completed: destroyed.
        clock.FrameCompleted = 100;
        for (int frame = 0; frame < 6; frame++)
        {
            clock.FrameRecorded++;
            pool.EndFrame();
            pool.Collect();
        }
        Assert.Contains(a, destroyed);
        Assert.Contains(b, destroyed);
        Assert.Equal(1, pool.Live); // the 'other' copy is still taken
        Assert.Equal(1, pool.InUse);
    }

    [Fact]
    public void ADiscardedImageTransitionsFromUndefinedEvenIntoItsCurrentLayout()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        var output = new List<ImageTransition>();
        tracker.Require(0, 1, 0, 1, ResourceUsage.ColorWrite, false, output);
        output.Clear();
        Assert.Equal(0, tracker.Require(0, 1, 0, 1, ResourceUsage.ColorWrite, false, output));

        tracker.Discard();
        Assert.Equal(1, tracker.Require(0, 1, 0, 1, ResourceUsage.ColorWrite, false, output));
        BarrierSides sides = output[0].Sides;
        Assert.Equal(ImageLayout.Undefined, sides.OldLayout);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, sides.NewLayout);
        // The previous lifetime's write stays on the source side.
        Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, sides.SrcStage);
        Assert.Equal(AccessFlags2.ColorAttachmentWriteBit, sides.SrcAccess);
    }

    // ------------------------------------------------------------------ device

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

    private readonly record struct ChainResult(byte[] Pixels, int AliasedLeases, int PhysicalImages, int SyncMessages);

    /// <summary>
    /// A post chain like bloom's: pass 0 fills transient 1, passes 1 and 2 read the previous
    /// transient into the next, pass 3 reads transient 3 into a persistent output. Lifetimes
    /// [0,1], [1,2], [2,3]: with aliasing on, transient 3 takes transient 1's image.
    /// </summary>
    private unsafe ChainResult? RunChain(bool aliasing, int frames)
    {
        const int size = 16;
        VulkanDevice seam = NewDevice();
        seam.TransientAliasingOverride = aliasing;
        if (!seam.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            _output.WriteLine("Vulkan unavailable: " + failureReason);
            seam.Dispose();
            return null;
        }

        using (seam)
        {
            int fill = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = vec4(floor(uv.x * 16.0) / 16.0, floor(uv.y * 16.0) / 16.0, 0.25, 1.0); }
                """, "chain-fill");
            int rotate = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D src;
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                void main(void)
                {
                    vec4 t = texelFetch(src, ivec2(gl_FragCoord.xy), 0);
                    outColor = vec4(t.g, t.b, t.r * 0.5 + 0.25, 1.0);
                }
                """, "chain-rotate");
            seam.SetSamplerUnit(rotate, "src", 0);

            int[] transients = new int[3];
            int[] framebuffers = new int[4];
            for (int i = 0; i < 3; i++)
            {
                transients[i] = seam.CreateTransientTexture2D(size, size, EnumTextureInternalFormat.Rgba8, 2 + i);
                framebuffers[i] = seam.CreateFramebuffer(size, size);
                seam.AttachTexture(framebuffers[i], EnumFramebufferAttachment.ColorAttachment0, transients[i], 0);
                seam.SetDrawBuffers(framebuffers[i], 1);
            }
            int output = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            framebuffers[3] = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffers[3], EnumFramebufferAttachment.ColorAttachment0, output, 0);
            seam.SetDrawBuffers(framebuffers[3], 1);
            Assert.Equal(3, seam.Transients.OptedInCount);

            var pixels = new byte[size * size * 4];
            int aliased = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();
                for (int pass = 0; pass < 3; pass++) seam.BindTransientForFrame(transients[pass], pass, pass + 1);
                aliased += seam.Transients.AliasedLeaseCount;

                for (int pass = 0; pass < 4; pass++)
                {
                    seam.BindFramebuffer(framebuffers[pass]);
                    seam.SetViewport(0, 0, size, size);
                    seam.SetDepthTest(false);
                    seam.SetCullFace(false);
                    seam.SetBlend(false, EnumBlendMode.Standard);
                    if (pass == 0)
                    {
                        seam.UseProgram(fill);
                    }
                    else
                    {
                        seam.UseProgram(rotate);
                        seam.BindTexture(0, transients[pass - 1]);
                    }
                    seam.DrawFullscreenTriangle();
                }
                seam.BindTexture(0, 0);

                if (frame == frames - 1)
                {
                    seam.BindFramebuffer(framebuffers[3]);
                    fixed (byte* destination = pixels)
                        seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
                }
                seam.Present();
            }

            int physical = seam.Transients.PhysicalImageCount;
            AssertClean(seam);
            int sync = 0;
            List<string> messages = MessagesOf(seam);
            lock (messages)
            {
                foreach (string message in messages)
                {
                    if (message.Contains("SYNC-", StringComparison.Ordinal)) sync++;
                }
            }
            return new ChainResult(pixels, aliased, physical, sync);
        }
    }

    [SkippableFact]
    public void APostChainReadsBackTheSameWithAliasingOnAndOff()
    {
        const int frames = 5;
        ChainResult? off = RunChain(aliasing: false, frames);
        Skip.If(off == null, "No usable Vulkan device.");
        ChainResult? on = RunChain(aliasing: true, frames);
        Assert.NotNull(on);

        _output.WriteLine("aliasing off: aliased leases " + off!.Value.AliasedLeases + ", physical images " +
                          off.Value.PhysicalImages);
        _output.WriteLine("aliasing on: aliased leases " + on!.Value.AliasedLeases + ", physical images " +
                          on.Value.PhysicalImages);

        Assert.Equal(0, off.Value.AliasedLeases);
        Assert.Equal(0, off.Value.PhysicalImages);
        Assert.Equal(frames, on.Value.AliasedLeases);
        Assert.Equal(2, on.Value.PhysicalImages);

        // Three rotations of (x/16, y/16, 0.25): a known value, so neither run is blank.
        int x = 5, y = 9;
        int index = (y * 16 + x) * 4;
        byte r0 = 80, g0 = 144, b0 = 64; // fill as UNORM8 (5/16, 9/16, 0.25)
        (byte r1, byte g1, byte b1) = (g0, b0, Half8(r0));
        (byte r2, byte g2, byte b2) = (g1, b1, Half8(r1));
        (byte r3, byte g3, byte b3) = (g2, b2, Half8(r2));
        _output.WriteLine("pixel " + x + "," + y + ": " + on.Value.Pixels[index] + "," + on.Value.Pixels[index + 1] +
                          "," + on.Value.Pixels[index + 2] + " expected about " + r3 + "," + g3 + "," + b3);
        Assert.InRange(on.Value.Pixels[index], (byte)Math.Max(0, r3 - 2), (byte)Math.Min(255, r3 + 2));
        Assert.InRange(on.Value.Pixels[index + 1], (byte)Math.Max(0, g3 - 2), (byte)Math.Min(255, g3 + 2));
        Assert.InRange(on.Value.Pixels[index + 2], (byte)Math.Max(0, b3 - 2), (byte)Math.Min(255, b3 + 2));

        Assert.Equal(off.Value.Pixels, on.Value.Pixels);
    }

    private static byte Half8(byte value) => (byte)Math.Round(value / 255.0 * 0.5 * 255.0 + 0.25 * 255.0);

    [SkippableFact]
    public void AliasingOnHasZeroSyncHazards()
    {
        ChainResult? on = RunChain(aliasing: true, frames: 8);
        Skip.If(on == null, "No usable Vulkan device.");
        Assert.Equal(0, on!.Value.SyncMessages);
    }

    [SkippableFact]
    public unsafe void ReadSelfCopiesArePooledAndRetiredOnTheTimeline()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, """
                #version 330 core
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                                       -1 + ((gl_VertexID & 2) << 1), 0, 1);
                }
                """, """
                #version 330 core
                uniform sampler2D atlas;
                out vec4 color;
                void main() {
                    color = texelFetch(atlas, ivec2(gl_FragCoord.x < 1.0 ? 1 : 0, 0), 0);
                }
                """, "readself-pool");
            byte[] original = { 255, 0, 0, 255, 0, 255, 0, 255 };
            int texture;
            fixed (byte* pixels = original)
                texture = seam.CreateTexture2D(2, 1, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
            int framebuffer = seam.CreateFramebuffer(2, 1);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetSamplerUnit(program, "atlas", 0);
            seam.BindTexture(0, texture);
            FeedbackCopyPool pool = seam.ReadSelfCopiesForTests;
            long copiesBefore = VulkanStats.ReadSelfCopies;

            const int frames = 12;
            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.UseProgram(program);
                seam.SetViewport(0, 0, 2, 1);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                // Two passes that read what they write: each swaps the texels, so the
                // second only restores the original if its copy was refreshed.
                seam.DrawFullscreenTriangle();
                seam.DrawFullscreenTriangle();
                if (frame == 0) Assert.Equal(1, pool.Created);
                seam.Present();

                var output = new byte[8];
                fixed (byte* destination = output)
                    seam.ReadDefaultFramebuffer(0, 0, 2, 1, (IntPtr)destination);
                Assert.Equal(original, output);
            }

            _output.WriteLine("copies created " + pool.Created + ", live " + pool.Live + ", retiring " +
                              pool.Retiring + ", destroyed " + pool.Destroyed);
            // A copy released in one frame is reused once the timeline passed that
            // frame, so the pool stays at the frames in flight plus one.
            Assert.InRange(pool.Created, 1, 3);
            Assert.Equal(pool.Created - pool.Destroyed, pool.Live);
            Assert.True(VulkanStats.ReadSelfCopies - copiesBefore >= 2 * frames);

            seam.DeleteFramebuffer(framebuffer);
            seam.DeleteTexture(texture);
            AssertClean(seam);
        }
    }
}
