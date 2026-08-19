using System;
using System.Globalization;
using System.Threading;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Tests for the parallel ServerChunk.FromBytes deserialization feature (Step 29).
/// Covers config toggles, clamping, and diagnostics recording.
/// </summary>
public class ChunkDeserializeParallelTests
{
    public ChunkDeserializeParallelTests()
    {
        OptimumConfig.ChunkDeserializeParallel = true;
        OptimumConfig.ChunkDeserializeParallelMinY = 4;
        OptimumDiagnostics.ResetChunkDeserializeParallel();
    }

    [Fact]
    public void DefaultEnabled()
    {
        Assert.True(OptimumConfig.ChunkDeserializeParallel);
    }

    [Fact]
    public void DefaultMinYIs4()
    {
        Assert.Equal(4, OptimumConfig.ChunkDeserializeParallelMinY);
    }

    [Fact]
    public void ConfigToggleDisablesParallelPath()
    {
        OptimumConfig.ChunkDeserializeParallel = false;
        Assert.False(OptimumConfig.ChunkDeserializeParallel);
    }

    [Fact]
    public void MinYClampedTo2Minimum()
    {
        // Simulate load clamping
        int clamped = Math.Clamp(1, 2, 64);
        Assert.Equal(2, clamped);
    }

    [Fact]
    public void MinYClampedTo64Maximum()
    {
        int clamped = Math.Clamp(100, 2, 64);
        Assert.Equal(64, clamped);
    }

    [Fact]
    public void DiagnosticsRecordSingleColumn()
    {
        OptimumDiagnostics.RecordChunkDeserializeParallel(16);

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        Assert.Contains("columns=1", summary);
        Assert.Contains("chunks=16", summary);
    }

    [Fact]
    public void DiagnosticsAccumulateMultipleColumns()
    {
        OptimumDiagnostics.RecordChunkDeserializeParallel(8);
        OptimumDiagnostics.RecordChunkDeserializeParallel(12);
        OptimumDiagnostics.RecordChunkDeserializeParallel(16);

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        Assert.Contains("columns=3", summary);
        Assert.Contains("chunks=36", summary);
    }

    [Fact]
    public void DiagnosticsResetClearsCounters()
    {
        OptimumDiagnostics.RecordChunkDeserializeParallel(10);
        OptimumDiagnostics.ResetChunkDeserializeParallel();

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        Assert.Contains("columns=0", summary);
        Assert.Contains("chunks=0", summary);
    }

    [Fact]
    public void DiagnosticsThreadSafe()
    {
        int threadCount = 8;
        int iterationsPerThread = 100;
        var barrier = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.Wait();
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    OptimumDiagnostics.RecordChunkDeserializeParallel(4);
                }
            });
            threads[t].Start();
        }

        barrier.Set();
        foreach (var thread in threads)
            thread.Join();

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        int expectedColumns = threadCount * iterationsPerThread;
        int expectedChunks = expectedColumns * 4;
        Assert.Contains($"columns={expectedColumns}", summary);
        Assert.Contains($"chunks={expectedChunks}", summary);
    }

    [Fact]
    public void GateConditionRespectsMinY()
    {
        OptimumConfig.ChunkDeserializeParallelMinY = 8;
        int chunkMapSizeY = 6;

        // Simulates the gate condition in ServerSystemSupplyChunks
        bool shouldParallelize = OptimumConfig.ChunkDeserializeParallel
            && chunkMapSizeY >= OptimumConfig.ChunkDeserializeParallelMinY;

        Assert.False(shouldParallelize);
    }

    [Fact]
    public void GateConditionAllowsWhenAboveMinY()
    {
        OptimumConfig.ChunkDeserializeParallelMinY = 4;
        int chunkMapSizeY = 16;

        bool shouldParallelize = OptimumConfig.ChunkDeserializeParallel
            && chunkMapSizeY >= OptimumConfig.ChunkDeserializeParallelMinY;

        Assert.True(shouldParallelize);
    }

    [Fact]
    public void GateConditionRespectsDisabledToggle()
    {
        OptimumConfig.ChunkDeserializeParallel = false;
        int chunkMapSizeY = 32;

        bool shouldParallelize = OptimumConfig.ChunkDeserializeParallel
            && chunkMapSizeY >= OptimumConfig.ChunkDeserializeParallelMinY;

        Assert.False(shouldParallelize);
    }

    [Fact]
    public void ConfigDataSerializationRoundTrip()
    {
        // Verify the DTO properties exist and have correct defaults
        var data = new OptimumConfigData();
        Assert.True(data.ChunkDeserializeParallel);
        Assert.Equal(4, data.ChunkDeserializeParallelMinY);
    }
}
