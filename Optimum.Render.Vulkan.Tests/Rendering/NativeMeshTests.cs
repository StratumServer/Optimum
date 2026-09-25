// Source: Optimum.Render.Vulkan.Tests/NativeMeshDrawTests.cs
namespace Optimum.Render.Vulkan.Tests
{
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

/// <summary>
/// The native device API's mesh draws (docs/vulkan.md, decision 4:
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
    /// <c>DrawMesh</c> (the platform's generic stated draw) and once through
    /// <see cref="VulkanDevice.DrawNativeMesh" />. Both paths run the same shader
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


    [SkippableFact]
    public unsafe void IndexedLineStripKeepsItsInteriorEmptyAndDoesNotChangeTheNextMeshTopology()
    {
        using var device = GpuTest.CreateDevice(output);
        int program = GpuTest.LinkProgram(device, """
            #version 330 core
            layout(location = 0) in vec3 position;
            void main() { gl_Position = vec4(position, 1); }
            """, """
            #version 330 core
            out vec4 color;
            void main() { color = vec4(1); }
            """, "indexed-line-strip");
        var rectangle = new MeshData(4, 5)
        {
            xyz = new[] { -.75f, -.75f, 0f, .75f, .75f, 0f,
                .75f, -.75f, 0f, -.75f, .75f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 2, 1, 3, 0 },
            IndicesCount = 5,
            mode = EnumDrawMode.LineStrip,
        };
        int lines = device.CreateMesh(rectangle, true);
        int image = device.CreateTexture2D(32, 32, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int target = device.CreateFramebuffer(32, 32);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, image, 0);
        device.SetDrawBuffers(target, 1);

        void Begin()
        {
            device.BeginFrame(); device.BindFramebuffer(target); device.UseProgram(program);
            device.SetViewport(0, 0, 32, 32); device.SetDepthTest(false);
            device.SetCullFace(false); device.SetBlend(false, EnumBlendMode.Standard);
            device.ClearColor(0, 0, 0, 0, 1);
        }
        byte[] pixels = new byte[32 * 32 * 4];
        void Read()
        {
            fixed (byte* pointer = pixels)
                device.ReadDefaultFramebuffer(0, 0, 32, 32, (IntPtr)pointer);
        }

        Begin(); device.DrawMesh(lines); Read();
        Assert.Equal(0, pixels[(16 * 32 + 16) * 4]);
        int lit = Enumerable.Range(0, 32 * 32).Count(pixel => pixels[pixel * 4] == 255);
        Assert.InRange(lit, 80, 112);
        device.Present();

        rectangle.mode = EnumDrawMode.Triangles;
        rectangle.Indices = new[] { 0, 2, 1, 0, 1, 3 };
        rectangle.IndicesCount = 6;
        int triangles = device.CreateMesh(rectangle, true);
        Begin(); device.DrawMesh(triangles); Read();
        Assert.Equal(255, pixels[(16 * 32 + 16) * 4]);
        device.Present();
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void EveryRequestedInstanceProducesItsOwnPixels()
    {
        using var device = GpuTest.CreateDevice(output);
        int program = GpuTest.LinkProgram(device, """
            #version 330 core
            layout(location = 0) in vec3 position;
            void main() {
                vec2 offset = vec2(gl_InstanceID) * 0.75;
                gl_Position = vec4(position.xy + offset, position.z, 1);
            }
            """, """
            #version 330 core
            out vec4 color;
            void main() { color = vec4(1, 0, 1, 1); }
            """, "instance-pixels");
        var quad = new MeshData(4, 6)
        {
            xyz = new[] { -1f, -1f, 0f, -.5f, -1f, 0f,
                -.5f, -.5f, 0f, -1f, -.5f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
            mode = EnumDrawMode.Triangles,
        };
        int mesh = device.CreateMesh(quad, true);
        int image = device.CreateTexture2D(16, 16, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int target = device.CreateFramebuffer(16, 16);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, image, 0);
        device.SetDrawBuffers(target, 1);
        device.BeginFrame(); device.BindFramebuffer(target); device.UseProgram(program);
        device.SetViewport(0, 0, 16, 16); device.SetDepthTest(false);
        device.SetCullFace(false); device.SetBlend(false, EnumBlendMode.Standard);
        device.ClearColor(0, 0, 0, 0, 1);
        device.DrawMeshInstanced(mesh, 2);
        byte[] pixels = new byte[16 * 16 * 4];
        fixed (byte* pointer = pixels)
            device.ReadDefaultFramebuffer(0, 0, 16, 16, (IntPtr)pointer);
        foreach ((int x, int y) in new[] { (2, 2), (8, 8) })
        {
            int pixel = (y * 16 + x) * 4;
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, pixels[pixel..(pixel + 4)]);
        }
        device.Present();
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
}

// Source: Optimum.Render.Vulkan.Tests/NativeStatedTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

using LinkedProgram = Optimum.Render.Vulkan.Tests.GpuTest.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.GpuTest.TestShader;

/// <summary>
/// The generic native draw (VulkanClientPlatform.NativeStated.cs, Platform/StatedDraw.cs) against a
/// native draw whose pipeline and pass are written out by hand, on one device: the same mesh, the
/// same program. The generic route gets its fixed state only through the platform's own virtuals;
/// the reference states what OpenGL would do with those calls. Identical pixels mean the record
/// states what the client said.
///
/// The program is a gui program that is not the registered ShaderPrograms.Gui, so no dedicated
/// route takes the draw: it is exactly the shape of a mod renderer's draw.
/// </summary>
public class NativeStatedTests(ITestOutputHelper output)
{
    private const int Size = 16;

    private sealed class StatedPlatform : VulkanClientPlatform
    {
        public StatedPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    public enum Mask { All, RedGreen }

    /// <summary>
    /// Blend off, three blend modes, a colour mask and a scissor rectangle: the generic route
    /// draws what the hand-stated reference draws, and records one native draw.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, EnumBlendMode.Standard, Mask.All, false)]
    [InlineData(true, EnumBlendMode.Standard, Mask.All, false)]
    [InlineData(true, EnumBlendMode.PremultipliedAlpha, Mask.All, false)]
    [InlineData(true, EnumBlendMode.Brighten, Mask.All, false)]
    [InlineData(true, EnumBlendMode.Standard, Mask.RedGreen, false)]
    [InlineData(true, EnumBlendMode.Standard, Mask.All, true)]
    public unsafe void TheStatedRouteDrawsWhatTheHandStatedReferenceDraws(bool blend, EnumBlendMode mode, Mask mask, bool scissor)
    {
        using Session session = Open();

        byte[] reference = session.Run(stated: false, blend, mode, mask, scissor);

        long statedBefore = session.Platform.StatedDrawsForTests;
        byte[] native = session.Run(stated: true, blend, mode, mask, scissor);

        Assert.Equal(1, session.Platform.StatedDrawsForTests - statedBefore);
        output.WriteLine("centre reference " + Centre(reference, 0) + " stated " + Centre(native, 0));
        Assert.Equal(reference, native);
        Assert.NotEqual("0,51,102,153", Centre(native, 0));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// A target with two colour attachments and only the first selected as a draw buffer: the
    /// second keeps its clear colour - the stated draw buffers are write masks - and the first
    /// matches the reference, which writes the first slot only.
    /// </summary>
    [SkippableFact]
    public unsafe void AnUnselectedDrawBufferKeepsItsContentsOnBothRoutes()
    {
        using Session session = Open();

        (byte[] firstReference, byte[] secondReference) = session.RunTwoTargets(stated: false);
        (byte[] firstStated, byte[] secondStated) = session.RunTwoTargets(stated: true);

        Assert.Equal(firstReference, firstStated);
        Assert.Equal(secondReference, secondStated);
        // The second attachment is still the clear colour (0, 51, 102, 153).
        Assert.Equal("0,51,102,153", Centre(secondStated, 0));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// A platform bind is the latest bind: a fork renderer's raw framebuffer bind before it no
    /// longer addresses the generic draws and clears (GL has one binding point).
    /// </summary>
    [Fact]
    public void APlatformBindReplacesAForkBind()
    {
        var platform = new StatedPlatform();
        var target = new FrameBufferRef { FboId = 7, Width = Size, Height = Size, ColorTextureIds = new[] { 1 } };

        platform.NoteForkFramebuffer(12);
        Assert.Equal(12, platform.CurrentTargetId);

        platform.CurrentFrameBuffer = target;
        Assert.Equal(7, platform.CurrentTargetId);

        platform.NoteForkFramebuffer(12);
        platform.BindCurrentFrameBufferKeepViewport(target);
        Assert.Equal(7, platform.CurrentTargetId);
    }

    private static string Centre(byte[] pixels, int offset)
    {
        int i = offset + (Size / 2 * Size + Size / 2) * 4;
        return pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," + pixels[i + 3];
    }

    private Session Open()
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);
        Session? session = Session.TryOpen(output, manifest);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    private sealed class Session : IDisposable
    {
        private static readonly float[] Identity =
            { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public StatedPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        private FrameBufferRef target = null!;
        private FrameBufferRef twoTargets = null!;
        private MeshRef quad = null!;
        private ShaderProgram gui = null!;
        private int texture;
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";

        public static Session? TryOpen(ITestOutputHelper output, string manifestDirectory)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-stated-" + Guid.NewGuid().ToString("N"));
            var platform = new StatedPlatform
            {
                DeviceFactory = () =>
                {
                    VulkanDevice created = GpuTest.NewDevice();
                    created.NativeShaderDirectory = manifestDirectory;
                    created.NativeShadersEnabled = true;
                    created.IgnoreModShaderScan = true;
                    return created;
                },
                CrashMarkerDataPath = dataPath,
            };
            if (!platform.InitializeGraphics(IntPtr.Zero, Size, Size, out string reason))
            {
                output.WriteLine("Vulkan unavailable: " + reason);
                platform.ShutdownGraphics();
                return null;
            }
            platform.NativeGuiEnabled = false;

            var session = new Session { Platform = platform, previousPlatform = ScreenManager.Platform, dataPath = dataPath };
            ScreenManager.Platform = platform;
            platform.ShaderUniforms = new DefaultShaderUniforms();

            VulkanDevice seam = platform.GraphicsDevice!;
            session.target = CreateTarget(seam, 1);
            session.twoTargets = CreateTarget(seam, 2);
            InstallFrameBuffers(platform, session.target);
            // The draw buffers each target writes, stated the way the platform states its own.
            platform.StateDrawBuffers(session.target.FboId, 1);
            platform.StateDrawBuffers(session.twoTargets.FboId, 1);

            var program = new ShaderProgram { PassName = "gui" };
            Link(seam, program, "gui",
                new[] { "projectionMatrix", "modelViewMatrix", "rgbaIn", "noTexture", "applyColor", "alphaTest" });
            session.gui = program;
            session.texture = Gradient(seam);
            session.quad = platform.UploadMesh(BuildQuad());
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            if (quad != null) Platform.DeleteMesh(quad);
            ScreenManager.Platform = previousPlatform!;
            Platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        /// <summary>
        /// One frame: the target bound and cleared, one quad - through the platform with the state
        /// set through its virtuals, or (<paramref name="stated" /> false) the hand-stated reference.
        /// </summary>
        public unsafe byte[] Run(bool stated, bool blend, EnumBlendMode mode, Mask mask, bool scissor)
        {
            Platform.BeginFrame();
            Prepare(target);
            if (stated)
            {
                Platform.GlToggleBlend(blend, mode);
                if (mask == Mask.RedGreen) Platform.GlColorMask(true, true, false, false);
                if (scissor)
                {
                    Platform.GlScissorFlag(true);
                    Platform.GlScissor(4, 4, 8, 8);
                }
                Platform.RenderMesh(quad);
            }
            else
            {
                AttachmentBlend attachment = AttachmentBlend.For(blend, mode);
                if (mask == Mask.RedGreen) attachment.WriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit;
                DrawReference(target, attachment,
                    scissor ? new Rect2D(new Offset2D(4, 4), new Extent2D(8, 8)) : null);
            }

            Platform.GlColorMask(true, true, true, true);
            Platform.GlScissorFlag(false);
            byte[] pixels = Read(target.ColorTextureIds[0]);
            Platform.EndFrame();
            return pixels;
        }

        public unsafe (byte[] First, byte[] Second) RunTwoTargets(bool stated)
        {
            Platform.BeginFrame();
            Prepare(twoTargets);
            if (stated)
            {
                Platform.GlToggleBlend(false);
                Platform.RenderMesh(quad);
            }
            else
            {
                DrawReference(twoTargets, AttachmentBlend.For(false, EnumBlendMode.Standard), null);
            }
            byte[] first = Read(twoTargets.ColorTextureIds[0]);
            byte[] second = Read(twoTargets.ColorTextureIds[1]);
            Platform.EndFrame();
            return (first, second);
        }

        private void Prepare(FrameBufferRef frameBuffer)
        {
            VulkanDevice seam = Seam;
            // Clears are not part of the comparison: every attachment starts from the same colour.
            seam.BindFramebuffer(frameBuffer.FboId);
            seam.SetDrawBuffers(frameBuffer.FboId, (1 << frameBuffer.ColorTextureIds.Length) - 1);
            for (int i = 0; i < frameBuffer.ColorTextureIds.Length; i++) seam.ClearColor(i, 0f, 0.2f, 0.4f, 0.6f);
            Platform.StateDrawBuffers(frameBuffer.FboId, 1);

            Platform.CurrentFrameBuffer = frameBuffer;
            Platform.GlDisableDepthTest();
            Platform.GlDepthMask(false);
            Platform.GlDisableCullFace();

            Platform.UseShaderProgram(gui.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = gui;
            seam.SetUniformMatrix(gui.ProgramId, gui.uniformLocations["projectionMatrix"], Identity);
            seam.SetUniformMatrix(gui.ProgramId, gui.uniformLocations["modelViewMatrix"], Identity);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["rgbaIn"], 1f, 0.75f, 0.5f, 0.8f);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["noTexture"], 0f);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["applyColor"], 1);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["alphaTest"], 0f);
            Platform.BindProgramTexture2D(gui, "tex2d", texture, 0);
            Platform.BindProgramTexture2D(gui, "tex2dOverlay", 0, 1);
        }

        /// <summary>
        /// The quad drawn with everything written out: depth and cull off, slot 0 only, the
        /// full-target viewport, the program's two samplers on the gradient and on nothing.
        /// </summary>
        private void DrawReference(FrameBufferRef frameBuffer, AttachmentBlend attachment, Rect2D? scissor)
        {
            VulkanDevice seam = Seam;
            int meshId = ((VAO)quad).VaoId;
            NativePipeline? pipeline = seam.RequestNativePipeline(new NativePipelineDescription
            {
                ProgramId = gui.ProgramId,
                Blend = new[] { attachment },
                DepthTest = false,
                DepthWrite = false,
                Cull = CullModeFlags.None,
                Topology = seam.NativeMeshTopology(meshId),
                VertexLayoutId = seam.NativeMeshLayoutId(meshId),
                Targets = seam.NativeTargetFormats(frameBuffer.FboId, 1u)!,
            }, out string error);
            Assert.True(pipeline != null, error);
            Assert.True(seam.BeginNativePass(new NativePassDescription
            {
                Name = "Reference",
                FramebufferId = frameBuffer.FboId,
                ColorSlots = 1u,
                Reads = new[] { texture },
                Scissor = scissor,
            }));
            Assert.True(seam.DrawNativeMesh(pipeline!, meshId, new[]
            {
                new NativeTexture(pipeline!.Sampler("tex2d"), texture),
                new NativeTexture(pipeline.Sampler("tex2dOverlay"), 0),
            }));
            seam.EndNativePass();
        }

        private unsafe byte[] Read(int textureId)
        {
            VulkanDevice seam = Seam;
            int reader = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(reader, EnumFramebufferAttachment.ColorAttachment0, textureId, 0);
            seam.SetDrawBuffers(reader, 1);
            seam.BindFramebuffer(reader);
            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }
            seam.DeleteFramebuffer(reader);
            return pixels;
        }

        private static void Link(VulkanDevice seam, ShaderProgramBase program, string name, string[] uniforms)
        {
            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                name, ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), new ShaderCorpus.ShaderVariant());
            var linked = new LinkedProgram { PassName = name };
            foreach (ShaderStageSource stage in stages)
            {
                var shader = new LinkedShader { Type = stage.Stage, Code = stage.Code, PrefixCode = stage.PrefixCode };
                Assert.True(seam.CompileShader(shader));
                if (stage.Stage == EnumShaderType.VertexShader) linked.VertexShader = shader;
                else if (stage.Stage == EnumShaderType.FragmentShader) linked.FragmentShader = shader;
            }
            int id = seam.LinkProgram(linked);
            Assert.True(id > 0, seam.GetError() ?? "link failed");
            program.ProgramId = id;
            foreach (string uniform in uniforms)
            {
                int location = seam.GetUniformLocation(id, uniform);
                Assert.True(location != -1, name + " has no location for " + uniform);
                program.uniformLocations[uniform] = location;
            }
        }

        private static FrameBufferRef CreateTarget(VulkanDevice seam, int attachments)
        {
            var target = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new int[attachments],
            };
            for (int i = 0; i < attachments; i++)
            {
                target.ColorTextureIds[i] = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                seam.AttachTexture(target.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + i),
                    target.ColorTextureIds[i], 0);
            }
            seam.SetDrawBuffers(target.FboId, (1 << attachments) - 1);
            Assert.True(seam.CheckFramebufferComplete(target.FboId, out string status), status);
            return target;
        }

        private static void InstallFrameBuffers(StatedPlatform platform, FrameBufferRef target)
        {
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = target;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);
        }

        private static unsafe int Gradient(VulkanDevice seam)
        {
            var pixels = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                pixels[i] = (byte)(16 + x * 30);
                pixels[i + 1] = (byte)(32 + y * 25);
                pixels[i + 2] = (byte)(((x + y) & 1) * 200 + 20);
                pixels[i + 3] = (byte)(96 + ((x + y) & 3) * 40);
            }
            fixed (byte* first = pixels)
            {
                return seam.CreateTexture2D(8, 8, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)first, false);
            }
        }

        private static MeshData BuildQuad()
        {
            var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: false);
            float[] positions = { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f };
            float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            for (int i = 0; i < 4; i++)
            {
                mesh.AddVertexWithFlags(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                    uvs[i * 2], uvs[i * 2 + 1], ColorUtil.WhiteArgb, 0);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(index);
            return mesh;
        }
    }

    private static readonly Lazy<(string Directory, string Reason)> NativeManifest = new(BuildNativeShaders);

    private static (string, string) BuildNativeShaders()
    {
        if (!NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason)) return ("", reason);
        using (compiler)
        {
            var builder = new NativeShaderBuilder(compiler!);
            NativeShaderBuildResult result = builder.Build(Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk"), "gui");
            if (!result.Success) return ("", string.Join("\n", result.Errors));
            string root = Path.Combine(Path.GetTempPath(), "optimum-native-stated-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(result, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
}
