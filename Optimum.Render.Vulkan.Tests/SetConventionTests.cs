using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Plan decision 9's set convention exists twice: <c>sources/shaders-vk/include/bindings.glsl</c>
/// for native shaders and <see cref="SetConvention" /> for the renderer. A drift
/// between them writes a texture or buffer into a binding the shader never reads,
/// which renders black or placeholder magenta rather than failing, so they are
/// compared define by define and declaration by declaration.
/// </summary>
public class SetConventionTests
{
    private static readonly Regex Define = new(@"^\s*#define\s+(OPTIMUM_[A-Z0-9_]+)\s+(\d+)\s*$", RegexOptions.Multiline);

    private static readonly Regex Declaration = new(
        @"^\s*layout\(set = (\w+), binding = (\w+)\) uniform (\w+) (\w+)(\[\])?;\s*$", RegexOptions.Multiline);

    [Fact]
    public void EveryDefineInTheIncludeHasTheValueTheRendererUses()
    {
        var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["OPTIMUM_SET_FRAME"] = SetConvention.FrameSet,
            ["OPTIMUM_SET_TEXTURES"] = SetConvention.TextureSet,
            ["OPTIMUM_SET_STORAGE"] = SetConvention.StorageSet,
            ["OPTIMUM_PUSH_CONSTANT_BYTES"] = (int)SetConvention.PushConstantBytes,
            ["OPTIMUM_BINDING_FRAME_GLOBALS"] = SetConvention.FrameGlobalsBinding,
            ["OPTIMUM_BINDING_PROGRAM_RECORD"] = SetConvention.ProgramRecordBinding,
            ["OPTIMUM_BINDING_NAMED_BLOCK_FIRST"] = SetConvention.NamedBlockFirstBinding,
            ["OPTIMUM_BINDING_NAMED_BLOCK_LAST"] = SetConvention.NamedBlockLastBinding,
        };
        foreach (SetConvention.Binding binding in SetConvention.FrameTextures) expected[binding.Define] = binding.Value;
        foreach (SetConvention.Binding binding in SetConvention.StorageBuffers) expected[binding.Define] = binding.Value;
        foreach (SetConvention.Binding binding in SetConvention.TextureArrays)
        {
            expected[binding.Define] = binding.Value;
            expected[CapacityDefine(binding)] = (int)binding.Capacity;
        }

        Assert.Equal(expected, Defines());
    }

    [Fact]
    public void EverySamplerDeclarationSitsAtTheSetBindingAndTypeTheRendererWrites()
    {
        Dictionary<string, int> defines = Defines();
        var declared = new List<string>();
        foreach (Match match in Declaration.Matches(ReadInclude()))
        {
            declared.Add(Describe(defines[match.Groups[1].Value], defines[match.Groups[2].Value],
                match.Groups[3].Value, match.Groups[4].Value, match.Groups[5].Success));
        }

        var expected = new List<string>();
        foreach (SetConvention.Binding binding in SetConvention.FrameTextures)
        {
            expected.Add(Describe(SetConvention.FrameSet, binding.Value, binding.GlslType, binding.Name, runtimeArray: false));
        }
        foreach (SetConvention.Binding binding in SetConvention.TextureArrays)
        {
            expected.Add(Describe(SetConvention.TextureSet, binding.Value, binding.GlslType, binding.Name, runtimeArray: true));
        }

        Assert.Equal(expected, declared);
    }

    [Fact]
    public void TheConventionAgreesWithTheFrameBlockAndTheDeviceFloor()
    {
        Assert.Equal(FrameGlobals.Set, SetConvention.FrameSet);
        Assert.Equal(FrameGlobals.Binding, SetConvention.FrameGlobalsBinding);

        Assert.Equal(SumOfCapacities(SetConvention.TextureArrays), SetConvention.TextureArrayCapacityTotal);
        Assert.Equal(SetConvention.TextureArrayCapacityTotal, DescriptorIndexingFloor.BindlessSampledImages);
        Assert.True(SetConvention.FrameTextures.Length <= DescriptorIndexingFloor.FrameTextures,
            "the device floor's frame-texture headroom must cover set 0's textures");
        Assert.Equal(SetConvention.PushConstantBytes, DescriptorIndexingFloor.RequiredPushConstantBytes);
    }

    [Fact]
    public void BindingsAreUniqueWithinEachSet()
    {
        AssertUnique(SetConvention.FrameGlobalsBinding, SetConvention.FrameTextures);
        AssertUnique(null, SetConvention.TextureArrays);
        AssertUnique(SetConvention.ProgramRecordBinding, SetConvention.StorageBuffers);
        foreach (SetConvention.Binding buffer in SetConvention.StorageBuffers)
        {
            Assert.False(buffer.Value is >= SetConvention.NamedBlockFirstBinding and <= SetConvention.NamedBlockLastBinding,
                buffer.Define + " sits in the named-block range");
        }
        Assert.False(SetConvention.ProgramRecordBinding is >= SetConvention.NamedBlockFirstBinding and <= SetConvention.NamedBlockLastBinding);
        Assert.Equal(SetConvention.NamedBlockLastBinding + 1, SetConvention.StorageSetBindingCount);
        Assert.Equal(SetConvention.StorageSetBindingCount, SharedPipelineLayout.StorageBindings().Length);
    }

    /// <summary>
    /// The rewriter is the other place the convention's numbers are written down: a
    /// mod-shader program's compiled SPIR-V must name exactly the sets and bindings the
    /// shared layout declares for what it reads - frame block and frame texture in set 0,
    /// the sampler's array in set 1, FaceData, Animation, the record and a named block in
    /// set 2 - and its push block must fit the layout's range.
    /// </summary>
    [SkippableFact]
    public void TheRewritersSetsAndBindingsAreTheConventions()
    {
        const string vertex = """
            #version 330 core
            layout(binding = 3, std430) readonly buffer faceDataBuf { vec4 faces[]; };
            layout(std140) uniform Animation { mat4 values[2]; };
            layout(std140) uniform Extra { vec4 extra; };
            uniform float viewDistance;
            void main() { gl_Position = faces[0] * values[1] * extra * viewDistance; }
            """;
        const string fragment = """
            #version 330 core
            uniform sampler2DShadow shadowMapFar;
            uniform sampler2DArray terrainTex;
            uniform float alphaTest;
            out vec4 outColor;
            void main() { outColor = texture(terrainTex, vec3(alphaTest)) * texture(shadowMapFar, vec3(0.5)); }
            """;

        ShaderCompiler compiler;
        try
        {
            compiler = new ShaderCompiler();
        }
        catch (Exception error) when (error is DllNotFoundException or InvalidOperationException)
        {
            Skip.If(true, "shaderc unavailable: " + error.Message);
            return;
        }

        using (compiler)
        {
            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource { Stage = EnumShaderType.VertexShader, Code = vertex, Filename = "convention.vsh" },
                new ShaderStageSource { Stage = EnumShaderType.FragmentShader, Code = fragment, Filename = "convention.fsh" },
            }, compiler, includes: new HashSet<string>(StringComparer.Ordinal) { "fogandlight.vsh" });
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            var declared = new HashSet<(int Set, int Binding, SpirvDescriptorKind Kind)>();
            foreach (DescriptorSetLayoutBinding binding in SharedPipelineLayout.FrameBindings())
                declared.Add((SetConvention.FrameSet, (int)binding.Binding, KindOf(binding.DescriptorType)));
            foreach (SetConvention.Binding array in SetConvention.TextureArrays)
                declared.Add((SetConvention.TextureSet, array.Value, SpirvDescriptorKind.CombinedImageSampler));
            foreach (DescriptorSetLayoutBinding binding in SharedPipelineLayout.StorageBindings())
                declared.Add((SetConvention.StorageSet, (int)binding.Binding, KindOf(binding.DescriptorType)));

            var used = new HashSet<(int, int, SpirvDescriptorKind)>();
            foreach (byte[] spirv in translated.Spirv.Values)
            {
                SpirvModuleReflection reflection = SpirvReflection.Reflect(spirv);
                foreach (SpirvDescriptorBinding binding in reflection.Bindings)
                {
                    var key = (binding.Set, binding.Binding, binding.Kind);
                    Assert.True(declared.Contains(key), $"{binding.Name} at set {binding.Set} binding {binding.Binding} ({binding.Kind}) is not in the shared layout");
                    used.Add(key);
                }
            }

            Assert.Contains((SetConvention.FrameSet, SetConvention.FrameGlobalsBinding, SpirvDescriptorKind.UniformBuffer), used);
            Assert.Contains((SetConvention.FrameSet, SetConvention.FrameTextures[0].Value, SpirvDescriptorKind.CombinedImageSampler), used);
            Assert.Contains((SetConvention.TextureSet, SetConvention.TextureArrays[1].Value, SpirvDescriptorKind.CombinedImageSampler), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.FaceDataBinding, SpirvDescriptorKind.StorageBuffer), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.AnimationBinding, SpirvDescriptorKind.StorageBuffer), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.ProgramRecordBinding, SpirvDescriptorKind.UniformBuffer), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.NamedBlockFirstBinding, SpirvDescriptorKind.StorageBuffer), used);
            Assert.True(translated.Layout.PushConstantSize <= SetConvention.PushConstantBytes);
        }
    }

    private static SpirvDescriptorKind KindOf(DescriptorType type) => type switch
    {
        DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => SpirvDescriptorKind.UniformBuffer,
        DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => SpirvDescriptorKind.StorageBuffer,
        _ => SpirvDescriptorKind.CombinedImageSampler,
    };

    /// <summary>
    /// The include is real GLSL: a fragment shader that includes it and samples a
    /// frame texture and a bindless array element compiles to SPIR-V.
    /// </summary>
    [SkippableFact]
    public void TheIncludeCompilesIntoAFragmentShaderThatSamplesBothSets()
    {
        string source = "#version 450\n" + ReadInclude() + """

            layout(location = 0) out vec4 outColor;
            void main()
            {
                float lit = texture(shadowMapFar, vec3(0.5, 0.5, 0.5));
                outColor = texture(optimumTextures2D[0], vec2(0.5)) * lit + texture(sky, vec2(0.5));
            }
            """;

        ShaderCompiler compiler;
        try
        {
            compiler = new ShaderCompiler();
        }
        catch (Exception error) when (error is DllNotFoundException or InvalidOperationException)
        {
            Skip.If(true, "shaderc unavailable: " + error.Message);
            return;
        }

        using (compiler)
        {
            ShaderCompileResult result = compiler.Compile(source, "bindings-probe.frag", EnumShaderType.FragmentShader);
            Assert.True(result.Success, result.Error);
            Assert.NotEmpty(result.Spirv);
        }
    }

    /// <summary>
    /// Native sources use .vert/.frag and includes; a .vsh or .fsh there would be
    /// picked up by the packagers' sources/shaders globs as a vanilla override.
    /// </summary>
    [Fact]
    public void TheNativeShaderTreeHoldsNoGameShaderExtensions()
    {
        string tree = Path.Combine(Root(), "sources", "shaders-vk");
        Assert.True(Directory.Exists(tree), tree + " missing");
        foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            Assert.False(extension is ".vsh" or ".fsh", file + " uses a game shader extension");
        }
    }

    private static string CapacityDefine(SetConvention.Binding binding) =>
        binding.Define.Replace("OPTIMUM_BINDING_", "OPTIMUM_CAPACITY_", StringComparison.Ordinal);

    private static string Describe(int set, int binding, string type, string name, bool runtimeArray) =>
        $"set {set} binding {binding}: {type} {name}{(runtimeArray ? "[]" : "")}";

    private static uint SumOfCapacities(SetConvention.Binding[] bindings)
    {
        uint sum = 0;
        foreach (SetConvention.Binding binding in bindings) sum += binding.Capacity;
        return sum;
    }

    private static void AssertUnique(int? reserved, SetConvention.Binding[] bindings)
    {
        var seen = new HashSet<int>();
        if (reserved != null) seen.Add(reserved.Value);
        foreach (SetConvention.Binding binding in bindings)
        {
            Assert.True(seen.Add(binding.Value), $"{binding.Define} reuses binding {binding.Value}");
        }
    }

    private static Dictionary<string, int> Defines()
    {
        var defines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Define.Matches(ReadInclude()))
        {
            Assert.True(defines.TryAdd(match.Groups[1].Value, int.Parse(match.Groups[2].Value)),
                match.Groups[1].Value + " is defined twice");
        }
        return defines;
    }

    private static string ReadInclude() =>
        File.ReadAllText(Path.Combine(Root(), SetConvention.IncludePath)).Replace("\r\n", "\n");

    private static string Root()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return root;
    }
}
