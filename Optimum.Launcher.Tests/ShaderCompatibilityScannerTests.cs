using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Optimum.Launcher;
using Vintagestory.API.Config;
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
        Assert.DoesNotContain("MapPageCache", report.DisabledFeatures);
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
        Assert.Contains("MapPageCache", report.DisabledFeatures);
        Assert.Contains("EntityLightBatch", report.DisabledFeatures);
    }

    [Fact]
    public void ExternalShaderAssetsDisableMapPageCache()
    {
        string dataPath = Path.Combine(_root, "data");
        string archivePath = Path.Combine(
            dataPath,
            "Mods",
            "AncestralBliss.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry(
                "assets/ancestralblissshaders/shaders/chunkopaque.fsh").Open());
            writer.Write("void main() { }");
        }

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(
            dataPath,
            Path.Combine(_root, "game"),
            "test");

        Assert.Contains(report.Sources, source => source.Name == "AncestralBliss");
        Assert.Contains("MapPageCache", report.DisabledFeatures);
        Assert.Contains("GreedyMesh", report.DisabledFeatures);
        Assert.Contains("ShaderPreprocessParallel", report.DisabledFeatures);
    }

    [Fact]
    public void SavedShaderCompatibilityReportDisablesEffectiveMapPageCache()
    {
        string dataPath = Path.Combine(_root, "data");
        string archivePath = Path.Combine(dataPath, "Mods", "AncestralBliss.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry(
                "assets/ancestralblissshaders/shaders/chunkopaque.fsh").Open());
            writer.Write("void main() { }");
        }

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(
            dataPath,
            Path.Combine(_root, "game"),
            "test");
        ShaderCompatibilityScanner.SaveReport(dataPath, report);

        bool originalEnabled = OptimumConfig.MapPageCacheEnabled;
        try
        {
            OptimumConfig.MapPageCacheEnabled = true;
            OptimumConfig.SetDataPath(dataPath);

            Assert.Contains("MapPageCache", report.DisabledFeatures);
            Assert.False(OptimumConfig.EffectiveMapPageCache);
        }
        finally
        {
            OptimumConfig.MapPageCacheEnabled = originalEnabled;
        }
    }

    [Theory]
    // Optimum's own temporal stages: an external copy of any of them is not a
    // writer that stops emitting, it is a second resolve compiled against an MRT
    // layout, history format and reactive convention it cannot know.
    [InlineData("assets/mymodshaders/shaders/taa-resolve.fsh")]
    [InlineData("assets/mymodshaders/shaders/taa-debug.vsh")]
    [InlineData("assets/mymodshaders/shaders/taa-skymotion.fsh")]
    [InlineData("assets/mymodshaders/shaders/taa-sharpen.fsh")]
    // A stage Optimum has not written yet: the "taa-" prefix rule has to cover
    // it, or every future stage ships without a scanner verdict.
    [InlineData("assets/mymodshaders/shaders/taa-somethingnew.fsh")]
    // The liquid velocity pass and the FSR pair the post-resolve sharpen shares
    // its vertex stage and lobe maths with.
    [InlineData("assets/mymodshaders/shaders/chunkliquidmotion.vsh")]
    [InlineData("assets/mymodshaders/shaders/fsr-rcas.fsh")]
    [InlineData("assets/mymodshaders/shaders/fsr-easu.vsh")]
    // ShaderRegistry merges every shaderinclude into one dictionary that all the
    // motion writers compile against, so any file in that directory can redefine
    // a helper they call - not only the vertexwarp include named in the rules.
    [InlineData("assets/mymodshaders/shaderincludes/vertexwarp.vsh")]
    [InlineData("assets/mymodshaders/shaderincludes/somehelper.vsh")]
    public void AnExternalCopyOfAnyTaaShaderDisablesTaa(string entryPath)
    {
        string dataPath = Path.Combine(_root, "data");
        string archivePath = Path.Combine(dataPath, "Mods", "SomeShaderPack.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry(entryPath).Open());
            writer.Write("void main() { }");
        }

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(
            dataPath,
            Path.Combine(_root, "game"),
            "test");

        Assert.Contains("Taa", report.DisabledFeatures);
        Assert.Contains(
            "external shader owns a motion-vector writer contract",
            report.FeatureReasons["Taa"]);
    }

    [Fact]
    public void AnUnrelatedExternalShaderLeavesTaaAlone()
    {
        string dataPath = Path.Combine(_root, "data");
        string archivePath = Path.Combine(dataPath, "Mods", "SomeShaderPack.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry(
                "assets/mymodshaders/shaders/gui.fsh").Open());
            writer.Write("void main() { }");
        }

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(
            dataPath,
            Path.Combine(_root, "game"),
            "test");

        // The veto is explicit, not a blanket "some mod ships shaders" reaction:
        // TAA stays available and only an owned contract turns it off.
        Assert.DoesNotContain("Taa", report.DisabledFeatures);
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
