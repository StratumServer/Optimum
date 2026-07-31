using System;
using System.IO;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class CacheManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimum-launcher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DonorChangeInvalidatesNestedModuleCache()
    {
        var gameDir = Path.Combine(_root, "game");
        var donorDir = Path.Combine(_root, "data", ".optimum", "donors");
        var cacheDir = Path.Combine(_root, "data", ".optimum", "cache");
        Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));
        Directory.CreateDirectory(donorDir);

        File.WriteAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll"), "vanilla");
        File.WriteAllText(Path.Combine(donorDir, "VSEssentials.Donor.dll"), "donor-one");

        var cache = new CacheManager(gameDir, cacheDir, donorDir, "test");
        cache.SavePatchedAssembly("Mods/VSEssentials.dll", [1, 2, 3], null);
        cache.CreateManifest([new PatchedTarget("Mods/VSEssentials.dll", "VSEssentials.Donor.dll", 1)]);

        Assert.NotNull(cache.ValidateCache());
        Assert.True(File.Exists(Path.Combine(cacheDir, "Mods", "VSEssentials.dll")));

        File.WriteAllText(Path.Combine(donorDir, "VSEssentials.Donor.dll"), "donor-two");

        Assert.Null(cache.ValidateCache());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
