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
}
