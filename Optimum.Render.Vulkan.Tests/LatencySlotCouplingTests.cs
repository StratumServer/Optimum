using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The latency slot follows the upscaler slot (plan, "The slots are coupled",
/// user 2026-09-12): a vendor latency backend only when the active upscaler's
/// vendor is the GPU's vendor, else Optimum's own pacing.
///
/// | upscaler \ GPU | NVIDIA | AMD | Intel |
/// | DLSS / DLSS-G  | nv     | native | native |
/// | FSR            | native | amd    | native |
/// | XeSS (+ XeFG)  | native | native | native on Vulkan (XeLL is D3D12-bridge only) |
/// | none           | the device-based auto order: nv, amd, native |
///
/// These are the decision table itself, the env override that wins over it, and
/// the one line that names the pair and the decision. The device here advertises
/// everything (NV rev 3 with present id, AMD anti-lag), so whatever the table
/// asks for is what the device could give: the answers below are the rule, not a
/// capability accident.
/// </summary>
public class LatencySlotCouplingTests
{
    private readonly ITestOutputHelper _output;

    public LatencySlotCouplingTests(ITestOutputHelper output) => _output = output;

    /// <summary>NV rev 3 with a present id, and AMD anti-lag: both vendor paths are available.</summary>
    private static LatencyDeviceSupport FullySupportedDevice =>
        new(nvLowLatency2: true, nvLowLatency2SpecVersion: 3, amdAntiLag: true, presentId: true, presentId2: false);

    // The whole table: every upscaler vendor against every GPU vendor. The enums
    // are internal to the backend, so they travel as their int values.
    [Theory]
    // no upscaler: the device-based auto order, whatever the GPU is
    [InlineData((int)UpscalerVendor.None, (int)GpuVendor.Nvidia, "nv")]
    [InlineData((int)UpscalerVendor.None, (int)GpuVendor.Amd, "nv")]
    [InlineData((int)UpscalerVendor.None, (int)GpuVendor.Intel, "nv")]
    [InlineData((int)UpscalerVendor.None, (int)GpuVendor.Unknown, "nv")]
    // DLSS / DLSS-G: Reflex on NVIDIA, ours everywhere else
    [InlineData((int)UpscalerVendor.Nvidia, (int)GpuVendor.Nvidia, "nv")]
    [InlineData((int)UpscalerVendor.Nvidia, (int)GpuVendor.Amd, "native")]
    [InlineData((int)UpscalerVendor.Nvidia, (int)GpuVendor.Intel, "native")]
    [InlineData((int)UpscalerVendor.Nvidia, (int)GpuVendor.Unknown, "native")]
    // FSR: anti-lag on AMD, ours everywhere else
    [InlineData((int)UpscalerVendor.Amd, (int)GpuVendor.Amd, "amd")]
    [InlineData((int)UpscalerVendor.Amd, (int)GpuVendor.Nvidia, "native")]
    [InlineData((int)UpscalerVendor.Amd, (int)GpuVendor.Intel, "native")]
    [InlineData((int)UpscalerVendor.Amd, (int)GpuVendor.Unknown, "native")]
    // XeSS / XeFG: XeLL only exists on the Windows D3D12 bridge, so on a Vulkan
    // present path even the matching Intel pair takes Optimum's own pacing
    [InlineData((int)UpscalerVendor.Intel, (int)GpuVendor.Intel, "native")]
    [InlineData((int)UpscalerVendor.Intel, (int)GpuVendor.Nvidia, "native")]
    [InlineData((int)UpscalerVendor.Intel, (int)GpuVendor.Amd, "native")]
    [InlineData((int)UpscalerVendor.Intel, (int)GpuVendor.Unknown, "native")]
    public void TheUpscalerVendorAndTheGpuVendorDecideTheBackend(int upscaler, int gpu, string expected)
    {
        var notes = new List<string>();
        LatencyBackendKind selected = LatencyBackendSelector.Select(
            FullySupportedDevice,
            forced: null,
            LatencyPresentPath.BlitFromOwned,
            (UpscalerVendor)upscaler,
            (GpuVendor)gpu,
            notes.Add);

        Assert.Equal(expected, LatencyBackends.Token(selected));
        foreach (string note in notes) _output.WriteLine(note);
    }

    // The rule is about the pair, not about what the device happens to have: a
    // matching pair on a device without that extension still degrades, and says
    // it was the pair that asked.
    [Fact]
    public void AMatchingPairOnADeviceWithoutTheExtensionDegradesToOurs()
    {
        var notes = new List<string>();
        var bare = new LatencyDeviceSupport(false, 0, false, false, false);

        LatencyBackendKind selected = LatencyBackendSelector.Select(
            bare, forced: null, LatencyPresentPath.BlitFromOwned,
            UpscalerVendor.Nvidia, GpuVendor.Nvidia, notes.Add);

        Assert.Equal(LatencyBackendKind.Native, selected);
        Assert.Contains(notes, note => note.Contains("the pair asked for nv"));
        foreach (string note in notes) _output.WriteLine(note);
    }

    // Every present path filter still applies on top of the coupling: the D3D12
    // bridge hosts none of our backends, XeLL owns the sleep there.
    [Fact]
    public void TheD3D12BridgeStillHostsNothingOfOurs()
    {
        var notes = new List<string>();
        LatencyBackendKind selected = LatencyBackendSelector.Select(
            FullySupportedDevice, forced: null, LatencyPresentPath.D3D12Bridge,
            UpscalerVendor.Intel, GpuVendor.Intel, notes.Add);

        Assert.Equal(LatencyBackendKind.None, selected);
        Assert.Contains(notes, note => note.Contains("XeLL"));
        foreach (string note in notes) _output.WriteLine(note);
    }

    // OPTIMUM_VULKAN_LATENCY is the testing override and wins over the pair, in
    // both directions: it can take a vendor backend a cross-vendor pair would
    // have refused, and it can take ours off a matching pair.
    [Theory]
    [InlineData("amd", (int)UpscalerVendor.Nvidia, (int)GpuVendor.Amd, "amd")]
    [InlineData("nv", (int)UpscalerVendor.Amd, (int)GpuVendor.Nvidia, "nv")]
    [InlineData("native", (int)UpscalerVendor.Nvidia, (int)GpuVendor.Nvidia, "native")]
    [InlineData("off", (int)UpscalerVendor.Nvidia, (int)GpuVendor.Nvidia, "off")]
    [InlineData("auto", (int)UpscalerVendor.Amd, (int)GpuVendor.Nvidia, "native")]
    public void TheEnvironmentOverrideWinsOverThePair(string value, int upscaler, int gpu, string expected)
    {
        string? previous = Environment.GetEnvironmentVariable(LatencyBackends.LatencyVariable);
        try
        {
            Environment.SetEnvironmentVariable(LatencyBackends.LatencyVariable, value);
            var notes = new List<string>();

            LatencyBackendKind selected = LatencyBackendSelector.Select(
                FullySupportedDevice,
                LatencyBackends.FromEnvironment(),
                LatencyPresentPath.BlitFromOwned,
                (UpscalerVendor)upscaler,
                (GpuVendor)gpu,
                notes.Add);

            Assert.Equal(expected, LatencyBackends.Token(selected));
            foreach (string note in notes) _output.WriteLine(note);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LatencyBackends.LatencyVariable, previous);
        }
    }

    // One line, naming the pair it saw and the decision it made.
    [Theory]
    [InlineData((int)UpscalerVendor.Nvidia, (int)GpuVendor.Nvidia, "upscaler nvidia on nvidia gpu -> nv", "vendor match")]
    [InlineData((int)UpscalerVendor.Nvidia, (int)GpuVendor.Amd, "upscaler nvidia on amd gpu -> native", "cross-vendor")]
    [InlineData((int)UpscalerVendor.Amd, (int)GpuVendor.Amd, "upscaler amd on amd gpu -> amd", "vendor match")]
    [InlineData((int)UpscalerVendor.Amd, (int)GpuVendor.Intel, "upscaler amd on intel gpu -> native", "cross-vendor")]
    [InlineData((int)UpscalerVendor.Intel, (int)GpuVendor.Intel, "upscaler intel on intel gpu -> native", "XeLL")]
    public void TheDecisionIsLoggedAsOneLine(int upscaler, int gpu, string pairAndDecision, string reason)
    {
        var notes = new List<string>();
        LatencyBackendSelector.Select(
            FullySupportedDevice, forced: null, LatencyPresentPath.BlitFromOwned,
            (UpscalerVendor)upscaler, (GpuVendor)gpu, notes.Add);

        string line = Assert.Single(notes, note => note.Contains("upscaler "));
        _output.WriteLine(line);
        Assert.StartsWith("latency: ", line);
        Assert.Contains(pairAndDecision, line);
        Assert.Contains(reason, line);
    }

    // The override is named in the line too, so a capture that pins a backend
    // still says why the pair was ignored.
    [Fact]
    public void TheOverrideIsNamedInTheDecisionLine()
    {
        var notes = new List<string>();
        LatencyBackendSelector.Select(
            FullySupportedDevice, LatencyBackendKind.Native, LatencyPresentPath.BlitFromOwned,
            UpscalerVendor.Nvidia, GpuVendor.Nvidia, notes.Add);

        string line = Assert.Single(notes, note => note.Contains("upscaler "));
        _output.WriteLine(line);
        Assert.Contains(LatencyBackends.LatencyVariable + "=native overrides the pair", line);
        Assert.Contains("-> native", line);
    }

    // With no vendor upscaler the selection is exactly what it was before the
    // coupling existed, log included: nothing new is said.
    [Fact]
    public void NoUpscalerLogsNothingNew()
    {
        var notes = new List<string>();
        LatencyBackendKind selected = LatencyBackendSelector.Select(
            FullySupportedDevice, forced: null, LatencyPresentPath.BlitFromOwned,
            UpscalerVendor.None, GpuVendor.Nvidia, notes.Add);

        Assert.Equal(LatencyBackendKind.NvLowLatency2, selected);
        Assert.Empty(notes);
    }

    // The GPU half of the pair is a vendor id from VkPhysicalDeviceProperties.
    [Theory]
    [InlineData(0x10DEu, (int)GpuVendor.Nvidia)]
    [InlineData(0x1002u, (int)GpuVendor.Amd)]
    [InlineData(0x1022u, (int)GpuVendor.Amd)]
    [InlineData(0x8086u, (int)GpuVendor.Intel)]
    [InlineData(0x13B5u, (int)GpuVendor.Unknown)]
    public void TheGpuVendorComesFromTheVendorId(uint vendorId, int expected)
    {
        Assert.Equal((GpuVendor)expected, GpuVendors.FromVendorId(vendorId));
    }

    // The upscaler half is the persisted setting token; "off" and Optimum's own
    // passthrough have no vendor latency stack, so they leave the slot alone.
    [Theory]
    [InlineData("dlss", (int)UpscalerVendor.Nvidia)]
    [InlineData("DLSS-G", (int)UpscalerVendor.Nvidia)]
    [InlineData("fsr", (int)UpscalerVendor.Amd)]
    [InlineData("xess", (int)UpscalerVendor.Intel)]
    [InlineData("xefg", (int)UpscalerVendor.Intel)]
    [InlineData("passthrough", (int)UpscalerVendor.None)]
    [InlineData("off", (int)UpscalerVendor.None)]
    [InlineData("", (int)UpscalerVendor.None)]
    [InlineData(null, (int)UpscalerVendor.None)]
    public void TheUpscalerVendorComesFromTheSettingToken(string? token, int expected)
    {
        Assert.Equal((UpscalerVendor)expected, UpscalerVendors.FromSettingToken(token));
    }

    // Every shipped upscaler name maps to a vendor the coupling knows, so a new
    // one cannot slip in as "none" by accident.
    [Fact]
    public void EveryShippedUpscalerNameIsMapped()
    {
        foreach (string name in Vintagestory.API.Config.OptimumConfig.UpscalerNames)
        {
            UpscalerVendor vendor = UpscalerVendors.FromSettingToken(name);
            _output.WriteLine(name + " -> " + UpscalerVendors.Token(vendor));
            if (string.Equals(name, "dlss", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(UpscalerVendor.Nvidia, vendor);
            }
            else
            {
                // "off" and the passthrough experiment: no vendor latency stack.
                Assert.Equal(UpscalerVendor.None, vendor);
            }
        }
    }

    // The wiring: the device the client creates carries the vendor the coupling
    // matches against, and it is the vendor id the driver reported.
    [SkippableFact]
    public void TheDeviceCarriesItsVendor()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanCapabilities capabilities = device!.ContextForTests.Capabilities;
            _output.WriteLine(capabilities.DeviceName + " vendor id 0x" + capabilities.VendorId.ToString("X") +
                " -> " + GpuVendors.Token(capabilities.Vendor));
            Assert.Equal(GpuVendors.FromVendorId(capabilities.VendorId), capabilities.Vendor);
            Assert.NotEqual(0u, capabilities.VendorId);
            GpuTest.AssertClean(device);
        }
    }
}
