using System;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Seam L0 on a real device: a device that comes up with nobody selecting a
/// latency backend has the None backend, and driving the whole backend interface
/// across real frames neither paces the frame nor produces a validation message.
/// This is the "off is off" check the later stages regress against.
/// </summary>
public class LatencyDeviceDefaultTests
{
    private readonly ITestOutputHelper _output;

    public LatencyDeviceDefaultTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void AFreshDeviceRunsTheNoneBackendAndNeverPacesTheFrame()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            ILatencyBackend latency = device!.Latency;
            _output.WriteLine("latency backend: " + LatencyBackends.Token(latency.Kind));

            Assert.Equal(LatencyBackendKind.None, latency.Kind);
            Assert.False(latency.OwnsFrameCap);
            Assert.Equal(LatencyMode.Off, latency.Settings.Mode);
            Assert.Equal(0UL, latency.Settings.MinimumIntervalUs);

            for (ulong frame = 1; frame <= 3; frame++)
            {
                // The frame as the seams will drive it (ILatencyBackend's call map).
                Assert.Equal(0UL, latency.Sleep(frame));
                latency.Marker(frame, LatencyMarker.InputSample);
                latency.Marker(frame, LatencyMarker.SimulationStart);

                device.BeginFrame();
                latency.Marker(frame, LatencyMarker.SimulationEnd);
                latency.Marker(frame, LatencyMarker.RenderSubmitStart);
                latency.Marker(frame, LatencyMarker.RenderSubmitEnd);

                latency.Marker(frame, LatencyMarker.PresentStart);
                // Headless: Present submits the frame, there is no swapchain.
                device.Present();
                latency.Marker(frame, LatencyMarker.PresentEnd);
                latency.OnPresent(frame, presentId: frame);
            }

            LatencyFrameReport[] reports = latency.TakeReports();
            Assert.Equal(3, reports.Length);
            for (int i = 0; i < reports.Length; i++)
            {
                LatencyFrameReport report = reports[i];
                _output.WriteLine($"frame {report.FrameId} present {report.PresentId}: " +
                    $"input {report.InputUs}us sim {report.SimulationUs}us submit {report.RenderSubmitUs}us " +
                    $"present {report.PresentUs}us total {report.TotalUs}us");
                Assert.Equal((ulong)(i + 1), report.FrameId);
                Assert.Equal((ulong)(i + 1), report.PresentId);
                // Real clock, real frame: the whole frame took some measurable time,
                // and a CPU-timestamp backend reports nothing about the driver.
                Assert.True(report.TotalUs > 0, "the report's total must come off the clock");
                Assert.Equal(0UL, report.DriverUs);
                Assert.Equal(0UL, report.OsRenderQueueUs);
                Assert.Equal(0UL, report.GpuUs);
            }
            // Drained: the stats sample sees each frame exactly once.
            Assert.Empty(latency.TakeReports());

            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void TheForcedBackendOptionReachesTheContextOptions()
    {
        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
        LatencyBackendKind? seen = null;
        device.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.LatencyBackend = LatencyBackendKind.Native;
            seen = options.LatencyBackend;
        };

        if (!device.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device.Dispose();
            Skip.If(true, "No usable Vulkan device: " + failureReason);
        }

        using (device)
        {
            Assert.Equal(LatencyBackendKind.Native, seen);
            // This stage only carries the option; selection lands in a later one,
            // so the live backend is still None.
            Assert.Equal(LatencyBackendKind.None, device.Latency.Kind);
            GpuTest.AssertClean(device);
        }
    }
}
