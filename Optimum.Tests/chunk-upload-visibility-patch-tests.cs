using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

public sealed class ChunkUploadVisibilityPatchTests
{
    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    [Fact]
    public void ManagerCapturesBoundsBeforeUploadAndClassifiesAfterCurrentFrustumSetup()
    {
        string patch = ReadRepoFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");
        string patcher = ReadRepoFile("Optimum.Patcher/Program.cs");
        string simd = ReadRepoFile("sources/VintagestoryApi/Config/OptimumFrustumCullSimd.cs");

        string boundsCopy = "Sphere uploadBounds = telemetryEnabled ? tesselatedChunk.boundingSphere : default;";
        string capture = "OptimumCaptureVisibilitySample(uploadQueueAgeTicks, uploadBounds);";
        Assert.Equal(2, Regex.Matches(patch, Regex.Escape(boundsCopy)).Count);
        Assert.Equal(2, Regex.Matches(patch, Regex.Escape(capture)).Count);
        Assert.Equal(2, Regex.Matches(patch, @"\+\s*if\s*\(telemetryEnabled\)\s*\+\s*\{\s*\+\s*OptimumCaptureVisibilitySample\(uploadQueueAgeTicks, uploadBounds\);").Count);
        int searchFrom = 0;
        for (int i = 0; i < 2; i++)
        {
            int boundsIndex = patch.IndexOf(boundsCopy, searchFrom, System.StringComparison.Ordinal);
            int uploadIndex = patch.IndexOf("game.chunkRenderer.AddTesselatedChunk", boundsIndex, System.StringComparison.Ordinal);
            int captureIndex = patch.IndexOf(capture, uploadIndex, System.StringComparison.Ordinal);
            Assert.True(boundsIndex >= searchFrom && uploadIndex > boundsIndex && captureIndex > uploadIndex);
            searchFrom = captureIndex + capture.Length;
        }
        Assert.Contains("OptimumIsValidVisibilityBounds(bounds)", patch);
        Assert.Contains("float.IsFinite(bounds.x)", patch);
        Assert.Contains("bounds.radius >= 0f", patch);
        Assert.Contains("optimumVisibilityClassifications[i] = 2", patch);
        Assert.Contains("OptimumFrustumCullSimd.InFrustumForTelemetry(game.frustumCuller, bounds)", patch);
        Assert.Contains("InFrustumForTelemetry(FrustumCulling culler, Sphere sphere)", simd);
        Assert.Contains("return InFrustumCore(culler, sphere, recordDiagnostics: false);", simd);
        Assert.Contains("if (OptimumConfig.EffectiveSimdCulling)", simd);
        Assert.Contains("OptimumDiagnostics.RecordSimdFrustumTest(culled: outside);", simd);
        Assert.Contains("internal static bool InFrustumForTelemetry(FrustumCulling culler, Sphere sphere)", simd);
        Assert.Contains("optimumVisibilityFramePending = telemetryEnabled;", patch);
        Assert.Contains("optimumVisibilityFrameDeltaSeconds,", patch);
        Assert.Contains("RegisterRenderer(optimumVisibilityRenderer, EnumRenderStage.Opaque", patch);
        Assert.Contains("OnOptimumAfterFrustum\", 1)", patcher);
    }

    [Fact]
    public void VisibilityHookIsLazyBoundedAndClearedAcrossWorldLifecycle()
    {
        string patch = ReadRepoFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselatorManager.cs.patch");

        Assert.Contains("if (optimumVisibilityRendererRegistered || !OptimumChunkStreamStats.Enabled)", patch);
        Assert.Contains("optimumVisibilitySampleCapacity = 4096", patch);
        Assert.Contains("OptimumFlushPendingVisibilityFrame(classificationFailed: true);", patch);
        Assert.Contains("game.api.Event.LeaveWorld += OnOptimumLeaveWorld;", patch);
        Assert.Contains("game.api.Event.LeaveWorld -= OnOptimumLeaveWorld;", patch);
        Assert.Contains("game.eventManager.UnregisterRenderer(optimumVisibilityRenderer, EnumRenderStage.Opaque);", patch);
        Assert.Contains("optimumPreviousFrameUploaded = false;", patch);
    }
}
