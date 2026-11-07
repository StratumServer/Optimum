using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class CloudRendererTests
{
    [Fact]
    public void CloudRendererPatch_DecouplesCloudDistanceAndAvoidsRedundantRebuilds()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSEssentials/Systems/Weather/CloudRenderer.cs.patch");
        Assert.True(File.Exists(patchPath), "CloudRenderer.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        // Verify cloud view distance clamp
        Assert.Contains("public const int MaxCloudViewDistance = 512;", patched);
        Assert.Contains("public const int MaxCloudDistance = 8 * MaxCloudViewDistance;", patched);
        Assert.Contains("int effectiveDistance = Math.Min(viewDistance, MaxCloudDistance);", patched);
        Assert.Contains("CloudTileLength = GameMath.Clamp(effectiveDistance / CloudTileSize, 20, 200);", patched);

        // Verify redundant rebuild avoidance
        Assert.Contains("if (lastViewDistance >= MaxCloudViewDistance && newValue >= MaxCloudViewDistance)", patched);
        Assert.Contains("lastViewDistance = newValue;", patched);
        Assert.Contains("requireTileRebuild = true;", patched);
    }
}
