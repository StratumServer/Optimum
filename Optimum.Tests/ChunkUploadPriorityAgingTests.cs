using System;
using System.Diagnostics;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class ChunkUploadPriorityAgingTests
{
    [Fact]
    public void QueueAgeReducesPriorityAtConfiguredRate()
    {
        long readyTimestamp = 1_000_000;
        long sortTimestamp = readyTimestamp + Stopwatch.Frequency / 4;
        double priorityUnitsPerTick = 100_000d / Stopwatch.Frequency;

        int agedPriority = OptimumChunkUploadPriority.ApplyAge(40_000, readyTimestamp, sortTimestamp, priorityUnitsPerTick, inCurrentFrustum: true);

        Assert.InRange(agedPriority, 14_999, 15_001);
    }

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(200L, 100L)]
    public void MissingOrFutureReadyTimestampDoesNotChangePriority(long readyTimestamp, long sortTimestamp)
    {
        Assert.Equal(123, OptimumChunkUploadPriority.ApplyAge(123, readyTimestamp, sortTimestamp, 0.5, inCurrentFrustum: true));
    }

    [Fact]
    public void OutOfFrustumChunksKeepCameraPriority()
    {
        Assert.Equal(123, OptimumChunkUploadPriority.ApplyAge(123, 1, 100_000, 1, inCurrentFrustum: false));
    }

    [Fact]
    public void OlderVisibleChunkCanOvertakeYoungerChunkWithEqualBasePriority()
    {
        double priorityUnitsPerTick = 100_000d / Stopwatch.Frequency;
        long oldReady = 1_000_000;
        long youngReady = oldReady + Stopwatch.Frequency / 5;
        long sortTimestamp = oldReady + Stopwatch.Frequency / 4;

        int oldPriority = OptimumChunkUploadPriority.ApplyAge(20_000, oldReady, sortTimestamp, priorityUnitsPerTick, inCurrentFrustum: true);
        int youngPriority = OptimumChunkUploadPriority.ApplyAge(20_000, youngReady, sortTimestamp, priorityUnitsPerTick, inCurrentFrustum: true);

        Assert.True(oldPriority < youngPriority);
    }

    [Fact]
    public void FractionalPriorityCreditDoesNotChangePriority()
    {
        Assert.Equal(123, OptimumChunkUploadPriority.ApplyAge(123, 1, 2, 0.5, inCurrentFrustum: true));
    }

    [Fact]
    public void QueueAgeSaturatesAtLowestPriorityWithoutOverflow()
    {
        Assert.Equal(int.MinValue, OptimumChunkUploadPriority.ApplyAge(1_000, 1, long.MaxValue, 1, inCurrentFrustum: true));
    }

    [Fact]
    public void ChunkPriorityComparisonUsesOverflowSafeComparer()
    {
        string patchedChunk = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunk.cs.patch");
        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("return priority.CompareTo(obj.priority);", patchedChunk);
        Assert.DoesNotContain("return priority - obj.priority;", patchedChunk);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.TesselatedChunk\", \"CompareTo\", 1)", patcher);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.TesselatedChunk\", \"MergeIfEqual\", 1)", patcher);
    }

    [Fact]
    public void ManagerRefreshesPriorityForCameraPitchAndNewFrustumPlanes()
    {
        string managerPatch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");

        Assert.Contains("cameraPlayer.CameraPitch", managerPatch);
        Assert.Contains("plr != null && cameraPlayer != null", managerPatch);
        Assert.Contains("bool cameraPoseChanged = plr != null && cameraPlayer != null", managerPatch);
        Assert.Contains("game.MainCamera.CameraEyePos.SquareDistanceTo(_lastSortCameraPos) >= SortMoveThresholdSq", managerPatch);
        Assert.Contains("cameraPlayer.CameraMode != _lastSortCameraMode", managerPatch);
        Assert.Contains("bool projectionChanged = _lastSortCameraFov != game.MainCamera.Fov", managerPatch);
        Assert.Contains("|| _lastSortCameraZNear != game.MainCamera.ZNear", managerPatch);
        Assert.Contains("|| _lastSortCameraZFar != game.MainCamera.ZFar", managerPatch);
        Assert.Contains("|| _lastSortScreenWidth != game.Width", managerPatch);
        Assert.Contains("|| _lastSortScreenHeight != game.Height", managerPatch);
        Assert.Contains("bool queueSortRequested = queueNeedsSort;", managerPatch);
        Assert.Contains("bool sortRequested = cameraChanged || queueSortRequested || projectionChanged;", managerPatch);
        Assert.Contains("_prioritySortFrustumRefreshPending", managerPatch);
        Assert.Contains("_prioritySortFrustumRefreshPending = cameraChanged || projectionChanged || (queueSortRequested && cameraPoseChanged);", managerPatch);
        Assert.Contains("Re-sort once next frame so a stopped camera uses the newly calculated planes.", managerPatch);
        Assert.Contains("OptimumIsValidVisibilityBounds(chunk.boundingSphere)", managerPatch);
    }
}
