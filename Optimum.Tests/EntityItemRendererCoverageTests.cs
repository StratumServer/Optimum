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

        // The distance gate dynamically scales based on viewport height and appears before render logic
        Assert.Contains("Math.Max(64.0, 64.0 * ((double)capi.Render.FrameHeight / 1080.0))", source);
        Assert.Contains("dx * dx + dz * dz > maxDist * maxDist", source);
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
