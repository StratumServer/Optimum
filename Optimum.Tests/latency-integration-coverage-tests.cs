using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The join between the three latency stages (plan section "Latency seams"): the
/// selection made at device creation (S1) has to be the backend the frame's
/// markers, submit tags, swapchain callbacks and stats line actually run on
/// (S2-S7), and the client's own FPS limiter may only stand down because that
/// backend says so (S3).
///
/// Placement is the point, which is why it is pinned in source: installing the
/// backend after the frame ring or after the first swapchain would leave those
/// two talking to a different instance than the one that was announced, and
/// nothing in a passing GPU test would say so.
/// </summary>
public class LatencyIntegrationCoverageTests
{
    private const string DevicePath = "Optimum.Render.Vulkan/VulkanDevice.cs";
    private const string StatsPath = "Optimum.Render.Vulkan/Core/VulkanStats.cs";
    private const string FramePath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs";

    [Fact]
    public void TheSelectedBackendIsInstalledBeforeTheRingAndTheFirstSwapchain()
    {
        string device = Read(DevicePath);
        string initialize = Body(device, "    public bool Initialize(IntPtr windowHandle, int width, int height, out string failureReason)");

        int context = initialize.IndexOf("_context = context!;", StringComparison.Ordinal);
        int install = initialize.IndexOf("InstallSelectedLatencyBackend();", StringComparison.Ordinal);
        int ring = initialize.IndexOf("_frames = new FrameRing(_context);", StringComparison.Ordinal);
        int swapchain = initialize.IndexOf("Swapchain.TryCreate(", StringComparison.Ordinal);

        Assert.True(context >= 0, "Initialize must take the context:\n" + initialize);
        Assert.True(install > context, "the backend is installed after the context exists:\n" + initialize);
        Assert.True(ring > install, "the frame ring must be built after the backend is installed:\n" + initialize);
        Assert.True(swapchain > install,
            "the first swapchain must be created after the backend is installed:\n" + initialize);

        // Exactly one install, so no second one can overwrite it mid-frame.
        Assert.Single(Regex.Matches(device, @"InstallSelectedLatencyBackend\(\);"));
    }

    [Fact]
    public void TheInstalledBackendIsTheOneTheCapabilitiesSelected()
    {
        string device = Read(DevicePath);
        string install = Body(device, "    private void InstallSelectedLatencyBackend()");

        Assert.Contains("_context.Capabilities.LatencyBackend", install);
        Assert.Contains("SetLatencyBackend(backend);", install);
        Assert.Contains("backend.Apply(LatencySettingsFromConfig());", install);
        Assert.Contains("VulkanStats.LatencyRevision = _context.Capabilities.LatencySupport.NvLowLatency2SpecVersion;",
            install);

        // A selection with no implementation degrades and says so, rather than
        // silently running something else.
        Assert.Contains("if (backend.Kind != selected)", install);

        // A backend installed before Initialize is a deliberate choice and is not
        // overwritten by the selection.
        Assert.Contains("if (_latencyBackendInstalled)", install);

        // One place learns about the vendor backends when wave 3 lands.
        string factory = Body(device, "    private ILatencyBackend CreateLatencyBackend(LatencyBackendKind kind)");
        Assert.Contains("return new NoneLatencyBackend(MirrorValidationMessage);", factory);

        // SetLatencyBackend is what makes the ring, the stats and the swapchain
        // agree; it must reach all three.
        string setter = Body(device, "    internal void SetLatencyBackend(ILatencyBackend backend)");
        Assert.Contains("VulkanStats.LatencySource = Latency;", setter);
        Assert.Contains("_frames.Latency.Backend = Latency;", setter);
        Assert.Contains("_swapchain.Latency = Latency;", setter);
    }

    [Fact]
    public void TheClientsPersistedSettingIsWhatTheBackendIsAskedFor()
    {
        string device = Read(DevicePath);
        string settings = Body(device, "    private static LatencySettings LatencySettingsFromConfig()");

        Assert.Contains("OptimumConfig.LatencyEnabled", settings);
        Assert.Contains("OptimumConfig.LatencyBoost", settings);
        Assert.Contains("return LatencySettings.Disabled;", settings);
        Assert.Contains("LatencyMode.Boost : LatencyMode.On", settings);
    }

    [Fact]
    public void TheFrameCapFollowsTheInstalledBackend()
    {
        string frame = Read(FramePath);
        string owns = Body(frame, "    public override bool LatencyOwnsFrameCap");

        // S3: the lib's limiter stands down only because the live backend says
        // it paces the frame itself, and the live backend is the device's.
        Assert.Contains("backend.OwnsFrameCap", owns);
        Assert.Contains("ILatencyBackend? LatencyBackend => LatencyBackendOverride ?? device?.Latency;", frame);
    }

    [Fact]
    public void TheDeviceUpLineAndTheStatsLineBothNameTheBackend()
    {
        string device = Read(DevicePath);
        Assert.Contains("_context.Capabilities.LatencySummary", device);

        string stats = Read(StatsPath);
        // Seam S7: backend, mode and the vendor revision behind it.
        Assert.Contains("public static volatile uint LatencyRevision;", stats);
        Assert.Contains("line.Append(\" rev=\")", stats);
        Assert.Contains("FormatLatencyLine(backend, mode, LatencyRevision,", stats);

        // Every token of the line is documented.
        string doc = File.ReadAllText(PatchReader.FindRepositoryFile("docs/taa-acceptance.md"));
        Assert.Contains("rev=", doc);

        // A disposed device leaves nothing behind for the next one's line.
        Assert.Contains("VulkanStats.LatencyRevision = 0;", device);
    }

    [Fact]
    public void PresentIdsAreChainedOnlyWhereTheFeatureWasEnabled()
    {
        string device = Read(DevicePath);
        Assert.Contains("_swapchain.PresentIdEnabled = _context.Capabilities.PresentIdEnabled;", device);
        Assert.Single(Regex.Matches(device, @"_swapchain\.PresentIdEnabled = "));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    /// <summary>The member starting at <paramref name="signature" />, up to its closing brace.</summary>
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "unterminated " + signature);
        return source.Substring(start, end - start);
    }
}
