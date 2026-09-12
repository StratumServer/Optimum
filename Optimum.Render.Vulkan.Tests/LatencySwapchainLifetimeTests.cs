using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Latency review 2026-09-12, seam S5: the backend has to be told when the
/// swapchain handle it holds goes away, not only when a new one appears.
///
/// <c>Swapchain.Build</c> hands the current slot to the retirement queue before
/// it creates the replacement, and does so even when that creation fails, after
/// which the client keeps rendering frames on a chain that no longer exists.
/// VK_NV_low_latency2 keys every one of its calls on a live
/// <c>VkSwapchainKHR</c> (vkLatencySleepNV, vkSetLatencyMarkerNV,
/// vkGetLatencyTimingsNV, vkSetLatencySleepModeNV), so without a retirement
/// notice it would keep calling into a handle the retirement queue is about to
/// destroy. Before the fix there was no notice at all.
/// </summary>
public class LatencySwapchainLifetimeTests
{
    private const int Width = 256;
    private const int Height = 192;

    private readonly ITestOutputHelper _output;

    public LatencySwapchainLifetimeTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void EveryRetiredSwapchainIsAnnouncedBeforeTheOneThatReplacesIt()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        var latency = new RecordingLatencyBackend();

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            device.SetLatencyBackend(latency);

            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            int creations;
            using (device)
            {
                VulkanDevice seam = device;
                Swapchain swapchain = device.SwapchainForTests!;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginLatencyFrame();
                        seam.BeginFrame();
                        seam.NoteRenderStageStarted();
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

                // The first swapchain exists without anything having been retired.
                Assert.Single(latency.Swapchains);
                Assert.Equal(0, latency.SwapchainRetirements);

                RenderFrames(2, Width, Height);

                (int W, int H)[] sizes = { (320, 240), (200, 150), (256, 192) };
                foreach ((int w, int h) in sizes)
                {
                    GLFW.SetWindowSize(window, w, h);
                    GLFW.PollEvents();
                    seam.Resize(w, h);
                    RenderFrames(2, w, h);
                }

                creations = swapchain.Creations;
                Assert.True(creations > 1, creations + " swapchain creations");
                GpuTest.AssertClean(seam);
            }

            // In order: handle, then (retired, handle) for every rebuild, and a
            // final retirement when the device disposed the swapchain. A new
            // handle is never announced while the old one is still the backend's.
            _output.WriteLine(creations + " creations, " + latency.SwapchainRetirements + " retirements, events: " +
                string.Join(", ", System.Array.ConvertAll(latency.SwapchainEvents.ToArray(), Describe)));

            Assert.Equal(creations, latency.Swapchains.Count);
            Assert.Equal(creations, latency.SwapchainRetirements);

            bool holdsOne = false;
            foreach (SwapchainKHR handle in latency.SwapchainEvents)
            {
                if (handle.Handle != 0)
                {
                    Assert.False(holdsOne, "a new swapchain was announced while the old one was still live");
                    holdsOne = true;
                }
                else
                {
                    holdsOne = false;
                }
            }

            // Disposal is the last word: the backend holds nothing afterwards.
            Assert.False(holdsOne, "the backend still holds a swapchain after the device was disposed");
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private static string Describe(SwapchainKHR handle) => handle.Handle == 0 ? "retired" : "created";

    /// <summary>
    /// The NV backend is the one that holds a handle, and it must stand down
    /// while there is none: with no swapchain the sleep does not call the driver
    /// and reports nothing, whatever the mode says. Gated on the extension, so it
    /// runs on the 4070 and skips everywhere else.
    /// </summary>
    [SkippableFact]
    public unsafe void TheNvBackendMakesNoCallWhileItHoldsNoSwapchain()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
            device.ConfigureContextOptions = options =>
            {
                suite?.Invoke(options);
                options.LatencyBackend = LatencyBackendKind.NvLowLatency2;
            };

            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                if (device.Latency is not NvLowLatency2Backend latency)
                {
                    Skip.If(true, "VK_NV_low_latency2 is not usable on this device (running " +
                        LatencyBackends.Token(device.Latency.Kind) + ").");
                    return;
                }

                latency.Apply(new LatencySettings(LatencyMode.On, 0));
                int applications = latency.SleepModeApplications;
                int sleeps = latency.SleepCount;

                latency.OnSwapchainRetired();

                // Every entry point of the extension takes the swapchain, so all
                // of them stand down: no sleep, no marker, no timings, no mode.
                Assert.Equal(0UL, latency.Sleep(1));
                latency.Marker(1, LatencyMarker.InputSample);
                Assert.Empty(latency.TakeReports());
                latency.Apply(new LatencySettings(LatencyMode.On, 16666));
                Assert.Equal(applications, latency.SleepModeApplications);
                Assert.Equal(sleeps, latency.SleepCount);

                GpuTest.AssertClean(device);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }
}
