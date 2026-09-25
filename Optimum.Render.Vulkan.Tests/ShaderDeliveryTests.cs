using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class ShaderDeliveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "optimum-shader-delivery-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "src");
    private string Output => Path.Combine(root, "out");
    private string Package => Path.Combine(Output, NativeShaderManifest.DirectoryName);

    public ShaderDeliveryTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "include"));
        File.Copy(Path.Combine(ShaderCorpus.RepositoryRoot, SetConvention.IncludePath), Path.Combine(Source, "include", "bindings.glsl"));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private const string Program = """
        #version 450
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        layout(push_constant, scalar) uniform Draw { vec4 tint; } draw;
        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar)
            uniform Record { float gain; } record;
        #if defined(OPTIMUM_VERTEX)
        layout(location = 0) out vec2 uv;
        void main() { uv = draw.tint.xy; gl_Position = vec4(uv, 0, 1); }
        #elif defined(OPTIMUM_FRAGMENT)
        #include "color.glsl"
        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 color;
        #if TAAMOTION == 1
        layout(location = 1) out vec4 motion;
        #endif
        void main() {
            color = fixtureColor() * draw.tint * record.gain + vec4(uv, 0, 0);
        #if TAAMOTION == 1
            motion = vec4(uv, 0, 1);
        #endif
        }
        #endif
        """;

    private void Fixtures()
    {
        File.WriteAllText(Path.Combine(Source, "fixture.glsl"), Program);
        File.WriteAllText(Path.Combine(Source, "include", "color.glsl"), "vec4 fixtureColor() { return vec4(0.2, 0.3, 0.4, 1); }");
    }

    private static NativeShaderBuildResult Build(ShaderCompiler compiler, string source)
    {
        var result = new NativeShaderBuilder(compiler).Build(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result;
    }

    [Fact]
    public void SingleSourceStagesProduceVariantsWithTheDeclaredAbi()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var build = Build(compiler, Source);
        var program = Assert.Single(build.Manifest.Programs);
        Assert.Equal(new[] { "TAAMOTION=0", "TAAMOTION=1" }, program.Variants.Select(v => v.Key));
        foreach (var variant in program.Variants)
        {
            Assert.Equal(16, variant.Push!.Size);
            Assert.Equal(4, variant.Record!.Size);
            Assert.Equal(new[] { 0 }, variant.Push.Members.Select(m => m.Offset));
            Assert.Equal(variant.Key.EndsWith("1") ? 2 : 1, variant.FragmentOutputs.Count);
            Assert.Equal(new[] { "vertex", "fragment" }.Order(), variant.Stages.Select(s => s.Stage).Order());
            foreach (var stage in variant.Stages)
            {
                Assert.Equal("fixture.glsl", stage.Source);
                Assert.Equal(stage.Sha256, Convert.ToHexStringLower(SHA256.HashData(build.Files[stage.Spirv])));
                Assert.Equal(0x07230203u, BitConverter.ToUInt32(build.Files[stage.Spirv]));
            }
        }
    }

    [Fact]
    public void IncludeEditsInvalidateOutputsAndRemovedProgramsLeaveNoStaleBinaries()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var initial = Build(compiler, Source);
        NativeShaderBuilder.Write(initial, Output);
        var times = Directory.GetFiles(Package).ToDictionary(Path.GetFileName, File.GetLastWriteTimeUtc);
        var unchanged = Build(compiler, Source);
        NativeShaderBuilder.Write(unchanged, Output);
        Assert.Empty(NativeShaderBuilder.Compare(unchanged, Output));
        Assert.All(Directory.GetFiles(Package), path => Assert.Equal(times[Path.GetFileName(path)], File.GetLastWriteTimeUtc(path)));

        File.WriteAllText(Path.Combine(Source, "include", "color.glsl"), "vec4 fixtureColor() { return vec4(0.8, 0.7, 0.6, 1); }");
        var changed = Build(compiler, Source);
        Assert.NotEmpty(NativeShaderBuilder.Compare(changed, Output));
        foreach (var variant in changed.Manifest.Programs[0].Variants)
        {
            var fragment = variant.Stages.Single(s => s.Stage == "fragment");
            Assert.False(initial.Files[fragment.Spirv].SequenceEqual(changed.Files[fragment.Spirv]));
        }
        NativeShaderBuilder.Write(changed, Output);
        File.Delete(Path.Combine(Source, "fixture.glsl"));
        NativeShaderBuilder.Write(Build(compiler, Source), Output);
        Assert.Empty(Directory.GetFiles(Package, "*.spv"));
        Assert.Empty(NativeShaderManifest.Load(Path.Combine(Package, NativeShaderManifest.FileName)).Programs);
    }

    [Fact]
    public void ShippedProgramsCompilePackageAndLoadEveryDeclaredStage()
    {
        using var compiler = new ShaderCompiler();
        string source = Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk");
        var build = Build(compiler, source);
        string[] names = Directory.GetFiles(source, "*.glsl").Select(Path.GetFileNameWithoutExtension).Order().ToArray()!;
        Assert.NotEmpty(names);
        Assert.Equal(names, build.Manifest.Programs.Select(p => p.Name).Order());
        NativeShaderBuilder.Write(build, Output);
        var library = NativeShaderLibrary.Load(Package, compiler.Identity, out string reason);
        Assert.True(library != null, reason);
        var sharedBindings = SharedBindings();
        foreach (var program in build.Manifest.Programs)
            foreach (var variant in program.Variants)
                foreach (var stage in variant.Stages)
                {
                    Assert.True(library!.TryGetSpirv(stage, out byte[] bytes, out string error), error);
                    Assert.Equal(build.Files[stage.Spirv], bytes);
                    var reflected = SpirvReflection.Reflect(bytes);
                    foreach (var binding in reflected.Bindings)
                        Assert.Contains((binding.Set, binding.Binding, binding.Kind), sharedBindings);
                }
        Assert.Empty(NativeShaderBuilder.Compare(build, Output));
    }

    [Fact]
    public void CorruptPackagedModulesAreRejectedBeforeLinking()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var build = Build(compiler, Source);
        NativeShaderBuilder.Write(build, Output);
        var stage = build.Manifest.Programs[0].Variants[0].Stages[0];
        File.WriteAllBytes(Path.Combine(Package, stage.Spirv), new byte[] { 1, 2, 3 });
        var library = NativeShaderLibrary.Load(Package, compiler.Identity, out _);
        Assert.NotNull(library);
        Assert.False(library!.TryGetSpirv(stage, out _, out string reason));
        Assert.NotEmpty(reason);
        Assert.NotEmpty(NativeShaderBuilder.Compare(build, Output));
    }

    [Fact]
    public void InvalidSourcesFailWithoutReplacingTheLastGoodPackage()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var good = Build(compiler, Source);
        NativeShaderBuilder.Write(good, Output);
        File.WriteAllText(Path.Combine(Source, "fixture.glsl"), Program.Replace("void main()", "void broken main()"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.NotEqual(0, NativeShaderTool.Run(new[] { "--build", Source, Output }, output, error));
        Assert.NotEmpty(error.ToString());
        Assert.Empty(NativeShaderBuilder.Compare(good, Output));
    }

    [Fact]
    public void BinaryCacheReusesCompiledModulesAndRejectsCorruption()
    {
        var cache = new ShaderBinaryCache(Path.Combine(root, "cache"));
        using var compiler = new ShaderCompiler { BinaryCache = cache };
        const string source = "#version 450\nvoid main() { gl_Position = vec4(0, 0, 0, 1); }";
        var first = compiler.Compile(source, "fixture.vert", EnumShaderType.VertexShader);
        Assert.True(first.Success, first.Error);
        var second = compiler.Compile(source, "fixture.vert", EnumShaderType.VertexShader);
        Assert.True(second.Success, second.Error);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(first.Spirv, second.Spirv);
        byte[] stored = ShaderBinaryCache.Wrap(first.Spirv);
        stored[^1] ^= 1;
        Assert.Null(ShaderBinaryCache.Unwrap(stored));
        string key = ShaderBinaryCache.KeyFor(source, EnumShaderType.VertexShader, compiler.Identity);
        Assert.NotEqual(key, ShaderBinaryCache.KeyFor(source, EnumShaderType.FragmentShader, compiler.Identity));
        Assert.NotEqual(key, ShaderBinaryCache.KeyFor(source, EnumShaderType.VertexShader, "different-compiler"));
    }

    [Fact]
    public void DriverCacheRequiresMatchingHardwareDriverAndIntactPayload()
    {
        var id = new PipelineCacheIdentity(0x8086, 0x4688, 1017088, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var blob = new byte[64];
        BitConverter.TryWriteBytes(blob.AsSpan(0), 32u);
        BitConverter.TryWriteBytes(blob.AsSpan(4), 1u);
        BitConverter.TryWriteBytes(blob.AsSpan(8), id.VendorId);
        BitConverter.TryWriteBytes(blob.AsSpan(12), id.DeviceId);
        id.Uuid.CopyTo(blob, 16);
        string path = PipelineCacheFile.PathFor(root, id);
        Assert.True(PipelineCacheFile.Save(path, blob, id));
        Assert.Equal(blob, PipelineCacheFile.Load(path, id));
        foreach (var other in new[]
        {
            new PipelineCacheIdentity(0x10de, id.DeviceId, id.DriverVersion, id.Uuid),
            new PipelineCacheIdentity(id.VendorId, id.DeviceId + 1, id.DriverVersion, id.Uuid),
            new PipelineCacheIdentity(id.VendorId, id.DeviceId, id.DriverVersion + 1, id.Uuid),
            new PipelineCacheIdentity(id.VendorId, id.DeviceId, id.DriverVersion, new byte[16]),
        }) Assert.Null(PipelineCacheFile.Load(path, other));
        byte[] stored = File.ReadAllBytes(path);
        File.WriteAllBytes(path, stored[..^1]);
        Assert.Null(PipelineCacheFile.Load(path, id));
        stored[^1] ^= 1;
        File.WriteAllBytes(path, stored);
        Assert.Null(PipelineCacheFile.Load(path, id));
    }

    [Fact]
    public void FailedAtomicCacheReplacementPreservesTheLastGoodFile()
    {
        string path = Path.Combine(root, "blocked-cache.bin");
        File.WriteAllBytes(path, new byte[] { 9, 8, 7 });
        Assert.False(CacheFileWriter.WriteAtomically(path, new byte[] { 1 },
            (_, _) => throw new IOException("sharing violation"), _ => { }));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.True(CacheFileWriter.WriteAtomically(path, new byte[] { 2 }));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void PipelineWarmupRecordsStaySeparatedBySettingsAndProgram()
    {
        var log = new PipelineKeyLog();
        PipelineKeyLogEntry Entry(ulong settings, ulong shader) => new()
        {
            SettingsHash = settings, ProgramHash = new UInt128(0, shader),
            Bindings = Array.Empty<VertexBinding>(), Attributes = Array.Empty<VertexAttribute>(),
            DepthFormat = Silk.NET.Vulkan.Format.Undefined,
            PolygonMode = Silk.NET.Vulkan.PolygonMode.Fill, Topology = Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
            ColorFormats = new[] { Silk.NET.Vulkan.Format.R8G8B8A8Unorm },
            Blend = new[] { AttachmentBlend.Default },
        };
        log.Record(Entry(1, 20), 100);
        log.Record(Entry(2, 20), 200);
        log.Record(Entry(1, 21), 300);
        log.Record(Entry(1, 20), 400);
        string path = Path.Combine(root, "warmup.keys");
        Assert.True(log.Save(path));
        var loaded = PipelineKeyLog.Load(path);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(400, Assert.Single(loaded.Matching(1, new UInt128(0, 20))).LastSeenUnixMs);
        Assert.Equal(200, Assert.Single(loaded.Matching(2, new UInt128(0, 20))).LastSeenUnixMs);
        Assert.Empty(loaded.Matching(2, new UInt128(0, 21)));
        File.WriteAllText(path, "invalid partial write");
        Assert.Equal(0, PipelineKeyLog.Load(path).Count);
    }

    private static HashSet<(int Set, int Binding, SpirvDescriptorKind Kind)> SharedBindings()
    {
        static SpirvDescriptorKind Kind(Silk.NET.Vulkan.DescriptorType type) => type switch {
            Silk.NET.Vulkan.DescriptorType.UniformBuffer or Silk.NET.Vulkan.DescriptorType.UniformBufferDynamic => SpirvDescriptorKind.UniformBuffer,
            Silk.NET.Vulkan.DescriptorType.StorageBuffer or Silk.NET.Vulkan.DescriptorType.StorageBufferDynamic => SpirvDescriptorKind.StorageBuffer,
            _ => SpirvDescriptorKind.CombinedImageSampler,
        };
        return SharedPipelineLayout.FrameBindings().Select(b => (SetConvention.FrameSet, (int)b.Binding, Kind(b.DescriptorType)))
            .Concat(SharedPipelineLayout.StorageBindings().Select(b => (SetConvention.StorageSet, (int)b.Binding, Kind(b.DescriptorType))))
            .Concat(SetConvention.TextureArrays.Select(b => (SetConvention.TextureSet, b.Value, SpirvDescriptorKind.CombinedImageSampler))).ToHashSet();
    }

    [Fact]
    public void ReflectionPreservesDeclaredAbiAndFindsActualUsesInOptimizedCode()
    {
        const string source = """
            #version 450
            #extension GL_EXT_scalar_block_layout : require
            #extension GL_EXT_nonuniform_qualifier : require
            layout(set=0, binding=1) uniform sampler2DShadow unusedShadow;
            layout(set=1, binding=2) uniform sampler2D images[];
            layout(set=2, binding=4, std430) readonly buffer Data { uint words[]; } data;
            layout(push_constant, scalar) uniform Draw { uint index; vec3 origin; mat4 transform; } draw;
            layout(set=2, binding=3, scalar) uniform Record { vec2 scale; float weights[3]; mat3 normal; int kind; } record;
            layout(constant_id=3) const int MODE = 2;
            layout(constant_id=7) const float THRESHOLD = 0.25;
            layout(constant_id=9) const bool ENABLE = true;
            layout(constant_id=11) const uint COUNT = 5u;
            layout(location=0) in vec2 uv;
            layout(location=1) flat in ivec3 flags;
            layout(location=0) out vec4 color;
            layout(location=2) out vec4 unwritten;
            layout(location=3) out vec4 partial;
            void main() {
                color = texture(images[draw.index], uv * record.scale) + vec4(record.normal * draw.origin, 1) * record.weights[2];
                color *= float(data.words[flags.x]) + float(MODE) + THRESHOLD + float(COUNT);
                if (ENABLE) partial.xy = draw.origin.xy;
            }
            """;
        using var compiler = new ShaderCompiler();
        var declaredCode = compiler.CompileForReflection(source, "abi.frag", EnumShaderType.FragmentShader);
        var shippedCode = compiler.Compile(source, "abi.frag", EnumShaderType.FragmentShader);
        Assert.True(declaredCode.Success, declaredCode.Error); Assert.True(shippedCode.Success, shippedCode.Error);
        var declared = SpirvReflection.Reflect(declaredCode.Spirv);
        var shipped = SpirvReflection.Reflect(shippedCode.Spirv);
        Assert.Equal("main", declared.EntryPoint);
        Assert.Equal(SpirvReflection.ExecutionModelFragment, declared.ExecutionModel);
        Assert.Equal(new[] { (0, "vec2"), (1, "ivec3") }, declared.Inputs.Select(v => (v.Location, v.GlslType)));
        Assert.Equal(new[] { 0, 2, 3 }, declared.Outputs.Select(v => v.Location));
        Assert.Equal(new[] { ("index", 0, 4), ("origin", 4, 12), ("transform", 16, 64) },
            declared.PushConstants!.Members.Select(m => (m.Name, m.Offset, m.Size)));
        Assert.Equal(80, declared.PushConstants.Size);
        var record = declared.Bindings.Single(b => b.Set == 2 && b.Binding == 3);
        Assert.Equal(SpirvDescriptorKind.UniformBuffer, record.Kind);
        Assert.Equal(new[] { ("scale", 0, 8, 0), ("weights", 8, 12, 3), ("normal", 20, 36, 0), ("kind", 56, 4, 0) },
            record.Block!.Members.Select(m => (m.Name, m.Offset, m.Size, m.ArrayLength)));
        Assert.Equal(60, record.Block.Size);
        Assert.True(declared.Bindings.Single(b => b.Set == 1).RuntimeArray);
        var storage = declared.Bindings.Single(b => b.Binding == 4);
        Assert.Equal(SpirvDescriptorKind.StorageBuffer, storage.Kind);
        Assert.Equal(-1, Assert.Single(storage.Block!.Members).ArrayLength);
        Assert.Equal(new[] { (3, "int", 2.0), (7, "float", 0.25), (9, "bool", 1.0), (11, "uint", 5.0) },
            declared.SpecConstants.Select(c => (c.SpecId, c.GlslType, c.DefaultValue)));
        Assert.Equal(new[] { 0, 3 }, shipped.WrittenOutputLocations);
        Assert.Equal(new[] { (1, 2), (2, 3), (2, 4) }, shipped.Bindings.Where(b => shipped.UsedVariables.Contains(b.VariableId)).Select(b => (b.Set, b.Binding)));
        Assert.Equal(new[] { (1, 2) }, shipped.PushMemberIndexes[0]);
        Assert.All(shipped.Bindings, binding => Assert.Empty(binding.Name));
        Assert.Throws<FormatException>(() => SpirvReflection.Reflect(new byte[] { 1, 2, 3 }));
        Assert.Throws<FormatException>(() => SpirvReflection.Reflect(new byte[20]));

        var vertex = compiler.CompileForReflection("""
            #version 450
            layout(location=0) in vec3 position;
            layout(location=1) in mat2 packedIn;
            layout(location=3) in ivec4 color;
            layout(location=0) out vec2 uv;
            void main() { uv = packedIn[0] + vec2(color.xy); gl_Position = vec4(position, 1); }
            """, "abi.vert", EnumShaderType.VertexShader);
        Assert.True(vertex.Success, vertex.Error);
        var input = SpirvReflection.Reflect(vertex.Spirv);
        Assert.Equal(SpirvReflection.ExecutionModelVertex, input.ExecutionModel);
        Assert.Equal(new[] { (0, "vec3"), (1, "mat2"), (3, "ivec4") }, input.Inputs.Select(v => (v.Location, v.GlslType)));
        Assert.Equal("uv", Assert.Single(input.Outputs).Name);
    }

    [Fact]
    public void CompiledSharedIncludesMatchCpuFrameOffsetsAndSpecializationIds()
    {
        using var compiler = new ShaderCompiler();
        string sum = string.Join(" + ", SpecializationConvention.Constants.Select(c => "float(" + c.Name + ")"));
        string probe = "#version 450\n#include \"frame.glsl\"\n#include \"specialization.glsl\"\n" +
            "layout(location=0) out vec4 color;\nvoid main() { color = vec4(optimumFrame.zNear + optimumFrame.pointLights[99].x + " + sum + "); }";
        var compiled = NativeShaderTree.Compile(compiler, probe, EnumShaderType.FragmentShader, "shared-abi");
        Assert.True(compiled.Success, compiled.Error);
        var reflected = SpirvReflection.Reflect(compiled.Spirv);
        var block = reflected.Bindings.Single(b => b.Set == FrameGlobals.Set && b.Binding == FrameGlobals.Binding).Block!;
        Assert.Equal(FrameGlobals.BlockSize, block.Size);
        Assert.Equal(FrameGlobals.Members.Select(m => (m.Offset, m.Size, m.ArrayLength)),
            block.Members.Select(m => (m.Offset, m.Size, m.ArrayLength)));
        Assert.Equal(SpecializationConvention.Constants.Select(c => ((int)c.Id, c.GlslType, double.Parse(c.Default, System.Globalization.CultureInfo.InvariantCulture))).Order(),
            reflected.SpecConstants.Select(c => (c.SpecId, c.GlslType, c.DefaultValue)).Order());
        foreach (var binding in reflected.Bindings)
            Assert.Contains((binding.Set, binding.Binding, binding.Kind), SharedBindings());
    }

    [Fact]
    public void OnlyCompatibleUniformsFromTheirOwningIncludesEnterTheSharedBlock()
    {
        const string code = "uniform vec3 pointLights[4]; uniform float viewDistance; uniform vec3 lightPosition; uniform float tint; void main() {}";
        ProgramInterfaceLayout Layout(string source, IReadOnlySet<string>? owners) => ProgramInterfaceLayout.Build(
            new[] { (EnumShaderType.VertexShader, GlslParser.Parse(source)) }, null, owners);
        var owned = Layout(code, new HashSet<string> { "fogandlight.vsh" });
        Assert.Equal(new[] { "pointLights", "viewDistance" }, owned.FrameMemberDeclaredLengths.Keys.Order());
        Assert.Equal(new[] { "lightPosition", "tint" }, owned.Members.Select(m => m.Name).Order());
        Assert.Equal(4, owned.FrameMemberDeclaredLengths["pointLights"]);
        Assert.False(Layout(code, null).UsesFrameBlock);
        Assert.False(Layout("uniform float pointLights[4]; void main() {}", new HashSet<string> { "fogandlight.vsh" }).UsesFrameBlock);
        Assert.False(Layout("uniform vec3 pointLights[101]; void main() {}", new HashSet<string> { "fogandlight.vsh" }).UsesFrameBlock);
        byte[] defaults = FrameGlobals.CreateShadow();
        foreach (var pair in new[] { ("zNear", 0.3f), ("zFar", 1500f), ("windWaveIntensity", 1f) })
        {
            Assert.True(FrameGlobals.TryGetMember(pair.Item1, out var member));
            Assert.Equal(pair.Item2, BitConverter.ToSingle(defaults, member.Offset));
        }
    }
}
