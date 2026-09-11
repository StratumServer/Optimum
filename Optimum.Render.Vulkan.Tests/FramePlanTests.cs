using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Pure tests for the frame plan (Phase 2, contract C2): signature matching, load/store
/// solving and transient alias placement. No device.
/// </summary>
public class FramePlanTests
{
    // Resource ids for the TAA-shaped frame.
    private const int Primary = 1;
    private const int Depth = 2;
    private const int Motion = 3;
    private const int HistoryOut = 4;
    private const int Resolved = 5;
    private const int Sharpened = 6;
    private const int Bloom1 = 7;
    private const int Bloom2 = 8;
    private const int Final = 9;
    private const int HistoryIn = 10;
    private const int Swapchain = 11;
    private const int BloomDepth = 12;

    // Pass indices.
    private const int Opaque = 0;
    private const int SkyMotion = 1;
    private const int TaaResolve = 2;
    private const int TaaSharpen = 3;
    private const int BloomDown = 4;
    private const int BloomUp = 5;
    private const int FinalComposition = 6;
    private const int Blit = 7;

    private static AttachmentUse Use(int id, ResourceUsage usage, bool transient = false) => new(id, usage, transient);

    private static PassSignature Pass(int name, AttachmentUse[] attachments, int[] reads, int width = 1920, int height = 1080, int formats = 1) =>
        new() { NameId = name, Attachments = attachments, Reads = reads, Width = width, Height = height, FormatsId = formats };

    /// <summary>Opaque, sky motion, TAA resolve and sharpen, a two-step bloom chain on
    /// transients, final composition and the blit.</summary>
    private static List<PassSignature> TaaFrame() => new()
    {
        Pass(100, new[] { Use(Primary, ResourceUsage.ColorWrite), Use(Motion, ResourceUsage.ColorWrite), Use(Depth, ResourceUsage.DepthWrite) },
            Array.Empty<int>(), formats: 7),
        Pass(101, new[] { Use(Motion, ResourceUsage.ColorBlend), Use(Depth, ResourceUsage.DepthReadOnly) },
            Array.Empty<int>(), formats: 8),
        Pass(102, new[] { Use(Resolved, ResourceUsage.ColorWrite, true), Use(HistoryOut, ResourceUsage.ColorWrite) },
            new[] { Primary, Motion, Depth, HistoryIn }, formats: 2),
        Pass(103, new[] { Use(Sharpened, ResourceUsage.ColorWrite, true) }, new[] { Resolved }, formats: 2),
        Pass(104, new[] { Use(Bloom1, ResourceUsage.ColorWrite, true), Use(BloomDepth, ResourceUsage.DepthWrite, true) },
            new[] { Sharpened }, formats: 2),
        Pass(105, new[] { Use(Bloom2, ResourceUsage.ColorWrite, true) }, new[] { Bloom1 }, formats: 2),
        Pass(106, new[] { Use(Final, ResourceUsage.ColorWrite) }, new[] { Sharpened, Bloom2 }, formats: 3),
        Pass(107, new[] { Use(Swapchain, ResourceUsage.ColorWrite) }, new[] { Final }, formats: 4),
    };

    [Fact]
    public void IdenticalFrameMatches()
    {
        FramePlan plan = FramePlan.Build(TaaFrame());
        Assert.True(plan.Matches(TaaFrame()));
        Assert.False(plan.IsConservative);
        Assert.Equal(8, plan.PassCount);
        for (int p = 0; p < plan.PassCount; p++)
            Assert.True(plan.MatchesPass(p, TaaFrame()[p]));
        Assert.False(plan.MatchesPass(8, TaaFrame()[0]));
    }

    [Fact]
    public void NullArraysMatchEmptyArrays()
    {
        var withEmpty = new List<PassSignature> { Pass(1, Array.Empty<AttachmentUse>(), Array.Empty<int>()) };
        var withNull = new List<PassSignature> { new() { NameId = 1, Attachments = null!, Reads = null!, Width = 1920, Height = 1080, FormatsId = 1 } };
        Assert.True(FramePlan.Build(withEmpty).Matches(withNull));
        Assert.True(FramePlan.Build(withNull).Matches(withEmpty));
    }

    public static IEnumerable<object[]> Mismatches()
    {
        yield return new object[] { "pass removed", (Action<List<PassSignature>>)(f => f.RemoveAt(BloomUp)) };
        yield return new object[] { "pass added", (Action<List<PassSignature>>)(f => f.Add(Pass(999, Array.Empty<AttachmentUse>(), Array.Empty<int>()))) };
        yield return new object[] { "passes reordered", (Action<List<PassSignature>>)(f => { (f[BloomDown], f[BloomUp]) = (f[BloomUp], f[BloomDown]); }) };
        yield return new object[] { "name", (Action<List<PassSignature>>)(f => f[TaaSharpen].NameId = 555) };
        yield return new object[] { "attachment added", (Action<List<PassSignature>>)(f => f[TaaSharpen].Attachments = new[] { Use(Sharpened, ResourceUsage.ColorWrite, true), Use(Motion, ResourceUsage.ColorWrite) }) };
        yield return new object[] { "attachment removed", (Action<List<PassSignature>>)(f => f[Opaque].Attachments = new[] { Use(Primary, ResourceUsage.ColorWrite), Use(Depth, ResourceUsage.DepthWrite) }) };
        yield return new object[] { "attachment resource", (Action<List<PassSignature>>)(f => f[Blit].Attachments[0] = Use(Final + 100, ResourceUsage.ColorWrite)) };
        yield return new object[] { "attachment order", (Action<List<PassSignature>>)(f => f[TaaResolve].Attachments = new[] { Use(HistoryOut, ResourceUsage.ColorWrite), Use(Resolved, ResourceUsage.ColorWrite, true) }) };
        yield return new object[] { "usage", (Action<List<PassSignature>>)(f => f[SkyMotion].Attachments[1] = Use(Depth, ResourceUsage.DepthReadOnlySampled)) };
        yield return new object[] { "transient flag", (Action<List<PassSignature>>)(f => f[BloomUp].Attachments[0] = Use(Bloom2, ResourceUsage.ColorWrite, false)) };
        yield return new object[] { "read added", (Action<List<PassSignature>>)(f => f[FinalComposition].Reads = new[] { Sharpened, Bloom2, Depth }) };
        yield return new object[] { "read removed", (Action<List<PassSignature>>)(f => f[FinalComposition].Reads = new[] { Sharpened }) };
        yield return new object[] { "read changed", (Action<List<PassSignature>>)(f => f[TaaSharpen].Reads = new[] { Primary }) };
        yield return new object[] { "read order", (Action<List<PassSignature>>)(f => f[FinalComposition].Reads = new[] { Bloom2, Sharpened }) };
        yield return new object[] { "width", (Action<List<PassSignature>>)(f => f[BloomDown].Width = 960) };
        yield return new object[] { "height", (Action<List<PassSignature>>)(f => f[BloomDown].Height = 540) };
        yield return new object[] { "formats", (Action<List<PassSignature>>)(f => f[Opaque].FormatsId = 70) };
    }

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void EveryFieldChangeIsAMismatch(string field, object mutation)
    {
        // xunit needs public parameter types; the signature types are internal.
        var mutate = (Action<List<PassSignature>>)mutation;
        FramePlan plan = FramePlan.Build(TaaFrame());
        List<PassSignature> changed = TaaFrame();
        mutate(changed);
        Assert.False(plan.Matches(changed), $"A change in '{field}' still matched.");
    }

    [Fact]
    public void PlanIsASnapshotOfTheSignatures()
    {
        List<PassSignature> frame = TaaFrame();
        FramePlan plan = FramePlan.Build(frame);
        frame[TaaResolve].Reads[0] = 77;
        frame[Opaque].Attachments[0] = Use(77, ResourceUsage.ColorWrite);
        Assert.True(plan.Matches(TaaFrame()));
        Assert.False(plan.Matches(frame));
    }

    [Fact]
    public void TaaFrameLoadStoreSolve()
    {
        FramePlan plan = FramePlan.Build(TaaFrame());
        const AttachmentLoadOp L = AttachmentLoadOp.Load, LX = AttachmentLoadOp.DontCare;
        const AttachmentStoreOp S = AttachmentStoreOp.Store, SX = AttachmentStoreOp.DontCare;

        // Persistent attachments always load and store, including the history the next frame reads.
        AssertOps(plan, Opaque, 0, L, S);
        AssertOps(plan, Opaque, 1, L, S);
        AssertOps(plan, Opaque, 2, L, S);
        AssertOps(plan, SkyMotion, 0, L, S);
        AssertOps(plan, SkyMotion, 1, L, S);
        AssertOps(plan, TaaResolve, 1, L, S);
        AssertOps(plan, FinalComposition, 0, L, S);
        AssertOps(plan, Blit, 0, L, S);

        // Transients: first write does not load; stored because a later pass reads them.
        AssertOps(plan, TaaResolve, 0, LX, S);
        AssertOps(plan, TaaSharpen, 0, LX, S);
        AssertOps(plan, BloomDown, 0, LX, S);
        AssertOps(plan, BloomUp, 0, LX, S);

        // A transient nobody reads after its only pass is neither loaded nor stored.
        AssertOps(plan, BloomDown, 1, LX, SX);

        Assert.Equal(-1, plan.AliasSlot(Primary));
        Assert.Equal(-1, plan.AliasSlot(HistoryOut));
        Assert.Equal(-1, plan.AliasSlot(HistoryIn));
        Assert.Equal(-1, plan.AliasSlot(12345));
    }

    [Fact]
    public void TransientLastAttachedIsNotStoredButLoadsInLaterPasses()
    {
        const int scratch = 50;
        var frame = new List<PassSignature>
        {
            Pass(1, new[] { Use(scratch, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(scratch, ResourceUsage.ColorBlend, true) }, Array.Empty<int>()),
            Pass(3, new[] { Use(scratch, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
        };
        FramePlan plan = FramePlan.Build(frame);
        AssertOps(plan, 0, 0, AttachmentLoadOp.DontCare, AttachmentStoreOp.Store);
        AssertOps(plan, 1, 0, AttachmentLoadOp.Load, AttachmentStoreOp.Store);
        AssertOps(plan, 2, 0, AttachmentLoadOp.Load, AttachmentStoreOp.DontCare);
        Assert.Equal(0, plan.AliasSlot(scratch));
    }

    public static IEnumerable<object[]> NotReallyTransient()
    {
        const int r = 60;
        // Read before it is written this frame: it depends on last frame's contents.
        yield return new object[] { "read first", new List<PassSignature>
        {
            Pass(1, new[] { Use(99, ResourceUsage.ColorWrite) }, new[] { r }),
            Pass(2, new[] { Use(r, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
        }, 1 };
        // Blended into on first use: the destination is read.
        yield return new object[] { "blend first", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.ColorBlend, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(99, ResourceUsage.ColorWrite) }, new[] { r }),
        }, 0 };
        // Depth tested read-only on first use.
        yield return new object[] { "depth read first", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.DepthReadOnly, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(r, ResourceUsage.DepthWrite, true) }, Array.Empty<int>()),
        }, 0 };
        // Sampled by the pass that first writes it (feedback).
        yield return new object[] { "feedback first", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.ColorWrite, true) }, new[] { r }),
        }, 0 };
        // One use is not marked transient.
        yield return new object[] { "mixed flag", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(r, ResourceUsage.ColorBlend, false) }, Array.Empty<int>()),
        }, 0 };
    }

    [Theory]
    [MemberData(nameof(NotReallyTransient))]
    public void TransientThatDependsOnOlderContentsStaysPersistent(string why, object frameObject, int firstAttachedPass)
    {
        var frame = (List<PassSignature>)frameObject;
        const int r = 60;
        FramePlan plan = FramePlan.Build(frame);
        Assert.True(plan.AliasSlot(r) == -1, why);
        for (int p = 0; p < frame.Count; p++)
        {
            for (int a = 0; a < frame[p].Attachments.Length; a++)
            {
                if (frame[p].Attachments[a].ResourceId != r) continue;
                Assert.True(plan.LoadOp(p, a) == AttachmentLoadOp.Load, $"{why}: pass {p} (first attached {firstAttachedPass}) does not load.");
                Assert.True(plan.StoreOp(p, a) == AttachmentStoreOp.Store, $"{why}: pass {p} does not store.");
            }
        }
    }

    [Fact]
    public void TaaFrameAliasesDisjointTransientsAndNeverOverlapping()
    {
        List<PassSignature> frame = TaaFrame();
        FramePlan plan = FramePlan.Build(frame);

        // Resolved [2,3] and Bloom1 [4,5] share bucket (1920x1080, formats 2, index 0) and are disjoint.
        Assert.Equal(plan.AliasSlot(Resolved), plan.AliasSlot(Bloom1));
        // Bloom depth has another attachment index, so another bucket and its own slot.
        Assert.NotEqual(plan.AliasSlot(Resolved), plan.AliasSlot(BloomDepth));
        Assert.Equal(4, plan.AliasSlotCount);

        AssertNoOverlap(frame, plan, new[] { Resolved, Sharpened, Bloom1, Bloom2, BloomDepth });
    }

    [Fact]
    public void DifferentExtentOrFormatsNeverShareASlot()
    {
        var frame = new List<PassSignature>
        {
            Pass(1, new[] { Use(20, ResourceUsage.ColorWrite, true) }, Array.Empty<int>(), width: 960, height: 540),
            Pass(2, new[] { Use(21, ResourceUsage.ColorWrite, true) }, new[] { 20 }, width: 480, height: 270),
            Pass(3, new[] { Use(22, ResourceUsage.ColorWrite, true) }, new[] { 21 }, width: 480, height: 270, formats: 9),
            Pass(4, new[] { Use(23, ResourceUsage.ColorWrite, true) }, new[] { 22 }, width: 960, height: 540),
        };
        FramePlan plan = FramePlan.Build(frame);
        // 20 [0,1] and 23 [3,3] share the 960x540 bucket; 21 and 22 differ in extent/formats.
        Assert.Equal(plan.AliasSlot(20), plan.AliasSlot(23));
        Assert.Equal(3, plan.AliasSlotCount);
        Assert.NotEqual(plan.AliasSlot(21), plan.AliasSlot(22));
        Assert.NotEqual(plan.AliasSlot(20), plan.AliasSlot(21));
    }

    [Fact]
    public void RandomIntervalsNeverOverlapAndUseMinimalSlots()
    {
        var random = new Random(1234);
        var buckets = new[]
        {
            new SizeBucket(1920, 1080, 1, 0),
            new SizeBucket(960, 540, 1, 0),
            new SizeBucket(1920, 1080, 2, 0),
        };

        for (int round = 0; round < 500; round++)
        {
            int count = random.Next(0, 40);
            var intervals = new List<TransientInterval>(count);
            for (int i = 0; i < count; i++)
            {
                int first = random.Next(0, 30);
                intervals.Add(new TransientInterval(i, buckets[random.Next(buckets.Length)], first, first + random.Next(0, 8)));
            }

            int[] slots = TransientPlacement.Place(intervals);
            Assert.Equal(count, slots.Length);

            var slotBucket = new Dictionary<int, SizeBucket>();
            for (int i = 0; i < count; i++)
            {
                if (slotBucket.TryGetValue(slots[i], out SizeBucket existing))
                    Assert.Equal(existing, intervals[i].Bucket);
                else
                    slotBucket[slots[i]] = intervals[i].Bucket;

                for (int j = i + 1; j < count; j++)
                {
                    if (slots[i] != slots[j]) continue;
                    bool overlap = intervals[i].FirstPass <= intervals[j].LastPass && intervals[j].FirstPass <= intervals[i].LastPass;
                    Assert.False(overlap, $"round {round}: {intervals[i]} and {intervals[j]} share slot {slots[i]}");
                }
            }

            // Slots are dense and, per bucket, equal to the peak number of live intervals.
            for (int s = 0; s < slotBucket.Count; s++) Assert.True(slotBucket.ContainsKey(s));
            foreach (SizeBucket bucket in buckets)
            {
                int peak = 0;
                for (int pass = 0; pass < 40; pass++)
                {
                    int live = 0;
                    foreach (TransientInterval t in intervals)
                        if (t.Bucket == bucket && t.FirstPass <= pass && pass <= t.LastPass) live++;
                    peak = Math.Max(peak, live);
                }
                int used = 0;
                foreach (KeyValuePair<int, SizeBucket> entry in slotBucket)
                    if (entry.Value == bucket) used++;
                Assert.Equal(peak, used);
            }
        }
    }

    [Fact]
    public void PlacementRejectsInvertedIntervals()
    {
        Assert.Throws<ArgumentException>(() =>
            TransientPlacement.Place(new[] { new TransientInterval(1, new SizeBucket(1, 1, 1, 0), 3, 2) }));
    }

    [Fact]
    public void ChangedFrameGetsConservativePlan()
    {
        FramePlan previous = FramePlan.Build(TaaFrame());

        List<PassSignature> changed = TaaFrame();
        changed.RemoveAt(BloomUp); // bloom toggled: the chain is one pass shorter
        changed[BloomUp].Reads = new[] { Sharpened, Bloom1 };

        FramePlan applied = FramePlan.Select(previous, changed);
        Assert.NotSame(previous, applied);
        Assert.True(applied.IsConservative);
        Assert.True(applied.Matches(changed));
        Assert.Equal(0, applied.AliasSlotCount);
        for (int p = 0; p < changed.Count; p++)
        {
            for (int a = 0; a < changed[p].Attachments.Length; a++)
            {
                Assert.Equal(AttachmentLoadOp.Load, applied.LoadOp(p, a));
                Assert.Equal(AttachmentStoreOp.Store, applied.StoreOp(p, a));
                Assert.Equal(-1, applied.AliasSlot(changed[p].Attachments[a].ResourceId));
            }
        }

        // The same frame again reuses the plan built from it; no previous plan is conservative.
        Assert.Same(previous, FramePlan.Select(previous, TaaFrame()));
        Assert.True(FramePlan.Select(null, TaaFrame()).IsConservative);

        // The next frame's plan, built from the changed frame, is solved again.
        FramePlan rebuilt = FramePlan.Build(changed);
        Assert.False(rebuilt.IsConservative);
        Assert.Equal(AttachmentStoreOp.DontCare, rebuilt.StoreOp(BloomDown, 1));
    }

    [Fact]
    public void OutOfRangeIndicesThrow()
    {
        FramePlan plan = FramePlan.Build(TaaFrame());
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.LoadOp(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.LoadOp(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.StoreOp(TaaSharpen, 1));
    }

    private static void AssertOps(FramePlan plan, int pass, int attachment, AttachmentLoadOp load, AttachmentStoreOp store)
    {
        Assert.True(load == plan.LoadOp(pass, attachment), $"pass {pass} attachment {attachment}: load {plan.LoadOp(pass, attachment)}, expected {load}");
        Assert.True(store == plan.StoreOp(pass, attachment), $"pass {pass} attachment {attachment}: store {plan.StoreOp(pass, attachment)}, expected {store}");
    }

    private static void AssertNoOverlap(List<PassSignature> frame, FramePlan plan, int[] transients)
    {
        var first = new Dictionary<int, int>();
        var last = new Dictionary<int, int>();
        for (int p = 0; p < frame.Count; p++)
        {
            var ids = new List<int>();
            foreach (AttachmentUse use in frame[p].Attachments) ids.Add(use.ResourceId);
            ids.AddRange(frame[p].Reads);
            foreach (int id in ids)
            {
                if (!first.ContainsKey(id)) first[id] = p;
                last[id] = p;
            }
        }

        foreach (int a in transients)
        {
            Assert.True(plan.AliasSlot(a) >= 0, $"transient {a} has no slot");
            foreach (int b in transients)
            {
                if (a >= b || plan.AliasSlot(a) != plan.AliasSlot(b)) continue;
                Assert.False(first[a] <= last[b] && first[b] <= last[a], $"{a} [{first[a]},{last[a]}] and {b} [{first[b]},{last[b]}] share slot {plan.AliasSlot(a)}");
            }
        }
    }
}
