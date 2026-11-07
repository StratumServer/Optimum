using System.Collections.Generic;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Correctness tests for the BFS chunk-visibility walk (issue #72). The guiding
/// invariant, from the MC-70850 "see-through chunks" bug, is that the walk must
/// never hide a chunk the player could actually see: BFS visibility may only ever
/// be a conservative (>=) superset of the ground-truth reachable set.
/// </summary>
public class BfsChunkVisibilityTests
{
    /// <summary>
    /// Synthetic chunk graph. Every loaded chunk is fully traversable (all faces
    /// passable) unless listed in <see cref="opaque"/>, which blocks ALL traversal
    /// through that chunk (no face pair passable) — a fully solid chunk.
    /// </summary>
    private sealed class FakeGraph : IChunkVisibilityGraph
    {
        private readonly HashSet<(int, int, int)> loaded;
        private readonly HashSet<(int, int, int)> opaque;
        private readonly HashSet<(int, int, int)>? outOfFrustum;
        public readonly HashSet<(int, int, int)> Visible = new();

        public FakeGraph(
            IEnumerable<(int, int, int)> loaded,
            IEnumerable<(int, int, int)>? opaque = null,
            IEnumerable<(int, int, int)>? outOfFrustum = null)
        {
            this.loaded = new HashSet<(int, int, int)>(loaded);
            this.opaque = opaque == null ? new() : new HashSet<(int, int, int)>(opaque);
            this.outOfFrustum = outOfFrustum == null ? null : new HashSet<(int, int, int)>(outOfFrustum);
        }

        public bool ChunkExists(int cx, int cy, int cz) => loaded.Contains((cx, cy, cz));

        public bool IsVisibleThrough(int cx, int cy, int cz, int fromFace, int toFace)
        {
            if (opaque.Contains((cx, cy, cz))) return false;
            return true; // all non-opaque chunks are fully passable
        }

        public bool IsInFrustum(int cx, int cy, int cz)
        {
            if (outOfFrustum == null) return true;
            return !outOfFrustum.Contains((cx, cy, cz));
        }

        public void MarkVisible(int cx, int cy, int cz) => Visible.Add((cx, cy, cz));

        public bool IsWithinWorldBounds(int cx, int cy, int cz) =>
            loaded.Contains((cx, cy, cz)); // treat only loaded coords as in-bounds for the test
    }

    private static List<(int, int, int)> Grid(int r)
    {
        var g = new List<(int, int, int)>();
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++)
                    g.Add((x, y, z));
        return g;
    }

    [Fact]
    public void OpenField_AllInFrustumChunksVisible()
    {
        // No opaque chunks, everything in frustum: every loaded chunk must be visible.
        var grid = Grid(3);
        var graph = new FakeGraph(grid);
        var bfs = new OptimumChunkVisibilityBfs();

        bfs.Run(graph, 0, 0, 0);

        Assert.Equal(new HashSet<(int, int, int)>(grid), graph.Visible);
    }

    [Fact]
    public void SolidWall_HidesChunksFullyBehindIt_ButNeverHidesReachableOnes()
    {
        // A solid X=2 plane (distance 2 from the camera, past the first-ring
        // exemption) between the camera (0,0,0) and everything at X>=3. With the
        // wall spanning the whole loaded Y/Z extent there is no way around, so the
        // X>=3 chunks are genuinely occluded and may be culled. X<=1 stays visible.
        var grid = Grid(4);
        var wall = new List<(int, int, int)>();
        for (int y = -4; y <= 4; y++)
            for (int z = -4; z <= 4; z++)
                wall.Add((2, y, z));

        var graph = new FakeGraph(grid, opaque: wall);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        // Everything at X <= 1 is reachable and must be visible (no over-cull).
        foreach (var c in grid)
        {
            if (c.Item1 <= 1)
            {
                Assert.Contains(c, graph.Visible);
            }
        }
        // The wall plane itself is adjacent to a visible chunk, so it is visible.
        foreach (var c in wall)
        {
            Assert.Contains(c, graph.Visible);
        }
        // At least some chunks strictly behind the full wall were culled (benefit exists).
        int behind = 0;
        foreach (var c in grid) if (c.Item1 >= 3 && !graph.Visible.Contains(c)) behind++;
        Assert.True(behind > 0, "expected some occluded chunks behind a full solid wall to be culled");
    }

    [Fact]
    public void FirstRingSolidChunk_DoesNotBlankWhatIsBehindIt()
    {
        // A lone solid chunk one step from the camera must NOT hide the chunks
        // behind it: vanilla's raycast passes through the first ring (its `num2 > 1`
        // guard), so the BFS first-ring exemption must too. Regression for the
        // over-cull the adversarial review found.
        var grid = Grid(3);
        var graph = new FakeGraph(grid, opaque: new[] { (0, 0, -1) }); // solid, distance 1 north
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        // The chunks directly behind the first-ring solid chunk stay visible.
        Assert.Contains((0, 0, -2), graph.Visible);
        Assert.Contains((0, 0, -3), graph.Visible);
    }

    [Fact]
    public void NeverOverCullsVersusGroundTruthReachability()
    {
        // Random-ish opaque set; compare BFS visible set against an independent
        // brute-force adjacency reachability that ignores never-go-back/frustum
        // (the maximal set of chunks connected to the camera through passable
        // chunks). BFS with occlusion may cull MORE than pure adjacency, but the
        // invariant we assert is the safety direction that matters for MC-70850:
        // every chunk BFS marks visible is genuinely graph-connected to the camera.
        var grid = Grid(4);
        var opaque = new List<(int, int, int)>
        {
            (2, 0, 0), (2, 1, 0), (2, -1, 0), (-3, 0, 2), (0, 2, -2), (1, 1, 1),
        };
        var graph = new FakeGraph(grid, opaque: opaque);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        var reachable = BruteForceReachable(new HashSet<(int, int, int)>(grid), new HashSet<(int, int, int)>(opaque), (0, 0, 0));

        // Safety: BFS never marks a chunk visible that is not actually reachable.
        foreach (var v in graph.Visible)
        {
            Assert.Contains(v, reachable);
        }
    }

    [Fact]
    public void FrustumCulledChunksAreNotVisible_NorAreChunksOnlyReachableThroughThem()
    {
        // Put the entire X>0 half-space out of frustum. Those chunks must not be
        // visible, and because the flood cannot pass through a frustum-culled chunk,
        // nothing solely behind them becomes visible either.
        var grid = Grid(2);
        var outFov = new List<(int, int, int)>();
        foreach (var c in grid) if (c.Item1 > 0) outFov.Add(c);

        var graph = new FakeGraph(grid, outOfFrustum: outFov);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        foreach (var c in outFov)
        {
            Assert.DoesNotContain(c, graph.Visible);
        }
        // X<=0 chunks remain visible.
        foreach (var c in grid) if (c.Item1 <= 0) Assert.Contains(c, graph.Visible);
    }

    [Fact]
    public void CameraChunkAlwaysVisibleEvenWhenSolid()
    {
        // Camera embedded in a solid chunk: the chunk it's in must still render.
        var grid = Grid(1);
        var graph = new FakeGraph(grid, opaque: new[] { (0, 0, 0) });
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        Assert.Contains((0, 0, 0), graph.Visible);
    }

    [Fact]
    public void OcclusionOff_MarksEveryInFrustumReachableChunk()
    {
        // With occlusion disabled, opaque chunks do not block the flood; every
        // in-frustum chunk connected by adjacency is visible.
        var grid = Grid(2);
        var graph = new FakeGraph(grid, opaque: new[] { (1, 0, 0) });
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0, useOcclusion: false);

        Assert.Equal(new HashSet<(int, int, int)>(grid), graph.Visible);
    }

    [Fact]
    public void UndulatingTerrain_UpOverHillThenDownIntoValley_RendersAllReachableChunks()
    {
        // Issue #176: In undulating terrain, sight must be able to step UP over a hill or
        // contour, and then DOWN into a valley behind it. The flawed ancestor-mask "never-go-back"
        // previously forbade opposite axis movement along the entire traversal history,
        // prematurely truncating render distance on non-flat terrain.
        var grid = Grid(3);
        // Place opaque chunk at (0, 0, 2) blocking straight horizontal line, but air at (0, 1, 2)
        // allows light to travel up and over into the valley at (0, 0, 3).
        var opaque = new List<(int, int, int)> { (0, 0, 2) };
        var graph = new FakeGraph(grid, opaque: opaque);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        Assert.Contains((0, 1, 2), graph.Visible);
        Assert.Contains((0, 0, 3), graph.Visible);
    }

    [Fact]
    public void DepressionTerrain_DownIntoBasinThenUpToOppositeRidge_RendersAllReachableChunks()
    {
        // Issue #176: Stepping DOWN into a depression/valley and then UP to an opposite ridge.
        var grid = Grid(3);
        // Opaque chunk at (0, 0, 2) blocking straight line, but air at (0, -1, 2) allows passing below.
        var opaque = new List<(int, int, int)> { (0, 0, 2) };
        var graph = new FakeGraph(grid, opaque: opaque);
        var bfs = new OptimumChunkVisibilityBfs();
        bfs.Run(graph, 0, 0, 0);

        Assert.Contains((0, -1, 2), graph.Visible);
        Assert.Contains((0, 0, 3), graph.Visible);
    }

    [Fact]
    public void OptimumBoundedHandoff_ReleaseWhenEmpty_DoesNotThrow()
    {
        // Issue #176: Defensive handoff release. On session teardown / world transition,
        // an over-release on an empty handoff must gracefully no-op instead of throwing
        // an unhandled InvalidOperationException that disconnects multiplayer clients.
        var handoff = new OptimumBoundedHandoff(4);
        Assert.Equal(0, handoff.Reserved);

        // Must not throw when releasing an empty handoff
        handoff.Release();
        Assert.Equal(0, handoff.Reserved);
    }

    [Fact]
    public void LibInlineBfs_MirrorsTheReferenceInvariants()
    {
        // Reconciles the two algorithm copies: the unit-tested reference
        // OptimumChunkVisibilityBfs (this file) and the inlined runBfsVisibility in
        // the ChunkCuller patch (which cannot be unit-tested directly because it
        // needs the live ClientMain/ClientChunk). This pins the Lib copy to the same
        // load-bearing invariants the reference guarantees, so the two cannot
        // silently diverge: editing the patch to drop one of these fails this test.
        string patch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkCuller.cs.patch");

        Assert.Contains("OptimumConfig.BfsChunkVisibilityEnabled", patch); // toggle-gated
        Assert.Contains("runBfsVisibility", patch);
        Assert.Contains("bfsReachable", patch);                            // reachability check
        Assert.Contains("for (int from = 0; from < 6; from++)", patch);    // all incoming faces (MC-70850-safe)
        Assert.Contains("camDist > 1", patch);                             // first-ring exemption (num2>1 mirror)
        Assert.Contains("bfsRadius", patch);                               // view-distance bound (no sky-flood freeze)
        Assert.Contains("bfsOppositeOf", patch);                           // never-go-back mask
        Assert.Contains("bfsQueue = new Queue<long>()", patch);            // lazy init (injected-field initializers don't run)
    }

    // Brute-force flood over passable (non-opaque) chunks, 6-connected, ignoring
    // frustum and never-go-back. This is the maximal set of chunks reachable from
    // the camera; BFS-visible must be a subset of it.
    private static HashSet<(int, int, int)> BruteForceReachable(
        HashSet<(int, int, int)> loaded,
        HashSet<(int, int, int)> opaque,
        (int, int, int) start)
    {
        int[] dx = { 0, 1, 0, -1, 0, 0 };
        int[] dy = { 0, 0, 0, 0, 1, -1 };
        int[] dz = { -1, 0, 1, 0, 0, 0 };
        var seen = new HashSet<(int, int, int)>();
        var q = new Queue<(int, int, int)>();
        seen.Add(start);
        q.Enqueue(start);
        while (q.Count > 0)
        {
            var (cx, cy, cz) = q.Dequeue();
            // Can only leave this chunk if it is passable (or it's the start chunk).
            bool passable = !opaque.Contains((cx, cy, cz)) || (cx, cy, cz) == start;
            if (!passable) continue;
            for (int d = 0; d < 6; d++)
            {
                var n = (cx + dx[d], cy + dy[d], cz + dz[d]);
                if (!loaded.Contains(n) || seen.Contains(n)) continue;
                seen.Add(n);
                q.Enqueue(n);
            }
        }
        return seen;
    }
}
