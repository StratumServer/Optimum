#nullable enable
using System;
using System.IO;
using System.Linq;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class LegacySsboMigrationTests
{
    [Fact]
    public void LegacySsboMigration_DefaultsAndConfigRoundTrip()
    {
        bool origEnabled = OptimumConfig.LegacySsboMigrationEnabled;
        bool origMigrated = OptimumConfig.LegacySsboMigrated;
        string dir = Path.Combine(Path.GetTempPath(), "optimum-ssbo-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(OptimumConfig.LegacySsboMigrationEnabled);
            Assert.False(OptimumConfig.LegacySsboMigrated);
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.LegacySsboMigration));
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.LegacySsboMigrated));

            OptimumConfig.SetDataPath(dir);
            OptimumConfig.LegacySsboMigrationEnabled = false;
            OptimumConfig.LegacySsboMigrated = true;
            OptimumConfig.Save();

            OptimumConfig.LegacySsboMigrationEnabled = true;
            OptimumConfig.LegacySsboMigrated = false;
            OptimumConfig.Load();

            Assert.False(OptimumConfig.LegacySsboMigrationEnabled);
            Assert.True(OptimumConfig.LegacySsboMigrated);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.LegacySsboMigrationEnabled = origEnabled;
            OptimumConfig.LegacySsboMigrated = origMigrated;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void EvaluateLegacySsboMigration_EnablesOnSupportedHardware()
    {
        bool origEnabled = OptimumConfig.LegacySsboMigrationEnabled;
        bool origMigrated = OptimumConfig.LegacySsboMigrated;
        try
        {
            OptimumConfig.LegacySsboMigrationEnabled = true;
            OptimumConfig.LegacySsboMigrated = false;

            bool migrate = OptimumConfig.EvaluateLegacySsboMigration(
                currentAllowSsbo: false,
                isMac: false,
                glVersionString: "4.6.0 NVIDIA 560.35.03",
                glslVersionString: "4.60 NVIDIA",
                glRenderer: "NVIDIA GeForce RTX 3070/PCIe/SSE2",
                out bool markMigrated,
                out string? message);

            Assert.True(migrate);
            Assert.True(markMigrated);
            Assert.NotNull(message);
            Assert.Contains("allowSSBOs=false -> true", message);
        }
        finally
        {
            OptimumConfig.LegacySsboMigrationEnabled = origEnabled;
            OptimumConfig.LegacySsboMigrated = origMigrated;
        }
    }

    [Fact]
    public void EvaluateLegacySsboMigration_PreservesManualOptOutOnceMigrated()
    {
        bool origEnabled = OptimumConfig.LegacySsboMigrationEnabled;
        bool origMigrated = OptimumConfig.LegacySsboMigrated;
        try
        {
            OptimumConfig.LegacySsboMigrationEnabled = true;
            OptimumConfig.LegacySsboMigrated = false;

            // Already enabled install records migration marker without changing setting.
            bool first = OptimumConfig.EvaluateLegacySsboMigration(
                currentAllowSsbo: true,
                isMac: false,
                glVersionString: "4.6 (Core Profile) Mesa 24.2",
                glslVersionString: "4.60",
                glRenderer: "AMD Radeon RX 6800 XT",
                out bool firstMark,
                out _);

            Assert.False(first);
            Assert.True(firstMark);

            // Subsequent run where user manually turned allowSSBOs off must stay off.
            OptimumConfig.LegacySsboMigrated = true;
            bool second = OptimumConfig.EvaluateLegacySsboMigration(
                currentAllowSsbo: false,
                isMac: false,
                glVersionString: "4.6 (Core Profile) Mesa 24.2",
                glslVersionString: "4.60",
                glRenderer: "AMD Radeon RX 6800 XT",
                out bool secondMark,
                out _);

            Assert.False(second);
            Assert.False(secondMark);
        }
        finally
        {
            OptimumConfig.LegacySsboMigrationEnabled = origEnabled;
            OptimumConfig.LegacySsboMigrated = origMigrated;
        }
    }

    [Theory]
    [InlineData(true, "4.6.0", "4.60", "Apple M2 Max", "macOS")]
    [InlineData(false, "4.1.0", "4.10", "Mesa Intel(R) HD", "below 4.3")]
    [InlineData(false, "3.3.0 NVIDIA", "3.30", "NVIDIA GeForce", "below 4.3")]
    [InlineData(false, "4.6.0", "3.30", "NVIDIA GeForce", "below 430")]
    [InlineData(false, null, null, "NVIDIA GeForce", "below 4.3")]
    [InlineData(false, "4.6 (Core Profile) Mesa 24.0", "4.60", "Intel(R) Arc(TM) A770 Graphics", "Intel Arc")]
    [InlineData(false, "4.6 (Core Profile) Mesa 24.0", "4.60", "Mesa Intel(R) Arc(tm) A750 Graphics (DG2)", "Intel Arc")]
    public void EvaluateLegacySsboMigration_SkipsExcludedPlatformsAndGpus(
        bool isMac,
        string? glVersion,
        string? glslVersion,
        string? glRenderer,
        string expectedReason)
    {
        bool origEnabled = OptimumConfig.LegacySsboMigrationEnabled;
        bool origMigrated = OptimumConfig.LegacySsboMigrated;
        try
        {
            OptimumConfig.LegacySsboMigrationEnabled = true;
            OptimumConfig.LegacySsboMigrated = false;

            bool migrate = OptimumConfig.EvaluateLegacySsboMigration(
                currentAllowSsbo: false,
                isMac: isMac,
                glVersionString: glVersion,
                glslVersionString: glslVersion,
                glRenderer: glRenderer,
                out bool markMigrated,
                out string? message);

            Assert.False(migrate);
            Assert.False(markMigrated);
            Assert.NotNull(message);
            Assert.Contains(expectedReason, message);
        }
        finally
        {
            OptimumConfig.LegacySsboMigrationEnabled = origEnabled;
            OptimumConfig.LegacySsboMigrated = origMigrated;
        }
    }

    [Fact]
    public void EvaluateLegacySsboMigration_RespectsDisabledToggle()
    {
        bool origEnabled = OptimumConfig.LegacySsboMigrationEnabled;
        bool origMigrated = OptimumConfig.LegacySsboMigrated;
        try
        {
            OptimumConfig.LegacySsboMigrationEnabled = false;
            OptimumConfig.LegacySsboMigrated = false;

            bool migrate = OptimumConfig.EvaluateLegacySsboMigration(
                currentAllowSsbo: false,
                isMac: false,
                glVersionString: "4.6.0 NVIDIA",
                glslVersionString: "4.60",
                glRenderer: "NVIDIA GeForce RTX 3070",
                out bool markMigrated,
                out string? message);

            Assert.False(migrate);
            Assert.False(markMigrated);
            Assert.Null(message);
        }
        finally
        {
            OptimumConfig.LegacySsboMigrationEnabled = origEnabled;
            OptimumConfig.LegacySsboMigrated = origMigrated;
        }
    }

    [Fact]
    public void ClientPlatformWindowsPatch_WiresLegacySsboMigration()
    {
        string patch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch"));

        Assert.Contains("OptimumConfig.EvaluateLegacySsboMigration(", patch);
        Assert.Contains("ClientSettings.AllowSSBOs = true;", patch);
        Assert.Contains("ClientSettings.Inst.Save(force: true);", patch);
        Assert.Contains("OptimumConfig.LegacySsboMigrated = true;", patch);
        Assert.Contains("OptimumConfig.Save();", patch);
    }
}
