using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;
using TestProgram = Optimum.Render.Vulkan.Tests.GpuTest.TestProgram;
using TestShader = Optimum.Render.Vulkan.Tests.GpuTest.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The native shader runtime seam (docs/vulkan.md): the manifest looked up at
/// <c>VulkanDevice.LinkProgram</c> by pass name and variant key, programs built from its SPIR-V with the
/// shared layout, specialization constants from the prefix, GLSL 330 initializers seeded, and a per-program
/// fall back to the rewriter.
/// </summary>
public sealed class NativeShaderRuntimeTests
{
    private const int Size = 16;
    private static readonly string[] FamilyOne = { "blit", "final", "luma" };

    private readonly ITestOutputHelper _output;

    public NativeShaderRuntimeTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ variant keys

    private static NativeProgram AllAxes() => new() { Name = "every-axis", Axes = NativeShaderBuilder.VariantAxes.ToList() };

    private static Dictionary<string, string> DefinesOf(ShaderCorpus.ShaderVariant variant) =>
        NativeShaderLibrary.ParseDefines(new[]
        {
            ShaderCorpus.PrefixFor(EnumShaderType.VertexShader, variant),
            ShaderCorpus.PrefixFor(EnumShaderType.FragmentShader, variant),
        });

    /// <summary>Every corpus variant keys as its settings say: GBUFFER from SSAOLEVEL &gt; 0, the rest from their defines.</summary>
    [Fact]
    public void TheVariantKeyOfEveryCorpusVariantFollowsItsSettings()
    {
        foreach (ShaderCorpus.ShaderVariant variant in ShaderCorpus.Variants())
        {
            string? key = NativeShaderLibrary.VariantKeyFor(AllAxes(), DefinesOf(variant), out string error);
            Assert.True(key != null, variant.Name + ": " + error);

            var expected = new Dictionary<string, int>
            {
                ["ALLOWDEPTHOFFSET"] = 0,
                ["GBUFFER"] = variant.SsaoLevel > 0 ? 1 : 0,
                ["GLOWSUB"] = 0,
                ["GREEDYMESH"] = variant.GreedyMesh,
                ["TAAMOTION"] = variant.TaaMotion,
                ["USEOIT"] = variant.UseOit,
                ["USESSBO"] = variant.UseSsbo,
                ["VEC3SCALE"] = 0,
            };
            Assert.Equal(NativeShaderManifest.VariantKey(expected.Keys, expected), key);
        }
    }

    /// <summary>
    /// The runtime key is the inverse of the parity harness's mapping from a key back to ShaderRegistry's prefix:
    /// every combination of every axis, on every harness base, comes back as the key it started from.
    /// </summary>
    [Fact]
    public void EveryAxisCombinationRoundTripsThroughThePrefixOnEveryParityBase()
    {
        string[] axes = NativeShaderBuilder.VariantAxes;
        foreach (string baseName in new[] { "everything-off", "everything-on", "taa-with-ssao" })
        {
            for (int combination = 0; combination < 1 << axes.Length; combination++)
            {
                var values = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int a = 0; a < axes.Length; a++) values[axes[a]] = (combination >> a) & 1;
                string key = NativeShaderManifest.VariantKey(axes, values);

                ShaderCorpus.ShaderVariant variant = NativeShaderParityTests.VariantFor(baseName, values);
                Assert.Equal(key, NativeShaderLibrary.VariantKeyFor(AllAxes(), DefinesOf(variant), out _));
            }
        }
    }

    /// <summary>Every variant the tree's manifest holds is the one the runtime asks for under the prefix that variant stands for.</summary>
    [SkippableFact]
    public void EveryManifestVariantKeyIsTheKeyItsPrefixProduces()
    {
        NativeShaderBuildResult build = RequireTreeBuild();
        int checkedKeys = 0;
        foreach (NativeProgram program in build.Manifest.Programs)
        {
            foreach (NativeVariant variant in program.Variants)
            {
                foreach (string baseName in new[] { "everything-off", "everything-on", "taa-with-ssao" })
                {
                    ShaderCorpus.ShaderVariant corpus = NativeShaderParityTests.VariantFor(baseName, NativeShaderParityTests.ParseKey(variant.Key));
                    string? key = NativeShaderLibrary.VariantKeyFor(program, DefinesOf(corpus), out string error);
                    Assert.True(key == variant.Key, program.Name + " [" + variant.Key + "] on " + baseName + ": got '" + key + "' " + error);
                    checkedKeys++;
                }
            }
        }
        Assert.True(checkedKeys > 0);
    }

    [Fact]
    public void AnAxisValueOutsideZeroAndOneHasNoKey()
    {
        var defines = NativeShaderLibrary.ParseDefines(new[] { "#define TAAMOTION 2\r\n" });
        Assert.Null(NativeShaderLibrary.VariantKeyFor(new NativeProgram { Axes = { "TAAMOTION" } }, defines, out string error));
        Assert.Contains("TAAMOTION", error);
    }

    /// <summary>Each specialization constant takes the value ShaderRegistry stamps into the define it replaces.</summary>
    [Fact]
    public void SpecializationConstantsTakeTheirDefinesValues()
    {
        var variant = new NativeVariant();
        foreach (SpecializationConvention.Constant constant in SpecializationConvention.Constants)
        {
            variant.SpecializationConstants.Add(new NativeSpecConstant { Id = (int)constant.Id, Name = constant.Name, Type = constant.GlslType });
        }

        foreach (ShaderCorpus.ShaderVariant corpus in ShaderCorpus.Variants())
        {
            Assert.True(NativeShaderLibrary.TryBuildSpecialization(variant, DefinesOf(corpus), out NativeSpecialization specialization, out string error), error);
            var expected = new Dictionary<string, double>
            {
                ["OPTIMUM_FXAA"] = corpus.Fxaa, ["OPTIMUM_SSAOLEVEL"] = corpus.SsaoLevel, ["OPTIMUM_NORMALVIEW"] = corpus.NormalView,
                ["OPTIMUM_BLOOM"] = corpus.Bloom, ["OPTIMUM_GODRAYS"] = corpus.GodRays, ["OPTIMUM_FOAMEFFECT"] = corpus.FoamEffect,
                ["OPTIMUM_SHINYEFFECT"] = corpus.ShinyEffect, ["OPTIMUM_SHADOWQUALITY"] = corpus.ShadowQuality,
                ["OPTIMUM_WAVINGSTUFF"] = corpus.WavingStuff, ["OPTIMUM_MINBRIGHT"] = corpus.MinBright,
                ["OPTIMUM_GREEDYMESH_GRAD"] = 0, ["OPTIMUM_DYNLIGHTS"] = corpus.DynLights,
                ["OPTIMUM_OPTIMUMAO"] = corpus.OptimumAo,
            };
            foreach (NativeSpecialization.Entry entry in specialization.Entries)
            {
                SpecializationConvention.Constant constant = SpecializationConvention.Constants.Single(c => c.Id == entry.Id);
                Assert.Equal(4u, entry.Size);
                double actual = constant.GlslType == "float"
                    ? BitConverter.ToSingle(specialization.Data, (int)entry.Offset)
                    : BitConverter.ToInt32(specialization.Data, (int)entry.Offset);
                Assert.True(Math.Abs(expected[constant.Name] - actual) < 1e-6, corpus.Name + " " + constant.Name + ": " + actual);
            }
            Assert.Equal(SpecializationConvention.Constants.Length, specialization.Entries.Length);
        }
    }

    // ------------------------------------------------------------------ loading

    [Fact]
    public void TheDisableVariableWinsOverEveryOtherSource()
    {
        Assert.Equal(NativeShaderLibrary.Mode.Off, NativeShaderLibrary.Resolve(null, "/dir", "0", "/src", "/bin").Mode);
        Assert.Equal(NativeShaderLibrary.Mode.Off, NativeShaderLibrary.Resolve(false, "/dir", null, "/src", "/bin").Mode);
        Assert.Equal((NativeShaderLibrary.Mode.Directory, "/dir"), Pick(NativeShaderLibrary.Resolve(null, "/dir", "1", "/src", "/bin")));
        Assert.Equal((NativeShaderLibrary.Mode.Source, "/src"), Pick(NativeShaderLibrary.Resolve(null, null, null, "/src", "/bin")));
        Assert.Equal((NativeShaderLibrary.Mode.Directory, Path.Combine("/bin", "shaders-vk")),
            Pick(NativeShaderLibrary.Resolve(true, null, null, null, "/bin")));

        Assert.Equal(NativeShaderLibrary.Mode.Directory, NativeShaderLibrary.Resolve(null, "/dir", "force", null, "/bin").Mode);
        Assert.True(NativeShaderLibrary.IgnoresModScan("force"));
        Assert.True(NativeShaderLibrary.IgnoresModScan(" FORCE "));
        Assert.False(NativeShaderLibrary.IgnoresModScan("0"));
        Assert.False(NativeShaderLibrary.IgnoresModScan("1"));
        Assert.False(NativeShaderLibrary.IgnoresModScan(null));

        static (NativeShaderLibrary.Mode, string?) Pick((NativeShaderLibrary.Mode Mode, string? Path, string Reason) r) => (r.Mode, r.Path);
    }

    [Fact]
    public void AManifestThatCannotBeTrustedIsRejectedWithOneReason()
    {
        string directory = TemporaryDirectory();
        try
        {
            Assert.Null(NativeShaderLibrary.Load(directory, "tool", out string missing));
            Assert.Contains("no manifest", missing);

            var manifest = new NativeShaderManifest { Toolchain = "tool" };
            string path = Path.Combine(directory, NativeShaderManifest.FileName);

            File.WriteAllText(path, manifest.ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
            Assert.Null(NativeShaderLibrary.Load(directory, "tool", out string schema));
            Assert.Contains("schema version 2", schema);

            File.WriteAllText(path, "{ not json");
            Assert.Null(NativeShaderLibrary.Load(directory, "tool", out string malformed));
            Assert.Contains("rejected", malformed);

            File.WriteAllText(path, manifest.ToJson());
            Assert.Null(NativeShaderLibrary.Load(directory, "another tool", out string toolchain));
            Assert.Contains("toolchain", toolchain);

            Assert.NotNull(NativeShaderLibrary.Load(directory, "tool", out string accepted));
            Assert.Equal("", accepted);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// A manifest built with the same compile options by another shaderc build (another platform, another package)
    /// is accepted: the per-stage SHA-256 pins the SPIR-V. The differing library is the note for the status line.
    /// Different options are still refused.
    /// </summary>
    [Fact]
    public void OnlyTheCompileOptionsOfTheToolchainMustMatch()
    {
        string directory = TemporaryDirectory();
        try
        {
            string options = ShaderCompiler.OptionsIdentity;
            var manifest = new NativeShaderManifest { Toolchain = options + ";shaderc-sha256:linux" };
            File.WriteAllText(Path.Combine(directory, NativeShaderManifest.FileName), manifest.ToJson());

            Assert.NotNull(NativeShaderLibrary.Load(directory, options + ";shaderc-sha256:linux", out string same));
            Assert.Equal("", same);

            Assert.NotNull(NativeShaderLibrary.Load(directory, options + ";silk-shaderc-2.23.0.0", out string otherLibrary));
            Assert.Contains("shaderc-sha256:linux", otherLibrary);
            Assert.Contains("silk-shaderc-2.23.0.0", otherLibrary);

            Assert.Null(NativeShaderLibrary.Load(directory, options.Replace("performance", "size") + ";shaderc-sha256:linux", out string otherOptions));
            Assert.Contains("toolchain", otherOptions);

            Assert.Equal(("a;b", "c"), NativeShaderLibrary.SplitToolchain("a;b;c"));
            Assert.Equal(("tool", ""), NativeShaderLibrary.SplitToolchain("tool"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>A SPIR-V file whose bytes are not the manifest's is refused, and stays refused.</summary>
    [SkippableFact]
    public void ASpirvFileWhoseHashDisagreesIsRefused()
    {
        NativeShaderBuildResult build = RequireFamilyOneBuild();
        string root = TemporaryDirectory();
        try
        {
            NativeShaderBuilder.Write(build, root);
            string directory = Path.Combine(root, NativeShaderManifest.DirectoryName);
            NativeShaderLibrary library = NativeShaderLibrary.Load(directory, build.Manifest.Toolchain, out string reason)!;
            Assert.True(library != null, reason);

            NativeStage stage = library!.Manifest.Find("luma", "")!.Stages.Single(s => s.Stage == "fragment");
            CorruptOneByte(Path.Combine(directory, stage.Spirv));
            Assert.False(library.TryGetSpirv(stage, out _, out string error));
            Assert.Contains("sha256", error);

            NativeStage intact = library.Manifest.Find("blit", "")!.Stages.Single(s => s.Stage == "fragment");
            Assert.True(library.TryGetSpirv(intact, out byte[] bytes, out string intactError), intactError);
            Assert.NotEmpty(bytes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TheStatsLineCarriesTheLoadCounts() =>
        Assert.Equal("stats.shaders native=3 rewritten=41 failed=1",
            Core.VulkanStats.FormatShadersLine(3, 41, 1));

    // ------------------------------------------------------------------ GPU

    /// <summary>A settings combination: the defines the post programs branch on.</summary>
    private sealed record Settings(int Fxaa, int Bloom, int SsaoLevel, int GodRays)
    {
        public ShaderCorpus.ShaderVariant ToVariant() => new()
        {
            Name = ToString(), Fxaa = Fxaa, Bloom = Bloom, SsaoLevel = SsaoLevel, GodRays = GodRays,
            TaaMotionLocation = SsaoLevel > 0 ? 4 : 2,
        };
    }

    private static readonly Settings[] Combinations =
    {
        new(0, 0, 0, 0),
        new(1, 1, 2, 1),
        new(0, 1, 1, 0),
        new(1, 0, 0, 2),
        new(0, 0, 2, 1),
    };

    /// <summary>
    /// blit, final and luma, linked natively and through the rewriter on one device, render the same pixels
    /// for fixed inputs under several settings: exact for blit and luma, within 1/255 for final. final's
    /// minlight/maxlight/minsat/maxsat and extraGamma are never set, so both paths rely on the seeded initializers
    /// (a zero maxlight divides by zero in ColorGrade).
    /// </summary>
    [SkippableFact]
    public void NativeAndRewrittenFamilyOneProgramsRenderTheSamePixels()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        NativeShaderBuildResult build = RequireFamilyOneBuild();
        string root = TemporaryDirectory();
        try
        {
            NativeShaderBuilder.Write(build, root);
            Skip.IfNot(TryCreateDevice(_output, Path.Combine(root, NativeShaderManifest.DirectoryName), null, out VulkanDevice? device),
                "No usable Vulkan device.");
            using (device)
            {
                VulkanDevice seam = device!;
                _output.WriteLine("native shaders: " + seam.NativeShaderStatus);
                int[] inputs = InputTextures(seam);
                int pairs = 0;

                foreach (Settings settings in Combinations)
                {
                    foreach (string name in FamilyOne)
                    {
                        int native = LinkGlsl330(seam, name, name, settings.ToVariant());
                        int rewritten = LinkGlsl330(seam, name, name + "-rewriter", settings.ToVariant());
                        Assert.True(seam.IsNativeProgram(native), name + " did not link natively: " + seam.GetError());
                        Assert.False(seam.IsNativeProgram(rewritten));

                        if (name == "final")
                        {
                            byte[] record = seam.ProgramRecordForTests(native)!;
                            Assert.Equal(1f, BitConverter.ToSingle(record, seam.GetUniformLocation(native, "maxlight")));
                            Assert.Equal(1f, BitConverter.ToSingle(record, seam.GetUniformLocation(native, "maxsat")));
                            Assert.Equal(1f, BitConverter.ToSingle(record, seam.GetUniformLocation(native, "extraGamma")));
                            Assert.Equal(0f, BitConverter.ToSingle(record, seam.GetUniformLocation(native, "minlight")));
                        }

                        byte[] nativePixels = Render(seam, native, name, inputs);
                        byte[] rewrittenPixels = Render(seam, rewritten, name, inputs);
                        int worst = WorstChannelDifference(nativePixels, rewrittenPixels);
                        _output.WriteLine(settings + " " + name + ": worst channel difference " + worst);
                        if (name == "final")
                        {
                            Assert.True(worst <= 1, settings + " final differs by " + worst + "/255");
                            Assert.Contains(nativePixels, b => b != 0);
                        }
                        else
                        {
                            Assert.Equal(rewrittenPixels, nativePixels);
                        }

                        seam.DeleteProgram(native);
                        seam.DeleteProgram(rewritten);
                        pairs++;
                    }
                }

                Assert.Equal(Combinations.Length * FamilyOne.Length, pairs);
                Assert.Equal((pairs, pairs, 0), seam.ShaderLinkCounts);
                AssertClean(seam);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>A variant whose SPIR-V fails its hash links through the rewriter, counts as failed, and still draws.</summary>
    [SkippableFact]
    public void ACorruptedSpirvFileFallsBackToTheRewriterAndCountsAsFailed()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        NativeShaderBuildResult build = RequireFamilyOneBuild();
        string root = TemporaryDirectory();
        try
        {
            NativeShaderBuilder.Write(build, root);
            string directory = Path.Combine(root, NativeShaderManifest.DirectoryName);
            CorruptOneByte(Path.Combine(directory, build.Manifest.Find("final", "")!.Stages.Single(s => s.Stage == "fragment").Spirv));

            Skip.IfNot(TryCreateDevice(_output, directory, null, out VulkanDevice? device), "No usable Vulkan device.");
            using (device)
            {
                VulkanDevice seam = device!;
                Settings settings = Combinations[1];
                int final = LinkGlsl330(seam, "final", "final", settings.ToVariant());
                int blit = LinkGlsl330(seam, "blit", "blit", settings.ToVariant());

                Assert.False(seam.IsNativeProgram(final));
                Assert.True(seam.IsNativeProgram(blit));
                Assert.Equal((1, 0, 1), seam.ShaderLinkCounts);

                int[] inputs = InputTextures(seam);
                Assert.Contains(Render(seam, final, "final", inputs), b => b != 0);
                AssertClean(seam);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary><c>OPTIMUM_VK_NATIVE_SHADERS=0</c> (the device setting it maps to) links every program through the rewriter.</summary>
    [SkippableFact]
    public void TurningNativeShadersOffLinksEveryProgramThroughTheRewriter()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        NativeShaderBuildResult build = RequireFamilyOneBuild();
        string root = TemporaryDirectory();
        try
        {
            NativeShaderBuilder.Write(build, root);
            Assert.Equal(NativeShaderLibrary.Mode.Off,
                NativeShaderLibrary.Resolve(null, Path.Combine(root, NativeShaderManifest.DirectoryName), "0", null, null).Mode);

            Skip.IfNot(TryCreateDevice(_output, Path.Combine(root, NativeShaderManifest.DirectoryName), false, out VulkanDevice? device),
                "No usable Vulkan device.");
            using (device)
            {
                VulkanDevice seam = device!;
                Assert.StartsWith("off", seam.NativeShaderStatus);
                foreach (string name in FamilyOne)
                {
                    Assert.False(seam.IsNativeProgram(LinkGlsl330(seam, name, name, Combinations[0].ToVariant())));
                }
                Assert.Equal((0, FamilyOne.Length, 0), seam.ShaderLinkCounts);
                AssertClean(seam);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// The launcher's mod shader scan at the seam: a program it names links through the rewriter and counts as
    /// rewritten while another links natively; a scan of "all" sends every program to the rewriter and says so in
    /// the status line; <c>OPTIMUM_VK_NATIVE_SHADERS=force</c> (the device setting it maps to) ignores the scan.
    /// </summary>
    [SkippableFact]
    public void AProgramTheModScanNamesLinksThroughTheRewriterUnlessForced()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        NativeShaderBuildResult build = RequireFamilyOneBuild();
        string root = TemporaryDirectory();
        try
        {
            NativeShaderBuilder.Write(build, root);
            string directory = Path.Combine(root, NativeShaderManifest.DirectoryName);
            ShaderCorpus.ShaderVariant variant = Combinations[1].ToVariant();
            Func<string, bool> blitOnly = name => string.Equals(name, "blit", StringComparison.OrdinalIgnoreCase);

            Skip.IfNot(TryCreateDevice(_output, directory, null, out VulkanDevice? device, blitOnly, false), "No usable Vulkan device.");
            using (device)
            {
                VulkanDevice seam = device!;
                Assert.DoesNotContain("rewriter-only", seam.NativeShaderStatus);
                int blit = LinkGlsl330(seam, "blit", "blit", variant);
                int luma = LinkGlsl330(seam, "luma", "luma", variant);
                int blitAgain = LinkGlsl330(seam, "blit", "blit", variant);
                Assert.False(seam.IsNativeProgram(blit));
                Assert.False(seam.IsNativeProgram(blitAgain));
                Assert.True(seam.IsNativeProgram(luma), seam.GetError());
                Assert.Equal((1, 2, 0), seam.ShaderLinkCounts);
                Assert.Contains(Render(seam, blit, "blit", InputTextures(seam)), b => b != 0);
                AssertClean(seam);
            }

            Skip.IfNot(TryCreateDevice(_output, directory, null, out device, _ => true, false), "No usable Vulkan device.");
            using (device)
            {
                VulkanDevice seam = device!;
                Assert.Contains("rewriter-only", seam.NativeShaderStatus);
                foreach (string name in FamilyOne)
                {
                    Assert.False(seam.IsNativeProgram(LinkGlsl330(seam, name, name, variant)));
                }
                Assert.Equal((0, FamilyOne.Length, 0), seam.ShaderLinkCounts);
                AssertClean(seam);
            }

            Skip.IfNot(TryCreateDevice(_output, directory, null, out device, _ => true, true), "No usable Vulkan device.");
            using (device)
            {
                VulkanDevice seam = device!;
                Assert.Contains("scan ignored", seam.NativeShaderStatus);
                Assert.True(seam.IsNativeProgram(LinkGlsl330(seam, "blit", "blit", variant)), seam.GetError());
                Assert.Equal((1, 0, 0), seam.ShaderLinkCounts);
                AssertClean(seam);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static readonly Lazy<(NativeShaderBuildResult? Result, string Reason)> FamilyOneBuild = new(() => BuildTree(FamilyOne));
    private static readonly Lazy<(NativeShaderBuildResult? Result, string Reason)> TreeBuild = new(() => BuildTree(null));

    private static string SourceDirectory => Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk");

    private static (NativeShaderBuildResult? Result, string Reason) BuildTree(string[]? programs)
    {
        if (!NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason)) return (null, reason);
        using (compiler)
        {
            var builder = new NativeShaderBuilder(compiler!);
            if (programs == null) return (builder.Build(SourceDirectory), "");

            var merged = new NativeShaderBuildResult();
            merged.Manifest.Toolchain = compiler!.Identity;
            foreach (string program in programs)
            {
                NativeShaderBuildResult one = builder.Build(SourceDirectory, program);
                merged.Errors.AddRange(one.Errors);
                merged.Manifest.Programs.AddRange(one.Manifest.Programs);
                foreach ((string file, byte[] bytes) in one.Files) merged.Files[file] = bytes;
            }
            return (merged, "");
        }
    }

    private static NativeShaderBuildResult RequireFamilyOneBuild() => Require(FamilyOneBuild.Value);

    private static NativeShaderBuildResult RequireTreeBuild() => Require(TreeBuild.Value);

    private static NativeShaderBuildResult Require((NativeShaderBuildResult? Result, string Reason) built)
    {
        Skip.If(built.Result == null, built.Reason);
        Assert.True(built.Result!.Success, string.Join("\n", built.Result.Errors));
        return built.Result;
    }

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "optimum-native-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CorruptOneByte(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(path, bytes);
    }

    private static bool TryCreateDevice(ITestOutputHelper output, string manifestDirectory, bool? enabled, out VulkanDevice? device,
        Func<string, bool>? modScan = null, bool? ignoreModScan = null)
    {
        VulkanDevice created = NewDevice();
        created.NativeShaderDirectory = manifestDirectory;
        created.NativeShadersEnabled = enabled;
        created.ShaderProgramOverriddenByMods = modScan;
        created.IgnoreModShaderScan = ignoreModScan;
        if (created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device = created;
            return true;
        }
        output.WriteLine("Vulkan unavailable: " + failureReason);
        created.Dispose();
        device = null;
        return false;
    }

    /// <summary>Links the GLSL 330 program <paramref name="program" /> as ShaderRegistry would, under <paramref name="passName" />.</summary>
    private static int LinkGlsl330(VulkanDevice seam, string program, string passName, ShaderCorpus.ShaderVariant variant)
    {
        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(program, ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant);
        var linked = new TestProgram { PassName = passName };
        foreach (ShaderStageSource stage in stages)
        {
            var shader = new TestShader { Type = stage.Stage, Code = stage.Code, PrefixCode = stage.PrefixCode };
            Assert.True(seam.CompileShader(shader));
            if (stage.Stage == EnumShaderType.VertexShader) linked.VertexShader = shader;
            else if (stage.Stage == EnumShaderType.FragmentShader) linked.FragmentShader = shader;
        }
        int id = seam.LinkProgram(linked);
        Assert.True(id > 0, seam.GetError() ?? "link failed");
        return id;
    }

    private static unsafe int Texture(VulkanDevice seam, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                (byte r, byte g, byte b) = pixel(x, y);
                int i = (y * Size + x) * 4;
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }
        fixed (byte* data = pixels)
        {
            return seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
        }
    }

    /// <summary>Five distinct inputs, one per final sampler, with edges for FXAA to find.</summary>
    private static int[] InputTextures(VulkanDevice seam) => new[]
    {
        Texture(seam, (x, y) => ((byte)(x * 16), (byte)(y * 16), (byte)(((x ^ y) & 1) * 200 + 20))),
        Texture(seam, (x, y) => ((byte)((x + y) % 3 * 90), 0, 0)),
        Texture(seam, (x, y) => (60, (byte)(x * 8 + 30), 120)),
        Texture(seam, (x, y) => ((byte)(y * 10), (byte)(y * 5), (byte)(x * 3))),
        Texture(seam, (x, y) => ((byte)(255 - x * 12), (byte)(200 - y * 6), 0)),
    };

    private static readonly string[] FinalSamplers = { "primaryScene", "glowParts", "bloomParts", "godrayParts", "ssaoScene" };

    private static byte[] Render(VulkanDevice seam, int program, string name, int[] inputs)
    {
        int target = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
        seam.SetDrawBuffers(framebuffer, 1);

        if (name == "final")
        {
            for (int i = 0; i < FinalSamplers.Length; i++) seam.SetSamplerUnit(program, FinalSamplers[i], i);
            SetFloats(seam, program, "invFrameSizeIn", 1f / Size, 1f / Size);
            SetFloats(seam, program, "sunPosScreenIn", 0.4f, 0.6f, 0.1f);
            SetFloats(seam, program, "sunPos3dIn", 0.3f, 0.5f, 0.2f);
            SetFloats(seam, program, "playerViewVector", 0.1f, 0.2f, 0.9f);
            seam.SetUniform(program, seam.GetUniformLocation(program, "optimumSsaoInScene"), 0);
            SetFloats(seam, program, "gammaLevel", 1.1f);
            SetFloats(seam, program, "brightnessLevel", 0.95f);
            SetFloats(seam, program, "contrastLevel", 0.05f);
            SetFloats(seam, program, "sepiaLevel", 0.1f);
            SetFloats(seam, program, "ambientBloomLevel", 0.4f);
            SetFloats(seam, program, "damageVignetting", 0.3f);
            SetFloats(seam, program, "damageVignettingSide", 0.2f);
            SetFloats(seam, program, "frostVignetting", 0.25f);
            SetFloats(seam, program, "windWaveCounter", 3f);
            SetFloats(seam, program, "glitchEffectStrength", 0.35f);
        }
        else
        {
            seam.SetSamplerUnit(program, "scene", 0);
        }

        seam.BeginFrame();
        for (int i = 0; i < inputs.Length; i++) seam.BindTexture(i, inputs[i]);
        seam.BindFramebuffer(framebuffer);
        seam.UseProgram(program);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawFullscreenTriangle();
        seam.Present();

        byte[] pixels = seam.ReadBackLevel0ForTests(target);
        seam.DeleteFramebuffer(framebuffer);
        seam.DeleteTexture(target);
        return pixels;
    }

    private static void SetFloats(VulkanDevice seam, int program, string name, params float[] values)
    {
        int location = seam.GetUniformLocation(program, name);
        Assert.True(location >= 0, name + " has no location");
        switch (values.Length)
        {
            case 1: seam.SetUniform(program, location, values[0]); break;
            case 2: seam.SetUniform(program, location, values[0], values[1]); break;
            case 3: seam.SetUniform(program, location, values[0], values[1], values[2]); break;
            default: throw new ArgumentException(name);
        }
    }

    private static int WorstChannelDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int worst = 0;
        for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }
}
