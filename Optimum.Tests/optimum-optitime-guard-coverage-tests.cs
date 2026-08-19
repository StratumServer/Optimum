using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class OptimumOptiTimeGuardCoverageTests
{
    [Fact]
    public void GuardMatchesDistributedOptiTimeFileForms()
    {
        string sourcePath = FindRepositoryFile("sources/VintagestoryLib/Optimum/OptimumOptiTimeGuard.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("Contains(\"optitime\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".zip\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".dll\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("extension.Equals(\".cs\", StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains(".disabled-by-optimum", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
