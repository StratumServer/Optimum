using System;
using System.IO;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 6, pure half: the per-slot indirect ring never wraps inside a
/// frame and grows only at a frame boundary.
/// </summary>
public class IndirectRingWrapTests
{
    private const ulong Command = 20; // sizeof(DrawIndexedIndirectCommand)

    [Fact]
    public void AllocationsInOneFrameNeverWrapBackToOffsetZero()
    {
        var ring = new IndirectRing(2, minimumCapacity: 100);
        Assert.False(ring.BeginFrame(0, out _));

        Assert.True(ring.NeedsBuffer(Command, out ulong capacity));
        Assert.Equal(100UL, capacity);
        ring.Attach(capacity);
        Assert.False(ring.NeedsBuffer(Command, out _));

        for (ulong i = 0; i < 5; i++)
        {
            Assert.True(ring.TryAllocate(Command, out ulong offset));
            Assert.Equal(i * Command, offset);
        }

        // Full: the wrapping ring would have handed out offset 0 here, a region a
        // draw of this very frame is still going to read.
        Assert.False(ring.TryAllocate(Command, out _));
        Assert.False(ring.TryAllocate(Command, out _));
        Assert.Equal(100UL, ring.CursorOf(0));
        Assert.Equal(2, ring.OverflowsOf(0));
        Assert.Equal(7 * Command, ring.FrameUsageOf(0));
        Assert.Equal(100UL, ring.CapacityOf(0));
    }

    [Fact]
    public void ASlotResetsOnlyAtItsOwnFrameBoundary()
    {
        var ring = new IndirectRing(2, minimumCapacity: 100);
        ring.BeginFrame(0, out _);
        ring.NeedsBuffer(Command, out ulong capacity);
        ring.Attach(capacity);
        for (int i = 0; i < 3; i++) Assert.True(ring.TryAllocate(Command, out _));

        // Slot 1's frame starts while slot 0's may still be executing: slot 0's
        // regions stay where they are.
        ring.BeginFrame(1, out _);
        Assert.Equal(1, ring.Current);
        Assert.Equal(3 * Command, ring.CursorOf(0));
        Assert.True(ring.NeedsBuffer(Command, out capacity));
        ring.Attach(capacity);
        Assert.True(ring.TryAllocate(Command, out ulong offset));
        Assert.Equal(0UL, offset);

        ring.BeginFrame(0, out _);
        Assert.Equal(0UL, ring.CursorOf(0));
        Assert.Equal(Command, ring.CursorOf(1));
        Assert.True(ring.TryAllocate(Command, out offset));
        Assert.Equal(0UL, offset);
    }

    [Fact]
    public void GrowthWaitsForTheFrameBoundaryAndThenFitsTheBusiestFrame()
    {
        var ring = new IndirectRing(2, minimumCapacity: 100);
        ring.BeginFrame(0, out _);
        ring.NeedsBuffer(Command, out ulong capacity);
        ring.Attach(capacity);

        int fitted = 0;
        for (int i = 0; i < 8; i++)
        {
            if (ring.TryAllocate(Command, out _)) fitted++;
        }
        Assert.Equal(5, fitted);
        Assert.Equal(100UL, ring.CapacityOf(0)); // no growth mid-frame

        // A slot without a buffer is not "grown"; it is created at its first
        // allocation, already big enough for the busiest frame so far.
        Assert.False(ring.BeginFrame(1, out _));
        Assert.Equal(8 * Command, ring.PeakFrameUsage);
        Assert.True(ring.NeedsBuffer(Command, out capacity));
        Assert.True(capacity >= 8 * Command);

        Assert.True(ring.BeginFrame(0, out capacity));
        Assert.True(capacity >= 8 * Command);
        ring.Attach(capacity);
        for (int i = 0; i < 8; i++) Assert.True(ring.TryAllocate(Command, out _));
        Assert.Equal(0, ring.OverflowsOf(0));

        // Fitting now: the next boundary does not grow again.
        ring.BeginFrame(1, out _);
        Assert.False(ring.BeginFrame(0, out _));
    }

    [Fact]
    public void CapacityHasHeadroomAFloorAndA64KiBGrain()
    {
        var ring = new IndirectRing(2);
        Assert.Equal(IndirectRing.DefaultMinimumCapacity, ring.CapacityFor(0));
        Assert.Equal(IndirectRing.DefaultMinimumCapacity, ring.CapacityFor(Command));
        Assert.Equal(1536UL * 1024, ring.CapacityFor(1024UL * 1024));

        var random = new Random(6);
        for (int i = 0; i < 200; i++)
        {
            ulong demand = (ulong)random.Next(0, 64 * 1024 * 1024);
            ulong capacity = ring.CapacityFor(demand);
            Assert.True(capacity >= demand + demand / 2);
            Assert.Equal(0UL, capacity % (64UL * 1024));
        }
    }

    [Fact]
    public void MisuseIsRefused()
    {
        var ring = new IndirectRing(1, minimumCapacity: 100);
        Assert.Throws<InvalidOperationException>(() => ring.TryAllocate(Command, out _));

        ring.BeginFrame(0, out _);
        ring.Attach(100);
        Assert.True(ring.TryAllocate(Command * 3, out _));
        Assert.Throws<InvalidOperationException>(() => ring.Attach(Command));
    }
}

/// <summary>Phase 1B step 6, pure half: only changed dynamic state is emitted.</summary>
public class DynamicStateCacheTests
{
    private static DynamicStateValues Values() => new()
    {
        Viewport = new Viewport(0, 0, 64, 64, 0, 1),
        Scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(64, 64)),
        CullMode = CullModeFlags.None,
        FrontFace = FrontFace.Clockwise,
        Topology = PrimitiveTopology.TriangleList,
        DepthCompare = CompareOp.Less,
        StencilCompare = CompareOp.Always,
        StencilCompareMask = 0xFF,
        StencilWriteMask = 0xFF,
        LineWidth = 1f,
    };

    [Fact]
    public void EveryBitIsOneCommandAndAllIsTheFullSet()
    {
        Assert.Equal(VulkanStats.DynamicStateCommandsPerDraw, DynamicStateCache.CommandCount(DynamicStateDirty.All));
    }

    [Fact]
    public void RepeatedIdenticalStateEmitsNothing()
    {
        var cache = new DynamicStateCache();
        DynamicStateValues values = Values();

        Assert.Equal(DynamicStateDirty.Everything, cache.Update(1, values));
        for (int i = 0; i < 10; i++) Assert.Equal(DynamicStateDirty.None, cache.Update(1, values));
    }

    [Fact]
    public void OnlyTheChangedFieldsAreDirty()
    {
        var cache = new DynamicStateCache();
        DynamicStateValues values = Values();
        cache.Update(1, values);

        values.Viewport.Width = 32;
        Assert.Equal(DynamicStateDirty.Viewport, cache.Update(1, values));

        values.DepthTest = !values.DepthTest;
        values.DepthWrite = !values.DepthWrite;
        Assert.Equal(DynamicStateDirty.DepthTestEnable | DynamicStateDirty.DepthWriteEnable, cache.Update(1, values));

        values.StencilDepthFail = StencilOp.Replace;
        Assert.Equal(DynamicStateDirty.StencilOp, cache.Update(1, values));

        values.Scissor.Offset.Y = 4;
        values.LineWidth = 2f;
        Assert.Equal(DynamicStateDirty.Scissor | DynamicStateDirty.LineWidth, cache.Update(1, values));

        values.LineWidth = float.NaN;
        cache.Update(1, values);
        Assert.Equal(DynamicStateDirty.None, cache.Update(1, values));
    }

    [Fact]
    public void ANewRecordingInvalidationOrDisabledCacheEmitsEverything()
    {
        var cache = new DynamicStateCache();
        DynamicStateValues values = Values();
        cache.Update(1, values);

        Assert.Equal(DynamicStateDirty.Everything, cache.Update(2, values));
        Assert.Equal(DynamicStateDirty.Everything, cache.Update(0, values));
        Assert.Equal(DynamicStateDirty.Everything, cache.Update(0, values));

        cache.Update(3, values);
        cache.Invalidate();
        Assert.Equal(DynamicStateDirty.Everything, cache.Update(3, values));

        cache.Enabled = false;
        Assert.Equal(DynamicStateDirty.Everything, cache.Update(3, values));
    }
}

/// <summary>Phase 1B step 6, pure half: short-lived resources by id watermark.</summary>
public class ResourceAgeTests
{
    [Fact]
    public void BeforeTheWindowFillsEveryResourceIsYoungButIdZeroNeverIs()
    {
        var age = new ResourceAge();
        age.NoteFrame(10);
        Assert.True(age.IsShortLived(1));
        Assert.False(age.IsShortLived(0));
    }

    [Fact]
    public void AResourceIsShortLivedForExactlyTheLastNFrames()
    {
        var age = new ResourceAge(60);
        // Frame k starts with ids up to 10k issued; frame k creates 10k+1 .. 10k+10.
        for (ulong frame = 0; frame < 100; frame++) age.NoteFrame(frame * 10);

        // Frame 99 records now; frames 40..99 are the last 60.
        Assert.True(age.IsShortLived(401));  // created in frame 40
        Assert.False(age.IsShortLived(400)); // created in frame 39
        Assert.True(age.IsShortLived(999));

        age.ShortLivedFrames = 1;
        Assert.True(age.IsShortLived(991));
        Assert.False(age.IsShortLived(990));

        age.ShortLivedFrames = 0;
        Assert.False(age.IsShortLived(999));
        Assert.Throws<ArgumentOutOfRangeException>(() => age.ShortLivedFrames = 61);
    }

    [Fact]
    public void ASetIsShortLivedWhenAnyResourceItNamesIs()
    {
        var age = new ResourceAge(2);
        age.NoteFrame(100);
        age.NoteFrame(200);
        age.NoteFrame(300); // frames 1..2 are the window: ids above 200

        var old = new SamplerBindingValue(0, new ImageView(1), new Sampler(1), Resource: 150);
        var young = new SamplerBindingValue(1, new ImageView(2), new Sampler(1), Resource: 250);
        var permanent = new BufferBindingValue(0, new Silk.NET.Vulkan.Buffer(3), 0, 64, Resource: 0);

        Assert.False(age.NamesShortLived(new DescriptorSetContents(1, 1, new[] { old }, new[] { permanent })));
        Assert.True(age.NamesShortLived(new DescriptorSetContents(1, 1, new[] { old, young }, Array.Empty<BufferBindingValue>())));
        Assert.True(age.NamesShortLived(new DescriptorSetContents(1, 0, Array.Empty<SamplerBindingValue>(),
            new[] { permanent, new BufferBindingValue(1, new Silk.NET.Vulkan.Buffer(4), 0, 64, Resource: 201) })));
    }
}

/// <summary>Phase 1B step 6: GetError is a counter read until there is an error.</summary>
public class GetErrorCounterTests
{
    [Fact]
    public void OnlyErrorsAreReportedOnceInOrder()
    {
        // Never initialised: no context, no GPU; only the diagnostics queue is exercised.
        using VulkanDevice device = GpuTest.NewDevice();
        Assert.Null(device.GetError());

        device.AddDiagnosticForTests("a warning the layers raised");
        Assert.Null(device.GetError());

        device.AddDiagnosticForTests(VulkanContext.ErrorPrefix + "first");
        device.AddDiagnosticForTests("advice");
        device.AddDiagnosticForTests(VulkanContext.ErrorPrefix + "second");
        Assert.Equal(VulkanContext.ErrorPrefix + "first\n" + VulkanContext.ErrorPrefix + "second", device.GetError());
        Assert.Null(device.GetError());
    }

    [Fact]
    public void ErrorsFromManyThreadsAreAllKept()
    {
        // Never initialised: no context, no GPU; only the diagnostics queue is exercised.
        using VulkanDevice device = GpuTest.NewDevice();
        Parallel.For(0, 4, thread =>
        {
            for (int i = 0; i < 200; i++) device.AddDiagnosticForTests(VulkanContext.ErrorPrefix + thread + ":" + i);
        });

        string? errors = device.GetError();
        Assert.NotNull(errors);
        Assert.Equal(800, errors!.Split('\n').Length);
        Assert.Null(device.GetError());
    }

    [Fact]
    public void TheSteadyStatePathIsACounterReadWithNoAllocation()
    {
        string device = File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, "Optimum.Render.Vulkan", "VulkanDevice.cs"));
        int start = device.IndexOf("public string GetError()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string body = device.Substring(start, device.IndexOf("\n    }\n", start, StringComparison.Ordinal) - start);

        int counter = body.IndexOf("if (_errorCount == 0) return null!;", StringComparison.Ordinal);
        Assert.True(counter >= 0, "GetError must start with the counter read");
        Assert.True(counter < body.IndexOf("lock (_errors)", StringComparison.Ordinal));
        Assert.DoesNotContain("new List", body);
        Assert.Contains("private volatile int _errorCount;", device);
        Assert.DoesNotContain("_diagnostics", device);
    }
}
