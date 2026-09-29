// Source: Optimum.Render.Vulkan.Tests/PlatformDeviceRoutingTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Vulkan-native plan, Phase 1A step 4: ClientPlatformWindows keeps only the GL path, and
/// VulkanClientPlatform overrides every graphics member with device calls. These drive the
/// moved overrides through the platform with no GL context: an override that is missing
/// falls into a GL call and throws, one that reaches the wrong device call changes the pixels.
/// </summary>
public class PlatformDeviceRoutingTests
{
    private readonly ITestOutputHelper _output;

    public PlatformDeviceRoutingTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The runtime self-check (VerifyHost) has to cover every override whose base member
    /// only exists in the patched lib - an injected ClientPlatformAbstract virtual or a
    /// member virtualized in place on ClientPlatformWindows - or an unpatched lib would
    /// bypass it mid-frame instead of failing the install.
    /// </summary>
    [Fact]
    public void EveryOverrideOfAPatchedVirtualIsInTheSelfCheck()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int checkedOverrides = 0;
        foreach (MethodInfo method in typeof(VulkanClientPlatform).GetMethods(flags))
        {
            MethodInfo definition = method.GetBaseDefinition();
            if (definition == method || method.IsSpecialName) continue;
            Type owner = definition.DeclaringType!;
            bool onAbstract = owner == typeof(ClientPlatformAbstract);
            if (!(onAbstract && !definition.IsAbstract) && owner != typeof(ClientPlatformWindows)) continue;

            ParameterInfo[] parameters = method.GetParameters();
            bool listed = false;
            foreach (VulkanClientPlatform.ExpectedVirtual expected in VulkanClientPlatform.ExpectedVirtuals)
            {
                if (expected.OnAbstract != onAbstract || expected.Name != method.Name || expected.ParameterTypeNames.Length != parameters.Length) continue;
                bool same = true;
                for (int i = 0; i < parameters.Length && same; i++)
                    same = expected.ParameterTypeNames[i] == parameters[i].ParameterType.Name;
                listed |= same;
            }
            Assert.True(listed, owner.Name + "." + method.Name + " is overridden but not in VulkanClientPlatform.ExpectedVirtuals");
            checkedOverrides++;
        }
        _output.WriteLine("patched virtuals overridden: " + checkedOverrides);
        Assert.True(checkedOverrides >= 40);
    }

    private abstract class BareAbstract { }
    private class BareWindows : BareAbstract { }
    private sealed class SealedWindows : BareAbstract { }

    [Fact]
    public void PatchedHostIsAcceptedAndMissingOrSealedHostsAreRejected()
    {
        Assert.True(typeof(VulkanClientPlatform).IsSubclassOf(typeof(ClientPlatformWindows)));
        Assert.False(typeof(ClientPlatformWindows).IsSealed);
        Assert.True(VulkanClientPlatform.VerifyHost(typeof(ClientPlatformAbstract), typeof(ClientPlatformWindows), out string? reason), reason);
        Assert.Null(reason);
        Assert.False(VulkanClientPlatform.VerifyHost(typeof(BareAbstract), typeof(BareWindows), out string? missing));
        Assert.Contains("InitializeGraphics", missing);
        Assert.False(VulkanClientPlatform.VerifyHost(typeof(BareAbstract), typeof(SealedWindows), out string? sealedReason));
        Assert.Contains("sealed", sealedReason);
        Assert.NotNull(new VulkanClientPlatform(null!).Logger);
    }

    [Fact]
    public void FailedInstallNeverConstructsADevice()
    {
        var platform = new VulkanClientPlatform(null!);
        int created = 0;
        platform.DeviceFactory = () => { created++; return GpuTest.NewDevice(); };
        string? previous = Environment.GetEnvironmentVariable(VulkanClientPlatform.ForceInstallFailureVariable);
        Environment.SetEnvironmentVariable(VulkanClientPlatform.ForceInstallFailureVariable, "1");
        try
        {
            Assert.False(platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason));
            Assert.Equal("forced by OPTIMUM_VULKAN_FORCE_INSTALL_FAILURE", reason);
            Assert.Equal(0, created); Assert.Null(platform.GraphicsDevice);
        }
        finally { Environment.SetEnvironmentVariable(VulkanClientPlatform.ForceInstallFailureVariable, previous); }
    }

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Frame 1: CreateFramebuffer, the CurrentFrameBuffer setter (BindCurrentFrameBuffer),
    /// the state setters and ClearFrameBuffer (ClearBoundFrameBuffer) through the platform.
    /// Frame 2: a program drawn with RenderFullscreenTriangle under GlScissor/GlScissorFlag
    /// over the left half only. Frame 3 reads back: left half the draw colour, right half the
    /// clear colour. BeginFrame/EndFrame are the platform's bracket; DisposeFrameBuffer and
    /// GLDeleteTexture release everything, and validation (sync, best) stays clean.
    /// </summary>
    [SkippableFact]
    public unsafe void FramebufferClearScissorAndDrawReachTheDeviceThroughThePlatform()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-device-routing-test-" + Guid.NewGuid().ToString("N"));
        var platform = new VulkanClientPlatform(null!)
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };
        try
        {
            bool installed = platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");
            VulkanDevice seam = platform.GraphicsDevice!;
            const int size = 16;

            var attrs = new FramebufferAttrs("routed", size, size)
            {
                Attachments = new[]
                {
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                        Texture = new RawTexture
                        {
                            Width = size,
                            Height = size,
                            PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                            PixelFormat = EnumTexturePixelFormat.Rgba,
                            MinFilter = EnumTextureFilter.Nearest,
                            MagFilter = EnumTextureFilter.Nearest,
                        },
                    },
                },
            };
            int program = GpuTest.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                out vec4 outColor;
                void main(void) { outColor = vec4(200.0 / 255.0, 40.0 / 255.0, 90.0 / 255.0, 1.0); }
                """, "routed-draw");

            platform.BeginFrame();
            FrameBufferRef target = platform.CreateFramebuffer(attrs);
            Assert.True(target.FboId > 0);
            Assert.Single(target.ColorTextureIds);
            platform.CurrentFrameBuffer = target;
            Assert.Same(target, platform.CurrentFrameBuffer);
            platform.GlDisableDepthTest();
            platform.GlDisableCullFace();
            platform.GlToggleBlend(false);
            platform.ClearFrameBuffer(target, new[] { 20f / 255f, 140f / 255f, 220f / 255f, 1f }, clearDepthBuffer: false);
            platform.EndFrame();

            platform.BeginFrame();
            platform.CurrentFrameBuffer = target;
            platform.GlDisableDepthTest();
            platform.GlDisableCullFace();
            platform.GlToggleBlend(false);
            platform.UseShaderProgram(program);
            platform.GlScissor(0, 0, size / 2, size);
            platform.GlScissorFlag(true);
            Assert.True(platform.GlScissorFlagEnabled);
            platform.RenderFullscreenTriangle(null!);
            platform.GlScissorFlag(false);
            Assert.False(platform.GlScissorFlagEnabled);
            platform.UseShaderProgram(0);
            platform.EndFrame();

            var pixels = new byte[size * size * 4];
            platform.BeginFrame();
            platform.CurrentFrameBuffer = target;
            fixed (byte* destination = pixels)
            {
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }
            platform.EndFrame();

            int row = size / 2 * size;
            int left = (row + 2) * 4;
            int right = (row + size - 3) * 4;
            _output.WriteLine($"left RGBA = {pixels[left]}, {pixels[left + 1]}, {pixels[left + 2]}, {pixels[left + 3]}");
            _output.WriteLine($"right RGBA = {pixels[right]}, {pixels[right + 1]}, {pixels[right + 2]}, {pixels[right + 3]}");
            Assert.Equal(new byte[] { 200, 40, 90, 255 }, pixels[left..(left + 4)]);
            Assert.Equal(new byte[] { 20, 140, 220, 255 }, pixels[right..(right + 4)]);

            platform.DisposeFrameBuffer(target);
            // Linked on the device directly above, so freed there too.
            seam.DeleteProgram(program);
            GpuTest.AssertClean(seam);
        }
        finally
        {
            platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/PlatformLeafRoutingTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.IO;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

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
            Assert.True(File.Exists(Path.Combine(dataPath, ".optimum", "vulkan-session.lock")));
            return new Session(platform, dataPath);
        }

        public void Dispose()
        {
            Platform.ShutdownGraphics();
            Assert.Null(Platform.GraphicsDevice);
            Assert.False(File.Exists(Path.Combine(_dataPath, ".optimum", "vulkan-session.lock")));
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
        // The platform's readback is the client's seam, and its contract is the
        // OpenGL body's: glReadPixels(..., GL_BGRA, ...). So the R=10, G=200, B=30
        // texel that went in comes back B, G, R, A = 30, 200, 10, 255. Asserting
        // RGBA here is what let every Vulkan screenshot and AVI recording ship with
        // red and blue exchanged, because Screenshot.GrabScreenshot decodes into an
        // SKBitmap declared Bgra8888 (wave-1 review, 2026-09-12). The device-level
        // VulkanDevice.ReadDefaultFramebuffer still hands texels back in their
        // stored order; the conversion is VulkanClientPlatform's.
        _output.WriteLine($"left BGRA = {read[left]}, {read[left + 1]}, {read[left + 2]}, {read[left + 3]}");
        _output.WriteLine($"right BGRA = {read[right]}, {read[right + 1]}, {read[right + 2]}, {read[right + 3]}");
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, read[left..(left + 4)]);
        Assert.Equal(new byte[] { 30, 200, 10, 255 }, read[right..(right + 4)]);

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
        int program = GpuTest.LinkProgram(seam, FullscreenVertex, """
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
        int oitProgram = GpuTest.LinkProgram(seam, FullscreenVertex, """
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
}

// Source: Optimum.Render.Vulkan.Tests/PlatformProgramRoutingTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.IO;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Vulkan-native plan, Phase 1A step 3: ShaderProgramBase and UBO no longer talk to the
/// device or to GL; they call ScreenManager.Platform. These drive the lib's own classes
/// (the donor the Cecil patch transplants) through a VulkanClientPlatform installed as
/// the client's platform, and read the pixels back, so a virtual that stops reaching the
/// device shows up as a wrong colour rather than as a missing call.
/// </summary>
public class PlatformProgramRoutingTests
{
    private readonly ITestOutputHelper _output;

    public PlatformProgramRoutingTests(ITestOutputHelper output) => _output = output;

    private sealed class RoutedProgram : ShaderProgramBase
    {
        public override bool Compile() => true;
    }

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    /// <summary>The installed platform, the platform it replaced and the crash-marker directory.</summary>
    private sealed class Session : IDisposable
    {
        private readonly ClientPlatformAbstract? _previous;
        private readonly string _dataPath;

        public VulkanClientPlatform Platform { get; }
        public VulkanDevice Seam => Platform.GraphicsDevice!;

        private Session(VulkanClientPlatform platform, ClientPlatformAbstract? previous, string dataPath)
        {
            Platform = platform;
            _previous = previous;
            _dataPath = dataPath;
        }

        public static Session? TryOpen(ITestOutputHelper output)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-routing-test-" + Guid.NewGuid().ToString("N"));
            var platform = new VulkanClientPlatform(null!)
            {
                DeviceFactory = GpuTest.NewDevice,
                CrashMarkerDataPath = dataPath,
            };
            if (!platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason))
            {
                output.WriteLine("Vulkan unavailable: " + reason);
                platform.ShutdownGraphics();
                TryDelete(dataPath);
                return null;
            }

            var session = new Session(platform, ScreenManager.Platform, dataPath);
            ScreenManager.Platform = platform;
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            ScreenManager.Platform = _previous!;
            Platform.ShutdownGraphics();
            TryDelete(_dataPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static int ColourTarget(VulkanDevice seam, int size)
    {
        int texture = seam.CreateTexture2D(size, size,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 0b1);
        return framebuffer;
    }

    private static void BeginDraw(VulkanDevice seam, int framebuffer, int size)
    {
        seam.BeginFrame();
        seam.BindFramebuffer(framebuffer);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.SetViewport(0, 0, size, size);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
    }

    private static unsafe byte[] ReadCentre(VulkanDevice seam, int framebuffer, int size, bool openFrame)
    {
        var pixels = new byte[size * size * 4];
        if (openFrame) seam.BeginFrame();
        seam.BindFramebuffer(framebuffer);
        fixed (byte* destination = pixels)
        {
            seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        if (openFrame) seam.Present();
        int centre = (size / 2 * size + size / 2) * 4;
        return new[] { pixels[centre], pixels[centre + 1], pixels[centre + 2], pixels[centre + 3] };
    }

    /// <summary>
    /// Use, the scalar/vector/integer-vector/matrix setters, Stop and Dispose, each
    /// called on the lib's ShaderProgramBase. Every uniform contributes to the colour,
    /// so any one of them failing to reach the device changes the pixel.
    /// </summary>
    [SkippableFact]
    public void UniformsSetOnAShaderProgramReachTheShaderThroughThePlatform()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanDevice seam = session!.Seam;
        const int size = 16;

        var program = new RoutedProgram { PassName = "routed-uniforms" };
        program.ProgramId = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform float red;
            uniform vec2 greenBlue;
            uniform int alphaOn;
            uniform ivec3 offsets;
            uniform mat4 transform;
            out vec4 outColor;
            void main(void)
            {
                vec4 moved = transform * vec4(0.0, 0.0, 0.0, 1.0);
                outColor = vec4(red,
                                greenBlue.x + float(offsets.y) / 255.0,
                                greenBlue.y + moved.x,
                                alphaOn == 1 ? 1.0 : 0.0);
            }
            """);
        foreach (string name in new[] { "red", "greenBlue", "alphaOn", "offsets", "transform" })
        {
            int location = seam.GetUniformLocation(program.ProgramId, name);
            Assert.True(location >= 0, name + " has no location");
            program.uniformLocations[name] = location;
        }

        int framebuffer = ColourTarget(seam, size);
        var transform = new float[16];
        transform[0] = transform[5] = transform[10] = transform[15] = 1f;
        transform[12] = 30f / 255f; // column-major translation x

        BeginDraw(seam, framebuffer, size);
        program.Use();
        Assert.Same(program, ShaderProgramBase.CurrentShaderProgram);
        program.Uniform("red", 60f / 255f);
        program.Uniform("greenBlue", new Vec2f(100f / 255f, 150f / 255f));
        program.Uniform("alphaOn", 1);
        program.Uniform("offsets", new Vec3i(0, 20, 0));
        program.UniformMatrix("transform", transform);
        seam.DrawFullscreenTriangle();
        program.Stop();
        Assert.Null(ShaderProgramBase.CurrentShaderProgram);
        seam.Present();

        byte[] pixel = ReadCentre(seam, framebuffer, size, openFrame: false);
        _output.WriteLine($"centre RGBA = {pixel[0]}, {pixel[1]}, {pixel[2]}, {pixel[3]}");
        Assert.Equal(new byte[] { 60, 120, 180, 255 }, pixel);

        program.Dispose();
        Assert.True(program.Disposed);
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// CreateUBO, then the object-range UBO update in three consecutive frames, each bound
    /// by ShaderProgramBase.Use and unbound by Stop, with no readback between the frames;
    /// then Dispose through the platform.
    /// </summary>
    [SkippableFact]
    public void EveryUboUpdatePathReachesTheBlockThroughThePlatform()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanDevice seam = session!.Seam;
        const int size = 16;

        var program = new RoutedProgram { PassName = "routed-ubo" };
        program.ProgramId = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            layout(std140) uniform Tint { vec4 tint; };
            out vec4 outColor;
            void main(void) { outColor = tint; }
            """);

        var ubo = Assert.IsType<UBO>(session.Platform.CreateUBO(program.ProgramId, 0, "Tint", sizeof(float) * 4));
        Assert.True(ubo.Handle > 0);
        Assert.Equal("Tint", ubo.BlockName);
        program.ubos["Tint"] = ubo;

        var colours = new[]
        {
            new byte[] { 25, 75, 125 },
            new byte[] { 210, 15, 45 },
            new byte[] { 90, 160, 230 },
        };
        var framebuffers = new int[colours.Length];
        for (int frame = 0; frame < colours.Length; frame++)
        {
            framebuffers[frame] = ColourTarget(seam, size);
            byte[] c = colours[frame];
            // The object-range overload is the one every caller uses (the entity
            // renderers' bone upload). The generic Update<T> overloads are not
            // driven here: vanilla pins through GCHandleProvider.Pointer, which is
            // GCHandle.ToIntPtr (the handle value, not the data address), so they
            // upload garbage on GL and on the device alike, before and after this move.
            ubo.Update(new[] { c[0] / 255f, c[1] / 255f, c[2] / 255f, 1f }, 0, sizeof(float) * 4);

            BeginDraw(seam, framebuffers[frame], size);
            program.Use();
            seam.DrawFullscreenTriangle();
            program.Stop();
            seam.Present();
        }

        for (int frame = 0; frame < colours.Length; frame++)
        {
            byte[] pixel = ReadCentre(seam, framebuffers[frame], size, openFrame: true);
            Assert.Equal(colours[frame][0], pixel[0]);
            Assert.Equal(colours[frame][1], pixel[1]);
            Assert.Equal(colours[frame][2], pixel[2]);
        }

        ubo.Dispose();
        program.Dispose();
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// BindTexture2D with a custom sampler: the program aims the sampler at the unit
    /// through the platform (the device never reads uniformLocations for it), Stop clears
    /// the sampler override, and Dispose deletes sampler and program.
    /// </summary>
    [SkippableFact]
    public unsafe void ATextureBoundOnAShaderProgramIsSampledThroughThePlatform()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        VulkanDevice seam = session!.Seam;
        const int size = 16;

        var program = new RoutedProgram { PassName = "routed-texture" };
        program.ProgramId = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D source;
            out vec4 outColor;
            void main(void) { outColor = texture(source, vec2(0.5, 0.5)); }
            """);
        program.SetCustomSampler("source", false);
        Assert.True(program.customSamplers["source"] > 0);

        var texel = new byte[] { 10, 200, 90, 255 };
        int texture;
        fixed (byte* data = texel)
        {
            texture = seam.CreateTexture2D(1, 1,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
        }
        int framebuffer = ColourTarget(seam, size);

        BeginDraw(seam, framebuffer, size);
        program.Use();
        program.BindTexture2D("source", texture, 0);
        seam.DrawFullscreenTriangle();
        program.Stop();
        seam.Present();

        byte[] pixel = ReadCentre(seam, framebuffer, size, openFrame: false);
        _output.WriteLine($"centre RGBA = {pixel[0]}, {pixel[1]}, {pixel[2]}, {pixel[3]}");
        Assert.Equal(new byte[] { 10, 200, 90, 255 }, pixel);

        program.Dispose();
        GpuTest.AssertClean(seam);
    }
}
}
