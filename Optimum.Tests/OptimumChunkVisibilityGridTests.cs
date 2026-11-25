using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Tests for the flat-grid chunk visibility walk (OptimumChunkVisibilityGrid), the class
/// the ChunkCuller patch runs. The walk is checked against a port of the vanilla ray
/// traversal (the monotonic walk must mark a superset of what the vanilla rays mark).
/// Algorithm after tehtelev's flat-grid ChunkCuller.
/// </summary>
public class OptimumChunkVisibilityGridTests
{
    private sealed class World
    {
        public int MapX, MapY, MapZ;
        public readonly Dictionary<(int X, int Y, int Z), ushort> Chunks = new();
    }

    // Vanilla ClientChunk.traversabilityMapping (1.22.7 decompile), pair -> bit.
    private static readonly (int A, int B, int Bit)[] VanillaPairs =
    {
        (0, 1, 0), (0, 3, 1), (0, 2, 2), (0, 4, 3), (0, 5, 4),
        (1, 2, 5), (1, 3, 6), (1, 4, 7), (1, 5, 8),
        (2, 3, 9), (2, 4, 10), (2, 5, 11),
        (3, 4, 12), (3, 5, 13), (4, 5, 14),
    };

    private static World RandomWorld(int seed, int mapX, int mapY, int mapZ, double loadedChance, double passChance, double staleChance)
    {
        var rng = new Random(seed);
        var world = new World { MapX = mapX, MapY = mapY, MapZ = mapZ };
        for (int x = 0; x < mapX; x++)
        {
            for (int y = 0; y < mapY; y++)
            {
                for (int z = 0; z < mapZ; z++)
                {
                    if (rng.NextDouble() >= loadedChance)
                    {
                        continue;
                    }
                    ushort mask = 0;
                    for (int bit = 0; bit < 15; bit++)
                    {
                        if (rng.NextDouble() < passChance)
                        {
                            mask |= (ushort)(1 << bit);
                        }
                    }
                    bool fresh = rng.NextDouble() >= staleChance;
                    world.Chunks[(x, y, z)] = OptimumChunkVisibilityGrid.EncodeCell(mask, fresh);
                }
            }
        }
        return world;
    }

    private static bool Valid(World w, int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < w.MapX && y < w.MapY && z < w.MapZ;

    private static HashSet<(int, int, int)> RunGrid(World w, (int X, int Y, int Z) cam, int viewDistance,
        bool occlusion = true)
    {
        int r = viewDistance / 32 + 4;
        int ry = w.MapY + 2;
        var grid = new OptimumChunkVisibilityGrid();
        grid.Prepare(r, ry);
        foreach (var kv in w.Chunks)
        {
            int lx = kv.Key.X - cam.X + r;
            int ly = kv.Key.Y - cam.Y + ry;
            int lz = kv.Key.Z - cam.Z + r;
            if ((uint)lx < (uint)grid.Width && (uint)lz < (uint)grid.Width && (uint)ly < (uint)grid.Height)
            {
                grid.SetCell(grid.IndexOf(lx, ly, lz), kv.Value);
            }
        }

        grid.Run(cam.X, cam.Y, cam.Z, w.MapX, w.MapY, w.MapZ, occlusion);

        var visible = new HashSet<(int, int, int)>();
        for (int i = 0; i < grid.QueueLength; i++)
        {
            int idx = grid.ReachedCell(i);
            int lx = idx % grid.Width;
            int rest = idx / grid.Width;
            int lz = rest % grid.Width;
            int ly = rest / grid.Width;
            var pos = (cam.X + lx - r, cam.Y + ly - ry, cam.Z + lz - r);
            if (w.Chunks.ContainsKey(pos))
            {
                visible.Add(pos);
            }
        }
        return visible;
    }

    [Fact]
    public void PairBits_MatchVanillaTraversabilityMapping()
    {
        foreach (var (a, b, bit) in VanillaPairs)
        {
            Assert.Equal(bit, OptimumChunkVisibilityGrid.PairBit[a * 6 + b]);
            Assert.Equal(bit, OptimumChunkVisibilityGrid.PairBit[b * 6 + a]);
        }
        for (int f = 0; f < 6; f++)
        {
            Assert.Equal(-1, OptimumChunkVisibilityGrid.PairBit[f * 6 + f]);
        }
    }

    [Fact]
    public void BlockFacingIndexOrder_MatchesTheGridFaceOrder()
    {
        // The grid hard-codes N=0, E=1, S=2, W=3, U=4, D=5 and the matching normals.
        BlockFacing[] faces = BlockFacing.ALLFACES;
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, faces.Select(f => f.Index).ToArray());
        int[] dx = { 0, 1, 0, -1, 0, 0 };
        int[] dy = { 0, 0, 0, 0, 1, -1 };
        int[] dz = { -1, 0, 1, 0, 0, 0 };
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal((dx[i], dy[i], dz[i]), (faces[i].Normali.X, faces[i].Normali.Y, faces[i].Normali.Z));
        }
    }

    [Fact]
    public void EncodeCell_StaleTraversabilityNeverBlocks()
    {
        Assert.Equal((ushort)(OptimumChunkVisibilityGrid.LoadedBit | OptimumChunkVisibilityGrid.AllPass),
            OptimumChunkVisibilityGrid.EncodeCell(0, fresh: false));
        Assert.Equal((ushort)(OptimumChunkVisibilityGrid.LoadedBit | 0b101),
            OptimumChunkVisibilityGrid.EncodeCell(0b101, fresh: true));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    public void MonotonicWalk_MarksEverythingTheVanillaRaysMark(int seed)
    {
        // Vanilla rays advance along positive-dot faces only, so their path is monotone per
        // axis; the monotonic walk records every entry face, so it must reach every loaded
        // cell a ray reaches without clipping to a smaller area.
        // Most face pairs pass, so the rays travel far and the walk has to reproduce long paths;
        // with sparse passage the rays die near the camera and any walk covers them.
        World w = RandomWorld(seed, 30, 5, 30, 0.97, 0.93, 0.05);
        const int viewDistance = 192;
        var rng = new Random(seed * 31);
        for (int trial = 0; trial < 8; trial++)
        {
            // Some cameras above the world exercise the vanilla above-height rule.
            int camY = trial % 4 == 3 ? w.MapY + rng.Next(0, 3) : rng.Next(0, w.MapY);
            var cam = (rng.Next(0, w.MapX), camY, rng.Next(0, w.MapZ));
            HashSet<(int, int, int)> rays = VanillaRays(w, cam, viewDistance);
            Assert.True(rays.Count > 150, $"seed {seed} trial {trial}: vanilla rays marked only {rays.Count} chunks, the test would be vacuous");
            HashSet<(int, int, int)> mono = RunGrid(w, cam, viewDistance);

            foreach (var pos in rays)
            {
                Assert.True(mono.Contains(pos), $"seed {seed} trial {trial} cam {cam}: ray-visible {pos} missing from the walk");
            }
        }
    }

    [Fact]
    public void CameraAboveTheWorld_StillReachesTheTerrainBelow()
    {
        // Vanilla keeps invalid positions walkable above the world so rays descend into it.
        World w = RandomWorld(31, 20, 4, 20, loadedChance: 1.0, passChance: 1.0, staleChance: 0.0);
        var cam = (10, w.MapY + 3, 10);

        HashSet<(int, int, int)> visible = RunGrid(w, cam, 160);

        Assert.Contains((10, w.MapY - 1, 10), visible);
    }

    [Fact]
    public void FullyOpenWorld_MarksEveryLoadedChunkInTheArea()
    {
        World w = RandomWorld(41, 24, 4, 24, 1.0, 1.0, 0.0);
        var cam = (12, 2, 12);
        HashSet<(int, int, int)> mono = RunGrid(w, cam, 128);

        int r = 128 / 32 + 4;
        foreach (var pos in w.Chunks.Keys)
        {
            if (Math.Abs(pos.X - cam.Item1) <= r && Math.Abs(pos.Z - cam.Item3) <= r)
            {
                Assert.Contains(pos, mono);
            }
        }
    }

    [Fact]
    public void FullyOpaqueNeighbour_BeyondTheFirstRingBlocksWhatIsBehind()
    {
        // A wall two chunks from the camera: the first ring is exempt from the traversal
        // test (vanilla distance 0-1 rule), so the wall at distance 2 is marked itself but
        // cannot be exited, so the chunk behind it stays hidden in a single-column world.
        var w = new World { MapX = 8, MapY = 1, MapZ = 8 };
        for (int z = 0; z < 8; z++)
        {
            w.Chunks[(4, 0, z)] = OptimumChunkVisibilityGrid.EncodeCell(0x7FFF, true);
        }
        w.Chunks[(4, 0, 6)] = OptimumChunkVisibilityGrid.EncodeCell(0, true); // opaque wall
        var cam = (4, 0, 4);

        HashSet<(int, int, int)> mono = RunGrid(w, cam, 128);

        Assert.Contains((4, 0, 5), mono);
        Assert.Contains((4, 0, 6), mono);
        Assert.DoesNotContain((4, 0, 7), mono);
    }

    [Fact]
    public void OcclusionOff_MarksEveryReachableLoadedChunk()
    {
        World w = RandomWorld(51, 24, 4, 24, 0.9, 0.0, 0.0); // nothing passes
        var cam = (12, 2, 12);
        HashSet<(int, int, int)> off = RunGrid(w, cam, 128, occlusion: false);
        HashSet<(int, int, int)> on = RunGrid(w, cam, 128, occlusion: true);
        Assert.True(on.IsSubsetOf(off));
        Assert.True(off.Count > on.Count);
    }

    [Fact]
    public void Prepare_ReusesArraysForTheSameSize_AndClearsTheSnapshot()
    {
        var grid = new OptimumChunkVisibilityGrid();
        grid.Prepare(6, 4);
        ushort[] cells = grid.Cells;
        int[] queue = grid.Queue;
        cells[5] = 123;

        grid.Prepare(6, 4);

        Assert.Same(cells, grid.Cells);
        Assert.Same(queue, grid.Queue);
        Assert.Equal(0, grid.Cells[5]);
    }

    [Fact]
    public void Prepare_GrowsWithoutTearingDimensions()
    {
        var grid = new OptimumChunkVisibilityGrid();
        grid.Prepare(3, 3);
        grid.Prepare(9, 5);
        Assert.Equal(19, grid.Width);
        Assert.Equal(11, grid.Height);
        Assert.Equal(19 * 19 * 11, grid.CellCount);
        Assert.True(grid.Cells.Length >= grid.CellCount);
        Assert.True(grid.Queue.Length >= grid.CellCount);

        grid.Prepare(3, 3); // shrinking keeps the larger arrays and updates the dimensions
        Assert.Equal(7, grid.Width);
        Assert.Equal(7 * 7 * 7, grid.CellCount);
    }

    [Fact]
    public void EachCellIsEnqueuedAtMostOnce()
    {
        World w = RandomWorld(61, 24, 4, 24, 1.0, 0.8, 0.0);
        var grid = new OptimumChunkVisibilityGrid();
        grid.Prepare(8, 6);
        foreach (var kv in w.Chunks)
        {
            int lx = kv.Key.X - 12 + 8;
            int ly = kv.Key.Y - 2 + 6;
            int lz = kv.Key.Z - 12 + 8;
            if ((uint)lx < (uint)grid.Width && (uint)lz < (uint)grid.Width && (uint)ly < (uint)grid.Height)
            {
                grid.Cells[grid.IndexOf(lx, ly, lz)] = kv.Value;
            }
        }
        grid.Run(12, 2, 12, w.MapX, w.MapY, w.MapZ, true);
        var seen = new HashSet<int>();
        for (int i = 0; i < grid.QueueLength; i++)
        {
            Assert.True(seen.Add(grid.Queue[i]));
        }
        Assert.True(grid.QueueLength <= grid.CellCount);
    }

    [Fact]
    public void Run_BeforePrepare_MarksNothingInsteadOfOverflowing()
    {
        var grid = new OptimumChunkVisibilityGrid();
        grid.Run(5, 2, 5, 10, 4, 10, true);
        Assert.Equal(0, grid.QueueLength);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(12)]
    public void CameraAboveTheWorld_ReachesTheTerrainOrStopsCleanly(int heightAboveWorld)
    {
        // The grid reaches mapY + 2 chunks vertically; a camera higher than the grid can see
        // down to the world must end with an empty or partial set, never an exception.
        World w = RandomWorld(71, 20, 4, 20, 1.0, 1.0, 0.0);
        var cam = (10, w.MapY - 1 + heightAboveWorld, 10);
        HashSet<(int, int, int)> visible = RunGrid(w, cam, 160);
        // The grid reaches MapY + 2 chunks below the camera, the same budget as the vanilla rays
        // (shell +/- MapY plus the 2 chunk march overshoot).
        bool canSeeDown = heightAboveWorld <= w.MapY + 2;
        Assert.Equal(canSeeDown, visible.Contains((10, w.MapY - 1, 10)));
    }

    [Fact]
    public void ReachedCellAndSetCell_MatchTheGridLayout()
    {
        var grid = new OptimumChunkVisibilityGrid();
        grid.Prepare(3, 2);
        int idx = grid.IndexOf(3, 2, 3); // the camera cell
        grid.SetCell(idx, OptimumChunkVisibilityGrid.EncodeCell(0x7FFF, true));
        grid.Run(10, 1, 10, 30, 4, 30, true);
        Assert.True(grid.QueueLength > 0);
        Assert.Equal(idx, grid.ReachedCell(0));
    }

    // ---- throttle ----

    private static bool Throttle(bool sameChunk, int regen, int last, ref bool pending, long now, long lastMs)
        => OptimumChunkVisibilityGrid.ShouldRecalculate(sameChunk, regen, last, ref pending, now, lastMs, 150, 1000, 2000, 10);

    [Fact]
    public void Throttle_NamedConstantsOverload_UsesTheDocumentedValues()
    {
        Assert.Equal(150, OptimumChunkVisibilityGrid.RecalcCooldownMs);
        Assert.Equal(1000, OptimumChunkVisibilityGrid.BigBacklogCooldownMs);
        Assert.Equal(2000, OptimumChunkVisibilityGrid.BigBacklogThreshold);
        Assert.Equal(10, OptimumChunkVisibilityGrid.SignificantQueueDelta);
        bool pending = false;
        Assert.False(OptimumChunkVisibilityGrid.ShouldRecalculate(true, 100, 120, ref pending, 1_000, 900));
        Assert.True(OptimumChunkVisibilityGrid.ShouldRecalculate(true, 100, 120, ref pending, 1_060, 900));
    }

    [Fact]
    public void Throttle_CameraChunkChangeRecalculatesImmediately_AndClearsPending()
    {
        bool pending = true;
        Assert.True(Throttle(false, 5, 5, ref pending, now: 100, lastMs: 99));
        Assert.False(pending);
    }

    [Fact]
    public void Throttle_SameChunkWithoutAQueueChangeDoesNothing()
    {
        bool pending = false;
        Assert.False(Throttle(true, 100, 105, ref pending, 10_000, 0));
        Assert.False(pending);
    }

    [Fact]
    public void Throttle_SignificantChangeWaitsForTheCooldown_ThenFiresOnce()
    {
        bool pending = false;
        Assert.False(Throttle(true, 100, 120, ref pending, now: 1_000, lastMs: 900)); // 100 ms < 150 ms
        Assert.True(pending);
        Assert.True(Throttle(true, 100, 120, ref pending, now: 1_060, lastMs: 900));  // 160 ms
        Assert.False(pending);
        Assert.False(Throttle(true, 100, 100, ref pending, now: 5_000, lastMs: 1_060));
    }

    [Fact]
    public void Throttle_PendingSurvivesTheQueueReturningToItsOldSize()
    {
        bool pending = false;
        Assert.False(Throttle(true, 100, 150, ref pending, 1_000, 950)); // big change, cooling down
        Assert.True(pending);
        Assert.True(Throttle(true, 150, 150, ref pending, 1_200, 950));  // queue back at 150, still fires
    }

    [Fact]
    public void Throttle_QueueDrainingToZeroCountsAsSignificant()
    {
        bool pending = false;
        Assert.True(Throttle(true, 0, 3, ref pending, 10_000, 0));
    }

    [Fact]
    public void Throttle_LargeBacklogUsesTheLongCooldown()
    {
        bool pending = false;
        Assert.False(Throttle(true, 3_000, 2_500, ref pending, now: 1_500, lastMs: 1_000)); // 500 ms < 1 s
        Assert.True(pending);
        Assert.True(Throttle(true, 3_000, 2_500, ref pending, now: 2_100, lastMs: 1_000));
    }

    // ---- extra dimensions (#764) ----

    [Theory]
    [InlineData(128.5, 0, 4)]                  // normal dim 0
    [InlineData(-64.0, 0, -2)]                 // below world floor in dim 0
    [InlineData(500.0, 0, 15)]                 // above world height in dim 0
    [InlineData(2 * 32768 + 128.5, 2, 2052)]   // normal extra dim 2
    [InlineData(2 * 32768 - 64.0, 2, 2046)]    // below floor in extra dim 2 (localY = -2)
    [InlineData(2 * 32768 + 500.0, 2, 2063)]   // above world height in extra dim 2
    [InlineData(128.5, 2, 2052)]               // torn CameraPos.Y (missing DimensionYAdjustment) in dim 2
    [InlineData(-64.0, 2, 2046)]               // torn CameraPos.Y below floor in dim 2
    [InlineData(2 * 32768 + 128.5, 0, 4)]      // stale dim 2 CameraPos.Y right after switching to dim 0
    [InlineData(2 * 32768 + 128.5, 3, 3076)]   // stale dim 2 CameraPos.Y right after switching to dim 3
    [InlineData(3 * 32768 + 128.5, 2, 2052)]   // stale dim 3 CameraPos.Y right after switching to dim 2
    [InlineData(128.5, -1, 4)]                 // defensive clamp for negative dimension
    public void ResolveCameraChunkY_ReconcilesDimensionOffsetAndTornReads(
        double cameraY, int currentDim, int expectedInternalChunkY)
    {
        Assert.Equal(expectedInternalChunkY, OptimumChunkVisibilityGrid.ResolveCameraChunkY(cameraY, currentDim));
    }

    [Fact]
    public void ExtraDimension_LocalCamYEnforcesFloorBounds_PreventingUnderWorldWallBypass()
    {
        // Issue #764: passing raw internal chunkY (e.g. 2052) into grid.Run made aboveWorld=true
        // because 2052 >= mapSizeY (8), letting the walk leak below localY=0 through unloaded cells
        // and bypass a full-height opaque wall. Passing dimension-local camY (2052 - 2048 = 4)
        // keeps aboveWorld=false and enforces [0, mapSizeY).
        var w = new World { MapX = 12, MapY = 8, MapZ = 12 };
        for (int x = 0; x < w.MapX; x++)
        {
            for (int y = 0; y < w.MapY; y++)
            {
                for (int z = 0; z < w.MapZ; z++)
                {
                    bool wall = x == 6;
                    w.Chunks[(x, y, z)] = OptimumChunkVisibilityGrid.EncodeCell(
                        wall ? (ushort)0 : OptimumChunkVisibilityGrid.AllPass, fresh: true);
                }
            }
        }

        const int dim = 2;
        int dimOffset = dim * OptimumChunkVisibilityGrid.ChunksPerDimension;
        int camInternalY = OptimumChunkVisibilityGrid.ResolveCameraChunkY(dim * 32768 + 4 * 32 + 16, dim);
        int localCamY = camInternalY - dimOffset;

        var visible = RunGrid(w, (4, localCamY, 5), viewDistance: 256, occlusion: true);
        Assert.Contains((4, 4, 5), visible);
        Assert.Contains((2, 4, 5), visible);
        Assert.Contains((6, 4, 5), visible);      // wall itself is visible
        Assert.DoesNotContain((8, 4, 5), visible); // behind full-height wall stays occluded
    }

    // ---- models ----

    /// <summary>Port of ChunkCuller.traverseRayAndMarkVisible / getExitingFace from the 1.22.7 decompile.</summary>
    private static HashSet<(int, int, int)> VanillaRays(World w, (int X, int Y, int Z) cam, int viewDistance)
    {
        int mapY = w.MapY;
        bool above = cam.Y >= mapY;
        int shellRadius = viewDistance / 32 + 1;

        var shell = new HashSet<(int, int, int)>();
        foreach (Vec2i p in ShapeUtil.GetOctagonPoints(0, 0, shellRadius))
        {
            for (int j = -mapY; j <= mapY; j++)
            {
                shell.Add((p.X, j, p.Y));
            }
        }
        for (int k = 0; k < shellRadius; k++)
        {
            foreach (Vec2i p in ShapeUtil.GetOctagonPoints(0, 0, k))
            {
                shell.Add((p.X, -mapY, p.Y));
                shell.Add((p.X, mapY, p.Y));
            }
        }

        var visible = new HashSet<(int, int, int)>();
        foreach (var rel in shell)
        {
            Ray(w, cam, rel, 0.25, 0.5, above, visible);
            Ray(w, cam, rel, 0.75, 0.5, above, visible);
            Ray(w, cam, rel, 0.75, 0.0, above, visible);
        }
        return visible;
    }

    private static void Ray(World w, (int X, int Y, int Z) from, (int X, int Y, int Z) rel,
        double yoff, double xoff, bool above, HashSet<(int, int, int)> visible)
    {
        double ox = from.X + xoff, oy = from.Y + yoff, oz = from.Z + 0.5;
        double dx = rel.X + xoff, dy = rel.Y + yoff, dz = rel.Z + 0.5;
        var to = (X: from.X + rel.X, Y: from.Y + rel.Y, Z: from.Z + rel.Z);
        int total = Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) + Math.Abs(to.Z - from.Z);
        var cur = from;
        int entry = -1;
        int dist;
        while ((dist = Math.Abs(cur.X - from.X) + Math.Abs(cur.Y - from.Y) + Math.Abs(cur.Z - from.Z)) <= total + 2)
        {
            int exit = ExitingFace(cur, ox, oy, oz, dx, dy, dz);
            if (exit < 0)
            {
                break;
            }
            if (w.Chunks.TryGetValue(cur, out ushort cell))
            {
                visible.Add(cur);
                if (dist > 1 && entry >= 0 && cell != (ushort)(OptimumChunkVisibilityGrid.LoadedBit | OptimumChunkVisibilityGrid.AllPass)
                    && ((cell >> OptimumChunkVisibilityGrid.PairBit[entry * 6 + exit]) & 1) == 0)
                {
                    break;
                }
            }
            int[] fdx = { 0, 1, 0, -1, 0, 0 };
            int[] fdy = { 0, 0, 0, 0, 1, -1 };
            int[] fdz = { -1, 0, 1, 0, 0, 0 };
            int[] opp = { 2, 3, 0, 1, 5, 4 };
            cur = (cur.X + fdx[exit], cur.Y + fdy[exit], cur.Z + fdz[exit]);
            entry = opp[exit];
            if (!Valid(w, cur.X, cur.Y, cur.Z) && (!above || cur.Y <= 0))
            {
                break;
            }
        }
    }

    private static int ExitingFace((int X, int Y, int Z) pos, double ox, double oy, double oz, double dx, double dy, double dz)
    {
        int[] nx = { 0, 1, 0, -1, 0, 0 };
        int[] ny = { 0, 0, 0, 0, 1, -1 };
        int[] nz = { -1, 0, 1, 0, 0, 0 };
        for (int i = 0; i < 6; i++)
        {
            double dot = nx[i] * dx + ny[i] * dy + nz[i] * dz;
            if (dot <= 1E-05)
            {
                continue;
            }
            // Vanilla: the chunk occupies [pos, pos + 1] on every axis and PlaneCenter is the face centre.
            Vec3f center = BlockFacing.ALLFACES[i].PlaneCenter;
            double px = pos.X + center.X, py = pos.Y + center.Y, pz = pos.Z + center.Z;
            double t = ((px - ox) * nx[i] + (py - oy) * ny[i] + (pz - oz) * nz[i]) / dot;
            if (t >= 0.0 && Math.Abs(ox + dx * t - px) <= 0.5 && Math.Abs(oy + dy * t - py) <= 0.5 && Math.Abs(oz + dz * t - pz) <= 0.5)
            {
                return i;
            }
        }
        return -1;
    }
}
