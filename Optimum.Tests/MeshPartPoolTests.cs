using System;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Issue #73: OptimumMeshPartPool recycles the per-chunk CustomMeshDataPart clone
/// buffers. These tests pin the load-bearing invariants: the pooled clone is
/// byte-identical to a vanilla Clone(), a reclaimed buffer is reused (the actual
/// allocation win), non-auto-allocation parts fall back to vanilla Clone(), and
/// the wiring (transplant target, gate, reclaim call, counter) is in place.
/// </summary>
public class MeshPartPoolTests
{
    private static CustomMeshDataPartInt MakeIntPart(int count, int seed)
    {
        var p = new CustomMeshDataPartInt(count);
        for (int i = 0; i < count; i++) p.Add(seed + i);
        p.InterleaveStride = 4;
        p.InterleaveSizes = new[] { 1 };
        p.InterleaveOffsets = new[] { 0 };
        return p;
    }

    [Fact]
    public void PooledClone_IsByteIdenticalToVanillaClone()
    {
        OptimumMeshPartPool.Clear();
        var src = MakeIntPart(4096, 100);

        var vanilla = src.Clone();
        var pooled = OptimumMeshPartPool.TestClonePartInt(src);

        Assert.Equal(vanilla.Count, pooled.Count);
        Assert.Equal(vanilla.InterleaveStride, pooled.InterleaveStride);
        Assert.Equal(vanilla.InterleaveSizes, pooled.InterleaveSizes);
        Assert.Equal(vanilla.InterleaveOffsets, pooled.InterleaveOffsets);
        for (int i = 0; i < src.Count; i++)
        {
            Assert.Equal(src.Values[i], pooled.Values[i]);
            Assert.Equal(vanilla.Values[i], pooled.Values[i]);
        }
    }

    [Fact]
    public void ReclaimedBuffer_IsReused_NoNewValuesArray()
    {
        OptimumMeshPartPool.Clear();
        var src = MakeIntPart(4096, 7);

        // First clone: pool cold -> fresh buffer.
        var first = OptimumMeshPartPool.TestClonePartInt(src);
        int[] firstBuffer = first.Values;

        // Reclaim it and drain onto the free list, then clone again.
        OptimumMeshPartPool.TestReclaimInt(first);
        OptimumMeshPartPool.TestDrain();
        Assert.Equal(1, OptimumMeshPartPool.TestFreeIntCount);

        var second = OptimumMeshPartPool.TestClonePartInt(src);

        // The second clone must reuse the exact buffer instance from the first,
        // proving zero new Values[] allocation on the warm path.
        Assert.Same(firstBuffer, second.Values);
        Assert.Equal(0, OptimumMeshPartPool.TestFreeIntCount);
        for (int i = 0; i < src.Count; i++) Assert.Equal(src.Values[i], second.Values[i]);
    }

    [Fact]
    public void CustomAllocationSizePart_FallsBackToVanillaClone()
    {
        OptimumMeshPartPool.Clear();
        var src = MakeIntPart(4096, 1);
        src.SetAllocationSize(8192); // AllocationSize != Count -> not poolable

        var reclaimable = MakeIntPart(4096, 2);
        OptimumMeshPartPool.TestReclaimInt(reclaimable);
        OptimumMeshPartPool.TestDrain();
        int freeBefore = OptimumMeshPartPool.TestFreeIntCount;

        var result = OptimumMeshPartPool.TestClonePartInt(src);

        // Fallback path must not consume a pooled buffer, and must preserve the
        // custom allocation size (vanilla Clone() copies it).
        Assert.Equal(freeBefore, OptimumMeshPartPool.TestFreeIntCount);
        Assert.Equal(8192, result.AllocationSize);
        Assert.Equal(4096, result.Count);
    }

    [Fact]
    public void MeshPartPool_HasDiagnosticsCounter()
    {
        Assert.True(OptimumDiagnostics.Counters.ContainsKey("MeshPartPool"));
    }

    // ---- source-wiring pins (keep the transplant + gate + reclaim wired) ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void ChunkTesselatorPatch_RoutesClonesThroughPool()
    {
        string patch = File.ReadAllText(Path.Combine(RepoRoot(),
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch"));
        Assert.Contains("OptimumCloneChunkMesh", patch);
        Assert.Contains("OptimumConfig.MeshPartPoolActive", patch);
    }

    [Fact]
    public void TesselatedChunkPartPatch_ReclaimsAfterUpload()
    {
        string patch = File.ReadAllText(Path.Combine(RepoRoot(),
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunkPart.cs.patch"));
        Assert.Contains("OptimumMeshPartPool.Reclaim", patch);
        Assert.Contains("MeshPartPoolActive", patch);
    }

    [Fact]
    public void PatcherRegistersOptimumCloneChunkMeshTransplant()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "Optimum.Patcher/Program.cs"));
        Assert.Contains("\"OptimumCloneChunkMesh\"", program);
    }
}

// Shares OptimumDiagnostics tessellation worker state; must serialize with the
// other tessellation-state tests (same reason OptimumDiagnosticsCountersTests
// uses this collection) so registering workers here cannot perturb the global
// static state other test classes read.
[Collection("TessellationDiagnostics")]
public class MeshPartPoolGateTests
{
    [Fact]
    public void MeshPartPoolActive_StaysActiveWithMultipleWorkers()
    {
        OptimumDiagnostics.ResetTessellation();
        bool originalEnabled = OptimumConfig.MeshPartPoolEnabled;
        try
        {
            OptimumConfig.MeshPartPoolEnabled = true;
            Assert.True(OptimumConfig.MeshPartPoolActive);

            var registry = new OptimumTesselationWorkerRegistry();
            registry.Register(11);
            registry.Register(22); // second worker
            Assert.True(OptimumDiagnostics.TessWorkerCount >= 2);
            Assert.True(OptimumConfig.MeshPartPoolActive);

            OptimumConfig.MeshPartPoolEnabled = false;
            Assert.False(OptimumConfig.MeshPartPoolActive);
        }
        finally
        {
            OptimumConfig.MeshPartPoolEnabled = originalEnabled;
            OptimumDiagnostics.ResetTessellation();
        }
    }

    [Fact]
    public void WorkerSlotsAreIsolatedAndCrossThreadReclaimRoutesToOrigin()
    {
        OptimumMeshPartPool.Clear();
        var srcA = new CustomMeshDataPartInt(4096) { Count = 4096 };
        srcA.Values[0] = 10;
        var srcB = new CustomMeshDataPartInt(4096) { Count = 4096 };
        srcB.Values[0] = 20;

        CustomMeshDataPartInt? rentedA = null;
        int[]? bufA = null;
        var workerReady = new System.Threading.ManualResetEventSlim(false);
        var mainReclaimed = new System.Threading.ManualResetEventSlim(false);
        int[]? reusedBufA = null;

        var workerA = new System.Threading.Thread(() =>
        {
            rentedA = OptimumMeshPartPool.TestClonePartInt(srcA);
            bufA = rentedA.Values;
            workerReady.Set();
            mainReclaimed.Wait();
            OptimumMeshPartPool.TestDrain();
            var secondA = OptimumMeshPartPool.TestClonePartInt(srcB);
            reusedBufA = secondA.Values;
        });
        workerA.Start();
        workerReady.Wait();

        // Main thread reclaims workerA's buffer; another thread must NOT steal it,
        // while workerA itself must get the exact reclaimed array back.
        OptimumMeshPartPool.TestReclaimInt(rentedA);
        OptimumMeshPartPool.TestDrain();
        Assert.Equal(0, OptimumMeshPartPool.TestFreeIntCount);

        mainReclaimed.Set();
        workerA.Join();
        Assert.Same(bufA, reusedBufA);
    }
}
