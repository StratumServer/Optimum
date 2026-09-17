using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Shaders;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// <c>shaders.manifest.json</c>: every field survives a write and a read, the text is deterministic
/// (the <c>--verify</c> gate compares bytes), and a manifest of another schema version is refused
/// rather than half-read.
/// </summary>
public class NativeShaderManifestTests
{
    private static NativeShaderManifest Sample() => new()
    {
        Toolchain = "glsl;vulkan1.3;spirv1.5;performance;shaderc-sha256:abc",
        Programs =
        {
            new NativeProgram
            {
                Name = "chunkopaque",
                Axes = { "GBUFFER", "TAAMOTION" },
                Variants =
                {
                    new NativeVariant
                    {
                        Key = "GBUFFER=1,TAAMOTION=1",
                        Stages =
                        {
                            new NativeStage { Stage = "vertex", Source = "chunkopaque.vert", Spirv = "chunkopaque.GBUFFER1.TAAMOTION1.vert.spv", Sha256 = "00ff" },
                            new NativeStage { Stage = "fragment", Source = "chunkopaque.frag", Spirv = "chunkopaque.GBUFFER1.TAAMOTION1.frag.spv", Sha256 = "ff00" },
                        },
                        Push = new NativeBlock
                        {
                            TypeName = "OptimumDraw",
                            Size = 20,
                            Members =
                            {
                                new NativeMember { Name = "terrainTex", Type = "uint", Offset = 0, Size = 4 },
                                new NativeMember { Name = "origin", Type = "vec3", Offset = 8, Size = 12 },
                            },
                        },
                        Record = null,
                        FrameMembers = { "zNear", "zFar" },
                        Samplers = { new NativeSampler { Name = "terrainTex", GlslType = "sampler2DArray", BindlessArray = "optimumTextures2DArray", ArrayBinding = 1, PushOffset = 0, Order = 0 } },
                        FrameTextures = { new NativeFrameTexture { Name = "shadowMapFar", GlslType = "sampler2DShadow", Binding = 1 } },
                        StorageBindings = { new NativeStorageBinding { Name = "faceDataBuf", Set = 2, Binding = 0, DescriptorType = "storageBuffer", ArrayLength = 0, RuntimeArray = false, Used = true } },
                        VertexInputs = { new NativeInterfaceVariable { Location = 0, Name = "vertexPositionIn", Type = "vec3" } },
                        FragmentOutputs =
                        {
                            new NativeInterfaceVariable { Location = 0, Name = "outColor", Type = "vec4" },
                            new NativeInterfaceVariable { Location = 4, Name = "outMotion", Type = "vec4", ArrayLength = 0 },
                        },
                        WrittenOutputs = 0b10001,
                        SpecializationConstants =
                        {
                            new NativeSpecConstant { Id = 1, Name = "OPTIMUM_FXAA", Type = "bool", Default = 1 },
                            new NativeSpecConstant { Id = 9, Name = "OPTIMUM_MINBRIGHT", Type = "float", Default = 0.125 },
                        },
                    },
                },
            },
        },
    };

    [Fact]
    public void EveryFieldSurvivesAWriteAndARead()
    {
        NativeShaderManifest original = Sample();
        string json = original.ToJson();

        NativeShaderManifest read = NativeShaderManifest.Parse(json);

        Assert.Equal(json, read.ToJson());
        Assert.Equal(original.Toolchain, read.Toolchain);
        NativeVariant variant = read.Find("chunkopaque", "GBUFFER=1,TAAMOTION=1")!;
        Assert.Equal(new[] { "GBUFFER", "TAAMOTION" }, read.Programs[0].Axes);
        Assert.Equal("chunkopaque.GBUFFER1.TAAMOTION1.frag.spv", variant.Stages[1].Spirv);
        Assert.Equal("ff00", variant.Stages[1].Sha256);
        Assert.Equal(20, variant.Push!.Size);
        Assert.Equal(("origin", "vec3", 8, 12), (variant.Push.Members[1].Name, variant.Push.Members[1].Type, variant.Push.Members[1].Offset, variant.Push.Members[1].Size));
        Assert.Null(variant.Record);
        Assert.Equal(new[] { "zNear", "zFar" }, variant.FrameMembers);
        Assert.Equal(("optimumTextures2DArray", 1, 0, 0), (variant.Samplers[0].BindlessArray, variant.Samplers[0].ArrayBinding, variant.Samplers[0].PushOffset, variant.Samplers[0].Order));
        Assert.Equal("shadowMapFar", variant.FrameTextures[0].Name);
        Assert.True(variant.StorageBindings[0].Used);
        Assert.Equal(4, variant.FragmentOutputs[1].Location);
        Assert.Equal(0b10001u, variant.WrittenOutputs);
        Assert.Equal(1.0, variant.SpecializationConstants[0].Default);
        Assert.Equal(0.125, variant.SpecializationConstants[1].Default);
        Assert.Null(read.Find("chunkopaque", "GBUFFER=0,TAAMOTION=1"));
    }

    [Fact]
    public void TheTextIsDeterministicWithUnixLineEndingsAndBoolDefaults()
    {
        string json = Sample().ToJson();

        Assert.Equal(json, Sample().ToJson());
        Assert.DoesNotContain("\r", json);
        Assert.EndsWith("}\n", json);
        Assert.Contains("\"default\": true", json);
        Assert.Contains("\"record\": null", json);
    }

    [Fact]
    public void AnotherSchemaVersionIsRefused()
    {
        string json = Sample().ToJson().Replace(
            "\"schemaVersion\": " + NativeShaderManifest.CurrentSchemaVersion,
            "\"schemaVersion\": " + (NativeShaderManifest.CurrentSchemaVersion + 1));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse(json));
        Assert.Contains("schema version " + (NativeShaderManifest.CurrentSchemaVersion + 1), error.Message);
    }

    [Fact]
    public void MalformedOrIncompleteManifestsAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse("{ not json"));
        Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse("{\"schemaVersion\": 1}"));
        Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse(Sample().ToJson().Replace("\"writtenOutputs\"", "\"writtenOutputz\"")));
    }

    [Fact]
    public void TheVariantKeyIsTheSortedAxisValueList()
    {
        var values = new Dictionary<string, int> { ["TAAMOTION"] = 1, ["GBUFFER"] = 0, ["USEOIT"] = 1 };

        Assert.Equal("GBUFFER=0,TAAMOTION=1", NativeShaderManifest.VariantKey(new[] { "TAAMOTION", "GBUFFER" }, values));
        Assert.Equal("", NativeShaderManifest.VariantKey(new string[0], values));
        Assert.Equal("GREEDYMESH=0", NativeShaderManifest.VariantKey(new[] { "GREEDYMESH" }, values));
    }
}
