using System;
using System.IO;
using System.Text;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class ShaderCompatibilityScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "optimum-shader-compatibility-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void IgnoresOptimumBuiltInModsAtTheGameDirectory()
    {
        string modsPath = Path.Combine(_root, "game", "Mods");
        Directory.CreateDirectory(modsPath);
        WriteHookAssembly(Path.Combine(modsPath, "VSEssentials.dll"));
        WriteHookAssembly(Path.Combine(modsPath, "VSSurvivalMod.dll"));

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(_root, Path.Combine(_root, "game"), "test");

        Assert.Empty(report.Sources);
        Assert.DoesNotContain("Oit", report.DisabledFeatures);
        Assert.DoesNotContain("EntityLightBatch", report.DisabledFeatures);
        Assert.DoesNotContain("EntityShaderStateCache", report.DisabledFeatures);
    }

    [Fact]
    public void ScansSameNamedExternalModsFromTheDataDirectory()
    {
        string dataPath = Path.Combine(_root, "data");
        string modsPath = Path.Combine(dataPath, "Mods");
        Directory.CreateDirectory(modsPath);
        WriteHookAssembly(Path.Combine(modsPath, "VSEssentials.dll"));
        WriteHookAssembly(Path.Combine(modsPath, "VSSurvivalMod.dll"));
        WriteHookAssembly(Path.Combine(modsPath, "CustomHook.dll"));

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(
            dataPath,
            Path.Combine(_root, "game"),
            "test");

        Assert.Equal(3, report.Sources.Count);
        Assert.Contains(report.Sources, source => source.Name == "VSEssentials");
        Assert.Contains(report.Sources, source => source.Name == "VSSurvivalMod");
        Assert.Contains(report.Sources, source => source.Name == "CustomHook");
        Assert.Contains("Oit", report.DisabledFeatures);
        Assert.Contains("EntityLightBatch", report.DisabledFeatures);
    }

    private static void WriteHookAssembly(string path)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(
            "ShaderRegistry LoadShaderProgram Harmony RegisterRenderer"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
