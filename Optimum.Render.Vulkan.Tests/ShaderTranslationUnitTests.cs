using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Unit coverage for the translation pieces, independent of the game assets.
///
/// The corpus test proves the pipeline works on the real shaders, but it needs a
/// bootstrapped checkout and takes seconds. These run anywhere in milliseconds and
/// pin the specific behaviours that were wrong at least once while building it.
/// </summary>
public class ShaderTranslationUnitTests
{
    private static ParsedShader Parse(string source) => GlslParser.Parse(source);

    private static ProgramInterfaceLayout LayoutOf(params (EnumShaderType Stage, string Source)[] stages)
    {
        var parsed = stages.Select(s => (s.Stage, Parse(s.Source))).ToList();
        return ProgramInterfaceLayout.Build(parsed);
    }

    // ------------------------------------------------------------ scalar layout

    /// <summary>
    /// The reason this backend requests scalar block layout: array strides stay
    /// tight, so a float[] from the game lands in the block as a memcpy. Under
    /// std140 a vec3[] would stride 16 and every upload would need re-striding.
    /// </summary>
    [Fact]
    public void ScalarLayoutKeepsArrayStridesTight()
    {
        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, """
            #version 330 core
            uniform vec3 pointLights[4];
            uniform float density;
            void main() {}
            """));

        UniformMember lights = layout.MembersByName["pointLights"];
        UniformMember density = layout.MembersByName["density"];

        Assert.Equal(0, lights.Offset);
        Assert.Equal(4 * 12, lights.Size);
        // Tight packing: the next member starts immediately after, with no
        // rounding up to a 16-byte boundary.
        Assert.Equal(48, density.Offset);
        Assert.Equal(52, layout.BlockSize);
    }

    [Fact]
    public void MatrixTypesUseGlslColumnMajorSizes()
    {
        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, """
            #version 330 core
            uniform mat4 projection;
            uniform mat4x3 bones[2];
            uniform mat3 normalMatrix;
            void main() {}
            """));

        Assert.Equal(64, layout.MembersByName["projection"].Size);
        // matCxR is C columns of R rows: mat4x3 is 4 * 3 * 4 = 48 bytes, which is
        // exactly what UniformMatrices4x3 uploads per matrix.
        Assert.Equal(2 * 48, layout.MembersByName["bones"].Size);
        Assert.Equal(36, layout.MembersByName["normalMatrix"].Size);
    }

    // ------------------------------------------------------------------- parsing

    [Fact]
    public void ArraySizeIsAcceptedOnEitherTheTypeOrTheName()
    {
        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.FragmentShader, """
            #version 330 core
            uniform vec3[64] samples;
            uniform vec4 rects[40];
            void main() {}
            """));

        Assert.Equal(64, layout.MembersByName["samples"].ArrayLength);
        Assert.Equal(40, layout.MembersByName["rects"].ArrayLength);
    }

    /// <summary>
    /// fogandlight.vsh declares <c>uniform vec4 fogSpheres[3 * 8];</c>. GLSL
    /// allows any constant expression there.
    /// </summary>
    [Theory]
    [InlineData("8", 8)]
    [InlineData("3 * 8", 24)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("16u", 16)]
    [InlineData("-4 + 8", 4)]
    public void ConstantArraySizeExpressionsAreEvaluated(string expression, int expected)
    {
        Assert.True(GlslParser.TryEvaluateConstantInt(expression, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("MAX_LIGHTS")]
    [InlineData("3 *")]
    [InlineData("")]
    [InlineData("4 / 0")]
    public void UnresolvableArraySizesAreRejectedRatherThanGuessed(string expression)
    {
        Assert.False(GlslParser.TryEvaluateConstantInt(expression, out _));
    }

    [Fact]
    public void DeclaredUniformDefaultsArePreserved()
    {
        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.FragmentShader, """
            #version 330 core
            uniform float extraGamma = 1.0;
            uniform int steps = 3;
            void main() {}
            """));

        byte[] shadow = layout.CreateShadowBuffer();
        Assert.Equal(1.0f, BitConverter.ToSingle(shadow, layout.MembersByName["extraGamma"].Offset), 5);
        Assert.Equal(3, BitConverter.ToInt32(shadow, layout.MembersByName["steps"].Offset));
    }

    /// <summary>
    /// "void main" must read as two tokens. Accumulating identifiers across the
    /// whitespace skip once merged them into one nine-character name, so no stage
    /// ever found its entry point.
    /// </summary>
    [Fact]
    public void MainIsFoundAcrossWhitespaceAndCommentsAndArgumentForms()
    {
        Assert.True(Parse("#version 330 core\nvoid main(void) { }").HasMain);
        Assert.True(Parse("#version 330 core\nvoid  main () { }").HasMain);
        Assert.True(Parse("#version 330 core\nvoid /* c */ main() { }").HasMain);
        Assert.True(Parse("#version 330 core\nfloat helper() { return 1.0; }\nvoid main() { }").HasMain);
    }

    [Fact]
    public void FunctionsNamedLikeMainDoNotCountAsTheEntryPoint()
    {
        Assert.False(Parse("#version 330 core\nvoid mainImage() { }").HasMain);
        Assert.False(Parse("#version 330 core\nvoid domain() { }").HasMain);
    }

    // ------------------------------------------------------------------ bindings

    /// <summary>
    /// chunkopaque.vsh declares <c>layout(binding = 3, std430) readonly buffer
    /// faceDataBuf</c> and the mesh path binds the vertex buffer to that exact
    /// index, so the declared binding has to survive. It also has to be
    /// recognised at all: failing to step over "readonly" left it unclassified.
    /// </summary>
    [Fact]
    public void DeclaredStorageBufferBindingsSurviveMemoryQualifiers()
    {
        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, """
            #version 330 core
            struct FaceData { vec4 position; };
            layout(binding = 3, std430) readonly buffer faceDataBuf { FaceData faces[]; };
            void main() {}
            """));

        BlockBinding block = Assert.Single(layout.StorageBlocks);
        Assert.Equal("faceDataBuf", block.BlockName);
        Assert.Equal(3, block.Binding);
        Assert.True(block.Explicit);
        Assert.Equal(ProgramInterfaceLayout.StorageSet, block.Set);
    }

    [Fact]
    public void SamplersBecomeDescriptorsRatherThanBlockMembers()
    {
        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.FragmentShader, """
            #version 330 core
            uniform sampler2D terrainTex;
            uniform sampler2DShadow shadowMapFar;
            uniform float gamma;
            void main() {}
            """));

        Assert.Equal(2, layout.Samplers.Count);
        Assert.Equal(0, layout.SamplersByName["terrainTex"].Binding);
        Assert.Equal(1, layout.SamplersByName["shadowMapFar"].Binding);
        Assert.DoesNotContain("terrainTex", layout.MembersByName.Keys);
        Assert.Equal(4, layout.BlockSize);
    }

    // ----------------------------------------------------------------- rewriting

    private static string RewriteVertex(string source, ProgramInterfaceLayout layout) =>
        ShaderRewriter.Rewrite(Parse(source), layout, EnumShaderType.VertexShader, emitDepthRemap: true).Code;

    /// <summary>
    /// EmitVertex() snapshots gl_Position, so a geometry stage cannot be fixed
    /// up by a wrapper after main returns: the remap has to precede every emit.
    /// </summary>
    [Fact]
    public void RewritingAGeometryStageRemapsDepthBeforeEveryEmitVertex()
    {
        const string source = """
            #version 330 core
            layout(triangles) in;
            layout(triangle_strip, max_vertices = 3) out;
            void main() {
                for (int i = 0; i < 3; i++) {
                    gl_Position = gl_in[i].gl_Position;
                    EmitVertex();
                }
                EmitVertex ();
                EndPrimitive();
            }
            """;

        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.GeometryShader, source));
        RewrittenShader rewritten = ShaderRewriter.Rewrite(
            Parse(source), layout, EnumShaderType.GeometryShader, emitDepthRemap: true);
        string code = rewritten.Code;

        Assert.Empty(rewritten.Errors);
        Assert.DoesNotContain("_optimum_main", code);
        Assert.Equal(2, CountOf(code, "gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5; EmitVertex"));
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>
    /// Set 0, binding 0 belongs to the generated OptimumUniforms block. A shader
    /// that claims it for its own block would double-register that descriptor.
    /// </summary>
    [Fact]
    public void AUserBlockAtBindingZeroMovesOffTheOptimumUniformsBinding()
    {
        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, """
            #version 330 core
            layout(std140, binding = 0) uniform Lights { vec4 pos; };
            layout(std140, binding = 2) uniform Fog { vec4 colour; };
            void main() {}
            """));

        Assert.Equal(2, layout.UniformBlocks.Count);
        Assert.NotEqual(ProgramInterfaceLayout.DefaultBlockBinding, layout.UniformBlocks[0].Binding);
        Assert.NotEqual(layout.UniformBlocks[1].Binding, layout.UniformBlocks[0].Binding);
        Assert.Equal(2, layout.UniformBlocks[1].Binding);
    }

    [Fact]
    public void RewritingBumpsTheVersionAndWrapsMainForTheVulkanDepthRange()
    {
        const string source = """
            #version 330 core
            uniform float scale;
            void main(void) { gl_Position = vec4(scale, 0, 0, 1); }
            """;

        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, source));
        string code = RewriteVertex(source, layout);

        Assert.Contains("#version 450", code);
        Assert.DoesNotContain("#version 330", code);
        Assert.Contains("void _optimum_main(void)", code);
        Assert.Contains("gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;", code);

        // No Y flip: GL and Vulkan agree on how clip space maps to memory, and
        // flipping here would invert every render-to-texture round trip.
        Assert.DoesNotContain("gl_Position.y = -gl_Position.y", code);
    }

    [Fact]
    public void LooseUniformsMoveIntoTheGeneratedBlockWithExplicitOffsets()
    {
        const string source = """
            #version 330 core
            uniform float zNear;
            uniform vec3 tint;
            void main() {}
            """;

        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, source));
        string code = RewriteVertex(source, layout);

        Assert.Contains("layout(scalar, set = 0, binding = 0) uniform OptimumUniforms", code);
        Assert.Contains("layout(offset = 0) float zNear;", code);
        Assert.Contains("layout(offset = 4) vec3 tint;", code);
        // The originals are gone, so the names resolve to the block members.
        Assert.DoesNotContain("uniform float zNear;", code);
    }

    /// <summary>
    /// bilateralblur declares <c>uniform vec2 frameSize</c> in the vertex stage
    /// and <c>in vec2 frameSize</c> in the fragment stage. Emitting the union of
    /// the program's uniforms into both stages redefines the varying, so each
    /// stage gets only what it declared.
    /// </summary>
    [Fact]
    public void TheGeneratedBlockCarriesOnlyTheMembersEachStageDeclared()
    {
        const string vertex = """
            #version 330 core
            uniform vec2 frameSize;
            uniform float shared1;
            void main() {}
            """;
        const string fragment = """
            #version 330 core
            in vec2 frameSize;
            uniform float shared1;
            out vec4 outColor;
            void main() { outColor = vec4(frameSize, shared1, 1); }
            """;

        ProgramInterfaceLayout layout = LayoutOf(
            (EnumShaderType.VertexShader, vertex),
            (EnumShaderType.FragmentShader, fragment));

        string fragmentCode = ShaderRewriter
            .Rewrite(Parse(fragment), layout, EnumShaderType.FragmentShader, emitDepthRemap: false).Code;

        Assert.Contains("shared1", fragmentCode);
        // The uniform belongs to the vertex stage only; here the name is a varying.
        Assert.DoesNotContain("vec2 frameSize;\n};", fragmentCode.Replace("\r", ""));
        Assert.Contains("in vec2 frameSize;", fragmentCode);
    }

    [Fact]
    public void VaryingsGetMatchingLocationsInBothStages()
    {
        const string vertex = """
            #version 330 core
            out vec2 texCoord;
            flat out float intensity;
            void main() {}
            """;
        const string fragment = """
            #version 330 core
            in vec2 texCoord;
            flat in float intensity;
            out vec4 outColor;
            void main() { outColor = vec4(texCoord, intensity, 1); }
            """;

        ProgramInterfaceLayout layout = LayoutOf(
            (EnumShaderType.VertexShader, vertex),
            (EnumShaderType.FragmentShader, fragment));

        Assert.NotEqual(layout.VaryingLocations["texCoord"], layout.VaryingLocations["intensity"]);

        string vertexCode = ShaderRewriter
            .Rewrite(Parse(vertex), layout, EnumShaderType.VertexShader, emitDepthRemap: true).Code;
        string fragmentCode = ShaderRewriter
            .Rewrite(Parse(fragment), layout, EnumShaderType.FragmentShader, emitDepthRemap: false).Code;

        string expected = $"layout(location = {layout.VaryingLocations["texCoord"]}) out vec2 texCoord;";
        Assert.Contains(expected, vertexCode);
        Assert.Contains(expected.Replace(" out ", " in "), fragmentCode);
    }

    [Fact]
    public void ExplicitAttributeLocationsAreLeftWhereTheShaderPutThem()
    {
        const string vertex = """
            #version 330 core
            layout(location = 0) in vec3 vertexPositionIn;
            layout(location = 3) in int renderFlagsIn;
            in vec2 extra;
            void main() {}
            """;

        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, vertex));

        // The unqualified one fills the lowest free slot, around the stated ones.
        Assert.Equal(1, layout.VertexInputLocations["extra"]);
        Assert.DoesNotContain("vertexPositionIn", layout.VertexInputLocations.Keys);
    }

    [Fact]
    public void FragmentOutputsWithoutLocationsGetThem()
    {
        const string fragment = """
            #version 330 core
            out vec4 outColor;
            void main() { outColor = vec4(1); }
            """;

        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.FragmentShader, fragment));
        string code = ShaderRewriter
            .Rewrite(Parse(fragment), layout, EnumShaderType.FragmentShader, emitDepthRemap: false).Code;

        Assert.Contains("layout(location = 0) out vec4 outColor;", code);
    }

    // ------------------------------------------------------------ reserved words

    /// <summary>
    /// ssao.fsh uses "sample" as a local, which 4.00 turned into a qualifier.
    /// Vulkan forces the version bump, so the name has to move.
    /// </summary>
    [Fact]
    public void IdentifiersReservedByTheNewerLanguageAreRenamed()
    {
        string renamed = GlslReservedWords.Rename("vec3 sample = texture(t, uv).rgb; float x = sample.r;");

        Assert.DoesNotContain(" sample ", renamed);
        Assert.Contains("_optimum_kw_sample", renamed);
    }

    [Fact]
    public void VulkanSpellingsOfBuiltInsAreSubstituted()
    {
        string renamed = GlslReservedWords.Rename("int i = gl_VertexID + gl_InstanceID;");

        Assert.Equal("int i = gl_VertexIndex + gl_InstanceIndex;", renamed);
    }

    /// <summary>
    /// A word after a dot can be a struct member declared elsewhere in this
    /// class with a reserved name (see
    /// <see cref="MemberAccessOfAReservedNameMatchesItsDeclaration"/>), so it is
    /// renamed exactly like a declaration would be - leaving it alone would
    /// desync the access from the member it is meant to reach.
    /// </summary>
    [Fact]
    public void FieldsAndSwizzlesAreRenamedLikeDeclarations()
    {
        Assert.Equal("value._optimum_kw_sample = 1.0;", GlslReservedWords.Rename("value.sample = 1.0;"));
        Assert.Equal("a._optimum_kw_filter", GlslReservedWords.Rename("a.filter"));
    }

    /// <summary>
    /// A struct member declared with a reserved name and an access to that
    /// member must end up with the same renamed identifier, or the access no
    /// longer resolves to the declaration.
    /// </summary>
    [Fact]
    public void MemberAccessOfAReservedNameMatchesItsDeclaration()
    {
        string renamed = GlslReservedWords.Rename("struct S { float filter; }; void main() { S t; t.filter = 1.0; }");

        Assert.Contains("float _optimum_kw_filter;", renamed);
        Assert.Contains("t._optimum_kw_filter = 1.0;", renamed);
    }

    /// <summary>
    /// "buffer" is deliberately not renamed: it is a storage qualifier in the
    /// chunk shaders, and renaming it would break the SSBO declaration the mesh
    /// path depends on.
    /// </summary>
    [Fact]
    public void StorageQualifiersAreNotTreatedAsRenameableIdentifiers()
    {
        const string declaration = "layout(binding = 3, std430) readonly buffer faceDataBuf { vec4 f[]; };";
        Assert.Equal(declaration, GlslReservedWords.Rename(declaration));
    }

    [Fact]
    public void SourceWithNothingToRenameIsReturnedUnchanged()
    {
        const string source = "#version 330 core\nvoid main() { gl_Position = vec4(0); }";
        Assert.Same(source, GlslReservedWords.Rename(source));
    }

    // ------------------------------------------------------------ untouched code

    /// <summary>
    /// The rewriter edits spans and copies everything else verbatim, so a
    /// construct it does not model survives rather than being mangled. That is
    /// what lets third-party mod shaders through.
    /// </summary>
    [Fact]
    public void UnrecognisedConstructsPassThroughVerbatim()
    {
        const string source = """
            #version 330 core
            struct Light { vec3 position; float radius; };
            const int MAX = 4;
            float attenuate(Light l, vec3 p) { return l.radius / distance(l.position, p); }
            void main() {}
            """;

        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.VertexShader, source));
        string code = RewriteVertex(source, layout);

        Assert.Contains("struct Light { vec3 position; float radius; };", code);
        Assert.Contains("const int MAX = 4;", code);
        Assert.Contains("float attenuate(Light l, vec3 p)", code);
    }

    /// <summary>
    /// shaderc enforces a #version floor of 140 during preprocessing, before the
    /// rewriter can raise the version itself, so a lower-versioned source has to
    /// be lifted first. The client's hardcoded minimal-GUI program is written to
    /// 130 and is the reason this exists.
    /// </summary>
    [Theory]
    [InlineData("#version 130\nvoid main() {}", "#version 450")]
    [InlineData("#version 110\r\nvoid main() {}", "#version 450")]
    [InlineData("#version   120  \nvoid main() {}", "#version   450")]
    public void PreprocessingRaisesVersionsBelowShadercFloor(string source, string expected)
    {
        Assert.StartsWith(expected, ShaderCompiler.RaiseVersionForPreprocessing(source));
    }

    /// <summary>
    /// Samplers are descriptor bindings rather than members of the generated
    /// uniform block, but the client resolves every declared uniform by name and
    /// reads -1 as "the shader does not use this". Returning -1 for samplers told
    /// it that every texture uniform in the game was unused.
    /// </summary>
    [Fact]
    public void SamplersResolveToLocationsDistinctFromBlockOffsets()
    {
        const string source = """
            #version 330 core
            uniform sampler2D terrainTex;
            uniform sampler2D terrainTexLinear;
            uniform float alphaTest;
            void main() {}
            """;

        ProgramInterfaceLayout layout = LayoutOf((EnumShaderType.FragmentShader, source));

        Assert.Equal(2, layout.Samplers.Count);
        Assert.Contains(layout.Samplers, s => s.Name == "terrainTex");
        Assert.Contains(layout.Samplers, s => s.Name == "terrainTexLinear");

        // A sampler is not a block member, so it has no byte offset to hand out.
        Assert.DoesNotContain("terrainTex", layout.MembersByName.Keys);
        Assert.Contains("alphaTest", layout.MembersByName.Keys);
    }

    /// <summary>
    /// Sources already at or above the floor are left exactly as written - every
    /// vanilla shader is in this group, so translation must not shift underneath
    /// them.
    /// </summary>
    [Theory]
    [InlineData("#version 140\nvoid main() {}")]
    [InlineData("#version 330 core\nvoid main() {}")]
    [InlineData("#version 450\nvoid main() {}")]
    [InlineData("void main() {}")]
    [InlineData("")]
    public void PreprocessingLeavesAcceptableVersionsAlone(string source)
    {
        Assert.Equal(source, ShaderCompiler.RaiseVersionForPreprocessing(source));
    }
}
