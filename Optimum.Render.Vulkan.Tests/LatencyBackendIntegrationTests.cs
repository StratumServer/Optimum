using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The integration seam of wave 3: all four backend kinds are constructible from
/// the one switch in <c>VulkanDevice.CreateLatencyBackend</c>, and a forced kind
/// the device cannot host degrades down the documented ladder
/// (<see cref="LatencyBackendSelector.Degrade" />: NV and AMD to Native, Native
/// to None) instead of silently becoming None or failing the device.
///
/// The three backend stages each tested their own backend against real hardware;
/// what none of them could test is that the merged switch still constructs the
/// other two. That is what these tests are for, so they run headless and assert
/// against the ladder rather than against one vendor's hardware.
/// </summary>
public class LatencyBackendIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public LatencyBackendIntegrationTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every kind the env override (<c>OPTIMUM_VULKAN_LATENCY</c>) can name
    /// reaches the device and comes back as either that kind or a kind on its
    /// degrade ladder. None is on every ladder, so the assertion has teeth only
    /// because it also asserts the ladder never skips a rung upwards.
    /// </summary>
    [SkippableTheory]
    [InlineData(0)] // None
    [InlineData(1)] // Native
    [InlineData(2)] // NvLowLatency2
    [InlineData(3)] // AmdAntiLag
    public void EveryForcedBackendIsConstructibleOrDegradesDownTheLadder(int forcedKind)
    {
        // LatencyBackendKind is internal, so the theory data is its numeric value.
        var forced = (LatencyBackendKind)forcedKind;
        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.LatencyBackend = forced;
        };

        if (!device.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device.Dispose();
            Skip.If(true, "Vulkan unavailable: " + failureReason);
        }

        using (device)
        {
            LatencyBackendKind installed = device.Latency.Kind;
            _output.WriteLine("forced " + LatencyBackends.Token(forced) +
                " -> selected " + LatencyBackends.Token(device.ContextForTests.Capabilities.LatencyBackend) +
                " -> installed " + LatencyBackends.Token(installed) +
                " (" + device.ContextForTests.Capabilities.LatencySummary + ")");

            Assert.Contains(installed, Ladder(forced));

            // Forcing off means off: no other kind is reachable from None.
            if (forced == LatencyBackendKind.None) Assert.Equal(LatencyBackendKind.None, installed);

            // The instance the frame runs on is the instance the stats line and
            // the frame ring were handed - the merge must not have split them.
            Assert.Same(device.Latency, VulkanStats.LatencySource);

            // LatencyMode ships off, so no installed backend takes the client's
            // FPS limiter away on a default run, whichever kind it turned out to be.
            Assert.False(device.Latency.OwnsFrameCap);

            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// Auto selection is the plan's order: NV where the device really offers it,
    /// then AMD, then Native, and never None (only an explicit "off" yields None).
    /// Asserted against what this device advertises, so it holds on any hardware.
    /// </summary>
    [SkippableFact]
    public void AutoSelectionTakesNvThenAmdThenNativeAndNeverNone()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            LatencyDeviceSupport support = device!.ContextForTests.Capabilities.LatencySupport;
            _output.WriteLine(support.ToString());

            LatencyBackendKind expected = support.NvUsable
                ? LatencyBackendKind.NvLowLatency2
                : support.AmdUsable ? LatencyBackendKind.AmdAntiLag : LatencyBackendKind.Native;

            Assert.Equal(expected, LatencyBackends.SelectBackend(support.NvUsable, support.AmdUsable, null));
            Assert.NotEqual(LatencyBackendKind.None, LatencyBackends.SelectBackend(false, false, null));

            // And the table the present-path filter walks is that same order.
            Assert.Equal(
                new[]
                {
                    LatencyBackendKind.NvLowLatency2,
                    LatencyBackendKind.AmdAntiLag,
                    LatencyBackendKind.Native,
                    LatencyBackendKind.None,
                },
                LatencyBackendSelector.AllowedBackends(LatencyPresentPath.BlitFromOwned));

            GpuTest.AssertClean(device);
        }
    }

    /// <summary>The kinds a forced kind may legitimately end up as, best first.</summary>
    private static List<LatencyBackendKind> Ladder(LatencyBackendKind forced)
    {
        var rungs = new List<LatencyBackendKind> { forced };
        LatencyBackendKind rung = forced;
        while (rung != LatencyBackendKind.None)
        {
            rung = LatencyBackendSelector.Degrade(rung);
            rungs.Add(rung);
        }

        return rungs;
    }
}
