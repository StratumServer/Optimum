using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class EntityItemRendererCoverageTests
{
    [Fact]
    public void DoRender3DOpaqueGatesOnDistanceBeforeTheModuloSkip()
    {
        string source = File.ReadAllText(FindRepositoryFile("patches/VSEssentials/EntityRenderer/EntityItemRenderer.cs.patch"));

        Assert.Contains("64.0 * 64.0", source);
        Assert.Contains("RunWittySkipRenderAlgorithm", source);
        Assert.Contains("itemCount > 50", source);

        int gateIndex = source.IndexOf("64.0 * 64.0");
        int skipIndex = source.IndexOf("if (RunWittySkipRenderAlgorithm)");
        Assert.True(gateIndex >= 0 && skipIndex >= 0 && gateIndex < skipIndex,
            "the distance gate must run before the modulo-skip check, not after");
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
