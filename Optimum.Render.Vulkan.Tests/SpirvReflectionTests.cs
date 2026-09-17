using System;
using System.Linq;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The SPIR-V reader the native shader manifest is built from (docs/vulkan-native-shaders.md
/// section 6). Each fixture is compiled by the runtime's own shaderc, then every reflected field is
/// compared with what the GLSL says, so a wrong offset, type or binding shows up here rather than as
/// a uniform written into the wrong bytes in game.
/// </summary>
public sealed class SpirvReflectionTests : IDisposable
{
    private readonly ShaderCompiler _compiler = new();

    public void Dispose() => _compiler.Dispose();

    private const string Fragment = """
        #version 450
        #extension GL_EXT_scalar_block_layout : require
        #extension GL_EXT_nonuniform_qualifier : require

        layout(set = 0, binding = 1) uniform sampler2DShadow shadowMapFar;
        layout(set = 1, binding = 0) uniform sampler2D optimumTextures2D[];
        layout(set = 1, binding = 1) uniform sampler2DArray optimumTextures2DArray[];
        layout(set = 1, binding = 6) uniform sampler2DShadow optimumTextures2DShadow[];

        layout(push_constant, scalar) uniform OptimumDraw {
            uint terrainTex;
            uint blockTex;
            vec3 origin;
            mat4 modelViewMatrix;
        } draw;

        layout(set = 2, binding = 3, scalar) uniform OptimumProgram {
            float alphaTest;
            vec4 rgbaFogIn;
            float weights[3];
            mat3 normalMatrix;
            ivec2 frameSize;
        } program;

        layout(set = 2, binding = 0, std430) readonly buffer FaceData { uint faces[]; } faceDataBuf;

        layout(constant_id = 3) const int OPTIMUM_BLOOM = 0;
        layout(constant_id = 7) const float OPTIMUM_MINBRIGHT = 0.25;
        layout(constant_id = 9) const bool OPTIMUM_FXAA = true;
        layout(constant_id = 11) const uint OPTIMUM_UNUSED = 5u;

        layout(location = 0) in vec2 uv;
        layout(location = 1) flat in ivec3 flags;
        layout(location = 2) in vec3 unusedVarying;

        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        layout(location = 2) out vec4 outNeverWritten;
        layout(location = 3) out vec4 outMotion;

        void main() {
            vec4 color = texture(optimumTextures2D[draw.terrainTex], uv);
            color *= texture(optimumTextures2DArray[draw.blockTex], vec3(uv, 0.0));
            if (OPTIMUM_BLOOM != 0) color *= program.alphaTest;
            color.a *= float(faceDataBuf.faces[flags.x]) * OPTIMUM_MINBRIGHT;
            color.rgb += texture(shadowMapFar, vec3(uv, 0.5)) * draw.origin;
            outColor = color;
            outGlow.rgb = program.normalMatrix * draw.origin;
            if (OPTIMUM_FXAA) outMotion = vec4(program.weights[1]);
        }
        """;

    private const string Vertex = """
        #version 450
        layout(location = 0) in vec3 vertexPosition;
        layout(location = 1) in vec2 uvIn;
        layout(location = 3) in ivec4 colorIn;
        layout(location = 4) in mat2 packedIn;
        layout(location = 0) out vec2 uv;
        void main() {
            uv = uvIn + packedIn[0] + vec2(colorIn.xy);
            gl_Position = vec4(vertexPosition, 1.0);
        }
        """;

    private (SpirvModuleReflection Declared, SpirvModuleReflection Shipped) Reflect(string source, EnumShaderType stage)
    {
        ShaderCompileResult declared = _compiler.CompileForReflection(source, "fixture", stage);
        Assert.True(declared.Success, declared.Error);
        ShaderCompileResult shipped = _compiler.Compile(source, "fixture", stage);
        Assert.True(shipped.Success, shipped.Error);
        return (SpirvReflection.Reflect(declared.Spirv), SpirvReflection.Reflect(shipped.Spirv));
    }

    [Fact]
    public void TheEntryPointAndStageAreRead()
    {
        (SpirvModuleReflection fragment, _) = Reflect(Fragment, EnumShaderType.FragmentShader);
        (SpirvModuleReflection vertex, _) = Reflect(Vertex, EnumShaderType.VertexShader);

        Assert.Equal("main", fragment.EntryPoint);
        Assert.Equal(SpirvReflection.ExecutionModelFragment, fragment.ExecutionModel);
        Assert.Equal("main", vertex.EntryPoint);
        Assert.Equal(SpirvReflection.ExecutionModelVertex, vertex.ExecutionModel);
    }

    [Fact]
    public void StageInterfacesCarryLocationNameAndTypeAndSkipBuiltIns()
    {
        (SpirvModuleReflection vertex, _) = Reflect(Vertex, EnumShaderType.VertexShader);
        (SpirvModuleReflection fragment, _) = Reflect(Fragment, EnumShaderType.FragmentShader);

        Assert.Equal(
            new[] { "0 vertexPosition vec3", "1 uvIn vec2", "3 colorIn ivec4", "4 packedIn mat2" },
            vertex.Inputs.Select(v => v.Location + " " + v.Name + " " + v.GlslType));
        // gl_Position is a built-in and never an interface variable of the manifest.
        Assert.Equal(new[] { "0 uv vec2" }, vertex.Outputs.Select(v => v.Location + " " + v.Name + " " + v.GlslType));

        Assert.Equal(
            new[] { "0 uv vec2", "1 flags ivec3", "2 unusedVarying vec3" },
            fragment.Inputs.Select(v => v.Location + " " + v.Name + " " + v.GlslType));
        Assert.Equal(
            new[] { "0 outColor vec4", "1 outGlow vec4", "2 outNeverWritten vec4", "3 outMotion vec4" },
            fragment.Outputs.Select(v => v.Location + " " + v.Name + " " + v.GlslType));
    }

    [Fact]
    public void DescriptorBindingsCarrySetBindingKindTypeArrayAndName()
    {
        (SpirvModuleReflection fragment, _) = Reflect(Fragment, EnumShaderType.FragmentShader);

        Assert.Equal(
            string.Join("\n", new[]
            {
                "0/1 shadowMapFar CombinedImageSampler sampler2DShadow len=0 runtime=False",
                "1/0 optimumTextures2D CombinedImageSampler sampler2D len=0 runtime=True",
                "1/1 optimumTextures2DArray CombinedImageSampler sampler2DArray len=0 runtime=True",
                // Declared `[]` but never indexed: glslang sizes it as a one-element array.
                "1/6 optimumTextures2DShadow CombinedImageSampler sampler2DShadow len=1 runtime=False",
                "2/0 faceDataBuf StorageBuffer FaceData len=0 runtime=False",
                "2/3 program UniformBuffer OptimumProgram len=0 runtime=False",
            }),
            string.Join("\n", fragment.Bindings.Select(b => b.Set + "/" + b.Binding + " " + b.Name + " " + b.Kind + " " + b.GlslType +
                                          " len=" + b.ArrayLength + " runtime=" + b.RuntimeArray)));

        SpirvBlockMember faces = Assert.Single(fragment.Bindings.Single(b => b.Name == "faceDataBuf").Block!.Members);
        Assert.Equal(("faces", "uint", 0, -1), (faces.Name, faces.GlslType, faces.Offset, faces.ArrayLength));
    }

    [Fact]
    public void PushConstantMembersHaveScalarLayoutOffsetsAndSizes()
    {
        (SpirvModuleReflection fragment, _) = Reflect(Fragment, EnumShaderType.FragmentShader);

        SpirvBlock push = fragment.PushConstants!;
        Assert.Equal("OptimumDraw", push.TypeName);
        Assert.Equal("draw", push.InstanceName);
        Assert.Equal(
            new[] { "terrainTex uint @0 4", "blockTex uint @4 4", "origin vec3 @8 12", "modelViewMatrix mat4 @20 64" },
            push.Members.Select(m => m.Name + " " + m.GlslType + " @" + m.Offset + " " + m.Size));
        Assert.Equal(84, push.Size);
    }

    [Fact]
    public void UniformBlockMembersHaveScalarLayoutOffsetsSizesAndArrays()
    {
        (SpirvModuleReflection fragment, _) = Reflect(Fragment, EnumShaderType.FragmentShader);

        SpirvBlock record = fragment.Bindings.Single(b => b.Name == "program").Block!;
        Assert.Equal(
            new[]
            {
                "alphaTest float @0 4 len=0",
                "rgbaFogIn vec4 @4 16 len=0",
                "weights float @20 12 len=3",
                "normalMatrix mat3 @32 36 len=0",
                "frameSize ivec2 @68 8 len=0",
            },
            record.Members.Select(m => m.Name + " " + m.GlslType + " @" + m.Offset + " " + m.Size + " len=" + m.ArrayLength));
        Assert.Equal(76, record.Size);
    }

    [Fact]
    public void SpecializationConstantsCarryIdNameTypeAndDefault()
    {
        (SpirvModuleReflection fragment, _) = Reflect(Fragment, EnumShaderType.FragmentShader);

        Assert.Equal(
            new[] { "3 OPTIMUM_BLOOM int 0", "7 OPTIMUM_MINBRIGHT float 0.25", "9 OPTIMUM_FXAA bool 1", "11 OPTIMUM_UNUSED uint 5" },
            fragment.SpecConstants.Select(c => c.SpecId + " " + c.Name + " " + c.GlslType + " " + c.DefaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TheShippedModuleTellsWhichOutputsAreWrittenAndWhichDescriptorsAreUsed()
    {
        (SpirvModuleReflection declared, SpirvModuleReflection shipped) = Reflect(Fragment, EnumShaderType.FragmentShader);

        // Written through a whole store (0), a swizzle into an access chain (1) and a spec-constant branch (3).
        Assert.Equal(new[] { 0, 1, 3 }, shipped.WrittenOutputLocations);
        Assert.Equal(new[] { 0, 1, 3 }, declared.WrittenOutputLocations);

        // The shipped module has no names but keeps set and binding: shadow array at binding 6 is unused.
        var used = shipped.Bindings.Where(b => shipped.UsedVariables.Contains(b.VariableId)).Select(b => b.Set + "/" + b.Binding);
        Assert.Equal(new[] { "0/1", "1/0", "1/1", "2/0", "2/3" }, used);
        Assert.All(shipped.Bindings, b => Assert.Equal("", b.Name));
    }

    [Fact]
    public void PushMembersAreTiedToTheArraysTheyIndex()
    {
        (_, SpirvModuleReflection shipped) = Reflect(Fragment, EnumShaderType.FragmentShader);

        Assert.Equal(new[] { 0, 1 }, shipped.PushMemberIndexes.Keys.OrderBy(k => k));
        Assert.Equal(new[] { (1, 0) }, shipped.PushMemberIndexes[0]);
        Assert.Equal(new[] { (1, 1) }, shipped.PushMemberIndexes[1]);
    }

    [Fact]
    public void AnythingButSpirvIsRejected()
    {
        Assert.Throws<FormatException>(() => SpirvReflection.Reflect(new byte[] { 1, 2, 3 }));
        Assert.Throws<FormatException>(() => SpirvReflection.Reflect(new byte[20]));
    }
}
