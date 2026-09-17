using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The shared frame block: its fixed layout, the rule that decides which of a
/// program's uniforms read it, how the rewriter emits it, and - against the game's
/// own files - that the table names exactly what <c>ShaderProgramBase.Use()</c>
/// writes, with the types the includes declare.
/// </summary>
public class FrameGlobalsTests
{
    private static readonly HashSet<string> AllOwners = new(StringComparer.Ordinal)
    {
        "fogandlight.fsh", "fogandlight.vsh", "shadowcoords.vsh", "vertexwarp.vsh",
        "skycolor.fsh", "colormap.vsh", "underwatereffects.fsh",
    };

    private static ProgramInterfaceLayout LayoutOf(IReadOnlySet<string>? includes, params (EnumShaderType Stage, string Source)[] stages)
    {
        var parsed = stages.Select(s => (s.Stage, GlslParser.Parse(s.Source))).ToList();
        return ProgramInterfaceLayout.Build(parsed, null, includes);
    }

    [Fact]
    public void MembersAreScalarAlignedInOrderAndNeverOverlap()
    {
        int end = 0;
        foreach (UniformMember member in FrameGlobals.Members)
        {
            Assert.True(member.Offset >= end, member.Name + " overlaps the member before it");
            Assert.Equal(0, member.Offset % 4);
            Assert.Equal(member.Type.Size * member.ElementCount, member.Size);
            end = member.Offset + member.Size;
        }
        Assert.Equal(end, FrameGlobals.BlockSize);
        // Small enough that a snapshot per change is nothing next to a frame.
        Assert.True(FrameGlobals.BlockSize < 8192, "frame block is " + FrameGlobals.BlockSize + " bytes");
    }

    [Fact]
    public void AMemberJoinsOnlyWhenTheProgramIncludesItsOwnerAndTheDeclarationFits()
    {
        GlslType vec3 = GetType("vec3");
        GlslType floatType = GetType("float");
        var fog = new HashSet<string>(StringComparer.Ordinal) { "fogandlight.vsh" };

        Assert.True(FrameGlobals.TryPlace("pointLights", vec3, 4, fog, out _));
        Assert.True(FrameGlobals.TryPlace("pointLights", vec3, FrameGlobals.MaxDynamicLights, fog, out _));
        // Longer than the shared capacity, or not an array where the member is one.
        Assert.False(FrameGlobals.TryPlace("pointLights", vec3, FrameGlobals.MaxDynamicLights + 1, fog, out _));
        Assert.False(FrameGlobals.TryPlace("pointLights", vec3, 0, fog, out _));
        // A different type.
        Assert.False(FrameGlobals.TryPlace("pointLights", floatType, 4, fog, out _));
        // The owner is not included: the GUI program's own lightPosition, say.
        Assert.False(FrameGlobals.TryPlace("lightPosition", vec3, 0, fog, out _));
        Assert.False(FrameGlobals.TryPlace("pointLights", vec3, 4, null, out _));
        // frameSize is written outside Use() by the blur passes, so it is never shared.
        Assert.False(FrameGlobals.TryGetMember("frameSize", out _));
    }

    [Fact]
    public void AProgramThatIncludesTheOwnerReadsTheSharedBlockAndKeepsTheRestToItself()
    {
        const string vertex = """
            #version 330 core
            uniform vec3 pointLights[4];
            uniform float viewDistance;
            uniform float tint;
            void main() {}
            """;
        const string fragment = """
            #version 330 core
            uniform float viewDistance;
            out vec4 outColor;
            void main() { outColor = vec4(viewDistance); }
            """;
        var includes = new HashSet<string>(StringComparer.Ordinal) { "fogandlight.vsh" };

        ProgramInterfaceLayout layout = LayoutOf(includes,
            (EnumShaderType.VertexShader, vertex), (EnumShaderType.FragmentShader, fragment));

        Assert.True(layout.UsesFrameBlock);
        Assert.Equal(4, layout.FrameMemberDeclaredLengths["pointLights"]);
        Assert.Equal(0, layout.FrameMemberDeclaredLengths["viewDistance"]);
        Assert.Contains("viewDistance", layout.FrameMembersByStage[EnumShaderType.FragmentShader]);
        Assert.DoesNotContain("pointLights", layout.FrameMembersByStage[EnumShaderType.FragmentShader]);
        // Only the program's own uniform is left in its block.
        Assert.Equal(new[] { "tint" }, layout.Members.Select(m => m.Name));

        string code = ShaderRewriter.Rewrite(GlslParser.Parse(vertex), layout, EnumShaderType.VertexShader, emitDepthRemap: true).Code;
        FrameGlobals.TryGetMember("pointLights", out UniformMember lights);
        FrameGlobals.TryGetMember("viewDistance", out UniformMember distance);
        Assert.Contains("layout(scalar, set = 0, binding = 0) uniform OptimumFrameGlobals", code);
        Assert.Contains($"layout(offset = {lights.Offset}) vec3 pointLights[4];", code);
        Assert.Contains($"layout(offset = {distance.Offset}) float viewDistance;", code);
        Assert.Contains("layout(scalar, set = 2, binding = 3) uniform OptimumUniforms", code);
        Assert.DoesNotContain("uniform vec3 pointLights[4];", code);
    }

    [Fact]
    public void WithoutIncludesEveryUniformStaysTheProgramsOwn()
    {
        ProgramInterfaceLayout layout = LayoutOf(null, (EnumShaderType.VertexShader, """
            #version 330 core
            uniform float zNear;
            void main() {}
            """));

        Assert.False(layout.UsesFrameBlock);
        Assert.Contains("zNear", layout.MembersByName.Keys);
    }

    [Fact]
    public void TheSharedShadowStartsWithTheDeclaredDefaults()
    {
        byte[] shadow = FrameGlobals.CreateShadow();
        Assert.Equal(FrameGlobals.BlockSize, shadow.Length);
        Assert.Equal(0.3f, ReadFloat(shadow, "zNear"));
        Assert.Equal(1500f, ReadFloat(shadow, "zFar"));
        Assert.Equal(1f, ReadFloat(shadow, "windWaveIntensity"));
        FrameGlobals.TryGetMember("perceptionEffectId", out UniformMember id);
        Assert.Equal(1, BitConverter.ToInt32(shadow, id.Offset));
    }

    /// <summary>
    /// Every member is declared by its owning include with the table's type, and
    /// its array fits the shared capacity. A game update that changes one of these
    /// declarations fails here rather than reading the wrong bytes on screen.
    /// </summary>
    [SkippableFact]
    public void TheOwningIncludesDeclareEveryMemberWithTheSameType()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();

        foreach (UniformMember member in FrameGlobals.Members)
        {
            string owner = FrameGlobals.OwnerOf(member.Name)!;
            Assert.True(includes.TryGetValue(owner, out string? source), owner + " is missing");
            // Declarations can sit in a file the owner includes (fogSpheres lives in
            // fogspheres.ash); the registry records every nested include too.
            source = ShaderCorpus.ExpandIncludes(source!, includes);
            Match declaration = Regex.Match(source,
                @"uniform\s+(\w+)\s+" + Regex.Escape(member.Name) + @"\b\s*(?:\[([^\]]*)\])?");
            Assert.True(declaration.Success, owner + " does not declare " + member.Name);
            Assert.Equal(member.Type.Name, declaration.Groups[1].Value);

            string size = declaration.Groups[2].Value.Trim();
            if (member.ArrayLength == 0)
            {
                Assert.True(size.Length == 0, member.Name + " is declared as an array in " + owner);
                continue;
            }
            int declared = size == "DYNLIGHTS"
                ? FrameGlobals.MaxDynamicLights
                : size.Split('*').Select(part => int.Parse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture))
                    .Aggregate(1, (a, b) => a * b);
            Assert.True(declared <= member.ArrayLength, member.Name + " is declared longer than the shared capacity");
        }
    }

    /// <summary>
    /// Every member is written by <c>Use()</c> inside its owner's include block. A
    /// member written anywhere else would be clobbered by other programs sharing it.
    /// </summary>
    [SkippableFact]
    public void UseWritesEveryMemberInsideItsOwnersBlock()
    {
        string path = Path.Combine(ShaderCorpus.RepositoryRoot, "build", "VintagestoryLib",
            "Vintagestory.Client.NoObf", "ShaderProgramBase.cs");
        Skip.IfNot(File.Exists(path), "No bootstrapped build tree.");
        string source = File.ReadAllText(path);
        int use = source.IndexOf("public void Use()", StringComparison.Ordinal);
        Assert.True(use > 0);
        string body = source[use..source.IndexOf("public void Stop()", use, StringComparison.Ordinal)];

        foreach (UniformMember member in FrameGlobals.Members)
        {
            string owner = FrameGlobals.OwnerOf(member.Name)!;
            Assert.Contains(owner, AllOwners);
            int block = body.IndexOf("includes.Contains(\"" + owner + "\")", StringComparison.Ordinal);
            Assert.True(block > 0, "Use() has no block for " + owner);
            int next = body.IndexOf("includes.Contains(", block + 1, StringComparison.Ordinal);
            int end = next > 0 ? next : body.Length;
            Assert.True(body.IndexOf("\"" + member.Name + "\"", block, end - block, StringComparison.Ordinal) > 0,
                "Use() does not write " + member.Name + " in the " + owner + " block");
        }
    }

    /// <summary>
    /// <c>sources/shaders-vk/include/frame.glsl</c> is generated from the table. Set
    /// OPTIMUM_REGENERATE_NATIVE_INCLUDES=1 to rewrite it after changing the table; without it
    /// any difference fails, so a table change cannot ship with a stale native block.
    /// </summary>
    [Fact]
    public void TheCommittedNativeIncludeIsWhatTheTableGenerates()
    {
        string path = Path.Combine(ShaderCorpus.RepositoryRoot, FrameGlobals.IncludePath);
        string generated = FrameGlobals.GenerateInclude();
        if (Environment.GetEnvironmentVariable("OPTIMUM_REGENERATE_NATIVE_INCLUDES") == "1")
        {
            File.WriteAllText(path, generated);
        }

        Assert.True(File.Exists(path), path + " is missing; run with OPTIMUM_REGENERATE_NATIVE_INCLUDES=1");
        Assert.Equal(generated, File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The compiled block puts every member at the offset the renderer writes: the SPIR-V
    /// <c>Offset</c> decorations of the block at set 0, binding 0 are the table's offsets, member
    /// by member, and the arrays stride by their element size (scalar layout, no std140 padding).
    /// </summary>
    [SkippableFact]
    public void TheCompiledNativeBlockHasTheTablesOffsets()
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        const string probe = """
            #version 450
            #include "frame.glsl"
            layout(location = 0) out vec4 outColor;
            void main()
            {
                outColor = vec4(optimumFrame.zNear, optimumFrame.pointLights[99].x, 0.0, 1.0);
            }
            """;

        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, probe, EnumShaderType.FragmentShader, "frame-offsets-probe");
            Assert.True(result.Success, result.Error);

            SpirvReader spirv = SpirvReader.Parse(result.Spirv);
            uint? block = spirv.BlockAt((uint)FrameGlobals.Set, (uint)FrameGlobals.Binding);
            Assert.True(block.HasValue, "no block at set 0, binding 0");
            Assert.Equal(FrameGlobals.Members.Count, spirv.MemberCount(block!.Value));

            for (int i = 0; i < FrameGlobals.Members.Count; i++)
            {
                UniformMember member = FrameGlobals.Members[i];
                Assert.True(member.Offset == spirv.MemberOffset(block.Value, i),
                    $"{member.Name}: table offset {member.Offset}, SPIR-V offset {spirv.MemberOffset(block.Value, i)}");
                string? name = spirv.MemberName(block.Value, i);
                if (name != null) Assert.Equal(member.Name, name);
                if (member.ArrayLength > 0)
                {
                    Assert.Equal((uint?)member.Type.Size, spirv.ArrayStride(block.Value, i));
                }
            }
        }
    }

    private static float ReadFloat(byte[] shadow, string name)
    {
        Assert.True(FrameGlobals.TryGetMember(name, out UniformMember member));
        return BitConverter.ToSingle(shadow, member.Offset);
    }

    private static GlslType GetType(string name)
    {
        Assert.True(GlslType.TryParse(name, out GlslType type));
        return type;
    }
}
