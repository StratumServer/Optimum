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
