using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class ChunkMeshingQuickWinsCoverageTests
{
    [Fact]
    public void SortableQueueSortAlreadyReusesTheScratchBuffer()
    {
        // C2: already shipped before this batch started, part of the
        // open-source VintagestoryApi fork (no Cecil constraint). Assert
        // it stays that way.
        string source = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Datastructures/SortableQueue.cs"));

        // expandArray() legitimately allocates T[maxSize] to grow the ring
        // buffer; the thing that should be gone is Sort()'s own copy of
        // that pattern, replaced by the sortBuffer reuse check below.
        int sortMethodStart = source.IndexOf("public void Sort()", System.StringComparison.Ordinal);
        int sortMethodEnd = source.IndexOf("public void RunForEach", System.StringComparison.Ordinal);
        Assert.True(sortMethodStart > 0 && sortMethodEnd > sortMethodStart, "Could not locate the Sort() method body.");
        string sortMethodBody = source[sortMethodStart..sortMethodEnd];

        // The old code unconditionally allocated `T[] newArray = new T[maxSize]`
        // every call; the fix reuses `sortBuffer`, only (re)allocating it
        // when missing or too small.
        Assert.DoesNotContain("newArray", sortMethodBody);
        Assert.Contains("if (sortBuffer == null || sortBuffer.Length < maxSize)", sortMethodBody);
    }

    [Fact]
    public void ChunkTesselatorManagerNoLongerUsesAnonymousDelegateInSource()
    {
        // C1: OnBeforeFrame used `tessChunksQueue.RunForEach(delegate(...) {...})`,
        // a closure the method can't carry through Cecil transplant.
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");
        Assert.DoesNotContain("RunForEach(delegate", source);
    }

    [Fact]
    public void ChunkTesselatorManagerKeepsLockFieldsAsObjectNotLock()
    {
        // Deliberate: this file's (unshipped) System.Threading.Lock
        // migration compiles `lock` differently (Lock.EnterScope vs
        // Monitor.Enter/Exit). Cecil's field-reference remapping matches
        // by name only, not type, so transplanting OnBeforeFrame while
        // this field is Lock-typed but the vanilla target's field is still
        // object-typed would carry mismatched IL through undetected by
        // either safety check. Assert the fields stay object-typed here
        // until that migration is actually Cecil-registered.
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");

        Assert.Contains("private readonly object tessChunksQueueLock = new object();", source);
        Assert.Contains("private readonly object tessChunksQueuePriorityLock = new object();", source);
        Assert.DoesNotContain("readonly Lock tessChunksQueueLock", source);
        Assert.DoesNotContain("readonly Lock tessChunksQueuePriorityLock", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch")]
    public void OnBeforeFrameSkipsSortWhenPlayerHasNotMoved(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("SortMoveThresholdSq = 0.25", source);
        Assert.Contains("SortYawThreshold = 0.05f", source);
        Assert.Contains("tessChunksQueue.ItemAt(i).RecalcPriority(game.player);", source);
        Assert.Contains("OptimumDiagnostics.ChunkUploadSort.Hit()", source);
        Assert.Contains("OptimumDiagnostics.ChunkUploadSort.Skip()", source);
    }

    [Fact]
    public void OnBeforeFrameIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselatorManager\", \"OnBeforeFrame\", 1", programSource);
    }

    [Fact]
    public void AddTesselatedChunkNoLongerAllocatesAFreshVec3iInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch");
        Assert.DoesNotContain("new Vec3i(tesschunk.positionX", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch")]
    public void AddTesselatedChunkReusesTheScratchVec3i(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));
        Assert.Contains("(chunkOriginScratch ??= new Vec3i()).Set(tesschunk.positionX", source);
    }

    [Fact]
    public void AddTesselatedChunkIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"AddTesselatedChunk\", 2", programSource);
    }

    [Fact]
    public void TesselatedChunkNoLongerAllocatesFreshPoolLocationListsInSource()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunk.cs.patch");

        Assert.DoesNotContain("new List<ModelDataPoolLocation>(centerParts.Length);", source);
        Assert.DoesNotContain("new List<ModelDataPoolLocation>(edgeParts.Length);", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunk.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunk.cs.patch")]
    public void TesselatedChunkReusesChunkRendererScratchLists(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("chunkRenderer.centerPoolLocationsScratch ??= new List<ModelDataPoolLocation>(centerParts.Length)", source);
        Assert.Contains("chunkRenderer.edgePoolLocationsScratch ??= new List<ModelDataPoolLocation>(edgeParts.Length)", source);
    }

    [Fact]
    public void AddCenterAndEdgeToPoolsAreRegisteredAsCecilTransplantTargets()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("\"Vintagestory.Client.NoObf.TesselatedChunk\", \"AddCenterToPools\", 5", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.TesselatedChunk\", \"AddEdgeToPools\", 5", programSource);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
