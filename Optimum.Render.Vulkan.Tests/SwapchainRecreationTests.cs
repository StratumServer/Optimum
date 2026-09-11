using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 4 against a real (hidden) window: resizing and vsync toggles
/// rebuild the swapchain with oldSwapchain while frames are in flight, never
/// wait for the device to go idle, retire every replaced slot once the GPU is
/// past it, and stay clean under sync and best-practices validation.
/// </summary>
public class SwapchainRecreationTests
{
    private const int Width = 256;
    private const int Height = 192;

    private readonly ITestOutputHelper _output;

    public SwapchainRecreationTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void AHiddenWindowResizeLoopRecreatesWithoutWaitingAndStaysClean()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                IOptimumGraphicsDevice seam = device;
                Swapchain swapchain = device.SwapchainForTests!;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginFrame();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, w, h);
                        seam.SetDepthTest(false);
                        seam.SetCullFace(false);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                    }
                }

                RenderFrames(3, Width, Height);

                (int W, int H)[] sizes =
                {
                    (320, 240), (200, 150), (512, 384), (256, 192), (300, 200), (640, 360), (257, 193),
                };

                long idleBefore = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle);
                int creationsBefore = swapchain.Creations;
                int iterations = 0;
                for (int round = 0; round < 2; round++)
                {
                    foreach ((int w, int h) in sizes)
                    {
                        GLFW.SetWindowSize(window, w, h);
                        GLFW.PollEvents();
                        seam.Resize(w, h);
                        if (iterations % 3 == 2) seam.SetVSync(iterations % 2 == 0);
                        // A frame is recorded before the rebuild happens at its
                        // acquire, so the old chain still has work in flight.
                        RenderFrames(3, w, h);
                        iterations++;
                    }
                }

                long idleWaits = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle) - idleBefore;
                int creations = swapchain.Creations - creationsBefore;
                _output.WriteLine($"{iterations} resizes: {creations} swapchains created, {swapchain.RetiredPending} slots pending, " +
                    $"final extent {swapchain.Extent.Width}x{swapchain.Extent.Height}, mode {swapchain.PresentMode}");

                Assert.Equal(0, idleWaits);
                Assert.True(creations >= iterations, $"{iterations} resizes rebuilt only {creations} swapchains");
                Assert.False(swapchain.Parked);
                Assert.Null(swapchain.RebuildFailure);

                // Every replaced slot goes once the frames after it completed.
                for (int frame = 0; frame < 8 && swapchain.RetiredPending > 0; frame++) RenderFrames(1, 257, 193);
                Assert.Equal(0, swapchain.RetiredPending);

                SwapchainSlot slot = swapchain.CurrentSlotForTests!;
                Assert.Equal(AcquireSemaphoreFreeList.CapacityFor(slot.ImageCount), slot.AcquireSemaphoreCount);
                // Nothing leaked: every semaphore is free or parked behind a submitted present.
                Assert.Equal(slot.AcquireSemaphoreCount, slot.FreeAcquireSemaphores + slot.PendingAcquireSemaphores);

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }
}
