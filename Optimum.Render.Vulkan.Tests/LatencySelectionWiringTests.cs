using System;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The seam the integration wires: the selection made while the device is
/// created (seam S1) is the backend the frame actually runs on (seams S2-S7).
/// Stage A owns the lib hook, stage B the selection and stage C the markers; the
/// only thing that joins them is <c>VulkanDevice.InstallSelectedLatencyBackend</c>,
/// and these tests are about that join, not about any one of the three.
///
/// No vendor backend exists yet (plan wave 3), so every selection must resolve
/// to <see cref="NoneLatencyBackend" />, which sleeps nowhere and owns no frame
/// cap - "nothing on screen changes" is exactly that assertion.
/// </summary>
public class LatencySelectionWiringTests
{
    private readonly ITestOutputHelper _output;

    public LatencySelectionWiringTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void TheDeviceRunsTheBackendItsCapabilitiesSelected()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanCapabilities capabilities = device!.ContextForTests.Capabilities;
            _output.WriteLine(capabilities.LatencySummary);

            // Either the selection is implemented and installed, or it is not
            // implemented yet and the device fell back to None. Nothing else.
            Assert.True(
                device.Latency.Kind == capabilities.LatencyBackend ||
                device.Latency.Kind == LatencyBackendKind.None,
                "selected " + LatencyBackends.Token(capabilities.LatencyBackend) +
                " but installed " + LatencyBackends.Token(device.Latency.Kind));

            // The stats source is the very instance the frame uses, so the line
            // cannot report a backend the frame is not running.
            Assert.Same(device.Latency, VulkanStats.LatencySource);

            // LatencyMode ships off, so whichever backend was installed, it never
            // takes the client's FPS limiter away.
            Assert.False(device.Latency.OwnsFrameCap);

            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void TheStatsLineNamesTheInstalledBackendAndItsRevision()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            // The revision token comes from the same capability the selection read.
            Assert.Equal(device!.ContextForTests.Capabilities.LatencySupport.NvLowLatency2SpecVersion, VulkanStats.LatencyRevision);

            VulkanStats.SampleIfDue(TimeSpan.Zero);
            string? sample = VulkanStats.SampleIfDue(TimeSpan.Zero);
            Assert.NotNull(sample);

            string latency = LineStartingWith(sample!, "stats.latency ");
            _output.WriteLine(latency);
            Assert.Contains("backend=" + LatencyBackends.Token(device.Latency.Kind), latency);
            Assert.Contains(
                "rev=" + device.ContextForTests.Capabilities.LatencySupport.NvLowLatency2SpecVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                latency);
            Assert.Contains("mode=" + VulkanStats.ModeToken(device.Latency.Settings.Mode), latency);

            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void DisposingTheDeviceLeavesNoStaleBackendBehindTheStatsLine()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        device!.Dispose();

        Assert.Null(VulkanStats.LatencySource);
        Assert.Equal(0u, VulkanStats.LatencyRevision);
    }

    /// <summary>Present ids may only be chained where the feature was enabled.</summary>
    [SkippableFact]
    public void PresentIdsAreOnlyChainedWhenTheFeatureWasEnabled()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            // A headless device has no swapchain at all, so the only thing to
            // assert here is the capability the wiring copies from: it is false
            // unless a vendor backend that needs it was selected on a presentable
            // device, and the swapchain copies exactly this value.
            Assert.False(device!.ContextForTests.Capabilities.PresentIdEnabled);
            GpuTest.AssertClean(device);
        }
    }

    private static string LineStartingWith(string sample, string prefix)
    {
        foreach (string line in sample.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }

        throw new Xunit.Sdk.XunitException("no line starting with '" + prefix + "' in:\n" + sample);
    }
}
