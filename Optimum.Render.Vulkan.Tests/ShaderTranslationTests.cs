using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The gate on the whole backend: every shader the client actually loads has to
/// survive translation to SPIR-V.
///
/// If a shader cannot be translated automatically it would have to be
/// hand-ported, and hand-porting does not scale to mod shaders, which are GLSL
/// authored by third parties and only exist at runtime. So this is not a
/// nice-to-have test - a failure here means the approach does not hold.
/// </summary>
public class ShaderTranslationTests
{
    private readonly ITestOutputHelper _output;

    public ShaderTranslationTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void EveryVanillaProgramTranslatesToSpirv()
    {
        Skip.If(ShaderCorpus.AssetRoot == null,
            "No bootstrapped game assets; run scripts/bootstrap.sh or set VINTAGE_STORY_ASSETS.");

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var programs = ShaderCorpus.ProgramNames(files);

        Assert.NotEmpty(programs);

        using var compiler = new ShaderCompiler();
        var failures = new List<string>();
        int translated = 0;

        foreach (ShaderCorpus.ShaderVariant variant in ShaderCorpus.Variants())
        {
            foreach (string program in programs)
            {
                var stages = ShaderCorpus.BuildProgram(program, files, includes, variant);
                TranslatedProgram result = ShaderTranslator.Translate(stages, compiler);

                if (result.Success)
                {
                    translated++;
                    continue;
                }

                failures.Add($"[{variant.Name}] {program}: {string.Join("; ", result.Errors)}");
            }
        }

        _output.WriteLine($"{translated} program/variant combinations translated, {failures.Count} failed.");

        if (failures.Count > 0)
        {
            var report = new StringBuilder();
            report.Append(failures.Count).Append(" shader program(s) failed to translate:\n");
            foreach (string failure in failures.Take(40))
            {
                report.Append("  ").Append(failure).Append('\n');
            }
            Assert.Fail(report.ToString());
        }
    }

    /// <summary>
    /// The corpus rows above all carry USEOIT 1 and no ALLOWDEPTHOFFSET, because
    /// those are the settings every program shares. The TAA motion writers live
    /// in exactly the configurations they leave out:
    ///
    /// - entityanimated's writer is inside `#if USEOIT == 0`, which only the
    ///   opaque Entityanimated registration and ModSystemFpHands' hand shader
    ///   produce, so the corpus has never translated the entity writer at all -
    ///   including its second AnimationPrev uniform block, the only place the
    ///   backend meets two named blocks in one program;
    /// - the two-argument writer that stamps `gl_FragCoord.z + depthOffset` into
    ///   the motion alpha only exists when ALLOWDEPTHOFFSET is stamped, which
    ///   ModSystemFpHands does for its private copies of entityanimated and
    ///   standard - the first-person hands and the first-person item.
    ///
    /// Those are shipped configurations, so they belong in the translation gate.
    /// </summary>
    [SkippableFact]
    public void MotionWritersTranslateInTheConfigurationsTheClientReallyBuilds()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();

        // (program, variant) pairs the client produces with TAA on.
        var cases = new List<(string Program, ShaderCorpus.ShaderVariant Variant)>();
        foreach (int ssao in new[] { 0, 2 })
        {
            int location = ssao > 0 ? 4 : 2;

            cases.Add(("entityanimated", new ShaderCorpus.ShaderVariant
            {
                Name = $"entity-opaque-ssao{ssao}",
                UseOit = 0, SsaoLevel = ssao, DynLights = 4, ShadowQuality = 2,
                TaaMotion = 1, TaaMotionLocation = location,
            }));
            cases.Add(("entityanimated", new ShaderCorpus.ShaderVariant
            {
                Name = $"entity-fphands-ssao{ssao}",
                UseOit = 0, SsaoLevel = ssao, DynLights = 4, ShadowQuality = 2,
                TaaMotion = 1, TaaMotionLocation = location,
                ExtraPrefix = "#define ALLOWDEPTHOFFSET 1",
            }));
            cases.Add(("standard", new ShaderCorpus.ShaderVariant
            {
                Name = $"standard-fpitem-ssao{ssao}",
                SsaoLevel = ssao, DynLights = 4, ShadowQuality = 2,
                TaaMotion = 1, TaaMotionLocation = location,
                ExtraPrefix = "#define ALLOWDEPTHOFFSET 1",
            }));
            // TAA P4 review: the decal writer's SSBO branch. USESSBO tracks
            // ScreenManager.Platform.UseSSBOs, which is on by default, and it is
            // the branch where vertexPos and renderFlagsIn are locals unpacked
            // from the face buffer rather than vertex attributes - so the
            // previous-position block reads different symbols there. The corpus
            // rows that carry USESSBO 1 all carry TAAMOTION 0, so the
            // combination the client really ships was outside the gate.
            cases.Add(("decals", new ShaderCorpus.ShaderVariant
            {
                Name = $"decals-ssbo-ssao{ssao}",
                SsaoLevel = ssao, DynLights = 4, ShadowQuality = 2, UseSsbo = 1,
                TaaMotion = 1, TaaMotionLocation = location,
            }));
            cases.Add(("decals", new ShaderCorpus.ShaderVariant
            {
                Name = $"decals-nossbo-ssao{ssao}",
                SsaoLevel = ssao, DynLights = 4, ShadowQuality = 2, UseSsbo = 0,
                TaaMotion = 1, TaaMotionLocation = location,
            }));
            // TAA P4 review: the cube-particle writer's VEC3SCALE branch.
            // VSEssentials' EntityParticleSystem stamps `#define VEC3SCALE 1` on
            // its private copy of particlescube (EntityParticleSystem.cs:190),
            // which is the per-axis-scale position path - a second place the
            // twin previous-position function has to agree with vanilla's own
            // lines. No corpus row produces it.
            cases.Add(("particlescube", new ShaderCorpus.ShaderVariant
            {
                Name = $"particlescube-vec3scale-ssao{ssao}",
                SsaoLevel = ssao, DynLights = 4, ShadowQuality = 2,
                TaaMotion = 1, TaaMotionLocation = location,
                ExtraPrefix = "#define VEC3SCALE 1",
            }));
        }

        using var compiler = new ShaderCompiler();
        var failures = new List<string>();

        foreach ((string program, ShaderCorpus.ShaderVariant variant) in cases)
        {
            var stages = ShaderCorpus.BuildProgram(program, files, includes, variant);
            Assert.NotEmpty(stages);

            TranslatedProgram result = ShaderTranslator.Translate(stages, compiler);
            if (!result.Success)
            {
                failures.Add($"[{variant.Name}] {program}: {string.Join("; ", result.Errors)}");
                continue;
            }

            foreach (KeyValuePair<EnumShaderType, byte[]> stage in result.Spirv)
            {
                Assert.True(stage.Value.Length >= 20 && stage.Value.Length % 4 == 0,
                    $"{variant.Name} {program} {stage.Key}: malformed SPIR-V");
                Assert.Equal(0x07230203u, BitConverter.ToUInt32(stage.Value, 0));
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The writer only means anything if it is actually in the translated source.
    /// A define typo or a stray guard would leave every assertion above passing
    /// on a shader that emits no motion at all.
    /// </summary>
    [SkippableFact]
    public void TheEntityMotionWriterSurvivesThePreprocessorInTheOpaqueConfiguration()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = new ShaderCorpus.ShaderVariant
        {
            Name = "entity-opaque", UseOit = 0, SsaoLevel = 2, DynLights = 4,
            TaaMotion = 1, TaaMotionLocation = 4,
            ExtraPrefix = "#define ALLOWDEPTHOFFSET 1",
        };

        var stages = ShaderCorpus.BuildProgram("entityanimated", files, includes, variant);

        using var compiler = new ShaderCompiler();

        // The raw Code still carries every #if branch, so asserting on it would
        // pass even when TAAMOTION or USEOIT compile the writer out. Only the
        // preprocessed text says what the compiler actually sees.
        string vertex = Preprocess(compiler, stages, EnumShaderType.VertexShader);
        Assert.Contains("PrevElementTransforms", vertex);
        Assert.Contains("previousWarpState()", vertex);
        Assert.Contains("applyVertexWarpingState", vertex);

        string fragment = Preprocess(compiler, stages, EnumShaderType.FragmentShader);
        Assert.Contains("outMotion", fragment);
        Assert.Contains("gl_FragCoord.z + depthOffset", fragment);
    }

    /// <summary>Runs one stage through the real preprocessor and returns its text.</summary>
    private static string Preprocess(
        ShaderCompiler compiler, IReadOnlyList<ShaderStageSource> stages, EnumShaderType stage)
    {
        ShaderStageSource source = stages.Single(s => s.Stage == stage);
        ShaderCompileResult result =
            compiler.Preprocess(source.Code, source.PrefixCode, source.Filename, source.Stage);

        Assert.True(result.Success, $"{stage}: {result.Error}");
        return result.PreprocessedText;
    }

    [SkippableFact]
    public void TranslatedProgramsProduceValidSpirvForEveryStage()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = ShaderCorpus.Variants().First(v => v.Name == "everything-on");

        using var compiler = new ShaderCompiler();

        foreach (string program in ShaderCorpus.ProgramNames(files))
        {
            var stages = ShaderCorpus.BuildProgram(program, files, includes, variant);
            TranslatedProgram result = ShaderTranslator.Translate(stages, compiler);

            Assert.True(result.Success, $"{program}: {string.Join("; ", result.Errors)}");
            Assert.Equal(stages.Count, result.Spirv.Count);

            foreach (KeyValuePair<EnumShaderType, byte[]> stage in result.Spirv)
            {
                byte[] spirv = stage.Value;
                Assert.True(spirv.Length >= 20, $"{program} {stage.Key}: SPIR-V too short");
                Assert.True(spirv.Length % 4 == 0, $"{program} {stage.Key}: SPIR-V not word-aligned");

                // 0x07230203 is the SPIR-V magic number.
                uint magic = BitConverter.ToUInt32(spirv, 0);
                Assert.True(magic == 0x07230203u,
                    $"{program} {stage.Key}: bad SPIR-V magic 0x{magic:X8}");
            }
        }
    }

    /// <summary>
    /// The chunk shaders are the ones that would hurt most to hand-port: they
    /// carry the SSBO vertex-fetch path, Optimum's greedy-mesh decode, and the
    /// heaviest include graph in the game.
    /// </summary>
    [SkippableFact]
    public void ChunkShadersTranslateWithSsboAndGreedyMeshEnabled()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = ShaderCorpus.Variants().First(v => v.Name == "everything-on");

        using var compiler = new ShaderCompiler();

        foreach (string program in new[] { "chunkopaque", "chunktransparent", "chunktopsoil", "chunkliquid" })
        {
            Skip.IfNot(files.ContainsKey(program + ".vsh"), $"{program} not present");

            var stages = ShaderCorpus.BuildProgram(program, files, includes, variant);
            TranslatedProgram result = ShaderTranslator.Translate(stages, compiler);

            Assert.True(result.Success, $"{program}: {string.Join("; ", result.Errors)}");

            // The storage buffer must keep the binding the shader declared: the
            // mesh path binds the vertex buffer to that exact index.
            if (program is "chunkopaque" or "chunktransparent" or "chunktopsoil")
            {
                BlockBinding? faceData = result.Layout.StorageBlocks
                    .FirstOrDefault(b => b.BlockName == "faceDataBuf");
                Assert.NotNull(faceData);
                Assert.Equal(3, faceData!.Binding);
                Assert.True(faceData.Explicit, "declared binding should be preserved, not reassigned");
            }
        }
    }

    /// <summary>
    /// final.fsh declares "uniform float extraGamma = 1.0;" and never assigns it
    /// unless colour grading is active. GL applies declared defaults at link time,
    /// so the shadow buffer has to start out carrying them or the screen comes
    /// back black.
    /// </summary>
    [SkippableFact]
    public void DeclaredUniformDefaultsReachTheShadowBuffer()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = ShaderCorpus.Variants().First(v => v.Name == "everything-on");

        using var compiler = new ShaderCompiler();
        var stages = ShaderCorpus.BuildProgram("final", files, includes, variant);
        TranslatedProgram result = ShaderTranslator.Translate(stages, compiler);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        UniformMember member = result.Layout.MembersByName["extraGamma"];
        Assert.Equal("1.0", member.Initializer);

        byte[] shadow = result.Layout.CreateShadowBuffer();
        Assert.Equal(1.0f, BitConverter.ToSingle(shadow, member.Offset), 5);
    }

    /// <summary>
    /// A uniform named in both stages is one uniform in GL. zNear and zFar come
    /// from the fogandlight includes and appear on both sides.
    /// </summary>
    [SkippableFact]
    public void UniformsSharedBetweenStagesGetOneSlot()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = ShaderCorpus.Variants().First(v => v.Name == "everything-on");

        using var compiler = new ShaderCompiler();
        var stages = ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
        TranslatedProgram result = ShaderTranslator.Translate(stages, compiler);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        var names = result.Layout.Members.Select(m => m.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // Offsets must not overlap.
        var ordered = result.Layout.Members.OrderBy(m => m.Offset).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            Assert.True(ordered[i].Offset >= ordered[i - 1].Offset + ordered[i - 1].Size,
                $"'{ordered[i].Name}' overlaps '{ordered[i - 1].Name}'");
        }
    }
}
