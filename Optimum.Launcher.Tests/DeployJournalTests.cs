using System;
using System.IO;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class DeployJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimum-deploy-tests", Guid.NewGuid().ToString("N"));

    public DeployJournalTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ReadDeployState_DefaultsToCleanVanillaWhenMissing()
    {
        var gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDir);

        var state = Program.ReadDeployState(gameDir);
        Assert.Equal(ModDeployState.CleanVanilla, state);
    }

    [Fact]
    public void WriteAndReadDeployState_RoundtripsAllEnumValues()
    {
        var gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDir);

        foreach (ModDeployState expected in Enum.GetValues<ModDeployState>())
        {
            Program.WriteDeployState(gameDir, expected);
            var actual = Program.ReadDeployState(gameDir);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void DeployPatchedMods_StagesAndTransitionsToActivePatched()
    {
        var (gameDir, cacheDir) = SetupEnvironment();

        Program.DeployPatchedMods(cacheDir, gameDir);

        Assert.Equal("patched-vsessentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")));
        Assert.Equal("patched-vssurvivalmod", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")));
        Assert.Equal(ModDeployState.ActivePatched, Program.ReadDeployState(gameDir));

        // Ensure no temporary files lingered
        string[] tempFiles = Directory.GetFiles(Path.Combine(gameDir, "Mods"), "*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void RestoreVanillaMods_RestoresAndTransitionsToCleanVanilla()
    {
        var (gameDir, cacheDir) = SetupEnvironment();

        // First deploy patched
        Program.DeployPatchedMods(cacheDir, gameDir);
        Assert.Equal(ModDeployState.ActivePatched, Program.ReadDeployState(gameDir));

        // Then restore vanilla
        Program.RestoreVanillaMods(gameDir);

        Assert.Equal("vanilla-vsessentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")));
        Assert.Equal("vanilla-vssurvivalmod", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")));
        Assert.Equal(ModDeployState.CleanVanilla, Program.ReadDeployState(gameDir));

        string[] tempFiles = Directory.GetFiles(Path.Combine(gameDir, "Mods"), "*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void RecoverIncompleteDeployment_StagingPatched_RollsBackToVanilla()
    {
        var (gameDir, cacheDir) = SetupEnvironment();

        // Simulate a crash during staging:
        // VSEssentials was replaced with patched, but VSSurvivalMod left a stray temp file
        File.WriteAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll"), "partially-patched");
        File.WriteAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll.deploy-123.tmp"), "stray-temp");
        Program.WriteDeployState(gameDir, ModDeployState.StagingPatched);

        Program.RecoverIncompleteDeployment(gameDir);

        Assert.Equal("vanilla-vsessentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")));
        Assert.Equal("vanilla-vssurvivalmod", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll.deploy-123.tmp")));
        Assert.Equal(ModDeployState.CleanVanilla, Program.ReadDeployState(gameDir));
    }

    [Fact]
    public void RecoverIncompleteDeployment_RestoringVanilla_CompletesVanillaRestore()
    {
        var (gameDir, cacheDir) = SetupEnvironment();

        // Simulate crash during restore:
        File.WriteAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll"), "still-patched");
        File.WriteAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll.restore-456.tmp"), "stray-restore-temp");
        Program.WriteDeployState(gameDir, ModDeployState.RestoringVanilla);

        Program.RecoverIncompleteDeployment(gameDir);

        Assert.Equal("vanilla-vsessentials", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSEssentials.dll")));
        Assert.Equal("vanilla-vssurvivalmod", File.ReadAllText(Path.Combine(gameDir, "Mods", "VSSurvivalMod.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "Mods", "VSEssentials.dll.restore-456.tmp")));
        Assert.Equal(ModDeployState.CleanVanilla, Program.ReadDeployState(gameDir));
    }

    private (string GameDir, string CacheDir) SetupEnvironment()
    {
        var gameDir = Path.Combine(_root, "game");
        var cacheDir = Path.Combine(_root, "cache");
        var modsDir = Path.Combine(gameDir, "Mods");
        var vanillaDir = Path.Combine(gameDir, ".optimum", "vanilla", "Mods");
        var cacheModsDir = Path.Combine(cacheDir, "Mods");

        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(vanillaDir);
        Directory.CreateDirectory(cacheModsDir);

        File.WriteAllText(Path.Combine(vanillaDir, "VSEssentials.dll"), "vanilla-vsessentials");
        File.WriteAllText(Path.Combine(vanillaDir, "VSSurvivalMod.dll"), "vanilla-vssurvivalmod");

        File.WriteAllText(Path.Combine(modsDir, "VSEssentials.dll"), "vanilla-vsessentials");
        File.WriteAllText(Path.Combine(modsDir, "VSSurvivalMod.dll"), "vanilla-vssurvivalmod");

        File.WriteAllText(Path.Combine(cacheModsDir, "VSEssentials.dll"), "patched-vsessentials");
        File.WriteAllText(Path.Combine(cacheModsDir, "VSSurvivalMod.dll"), "patched-vssurvivalmod");

        return (gameDir, cacheDir);
    }
}
