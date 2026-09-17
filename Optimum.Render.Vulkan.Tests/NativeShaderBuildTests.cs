using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Optimum.Render.Vulkan.Shaders;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The offline native shader compiler, driven through the same entry point
/// <c>tools/shader-compiler</c> runs (<see cref="NativeShaderTool.Run" />), on fixture programs in a
/// temporary source tree - never in <c>sources/shaders-vk</c>. The fixtures include the committed
/// <c>bindings.glsl</c>, so the convention the tool checks is the real one.
/// </summary>
public sealed class NativeShaderBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimum-native-shaders-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "src");
    private string Output => Path.Combine(_root, "out");
    private string ShadersVk => Path.Combine(Output, NativeShaderManifest.DirectoryName);
    private string ManifestPath => Path.Combine(ShadersVk, NativeShaderManifest.FileName);

    public NativeShaderBuildTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "include"));
        File.Copy(Path.Combine(ShaderCorpus.RepositoryRoot, SetConvention.IncludePath), Path.Combine(Source, "include", "bindings.glsl"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ------------------------------------------------------------------ fixtures

    private const string OpaqueInterface = """
        layout(push_constant, scalar) uniform OptimumDraw {
            OPTIMUM_SAMPLER_SLOT(sampler2DArray, terrainTex);
            OPTIMUM_SAMPLER_SLOT(sampler2D, tex2);
            vec3 origin;
        } draw;

        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram {
            float alphaTest;
            vec4 rgbaFogIn;
        } program;
        """;

    private const string OpaqueVertex = """
        #version 450
        #extension GL_GOOGLE_include_directive : require
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        #include "fixtureopaque.interface.glsl"

        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer FaceData { uint faces[]; } faceDataBuf;

        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec2 uvIn;
        #if GREEDYMESH == 1
        layout(location = 2) in ivec2 greedySize;
        #endif
        layout(location = 0) out vec2 uv;

        void main() {
            uv = uvIn;
            gl_Position = vec4(vertexPositionIn + draw.origin, 1.0);
        }
        """;

    private const string OpaqueFragment = """
        #version 450
        #extension GL_GOOGLE_include_directive : require
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        #include "fixtureopaque.interface.glsl"
        #include "fogandlight.frag.glsl"

        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 outColor;
        #if GBUFFER == 1
        layout(location = 1) out vec4 outGlow;
        #endif
        #if TAAMOTION == 1
        layout(location = 2 + 2 * GBUFFER) out vec4 outMotion;
        #endif

        layout(constant_id = 1) const int OPTIMUM_BLOOM = 0;

        void main() {
            vec4 color = texture(optimumTextures2DArray[draw.terrainTex], vec3(uv, 0.0));
            color *= texture(optimumTextures2D[draw.tex2], uv);
            if (color.a < program.alphaTest) discard;
            outColor = mix(color, program.rgbaFogIn, fixtureFog(uv));
        #if GBUFFER == 1
            outGlow = vec4(OPTIMUM_BLOOM != 0 ? 1.0 : 0.0);
        #endif
        #if TAAMOTION == 1
            outMotion = vec4(0.0);
        #endif
        }
        """;

    private const string FogInclude = """
        #ifndef FIXTURE_FOGANDLIGHT_FRAG
        #define FIXTURE_FOGANDLIGHT_FRAG
        float fixtureFog(vec2 at) { return texture(shadowMapFar, vec3(at, 0.5)); }
        #endif
        """;

    private const string PostVertex = """
        #version 450
        void main() {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string PostFragment = """
        #version 450
        #extension GL_GOOGLE_include_directive : require
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram {
            float exposure;
        } program;
        layout(location = 0) out vec4 outColor;
        void main() { outColor = vec4(program.exposure); }
        """;

    private void WriteSource(string name, string text) => File.WriteAllText(Path.Combine(Source, name), text);

    private void WriteFixtures()
    {
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface);
        WriteSource("fixtureopaque.vert", OpaqueVertex);
        WriteSource("fixtureopaque.frag", OpaqueFragment);
        File.WriteAllText(Path.Combine(Source, "include", "fogandlight.frag.glsl"), FogInclude);
        WriteSource("fixturepost.vert", PostVertex);
        WriteSource("fixturepost.frag", PostFragment);
    }

    private (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = NativeShaderTool.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private void Build()
    {
        (int exit, _, string error) = Run("--build", Source, Output);
        Assert.True(exit == 0, error);
    }

    // ------------------------------------------------------------------ build

    [Fact]
    public void EveryAxisCombinationOfEveryProgramIsCompiledHashedAndListed()
    {
        WriteFixtures();
        Build();

        NativeShaderManifest manifest = NativeShaderManifest.Load(ManifestPath);
        Assert.Equal(NativeShaderManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        using (var compiler = new ShaderCompiler()) Assert.Equal(compiler.Identity, manifest.Toolchain);
        Assert.Equal(new[] { "fixtureopaque", "fixturepost" }, manifest.Programs.Select(p => p.Name));

        NativeProgram opaque = manifest.Programs[0];
        // GREEDYMESH comes from the vertex stage, GBUFFER and TAAMOTION from the fragment stage.
        Assert.Equal(new[] { "GBUFFER", "GREEDYMESH", "TAAMOTION" }, opaque.Axes);
        Assert.Equal(
            new[]
            {
                "GBUFFER=0,GREEDYMESH=0,TAAMOTION=0", "GBUFFER=0,GREEDYMESH=0,TAAMOTION=1",
                "GBUFFER=0,GREEDYMESH=1,TAAMOTION=0", "GBUFFER=0,GREEDYMESH=1,TAAMOTION=1",
                "GBUFFER=1,GREEDYMESH=0,TAAMOTION=0", "GBUFFER=1,GREEDYMESH=0,TAAMOTION=1",
                "GBUFFER=1,GREEDYMESH=1,TAAMOTION=0", "GBUFFER=1,GREEDYMESH=1,TAAMOTION=1",
            },
            opaque.Variants.Select(v => v.Key));

        NativeProgram post = manifest.Programs[1];
        Assert.Empty(post.Axes);
        NativeVariant postVariant = Assert.Single(post.Variants);
        Assert.Equal("", postVariant.Key);
        Assert.Equal(new[] { "fixturepost.vert.spv", "fixturepost.frag.spv" }, postVariant.Stages.Select(s => s.Spirv));

        NativeStage stage = opaque.Variants[5].Stages[1];
        Assert.Equal(("fragment", "fixtureopaque.frag", "fixtureopaque.GBUFFER1.GREEDYMESH0.TAAMOTION1.frag.spv"), (stage.Stage, stage.Source, stage.Spirv));

        var listed = manifest.Programs.SelectMany(p => p.Variants).SelectMany(v => v.Stages).ToList();
        Assert.Equal(18, listed.Count);
        foreach (NativeStage entry in listed)
        {
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(ShadersVk, entry.Spirv)))), entry.Sha256);
        }
        Assert.Equal(
            listed.Select(s => s.Spirv).Append(NativeShaderManifest.FileName).OrderBy(n => n, StringComparer.Ordinal),
            Directory.GetFiles(ShadersVk).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EachVariantRecordsItsReflectedInterface()
    {
        WriteFixtures();
        Build();
        NativeShaderManifest manifest = NativeShaderManifest.Load(ManifestPath);

        NativeVariant full = manifest.Find("fixtureopaque", "GBUFFER=1,GREEDYMESH=1,TAAMOTION=1")!;
        Assert.Equal("OptimumDraw", full.Push!.TypeName);
        Assert.Equal(20, full.Push.Size);
        Assert.Equal(new[] { "terrainTex uint @0 4", "tex2 uint @4 4", "origin vec3 @8 12" },
            full.Push.Members.Select(m => m.Name + " " + m.Type + " @" + m.Offset + " " + m.Size));
        Assert.Equal(new[] { "alphaTest float @0 4", "rgbaFogIn vec4 @4 16" },
            full.Record!.Members.Select(m => m.Name + " " + m.Type + " @" + m.Offset + " " + m.Size));

        Assert.Equal(
            new[] { "0 terrainTex sampler2DArray optimumTextures2DArray b1 @0", "1 tex2 sampler2D optimumTextures2D b0 @4" },
            full.Samplers.Select(s => s.Order + " " + s.Name + " " + s.GlslType + " " + s.BindlessArray + " b" + s.ArrayBinding + " @" + s.PushOffset));

        // fogandlight.frag.glsl stands for fogandlight.fsh, the owner of these FrameGlobals members.
        Assert.Equal(FrameGlobals.Members.Where(m => FrameGlobals.OwnerOf(m.Name) == "fogandlight.fsh").Select(m => m.Name), full.FrameMembers);
        NativeFrameTexture shadow = Assert.Single(full.FrameTextures);
        Assert.Equal(("shadowMapFar", "sampler2DShadow", 1), (shadow.Name, shadow.GlslType, shadow.Binding));

        NativeStorageBinding faces = Assert.Single(full.StorageBindings);
        Assert.Equal(("faceDataBuf", 2, 0, "storageBuffer", false), (faces.Name, faces.Set, faces.Binding, faces.DescriptorType, faces.Used));

        Assert.Equal(new[] { "0 vertexPositionIn vec3", "1 uvIn vec2", "2 greedySize ivec2" },
            full.VertexInputs.Select(v => v.Location + " " + v.Name + " " + v.Type));
        Assert.Equal(new[] { "0 outColor vec4", "1 outGlow vec4", "4 outMotion vec4" },
            full.FragmentOutputs.Select(v => v.Location + " " + v.Name + " " + v.Type));
        Assert.Equal((1u << 0) | (1u << 1) | (1u << 4), full.WrittenOutputs);

        NativeSpecConstant bloom = Assert.Single(full.SpecializationConstants);
        Assert.Equal((1, "OPTIMUM_BLOOM", "int", 0.0), (bloom.Id, bloom.Name, bloom.Type, bloom.Default));

        NativeVariant bare = manifest.Find("fixtureopaque", "GBUFFER=0,GREEDYMESH=0,TAAMOTION=1")!;
        Assert.Equal(new[] { "0 outColor", "2 outMotion" }, bare.FragmentOutputs.Select(v => v.Location + " " + v.Name));
        Assert.Equal((1u << 0) | (1u << 2), bare.WrittenOutputs);
        Assert.DoesNotContain(bare.VertexInputs, v => v.Name == "greedySize");

        NativeVariant post = manifest.Find("fixturepost", "")!;
        Assert.Null(post.Push);
        Assert.Empty(post.Samplers);
        Assert.Empty(post.FrameMembers);
        Assert.Empty(post.VertexInputs);
        Assert.Equal(1u, post.WrittenOutputs);
    }

    [Fact]
    public void AnEmptySourceTreeYieldsAValidEmptyManifest()
    {
        (int exit, string output, string error) = Run("--build", Source, Output);

        Assert.True(exit == 0, error);
        Assert.Contains("0 program(s)", output);
        NativeShaderManifest manifest = NativeShaderManifest.Load(ManifestPath);
        Assert.Empty(manifest.Programs);
        Assert.Equal(NativeShaderManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(new[] { NativeShaderManifest.FileName }, Directory.GetFiles(ShadersVk).Select(Path.GetFileName));
        Assert.Equal(0, Run("--verify", Source, Output).Exit);
    }

    [Fact]
    public void AnUnchangedRebuildRewritesNothing()
    {
        WriteFixtures();
        Build();
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.GetFiles(ShadersVk)) File.SetLastWriteTimeUtc(file, past);

        Build();

        Assert.All(Directory.GetFiles(ShadersVk), file => Assert.Equal(past, File.GetLastWriteTimeUtc(file)));
    }

    [Fact]
    public void ARebuildDeletesTheSpirvOfARemovedProgram()
    {
        WriteFixtures();
        Build();
        File.Delete(Path.Combine(Source, "fixturepost.vert"));
        File.Delete(Path.Combine(Source, "fixturepost.frag"));

        Build();

        Assert.DoesNotContain(Directory.GetFiles(ShadersVk), f => Path.GetFileName(f).StartsWith("fixturepost", StringComparison.Ordinal));
        Assert.Null(NativeShaderManifest.Load(ManifestPath).FindProgram("fixturepost"));
    }

    // ------------------------------------------------------------------ verify and single

    [Fact]
    public void VerifyPassesOnAFreshBuildAndFailsOnAnyDifference()
    {
        WriteFixtures();
        Build();
        Assert.Equal(0, Run("--verify", Source, Output).Exit);

        string blob = Path.Combine(ShadersVk, "fixturepost.frag.spv");
        byte[] original = File.ReadAllBytes(blob);
        byte[] tampered = (byte[])original.Clone();
        tampered[^1] ^= 0xFF;
        File.WriteAllBytes(blob, tampered);
        (int exit, _, string error) = Run("--verify", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains("fixturepost.frag.spv differs", error);
        File.WriteAllBytes(blob, original);

        File.WriteAllBytes(Path.Combine(ShadersVk, "leftover.vert.spv"), original);
        (exit, _, error) = Run("--verify", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains("unexpected leftover.vert.spv", error);
        File.Delete(Path.Combine(ShadersVk, "leftover.vert.spv"));

        WriteSource("fixturepost.frag", PostFragment.Replace("vec4(program.exposure)", "vec4(program.exposure * 2.0)"));
        (exit, _, error) = Run("--verify", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains(NativeShaderManifest.FileName + " differs", error);

        File.Delete(ManifestPath);
        Assert.Equal(1, Run("--verify", Source, Output).Exit);
    }

    [Fact]
    public void SingleRebuildsOneProgramAndLeavesTheOthers()
    {
        WriteFixtures();
        Build();
        string opaqueBefore = File.ReadAllText(Path.Combine(ShadersVk, "fixtureopaque.GBUFFER0.GREEDYMESH0.TAAMOTION0.frag.spv"));
        WriteSource("fixturepost.frag", PostFragment.Replace("vec4(program.exposure)", "vec4(program.exposure * 2.0)"));

        (int exit, string output, string error) = Run("--single", "fixturepost", Source, Output);

        Assert.True(exit == 0, error);
        Assert.Contains("rebuilt fixturepost", output);
        Assert.Equal(0, Run("--verify", Source, Output).Exit);
        Assert.Equal(opaqueBefore, File.ReadAllText(Path.Combine(ShadersVk, "fixtureopaque.GBUFFER0.GREEDYMESH0.TAAMOTION0.frag.spv")));
        Assert.Equal(new[] { "fixtureopaque", "fixturepost" }, NativeShaderManifest.Load(ManifestPath).Programs.Select(p => p.Name));

        Assert.Equal(1, Run("--single", "nosuchprogram", Source, Output).Exit);
    }

    [Fact]
    public void SingleNeedsAnExistingManifest()
    {
        WriteFixtures();
        (int exit, _, string error) = Run("--single", "fixturepost", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains("run --build first", error);
    }

    [Fact]
    public void AWrongCommandLineIsAUsageError()
    {
        Assert.Equal(2, Run().Exit);
        Assert.Equal(2, Run("--build", Source).Exit);
        Assert.Equal(2, Run("--single", Source, Output).Exit);
        Assert.Equal(2, Run("--frobnicate", Source, Output).Exit);
    }

    // ------------------------------------------------------------------ what the tool refuses

    private string BuildFails()
    {
        (int exit, _, string error) = Run("--build", Source, Output);
        Assert.Equal(1, exit);
        Assert.False(File.Exists(ManifestPath), "a failed build must write nothing");
        return error;
    }

    [Fact]
    public void ASamplerSlotDeclaredWithTheWrongTypeFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface.Replace("OPTIMUM_SAMPLER_SLOT(sampler2DArray, terrainTex)", "OPTIMUM_SAMPLER_SLOT(samplerCube, terrainTex)"));
        Assert.Contains("sampler slot 'terrainTex' is declared samplerCube", BuildFails());
    }

    [Fact]
    public void AnUndeclaredSlotIndexingATextureArrayFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface.Replace("OPTIMUM_SAMPLER_SLOT(sampler2D, tex2)", "uint tex2"));
        Assert.Contains("push member 'tex2' indexes a texture array but is not declared with OPTIMUM_SAMPLER_SLOT", BuildFails());
    }

    [Fact]
    public void ASamplerSlotAfterAnotherPushMemberFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface
            .Replace("    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2);\n    vec3 origin;", "    vec3 origin;\n    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2);"));
        Assert.Contains("sampler slot 'tex2' follows a non-slot push member", BuildFails());
    }

    [Fact]
    public void APushBlockOverTheLimitFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface.Replace("vec3 origin;", "vec3 origin;\n    mat4 a;\n    mat4 b;"));
        Assert.Contains("push block is 148 B, the limit is 128", BuildFails());
    }

    [Fact]
    public void ABindingOutsideTheSetConventionFails()
    {
        WriteFixtures();
        WriteSource("fixturepost.frag", PostFragment.Replace(
            "layout(location = 0) out vec4 outColor;",
            "layout(set = 1, binding = 0) uniform sampler2DArray wrong[];\nlayout(location = 0) out vec4 outColor;")
            .Replace("vec4(program.exposure)", "texture(wrong[0], vec3(0.0)) * program.exposure"));
        string error = BuildFails();
        Assert.Contains("'wrong' at set 1 binding 0 (reflected CombinedImageSampler sampler2DArray[1]) must be sampler2D optimumTextures2D[]", error);
    }

    [Fact]
    public void AnAxisTestedForDefinitionFails()
    {
        WriteFixtures();
        WriteSource("fixturepost.frag", PostFragment.Replace("layout(location = 0) out", "#ifdef TAAMOTION\n#endif\nlayout(location = 0) out"));
        Assert.Contains("#ifdef TAAMOTION", BuildFails());
    }

    [Fact]
    public void ALoneStageAMissingIncludeAndACompileErrorFail()
    {
        WriteFixtures();
        WriteSource("lonely.vert", PostVertex);
        Assert.Contains("lonely.vert has no lonely.frag", BuildFails());
        File.Delete(Path.Combine(Source, "lonely.vert"));

        WriteSource("fixturepost.frag", PostFragment.Replace("#include \"bindings.glsl\"", "#include \"bindings.glsl\"\n#include \"missing.glsl\""));
        Assert.Contains("includes 'missing.glsl'", BuildFails());

        WriteSource("fixturepost.frag", PostFragment.Replace("vec4(program.exposure)", "vec4(undeclaredName)"));
        Assert.Contains("fixturepost.frag", BuildFails());
    }

    [Fact]
    public void TheSamplerSlotMacroIsDeclaredByBindingsGlsl()
    {
        string bindings = File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, SetConvention.IncludePath));
        Assert.Contains("#define " + NativeShaderBuilder.SamplerSlotMacro + "(glslType, name) uint name", bindings);
    }

    [Fact]
    public void OwnerFilesMapToTheirNativeIncludes()
    {
        Assert.Equal(new[] { "fogandlight.vert.glsl", "fogandlight.glsl" }, NativeShaderBuilder.NativeIncludesFor("fogandlight.vsh"));
        Assert.Equal(new[] { "skycolor.frag.glsl", "skycolor.glsl" }, NativeShaderBuilder.NativeIncludesFor("skycolor.fsh"));
    }

    [Fact]
    public void TheAxisListIsTheContractsAndSorted()
    {
        Assert.Equal(
            new[] { "TAAMOTION", "GBUFFER", "USEOIT", "USESSBO", "GREEDYMESH", "ALLOWDEPTHOFFSET", "GLOWSUB", "VEC3SCALE" }.OrderBy(a => a, StringComparer.Ordinal),
            NativeShaderBuilder.VariantAxes);
    }
}
