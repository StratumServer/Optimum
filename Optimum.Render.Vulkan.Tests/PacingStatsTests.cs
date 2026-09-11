using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The pacing measurement the Phase 0 plan asks for: the frame-interval ring, the
/// stats lines' stable tokens, the pacing gate reading them, every wait site being
/// counted, and one GPU case that pins today's blocking upload so Phase 1B can
/// flip it.
/// </summary>
public class PacingStatsTests
{
    private readonly ITestOutputHelper _output;

    public PacingStatsTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------ interval ring

    [Fact]
    public void RingPercentilesAreNearestRankAndStddevIsPopulation()
    {
        var ring = new FrameIntervalRing(512);
        for (int i = 1; i <= 100; i++) ring.Add(i);

        FramePacingSnapshot s = ring.Snapshot();
        Assert.Equal(100, s.Samples);
        Assert.Equal(50, s.P50);
        Assert.Equal(95, s.P95);
        Assert.Equal(99, s.P99);
        // Population stddev of 1..100 is sqrt((n^2 - 1) / 12).
        Assert.Equal(Math.Sqrt((100.0 * 100.0 - 1.0) / 12.0), s.StdDev, 9);
        // Nothing above 2 x p50 = 100.
        Assert.Equal(0, s.Stutters);
    }

    [Fact]
    public void RingCountsStuttersAboveTwiceTheMedian()
    {
        var ring = new FrameIntervalRing(512);
        for (int i = 0; i < 95; i++) ring.Add(10);
        ring.Add(20);        // exactly 2 x p50: not a stutter
        ring.Add(20.001);
        ring.Add(45);
        ring.Add(90);
        ring.Add(33);
        ring.Add(21);

        Assert.Equal(5, ring.Snapshot().Stutters);
    }

    [Fact]
    public void RingWrapKeepsOnlyTheNewestFrames()
    {
        var small = new FrameIntervalRing(4);
        for (int i = 1; i <= 6; i++) small.Add(i);
        FramePacingSnapshot s = small.Snapshot();
        Assert.Equal(4, s.Samples);
        // {3, 4, 5, 6}: 1 and 2 were overwritten.
        Assert.Equal(4, s.P50);
        Assert.Equal(6, s.P95);
        Assert.Equal(6, s.P99);
        Assert.Equal(Math.Sqrt(1.25), s.StdDev, 9);

        var ring = new FrameIntervalRing(FrameIntervalRing.DefaultCapacity);
        for (int i = 0; i < 88; i++) ring.Add(1000);
        for (int i = 0; i < 512; i++) ring.Add(10);
        s = ring.Snapshot();
        Assert.Equal(512, s.Samples);
        Assert.Equal(10, s.P99);
        Assert.Equal(0, s.StdDev);
        Assert.Equal(0, s.Stutters);

        // Wrapping past the start again replaces the oldest 10s, not the newest.
        for (int i = 0; i < 5; i++) ring.Add(25);
        s = ring.Snapshot();
        Assert.Equal(512, s.Samples);
        Assert.Equal(5, s.Stutters);
        Assert.Equal(10, s.P50);
        Assert.Equal(10, s.P99);
    }

    [Fact]
    public void RingIgnoresInvalidIntervalsAndSnapshotsEmpty()
    {
        var ring = new FrameIntervalRing(8);
        Assert.Equal(default, ring.Snapshot());
        ring.Add(double.NaN);
        ring.Add(-1);
        ring.Add(double.PositiveInfinity);
        Assert.Equal(0, ring.Count);
    }

    // --------------------------------------------------------------- line tokens

    [Fact]
    public void OriginalStatsLineKeepsItsFormat()
    {
        string line = VulkanStats.FormatIntervalLine(1.0, 60, 2, 10, 1, 20.0, 3, 4, 5, 6);
        Assert.Equal(
            "stats 1.0s: 60 frames (16.7 ms/frame), 2 allocations (10 live), " +
            "1 blocking uploads costing 20 ms (2% of the interval), textures +3/-4, " +
            "mesh writes dropped 5, uniform overflows 6",
            line);
    }

    [Fact]
    public void NewStatsLinesCarryStableKeyValueTokens()
    {
        Assert.Equal(
            "stats.pacing samples=512 p50_ms=16.667 p95_ms=17.100 p99_ms=18.300 stddev_ms=0.420 stutters=3",
            VulkanStats.FormatPacingLine(new FramePacingSnapshot(512, 16.66666, 17.1, 18.3, 0.42, 3)));

        var counts = new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        // No midpoints: F1 rounding of an exact x.x5 is not something to pin.
        var ms = new double[] { 1.21, 2.4, 3.6, 4.8, 6.0, 7.2, 8.4, 9.66, 10.83 };
        Assert.Equal(
            "stats.waits frame_pacing_n=1 frame_pacing_ms=1.2 upload_submit_n=2 upload_submit_ms=2.4 " +
            "flush_frame_n=3 flush_frame_ms=3.6 device_wait_idle_n=4 device_wait_idle_ms=4.8 " +
            "readback_n=5 readback_ms=6.0 occlusion_query_n=6 occlusion_query_ms=7.2 " +
            "swapchain_acquire_n=7 swapchain_acquire_ms=8.4 present_n=8 present_ms=9.7 " +
            "queue_submit_n=9 queue_submit_ms=10.8",
            VulkanStats.FormatWaitsLine(counts, ms));

        Assert.Equal(
            "stats.counters blocking_uploads=1 uploads=2 scopes=3 barriers=4 rebar_fallbacks=5 " +
            "dynamic_state=6 uniform_ring_used=7 uniform_ring_capacity=8",
            VulkanStats.FormatCountersLine(new CounterSample(1, 2, 3, 4, 5, 6, 7, 8)));

        // The enum and the token table cannot drift apart.
        Assert.Equal(VulkanStats.WaitSiteCount, Enum.GetValues<WaitSite>().Length);
        Assert.Equal(VulkanStats.WaitSiteCount, VulkanStats.WaitSiteTokens.Length);
        Assert.Equal("swapchain_acquire", VulkanStats.WaitSiteTokens[(int)WaitSite.SwapchainAcquire]);
        Assert.Equal("present", VulkanStats.WaitSiteTokens[(int)WaitSite.Present]);
        Assert.Equal("queue_submit", VulkanStats.WaitSiteTokens[(int)WaitSite.QueueSubmit]);
    }

    [Fact]
    public void SampleIsTheOriginalLineFollowedByThreeTokenLines()
    {
        // The first call may only arm the interval clock.
        VulkanStats.SampleIfDue(TimeSpan.Zero);
        string? sample = VulkanStats.SampleIfDue(TimeSpan.Zero);

        Assert.NotNull(sample);
        string[] lines = sample!.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Matches(new Regex(
            @"^stats [\d.]+s: \d+ frames \([\d.]+ ms/frame\), \d+ allocations \(\d+ live\), " +
            @"\d+ blocking uploads costing \d+ ms \(\S+% of the interval\), textures \+\d+/-\d+, " +
            @"mesh writes dropped \d+, uniform overflows \d+$"), lines[0]);
        Assert.StartsWith("stats.pacing samples=", lines[1]);
        Assert.StartsWith("stats.waits frame_pacing_n=", lines[2]);
        Assert.StartsWith("stats.counters blocking_uploads=", lines[3]);
    }

    [Fact]
    public void AcceptanceDocumentNamesEveryStatsToken()
    {
        string doc = File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, "docs", "taa-acceptance.md"));

        foreach (string line in new[]
                 {
                     VulkanStats.FormatPacingLine(default),
                     VulkanStats.FormatCountersLine(default),
                 })
        {
            foreach (Match token in Regex.Matches(line, @"([a-z0-9_]+)="))
            {
                Assert.Contains(token.Groups[1].Value + "=", doc);
            }
        }
        foreach (string site in VulkanStats.WaitSiteTokens)
        {
            Assert.Contains("`" + site + "`", doc);
        }
        Assert.Contains("stats.pacing", doc);
        Assert.Contains("stats.waits", doc);
        Assert.Contains("stats.counters", doc);
    }

    [Fact]
    public void PacingGateReadsTheLinesThisBackendWrites()
    {
        string dir = Directory.CreateTempSubdirectory("optimum-pacing-").FullName;
        try
        {
            string fps = Path.Combine(dir, "fps.log");
            string stats = Path.Combine(dir, "vulkan-stats.log");
            File.WriteAllText(fps,
                "[Optimum] fps window=1.002 frames=120 mean=8.350 min=7.900 max=10.100 p99=9.800 stddev=0.500\n" +
                "[Optimum] fps window=1.001 frames=121 mean=8.300 min=7.800 max=10.000 p99=9.700 stddev=0.480\n");

            File.WriteAllText(stats, Sample(blockingUploads: 0) + "\n" + Sample(blockingUploads: 0) + "\n");
            (int code, string output) = RunGate("--renderer", "vulkan", "--fps", fps, "--stats", stats);
            _output.WriteLine(output);
            Assert.True(code == 0, output);
            Assert.Contains("max 0 (0 of 2 samples non-zero)", output);

            File.WriteAllText(stats, Sample(blockingUploads: 0) + "\n" + Sample(blockingUploads: 2) + "\n");
            (code, output) = RunGate("--renderer", "vulkan", "--fps", fps, "--stats", stats);
            _output.WriteLine(output);
            Assert.True(code == 1, output);
            Assert.Matches(new Regex(@"blocking_uploads\s+max 2 \(1 of 2 samples non-zero\).*FAIL"), output);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static string Sample(long blockingUploads) =>
            VulkanStats.FormatIntervalLine(1.0, 120, 0, 812, 0, 0, 0, 0, 0, 0) + "\n" +
            VulkanStats.FormatPacingLine(new FramePacingSnapshot(512, 8.3, 9.8, 10.7, 0.6, 0)) + "\n" +
            VulkanStats.FormatWaitsLine(new long[VulkanStats.WaitSiteCount], new double[VulkanStats.WaitSiteCount]) + "\n" +
            VulkanStats.FormatCountersLine(new CounterSample(blockingUploads, 0, 2640, 240, 0, 168000, 402112, 16777216));
    }

    private static (int Code, string Output) RunGate(params string[] arguments)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = ShaderCorpus.RepositoryRoot,
        };
        start.ArgumentList.Add(Path.Combine(ShaderCorpus.RepositoryRoot, "scripts", "dev", "pacing-gate.sh"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    // ------------------------------------------------------ wait-site coverage

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, "Optimum.Render.Vulkan", relative));

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "unterminated " + signature);
        return source.Substring(start, end - start);
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    [Fact]
    public void EveryCpuWaitOnTheGpuIsCountedAtItsSite()
    {
        string root = Path.Combine(ShaderCorpus.RepositoryRoot, "Optimum.Render.Vulkan");
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);

            // vkDeviceWaitIdle only through the counting helper.
            if (name != "VulkanStats.cs") Assert.DoesNotContain(".DeviceWaitIdle(", text);
            // Every fence wait lives in a file that notes the wait.
            if (text.Contains("WaitForFences(")) Assert.Contains("VulkanStats.NoteWait(", text);
            // So does every timeline semaphore wait.
            if (text.Contains("WaitSemaphores(")) Assert.Contains("VulkanStats.NoteWait(", text);
            // So does every queue submission (the queue lock is held through upload fence waits).
            if (text.Contains("QueueSubmit(")) Assert.Contains("VulkanStats.NoteWait(", text);
        }

        // Phase 1B step 1: the frame ring paces on the Frame timeline, never on a fence.
        string timeline = Source("Frame/FrameTimeline.cs");
        string wait = Body(timeline, "private void Wait(");
        Assert.True(wait.IndexOf("WaitSemaphores(", StringComparison.Ordinal) <
                    wait.IndexOf("VulkanStats.NoteWait(site, waitStart);", StringComparison.Ordinal),
            "the timeline wait must be counted after it returns");
        string frameRing = Source("Core/FrameRing.cs");
        Assert.DoesNotContain("WaitForFences(", frameRing);
        Assert.DoesNotContain("Fence Fence", frameRing);
        string ringBegin = Body(frameRing, "public FrameSlot BeginFrame(");
        Assert.Equal(1, Count(ringBegin, "_timeline.WaitForFrame("));
        Assert.Contains("_timeline.WaitForFrame(slot.LastSignalledValue, WaitSite.FramePacing);", ringBegin);
        Assert.Contains("_retired.Collect();", ringBegin);
        Assert.Contains("_retired.Retire(resource)", frameRing);
        // Phase 1B step 2: a partial submit never waits.
        Assert.DoesNotContain("WaitForFrame(", Body(frameRing, "public ulong SubmitPartial()"));
        string frameSubmit = Body(frameRing, "private void Submit(");
        int submitStart = frameSubmit.IndexOf("long submitStart = VulkanStats.WaitStart();", StringComparison.Ordinal);
        int queueLock = frameSubmit.IndexOf("lock (_context.QueueLock)", StringComparison.Ordinal);
        int submitNoted = frameSubmit.IndexOf("VulkanStats.NoteWait(WaitSite.QueueSubmit, submitStart);", StringComparison.Ordinal);
        Assert.True(submitStart >= 0 && queueLock > submitStart && submitNoted > queueLock,
            "the frame submit must be timed from before the queue lock to after the submit");

        string resources = Source("Core/VulkanResources.cs");
        string submit = Body(resources, "public void SubmitAndWait(");
        Assert.Contains("VulkanStats.NoteWait(site, start);", submit);
        Assert.Contains("if (site == WaitSite.UploadSubmit) VulkanStats.NoteBlockingUpload();", submit);

        string swapchain = Source("Core/Swapchain.cs");
        Assert.Contains("WaitSite.SwapchainAcquire", Body(swapchain, "public bool TryAcquire("));
        Assert.Contains("WaitSite.Present", Body(swapchain, "public void Present("));

        string device = Source("VulkanDevice.cs");
        Assert.Contains("FrameSlot slot = _frames.BeginFrame();", Body(device, "public void BeginFrame()"));
        // Phase 1B step 2: no flush, no query wait, no device-idle wait on a readback.
        Assert.DoesNotContain("FlushFrame", device);
        Assert.DoesNotContain("WaitSite.OcclusionQuery", device);
        Assert.DoesNotContain("WaitSite.FlushFrame", frameRing);
        Assert.DoesNotContain("ResultWaitBit", Source("Frame/QueryRing.cs"));
        string readBack = Body(device, "private void ReadBack(");
        Assert.DoesNotContain("WaitDeviceIdle", readBack);
        Assert.Contains("_readbacks.WaitAndCopy(ticket, destination);", readBack);
        Assert.Contains("_frames.Timeline.WaitForFrame(ticket.FrameValue, WaitSite.Readback);",
            Source("Transfer/ReadbackManager.cs"));
        // A between-frames readback waits on a setup fence, but it is not an upload.
        Assert.Equal(Count(device, "_setupCommands.SubmitAndWait("), Count(device, "WaitSite.Readback);"));

        // The per-draw dynamic-state count matches the commands actually recorded.
        string dynamicState = Body(device, "private void ApplyDynamicState(");
        Assert.Equal(VulkanStats.DynamicStateCommandsPerDraw, Count(dynamicState, "api.CmdSet"));
        Assert.Contains("VulkanStats.NoteDynamicStateCommands(VulkanStats.DynamicStateCommandsPerDraw);", dynamicState);

        Assert.Contains("VulkanStats.NoteScopeOpened();", Source("Core/RenderTargetManager.cs"));
        Assert.Contains("VulkanStats.NoteRebarFallback();", Source("Core/MeshManager.cs"));
        Assert.Equal(Count(device, "CmdPipelineBarrier2("), Count(device, "VulkanStats.NoteImageBarriers(1);"));
    }

    // ------------------------------------------------------------------ GPU

    /// <summary>
    /// Today a texture upload inside a frame submits a setup command buffer and
    /// waits for its fence: one blocking upload, one wait at the upload site.
    /// Phase 1B (non-blocking transfer) flips both deltas to zero - rename this
    /// test then, keep the readback half as it is. The readback that verifies the
    /// pixels waits too, but at the readback site, and must never count as an
    /// upload.
    /// </summary>
    [SkippableFact]
    public unsafe void TextureUploadInsideAFrameBlocksOnTheUploadSiteUntilPhase1B()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 4;
            int texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 1);

            var data = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                data[i * 4] = 10;
                data[i * 4 + 1] = 200;
                data[i * 4 + 2] = (byte)(i * 16);
                data[i * 4 + 3] = 255;
            }

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);

            long blockingBefore = VulkanStats.BlockingUploads;
            long requestsBefore = VulkanStats.UploadRequests;
            long uploadWaitsBefore = VulkanStats.WaitCount(WaitSite.UploadSubmit);
            fixed (byte* pixels = data)
                seam.UploadTexture2D(texture, 0, 0, 0, size, size, EnumTexturePixelFormat.Rgba, (IntPtr)pixels);
            long blockingDelta = VulkanStats.BlockingUploads - blockingBefore;
            long requestsDelta = VulkanStats.UploadRequests - requestsBefore;
            long uploadWaitsDelta = VulkanStats.WaitCount(WaitSite.UploadSubmit) - uploadWaitsBefore;

            seam.Present();

            long blockingBeforeReadback = VulkanStats.BlockingUploads;
            long readbackWaitsBefore = VulkanStats.WaitCount(WaitSite.Readback);
            var pixelsOut = new byte[size * size * 4];
            fixed (byte* destination = pixelsOut)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);

            Assert.Equal(data, pixelsOut);

            Assert.Equal(1, requestsDelta);
            // Phase 1B: both of these become Assert.Equal(0, ...).
            Assert.Equal(1, blockingDelta);
            Assert.Equal(1, uploadWaitsDelta);

            Assert.Equal(0, VulkanStats.BlockingUploads - blockingBeforeReadback);
            Assert.True(VulkanStats.WaitCount(WaitSite.Readback) - readbackWaitsBefore >= 1);

            GpuTest.AssertClean(seam);
        }
    }
    /// <summary>
    /// A frame's vkQueueSubmit is a counted wait: it takes the queue lock that a
    /// worker's synchronous upload holds through its fence wait. One frame, one
    /// submit at the queue_submit site; the readback that checks the frame's
    /// pixels goes through the setup queue path and adds none.
    /// </summary>
    [SkippableFact]
    public unsafe void AFrameSubmitIsCountedAtTheQueueSubmitSite()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 4;
            int texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 1);

            long submitsBefore = VulkanStats.WaitCount(WaitSite.QueueSubmit);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            // No channel lands on x.5 in UNORM8: the spec lets 0.5 quantise to 127 or
            // 128, and the RTX 4070 driver reads 127 (0.2 -> 51 is exact either way).
            seam.ClearColor(0, 0.25f, 0.2f, 0.75f, 1f);
            seam.Present();
            long submitsAfterFrame = VulkanStats.WaitCount(WaitSite.QueueSubmit);

            var pixelsOut = new byte[size * size * 4];
            fixed (byte* destination = pixelsOut)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);

            for (int i = 0; i < pixelsOut.Length; i += 4)
            {
                Assert.Equal(new byte[] { 64, 51, 191, 255 }, pixelsOut[i..(i + 4)]);
            }
            Assert.Equal(1, submitsAfterFrame - submitsBefore);
            Assert.Equal(submitsAfterFrame, VulkanStats.WaitCount(WaitSite.QueueSubmit));

            GpuTest.AssertClean(seam);
        }
    }
}
