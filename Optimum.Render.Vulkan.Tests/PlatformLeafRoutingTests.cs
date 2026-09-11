using System;
using System.IO;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 5: the leaf operations the render systems outside the
/// platform used to send through the static device seam are platform virtuals, and the seam
/// is gone. These drive them through VulkanClientPlatform with no GL context - an override
/// that is missing lands in the ClientPlatformWindows GL body and throws - and read back what
/// the device made of them. The fork bridge the forked mods use is exercised the same way.
/// </summary>
public class PlatformLeafRoutingTests
{
    private readonly ITestOutputHelper _output;

    public PlatformLeafRoutingTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private sealed class Session : IDisposable
    {
        private readonly string _dataPath;

        public VulkanClientPlatform Platform { get; }

        public VulkanDevice Seam => Platform.GraphicsDevice!;

        private Session(VulkanClientPlatform platform, string dataPath)
        {
            Platform = platform;
            _dataPath = dataPath;
        }

        public static Session? TryOpen(ITestOutputHelper output)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-leaf-routing-test-" + Guid.NewGuid().ToString("N"));
            var platform = new VulkanClientPlatform(null!)
            {
                DeviceFactory = GpuTest.NewDevice,
                CrashMarkerDataPath = dataPath,
            };
            if (!platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason))
            {
                output.WriteLine("Vulkan unavailable: " + reason);
                platform.ShutdownGraphics();
                return null;
            }
            return new Session(platform, dataPath);
        }

        public void Dispose()
        {
            Platform.ShutdownGraphics();
            try
            {
                Directory.Delete(_dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// The backend state and the fork bridge follow the platform's graphics: published by
    /// InitializeGraphics, withdrawn by ShutdownGraphics.
    /// </summary>
    [SkippableFact]
    public void BackendNameAndForkBridgeFollowTheInstall()
    {
        Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        try
        {
            Assert.Equal("Vulkan", session!.Platform.GraphicsBackendName);
            Assert.True(OptimumRender.IsVulkan);
            Assert.NotNull(OptimumForkGraphics.Active);
        }
        finally
        {
            session!.Dispose();
        }
        Assert.Null(OptimumForkGraphics.Active);
        Assert.False(OptimumRender.IsVulkan);
        Assert.Null(session.Platform.GraphicsDevice);
    }

    /// <summary>
    /// LoadTextureFromRgbaPointer uploads a solid colour, ClearTextureRegion blanks its left
    /// half, the fork bridge builds and binds a framebuffer over it, and ReadDefaultFramebuffer
    /// reads the bound target back: left half zero, right half the uploaded colour. The LOD,
    /// sampler-LOD and depth-compare setters and the depth range run on the same texture with
    /// no GL context, so a missing override would throw.
    /// </summary>
    [SkippableFact]
    public unsafe void TextureUploadRegionClearAndReadbackReachTheDevice()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanClientPlatform platform = session!.Platform;
        OptimumForkGraphics fork = OptimumForkGraphics.Active!;
        const int size = 16;

        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 10;
            rgba[i + 1] = 200;
            rgba[i + 2] = 30;
            rgba[i + 3] = 255;
        }
        int texture;
        fixed (byte* pixels = rgba)
        {
            texture = platform.LoadTextureFromRgbaPointer(size, size, (IntPtr)pixels);
        }
        Assert.True(texture > 0);
        platform.ClearTextureRegion(texture, 0, 0, size / 2, size, new int[size / 2 * size]);

        platform.SetTextureLodBias(new[] { texture }, -0.5f);
        platform.SetTextureDepthCompare(texture, 0);
        int sampler = session.Seam.CreateSampler(true);
        platform.SetSamplerLodBias(sampler, 0.25f);
        platform.SetDepthRange(0f, 20000f);

        int framebuffer = fork.CreateFramebuffer(size, size);
        fork.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        fork.SetDrawBuffers(framebuffer, 1);

        var read = new byte[size * size * 4];
        platform.BeginFrame();
        fork.BindFramebuffer(framebuffer);
        fork.SetViewport(0, 0, size, size);
        fixed (byte* destination = read)
        {
            platform.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        platform.EndFrame();

        int row = size / 2 * size;
        int left = (row + 2) * 4;
        int right = (row + size - 3) * 4;
        _output.WriteLine($"left RGBA = {read[left]}, {read[left + 1]}, {read[left + 2]}, {read[left + 3]}");
        _output.WriteLine($"right RGBA = {read[right]}, {read[right + 1]}, {read[right + 2]}, {read[right + 3]}");
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, read[left..(left + 4)]);
        Assert.Equal(new byte[] { 10, 200, 30, 255 }, read[right..(right + 4)]);

        fork.DeleteFramebuffer(framebuffer);
        platform.GLDeleteTexture(texture);
        session.Seam.DeleteSampler(sampler);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// ClearDefaultDepth clears the bound target's depth like glClearBuffer: 0.25 stays 0.25,
    /// and ScreenManager's 20000 clamps to 1.
    /// </summary>
    [SkippableFact]
    public void ClearDefaultDepthClampsLikeGl()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanClientPlatform platform = session!.Platform;
        VulkanDevice seam = session.Seam;
        const int size = 8;

        int colour = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int depth = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        seam.SetDrawBuffers(framebuffer, 1);

        float Cleared(float value)
        {
            platform.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthMask(true);
            platform.ClearDefaultDepth(value);
            platform.EndFrame();
            // The parity readback only runs inside a frame; the next one sees the clear presented.
            platform.BeginFrame();
            OptimumTextureReadback? readback = seam.ReadTextureForParity(depth);
            platform.EndFrame();
            Assert.NotNull(readback);
            Assert.NotNull(readback!.Floats);
            return readback.Floats![size / 2 * size + size / 2];
        }

        float quarter = Cleared(0.25f);
        float clamped = Cleared(20000f);
        _output.WriteLine("depth after 0.25 = " + quarter + ", after 20000 = " + clamped);
        Assert.Equal(0.25f, quarter, 5);
        Assert.Equal(1f, clamped, 5);

        seam.DeleteFramebuffer(framebuffer);
        seam.DeleteTexture(colour);
        seam.DeleteTexture(depth);
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// The sun probe's query protocol through the platform over several presented frames:
    /// GenOcclusionQuery, Begin, a fullscreen draw, End, then TryGetOcclusionQueryResult polled
    /// each later frame until it reports - and it reports samples. A second query around no
    /// draw reports zero. Present runs between frames and nothing reads back inside the loop.
    /// </summary>
    [SkippableFact]
    public void OcclusionQueriesCountSamplesAcrossFrames()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanClientPlatform platform = session!.Platform;
        VulkanDevice seam = session.Seam;
        const int size = 16;

        int colour = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            out vec4 outColor;
            void main(void) { outColor = vec4(1.0); }
            """, "leaf-occlusion");

        int drawn = platform.GenOcclusionQuery();
        int empty = platform.GenOcclusionQuery();
        Assert.True(drawn > 0 && empty > 0 && drawn != empty);

        platform.BeginFrame();
        seam.BindFramebuffer(framebuffer);
        seam.SetViewport(0, 0, size, size);
        platform.GlDisableDepthTest();
        platform.GlDisableCullFace();
        platform.GlToggleBlend(false);
        platform.GlColorMask(false, false, false, false);
        platform.UseShaderProgram(program);
        platform.BeginOcclusionQuery(drawn);
        platform.RenderFullscreenTriangle(null!);
        platform.EndOcclusionQuery(drawn);
        platform.BeginOcclusionQuery(empty);
        platform.EndOcclusionQuery(empty);
        platform.UseShaderProgram(0);
        platform.GlColorMask(true, true, true, true);
        platform.EndFrame();

        bool drawnReported = false;
        bool emptyReported = false;
        int drawnSamples = 0;
        int emptySamples = -1;
        for (int frame = 0; frame < 60 && !(drawnReported && emptyReported); frame++)
        {
            platform.BeginFrame();
            if (!drawnReported) drawnReported = platform.TryGetOcclusionQueryResult(drawn, out drawnSamples);
            if (!emptyReported) emptyReported = platform.TryGetOcclusionQueryResult(empty, out emptySamples);
            platform.EndFrame();
        }

        _output.WriteLine("drawn: " + drawnReported + " " + drawnSamples + ", empty: " + emptyReported + " " + emptySamples);
        Assert.True(drawnReported, "the drawn query never reported within 60 frames");
        Assert.True(emptyReported, "the empty query never reported within 60 frames");
        Assert.True(drawnSamples > 0);
        Assert.Equal(0, emptySamples);

        platform.DeleteOcclusionQuery(drawn);
        platform.DeleteOcclusionQuery(empty);
        seam.DeleteProgram(program);
        seam.DeleteFramebuffer(framebuffer);
        seam.DeleteTexture(colour);
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// The OIT targets and pass state through the platform: CreateOitTargets attaches the
    /// reveal texture at 0 and the three-layer accumulation array at 3-5 of a three-attachment
    /// transparent framebuffer, BeginOitAccumulation enables all six draw buffers and clears
    /// 0 and 1 to one and 3-5 to zero, BindOitTextures binds units 6 and 7. The reveal target
    /// and the framebuffer's own attachment 1 read back as one; its attachment 2, outside the
    /// clear set, keeps its earlier clear colour.
    /// </summary>
    [SkippableFact]
    public void OitTargetsAndAccumulationClearsReachTheDevice()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanClientPlatform platform = session!.Platform;
        VulkanDevice seam = session.Seam;
        const int size = 8;

        RawTexture Colour() => new()
        {
            Width = size,
            Height = size,
            PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
            PixelFormat = EnumTexturePixelFormat.Rgba,
            MinFilter = EnumTextureFilter.Nearest,
            MagFilter = EnumTextureFilter.Nearest,
        };
        var attrs = new FramebufferAttrs("transparent", size, size)
        {
            Attachments = new[]
            {
                new FramebufferAttrsAttachment { AttachmentType = EnumFramebufferAttachment.ColorAttachment0, Texture = Colour() },
                new FramebufferAttrsAttachment { AttachmentType = EnumFramebufferAttachment.ColorAttachment1, Texture = Colour() },
                new FramebufferAttrsAttachment { AttachmentType = EnumFramebufferAttachment.ColorAttachment2, Texture = Colour() },
            },
        };
        int oitProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D OITreveal;
            uniform sampler2DArray OITaccumulation;
            out vec4 outColor;
            void main(void) { outColor = texture(OITreveal, vec2(0.5)) + texture(OITaccumulation, vec3(0.5, 0.5, 0.0)); }
            """, "leaf-oit");

        platform.BeginFrame();
        FrameBufferRef transparent = platform.CreateFramebuffer(attrs);
        platform.CurrentFrameBuffer = transparent;
        platform.ClearFrameBuffer(transparent, new[] { 40f / 255f, 80f / 255f, 120f / 255f, 1f }, clearDepthBuffer: false);
        platform.EndFrame();

        platform.CreateOitTargets(transparent, 3, out int reveal, out int accum);
        Assert.True(reveal > 0 && accum > 0 && reveal != accum);

        platform.BeginFrame();
        platform.CurrentFrameBuffer = transparent;
        platform.SetProgramSamplerUnit(oitProgram, "OITaccumulation", 7);
        platform.BeginOitAccumulation(transparent);
        platform.BindOitTextures(reveal, accum);
        platform.EndFrame();

        byte[] Centre(int textureId)
        {
            OptimumTextureReadback? readback = seam.ReadTextureForParity(textureId);
            Assert.NotNull(readback);
            Assert.NotNull(readback!.Bytes);
            int offset = (size / 2 * size + size / 2) * 4;
            return readback.Bytes![offset..(offset + 4)];
        }

        // The parity readback only runs inside a frame; this one follows the presented clears.
        platform.BeginFrame();
        byte[] revealTexel = Centre(reveal);
        byte[] second = Centre(transparent.ColorTextureIds[1]);
        byte[] third = Centre(transparent.ColorTextureIds[2]);
        platform.EndFrame();
        _output.WriteLine("reveal = " + string.Join(",", revealTexel) + "; colour1 = " + string.Join(",", second) + "; colour2 = " + string.Join(",", third));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, revealTexel);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, second);
        Assert.Equal(new byte[] { 40, 80, 120, 255 }, third);

        platform.GLDeleteTexture(accum);
        platform.GLDeleteTexture(reveal);
        platform.DisposeFrameBuffer(transparent);
        seam.DeleteProgram(oitProgram);
        GpuTest.AssertClean(seam);
    }

    private static MeshData Quad() => new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
    {
        xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
        VerticesCount = 4,
        Indices = new[] { 0, 1, 2, 0, 2, 3 },
        IndicesCount = 6,
    };

    /// <summary>
    /// Phase 1 review regression: MeshRef.Dispose (which the client and mods call directly as
    /// often as DeleteMesh) releases the device mesh, exactly once. Before the fix VAO.Dispose
    /// reached an empty DeleteVertexArrayHandles override and every directly disposed mesh
    /// leaked its device buffers for the rest of the session.
    /// </summary>
    [SkippableFact]
    public void DisposingAMeshRefReleasesTheDeviceMeshOnce()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanClientPlatform platform = session!.Platform;
        VulkanDevice seam = session.Seam;
        ClientPlatformAbstract previous = ScreenManager.Platform;
        ScreenManager.Platform = platform;
        try
        {
            MeshRef direct = platform.UploadMesh(Quad());
            int directId = ((VAO)direct).VaoId;
            Assert.NotNull(seam.MeshesForTests.Get(directId));
            direct.Dispose();
            Assert.True(direct.Disposed);
            Assert.Null(seam.MeshesForTests.Get(directId));

            MeshRef viaPlatform = platform.UploadMesh(Quad());
            int viaId = ((VAO)viaPlatform).VaoId;
            platform.DeleteMesh(viaPlatform);
            Assert.True(viaPlatform.Disposed);
            Assert.Null(seam.MeshesForTests.Get(viaId));

            // The freed ids are reused; disposing the released VAOs again must not free the
            // mesh that now holds one of them.
            MeshRef survivor = platform.UploadMesh(Quad());
            int survivorId = ((VAO)survivor).VaoId;
            direct.Dispose();
            viaPlatform.Dispose();
            platform.DeleteMesh(viaPlatform);
            Assert.NotNull(seam.MeshesForTests.Get(survivorId));

            // The released meshes are destroyed on the timelines like any other resource.
            for (int frame = 0; frame < 4; frame++)
            {
                platform.BeginFrame();
                platform.EndFrame();
            }
            Assert.NotNull(seam.MeshesForTests.Get(survivorId));
            survivor.Dispose();
            Assert.Null(seam.MeshesForTests.Get(survivorId));
        }
        finally
        {
            ScreenManager.Platform = previous;
        }
        GpuTest.AssertClean(seam);
    }
}
