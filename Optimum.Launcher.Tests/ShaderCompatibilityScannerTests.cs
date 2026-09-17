using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    // Vanilla's shaderincludes/ also carries .ash files, and ShaderRegistry loads
    // every include regardless of extension - so the stage-extension filter must
    // not apply here or an external .ash helper is invisible to the scanner.
    [InlineData("assets/mymodshaders/shaderincludes/foo.ash")]
    [InlineData("assets/mymodshaders/shaderincludes/vertexflagbits.ash")]
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

    [Theory]
    [InlineData("assets/mymodshaders/shaders/gui.fsh")]
    // Under shaders/ a non-stage extension is not a shader at all: the relaxed
    // extension rule belongs to shaderincludes/ only.
    [InlineData("assets/mymodshaders/shaders/readme.ash")]
    public void AnUnrelatedExternalShaderLeavesTaaAlone(string entryPath)
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

        // The veto is explicit, not a blanket "some mod ships shaders" reaction:
        // TAA stays available and only an owned contract turns it off.
        Assert.DoesNotContain("Taa", report.DisabledFeatures);
    }

    // ------------------------------------------------ v2: vanilla-shader overrides

    [Fact]
    public void AModOverridingChunkopaqueMarksOnlyThatProgramForTheRewriter()
    {
        string dataPath = Path.Combine(_root, "data");
        WriteArchive(Path.Combine(dataPath, "Mods", "OpaquePack.zip"),
            "assets/opaquepack/shaders/chunkopaque.fsh",
            // A new program name is the mod's own: it has no native blob to bypass.
            "assets/opaquepack/shaders/opaquepack-glow.fsh");

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(dataPath, Path.Combine(_root, "game"), "test");

        Assert.Equal(["chunkopaque"], report.RewriterPrograms);
        ShaderAssetOverride finding = Assert.Single(report.ShaderAssetOverrides);
        // NormalizeShaderPath keeps only "shaders/..." for a domain other than game.
        Assert.Equal("shaders/chunkopaque.fsh", finding.Asset);
        Assert.Equal(["chunkopaque"], finding.Programs);
        Assert.Single(finding.Owners);
        Assert.False(report.OpenGlRequired);
    }

    [Fact]
    public void AShaderincludesOverrideMarksEveryProgram()
    {
        string dataPath = Path.Combine(_root, "data");
        WriteArchive(Path.Combine(dataPath, "Mods", "FogPack.zip"),
            "assets/fogpack/shaderincludes/fogandlight.fsh",
            "assets/fogpack/shaders/sky.fsh");

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(dataPath, Path.Combine(_root, "game"), "test");

        Assert.Equal([ShaderCompatibilityScanner.AllPrograms], report.RewriterPrograms);
        Assert.Contains(report.ShaderAssetOverrides, x =>
            x.Asset == "shaderincludes/fogandlight.fsh" || x.Asset.EndsWith("shaderincludes/fogandlight.fsh", StringComparison.Ordinal));
        Assert.Contains(report.ShaderAssetOverrides, x => x.Programs.SequenceEqual([ShaderCompatibilityScanner.AllPrograms]));
        Assert.Contains(report.ShaderAssetOverrides, x => x.Programs.SequenceEqual(["sky"]));
        Assert.False(report.OpenGlRequired);
    }

    [Fact]
    public void AProgramOnlyTheInstalledGameShipsIsStillAnOverride()
    {
        string gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(gameDir, "assets", "game", "shaders"));
        File.WriteAllText(Path.Combine(gameDir, "assets", "game", "shaders", "futureprogram.vsh"), "void main() { }");
        string dataPath = Path.Combine(_root, "data");
        WriteArchive(Path.Combine(dataPath, "Mods", "FuturePack.zip"), "assets/futurepack/shaders/futureprogram.vsh");

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(dataPath, gameDir, "test");

        Assert.Equal(["futureprogram"], report.RewriterPrograms);
    }

    [Fact]
    public void AFailedScanSendsEveryProgramToTheRewriterButDoesNotVetoVulkan()
    {
        ShaderCompatibilityReport report = ShaderCompatibilityScanner.CreateConservativeReport("test", "boom");

        Assert.Equal([ShaderCompatibilityScanner.AllPrograms], report.RewriterPrograms);
        Assert.False(report.OpenGlRequired);
    }

    /// <summary>
    /// Every native program is one a mod can override by file name; a program missing
    /// from the scanner's list would link its SPIR-V over a mod's source whenever the
    /// installed game directory is not readable.
    /// </summary>
    [Fact]
    public void EveryNativeProgramIsAKnownOverridableProgram()
    {
        string shadersVk = Path.Combine(FindRepositoryRoot(), "sources", "shaders-vk");
        string[] native = Directory.EnumerateFiles(shadersVk, "*.frag")
            .Select(Path.GetFileNameWithoutExtension)
            .Cast<string>()
            .ToArray();

        Assert.NotEmpty(native);
        Assert.Empty(native.Except(ShaderCompatibilityScanner.KnownPrograms, StringComparer.OrdinalIgnoreCase));
    }

    // ------------------------------------------------ v2: PlatformInternals

    [Fact]
    public void HarmonyPlusAClientPlatformWindowsNameRoutesToOpenGl()
    {
        string dataPath = Path.Combine(_root, "data");
        string modsPath = Path.Combine(dataPath, "Mods");
        Directory.CreateDirectory(modsPath);
        File.WriteAllBytes(Path.Combine(modsPath, "PlatformPatch.dll"),
            Encoding.UTF8.GetBytes("0Harmony HarmonyLib HarmonyPatch ClientPlatformWindows RenderMesh"));

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(dataPath, Path.Combine(_root, "game"), "test");

        ShaderModSource source = Assert.Single(report.Sources);
        Assert.Contains("PlatformInternals", source.Indicators);
        Assert.DoesNotContain("RawOpenGL", source.Indicators);
        Assert.True(report.OpenGlRequired);
        Assert.Contains("Vulkan", report.DisabledFeatures);
        Assert.Equal([source.Id], report.OpenGlRequiredBy);
        Assert.Contains(report.FeatureReasons["Vulkan"], reason => reason.Contains("Harmony", StringComparison.Ordinal));
    }

    [Fact]
    public void AUtf16UserStringNamingShaderProgramBaseCountsAsAPlatformReference()
    {
        string dataPath = Path.Combine(_root, "data");
        string modsPath = Path.Combine(dataPath, "Mods");
        Directory.CreateDirectory(modsPath);
        byte[] metadata = Encoding.UTF8.GetBytes("0Harmony AccessTools ");
        // An odd prefix puts the user string at an odd offset, as the #US heap may.
        byte[] userString = [0x2B, .. Encoding.Unicode.GetBytes("Vintagestory.Client.NoObf.ShaderProgramBase:Use")];
        File.WriteAllBytes(Path.Combine(modsPath, "ReflectionPatch.dll"), [.. metadata, .. userString]);

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(dataPath, Path.Combine(_root, "game"), "test");

        Assert.Contains("PlatformInternals", Assert.Single(report.Sources).Indicators);
        Assert.True(report.OpenGlRequired);
    }

    [Theory]
    // A platform name without Harmony is a reflection read, not a patch.
    // (RegisterRenderer keeps the assembly a listed source.)
    [InlineData("ClientPlatformWindows ScreenshotHelper RegisterRenderer")]
    // Harmony on gameplay code does not touch the graphics seam.
    [InlineData("0Harmony HarmonyLib EntityBehaviorHealth OnDamage")]
    public void PlatformNamesOrHarmonyAloneStayOnVulkan(string content)
    {
        string dataPath = Path.Combine(_root, "data");
        string modsPath = Path.Combine(dataPath, "Mods");
        Directory.CreateDirectory(modsPath);
        File.WriteAllBytes(Path.Combine(modsPath, "Gameplay.dll"), Encoding.UTF8.GetBytes(content));

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(dataPath, Path.Combine(_root, "game"), "test");

        Assert.DoesNotContain("PlatformInternals", Assert.Single(report.Sources).Indicators);
        Assert.False(report.OpenGlRequired);
    }

    [Fact]
    public void ACleanModStaysOnVulkanWithNativePrograms()
    {
        string dataPath = Path.Combine(_root, "data");
        string archivePath = Path.Combine(dataPath, "Mods", "CleanMod.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (StreamWriter info = new(archive.CreateEntry("modinfo.json").Open()))
                info.Write("{\"modid\":\"cleanmod\",\"name\":\"Clean Mod\",\"version\":\"1.0.0\"}");
            using (Stream dll = archive.CreateEntry("CleanMod.dll").Open())
                dll.Write(Encoding.UTF8.GetBytes("Vintagestory.API.Common ModSystem ICoreClientAPI BlockEntity"));
        }

        ShaderCompatibilityReport report = ShaderCompatibilityScanner.Scan(dataPath, Path.Combine(_root, "game"), "test");

        Assert.Equal("cleanmod", Assert.Single(report.Sources).Id);
        Assert.False(report.OpenGlRequired);
        Assert.Empty(report.OpenGlRequiredBy);
        Assert.Empty(report.RewriterPrograms);
        Assert.Empty(report.ShaderAssetOverrides);
        Assert.DoesNotContain("Vulkan", report.DisabledFeatures);
    }

    // ------------------------------------------------ v2: schema

    [Fact]
    public void AVersion1ReportIsInvalidatedAndTheNextScanReplacesIt()
    {
        string dataPath = Path.Combine(_root, "data");
        string reportPath = Path.Combine(dataPath, ".optimum", ShaderCompatibilityScanner.ReportFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath,
            "{\"schemaVersion\":1,\"optimumVersion\":\"old\",\"scanFailed\":false,\"disabledFeatures\":[]}");

        Assert.Equal(2, ShaderCompatibilityScanner.CurrentSchemaVersion);
        Assert.Null(ShaderCompatibilityScanner.LoadReport(dataPath));

        WriteArchive(Path.Combine(dataPath, "Mods", "OpaquePack.zip"), "assets/opaquepack/shaders/chunkopaque.vsh");
        ShaderCompatibilityScanner.SaveReport(dataPath,
            ShaderCompatibilityScanner.Scan(dataPath, Path.Combine(_root, "game"), "test"));

        ShaderCompatibilityReport? loaded = ShaderCompatibilityScanner.LoadReport(dataPath);
        Assert.NotNull(loaded);
        Assert.Equal(ShaderCompatibilityScanner.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(["chunkopaque"], loaded.RewriterPrograms);
        Assert.Contains("\"rewriterPrograms\"", File.ReadAllText(reportPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"optimumVersion\":\"old\"}")]
    [InlineData("{\"schemaVersion\":\"2\"}")]
    [InlineData("{\"schemaVersion\":3}")]
    [InlineData("not json")]
    public void AReportWithoutTheCurrentSchemaVersionIsNotLoaded(string content)
    {
        string dataPath = Path.Combine(_root, "data");
        string reportPath = Path.Combine(dataPath, ".optimum", ShaderCompatibilityScanner.ReportFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, content);

        Assert.Null(ShaderCompatibilityScanner.LoadReport(dataPath));
    }

    private static void WriteArchive(string archivePath, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (string entry in entries)
        {
            using StreamWriter writer = new(archive.CreateEntry(entry).Open());
            writer.Write("void main() { }");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root with VintageStory.slnx not found.");
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
