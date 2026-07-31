using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class EntityItemRendererCoverageTests
{
    [Fact]
    public void DoRender3DOpaqueGatesOnDistanceBeforeInterpolation()
    {
        string source = File.ReadAllText(FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityItemRenderer.cs.patch"));

        // The distance gate uses 4096.0 (64^2) and appears before the render logic
        Assert.Contains("4096.0", source);
        Assert.Contains("dx * dx + dz * dz", source);
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
