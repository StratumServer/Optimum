using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 2: occlusion queries through the per-slot query ring. A result
/// appears a frame or two after its query, like GL's availability polling, and
/// nothing waits for it: no mid-frame flush, no query wait, no extra submit.
/// </summary>
public class QueryRingTests
{
    private const int Size = 8;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private const string WhiteFragment = """
        #version 330 core
        out vec4 outColor;
        void main(void) { outColor = vec4(1.0); }
        """;

    /// <summary>Sites nothing in a query loop may wait at.</summary>
    private static readonly WaitSite[] SilentSites =
    {
        WaitSite.UploadSubmit, WaitSite.FlushFrame, WaitSite.DeviceWaitIdle, WaitSite.Readback,
        WaitSite.OcclusionQuery, WaitSite.SwapchainAcquire, WaitSite.Present,
    };

    private readonly ITestOutputHelper _output;

    public QueryRingTests(ITestOutputHelper output) => _output = output;

    private static long[] SilentCounts()
    {
        var counts = new long[SilentSites.Length];
        for (int i = 0; i < SilentSites.Length; i++) counts[i] = VulkanStats.WaitCount(SilentSites[i]);
        return counts;
    }

    private static void AssertNoSilentWaits(long[] before)
    {
        long[] after = SilentCounts();
        for (int i = 0; i < SilentSites.Length; i++)
        {
            Assert.True(after[i] == before[i],
                VulkanStats.WaitSiteTokens[(int)SilentSites[i]] + ": " + (after[i] - before[i]) + " waits, expected 0");
        }
    }

    private static int CreateTarget(VulkanDevice seam)
    {
        int texture = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        return framebuffer;
    }

    /// <summary>
    /// SystemRenderSunMoon's probe: colour writes off, a query around one draw
    /// covering <paramref name="coverage" /> squared pixels.
    /// </summary>
    private static void Probe(VulkanDevice seam, int framebuffer, int program, int query, int coverage)
    {
        seam.BindFramebuffer(framebuffer);
        seam.UseProgram(program);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.SetViewport(0, 0, coverage, coverage);
        seam.SetColorMask(false, false, false, false);
        seam.BeginOcclusionQuery(query);
        seam.DrawFullscreenTriangle();
        seam.EndOcclusionQuery(query);
        seam.SetColorMask(true, true, true, true);
    }

    /// <summary>
    /// Sixty presented frames of the sun glare pattern: poll availability, read
    /// the result when it is there, begin the next query only then. Every result
    /// matches the coverage of the query that produced it (alternating 64 and 16
    /// samples, so a result read from the wrong slot or frame fails), arrives one
    /// or two frames after its query and never in the frame that recorded it, and
    /// the loop waits nowhere but the one pacing wait per frame, with exactly one
    /// submit per frame.
    /// </summary>
    [SkippableFact]
    public void SunGlarePatternReadsEveryResultAFrameOrTwoLaterWithoutWaiting()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, WhiteFragment, "query-probe");
            int framebuffer = CreateTarget(seam);
            int query = seam.CreateOcclusionQuery();
            bool precise = device!.PreciseOcclusionForTests;

            seam.BeginFrame();
            seam.Present();

            long[] silentBefore = SilentCounts();
            long pacingBefore = VulkanStats.WaitCount(WaitSite.FramePacing);
            long submitsBefore = VulkanStats.WaitCount(WaitSite.QueueSubmit);
            ulong signalledBefore = device.TimelineForTests.FrameSignalled;

            const int frames = 60;
            bool querying = false;
            int begunAt = -1;
            int expected = 0;
            var latencies = new List<int>();
            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();
                if (querying && seam.IsQueryResultAvailable(query))
                {
                    int samples = seam.GetQueryResult(query);
                    if (precise) Assert.Equal(expected, samples);
                    else Assert.True(samples > 0, "an imprecise query still reports passing samples");
                    latencies.Add(frame - begunAt);
                    querying = false;
                }

                if (!querying)
                {
                    int coverage = latencies.Count % 2 == 0 ? Size : Size / 2;
                    Probe(seam, framebuffer, program, query, coverage);
                    Assert.False(seam.IsQueryResultAvailable(query),
                        "a query cannot be available in the frame that recorded it");
                    expected = coverage * coverage;
                    begunAt = frame;
                    querying = true;
                }
                seam.Present();
            }

            _output.WriteLine("results " + latencies.Count + ", latencies " + string.Join(",", latencies) +
                ", precise " + precise);

            AssertNoSilentWaits(silentBefore);
            Assert.Equal(frames, VulkanStats.WaitCount(WaitSite.FramePacing) - pacingBefore);
            Assert.Equal(frames, VulkanStats.WaitCount(WaitSite.QueueSubmit) - submitsBefore);
            Assert.Equal((ulong)frames, device.TimelineForTests.FrameSignalled - signalledBefore);

            // Two frames in flight: frame f+2 reuses f's slot and starts only once
            // f finished, so no result takes longer than two frames and a cycle
            // (query to next query) lasts at most two.
            Assert.True(latencies.Count >= frames / 2 - 1, "only " + latencies.Count + " results in " + frames + " frames");
            foreach (int latency in latencies) Assert.InRange(latency, 1, 2);

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// Forty queries in one frame, more than one pool holds, polled only after
    /// their slot was recycled twice: every result was moved to the host before
    /// the reset and matches its own coverage. The same query objects then run a
    /// second round with different coverage and report the new counts, and the
    /// pools were reused rather than recreated.
    /// </summary>
    [SkippableFact]
    public void ResultsSurviveTheirSlotBeingRecycledAndSpanSeveralPools()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, WhiteFragment, "query-probe");
            int framebuffer = CreateTarget(seam);
            bool precise = device!.PreciseOcclusionForTests;

            const int count = 40;
            var queries = new int[count];
            for (int i = 0; i < count; i++) queries[i] = seam.CreateOcclusionQuery();

            long[] silentBefore = SilentCounts();

            for (int round = 0; round < 2; round++)
            {
                seam.BeginFrame();
                for (int i = 0; i < count; i++) Probe(seam, framebuffer, program, queries[i], Coverage(round, i));
                seam.Present();

                for (int frame = 0; frame < 4; frame++)
                {
                    seam.BeginFrame();
                    seam.Present();
                }

                for (int i = 0; i < count; i++)
                {
                    Assert.True(seam.IsQueryResultAvailable(queries[i]), "query " + i + " of round " + round);
                    int samples = seam.GetQueryResult(queries[i]);
                    int coverage = Coverage(round, i);
                    if (precise) Assert.Equal(coverage * coverage, samples);
                    else Assert.True(samples > 0);
                }
            }

            AssertNoSilentWaits(silentBefore);
            // Two pools in the slot that ran the rounds (round two lands in the
            // other slot only if the frame parity says so): never more than two
            // per slot.
            int pools = device.OcclusionQueryPoolsForTests;
            Assert.InRange(pools, 2, 4);

            foreach (int query in queries) seam.DeleteQuery(query);
            Assert.False(seam.IsQueryResultAvailable(queries[0]));

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// Review fix: a GL query counts across framebuffer binds and mid-frame
    /// readbacks, a Vulkan query only inside one scope and one command buffer.
    /// Thirty-one one-sample probes fill the first pool up to its last index, then
    /// one query spans a draw into A, a bind to B (scope end; its continuation
    /// needs a second pool, reset between the scopes), a draw into B, a readback
    /// (partial submit) and a third draw. It reports the sum of all three draws,
    /// every probe still reports its own sample, and validation stays clean
    /// (before the fix: vkCmdEndRendering and vkEndCommandBuffer with an active query).
    /// </summary>
    [SkippableFact]
    public unsafe void AQuerySpanningScopeEndsAndAPartialSubmitCountsEveryDraw()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, WhiteFragment, "query-probe");
            int targetA = CreateTarget(seam);
            int targetB = CreateTarget(seam);
            bool precise = device!.PreciseOcclusionForTests;

            var probes = new int[QueryRing.QueriesPerPool - 1];
            for (int i = 0; i < probes.Length; i++) probes[i] = seam.CreateOcclusionQuery();
            int spanning = seam.CreateOcclusionQuery();

            seam.BeginFrame();
            seam.Present();

            seam.BeginFrame();
            foreach (int probe in probes) Probe(seam, targetA, program, probe, 1);

            seam.BindFramebuffer(targetA);
            seam.UseProgram(program);
            seam.SetViewport(0, 0, 4, 4);
            seam.SetColorMask(false, false, false, false);
            seam.BeginOcclusionQuery(spanning);
            seam.DrawFullscreenTriangle();
            seam.BindFramebuffer(targetB);
            seam.DrawFullscreenTriangle();
            var pixel = new byte[4];
            fixed (byte* destination = pixel) seam.ReadDefaultFramebuffer(0, 0, 1, 1, (IntPtr)destination);
            seam.BindFramebuffer(targetB);
            seam.DrawFullscreenTriangle();
            seam.EndOcclusionQuery(spanning);
            seam.SetColorMask(true, true, true, true);
            seam.Present();

            for (int frame = 0; frame < 4 && !seam.IsQueryResultAvailable(spanning); frame++)
            {
                seam.BeginFrame();
                seam.Present();
            }

            Assert.True(seam.IsQueryResultAvailable(spanning), "the spanning query never became available");
            int samples = seam.GetQueryResult(spanning);
            _output.WriteLine("spanning samples " + samples + ", precise " + precise + ", pools " +
                device.OcclusionQueryPoolsForTests);
            Assert.NotEqual(int.MaxValue, samples);
            if (precise) Assert.Equal(3 * 16, samples);
            else Assert.True(samples > 0);

            foreach (int probe in probes)
            {
                Assert.True(seam.IsQueryResultAvailable(probe));
                if (precise) Assert.Equal(1, seam.GetQueryResult(probe));
                else Assert.True(seam.GetQueryResult(probe) > 0);
            }

            GpuTest.AssertClean(seam);
        }
    }

    private static int Coverage(int round, int index) =>
        round == 0 ? index % Size + 1 : Size - index % Size;
}
