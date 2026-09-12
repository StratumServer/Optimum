using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 2 step 4: the upscaler's lifetime inside the renderer.
///
/// Four orderings decide whether this works or takes the process down, and none
/// of them can be observed from a unit test on a headless host, so each is
/// pinned against the source that carries it:
/// <list type="number">
/// <item>the session is prepared <b>before</b> the device is created, because
/// NGX's instance and device extensions are requested at device creation;</item>
/// <item>NGX is initialised <b>after</b> the device exists, and a refusal leaves
/// the client on the Vulkan device without an upscaler rather than failing the
/// install;</item>
/// <item>teardown is retire the feature, drain the frame timeline, shut NGX
/// down, <b>then</b> destroy the device - releasing a feature after
/// <c>Shutdown1</c>, or shutting NGX down after its device is gone, is a
/// use-after-free inside the driver;</item>
/// <item>NGX is never brought up twice in a process: the second
/// <c>Shutdown1</c> segfaults (measured 2026-09-12, driver 615.71.09).</item>
/// </list>
/// </summary>
public class UpscalerLifecycleCoverageTests
{
    [Fact]
    public void TheSessionIsPreparedBeforeTheDeviceAndNgxComesUpAfterIt()
    {
        string platform = VulkanPlatformSource.Read();

        int factory = platform.IndexOf("device = DeviceFactory();", StringComparison.Ordinal);
        int prepare = platform.IndexOf("PrepareUpscaler(device);", StringComparison.Ordinal);
        int initialize = platform.IndexOf("if (!device.Initialize(windowHandle", StringComparison.Ordinal);
        int bringUp = platform.IndexOf("BringUpUpscaler(device);", StringComparison.Ordinal);

        Assert.True(factory > 0 && prepare > factory,
            "the upscaler must be prepared on the device object that was just created");
        Assert.True(initialize > prepare,
            "the requirement contributor must be added before device.Initialize, or NGX's device " +
            "extensions never reach the created device");
        Assert.True(bringUp > initialize, "NGX must be initialised after the device exists");

        // The contributor is chained onto whatever was configured before, never
        // assigned over it.
        Assert.Contains("Action<VulkanContextOptions>? configured = target.ConfigureContextOptions;", platform);
        Assert.Contains("options.RequirementContributors.Add(requirements);", platform);

        // A refusal is not a failed install: the device stays, the host goes.
        Assert.Contains("if (!upscaler.BringUp(target, instance, physicalDevice, deviceHandle))", platform);
        Assert.Contains("upscaler.Dispose();", platform);
        Assert.Contains("upscaler = null;", platform);
    }

    [Fact]
    public void TheVendorRuntimeIsShutDownBeforeTheDeviceItWasInitialisedOn()
    {
        string platform = VulkanPlatformSource.Read();

        int shutdownGraphics = platform.IndexOf("public override void ShutdownGraphics()", StringComparison.Ordinal);
        Assert.True(shutdownGraphics > 0);
        string body = platform.Substring(shutdownGraphics);

        int upscaler = body.IndexOf("ShutDownUpscaler();", StringComparison.Ordinal);
        int dispose = body.IndexOf("device?.Dispose();", StringComparison.Ordinal);
        Assert.True(upscaler > 0, "ShutdownGraphics does not shut the upscaler down");
        Assert.True(dispose > upscaler,
            "the upscaler must go before the device it was initialised on:\n" + body.Substring(0, 800));
    }

    /// <summary>
    /// The host's own order, which is the one the driver actually sees: the
    /// feature is retired onto the frame timeline, the timeline is drained so
    /// nothing still names its handle, and only then is NGX shut down.
    /// </summary>
    [Fact]
    public void TheHostRetiresAndDrainsBeforeItShutsNgxDown()
    {
        string host = Read("Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs");
        int shutdown = host.IndexOf("public void Shutdown()", StringComparison.Ordinal);
        Assert.True(shutdown > 0);
        string body = host.Substring(shutdown);

        // The order is no longer written out at this call site: it is performed by
        // the one owner, which is handed the release and the drain to run itself.
        Assert.Contains(
            "NgxLifetime.ShutDown(RetireFeature, device != null ? device.DrainDeferredDeletions : null, _log);",
            body);

        string owner = Read("Optimum.Render.Vulkan/Upscale/Ngx/NgxLifetime.cs");
        int release = owner.IndexOf("if (releaseFeatures != null) releaseFeatures();", StringComparison.Ordinal);
        int drain = owner.IndexOf("if (drainFrameTimeline != null) drainFrameTimeline();", StringComparison.Ordinal);
        int ngx = owner.IndexOf("ShutdownResult = _shutdownCall(_device);", StringComparison.Ordinal);
        Assert.True(release > 0 && drain > release && ngx > drain,
            "the owner must release, drain and only then shut NGX down");

        // One lifetime per process, enforced in both directions.
        Assert.Contains("if (_shutDown) return NgxLifetimeOutcome.AlreadyShutDown;", owner);
        Assert.Contains(
            "return Fail(\"NGX was already shut down in this process and allows exactly one lifetime\");", host);

        // A feature is never destroyed inline while an evaluate may still name it.
        Assert.Contains("if (_device != null) _device.RetireDlssFeature(feature);", host);
    }

    /// <summary>
    /// A plan change - a resize or a preset change - retires the old feature
    /// before creating the new one, which is what keeps a resize from leaking
    /// features.
    /// </summary>
    [Fact]
    public void APlanChangeRetiresBeforeItCreates()
    {
        string host = Read("Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs");
        int ensure = host.IndexOf("public bool EnsureFeature(in UpscalePlan plan)", StringComparison.Ordinal);
        Assert.True(ensure > 0);
        string body = host.Substring(ensure, 1600);

        int matches = body.IndexOf("_feature.Matches(settings)", StringComparison.Ordinal);
        int retire = body.IndexOf("RetireFeature();", StringComparison.Ordinal);
        int create = body.IndexOf("_device.CreateDlssFeature(settings", StringComparison.Ordinal);
        Assert.True(matches > 0 && retire > matches && create > retire,
            "EnsureFeature must keep a matching feature, and otherwise retire before creating:\n" + body);
    }

    /// <summary>
    /// The device path allocates the motion attachment for either temporal
    /// consumer, exactly as the GL path does since this phase. The two bodies are
    /// separate by design (a device cannot route through four hundred lines of raw
    /// GL), which is precisely why a gate that moves on one side has to be pinned
    /// on the other.
    /// </summary>
    [Fact]
    public void TheDevicePathAllocatesTheMotionAttachmentForEitherConsumer()
    {
        string platform = VulkanPlatformSource.Read();
        Assert.Contains("bool temporalRequested = OptimumTemporalRequested;", platform);
        Assert.Contains(
            "DisableOptimumUpscaler(\"Primary motion attachment (device): \" + error.Message);", platform);

        // The history slots stay TAA's own on this path too.
        Assert.Contains("list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTarget(width, height);", platform);
        Assert.Contains("OptimumAdoptTaaTargets(list, taaRequested);", platform);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
