using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The shared native includes (<c>sources/shaders-vk/include</c>, docs/vulkan-native-shaders.md
/// sections 1, 3 and 4): each compiles inside a probe program the way a family program will
/// include it, each port accounts for every uniform its game file declared, a verbatim port keeps
/// the game file's code token for token, a transformed one keeps every function signature, and
/// the push block and record forms of section 4 compile with their members as global names.
/// </summary>
public class NativeShaderIncludeTests
{
    public static IEnumerable<object[]> ProbeCases()
    {
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            foreach (EnumShaderType stage in NativeShaderTree.StagesOf(include))
            {
                yield return new object[] { include, stage, true };
                yield return new object[] { include, stage, false };
            }
        }
    }

    /// <summary>
    /// A probe includes bindings.glsl, frame.glsl and specialization.glsl, declares a record with
    /// every name the include (and what it includes) leaves to the program, then includes it.
    ///
    /// With owner names active the probe defines every frame owner macro, as a program whose
    /// stages include every owner would, so every frame member comes from the block. With them
    /// inactive only the includes themselves activate owners, and every other frame-ownable name
    /// is a record member, as in a program that includes the fragment half of fog and light alone.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(ProbeCases))]
    public void TheIncludeCompilesInsideAProbeProgram(string include, EnumShaderType stage, bool ownerNamesActive)
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);

        string probe = BuildProbe(include, stage, ownerNamesActive);
        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, probe, stage,
                "probe-" + include.Replace('.', '-') + (ownerNamesActive ? "-owners" : "-alone"));
            Assert.True(result.Success, include + " (" + stage + ", owner names " +
                (ownerNamesActive ? "active" : "inactive") + "):\n" + result.Error + "\n--- probe ---\n" + probe);
            Assert.NotEmpty(result.Spirv);
        }
    }

    internal static string BuildProbe(string include, EnumShaderType stage, bool ownerNamesActive)
    {
        List<string> closure = NativeShaderTree.Closure(include);
        var ports = closure.Select(NativeShaderTree.PortOf).Where(port => port != null).Select(port => port!).ToList();
        var ownersInClosure = ports.Select(port => port.FrameOwner).Where(owner => owner != null).ToHashSet(StringComparer.Ordinal);

        var probe = new StringBuilder();
        probe.Append("#version 450\n#extension GL_EXT_scalar_block_layout : require\n");
        if (ownerNamesActive)
        {
            foreach (string owner in FrameGlobals.Owners) probe.Append("#define ").Append(FrameGlobals.OwnerMacro(owner)).Append('\n');
        }
        // Variant axes a program always stamps; the probe takes the branch that declares the most.
        probe.Append("#define USEOIT 1\n");
        probe.Append("#include \"bindings.glsl\"\n#include \"frame.glsl\"\n#include \"specialization.glsl\"\n");

        var record = new List<string>();
        var symbols = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (NativeShaderTree.Port port in ports)
        {
            foreach (NativeShaderTree.Declaration uniform in port.ProgramUniforms)
            {
                if (!seen.Add(uniform.Name)) continue;
                string? owner = FrameGlobals.OwnerOf(uniform.Name);
                bool fromFrame = owner != null && (ownerNamesActive || ownersInClosure.Contains(owner));
                if (!fromFrame) record.Add("    " + uniform.Text + ";\n");
            }
            foreach (NativeShaderTree.Declaration symbol in port.ProgramSymbols)
            {
                if (seen.Add(symbol.Name)) symbols.Add(symbol.Text + ";\n");
            }
        }
        if (record.Count > 0)
        {
            probe.Append("layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram\n{\n");
            foreach (string member in record) probe.Append(member);
            probe.Append("};\n");
        }
        foreach (string symbol in symbols) probe.Append(symbol);

        probe.Append("#include \"").Append(include).Append("\"\n");
        probe.Append(stage == EnumShaderType.VertexShader
            ? "void main()\n{\n    gl_Position = vec4(0.0);\n}\n"
            : "void main()\n{\n}\n");
        return probe.ToString();
    }

    /// <summary>
    /// Every uniform the game file declares is accounted for by the port: a frame member the file
    /// owns, a frame texture from bindings.glsl, or a program uniform in the header - with the
    /// game file's type. A game update that adds a uniform fails here.
    /// </summary>
    [SkippableFact]
    public void EveryPortAccountsForEveryUniformItsGameFileDeclares()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();
        var uniform = new Regex(@"^\s*uniform\s+(\w+)\s+(\w+)", RegexOptions.Multiline);

        var ported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            NativeShaderTree.Port? port = NativeShaderTree.PortOf(include);
            if (port == null) continue;
            Assert.True(ported.Add(port.GameFile), port.GameFile + " is ported twice");
            Assert.True(includes.TryGetValue(port.GameFile, out string? source), port.GameFile + " is not a game include");

            var declared = uniform.Matches(source!).Select(m => m.Groups[1].Value + " " + m.Groups[2].Value)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();

            var accounted = new List<string>();
            foreach (UniformMember member in FrameGlobals.Members)
            {
                if (FrameGlobals.OwnerOf(member.Name) == port.GameFile && declared.Contains(member.Type.Name + " " + member.Name))
                {
                    accounted.Add(member.Type.Name + " " + member.Name);
                }
            }
            accounted.AddRange(port.FrameTextures.Select(d => d.Type + " " + d.Name));
            accounted.AddRange(port.ProgramUniforms.Select(d => d.Type + " " + d.Name));
            accounted.Sort(StringComparer.Ordinal);

            Assert.True(declared.SequenceEqual(accounted),
                $"{include}: game file declares [{string.Join(", ", declared)}], port accounts for [{string.Join(", ", accounted)}]");

            foreach (NativeShaderTree.Declaration texture in port.FrameTextures)
            {
                Assert.Contains(SetConvention.FrameTextures, binding => binding.Name == texture.Name && binding.GlslType == texture.Type);
            }
            foreach (NativeShaderTree.Declaration programUniform in port.ProgramUniforms)
            {
                Assert.False(FrameGlobals.OwnerOf(programUniform.Name) == port.GameFile,
                    include + " lists " + programUniform.Name + " as a program uniform but owns it");
            }
        }

        var expectedPorts = includes.Keys
            .Where(name => name is not ("default.fsh" or "printvalues.fsh"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(expectedPorts.SetEquals(ported),
            "ported [" + string.Join(", ", ported.OrderBy(s => s)) + "], game includes [" + string.Join(", ", expectedPorts.OrderBy(s => s)) + "]");
    }

    /// <summary>
    /// Every frame owner has exactly one native include, the port of the owner file, and that
    /// include activates its names: it defines the owner macro and includes frame.glsl.
    /// </summary>
    [Fact]
    public void EveryFrameOwnerIsActivatedByItsPortAndNoOther()
    {
        foreach (string owner in FrameGlobals.Owners)
        {
            var claiming = NativeShaderTree.IncludeNames()
                .Where(include => NativeShaderTree.PortOf(include)?.FrameOwner == owner)
                .ToList();
            Assert.True(claiming.Count == 1, owner + " is claimed by [" + string.Join(", ", claiming) + "]");

            NativeShaderTree.Port port = NativeShaderTree.PortOf(claiming[0])!;
            Assert.Equal(owner, port.GameFile);
            string text = NativeShaderTree.Read(claiming[0]);
            Assert.Contains("#define " + FrameGlobals.OwnerMacro(owner) + "\n#include \"frame.glsl\"", text);

            foreach (string other in NativeShaderTree.IncludeNames().Where(include => include != claiming[0]))
            {
                Assert.DoesNotContain("#define " + FrameGlobals.OwnerMacro(owner), NativeShaderTree.Read(other));
            }
        }
    }

    /// <summary>
    /// A verbatim port is the game file's code, token for token, once comments, preprocessor lines
    /// and interface declarations (uniform, in, out, with or without a layout) are set aside - the
    /// only things a verbatim port may change.
    /// </summary>
    [SkippableFact]
    public void VerbatimPortsKeepTheGameFilesCodeTokenForToken()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();

        int checkedPorts = 0;
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            NativeShaderTree.Port? port = NativeShaderTree.PortOf(include);
            if (port == null || port.Kind != "verbatim") continue;
            checkedPorts++;

            List<string> game = CodeTokens(includes[port.GameFile]);
            List<string> native = CodeTokens(NativeShaderTree.Read(include));
            int firstDifference = Enumerable.Range(0, Math.Min(game.Count, native.Count))
                .FirstOrDefault(i => game[i] != native[i], Math.Min(game.Count, native.Count));
            Assert.True(game.SequenceEqual(native),
                $"{include} differs from {port.GameFile} at token {firstDifference}: game '{string.Join(" ", game.Skip(firstDifference).Take(12))}', port '{string.Join(" ", native.Skip(firstDifference).Take(12))}'");
        }
        Assert.True(checkedPorts >= 10, "only " + checkedPorts + " verbatim ports found");
    }

    /// <summary>
    /// A transformed port rewrote preprocessor branches into specialization-constant branches; it
    /// keeps every function the game file defines, with the same return type, name and parameters,
    /// in the same order.
    /// </summary>
    [SkippableFact]
    public void TransformedPortsKeepEveryFunctionSignature()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();

        int checkedPorts = 0;
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            NativeShaderTree.Port? port = NativeShaderTree.PortOf(include);
            if (port == null || port.Kind != "transformed") continue;
            checkedPorts++;
            Assert.Equal(Signatures(includes[port.GameFile]), Signatures(NativeShaderTree.Read(include)));
        }
        Assert.Equal(5, checkedPorts);
    }

    /// <summary>
    /// Section 4's forms: a push block and a program record without instance names compile, and
    /// their members are global names a program's code uses directly. The record is a uniform
    /// block at set 2, binding OPTIMUM_BINDING_PROGRAM_RECORD, the push block a push-constant
    /// block; both are laid out scalar, so a vec3 after a uint sits at offset 4, not 16.
    /// </summary>
    [SkippableTheory]
    [InlineData(EnumShaderType.VertexShader)]
    [InlineData(EnumShaderType.FragmentShader)]
    public void AnonymousPushAndRecordBlocksExposeTheirMembersAsGlobalNames(EnumShaderType stage)
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        const string interfaceBlocks = """
            #version 450
            #extension GL_EXT_scalar_block_layout : require
            #include "bindings.glsl"

            layout(push_constant, scalar) uniform OptimumDraw
            {
                uint terrainTex;
                vec3 origin;
                mat4 modelViewMatrix;
            };

            layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
            {
                float alphaTest;
                vec4 rgbaFogIn;
            };

            """;
        string body = stage == EnumShaderType.VertexShader
            ? """
              layout(location = 0) in vec3 xyz;
              void main()
              {
                  gl_Position = modelViewMatrix * vec4(xyz + origin, 1.0) * rgbaFogIn.a * alphaTest + vec4(float(terrainTex));
              }
              """
            : """
              layout(location = 0) out vec4 outColor;
              void main()
              {
                  outColor = texture(optimumTextures2D[terrainTex], vec2(0.5)) * rgbaFogIn;
                  outColor.rgb += (modelViewMatrix * vec4(origin, 1.0)).rgb;
                  if (outColor.a < alphaTest) discard;
              }
              """;

        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, interfaceBlocks + body, stage, "interface-blocks-probe");
            Assert.True(result.Success, result.Error);

            SpirvReader spirv = SpirvReader.Parse(result.Spirv);
            uint? record = spirv.BlockAt((uint)SetConvention.StorageSet, (uint)SetConvention.ProgramRecordBinding);
            Assert.True(record.HasValue, "no record block at set 2, binding 3");
            Assert.Equal(new uint[] { 0, 4 }, Enumerable.Range(0, spirv.MemberCount(record!.Value)).Select(i => spirv.MemberOffset(record.Value, i)));

            List<uint> push = spirv.BlocksIn(SpirvReader.StoragePushConstant);
            Assert.Single(push);
            Assert.Equal(new uint[] { 0, 4, 16 }, Enumerable.Range(0, spirv.MemberCount(push[0])).Select(i => spirv.MemberOffset(push[0], i)));
        }
    }

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex LineComment = new(@"//[^\n]*");
    private static readonly Regex InterfaceDeclaration = new(
        @"^\s*(layout\s*\([^)]*\)\s*)?(uniform|in|out)\s+[^;{(]*;", RegexOptions.Multiline);
    private static readonly Regex Token = new(@"\w+|[^\s\w]");

    private static string StripComments(string source) =>
        LineComment.Replace(BlockComment.Replace(source.Replace("\r\n", "\n"), " "), "");

    private static List<string> CodeTokens(string source)
    {
        string text = StripComments(source);
        text = string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith("#", StringComparison.Ordinal)));
        text = InterfaceDeclaration.Replace(text, "");
        return Token.Matches(text).Select(m => m.Value).ToList();
    }

    private static readonly Regex FunctionDefinition = new(
        @"^[ \t]*(\w+)[ \t]+(\w+)[ \t]*\(([^)]*)\)\s*\{", RegexOptions.Multiline);

    private static List<string> Signatures(string source) =>
        FunctionDefinition.Matches(StripComments(source))
            .Where(m => m.Groups[1].Value is not ("if" or "for" or "while" or "switch" or "return" or "else"))
            .Select(m => m.Groups[1].Value + " " + m.Groups[2].Value + "(" +
                Regex.Replace(m.Groups[3].Value.Trim(), @"\s+", " ") + ")")
            .ToList();
}
