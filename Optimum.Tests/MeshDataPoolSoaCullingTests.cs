using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Tests;

public sealed class MeshDataPoolSoaCullingTests
{
    private readonly ITestOutputHelper output;

    public MeshDataPoolSoaCullingTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerPos")]
    private static extern ref BlockPos GetPlayerPos(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustum")]
    private static extern ref Plane[] GetFrustum(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    private static extern ref List<ModelDataPoolLocation> GetPoolLocations(MeshDataPool pool);

    private static MeshDataPool CreateTestPool(int maxParts = 900)
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
        pool.cullData = new PoolCullData(maxParts);
        return pool;
    }

    [Fact]
    public void SoA_SentinelRadii_RejectsUnallocatedSlotsAndPreservesCapacityAlignment()
    {
        var cullData = new PoolCullData(100);

        // Capacity must align to multiples of 64
        Assert.True(cullData.Capacity >= 100);
        Assert.Equal(0, cullData.Capacity % PoolCullData.CapacityStep);

        // Populate 10 active slots
        for (int i = 0; i < 10; i++)
        {
            cullData.SetSlot(i, i * 32, 100, i * 32, 16, 16, 16, i * 128, 96);
        }

        // Slots beyond 10 must have sentinel negative radius
        for (int i = 10; i < cullData.Capacity; i++)
        {
            Assert.True(cullData.RadiusX[i] <= PoolCullData.FreeSlotSentinelRadius);
        }

        // Test culling: planes that enclose the whole world
        Plane[] planes = new Plane[6];
        planes[0] = new Plane(1, 0, 0, 10000);
        planes[1] = new Plane(-1, 0, 0, 10000);
        planes[2] = new Plane(0, 1, 0, 10000);
        planes[3] = new Plane(0, -1, 0, 10000);
        planes[4] = new Plane(0, 0, 1, 10000);
        planes[5] = new Plane(0, 0, -1, 10000);

        int candidateCount = cullData.CullPlanesSimd(planes, 5, 10);
        Assert.Equal(10, candidateCount);

        for (int i = 0; i < candidateCount; i++)
        {
            Assert.Equal(i, cullData.CandidateIndices[i]);
        }
    }

    [Fact]
    public void SoA_SimdVsScalar_BitExactParityAcrossPlanes()
    {
        const int chunkCount = 400;
        var cullData = new PoolCullData(chunkCount);

        Random rnd = new(42);
        for (int i = 0; i < chunkCount; i++)
        {
            float x = (float)(rnd.NextDouble() * 1000 - 500);
            float y = (float)(rnd.NextDouble() * 256);
            float z = (float)(rnd.NextDouble() * 1000 - 500);
            cullData.SetSlot(i, x, y, z, 16f, 16f, 16f, i * 120, 90);
        }

        // Typical camera frustum planes
        Plane[] planes = new Plane[6];
        planes[0] = new Plane(0.7071, 0, 0.7071, 50);    // Left
        planes[1] = new Plane(-0.7071, 0, 0.7071, 50);   // Right
        planes[2] = new Plane(0, 0.7071, 0.7071, 50);    // Bottom
        planes[3] = new Plane(0, -0.7071, 0.7071, 50);   // Top
        planes[4] = new Plane(0, 0, 1, 5);               // Near
        planes[5] = new Plane(0, 0, -1, 800);            // Far

        // Run scalar culling
        ushort[] scalarCandidates = new ushort[cullData.Capacity];
        int scalarCount = cullData.CullPlanesScalar(planes, 5, chunkCount);
        Array.Copy(cullData.CandidateIndices, scalarCandidates, scalarCount);

        // Run SIMD AVX2 culling
        int avxCount = cullData.CullPlanesAvx2(planes, 5, chunkCount);
        ushort[] avxCandidates = new ushort[avxCount];
        Array.Copy(cullData.CandidateIndices, avxCandidates, avxCount);

        // Run SIMD Vector128 culling
        int v128Count = cullData.CullPlanesVector128(planes, 5, chunkCount);
        ushort[] v128Candidates = new ushort[v128Count];
        Array.Copy(cullData.CandidateIndices, v128Candidates, v128Count);

        // Bit-exact parity check
        Assert.Equal(scalarCount, avxCount);
        Assert.Equal(scalarCount, v128Count);

        for (int i = 0; i < scalarCount; i++)
        {
            Assert.Equal(scalarCandidates[i], avxCandidates[i]);
            Assert.Equal(scalarCandidates[i], v128Candidates[i]);
        }
    }

    [Fact]
    public void SoA_FrustumCull_ZeroAllocationsInHotPath()
    {
        var pool = CreateTestPool(400);
        var culler = new FrustumCulling();
        GetPlayerPos(culler) = new BlockPos(0, 100, 0);

        culler.CalcFrustumEquations(
            new BlockPos(0, 100, 0),
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
        );

        for (int i = 0; i < 400; i++)
        {
            var loc = new ModelDataPoolLocation
            {
                PoolId = 1,
                IndicesStart = i * 30,
                IndicesEnd = (i + 1) * 30,
                VerticesStart = i * 20,
                VerticesEnd = (i + 1) * 20,
                FrustumCullSphere = new Sphere { x = i % 20 * 32 - 320, y = 100, z = (i / 20) * 32 - 320, radius = 16, radiusY = 16, radiusZ = 16 },
                CullVisible = new Bools(true, true),
                LodLevel = 1
            };
            GetPoolLocations(pool).Add(loc);
            pool.cullData.InsertSlot(i, i, loc.FrustumCullSphere.x, loc.FrustumCullSphere.y, loc.FrustumCullSphere.z, 16, 16, 16, loc.IndicesStart * 4, 30);
        }
        pool.RecomputeTightBounds();

        // Warm up JIT and static caches
        for (int w = 0; w < 10; w++)
        {
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
        }

        // Measure allocations across 100 consecutive culling passes
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 100; frame++)
        {
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
        }
        long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocStart;

        Assert.Equal(0, allocDelta);
    }

    [Fact]
    public void SoA_Versus_AoS_ComprehensivePerformanceBenchmark()
    {
        var culler = new FrustumCulling();
        GetPlayerPos(culler) = new BlockPos(0, 100, 0);
        culler.CalcFrustumEquations(
            new BlockPos(0, 100, 0),
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
        );

        // Distance scenarios: (distanceM, pools, chunksPerPool)
        (int distanceM, int poolCount, int chunksPerPool)[] scenarios =
        [
            (256, 6, 400),
            (512, 20, 388),
            (768, 41, 395),
            (1024, 70, 396),
            (1536, 150, 400)
        ];

        output.WriteLine("==========================================================================================================");
        output.WriteLine(" OPTIMUM STRUCTURE-OF-ARRAYS (SOA) SIMD CHUNK CULLING BENCHMARK RESULTS");
        output.WriteLine("==========================================================================================================");
        output.WriteLine(string.Format("{0,-12} | {1,-8} | {2,-8} | {3,-16} | {4,-16} | {5,-10} | {6,-16}",
            "Distance", "Pools", "Chunks", "AoS Scalar (ms)", "SoA SIMD (ms)", "Speedup", "Triangles Drawn"));
        output.WriteLine("----------------------------------------------------------------------------------------------------------");

        const int iterations = 50;

        foreach (var (distanceM, poolCount, chunksPerPool) in scenarios)
        {
            int totalChunks = poolCount * chunksPerPool;
            List<MeshDataPool> pools = new(poolCount);

            Random rnd = new(1337);
            for (int p = 0; p < poolCount; p++)
            {
                var pool = CreateTestPool(chunksPerPool);
                float poolOriginX = (p % 10) * 150 - 750;
                float poolOriginZ = (p / 10) * 150 - 750;

                for (int c = 0; c < chunksPerPool; c++)
                {
                    float cx = poolOriginX + (c % 20) * 32;
                    float cy = 100 + (float)(rnd.NextDouble() * 30);
                    float cz = poolOriginZ + (c / 20) * 32;

                    var loc = new ModelDataPoolLocation
                    {
                        PoolId = p,
                        IndicesStart = c * 60,
                        IndicesEnd = (c + 1) * 60,
                        VerticesStart = c * 40,
                        VerticesEnd = (c + 1) * 40,
                        FrustumCullSphere = new Sphere { x = cx, y = cy, z = cz, radius = 16, radiusY = 16, radiusZ = 16 },
                        CullVisible = new Bools(true, true),
                        LodLevel = 1
                    };
                    GetPoolLocations(pool).Add(loc);
                    pool.cullData.InsertSlot(c, c, cx, cy, cz, 16, 16, 16, loc.IndicesStart * 4, 60);
                }
                pool.RecomputeTightBounds();
                pools.Add(pool);
            }

            // Warmup
            foreach (var pool in pools)
            {
                pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            }

            // Benchmark AoS (scalar emulation over poolLocations directly)
            var swAos = Stopwatch.StartNew();
            int aosTriangles = 0;
            for (int it = 0; it < iterations; it++)
            {
                Plane[] planes = GetFrustum(culler);
                for (int p = 0; p < pools.Count; p++)
                {
                    var pool = pools[p];
                    var locs = GetPoolLocations(pool);
                    int locCount = locs.Count;
                    int groups = 0;
                    int renderedTris = 0;
                    for (int i = 0; i < locCount; i++)
                    {
                        var loc = locs[i];
                        if (loc.Hide || !loc.CullVisible[0]) continue;
                        bool outside = false;
                        for (int pl = 0; pl < 5; pl++)
                        {
                            if (planes[pl].AABBisOutside(loc.FrustumCullSphere))
                            {
                                outside = true;
                                break;
                            }
                        }
                        if (!outside && culler.InFrustumAndRange(loc.FrustumCullSphere, false, loc.LodLevel))
                        {
                            renderedTris += (loc.IndicesEnd - loc.IndicesStart) / 3;
                            groups++;
                        }
                    }
                    if (it == iterations - 1) aosTriangles += renderedTris;
                }
            }
            swAos.Stop();
            double aosMs = (double)swAos.ElapsedTicks / Stopwatch.Frequency * 1000.0 / iterations;

            // Benchmark SoA SIMD (via MeshDataPool.FrustumCull)
            var swSoa = Stopwatch.StartNew();
            int soaTriangles = 0;
            for (int it = 0; it < iterations; it++)
            {
                for (int p = 0; p < pools.Count; p++)
                {
                    pools[p].FrustumCull(culler, EnumFrustumCullMode.CullNormal);
                    if (it == iterations - 1) soaTriangles += pools[p].RenderedTriangles;
                }
            }
            swSoa.Stop();
            double soaMs = (double)swSoa.ElapsedTicks / Stopwatch.Frequency * 1000.0 / iterations;

            // Assert bit-exact triangle parity between AoS and SoA
            Assert.Equal(aosTriangles, soaTriangles);

            double speedup = aosMs / Math.Max(0.001, soaMs);

            output.WriteLine(string.Format("{0,-12} | {1,-8} | {2,-8} | {3,-16:F3} | {4,-16:F3} | {5,-10:F2}x | {6,-16:N0}",
                $"{distanceM}m", poolCount, totalChunks, aosMs, soaMs, speedup, soaTriangles));
        }

        output.WriteLine("==========================================================================================================");
    }
}
