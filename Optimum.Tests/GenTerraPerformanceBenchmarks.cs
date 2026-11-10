using System;
using System.Collections;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Tests;

public sealed class GenTerraPerformanceBenchmarks
{
    private readonly ITestOutputHelper output;

    public GenTerraPerformanceBenchmarks(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void Phase1_FalseSharing_Versus_ThreadLocalBuffer_Benchmark()
    {
        const int mapHeight = 256;
        const int columnsPerChunk = 1024;
        const int chunkBatches = 100;
        int workerThreads = Environment.ProcessorCount;

        // Baseline: Shared bool arrays with False Sharing
        bool[] sharedSolid = new bool[mapHeight];
        bool[] sharedEmpty = new bool[mapHeight];

        var swBaseline = Stopwatch.StartNew();
        for (int batch = 0; batch < chunkBatches; batch++)
        {
            Array.Fill(sharedSolid, true);
            Array.Fill(sharedEmpty, true);

            Parallel.For(0, columnsPerChunk, new ParallelOptions { MaxDegreeOfParallelism = workerThreads }, col =>
            {
                int breakY = 110 + (col % 40);
                for (int y = 1; y < mapHeight - 2; y++)
                {
                    if (y < 80)
                    {
                        sharedEmpty[y] = false;
                    }
                    else if (y >= breakY)
                    {
                        sharedSolid[y] = false;
                        for (int yAbove = y + 1; yAbove < mapHeight - 2; yAbove++)
                        {
                            sharedSolid[yAbove] = false;
                        }
                        break;
                    }
                    else
                    {
                        sharedEmpty[y] = false;
                    }
                }
            });
        }
        swBaseline.Stop();
        long baselineMs = swBaseline.ElapsedMilliseconds;

        // Phase 1: Thread-Local Buffers & Lock-Free Merge (No False Sharing)
        ThreadLocal<(bool[] solid, bool[] empty)> tls = new(() => (new bool[mapHeight], new bool[mapHeight]));
        bool[] optSolid = new bool[mapHeight];
        bool[] optEmpty = new bool[mapHeight];

        var swOpt = Stopwatch.StartNew();
        for (int batch = 0; batch < chunkBatches; batch++)
        {
            Array.Fill(optSolid, true);
            Array.Fill(optEmpty, true);

            Parallel.For(0, columnsPerChunk, new ParallelOptions { MaxDegreeOfParallelism = workerThreads },
                () =>
                {
                    var data = tls.Value;
                    Array.Fill(data.solid, true);
                    Array.Fill(data.empty, true);
                    return data;
                },
                (col, loopState, data) =>
                {
                    int breakY = 110 + (col % 40);
                    for (int y = 1; y < mapHeight - 2; y++)
                    {
                        if (y < 80)
                        {
                            data.empty[y] = false;
                        }
                        else if (y >= breakY)
                        {
                            data.solid[y] = false;
                            for (int yAbove = y + 1; yAbove < mapHeight - 2; yAbove++)
                            {
                                data.solid[yAbove] = false;
                            }
                            break;
                        }
                        else
                        {
                            data.empty[y] = false;
                        }
                    }
                    return data;
                },
                data =>
                {
                    bool[] solid = optSolid;
                    bool[] empty = optEmpty;
                    bool[] localSolid = data.solid;
                    bool[] localEmpty = data.empty;
                    for (int i = 0; i < solid.Length; i++)
                    {
                        if (!localSolid[i]) solid[i] = false;
                        if (!localEmpty[i]) empty[i] = false;
                    }
                });
        }
        swOpt.Stop();
        long optMs = swOpt.ElapsedMilliseconds;

        // Parity Verification
        Assert.Equal(sharedSolid.Length, optSolid.Length);
        Assert.Equal(sharedEmpty.Length, optEmpty.Length);
        for (int i = 0; i < mapHeight; i++)
        {
            Assert.Equal(sharedSolid[i], optSolid[i]);
            Assert.Equal(sharedEmpty[i], optEmpty[i]);
        }

        double speedup = baselineMs > 0 ? (double)baselineMs / Math.Max(1, optMs) : 1.0;
        output.WriteLine($"[Benchmark Phase 1] Cores: {workerThreads}, Chunks: {chunkBatches} ({chunkBatches * columnsPerChunk} columns)");
        output.WriteLine($"  Baseline (Shared Arrays with False Sharing): {baselineMs} ms");
        output.WriteLine($"  Optimized (Thread-Local Buffers & Lock-Free Merge): {optMs} ms");
        output.WriteLine($"  Speedup: {speedup:F2}x (-{((baselineMs - optMs) * 100.0 / Math.Max(1, baselineMs)):F1}%)");
    }

    [Fact]
    public void Phase2_CompactLandforms_FmaThreshold_Benchmark()
    {
        const int landformCount = 24;
        const int mapHeight = 256;
        const int columns = 100_000;

        // Mock terrain thresholds for each landform
        float[][] terrainYThresholds = new float[landformCount][];
        Random rnd = new(4242);
        for (int i = 0; i < landformCount; i++)
        {
            terrainYThresholds[i] = new float[mapHeight];
            for (int y = 0; y < mapHeight; y++)
            {
                terrainYThresholds[i][y] = (float)rnd.NextDouble();
            }
        }

        // Mock sparse weights: only 3 active landforms
        float[] weights = new float[landformCount];
        weights[2] = 0.5f;
        weights[5] = 0.3f;
        weights[11] = 0.2f;

        float distY = 3.42f;
        int mapsizeYm2 = mapHeight - 2;

        // Baseline: Vanilla loop over all landforms with jagged array indexing
        float distortedPosYSlide = distY - (int)Math.Floor(distY);
        var swVanilla = Stopwatch.StartNew();
        double vanillaSum = 0;
        for (int c = 0; c < columns; c++)
        {
            for (int posY = 1; posY <= mapsizeYm2; posY++)
            {
                int distortedPosYBase = Math.Clamp((int)Math.Floor(posY + distY), 0, mapsizeYm2);
                double threshold = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    float weight = weights[i];
                    if (weight == 0) continue;
                    float lerp = GameMath.Lerp(terrainYThresholds[i][distortedPosYBase], terrainYThresholds[i][distortedPosYBase + 1], distortedPosYSlide);
                    threshold += weight * lerp;
                }
                vanillaSum += threshold;
            }
        }
        swVanilla.Stop();
        long vanillaMs = swVanilla.ElapsedMilliseconds;

        // Phase 2: Compact landforms with pre-multiplied weights and FMA
        float[] wA = new float[landformCount];
        float[] wB = new float[landformCount];
        float[][] ths = new float[landformCount][];

        var swCompact = Stopwatch.StartNew();
        double compactSum = 0;
        for (int c = 0; c < columns; c++)
        {
            int floorDistY = (int)Math.Floor(distY);
            float slide = distY - floorDistY;
            float invSlide = 1f - slide;

            int nzCount = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                float w = weights[i];
                if (w == 0) continue;
                wA[nzCount] = w * invSlide;
                wB[nzCount] = w * slide;
                ths[nzCount] = terrainYThresholds[i];
                nzCount++;
            }

            for (int posY = 1; posY <= mapsizeYm2; posY++)
            {
                int b = Math.Clamp((int)Math.Floor(posY + distY), 0, mapsizeYm2);
                double threshold = 0;
                for (int k = 0; k < nzCount; k++)
                {
                    float[] t = ths[k];
                    threshold += t[b] * wA[k] + t[b + 1] * wB[k];
                }
                compactSum += threshold;
            }
        }
        swCompact.Stop();
        long compactMs = swCompact.ElapsedMilliseconds;

        // Parity Verification: relative delta within floating-point epsilon (1e-6)
        double diff = Math.Abs(vanillaSum - compactSum);
        double relDiff = diff / Math.Max(1.0, Math.Abs(vanillaSum));
        Assert.True(relDiff < 1e-6, $"Relative diff {relDiff:E4} exceeded threshold");

        double speedup = vanillaMs > 0 ? (double)vanillaMs / Math.Max(1, compactMs) : 1.0;
        output.WriteLine($"[Benchmark Phase 2] Columns: {columns:N0}, Evaluated Y-cells: {columns * mapsizeYm2:N0}");
        output.WriteLine($"  Baseline (Vanilla 24-landform scan + Lerp): {vanillaMs} ms");
        output.WriteLine($"  Optimized (Compact K=3 Non-Zero + FMA): {compactMs} ms");
        output.WriteLine($"  Speedup: {speedup:F2}x (-{((vanillaMs - compactMs) * 100.0 / Math.Max(1, vanillaMs)):F1}%)");
    }

    [Fact]
    public void Phase3_BitPackedUlong_ZeroAllocation_Benchmark()
    {
        const int mapHeight = 256;
        const int columnsPerChunk = 1024;
        const int chunks = 1000;
        int words = (mapHeight + 63) / 64;

        // Baseline: Managed BitArray
        long startAllocManaged = GC.GetAllocatedBytesForCurrentThread();
        var swManaged = Stopwatch.StartNew();
        BitArray[] managedColumns = new BitArray[columnsPerChunk];
        for (int i = 0; i < columnsPerChunk; i++)
        {
            managedColumns[i] = new BitArray(mapHeight);
        }
        for (int c = 0; c < chunks; c++)
        {
            for (int col = 0; col < columnsPerChunk; col++)
            {
                BitArray b = managedColumns[col];
                b.SetAll(false);
                for (int y = 0; y < 120; y++)
                {
                    b[y] = true;
                }
            }
        }
        swManaged.Stop();
        long endAllocManaged = GC.GetAllocatedBytesForCurrentThread();
        long managedMs = swManaged.ElapsedMilliseconds;

        // Phase 3: Bit-packed ulong[]
        long startAllocBitPacked = GC.GetAllocatedBytesForCurrentThread();
        var swBitPacked = Stopwatch.StartNew();
        ulong[][] bitPackedColumns = new ulong[columnsPerChunk][];
        for (int i = 0; i < columnsPerChunk; i++)
        {
            bitPackedColumns[i] = new ulong[words];
        }
        for (int c = 0; c < chunks; c++)
        {
            for (int col = 0; col < columnsPerChunk; col++)
            {
                ulong[] w = bitPackedColumns[col];
                Array.Clear(w, 0, w.Length);
                for (int y = 0; y < 120; y++)
                {
                    w[y >> 6] |= 1UL << (y & 63);
                }
            }
        }
        swBitPacked.Stop();
        long endAllocBitPacked = GC.GetAllocatedBytesForCurrentThread();
        long bitPackedMs = swBitPacked.ElapsedMilliseconds;

        // Parity Verification: every single bit must match across all 1024 columns and 256 posY
        for (int col = 0; col < columnsPerChunk; col++)
        {
            BitArray b = managedColumns[col];
            ulong[] w = bitPackedColumns[col];
            for (int y = 0; y < mapHeight; y++)
            {
                bool managedBit = b[y];
                bool bitPackedBit = (w[y >> 6] & (1UL << (y & 63))) != 0;
                Assert.Equal(managedBit, bitPackedBit);
            }
        }

        output.WriteLine($"[Benchmark Phase 3] Chunks: {chunks:N0} ({chunks * columnsPerChunk:N0} columns)");
        output.WriteLine($"  Baseline (Managed BitArray): {managedMs} ms, Init Allocations: {(endAllocManaged - startAllocManaged) / 1024} KB");
        output.WriteLine($"  Optimized (Bit-Packed ulong[]): {bitPackedMs} ms, Init Allocations: {(endAllocBitPacked - startAllocBitPacked) / 1024} KB");
        output.WriteLine($"  Throughput Speedup: {((double)managedMs / Math.Max(1, bitPackedMs)):F2}x (-{((managedMs - bitPackedMs) * 100.0 / Math.Max(1, managedMs)):F1}%)");
    }
}
