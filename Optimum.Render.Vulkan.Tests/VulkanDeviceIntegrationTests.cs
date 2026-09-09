using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Drives the backend the way the client will: through
/// <see cref="IOptimumGraphicsDevice" /> and nothing else.
///
/// Every other test in this project reaches past the seam into a specific
/// manager. This one deliberately does not, because the seam is the contract that
/// has to hold - <c>ClientPlatformWindows</c> will only ever see these methods,
/// in this order, with GL's semantics assumed.
/// </summary>
public class VulkanDeviceIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public VulkanDeviceIntegrationTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device)
    {
        var created = new VulkanDevice { DebugMode = true };
        if (created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device = created;
            return true;
        }

        output.WriteLine("Vulkan unavailable: " + failureReason);
        created.Dispose();
        device = null;
        return false;
    }

    [SkippableTheory]
    [InlineData(EnumDrawMode.Lines)]
    [InlineData(EnumDrawMode.LineStrip)]
    public unsafe void IndexedLineMeshesDrawOnlyEdgesAndRestoreTriangleTopology(EnumDrawMode mode)
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 32;
            int program = LinkProgram(seam, """
                #version 330 core
                layout(location = 0) in vec3 position;
                void main() { gl_Position = vec4(position, 1); }
                """, """
                #version 330 core
                out vec4 color;
                void main() { color = vec4(1); }
                """);
            // Deliberately shuffled vertices: drawing without these indices
            // introduces diagonals through the otherwise empty box interior.
            var data = new MeshData(4, 8) {
                xyz = new float[] { -.75f, -.75f, 0, .75f, .75f, 0,
                                    .75f, -.75f, 0, -.75f, .75f, 0 },
                VerticesCount = 4,
                Indices = mode == EnumDrawMode.Lines
                    ? new[] { 0, 2, 2, 1, 1, 3, 3, 0 }
                    : new[] { 0, 2, 1, 3, 0 },
                IndicesCount = mode == EnumDrawMode.Lines ? 8 : 5,
                mode = mode
            };
            int mesh = seam.CreateMesh(data, true);
            int texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 1);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.SetLineWidth(1);
            seam.ClearColor(0, 0, 0, 0, 1);
            seam.DrawMeshInstanced(mesh, 1);
            seam.Present();
            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            int lit = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte red = pixels[(y * size + x) * 4];
                if (red != 0) lit++;
                if (x >= 8 && x < 24 && y >= 8 && y < 24) Assert.Equal(0, red);
            }
            Assert.InRange(lit, 80, 112);

            // A following triangle mesh must change topology class again.
            data.mode = EnumDrawMode.Triangles;
            data.Indices = new[] { 0, 2, 1, 0, 1, 3 };
            data.IndicesCount = 6;
            int triangles = seam.CreateMesh(data, true);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.DrawMesh(triangles);
            seam.Present();
            fixed (byte* destination = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            Assert.Equal(255, pixels[(16 * size + 16) * 4]);
            AssertClean(seam);
        }
    }

    [SkippableFact]
    public unsafe void CloudMapShortUploadsKeepFullDensityAndBrightness()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            int program = LinkProgram(seam, """
                #version 330 core
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                                       -1 + ((gl_VertexID & 2) << 1), 0, 1);
                }
                """, """
                #version 330 core
                uniform sampler2D cloudData;
                out vec4 color;
                void main() {
                    // Check all 16 bits before the UNORM8 render target can round
                    // half density to either 127 or 128 (both legal in Vulkan).
                    // A raw signed-short upload, wrong scale, or missing negative
                    // clamp must still fail its channel, independently of the GPU.
                    uvec4 stored = uvec4(round(texelFetch(cloudData, ivec2(0), 0) * 65535.0));
                    color = vec4(equal(stored, uvec4(65535, 32769, 0, 0)));
                }
                """);
            int texture = seam.CreateTexture2DRaw(1, 1, 0x805B, IntPtr.Zero, 0); // GL_RGBA16
            short[] source = { short.MaxValue, 16384, 0, short.MinValue };
            seam.UploadTexture2DNormalizedShorts(texture, 0, 0, 0, 1, 1, source);
            Assert.Equal(new short[] { short.MaxValue, 16384, 0, short.MinValue }, source);

            int target = seam.CreateTexture2D(1, 1, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(1, 1);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 1);
            seam.SetSamplerUnit(program, "cloudData", 0);
            seam.BindTexture(0, texture);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetViewport(0, 0, 1, 1);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();
            seam.Present();
            var output = new byte[4];
            fixed (byte* destination = output)
                seam.ReadDefaultFramebuffer(0, 0, 1, 1, (IntPtr)destination);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, output);
            AssertClean(seam);
        }
    }

    [SkippableFact]
    public unsafe void AtlasCopiesWithinTheSameTextureReadTheContentsBeforeEachDraw()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            int program = LinkProgram(seam, """
                #version 330 core
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                                       -1 + ((gl_VertexID & 2) << 1), 0, 1);
                }
                """, """
                #version 330 core
                uniform sampler2D atlas;
                out vec4 color;
                void main() {
                    color = texelFetch(atlas, ivec2(gl_FragCoord.x < 1.0 ? 1 : 0, 0), 0);
                }
                """, "atlas-self-copy");
            byte[] original = { 255, 0, 0, 255, 0, 255, 0, 255 };
            byte[] swapped = { 0, 255, 0, 255, 255, 0, 0, 255 };
            int texture;
            fixed (byte* pixels = original)
                texture = seam.CreateTexture2D(2, 1, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
            int framebuffer = seam.CreateFramebuffer(2, 1);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetSamplerUnit(program, "atlas", 0);
            seam.BindTexture(0, texture);

            // The first draw starts with a shader-readable upload; later draws
            // start with a colour attachment. Refreshing the snapshot and leaving
            // the client's texture binding intact must both hold across frames.
            for (int frame = 0; frame < 4; frame++)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                if (frame == 0)
                {
                    var before = new byte[8];
                    fixed (byte* destination = before)
                        seam.ReadDefaultFramebuffer(0, 0, 2, 1, (IntPtr)destination);
                    Assert.Equal(original, before);
                }
                seam.UseProgram(program);
                seam.SetViewport(0, 0, 2, 1);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.DrawFullscreenTriangle();
                seam.Present();
                var output = new byte[8];
                fixed (byte* destination = output)
                    seam.ReadDefaultFramebuffer(0, 0, 2, 1, (IntPtr)destination);
                _output.WriteLine("frame " + frame + ": " + string.Join(", ", output));
                AssertClean(seam);
                Assert.Equal(frame % 2 == 0 ? swapped : original, output);
            }
            seam.DeleteFramebuffer(framebuffer);
            seam.DeleteTexture(texture);
            AssertClean(seam);
        }
    }

    /// <summary>
    /// A minimal shader stand-in. The client passes its own IShader and
    /// IShaderProgram implementations across the seam, so the device must work
    /// against the interfaces rather than any concrete type.
    /// </summary>
    private sealed class TestShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    private sealed class TestProgram : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId { get; set; }
        public string PassName { get; set; } = "test";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; } = true;
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();

        public void Use() { }
        public void Stop() { }
        public bool Compile() => true;
        public void Dispose() { }
        public void Uniform(string uniformName, float value) { }
        public void Uniform(string uniformName, int value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2f value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
        public bool HasUniform(string uniformName) => false;
    }

    private static int LinkProgram(
        IOptimumGraphicsDevice device, string vertexCode, string fragmentCode, string name = "test")
    {
        var vertex = new TestShader { Type = EnumShaderType.VertexShader, Code = vertexCode };
        var fragment = new TestShader { Type = EnumShaderType.FragmentShader, Code = fragmentCode };

        Assert.True(device.CompileShader(vertex));
        Assert.True(device.CompileShader(fragment));

        var program = new TestProgram { PassName = name, VertexShader = vertex, FragmentShader = fragment };
        int programId = device.LinkProgram(program);
        Assert.True(programId > 0, device.GetError() ?? "link failed");
        return programId;
    }

    [SkippableFact]
    public void TheDeviceReportsItsCapabilitiesThroughTheSeam()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;

            _output.WriteLine($"backend  : {seam.BackendName}");
            _output.WriteLine($"renderer : {seam.RendererString}");
            _output.WriteLine($"vendor   : {seam.VendorString}");
            _output.WriteLine($"version  : {seam.VersionString}");
            _output.WriteLine($"shaders  : {seam.ShaderVersionString}");
            _output.WriteLine($"max tex  : {seam.MaxTextureSize}");

            Assert.Equal("Vulkan", seam.BackendName);
            Assert.True(seam.MaxTextureSize >= 4096);
            Assert.True(seam.SupportsSSBOs);

            // The client parses this to decide whether a shader's #version is
            // supported, so it has to read as a GLSL version number.
            Assert.Matches(@"^\d\.\d+$", seam.ShaderVersionString);
        }
    }

    /// <summary>
    /// The whole path, driven only through the seam: compile, link, create a
    /// target, set state, draw, read back.
    /// </summary>
    [SkippableFact]
    public unsafe void AFrameCanBeRenderedEntirelyThroughTheSeam()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 32;

            int programId = LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                uniform vec4 tint;
                out vec4 outColor;
                void main(void) { outColor = tint; }
                """);

            int texture = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);
            Assert.True(seam.CheckFramebufferComplete(framebuffer, out _));

            // The uniform reaches the shader through the generated block.
            int tint = seam.GetUniformLocation(programId, "tint");
            Assert.True(tint >= 0);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.ClearColor(0, 0f, 0f, 0f, 1f);

            seam.UseProgram(programId);
            seam.SetUniform(programId, tint, 0.25f, 0.5f, 0.75f, 1f);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();

            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            _output.WriteLine($"centre RGBA = {pixels[centre]}, {pixels[centre + 1]}, " +
                              $"{pixels[centre + 2]}, {pixels[centre + 3]}");

            // 0.25, 0.5, 0.75 in 8-bit, within rounding.
            Assert.InRange(pixels[centre + 0], 60, 68);
            Assert.InRange(pixels[centre + 1], 124, 132);
            Assert.InRange(pixels[centre + 2], 187, 195);
            Assert.Equal(255, pixels[centre + 3]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// Uniforms set at any point before a draw have to persist for the life of
    /// the program, which is what GL promises and what every render system
    /// assumes when it sets a uniform once and draws many times.
    /// </summary>
    [SkippableFact]
    public unsafe void UniformsPersistAcrossDrawsAndFrames()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 16;

            int programId = LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                uniform float level;
                out vec4 outColor;
                void main(void) { outColor = vec4(level, level, level, 1.0); }
                """);

            int texture = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int level = seam.GetUniformLocation(programId, "level");
            seam.UseProgram(programId);
            seam.SetUniform(programId, level, 1.0f);

            // Two frames, with the uniform set only in the first.
            for (int frame = 0; frame < 2; frame++)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.SetViewport(0, 0, size, size);
                seam.UseProgram(programId);
                seam.DrawFullscreenTriangle();
                seam.Present();
            }

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(255, pixels[centre]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// Texture ids are public API surface - mods read
    /// <c>LoadedTexture.TextureId</c> and hand it back - so they have to behave
    /// like GL names, including being reused after deletion.
    /// </summary>
    [SkippableFact]
    public void TextureAndFramebufferIdsBehaveLikeGlNames()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;

            int first = seam.CreateTexture2D(8, 8,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int second = seam.CreateTexture2D(8, 8,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            Assert.True(first > 0);
            Assert.NotEqual(first, second);

            int framebuffer = seam.CreateFramebuffer(8, 8);
            Assert.True(framebuffer > 0);

            seam.DeleteTexture(first);
            seam.DeleteFramebuffer(framebuffer);
        }
    }

    /// <summary>
    /// Sampler uniforms are pointed at texture units, and a texture bound to that
    /// unit has to reach the shader. This is the path every textured draw takes.
    /// </summary>
    [SkippableFact]
    public unsafe void ATextureBoundToAUnitIsSampledByTheShader()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 16;

            int programId = LinkProgram(seam, """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """, """
                #version 330 core
                uniform sampler2D source;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = texture(source, uv); }
                """);

            // A source texture filled with a known colour.
            var sourcePixels = new byte[size * size * 4];
            for (int i = 0; i < sourcePixels.Length; i += 4)
            {
                sourcePixels[i + 0] = 10;
                sourcePixels[i + 1] = 200;
                sourcePixels[i + 2] = 30;
                sourcePixels[i + 3] = 255;
            }

            int source;
            fixed (byte* data = sourcePixels)
            {
                source = seam.CreateTexture2D(size, size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
            }

            int target = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(programId);
            seam.SetSamplerUnit(programId, "source", 0);
            seam.BindTexture(0, source);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(10, pixels[centre + 0]);
            Assert.Equal(200, pixels[centre + 1]);
            Assert.Equal(30, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// The multi-pass case the whole renderer is built out of: one pass renders
    /// into a texture, a later pass in the same frame samples it.
    ///
    /// In GL that needs nothing at all. In Vulkan the texture is left in the
    /// colour-attachment layout by the first pass and a shader read of it in that
    /// layout is invalid - the driver is entitled to abandon the work, and on this
    /// machine it did, losing the device partway through the first world load.
    /// The device has to notice and transition it before the second draw.
    /// </summary>
    [SkippableFact]
    public unsafe void ATextureRenderedIntoIsSampledCorrectlyByALaterPass()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 16;

            const string fullscreenVertex = """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """;

            int writeProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = vec4(40.0 / 255.0, 90.0 / 255.0, 160.0 / 255.0, 1.0); }
                """);

            int copyProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                uniform sampler2D source;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = texture(source, uv); }
                """);

            int intermediate = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int final = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            int firstPass = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(firstPass, EnumFramebufferAttachment.ColorAttachment0, intermediate, 0);
            seam.SetDrawBuffers(firstPass, 0b1);

            int secondPass = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(secondPass, EnumFramebufferAttachment.ColorAttachment0, final, 0);
            seam.SetDrawBuffers(secondPass, 0b1);

            seam.BeginFrame();

            // Pass one leaves `intermediate` as a colour attachment.
            seam.BindFramebuffer(firstPass);
            seam.UseProgram(writeProgram);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            // Pass two samples it. Nothing here announces the change of role.
            seam.BindFramebuffer(secondPass);
            seam.UseProgram(copyProgram);
            seam.SetSamplerUnit(copyProgram, "source", 0);
            seam.BindTexture(0, intermediate);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(secondPass);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(40, pixels[centre + 0]);
            Assert.Equal(90, pixels[centre + 1]);
            Assert.Equal(160, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// The composition case: render into attachment 0 of a framebuffer while
    /// sampling its attachment 1, which glDrawBuffers has masked off.
    ///
    /// The masked slot is not part of the rendering scope, so it must be readable
    /// rather than held in the colour-attachment layout the previous pass left it
    /// in. It is also the one slot a scope-bounded transition loop never reaches,
    /// because it sits above the highest enabled attachment.
    /// </summary>
    [SkippableFact]
    public unsafe void AnAttachmentMaskedOutOfTheDrawCanBeSampledByIt()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 16;

            const string fullscreenVertex = """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """;

            // Pass one writes both attachments, leaving both as colour attachments.
            int fillProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                layout(location = 1) out vec4 outGlow;
                void main(void)
                {
                    outColor = vec4(0.0, 0.0, 0.0, 1.0);
                    outGlow = vec4(20.0 / 255.0, 130.0 / 255.0, 240.0 / 255.0, 1.0);
                }
                """, "fill");

            // Pass two writes attachment 0 only, reading attachment 1.
            int composeProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                uniform sampler2D glow;
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = texture(glow, uv); }
                """, "compose");

            int color = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int glowTexture = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, color, 0);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment1, glowTexture, 0);

            seam.BeginFrame();

            seam.BindFramebuffer(framebuffer);
            seam.SetDrawBuffers(framebuffer, 0b11);
            seam.UseProgram(fillProgram);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            // Attachment 1 drops out of the scope and becomes an input.
            seam.SetDrawBuffers(framebuffer, 0b01);
            seam.UseProgram(composeProgram);
            seam.SetSamplerUnit(composeProgram, "glow", 0);
            seam.BindTexture(0, glowTexture);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.SetDrawBuffers(framebuffer, 0b01);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(20, pixels[centre + 0]);
            Assert.Equal(130, pixels[centre + 1]);
            Assert.Equal(240, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// A uniform block the shader declares itself, fed by the client's own UBO.
    ///
    /// The client creates one of these per program and updates it directly; the
    /// device has to route it into the descriptor set for the block of that name,
    /// or the binding is read without ever having been written.
    /// </summary>
    [SkippableFact]
    public unsafe void AClientUniformBufferSuppliesTheBlockTheShaderDeclares()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 16;

            int programId = LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                layout(std140) uniform Tint { vec4 tint; };
                out vec4 outColor;
                void main(void) { outColor = tint; }
                """);

            int target = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int ubo = seam.CreateUniformBuffer(programId, 0, "Tint", sizeof(float) * 4);
            Assert.True(ubo > 0);

            var tint = new float[] { 60f / 255f, 120f / 255f, 180f / 255f, 1f };
            fixed (float* values = tint)
            {
                seam.UpdateUniformBuffer(ubo, (IntPtr)values, 0, sizeof(float) * 4);
            }
            seam.BindUniformBuffer(ubo);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(programId);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(60, pixels[centre + 0]);
            Assert.Equal(120, pixels[centre + 1]);
            Assert.Equal(180, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// The loading-screen crash. A texture is deleted and a new one takes its
    /// place; the driver may give the new image view the very handle value the
    /// old one had. A set cache keyed by handle then serves the stale set and the
    /// GPU reads freed memory. The cache must drop a deleted texture's sets and
    /// serve a successor its own, and the deferred free must land only after
    /// every frame that could have bound the old set has finished - which the
    /// validation layer checks for us.
    /// </summary>
    [SkippableFact]
    public unsafe void ADeletedTextureTakesItsDescriptorSetsWithIt()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 8;

            int program = LinkProgram(seam, """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """, """
                #version 330 core
                uniform sampler2D source;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = texture(source, uv); }
                """);

            int target = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int first = SolidTexture(seam, size, 10, 20, 30);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetSamplerUnit(program, "source", 0);
            seam.BindTexture(0, first);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            int cachedWhileAlive = device!.CachedDescriptorSets;
            Assert.True(cachedWhileAlive >= 1, "the draw should have cached a sampler set");

            seam.DeleteTexture(first);

            // The next frame evicts the set; two more let the deferred free run
            // once the frame that bound it has signalled its fence.
            for (int i = 0; i < 3; i++)
            {
                seam.BeginFrame();
                seam.Present();
            }
            Assert.Equal(cachedWhileAlive - 1, device.CachedDescriptorSets);

            int second = SolidTexture(seam, size, 200, 100, 50);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetSamplerUnit(program, "source", 0);
            seam.BindTexture(0, second);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(200, pixels[centre + 0]);
            Assert.Equal(100, pixels[centre + 1]);
            Assert.Equal(50, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    private static unsafe int SolidTexture(IOptimumGraphicsDevice seam, int size, byte r, byte g, byte b)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        fixed (byte* data = pixels)
        {
            return seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
        }
    }

    /// <summary>
    /// The pooled-chunk case. One mesh holds many chunks; each is written with
    /// the byte offset of its own slice in every part, exactly as GL's
    /// glBufferSubData destination offset works.
    ///
    /// Writing them all at zero is not a subtle corruption - every chunk in the
    /// world lands on top of the first, which renders as no terrain at all.
    /// </summary>
    [SkippableFact]
    public unsafe void AMeshUpdateWritesEachPartAtItsOwnDestinationOffset()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int verticesPerSlice = 3;
            const int slices = 4;

            int meshId = seam.CreateEmptyMesh(
                xyzSize: slices * verticesPerSlice * 3 * sizeof(float),
                normalsSize: 0,
                uvSize: slices * verticesPerSlice * 2 * sizeof(float),
                rgbaSize: slices * verticesPerSlice * 4,
                flagsSize: 0,
                indicesSize: slices * verticesPerSlice * sizeof(int),
                customFloats: null, customShorts: null, customBytes: null, customInts: null,
                drawMode: EnumDrawMode.Triangles, staticDraw: false, ssbo: false);
            Assert.True(meshId > 0);

            // Each slice writes a value identifying itself, at its own offset.
            for (int slice = 0; slice < slices; slice++)
            {
                var xyz = new float[verticesPerSlice * 3];
                for (int i = 0; i < xyz.Length; i++) xyz[i] = slice * 100 + i;

                // XyzCount is derived from VerticesCount, so only the offset and
                // the vertex count need setting.
                var data = new MeshData(verticesPerSlice, verticesPerSlice)
                {
                    xyz = xyz,
                    XyzOffset = slice * verticesPerSlice * 3 * sizeof(float),
                    VerticesCount = verticesPerSlice,
                };

                seam.UpdateMesh(meshId, data);
            }

            // Read the whole buffer back and confirm each slice kept its place.
            IntPtr mapped = seam.GetMappedPointer(meshId, EnumMeshBufferPart.Xyz);
            Assert.NotEqual(IntPtr.Zero, mapped);

            var actual = new float[slices * verticesPerSlice * 3];
            fixed (float* destination = actual)
            {
                System.Buffer.MemoryCopy((void*)mapped, destination,
                    actual.Length * sizeof(float), actual.Length * sizeof(float));
            }

            for (int slice = 0; slice < slices; slice++)
            {
                int at = slice * verticesPerSlice * 3;
                Assert.Equal(slice * 100f, actual[at]);
                Assert.Equal(slice * 100f + 1, actual[at + 1]);
            }

            seam.DeleteMesh(meshId);
            AssertClean(seam);
        }
    }

    private static void AssertClean(IOptimumGraphicsDevice device)
    {
        string? diagnostics = device.GetError();
        if (diagnostics == null) return;

        Assert.False(
            diagnostics.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("VUID", StringComparison.Ordinal),
            "validation errors:\n" + diagnostics);
    }
}
