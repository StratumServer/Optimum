using System;
using System.IO;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Tests;

public sealed class RuntimeFailClosedTests
{
    [Fact]
    public void CacheValidationRejectsAManifestThatOmitsARequiredAssembly()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-cache-test-{Guid.NewGuid():N}");
        string gameDir = Path.Combine(root, "game");
        string cacheDir = Path.Combine(root, "cache");
        string donorDir = Path.Combine(root, "donors");

        try
        {
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(donorDir);
            WriteFile(Path.Combine(gameDir, "VintagestoryLib.dll"));
            WriteFile(Path.Combine(cacheDir, "VintagestoryLib.dll"));
            WriteFile(Path.Combine(donorDir, "VintagestoryLib.Donor.dll"));

            var cache = new CacheManager(gameDir, cacheDir, donorDir, "0.3.3");
            cache.CreateManifest(
            [
                new PatchedTarget(
                    "VintagestoryLib.dll",
                    "VintagestoryLib.Donor.dll",
                    1),
            ]);

            Assert.True(cache.ValidateCache(["VintagestoryLib.dll"]).IsValid);

            CacheValidationResult missing = cache.ValidateCache(["VintagestoryLib.dll", "VintagestoryAPI.dll"]);
            Assert.False(missing.IsValid);
            Assert.Equal(CacheValidationStatus.RequiredAssemblyMissing, missing.Status);
            Assert.Equal("VintagestoryAPI.dll", missing.TargetAssembly);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AbortCleanupCanReportCacheInvalidationFailureWithoutThrowing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"optimum-cache-failure-{Guid.NewGuid():N}");
        string cacheDir = Path.Combine(root, "cache");

        try
        {
            Directory.CreateDirectory(cacheDir);
            var cache = new CacheManager(
                Path.Combine(root, "game"),
                cacheDir,
                Path.Combine(root, "donors"),
                "0.3.3");
            Directory.CreateDirectory(cache.ManifestPath);

            Assert.False(cache.TryInvalidate(out string? failureReason));
            Assert.Contains("directory", failureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteFile(string path)
    {
        File.WriteAllBytes(path, [1, 2, 3]);
    }
}
