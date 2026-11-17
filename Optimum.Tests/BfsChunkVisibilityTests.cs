using System;
using System.Collections.Generic;
using System.Linq;
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
    private static HashSet<(int, int, int)> RunGrid(
        IEnumerable<(int, int, int)> loaded,
        IEnumerable<(int, int, int)>? opaque = null,
        (int X, int Y, int Z) cam = default,
        int radius = 4,
        bool useOcclusion = true)
    {
        var loadedSet = new HashSet<(int, int, int)>(loaded);
        var opaqueSet = opaque == null ? new HashSet<(int, int, int)>() : new HashSet<(int, int, int)>(opaque);
        int minX = loadedSet.Min(c => c.Item1);
        int minY = loadedSet.Min(c => c.Item2);
        int minZ = loadedSet.Min(c => c.Item3);
        int shiftX = minX < 0 ? -minX : 0;
        int shiftY = minY < 0 ? -minY : 0;
        int shiftZ = minZ < 0 ? -minZ : 0;

        int maxX = loadedSet.Max(c => c.Item1) + shiftX + 1;
        int maxY = loadedSet.Max(c => c.Item2) + shiftY + 1;
        int maxZ = loadedSet.Max(c => c.Item3) + shiftZ + 1;
        int ry = maxY + 2;

        var grid = new OptimumChunkVisibilityGrid();
        grid.Prepare(radius, ry);
        int scx = cam.X + shiftX;
        int scy = cam.Y + shiftY;
        int scz = cam.Z + shiftZ;

        foreach (var c in loadedSet)
        {
            int sx = c.Item1 + shiftX;
            int sy = c.Item2 + shiftY;
            int sz = c.Item3 + shiftZ;
            int lx = sx - scx + radius;
            int ly = sy - scy + ry;
            int lz = sz - scz + radius;
            if ((uint)lx < (uint)grid.Width && (uint)lz < (uint)grid.Width && (uint)ly < (uint)grid.Height)
            {
                ushort trav = opaqueSet.Contains(c) ? (ushort)0 : OptimumChunkVisibilityGrid.AllPass;
                grid.SetCell(grid.IndexOf(lx, ly, lz), OptimumChunkVisibilityGrid.EncodeCell(trav, fresh: true));
            }
        }

        grid.Run(scx, scy, scz, maxX, maxY, maxZ, useOcclusion);

        var visible = new HashSet<(int, int, int)>();
        for (int i = 0; i < grid.QueueLength; i++)
        {
            int idx = grid.ReachedCell(i);
            int lx = idx % grid.Width;
            int rest = idx / grid.Width;
            int lz = rest % grid.Width;
            int ly = rest / grid.Width;
            var pos = (cam.X + lx - radius, cam.Y + ly - ry, cam.Z + lz - radius);
            if (loadedSet.Contains(pos))
            {
                visible.Add(pos);
            }
        }
        return visible;
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
        var grid = Grid(3);
        var visible = RunGrid(grid, radius: 4);

        Assert.Equal(new HashSet<(int, int, int)>(grid), visible);
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

        var visible = RunGrid(grid, opaque: wall, radius: 5);

        foreach (var c in grid)
        {
            if (c.Item1 <= 1)
            {
                Assert.Contains(c, visible);
            }
        }
        foreach (var c in wall)
        {
            Assert.Contains(c, visible);
        }
        int behind = 0;
        foreach (var c in grid) if (c.Item1 >= 3 && !visible.Contains(c)) behind++;
        Assert.True(behind > 0, "expected some occluded chunks behind a full solid wall to be culled");
    }

    [Fact]
    public void FirstRingSolidChunk_DoesNotBlankWhatIsBehindIt()
    {
        // A lone solid chunk one step from the camera must NOT hide the chunks
        // behind it: vanilla's raycast passes through the first ring (its `num2 > 1`
        // guard), so the BFS first-ring exemption must too.
        var grid = Grid(3);
        var visible = RunGrid(grid, opaque: new[] { (0, 0, -1) }, radius: 4);

        Assert.Contains((0, 0, -2), visible);
        Assert.Contains((0, 0, -3), visible);
    }

    [Fact]
    public void NeverOverCullsVersusGroundTruthReachability()
    {
        var grid = Grid(4);
        var opaque = new List<(int, int, int)>
        {
            (2, 0, 0), (2, 1, 0), (2, -1, 0), (-3, 0, 2), (0, 2, -2), (1, 1, 1),
        };
        var visible = RunGrid(grid, opaque: opaque, radius: 5);
        var reachable = BruteForceReachable(new HashSet<(int, int, int)>(grid), new HashSet<(int, int, int)>(opaque), (0, 0, 0));

        foreach (var v in visible)
        {
            Assert.Contains(v, reachable);
        }
    }

    [Fact]
    public void CameraChunkAlwaysVisibleEvenWhenSolid()
    {
        var grid = Grid(1);
        var visible = RunGrid(grid, opaque: new[] { (0, 0, 0) }, radius: 2);

        Assert.Contains((0, 0, 0), visible);
    }

    [Fact]
    public void OcclusionOff_MarksEveryReachableChunk()
    {
        var grid = Grid(2);
        var visible = RunGrid(grid, opaque: new[] { (1, 0, 0) }, radius: 3, useOcclusion: false);

        Assert.Equal(new HashSet<(int, int, int)>(grid), visible);
    }

    [Fact]
    public void UndulatingTerrain_UpOverHill_RendersRidgeWhileHidingChunkDirectlyBehindWall()
    {
        // With monotonic entry-face traversal (matching vanilla raycasts), sight steps UP
        // over the hill at (0, 1, 2) to reveal (0, 1, 3), while (0, 0, 3) directly behind
        // the fully opaque hill core at (0, 0, 2) stays occluded because no monotone ray reaches it.
        var grid = Grid(3);
        var opaque = new List<(int, int, int)> { (0, 0, 2) };
        var visible = RunGrid(grid, opaque: opaque, radius: 4);

        Assert.Contains((0, 1, 2), visible);
        Assert.Contains((0, 1, 3), visible);
        Assert.DoesNotContain((0, 0, 3), visible);
    }

    [Fact]
    public void DepressionTerrain_DownIntoBasin_RendersBasinWhileHidingChunkDirectlyBehindWall()
    {
        var grid = Grid(3);
        var opaque = new List<(int, int, int)> { (0, 0, 2) };
        var visible = RunGrid(grid, opaque: opaque, radius: 4);

        Assert.Contains((0, -1, 2), visible);
        Assert.Contains((0, -1, 3), visible);
        Assert.DoesNotContain((0, 0, 3), visible);
    }

    [Fact]
    public void LibAdapter_RunsTheSharedFlatGridCore()
    {
        // The ChunkCuller patch cannot be unit-tested directly (it needs the live
        // ClientMain and ClientChunk), so it delegates the walk to OptimumChunkVisibilityGrid,
        // which OptimumChunkVisibilityGridTests exercises. This pins the delegation and the
        // lifecycle rules of the adapter, so editing the patch to bring back an inline copy,
        // a lock around the walk, measurement scaffolding, or a settings-watcher rebuild fails here.
        string patch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkCuller.cs.patch");

        Assert.Contains("OptimumConfig.BfsChunkVisibilityEnabled", patch);  // toggle-gated, off = raycast
        Assert.Contains("OptimumChunkVisibilityGrid", patch);               // the shared core
        Assert.Contains("grid.Run(", patch);
        Assert.Contains("ShouldRecalculate(", patch);                       // throttle decision lives in the core
        Assert.Contains("bfsPrepareGrid();", patch);                        // sized at the start of a pass
        Assert.Contains("bfsResetAndFill(chunks);", patch);                 // snapshot under chunksLock
        Assert.Contains("OptimumChunkVisibilityGrid.EncodeCell(", patch);
        Assert.Contains("bool fresh = System.Threading.Volatile.Read(ref chunk.traversabilityFresh);", patch);  // fresh flag acquire-read before the bits
        Assert.Contains("Array.Clear(chunkGrid, 0, grid.CellCount);", patch); // chunk references dropped after the pass
        Assert.Contains("runFlatBfsVisibility(ClientSettings.Occlusionculling);", patch);

        // The walk runs outside chunksLock: no lock between the reset and runFlatBfsVisibility.
        Assert.DoesNotContain("runBfsVisibility", patch);
        Assert.DoesNotContain("new Queue<long>()", patch);
        int call = patch.IndexOf("runFlatBfsVisibility(ClientSettings.Occlusionculling);", StringComparison.Ordinal);
        Assert.True(call > 0);
        string before = patch.Substring(Math.Max(0, call - 120), 120);
        Assert.DoesNotContain("lock (game.WorldMap.chunksLock)", before);

        // The toggle is read once per pass into a local; no later branch re-reads the static.
        Assert.Contains("bool flatBfs = OptimumConfig.BfsChunkVisibilityEnabled;", patch);
        Assert.Contains("if (flatBfs)", patch);
        Assert.Equal(2, CountOccurrences(patch, "OptimumConfig.BfsChunkVisibilityEnabled")); // gate + the capture
        Assert.DoesNotContain("150L, 1000L, 2000, 10", patch);                           // named constants, not literals

        // No temporary measurement scaffold remains in the patch.
        Assert.DoesNotContain("OPTIMUM_CULL_BFS", patch);
        Assert.DoesNotContain("OPTIMUM_CULL_SHADOW", patch);
        Assert.DoesNotContain("bfsModeBits", patch);
        Assert.DoesNotContain("bfsRunShadowCompare", patch);

        // The grid is never resized from the viewDistance watcher (cross-thread tear).
        Assert.DoesNotContain("AddWatcher<int>(\"viewDistance\", bfs", patch);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
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
