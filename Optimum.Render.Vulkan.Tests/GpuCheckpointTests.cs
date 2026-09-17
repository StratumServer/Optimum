using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The device-loss diagnostic. The marker is the only part with logic of its
/// own: the driver stores whatever value it is handed and returns it verbatim,
/// so what matters is that the value decodes to what was encoded.
/// </summary>
public class GpuCheckpointTests
{
    private readonly ITestOutputHelper _output;

    public GpuCheckpointTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ADrawMarkerRoundTripsItsKindAndPayload()
    {
        nint marker = CheckpointMarker.Draw(CheckpointKind.DrawMulti, program: 0xBEEF, target: 7, mesh: 123456);

        Assert.NotEqual((nint)0, marker);
        Assert.Equal(CheckpointKind.DrawMulti, CheckpointMarker.KindOf(marker));
        Assert.Equal(123456u, CheckpointMarker.BOf(marker));
        Assert.Equal(
            "multi-draw with program 48879 'chunkopaque' mesh 123456 into framebuffer 7",
            CheckpointMarker.Describe(marker, id => id == 0xBEEF ? "chunkopaque" : null));
    }

    [Fact]
    public void EveryKindIsNonZeroAndReadsBackDistinctly()
    {
        nint frame = CheckpointMarker.FrameBegin(42);
        nint upload = CheckpointMarker.Upload(9, 4096, 2048);
        nint mips = CheckpointMarker.Mipmaps(9, 13);
        nint blit = CheckpointMarker.PresentBlit(2, 42);
        nint fullscreen = CheckpointMarker.Draw(CheckpointKind.Fullscreen, 3, 1, 0);

        Assert.All(new[] { frame, upload, mips, blit, fullscreen }, m => Assert.NotEqual((nint)0, m));

        Assert.Equal("frame 42 begins", CheckpointMarker.Describe(frame));
        Assert.Equal("upload of 4096x2048 texels into texture 9", CheckpointMarker.Describe(upload));
        Assert.Equal("mipmap generation for texture 9 (13 levels)", CheckpointMarker.Describe(mips));
        Assert.Equal("blit of frame 42 into swapchain image 2", CheckpointMarker.Describe(blit));
        Assert.Equal("fullscreen draw with program 3 into framebuffer 1", CheckpointMarker.Describe(fullscreen));
    }

    /// <summary>
    /// On a driver that offers checkpoints, reading them from a healthy queue
    /// must at least not fail: the crash path calls this with nothing to lose,
    /// and a diagnostic that throws is worse than none.
    /// </summary>
    [SkippableFact]
    public void CheckpointsCanBeReadFromAHealthyQueue()
    {
        var options = GpuTest.ContextOptions();
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason),
            "No usable Vulkan device: " + reason);

        using (context)
        {
            _output.WriteLine("checkpoints: " + context!.CheckpointsAvailable +
                ", device fault: " + context.DeviceFaultAvailable);
            Skip.IfNot(context.CheckpointsAvailable, "Driver has no VK_NV_device_diagnostic_checkpoints.");

            var checkpoints = context.ReadQueueCheckpoints();
            Assert.NotNull(checkpoints);
        }
    }
}
