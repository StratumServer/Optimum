using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public class PoolBoundsAabbCullingTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerPos")]
    private static extern ref BlockPos GetPlayerPos(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    private static extern ref List<ModelDataPoolLocation> GetPoolLocations(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolId")]
    private static extern ref int GetPoolId(MeshDataPool pool);

    private static MeshDataPool CreateTestPool(int maxParts = 100)
    {
        var pool = (MeshDataPool)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPool));
        pool.MaxPartsPerPool = maxParts;
        pool.indicesStartsByte = new int[maxParts * 2];
        pool.indicesSizes = new int[maxParts];
        GetPoolLocations(pool) = new List<ModelDataPoolLocation>();
        pool.BoundMinX = float.MaxValue;
        pool.BoundMinY = float.MaxValue;
        pool.BoundMinZ = float.MaxValue;
        pool.BoundMaxX = float.MinValue;
        pool.BoundMaxY = float.MinValue;
        pool.BoundMaxZ = float.MinValue;
        pool.BoundsValid = false;
        return pool;
    }

    [Fact]
    public void Plane_IsBoxOutside_CorrectlyIdentifiesPointsOutsideAndInside()
    {
        // Plane facing +Z at z = 100 (normal = (0, 0, 1), D = -100)
        var plane = new Plane(0, 0, 1, -100);

        // Box entirely behind plane (z from 10 to 50): should be outside (distance < 0)
        bool outside = plane.IsBoxOutside(0, 0, 10, 50, 50, 50);
        Assert.True(outside, "Box behind plane must be reported outside.");

        // Box crossing or in front of plane (z from 80 to 120): should be inside
        bool crossing = plane.IsBoxOutside(0, 0, 80, 50, 50, 120);
        Assert.False(crossing, "Box crossing plane must not be reported outside.");

        // Box entirely in front of plane (z from 110 to 200): should be inside
        bool inside = plane.IsBoxOutside(0, 0, 110, 50, 50, 200);
        Assert.False(inside, "Box in front of plane must not be reported outside.");
    }

    [Fact]
    public void FrustumCulling_BoxInShadowPass_RejectsOffVolumeBoxes()
    {
        var culler = new FrustumCulling();
        GetPlayerPos(culler) = new BlockPos(1000, 100, 1000);
        culler.shadowRangeX = 80;
        culler.shadowRangeZ = 80;

        // Box within shadow range (player at 1000, box at 1010..1040): inside
        Assert.True(culler.BoxInShadowPass(1010, 1010, 1040, 1040));

        // Box outside shadow range on X (box at 1100..1130, dx = 100..130 >= 80): rejected
        Assert.False(culler.BoxInShadowPass(1100, 1010, 1130, 1040));

        // Box outside shadow range on Z (box at 1010..880, dz <= -80): rejected
        Assert.False(culler.BoxInShadowPass(1010, 880, 1040, 910));
    }

    [Fact]
    public void FrustumCulling_BoxInRange_RejectsBoxesBeyondViewDistance()
    {
        var culler = new FrustumCulling();
        GetPlayerPos(culler) = new BlockPos(0, 0, 0);
        culler.UpdateViewDistance(256);

        // Box within 256m: inside
        Assert.True(culler.BoxInRange(50, 50, 80, 80));

        // Box entirely beyond 256m (nearest point at 300, 300 -> distanceSq = 180000 > 256^2): rejected
        Assert.False(culler.BoxInRange(300, 300, 350, 350));
    }

    [Fact]
    public void MeshDataPool_ExpandsAndTightensBoundsAcrossInsertAndRemove()
    {
        var pool = CreateTestPool();

        Assert.False(pool.BoundsValid);
        Assert.Equal(float.MaxValue, pool.BoundMinX);
        Assert.Equal(float.MinValue, pool.BoundMaxX);

        var sphere1 = new Sphere { x = 100, y = 50, z = 100, radius = 16, radiusY = 16, radiusZ = 16 };
        int poolId = GetPoolId(pool);
        var loc1 = new ModelDataPoolLocation
        {
            FrustumCullSphere = sphere1,
            IndicesStart = 0,
            IndicesEnd = 60,
            VerticesStart = 0,
            VerticesEnd = 24,
            PoolId = poolId
        };
        GetPoolLocations(pool).Add(loc1);
        pool.RecomputeTightBounds();

        Assert.True(pool.BoundsValid);
        Assert.Equal(84f, pool.BoundMinX);
        Assert.Equal(116f, pool.BoundMaxX);

        var sphere2 = new Sphere { x = 200, y = 50, z = 200, radius = 16, radiusY = 16, radiusZ = 16 };
        var loc2 = new ModelDataPoolLocation
        {
            FrustumCullSphere = sphere2,
            IndicesStart = 60,
            IndicesEnd = 120,
            VerticesStart = 24,
            VerticesEnd = 48,
            PoolId = poolId
        };
        GetPoolLocations(pool).Add(loc2);
        pool.RecomputeTightBounds();

        Assert.Equal(84f, pool.BoundMinX);
        Assert.Equal(216f, pool.BoundMaxX);

        // Remove loc2 and recompute: bounds should tighten back to sphere1
        pool.RemoveLocation(loc2);
        Assert.True(pool.BoundsValid);
        Assert.Equal(84f, pool.BoundMinX);
        Assert.Equal(116f, pool.BoundMaxX);

        // Remove loc1: pool becomes empty and bounds reset
        pool.RemoveLocation(loc1);
        Assert.False(pool.BoundsValid);
        Assert.Equal(float.MaxValue, pool.BoundMinX);
        Assert.Equal(float.MinValue, pool.BoundMaxX);
    }

    [Fact]
    public void MeshDataPool_FrustumCull_EarlyRejectsOffShadowPools()
    {
        var pool = CreateTestPool();
        var sphere = new Sphere { x = 2000, y = 100, z = 2000, radius = 16, radiusY = 16, radiusZ = 16 };
        int poolId = GetPoolId(pool);
        var loc = new ModelDataPoolLocation
        {
            FrustumCullSphere = sphere,
            IndicesStart = 0,
            IndicesEnd = 60,
            VerticesStart = 0,
            VerticesEnd = 24,
            PoolId = poolId,
            CullVisible = new Bools(true, true),
            Hide = false
        };
        GetPoolLocations(pool).Add(loc);
        pool.RecomputeTightBounds();

        var culler = new FrustumCulling();
        GetPlayerPos(culler) = new BlockPos(1000, 100, 1000);
        culler.shadowRangeX = 80;
        culler.shadowRangeZ = 80;

        // Near shadow pass on pool at x=2000 (dx = 1000 >> 80): must early exit with 0 groups
        pool.FrustumCull(culler, EnumFrustumCullMode.CullInstantShadowPassNear);

        Assert.Equal(0, pool.indicesGroupsCount);
        Assert.Equal(0, pool.RenderedTriangles);
    }
}
