using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class ChunkRenderDiagnosticsCoverageTests
{
    [Fact]
    public void MeshPoolRecordsEveryMultiDrawSubmission()
    {
        string source = Read("VintagestoryApi/Client/MeshPool/MeshDataPool.cs");

        Assert.Contains("RecordChunkDrawCall(indicesGroupsCount)", source);
        Assert.Contains("render.RenderMesh(modelRef, indicesStartsByte, indicesSizes, indicesGroupsCount)", source);
    }

    [Fact]
    public void PoolManagerRecordsCullingAndVisiblePoolCounts()
    {
        string source = Read("VintagestoryApi/Client/MeshPool/MeshDataPoolManager.cs");

        Assert.Contains("int poolsRendered = 0;", source);
        Assert.Contains("Stopwatch.GetTimestamp()", source);
        Assert.Contains("RecordChunkFrustumCullTicks", source);
        Assert.Contains("int poolsCulled = 0;", source);
        Assert.Contains("poolsCulled++", source);
        Assert.Contains("RecordChunkRenderPass(poolsRendered, poolsCulled)", source);
        Assert.Equal(2, Count(source, "poolsRendered++"));
    }

    [Fact]
    public void MasterPoolAnchorsRenderCountersToClientFrames()
    {
        string source = Read("VintagestoryApi/Client/MeshPool/MeshDataPoolMasterManager.cs");

        Assert.Contains("RecordChunkRenderFrame()", source);
    }

    [Fact]
    public void RenderSummaryUsesFrameAnchoredCounters()
    {
        string source = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("RecordChunkRenderFrame", source);
        Assert.Contains("ResetChunkRenderFrame", source);
        Assert.Contains("ChunkRenderWindowSize = 120", source);
        Assert.Contains("windowFrames=", source);
        Assert.Contains("windowPoolsCulled/frame=", source);
        Assert.Contains("drawCalls/frame=", source);
        Assert.Contains("poolsRendered/frame=", source);
        Assert.Contains("visibleGroups/frame=", source);
        Assert.Contains("frustumCullMs/frame=", source);
    }

    [Fact]
    public void RenderDiagnosticsKeepsABoundedWindow()
    {
        OptimumDiagnostics.ResetChunkRender();
        OptimumDiagnostics.RecordChunkRenderFrame();

        for (int i = 0; i < 125; i++)
        {
            OptimumDiagnostics.RecordChunkDrawCall(1);
            OptimumDiagnostics.RecordChunkRenderPass(1, 1);
            OptimumDiagnostics.RecordChunkFrustumCullTicks(1);
            OptimumDiagnostics.RecordChunkRenderFrame();
        }

        string summary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.Contains("windowFrames=120", summary);
        double expectedCulled = 1.0;
        Assert.Contains($"windowPoolsCulled/frame={expectedCulled:0.0}", summary);
        OptimumDiagnostics.ResetChunkRender();
    }

    private static int Count(string source, string value)
    {
        return (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
    }

    private static string Read(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
