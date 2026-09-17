using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The native device API's mesh draws (docs/vulkan-native-render-systems.md, decision 4:
/// "fullscreen triangle, mesh, multi-draw or instanced"), on the real chunk program and a real
/// tesselated face.
///
/// Stage 1 recorded fullscreen draws only. These pin the four facts a world system depends on:
/// a native mesh draw puts the same pixels on the target as the stated draw of the same mesh
/// with the same state; a mesh pipeline is never the fullscreen pipeline of the same program;
/// a native multi-draw takes its own region of the per-slot indirect ring, the same ring the
/// stated multi-draw allocates from; and the stats count mesh, instanced and indirect draws
/// apart from fullscreen ones.
/// </summary>
public class NativeMeshDrawTests(ITestOutputHelper output)
{
    private const int Size = 32;
    private const int UpNormalFlags = 7 << 18;

    // ------------------------------------------------------------------- the tests

    /// <summary>
    /// The same face, drawn twice into the same target: once through the tests' GL-shaped
    /// <c>DrawMesh</c> (<see cref="GlShapedDevice" />: the platform's generic stated draw, the route
    /// every draw without a dedicated one takes) and once through <see cref="VulkanDevice.DrawNativeMesh" />. Both paths run the same shader
    /// over the same vertices with the same fixed state, so the pixels are bitwise equal.
    /// </summary>
    [SkippableFact]
    public unsafe void ANativeMeshDrawMatchesTheStatedDrawOfTheSameMesh()
    {
        using Session session = Open();

        byte[] stated = session.RunStatedFrame();

        long meshDrawsBefore = session.Device.NativeMeshDrawsForTests;
        long fullscreenBefore = session.Device.NativeFullscreenDrawsForTests;
        byte[] native = session.RunNativeFrame();

        Assert.Equal(1, session.Device.NativeMeshDrawsForTests - meshDrawsBefore);
        Assert.Equal(0, session.Device.NativeFullscreenDrawsForTests - fullscreenBefore);

        output.WriteLine("stated centre: " + Centre(stated) + "  native centre: " + Centre(native));
        Assert.Equal(stated, native);
        GpuTest.AssertClean(session.Device);
    }

    /// <summary>
    /// The pipeline key carries the vertex layout, so the mesh pipeline and the fullscreen
    /// pipeline of one program, one target and one blend set are two entries, never one. Before
    /// the layout entered the key they would have collided and a mesh draw would have run the
    /// fullscreen pipeline, which binds no vertex buffers at all.
    /// </summary>
    [SkippableFact]
    public void AMeshPipelineKeyNeverCollidesWithAFullscreenOne()
    {
        using Session session = Open();
        VulkanDevice device = session.Device;

        int before = device.NativePipelinesForTests;
        NativePipeline? fullscreen = device.RequestNativePipeline(
            session.Description(MeshManager.EmptyLayoutId), out string fullscreenError);
        NativePipeline? mesh = device.RequestNativePipeline(
            session.Description(session.LayoutId), out string meshError);

        Assert.True(fullscreen != null, fullscreenError);
        Assert.True(mesh != null, meshError);
        Assert.NotSame(fullscreen, mesh);
        Assert.NotEqual(fullscreen!.Key, mesh!.Key);
        Assert.Equal(2, device.NativePipelinesForTests - before);

        // Asking again for either gives the entry that was made for it, not the other one.
        Assert.Same(mesh, device.RequestNativePipeline(session.Description(session.LayoutId), out _));
        Assert.Same(fullscreen,
            device.RequestNativePipeline(session.Description(MeshManager.EmptyLayoutId), out _));
        Assert.Equal(2, device.NativePipelinesForTests - before);
    }

    /// <summary>
    /// The other new state dimensions are in the key too: polygon mode, front face, line width
    /// and the bound-depth declaration each make a distinct pipeline, so a cached one is never
    /// handed to a draw that asked for different state.
    /// </summary>
    [SkippableFact]
    public void EveryNewStateDimensionMakesItsOwnPipeline()
    {
        using Session session = Open();
        VulkanDevice device = session.Device;

        int before = device.NativePipelinesForTests;
        Assert.NotNull(device.RequestNativePipeline(session.Description(session.LayoutId), out _));

        NativePipelineDescription lines = session.Description(session.LayoutId);
        lines.PolygonMode = PolygonMode.Line;
        Assert.NotNull(device.RequestNativePipeline(lines, out _));

        NativePipelineDescription winding = session.Description(session.LayoutId);
        winding.FrontFace = FrontFace.CounterClockwise;
        Assert.NotNull(device.RequestNativePipeline(winding, out _));

        NativePipelineDescription wide = session.Description(session.LayoutId);
        wide.Topology = PrimitiveTopology.LineList;
        wide.LineWidth = 2f;
        Assert.NotNull(device.RequestNativePipeline(wide, out _));

        NativePipelineDescription depthRead = session.Description(session.LayoutId);
        depthRead.DepthWrite = false;
        depthRead.SamplesBoundDepth = true;
        Assert.NotNull(device.RequestNativePipeline(depthRead, out _));

        Assert.Equal(5, device.NativePipelinesForTests - before);
    }

    /// <summary>A pipeline that samples the depth it draws into may not also write it.</summary>
    [SkippableFact]
    public void APipelineCannotBothSampleAndWriteTheBoundDepth()
    {
        using Session session = Open();
        NativePipelineDescription description = session.Description(session.LayoutId);
        description.DepthWrite = true;
        description.SamplesBoundDepth = true;

        Assert.Null(session.Device.RequestNativePipeline(description, out string error));
        Assert.Contains("cannot also write depth", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two native multi-draws in one frame take two regions of the slot's indirect buffer, as
    /// the stated route does: writing both at offset zero was the Phase 1B bug where every
    /// multi-draw in a frame executed with the ranges of whichever was recorded last.
    /// </summary>
    [SkippableFact]
    public unsafe void TwoNativeMultiDrawsTakeTwoRegionsOfTheIndirectRing()
    {
        using Session session = Open();
        VulkanDevice device = session.Device;

        long indirectBefore = device.NativeIndirectDrawsForTests;
        ulong cursorBefore = 0;
        ulong cursorAfter = 0;

        session.RunFrame(() =>
        {
            int slot = device.IndirectRingForTests.Current;
            cursorBefore = device.IndirectRingForTests.CursorOf(slot);
            NativePipeline pipeline = session.BeginNativeSceneDraw();
            Assert.True(device.DrawNativeMeshMulti(pipeline, session.Mesh,
                new[] { 0, 0 }, new[] { 6 }, 1, session.Textures(pipeline)));
            Assert.True(device.DrawNativeMeshMulti(pipeline, session.Mesh,
                new[] { 0, 0 }, new[] { 6 }, 1, session.Textures(pipeline)));
            cursorAfter = device.IndirectRingForTests.CursorOf(slot);
            device.EndNativePass();
        });

        Assert.Equal(2, device.NativeIndirectDrawsForTests - indirectBefore);
        ulong command = (ulong)sizeof(DrawIndexedIndirectCommand);
        Assert.Equal(2 * command, cursorAfter - cursorBefore);
        GpuTest.AssertClean(device);
    }

    /// <summary>
    /// An instanced native draw is counted as one, apart from the plain mesh draws, and draws
    /// through the mesh's own bindings exactly as <see cref="VulkanDevice.DrawMeshInstanced" />
    /// does.
    /// </summary>
    [SkippableFact]
    public void AnInstancedNativeDrawIsCountedApartFromAPlainMeshDraw()
    {
        using Session session = Open();
        VulkanDevice device = session.Device;

        long meshBefore = device.NativeMeshDrawsForTests;
        long instancedBefore = device.NativeInstancedDrawsForTests;

        session.RunFrame(() =>
        {
            NativePipeline pipeline = session.BeginNativeSceneDraw();
            Assert.True(device.DrawNativeMesh(pipeline, session.Mesh, session.Textures(pipeline)));
            Assert.True(device.DrawNativeMeshInstanced(pipeline, session.Mesh, 3, session.Textures(pipeline)));
            device.EndNativePass();
        });

        Assert.Equal(1, device.NativeMeshDrawsForTests - meshBefore);
        Assert.Equal(1, device.NativeInstancedDrawsForTests - instancedBefore);
        GpuTest.AssertClean(device);
    }

    /// <summary>
    /// A mesh drawn through a pipeline built for another mesh's layout is refused rather than
    /// recorded: the vertex buffers it would read are not the ones the pipeline declares, and
    /// no validation layer can see that, because every descriptor involved is valid.
    /// </summary>
    [SkippableFact]
    public void AMeshDrawThroughTheWrongLayoutsPipelineIsRefused()
    {
        using Session session = Open();
        VulkanDevice device = session.Device;

        session.RunFrame(() =>
        {
            NativePipeline? fullscreen = device.RequestNativePipeline(
                session.Description(MeshManager.EmptyLayoutId), out string error);
            Assert.True(fullscreen != null, error);
            Assert.True(session.BeginPass());
            Assert.False(device.DrawNativeMesh(fullscreen!, session.Mesh, session.Textures(fullscreen!)));
            device.EndNativePass();
        });

        GpuTest.AssertClean(device);
    }


    /// <summary>The client's IShader, as much of it as CompileShader reads.</summary>
    private sealed class CorpusShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    /// <summary>The client's IShaderProgram, as much of it as LinkProgram reads.</summary>
    private sealed class CorpusProgram : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId { get; set; }
        public string PassName { get; set; } = "chunkopaque";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; } = true;
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();
        public bool Compile() => true;
        public bool HasUniform(string uniformName) => false;
        public void Use() { }
        public void Stop() { }
        public void Dispose() { }
        public void Uniform(string uniformName, float value) { }
        public void Uniform(string uniformName, int value) { }
        public void Uniform(string uniformName, Vec2f value) { }
        public void Uniform(string uniformName, Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
    }

    // ---------------------------------------------------------------------- session

    private Session Open()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        return new Session(device!);
    }

    private static string Centre(byte[] pixels)
    {
        int i = (Size / 2 * Size + Size / 2) * 4;
        return pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," + pixels[i + 3];
    }

    /// <summary>
    /// The real chunkopaque program, one tesselated face, and a target to draw it into: the
    /// smallest setting in which both routes can draw the same thing.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly Dictionary<string, int> _samplerTextures = new(StringComparer.Ordinal);

        public Session(VulkanDevice device)
        {
            Device = device;
            Program = LinkChunkOpaque(device);
            BindEveryDeclaredSampler();

            Color = device.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            Depth = device.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                IntPtr.Zero, false);
            Framebuffer = device.CreateFramebuffer(Size, Size);
            device.AttachTexture(Framebuffer, EnumFramebufferAttachment.ColorAttachment0, Color, 0);
            device.AttachTexture(Framebuffer, EnumFramebufferAttachment.DepthAttachment, Depth, 0);
            device.SetDrawBuffers(Framebuffer, 0b1);
            Assert.True(device.CheckFramebufferComplete(Framebuffer, out string status), status);

            Mesh = device.CreateMesh(BuildBlockFace(), staticDraw: true);
            Assert.True(Mesh > 0, device.GetError() ?? "mesh upload failed");
            LayoutId = device.NativeMeshLayoutId(Mesh);
            Assert.True(LayoutId > 0, "the face's vertex layout is the reserved empty one");
        }

        public VulkanDevice Device { get; }
        public int Program { get; }
        public int Framebuffer { get; }
        public int Color { get; }
        public int Depth { get; }
        public int Mesh { get; }
        public int LayoutId { get; }

        public void Dispose() => Device.Dispose();

        /// <summary>The fixed state both routes draw with: depth LEQUAL, writes on, no culling, no blending.</summary>
        public NativePipelineDescription Description(int layoutId) => new()
        {
            ProgramId = Program,
            Blend = new[] { AttachmentBlend.Default },
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = CompareOp.LessOrEqual,
            Cull = CullModeFlags.None,
            Topology = PrimitiveTopology.TriangleList,
            VertexLayoutId = layoutId,
            Targets = Device.NativeTargetFormats(Framebuffer, 1u)!,
        };

        /// <summary>Every sampler the program declares, at the slot this pipeline resolved for it.</summary>
        public NativeTexture[] Textures(NativePipeline pipeline)
        {
            var textures = new List<NativeTexture>();
            foreach (KeyValuePair<string, int> sampler in _samplerTextures)
            {
                NativeSamplerSlot slot = pipeline.Sampler(sampler.Key);
                if (slot.IsPresent) textures.Add(new NativeTexture(slot, sampler.Value));
            }
            return textures.ToArray();
        }

        public bool BeginPass() => Device.BeginNativePass(new NativePassDescription
        {
            Name = "NativeMeshDrawTests",
            FramebufferId = Framebuffer,
            ColorSlots = 1u,
            Reads = _samplerTextures.Values.ToArray(),
            ViewportWidth = Size,
            ViewportHeight = Size,
        });

        /// <summary>Opens the pass and returns the pipeline the mesh draws through.</summary>
        public NativePipeline BeginNativeSceneDraw()
        {
            NativePipeline? pipeline = Device.RequestNativePipeline(Description(LayoutId), out string error);
            Assert.True(pipeline != null, error);
            Assert.True(BeginPass());
            return pipeline!;
        }

        /// <summary>One frame: clear, set the uniforms both routes read, run <paramref name="body" />, present.</summary>
        public void RunFrame(Action body)
        {
            Device.BeginFrame();
            Device.BindFramebuffer(Framebuffer);
            Device.ClearColor(0, 1f, 0f, 1f, 1f);
            Device.ClearDepth(1f);
            SetUniforms();
            Device.SetViewport(0, 0, Size, Size);
            body();
            Device.Present();
        }

        /// <summary>The stated route: the GL-shaped state, then DrawMesh.</summary>
        public unsafe byte[] RunStatedFrame()
        {
            byte[] pixels = new byte[Size * Size * 4];
            RunFrame(() =>
            {
                Device.UseProgram(Program);
                Device.SetDepthTest(true);
                Device.SetDepthMask(true);
                Device.SetDepthFunc(0x203);   // GL_LEQUAL
                Device.SetCullFace(false);
                Device.SetBlend(false, EnumBlendMode.Standard);
                Device.DrawMesh(Mesh);
            });
            Read(pixels);
            return pixels;
        }

        /// <summary>The native route: a declared pass, a pipeline with the mesh's layout, one draw.</summary>
        public unsafe byte[] RunNativeFrame()
        {
            byte[] pixels = new byte[Size * Size * 4];
            RunFrame(() =>
            {
                NativePipeline pipeline = BeginNativeSceneDraw();
                Assert.True(Device.DrawNativeMesh(pipeline, Mesh, Textures(pipeline)));
                Device.EndNativePass();
            });
            Read(pixels);
            return pixels;
        }

        private unsafe void Read(byte[] pixels)
        {
            fixed (byte* destination = pixels)
            {
                Device.BindFramebuffer(Framebuffer);
                Device.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }
        }

        // ------------------------------------------------------------- program setup

        private void SetUniforms()
        {
            float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            foreach (string name in new[] { "projectionMatrix", "modelViewMatrix", "modelMatrix", "mvpMatrix",
                         "toShadowMapSpaceMatrixFar", "toShadowMapSpaceMatrixNear" })
            {
                int location = Device.GetUniformLocation(Program, name);
                if (location >= 0) Device.SetUniformMatrix(Program, location, identity);
            }
            SetFloat("viewDistance", 1024f);
            SetFloat("viewDistanceLod0", 1024f);
            SetFloat("alphaTest", 0.001f);
            SetFloat("zNear", 0.1f);
            SetFloat("zFar", 1024f);
            SetFloat("shadowRangeFar", 1024f);
            SetFloat("shadowRangeNear", 64f);
            SetFloat("shadowMapWidthInv", 1f);
            SetFloat("shadowMapHeightInv", 1f);
            int ambient = Device.GetUniformLocation(Program, "rgbaAmbientIn");
            if (ambient >= 0) Device.SetUniform(Program, ambient, 1f, 1f, 1f);
            int frameSize = Device.GetUniformLocation(Program, "frameSize");
            if (frameSize >= 0) Device.SetUniform(Program, frameSize, (float)Size, (float)Size);
        }

        private void SetFloat(string name, float value)
        {
            int location = Device.GetUniformLocation(Program, name);
            if (location >= 0) Device.SetUniform(Program, location, value);
        }

        private unsafe void BindEveryDeclaredSampler()
        {
            var white = new byte[] { 255, 255, 255, 255 };
            int unit = 0;
            foreach (string name in Device.SamplerNamesOf(Program))
            {
                int texture;
                fixed (byte* pixels = white)
                {
                    texture = Device.CreateTexture2D(1, 1,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
                }
                Device.SetSamplerUnit(Program, name, unit);
                Device.BindTexture(unit, texture);
                _samplerTextures[name] = texture;
                unit++;
            }
        }

        private static int LinkChunkOpaque(VulkanDevice device)
        {
            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("chunkopaque",
                ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), ShaderCorpus.Variants().First());
            Assert.NotEmpty(stages);

            var program = new CorpusProgram { PassName = "chunkopaque" };
            foreach (ShaderStageSource stage in stages)
            {
                var shader = new CorpusShader
                {
                    Type = stage.Stage,
                    Code = stage.Code,
                    PrefixCode = stage.PrefixCode ?? "",
                };
                Assert.True(device.CompileShader(shader), device.GetError() ?? "compile failed");
                if (stage.Stage == EnumShaderType.VertexShader) program.VertexShader = shader;
                else if (stage.Stage == EnumShaderType.FragmentShader) program.FragmentShader = shader;
                else program.GeometryShader = shader;
            }

            int id = device.LinkProgram(program);
            Assert.True(id > 0, device.GetError() ?? "link failed");
            return id;
        }

        /// <summary>One tesselated block face, in the layout the chunk tesselator emits.</summary>
        private static MeshData BuildBlockFace()
        {
            var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);
            float[] positions =
            {
                -0.5f, -0.5f, 0f,
                 0.5f, -0.5f, 0f,
                 0.5f,  0.5f, 0f,
                -0.5f,  0.5f, 0f,
            };
            float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            for (int i = 0; i < 4; i++)
            {
                mesh.AddVertexWithFlags(
                    positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                    uvs[i * 2], uvs[i * 2 + 1], ColorUtil.WhiteArgb, flags: UpNormalFlags);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(index);
            return mesh;
        }
    }
}
