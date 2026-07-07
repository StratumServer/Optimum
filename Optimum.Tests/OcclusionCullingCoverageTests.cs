using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class OcclusionCullingCoverageTests
{
    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkCuller.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkCuller.cs.patch")]
    public void ThresholdIsScaledByViewDistanceInsteadOfFixed(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("OptimumConfig.OcclusionCullingScaleEnabled", source);
        Assert.Contains("ClientSettings.ViewDistance / 32 + 1", source);
        Assert.Contains("Math.Max(50,", source);
        Assert.Contains("OptimumDiagnostics.OcclusionCullingScale.Skip()", source);
        Assert.Contains("OptimumDiagnostics.OcclusionCullingScale.Hit()", source);
        // The fixed floor stays available as the toggle-off fallback, not deleted outright.
        Assert.Contains(": 100;", source);
    }

    [Fact]
    public void CullInvisibleChunksIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkCuller\", \"CullInvisibleChunks\"", programSource);
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
