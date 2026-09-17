using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The specialization constants exist twice, like the set convention:
/// <c>sources/shaders-vk/include/specialization.glsl</c> for native shaders and
/// <see cref="SpecializationConvention" /> for the pipeline key. A drift specializes the wrong
/// constant - shadows switched by the bloom setting - which renders wrong without failing, so the
/// two are compared constant by constant, and each constant against the define it replaces.
/// </summary>
public class SpecializationConventionTests
{
    private static readonly Regex Declaration = new(
        @"^layout\(constant_id = (\d+)\) const (\w+) (\w+) = ([^;]+);\s*$", RegexOptions.Multiline);

    /// <summary>The defines that stay variant axes or are fixed (docs/vulkan-native-shaders.md section 5).</summary>
    private static readonly string[] NotConstants =
        { "TAAMOTION", "TAAMOTIONLOCATION", "USEOIT", "USESSBO", "GREEDYMESH", "MAXANIMATEDELEMENTS" };

    [Fact]
    public void EveryDeclarationInTheIncludeIsTheConstantTheRendererUses()
    {
        var declared = Declaration.Matches(ReadInclude())
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value} = {m.Groups[4].Value}")
            .ToList();
        var expected = SpecializationConvention.Constants
            .Select(c => $"{c.Id} {c.GlslType} {c.Name} = {c.Default}")
            .ToList();
        Assert.Equal(expected, declared);
    }

    [Fact]
    public void IdsAreDenseAndNamesFollowTheDefine()
    {
        for (int i = 0; i < SpecializationConvention.Constants.Length; i++)
        {
            SpecializationConvention.Constant constant = SpecializationConvention.Constants[i];
            Assert.Equal((uint)i, constant.Id);
            Assert.Equal("OPTIMUM_" + constant.Define, constant.Name);
            Assert.True(constant.GlslType is "int" or "float", constant.Name + " has type " + constant.GlslType);
        }
    }

    /// <summary>
    /// Every define <c>registerDefaultShaderCodePrefixes</c> stamps is either a constant or one of
    /// the contract's variant axes, never both, so a new quality define cannot slip past both lists.
    /// </summary>
    [SkippableFact]
    public void EveryPrefixDefineIsExactlyOneOfConstantOrVariantAxis()
    {
        string path = Path.Combine(ShaderCorpus.RepositoryRoot, "build", "VintagestoryLib",
            "Vintagestory.Client.NoObf", "ShaderRegistry.cs");
        Skip.IfNot(File.Exists(path), "No bootstrapped build tree.");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("private static void registerDefaultShaderCodePrefixes", StringComparison.Ordinal);
        Assert.True(start > 0, "registerDefaultShaderCodePrefixes not found");
        int end = source.IndexOf("private static string HandleIncludes", start, StringComparison.Ordinal);
        string body = source[start..end];

        var stamped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(body, @"#define (\w+) ")) stamped.Add(match.Groups[1].Value);

        var constants = SpecializationConvention.Constants.Select(c => c.Define).ToHashSet(StringComparer.Ordinal);
        foreach (string define in constants)
        {
            Assert.True(stamped.Contains(define), define + " is not stamped by registerDefaultShaderCodePrefixes");
        }
        foreach (string define in stamped)
        {
            bool isConstant = constants.Contains(define);
            bool isAxis = NotConstants.Contains(define);
            Assert.True(isConstant ^ isAxis, define + (isConstant ? " is both a constant and an axis" : " is neither a constant nor an axis"));
        }
    }

    /// <summary>No native include keeps a preprocessor branch on a define a constant replaced.</summary>
    [Fact]
    public void NoNativeIncludeBranchesOnAReplacedDefineWithThePreprocessor()
    {
        var defines = SpecializationConvention.Constants.Select(c => c.Define).ToList();
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            foreach (string line in NativeShaderTree.Read(include).Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("#if", StringComparison.Ordinal) && !trimmed.StartsWith("#elif", StringComparison.Ordinal)) continue;
                foreach (string define in defines)
                {
                    Assert.False(Regex.IsMatch(trimmed, @"\b" + define + @"\b"),
                        include + " branches on " + define + " with the preprocessor: " + trimmed);
                }
            }
        }
    }

    /// <summary>The compiled module carries every constant under its id with its type.</summary>
    [SkippableFact]
    public void TheCompiledConstantsCarryTheirIdsAndTypes()
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        var probe = new StringBuilder("#version 450\n#include \"specialization.glsl\"\nlayout(location = 0) out vec4 outColor;\nvoid main()\n{\n    float sum = 0.0;\n");
        foreach (SpecializationConvention.Constant constant in SpecializationConvention.Constants)
        {
            probe.Append("    sum += float(").Append(constant.Name).Append(");\n");
        }
        probe.Append("    outColor = vec4(sum);\n}\n");

        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, probe.ToString(), EnumShaderType.FragmentShader, "specialization-probe");
            Assert.True(result.Success, result.Error);

            Dictionary<uint, string> ids = SpirvReader.Parse(result.Spirv).SpecIds();
            var expected = SpecializationConvention.Constants.ToDictionary(c => c.Id, c => c.GlslType);
            Assert.Equal(expected.OrderBy(p => p.Key), ids.OrderBy(p => p.Key));
        }
    }

    private static string ReadInclude() =>
        File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, SpecializationConvention.IncludePath)).Replace("\r\n", "\n");
}
