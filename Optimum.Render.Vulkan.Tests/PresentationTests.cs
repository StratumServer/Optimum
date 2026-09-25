using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class PresentationTests(ITestOutputHelper output)
{
    private sealed class Clock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    private sealed class Resource(Action destroy) : IDisposable
    {
        public void Dispose() => destroy();
    }

    [Fact]
    public void RetirementRequiresBothRenderingAndPresentationCompletion()
    {
        var clock = new Clock();
        var queue = new SwapchainRetirement(clock);
        var disposed = new List<int>();
        bool fence = false;
        queue.Retire(new Resource(() => disposed.Add(1)), 7, () => fence);
        queue.NoteSuccessorReacquired(8);
        clock.FrameCompleted = 100;
        Assert.Equal(0, queue.Collect()); // Timeline progress cannot replace the fence.
        fence = true;
        Assert.Equal(1, queue.Collect());
        queue.Retire(new Resource(() => disposed.Add(2)), 101, () => true);
        Assert.Equal(0, queue.Collect()); // Nor can the fence replace render completion.
        clock.FrameCompleted = 101;
        Assert.Equal(1, queue.Collect());
        Assert.Equal(new[] { 1, 2 }, disposed);
        Assert.Equal(0, queue.Collect());
    }

    [Fact]
    public void ResizeStormsWithoutFencesWaitForASuccessorReacquisition()
    {
        var clock = new Clock { FrameCompleted = 100 };
        var queue = new SwapchainRetirement(clock);
        var disposed = new List<int>();
        queue.Retire(new Resource(() => disposed.Add(1)), 7);
        queue.Retire(new Resource(() => disposed.Add(2)), 9);
        Assert.Equal(0, queue.Collect());
        queue.NoteSuccessorReacquired(101);
        queue.Retire(new Resource(() => disposed.Add(3)), 102);
        Assert.Equal(0, queue.Collect());
        clock.FrameCompleted = 110;
        Assert.Equal(2, queue.Collect());
        Assert.Equal(new[] { 1, 2 }, disposed);
        queue.NoteSuccessorReacquired(111);
        clock.FrameCompleted = 111;
        Assert.Equal(1, queue.Collect());
        queue.Retire(new Resource(() => disposed.Add(4)), 0);
        Assert.Equal(1, queue.Collect());
        Assert.Equal(new[] { 1, 2, 3, 4 }, disposed);
    }

    [Fact]
    public void AcquireSemaphoresCannotBeReissuedWhileTheirWaitIsPending()
    {
        var pool = new AcquireSemaphoreFreeList(new ulong[] { 11, 22 });
        ulong first = pool.Take(0), second = pool.Take(0);
        pool.ReturnAfter(first, 8);
        pool.Return(second); // Failed acquisition did not signal it.
        Assert.Equal(second, pool.Take(7));
        Assert.Throws<InvalidOperationException>(() => pool.Take(7));
        Assert.Equal(first, pool.Take(8));
        Assert.Throws<InvalidOperationException>(() => pool.Take(8));
    }

    [Fact]
    public void SurfaceDecisionsBoundRetriesAndRespectSupportedModes()
    {
        Assert.Equal(AcquireAction.PresentThenRebuild, SwapchainPolicy.OnAcquire(Result.SuboptimalKhr, 0));
        Assert.Equal(AcquireAction.RebuildAndRetry, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 0));
        Assert.Equal(AcquireAction.SkipFrame, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 1));
        Assert.Equal(AcquireAction.Fail, SwapchainPolicy.OnAcquire(Result.ErrorDeviceLost, 0));
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(0, 20)));
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(20, 0)));
        var supported = new[] { PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr, PresentModeKHR.FifoRelaxedKhr };
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(true, false, supported));
        Assert.Equal(PresentModeKHR.FifoRelaxedKhr, SwapchainPolicy.ChoosePresentMode(true, true, supported));
        Assert.Equal(PresentModeKHR.MailboxKhr, SwapchainPolicy.ChoosePresentMode(false, false, supported));
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(false, false, new[] { PresentModeKHR.FifoKhr }));
        Assert.Equal(2u, SwapchainPolicy.ChooseImageCount(2, 2, PresentModeKHR.MailboxKhr));
        Assert.Equal(4u, SwapchainPolicy.ChooseImageCount(3, 0, PresentModeKHR.FifoKhr));
        var detector = new MissedVsyncDetector();
        for (int i = 0; i < 240; i++) Assert.False(detector.NoteInterval(16));
        bool promoted = false;
        for (int i = 0; i < 120; i++) promoted |= detector.NoteInterval(i % 3 == 0 ? 32 : 16);
        Assert.True(promoted);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(30)]
    public unsafe void RealWindowSurvivesResizeVsyncChangesAndResourceRetirement(int acquireDelayMs)
    {
        Window* window = null;
        VulkanDevice? device = null;
        try
        {
            Skip.IfNot(GLFW.Init(), "GLFW initialization unavailable.");
            Skip.IfNot(GLFW.VulkanSupported(), "Window system has no Vulkan support.");
            GLFW.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            GLFW.WindowHint(WindowHintBool.Visible, false);
            window = GLFW.CreateWindow(128, 96, "Optimum presentation acceptance", null, null);
            Skip.If(window == null, "Window creation unavailable.");
            GLFW.GetFramebufferSize(window, out int initialWidth, out int initialHeight);
            device = GpuTest.NewDevice();
            var configure = device.ConfigureContextOptions;
            device.ConfigureContextOptions = options =>
            {
                configure?.Invoke(options);
                options.ValidationFeatures = "sync,best";
                options.AcquireDelayForTests = TimeSpan.FromMilliseconds(acquireDelayMs);
            };
            Assert.True(device.Initialize((IntPtr)window, initialWidth, initialHeight, out string reason), reason);
            output.WriteLine(device.RendererString);
            var context = device.ContextForTests;
            Assert.True(context.ValidationEnabled);
            Assert.DoesNotContain("NOT APPLIED", context.ValidationSettingsApplied);
            output.WriteLine(context.Capabilities.LatencySummary);
            var swapchain = device.SwapchainForTests!;
            long idle = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle);
            ulong previousPresent = 0, previousTimeline = 0;
            var sizes = new[] { (128, 96), (193, 129), (160, 120), (128, 96) };
            for (int step = 0; step < sizes.Length; step++)
            {
                var (width, height) = sizes[step];
                GLFW.SetWindowSize(window, width, height);
                GLFW.PollEvents();
                GLFW.GetFramebufferSize(window, out int pixelWidth, out int pixelHeight);
                Assert.True(pixelWidth > 0 && pixelHeight > 0);
                device.Resize(pixelWidth, pixelHeight);
                device.SetVSync(step % 2 == 0);
                for (int frame = 0; frame < 8; frame++)
                {
                    device.BeginFrame();
                    int scratch = device.CreateTexture2D(4, 4, EnumTextureInternalFormat.Rgba8,
                        EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                    device.DeleteTexture(scratch);
                    device.BindDefaultFramebuffer();
                    device.ClearColor(0, (step * 8 + frame) / 255f, 0.2f, 0.4f, 1);
                    if (frame == 3)
                    {
                        byte[] pixel = new byte[4];
                        fixed (byte* pointer = pixel) device.ReadDefaultFramebuffer(0, 0, 1, 1, (IntPtr)pointer);
                        Assert.InRange((int)pixel[0], step * 8 + frame - 1, step * 8 + frame + 1);
                        Assert.InRange((int)pixel[1], 50, 52);
                        Assert.InRange((int)pixel[2], 101, 103);
                    }
                    device.Present();
                    var timing = device.LastPresentTimingsForTests;
                    Assert.True(timing.Presented);
                    Assert.True(timing.PresentValue > timing.RenderValue && timing.RenderValue > previousTimeline);
                    previousTimeline = timing.PresentValue;
                    Assert.True(timing.AcquireReturned >= timing.FrameSubmitted);
                    if (acquireDelayMs > 0)
                        Assert.True((timing.AcquireReturned - timing.FrameSubmitted) * 1000.0 / Stopwatch.Frequency >= acquireDelayMs * 0.9);
                    Assert.True(swapchain.PresentIds.LastPresentId > previousPresent);
                    previousPresent = swapchain.PresentIds.LastPresentId;
                    Assert.Equal(device.LatencyFrameId, swapchain.PresentIds.LastFrameId);
                    Assert.Null(swapchain.RebuildFailure);
                }
                Assert.Equal((uint)pixelWidth, swapchain.Extent.Width);
                Assert.Equal((uint)pixelHeight, swapchain.Extent.Height);
            }
            // Complete submitted work, then let the next acquisition collect retired
            // chains. A fence-enabled device may finish presentation after rendering.
            var drain = Stopwatch.StartNew();
            while (swapchain.RetiredPending > 0 && drain.Elapsed < TimeSpan.FromSeconds(5))
            {
                device.BeginFrame();
                device.BindDefaultFramebuffer();
                device.ClearColor(0, 0, 0, 0, 1);
                device.Present();
            }
            Assert.Equal(0, swapchain.RetiredPending);
            Assert.True(swapchain.Creations >= sizes.Length);
            Assert.Equal(idle, VulkanStats.WaitCount(WaitSite.DeviceWaitIdle));
            GpuTest.AssertClean(device);
        }
        finally
        {
            device?.Dispose();
            if (window != null) GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
        if (device != null) GpuTest.AssertClean(device); // Includes teardown diagnostics.
    }
}
