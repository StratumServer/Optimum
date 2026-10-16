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
        string stagedPath = WriteStagedDll(cacheDir, "Mods/VSEssentials.stage-test.dll", [1, 2, 3]);
        cache.CommitPatchedAssembly("Mods/VSEssentials.dll", stagedPath);
        cache.CreateManifest([new PatchedTarget("Mods/VSEssentials.dll", "VSEssentials.Donor.dll", 1)]);

        Assert.NotNull(cache.ValidateCache());
        Assert.True(File.Exists(Path.Combine(cacheDir, "Mods", "VSEssentials.dll")));

        File.WriteAllText(Path.Combine(donorDir, "VSEssentials.Donor.dll"), "donor-two");

        Assert.Null(cache.ValidateCache());
    }

    [Fact]
    public void ModifiedCachedAssemblyInvalidatesTheManifest()
    {
        var gameDir = Path.Combine(_root, "game");
        var donorDir = Path.Combine(_root, "data", ".optimum", "donors");
        var cacheDir = Path.Combine(_root, "data", ".optimum", "cache");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(donorDir);

        File.WriteAllText(Path.Combine(gameDir, "VintagestoryLib.dll"), "vanilla");
        File.WriteAllText(Path.Combine(donorDir, "VintagestoryLib.Donor.dll"), "donor");

        var cache = new CacheManager(gameDir, cacheDir, donorDir, "test");
        cache.CommitPatchedAssembly("VintagestoryLib.dll",
            WriteStagedDll(cacheDir, "VintagestoryLib.stage-test.dll", [1, 2, 3]));
        cache.CreateManifest([new PatchedTarget("VintagestoryLib.dll", "VintagestoryLib.Donor.dll", 1)]);

        File.WriteAllBytes(Path.Combine(cacheDir, "VintagestoryLib.dll"), [9, 9, 9]);

        Assert.Null(cache.ValidateCache());
    }

    [Fact]
    public void CacheWritesLeaveNoTemporaryFiles()
    {
        var gameDir = Path.Combine(_root, "game");
        var donorDir = Path.Combine(_root, "data", ".optimum", "donors");
        var cacheDir = Path.Combine(_root, "data", ".optimum", "cache");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(donorDir);

        File.WriteAllText(Path.Combine(gameDir, "VintagestoryLib.dll"), "vanilla");
        File.WriteAllText(Path.Combine(donorDir, "VintagestoryLib.Donor.dll"), "donor");

        var cache = new CacheManager(gameDir, cacheDir, donorDir, "test");
        cache.CommitPatchedAssembly("VintagestoryLib.dll",
            WriteStagedDll(cacheDir, "VintagestoryLib.stage-test.dll", [1, 2, 3]));
        cache.CreateManifest([new PatchedTarget("VintagestoryLib.dll", "VintagestoryLib.Donor.dll", 1)]);

        Assert.Empty(Directory.EnumerateFiles(cacheDir, "*.tmp-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(cacheDir, "*.stage-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CommitReplacesExistingAssemblyAndMovesMatchingSymbolsOnce()
    {
        var gameDir = Path.Combine(_root, "game");
        var donorDir = Path.Combine(_root, "data", ".optimum", "donors");
        var cacheDir = Path.Combine(_root, "data", ".optimum", "cache");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(donorDir);

        var cache = new CacheManager(gameDir, cacheDir, donorDir, "test");
        string outputPath = Path.Combine(cacheDir, "VintagestoryLib.dll");
        string outputPdbPath = Path.ChangeExtension(outputPath, ".pdb");
        File.WriteAllBytes(outputPath, [1]);
        File.WriteAllBytes(outputPdbPath, [2]);
        string stagedPath = WriteStagedDll(cacheDir, "VintagestoryLib.stage-new.dll", [3, 4]);
        File.WriteAllBytes(Path.ChangeExtension(stagedPath, ".pdb"), [5, 6]);

        cache.CommitPatchedAssembly("VintagestoryLib.dll", stagedPath);

        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(outputPath));
        Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(outputPdbPath));
        Assert.False(File.Exists(stagedPath));
        Assert.False(File.Exists(Path.ChangeExtension(stagedPath, ".pdb")));
    }

    [Fact]
    public void CommitRemovesStaleSymbolsWhenPatcherProducesNoPdb()
    {
        var gameDir = Path.Combine(_root, "game");
        var donorDir = Path.Combine(_root, "data", ".optimum", "donors");
        var cacheDir = Path.Combine(_root, "data", ".optimum", "cache");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(donorDir);

        var cache = new CacheManager(gameDir, cacheDir, donorDir, "test");
        string outputPdbPath = Path.Combine(cacheDir, "VintagestoryLib.pdb");
        File.WriteAllBytes(outputPdbPath, [1]);
        string stagedPath = WriteStagedDll(cacheDir, "VintagestoryLib.stage-new.dll", [3, 4]);

        cache.CommitPatchedAssembly("VintagestoryLib.dll", stagedPath);

        Assert.False(File.Exists(outputPdbPath));
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(Path.Combine(cacheDir, "VintagestoryLib.dll")));
    }

    [Fact]
    public void CommitRejectsStagedAssemblyOutsideCacheDirectory()
    {
        var gameDir = Path.Combine(_root, "game");
        var donorDir = Path.Combine(_root, "data", ".optimum", "donors");
        var cacheDir = Path.Combine(_root, "data", ".optimum", "cache");
        var stagingDir = Path.Combine(_root, "other-staging");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(donorDir);

        var cache = new CacheManager(gameDir, cacheDir, donorDir, "test");
        string stagedPath = Path.Combine(stagingDir, "VintagestoryLib.stage-test.dll");
        File.WriteAllBytes(stagedPath, [1, 2, 3]);

        var exception = Assert.Throws<ArgumentException>(
            () => cache.CommitPatchedAssembly("VintagestoryLib.dll", stagedPath));

        Assert.Contains(stagingDir, exception.Message);
        Assert.Contains(cacheDir, exception.Message);
        Assert.True(File.Exists(stagedPath));
        Assert.False(File.Exists(Path.Combine(cacheDir, "VintagestoryLib.dll")));
    }

    [Fact]
    public void CommitRejectsEmptyStagedAssembly()
    {
        var gameDir = Path.Combine(_root, "game");
        var donorDir = Path.Combine(_root, "data", ".optimum", "donors");
        var cacheDir = Path.Combine(_root, "data", ".optimum", "cache");
        Directory.CreateDirectory(donorDir);

        var cache = new CacheManager(gameDir, cacheDir, donorDir, "test");
        string stagedPath = WriteStagedDll(cacheDir, "VintagestoryLib.stage-empty.dll", []);

        var exception = Assert.Throws<InvalidDataException>(
            () => cache.CommitPatchedAssembly("VintagestoryLib.dll", stagedPath));

        Assert.Contains(stagedPath, exception.Message);
        Assert.False(File.Exists(Path.Combine(cacheDir, "VintagestoryLib.dll")));
    }

    private static string WriteStagedDll(string cacheDir, string relativePath, byte[] bytes)
    {
        string path = Path.Combine(cacheDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
