using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Latency seams, stage C (plan section "Latency seams", S2, S4, S5, S7, S8): the
/// renderer's side of the seams, checked in source because the placement is the
/// point - a marker one line later than the call it brackets measures something
/// else, and a second stamp of the same marker corrupts the frame's report.
///
/// The GPU tests (LatencyMarkerOrderTests, PresentIdentityTests) prove the
/// behaviour on a device; these pin where it lives, so a refactor cannot quietly
/// move a marker across the call it belongs to.
/// </summary>
public class LatencyRendererCoverageTests
{
    private const string DevicePath = "Optimum.Render.Vulkan/VulkanDevice.cs";
    private const string RingPath = "Optimum.Render.Vulkan/Core/FrameRing.cs";
    private const string SwapchainPath = "Optimum.Render.Vulkan/Present/Swapchain.cs";
    private const string StagesPath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Stages.cs";
    private const string StatsPath = "Optimum.Render.Vulkan/Core/VulkanStats.cs";
    private const string PresentPathPath = "Optimum.Render.Vulkan/Present/IPresentPath.cs";

    [Fact]
    public void TheFrameIdIsAllocatedOncePerFrameAndIsNotTheCheckpointCounter()
    {
        string device = Read(DevicePath);

        // Seam S2: one allocator, and the checkpoint counter is untouched by it.
        Assert.Single(Regex.Matches(device, @"public ulong BeginLatencyFrame\(\)"));
        Assert.Contains("_latencyFrameId++;", device);
        Assert.Single(Regex.Matches(device, @"_latencyFrameId\+\+;"));
        Assert.Contains("private uint _frameCounter;", device);
        Assert.Contains("Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));", device);

        // A frame without the lib hook still gets exactly one id.
        string beginFrame = Body(device, "    public void BeginFrame()");
        Assert.Contains("if (!_latencyFrameIdPending) BeginLatencyFrame();", beginFrame);
        Assert.Contains("_latencyFrameIdPending = false;", beginFrame);
        Assert.Contains("_frames.Latency.FrameId = _latencyFrameId;", beginFrame);
    }

    [Fact]
    public void EveryMarkerIsStampedOnceAtItsCallSite()
    {
        string device = Read(DevicePath);
        string present = Body(device, "    public void Present()");

        int submit = present.IndexOf("ulong renderValue = _frames.EndFrame();", StringComparison.Ordinal);
        int submitEnd = present.IndexOf("LatencyMarker.RenderSubmitEnd", StringComparison.Ordinal);
        int presentStart = present.IndexOf("LatencyMarker.PresentStart", StringComparison.Ordinal);
        int queuePresent = present.IndexOf("_swapchain.Present(target, _latencyFrameId);", StringComparison.Ordinal);
        int presentEnd = present.IndexOf("LatencyMarker.PresentEnd", StringComparison.Ordinal);
        int onPresent = present.IndexOf("Latency.OnPresent(_latencyFrameId, presentId);", StringComparison.Ordinal);

        Assert.True(submit >= 0 && submitEnd > submit, "RenderSubmitEnd must follow Submit A:\n" + present);
        Assert.True(presentStart > submitEnd && queuePresent > presentStart && presentEnd > queuePresent &&
                    onPresent > presentEnd,
            "PresentStart/End must bracket vkQueuePresentKHR, with OnPresent after them:\n" + present);

        // No marker twice, anywhere in the device.
        foreach (string marker in new[]
                 {
                     "LatencyMarker.RenderSubmitEnd", "LatencyMarker.PresentStart", "LatencyMarker.PresentEnd",
                     "LatencyMarker.SimulationEnd", "LatencyMarker.RenderSubmitStart",
                 })
        {
            Assert.Single(Regex.Matches(device, Regex.Escape(marker)));
        }

        // Seam S4: the first render stage of the frame stamps the pair, and only
        // the first, because the guard is the frame id itself.
        string renderStage = Body(device, "    internal void NoteRenderStageStarted()");
        Assert.Contains("if (_latencyRenderStartFrame == _latencyFrameId) return;", renderStage);
        Assert.Contains("_latencyRenderStartFrame = _latencyFrameId;", renderStage);

        // The platform bracket asks on every stage; the listener decides.
        string stages = Read(StagesPath);
        Assert.Contains("ActiveLatencyStageListener()?.OnFrameRenderStart();", stages);
        Assert.Contains("internal ILatencyStageListener? LatencyStageListener;", stages);
        Assert.Single(Regex.Matches(stages, @"ActiveLatencyStageListener\(\)\?\.OnFrameRenderStart\(\);"));
    }

    [Fact]
    public void EveryFrameSubmitIsTaggedAndStandaloneUploadsAreNot()
    {
        string ring = Read(RingPath);
        string submit = Body(ring, "    private void Submit(");
        Assert.Contains("void* chain = _latency.Backend.TagSubmit(_latency.FrameId, &timelineInfo);", submit);
        Assert.Contains("PNext = chain,", submit);

        // Submit A, Submit B and SubmitPartial all pass through that one method.
        foreach (string caller in new[]
                 {
                     "    public ulong SubmitPartial()",
                     "    public ulong EndFrameAndSubmit()",
                     "    public ulong SubmitPresent(",
                 })
        {
            Assert.Contains("Submit(", Body(ring, caller));
        }

        string uploads = Read("Optimum.Render.Vulkan/Transfer/UploadManager.cs");
        Assert.DoesNotContain("TagSubmit", uploads);
    }

    [Fact]
    public void EverySwapchainCreationTellsTheBackendOnceAndOffersAPNextHook()
    {
        string swapchain = Read(SwapchainPath);
        string build = Body(swapchain, "    private bool Build(out string? failureReason)");

        int created = build.IndexOf("_current = new SwapchainSlot(", StringComparison.Ordinal);
        int announced = build.IndexOf("_latency.OnSwapchainCreated(handle);", StringComparison.Ordinal);
        Assert.True(created >= 0 && announced > created,
            "the backend must be told after the slot exists:\n" + build);
        Assert.Contains("if (CreateChain != null) createInfo.PNext = CreateChain(createInfo.PNext);", build);

        // Once per creation: Build is the one creation path and the one caller.
        Assert.Single(Regex.Matches(swapchain, @"OnSwapchainCreated\(handle\);"));
        Assert.Single(Regex.Matches(swapchain, @"_current = new SwapchainSlot\("));

        // Seam S2: one present id per present, global so recreation cannot reset it.
        Assert.Contains("ulong presentId = PresentIdCounter.Next();", swapchain);
        Assert.Contains("PresentIds.Record(presentId, frameId);", swapchain);
        Assert.Contains("PNext = PresentIdEnabled ? &presentIdInfo : null,", swapchain);
        Assert.Single(Regex.Matches(swapchain, @"PresentIdCounter\.Next\(\)"));
    }

    [Fact]
    public void TheStatsSampleAlwaysCarriesTheLatencyLineAndTheSleepSite()
    {
        string stats = Read(StatsPath);
        Assert.Contains("LatencySleep = 9,", stats);
        Assert.Contains("\"latency_sleep\",", stats);
        Assert.Contains("public const int WaitSiteCount = 10;", stats);
        // Unconditional: no branch decides whether the line is written.
        Assert.Contains("LatencyLine(waitCounts[(int)WaitSite.LatencySleep], waitMs[(int)WaitSite.LatencySleep]) + \"\\n\" +", stats);

        string doc = File.ReadAllText(PatchReader.FindRepositoryFile("docs/taa-acceptance.md"));
        Assert.Contains("stats.latency", doc);
        Assert.Contains("`latency_sleep`", doc);
    }

    [Fact]
    public void ThePresentPathStatesTheBackendsItSupports()
    {
        string path = Read(PresentPathPath);
        Assert.Contains("LatencyBackendKind[] SupportedLatencyBackends { get; }", path);
        foreach (string kind in new[] { "None", "Native", "NvLowLatency2", "AmdAntiLag" })
        {
            Assert.Contains("LatencyBackendKind." + kind + ",", path);
        }
    }

    /// <summary>
    /// Latency review 2026-09-12. Two placements the review added, both of which
    /// a refactor could silently undo:
    /// the client's frame cap reaches a pacing backend at the sleep (without it,
    /// turning LatencyMode on stands the lib's limiter down and replaces it with
    /// nothing), and the swapchain tells the backend when the handle it holds is
    /// retired or destroyed, before the replacement is announced.
    /// </summary>
    [Fact]
    public void TheFrameCapAndTheSwapchainRetirementReachTheBackend()
    {
        string frame = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs");
        string sleep = Body(frame, "    public override void LatencySleep()");
        int cap = sleep.IndexOf("ApplyFrameCap(backend);", StringComparison.Ordinal);
        int backendSleep = sleep.IndexOf("backend.Sleep(frameId);", StringComparison.Ordinal);
        Assert.True(cap >= 0, "the cap never reaches the backend:\n" + sleep);
        Assert.True(backendSleep > cap, "the cap is applied after the sleep it paces:\n" + sleep);

        // Off is off: a disabled backend is never applied to.
        string apply = Body(frame, "    private void ApplyFrameCap(ILatencyBackend backend)");
        Assert.Contains("if (current.Mode == LatencyMode.Off) return;", apply);
        Assert.Contains("if (current.MinimumIntervalUs == interval) return;", apply);

        string swapchain = Read(SwapchainPath);
        // Once where the old slot is retired (a rebuild, failed or not), once at
        // teardown, and nowhere else.
        Assert.Equal(2, Regex.Matches(swapchain, @"_latency\.OnSwapchainRetired\(\);").Count);
        string build = Body(swapchain, "    private bool Build(out string? failureReason)");
        int retired = build.IndexOf("_latency.OnSwapchainRetired();", StringComparison.Ordinal);
        int created = build.IndexOf("_latency.OnSwapchainCreated(handle);", StringComparison.Ordinal);
        Assert.True(retired >= 0, "a rebuild never tells the backend the old handle is gone:\n" + build);
        Assert.True(created > retired, "the new handle is announced before the old one is retired:\n" + build);
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
