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
        // The setting ships on since 2026-09-12, so "off is off" asks for off.
        using LatencyModeScope mode = LatencyModeScope.Off();
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            ILatencyBackend latency = device!.Latency;
            _output.WriteLine("latency backend: " + LatencyBackends.Token(latency.Kind));

            // With the mode off the installed backend - None, or the Native
            // one auto-selection lands on where no vendor path exists - is disabled:
            // it sleeps nowhere and owns no frame cap. "Off is off" is that, not the
            // identity of the instance.
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

    /// <summary>
    /// The decision of 2026-09-12: latency reduction ships on, on every GPU. A device
    /// that comes up with the shipped setting untouched therefore has an enabled
    /// backend that owns the client's frame cap, and the cap the lib hands over
    /// through <c>SetLatencyFrameCap</c> reaches it as the matching minimum interval -
    /// including the small background-window cap, which is the number an unfocused
    /// window would otherwise stop being paced by.
    /// </summary>
    [SkippableFact]
    public void TheShippedDefaultPacesTheFrameAndTakesTheClientsFrameCap()
    {
        using LatencyModeScope mode = LatencyModeScope.Of(LatencyModeScope.ShippedDefault);
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            ILatencyBackend latency = device!.Latency;
            _output.WriteLine("latency backend: " + LatencyBackends.Token(latency.Kind) +
                " mode " + latency.Settings.Mode);

            // On by default means: a real backend, enabled, owning the frame cap.
            Assert.NotEqual(LatencyBackendKind.None, latency.Kind);
            Assert.Equal(LatencyMode.On, latency.Settings.Mode);
            Assert.True(latency.OwnsFrameCap);
            // The device installs no cap of its own; the client's is the only source.
            Assert.Equal(0UL, latency.Settings.MinimumIntervalUs);

            var platform = new Platform.VulkanClientPlatform(null!);
            platform.LatencyBackendOverride = latency;

            // The background-window cap (OptimumBgMaxFps = 30 after sustained focus loss).
            platform.SetLatencyFrameCap(30);
            Assert.Equal(33333UL, latency.Settings.MinimumIntervalUs);
            Assert.Equal(30u, latency.Settings.MaxFps);
            Assert.Equal(LatencyMode.On, latency.Settings.Mode);

            // Repeating it changes nothing, so nothing is re-applied to the driver.
            LatencySettings unchanged = latency.Settings;
            for (int frame = 0; frame < 4; frame++) platform.SetLatencyFrameCap(30);
            Assert.Equal(unchanged, latency.Settings);

            // Focused again: the foreground cap, and then uncapped.
            platform.SetLatencyFrameCap(144);
            Assert.Equal(LatencySettings.IntervalUsForFps(144), latency.Settings.MinimumIntervalUs);
            platform.SetLatencyFrameCap(0);
            Assert.Equal(0UL, latency.Settings.MinimumIntervalUs);

            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void TheForcedBackendOptionReachesTheContextOptions()
    {
        using LatencyModeScope mode = LatencyModeScope.Off();
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
            // Whatever was installed, with the mode off it paces nothing.
            Assert.False(device.Latency.OwnsFrameCap);
            GpuTest.AssertClean(device);
        }
    }
}
