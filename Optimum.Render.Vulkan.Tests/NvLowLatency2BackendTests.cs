using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The NVIDIA Reflex backend (VK_NV_low_latency2) against a real, hidden window.
///
/// Gated on the extension the way the other optional-feature GPU tests are
/// gated: without an NVIDIA driver that advertises VK_NV_low_latency2 (and the
/// VK_KHR_present_id it depends on) the selection degrades to another backend and
/// the test skips. On this machine's RTX 4070 it runs.
///
/// What it pins, all of it a number rather than a look:
/// <list type="bullet">
/// <item><description>over ~30 frames the driver's <c>vkGetLatencyTimingsNV</c>
/// reports carry the present ids we stamped our markers with, and each maps back
/// to the frame that produced it;</description></item>
/// <item><description>the sleep mode is re-applied on every swapchain creation,
/// because nothing carries over through oldSwapchain;</description></item>
/// <item><description>sync + best-practices validation stays clean with the
/// extension enabled.</description></item>
/// </list>
/// </summary>
public class NvLowLatency2BackendTests
{
    private const int Width = 256;
    private const int Height = 192;

    /// <summary>Enough frames for the driver to have closed reports for most of them.</summary>
    private const int Frames = 30;

    private readonly ITestOutputHelper _output;

    public NvLowLatency2BackendTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void TheDriverReportsCarryOurPresentIdsAndTheSleepModeSurvivesRecreation()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window),
            "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
            device.ConfigureContextOptions = options =>
            {
                suite?.Invoke(options);
                // Selection would take NV on this device anyway; forcing it makes
                // the test independent of what auto prefers later.
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
                VulkanDevice seam = device;
                _output.WriteLine(seam.RendererString + ": " + device.ContextForTests.Capabilities.LatencySummary);

                if (device.Latency is not NvLowLatency2Backend latency)
                {
                    Skip.If(true, "VK_NV_low_latency2 is not usable on this device (running " +
                        LatencyBackends.Token(device.Latency.Kind) + ").");
                    return;
                }

                // The client's persisted setting is off by default; the backend is
                // driven here the way the client will drive it with the setting on.
                latency.Apply(new LatencySettings(LatencyMode.On, 0));
                Assert.True(latency.OwnsFrameCap,
                    "with the mode on, vkSetLatencySleepModeNV owns the cap and the client's limiter stands down");

                uint revision = device.ContextForTests.Capabilities.LatencySupport.NvLowLatency2SpecVersion;
                Swapchain swapchain = device.SwapchainForTests!;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);
                var stamped = new List<ulong>();
                var sleeps = new List<ulong>();

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        // The frame as the seams drive it: the id, the sleep, the
                        // two markers before input, then the frame itself. Every
                        // later marker is stamped by the device.
                        ulong frameId = seam.BeginLatencyFrame();
                        sleeps.Add(latency.Sleep(frameId));
                        latency.Marker(frameId, LatencyMarker.InputSample);
                        latency.Marker(frameId, LatencyMarker.SimulationStart);

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

                        if (device.LastPresentTimingsForTests.Presented) stamped.Add(device.LastPresentIdForTests);
                    }
                }

                // The first swapchain was created before the mode was applied, so
                // the count starts from whatever creation announced it.
                int applicationsBefore = latency.SleepModeApplications;
                int creationsBefore = swapchain.Creations;

                RenderFrames(Frames, Width, Height);

                // A forced recreation: the sleep mode is per swapchain and the new
                // handle starts without it, so the backend must set it again.
                GLFW.SetWindowSize(window, 320, 240);
                GLFW.PollEvents();
                seam.Resize(320, 240);
                RenderFrames(6, 320, 240);

                int creations = swapchain.Creations - creationsBefore;
                int applications = latency.SleepModeApplications - applicationsBefore;
                _output.WriteLine($"{stamped.Count} presents, {latency.SleepCount} sleeps, " +
                    $"{creations} swapchain creations, {applications} sleep-mode applications, " +
                    $"per-submit attribution {(latency.PerSubmitAttribution ? "on" : "off")} " +
                    $"(revision {revision})");

                Assert.True(creations >= 1, "the resize did not recreate the swapchain");
                Assert.True(applications >= creations,
                    $"{creations} swapchain creations but only {applications} sleep-mode applications");
                Assert.Equal(Frames + 6, latency.SleepCount);
                // The markers of a frame are stamped with the id that frame really
                // presented with; a miss would make the driver's report unmatchable.
                Assert.Equal(0, latency.PresentIdMismatches);

                // This machine's driver (615.71.09) advertises revision 2, where
                // VkLatencySubmissionPresentIdNV does not exist: tagging is all or
                // nothing per frame, so below revision 3 it stays off.
                Assert.Equal(
                    revision >= LatencyBackendSelector.NvPerSubmitAttributionRevision,
                    latency.PerSubmitAttribution);

                LatencyFrameReport[] reports = latency.TakeReports();
                _output.WriteLine($"{reports.Length} driver reports drained");
                Assert.NotEmpty(reports);

                var presented = new HashSet<ulong>(stamped);
                ulong previous = 0;
                foreach (LatencyFrameReport report in reports)
                {
                    // Every report is about a present we made, and the map turns it
                    // back into the frame that produced it.
                    Assert.Contains(report.PresentId, presented);
                    Assert.True(swapchain.PresentIds.TryGetFrameId(report.PresentId, out ulong mapped),
                        $"present id {report.PresentId} is not in the frame map");
                    Assert.Equal(mapped, report.FrameId);
                    Assert.True(report.PresentId > previous,
                        $"report present id {report.PresentId} did not exceed {previous}");
                    previous = report.PresentId;
                }

                LatencyFrameReport sample = reports[^1];
                _output.WriteLine(
                    $"frame {sample.FrameId} present {sample.PresentId}: input {sample.InputUs}us " +
                    $"sim {sample.SimulationUs}us submit {sample.RenderSubmitUs}us present {sample.PresentUs}us " +
                    $"driver {sample.DriverUs}us os_queue {sample.OsRenderQueueUs}us gpu {sample.GpuUs}us " +
                    $"total {sample.TotalUs}us");
                // A driver report is exactly what a CPU-timestamp backend cannot
                // produce: the GPU's own span is filled in.
                Assert.True(sample.TotalUs > 0, "the driver's report has no end-to-end span");

                // Drained: the driver hands back the same window of frames on
                // every call, and a report is only ever returned once.
                LatencyFrameReport[] again = latency.TakeReports();
                foreach (LatencyFrameReport report in again)
                {
                    Assert.True(report.PresentId > previous,
                        $"present id {report.PresentId} was returned twice");
                }

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
