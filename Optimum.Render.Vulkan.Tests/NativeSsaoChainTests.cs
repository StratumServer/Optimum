using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

using LinkedProgram = Optimum.Render.Vulkan.Tests.GpuTest.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.GpuTest.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The post chain's ambient-occlusion step on the Vulkan platform: the vanilla SSAO pass, its
/// bilateral blur ping-pong and the AO composite that multiplies the visibility into Primary
/// colour 0 before the TAA resolve reads it.
///
/// Acceptance is behavioural identity (docs/vulkan.md, decision 6): every
/// test here runs the same inputs through the OpenGL body - the lib virtual the chain switch falls
/// back to - and through the native route, and compares the pixels of all three written targets.
/// The settings that change this step are covered: SSAO quality 1 and 2 (one blur iteration or
/// three, and SSAOLEVEL 1 or 2 in the shaders), AO off, TAA off (no composite, no temporal
/// dither), a render scale below 1 and the GTAO mode with its own composite branch. Bloom, god
/// rays, FXAA and the AO debug view do not reach this step at all - they read what it leaves, and
/// the passes that consume it are covered where they live.
/// </summary>
public class NativeSsaoChainTests(ITestOutputHelper output)
{
    private const int Size = 16;
    private const int HalfSize = Size / 2;

    private static readonly string[] Programs = { "ssao", "bilateralblur", "scene-ssao" };

    // ------------------------------------------------------------------------ tests

    /// <summary>
    /// Vanilla SSAO at both qualities with TAA running: the raw SSAO target, the blurred target
    /// the final composition reads, and Primary colour 0 after the multiply all have to come out
    /// of the native route exactly as the OpenGL body leaves them. Quality 1 runs the blur once,
    /// quality 2 three times, and the shaders differ by SSAOLEVEL.
    /// </summary>
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheVanillaSsaoStepMatchesTheOpenGlBody(int quality)
    {
        using Session session = Open(quality == 1 ? "ssao-only" : "taa-with-ssao", taa: quality != 1);
        session.SsaoQuality = quality;

        Frame stated = session.Run(native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        Frame nativeRoute = session.Run(native: true);

        // The raw pass, one blur half-iteration per pass, and the composite when TAA runs.
        int blurPasses = quality == 1 ? 2 : 6;
        int expected = 1 + blurPasses + (quality != 1 ? 1 : 0);
        Assert.Equal(expected, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(expected, session.Seam.NativeDrawsForTests - drawsBefore);

        Assert.Equal(stated.Raw, nativeRoute.Raw);
        Assert.Equal(stated.Blurred, nativeRoute.Blurred);
        Assert.Equal(stated.Scene, nativeRoute.Scene);
        Assert.Equal(stated.SsaoInScene, nativeRoute.SsaoInScene);

        // The step really did something, or the comparison above would pass on two routes that
        // both wrote nothing.
        Assert.NotEqual(session.RawSeed, nativeRoute.Raw);
        Assert.NotEqual(session.BlurredSeed, nativeRoute.Blurred);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The same step with TAA off: SSAO and its blur still run, the composite does not, and
    /// Primary colour 0 comes back exactly as the frame seeded it on both routes - the AO is left
    /// for the final composition to apply, which is what the OpenGL path has always done.
    /// </summary>
    [SkippableFact]
    public void TheVanillaSsaoStepSkipsTheCompositeWithTaaOff()
    {
        using Session session = Open("ssao-only", taa: false);

        Frame stated = session.Run(native: false);
        Frame nativeRoute = session.Run(native: true);

        Assert.Equal(stated.Raw, nativeRoute.Raw);
        Assert.Equal(stated.Blurred, nativeRoute.Blurred);
        Assert.Equal(stated.Scene, nativeRoute.Scene);
        Assert.Equal(session.SceneSeed, nativeRoute.Scene);
        Assert.False(nativeRoute.SsaoInScene);
        Assert.False(stated.SsaoInScene);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// A render scale below 1. The SSAO pass's screenSize is the body's
    /// <c>ssaaLevel * client * (ssaaLevel == 1 ? 0.5 : 1)</c>, which the dither's Bayer lattice is
    /// laid out on, so it changes every occlusion value - except at exactly 0.5, where the
    /// half-resolution fudge cancels and the value is the one render scale 1 produces. Both scales
    /// are here: 0.5 because it is the shipped setting, and 0.75 because it is a scale where the
    /// value really differs, which is what proves it reaches the pass at all.
    /// </summary>
    [SkippableFact]
    public void TheVanillaSsaoStepMatchesTheOpenGlBodyBelowRenderScaleOne()
    {
        using Session session = Open("taa-with-ssao", taa: true);

        session.SsaaLevel = 0.5f;
        Frame statedHalf = session.Run(native: false);
        Frame nativeHalf = session.Run(native: true);
        Assert.Equal(statedHalf.Raw, nativeHalf.Raw);
        Assert.Equal(statedHalf.Blurred, nativeHalf.Blurred);
        Assert.Equal(statedHalf.Scene, nativeHalf.Scene);

        session.SsaaLevel = 0.75f;
        Frame stated = session.Run(native: false);
        Frame nativeRoute = session.Run(native: true);
        Assert.Equal(stated.Raw, nativeRoute.Raw);
        Assert.Equal(stated.Blurred, nativeRoute.Blurred);
        Assert.Equal(stated.Scene, nativeRoute.Scene);

        Assert.NotEqual(nativeHalf.Raw, nativeRoute.Raw);
        Assert.NotEqual(statedHalf.Raw, stated.Raw);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// AO off (SSAO quality 0, so RenderSSAO is false): neither route runs a pass, neither target
    /// moves, and the "AO is in the scene" flag stays false - which is what makes the final
    /// composition apply nothing rather than multiply by an unwritten target.
    /// </summary>
    [SkippableFact]
    public void TheAoStepDoesNothingWhenAmbientOcclusionIsOff()
    {
        using Session session = Open("ssao-only", taa: true);
        session.RenderSsao = false;

        Frame stated = session.Run(native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        Frame nativeRoute = session.Run(native: true);

        Assert.Equal(0, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(stated.Raw, nativeRoute.Raw);
        Assert.Equal(session.RawSeed, nativeRoute.Raw);
        Assert.Equal(session.BlurredSeed, nativeRoute.Blurred);
        Assert.Equal(session.SceneSeed, nativeRoute.Scene);
        Assert.False(nativeRoute.SsaoInScene);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The GTAO mode: the platform's own visibility texture replaces vanilla SSAO, so the raw and
    /// blurred targets are never written and the composite takes the OPTIMUMAO branch with
    /// optimumAoMode = 1, sampling the G-buffer position and the OIT revealage for the attenuation
    /// vanilla SSAO applies inside its own pass. The compute pass itself is not exercised here -
    /// this is the raster step around it - so the visibility texture is supplied directly.
    /// </summary>
    [SkippableFact]
    public void TheGtaoCompositeMatchesTheOpenGlBody()
    {
        using Session session = Open("taa-with-gtao", taa: true, gtao: true);
        session.AmbientOcclusionTexture = session.GtaoVisibility;

        Frame stated = session.Run(native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        Frame nativeRoute = session.Run(native: true);

        // The composite alone: vanilla SSAO and its blur stood down.
        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);

        Assert.Equal(stated.Scene, nativeRoute.Scene);
        Assert.True(nativeRoute.SsaoInScene);
        Assert.Equal(session.RawSeed, nativeRoute.Raw);
        Assert.Equal(session.BlurredSeed, nativeRoute.Blurred);
        Assert.NotEqual(session.SceneSeed, nativeRoute.Scene);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The step leaves the GL-shaped state the steps after it inherit exactly where the OpenGL
    /// body leaves it: blending on, the depth test on, and the viewport back at full render
    /// resolution - the Luma step sets no viewport of its own and would otherwise draw into the
    /// SSAO target's half-resolution one.
    /// </summary>
    [SkippableFact]
    public void TheNativeAoStepLeavesTheGlShapedStateWhereTheBodyLeavesIt()
    {
        using Session session = Open("taa-with-ssao", taa: true);

        session.Run(native: false);
        (int Width, int Height) stated = session.Viewport;
        session.Run(native: true);

        Assert.Equal(stated, session.Viewport);
        Assert.Equal((Size, Size), session.Viewport);

        GpuTest.AssertClean(session.Seam);
    }

    // ---------------------------------------------------------------------- session

    /// <summary>What one run of the step produced, on either route.</summary>
    private readonly record struct Frame(byte[] Raw, byte[] Blurred, byte[] Scene, bool SsaoInScene);

    private Session Open(string variantName, bool taa, bool gtao = false)
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        Session? session = Session.TryOpen(output, manifest, variantName, taa, gtao);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    /// <summary>The Vulkan platform without a window, with the targets this step indexes.</summary>
    private sealed class AoPlatform : VulkanClientPlatform
    {
        public AoPlatform() : base(null!)
        {
        }

        /// <summary>The visibility texture RenderOptimumAmbientOcclusion is made to return, or 0.</summary>
        public int AmbientOcclusionTexture { get; set; }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);

        public override int RenderOptimumAmbientOcclusion(float[] projectMatrix) => AmbientOcclusionTexture;

        /// <summary>Primary is the render resolution here, so the bind and the viewport are the base's.</summary>
        public override void LoadFrameBuffer(EnumFrameBuffer framebuffer)
        {
            if (framebuffer == EnumFrameBuffer.Primary)
            {
                CurrentFrameBuffer = FrameBuffers[0];
                return;
            }
            base.LoadFrameBuffer(framebuffer);
        }
    }

    private sealed class Session : IDisposable
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        public AoPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;

        /// <summary>The three targets' contents as the frame seeds them, decoded the way a run decodes them.</summary>
        public byte[] RawSeed { get; private set; } = Array.Empty<byte>();
        public byte[] BlurredSeed { get; private set; } = Array.Empty<byte>();
        public byte[] SceneSeed { get; private set; } = Array.Empty<byte>();

        /// <summary>A prepared visibility texture, for the GTAO branch.</summary>
        public int GtaoVisibility { get; private set; }

        public (int Width, int Height) Viewport { get; private set; }

        public int AmbientOcclusionTexture
        {
            set => Platform.AmbientOcclusionTexture = value;
        }

        public int SsaoQuality
        {
            set => ClientSettings.SSAOQuality = value;
        }

        public float SsaaLevel
        {
            set => typeof(ClientPlatformWindows).GetField("ssaaLevel", Hidden)!.SetValue(Platform, value);
        }

        public bool RenderSsao
        {
            set => typeof(ClientPlatformWindows).GetField("RenderSSAO", Hidden)!.SetValue(Platform, value);
        }

        private readonly List<FrameBufferRef> buffers = new();
        private FrameBufferRef primary = null!;
        private FrameBufferRef transparent = null!;
        private FrameBufferRef ssao = null!;
        private FrameBufferRef blurHorizontal = null!;
        private FrameBufferRef blurVertical = null!;
        private float[] projection = null!;

        private int decodeProgram;
        private int decodeTarget;
        private int decodeFramebuffer;

        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";
        private ShaderProgramSsao? ssaoBefore;
        private ShaderProgramBilateralblur? blurBefore;
        private ShaderProgram? sceneSsaoBefore;
        private bool taaBefore;
        private bool gtaoBefore;
        private int qualityBefore;
        private DefaultShaderUniforms uniforms = new();

        public static Session? TryOpen(ITestOutputHelper output, string manifestDirectory,
            string variantName, bool taa, bool gtao)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-ssao-" + Guid.NewGuid().ToString("N"));
            var platform = new AoPlatform
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

            var session = new Session
            {
                Platform = platform,
                previousPlatform = ScreenManager.Platform,
                dataPath = dataPath,
                ssaoBefore = ShaderPrograms.Ssao,
                blurBefore = ShaderPrograms.Bilateralblur,
                sceneSsaoBefore = ShaderPrograms.SceneSsao,
                taaBefore = OptimumConfig.Taa,
                gtaoBefore = OptimumConfig.AmbientOcclusionShadersUseGtao,
                qualityBefore = ClientSettings.SSAOQuality,
            };
            ScreenManager.Platform = platform;
            ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static (string _) => { });
            platform.ShaderUniforms = session.uniforms;

            OptimumConfig.Taa = taa;
            OptimumConfig.AmbientOcclusionShadersUseGtao = gtao;
            ClientSettings.SSAOQuality = 2;

            session.BuildTargets();
            session.LinkPrograms(variantName);
            session.InstallState();
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            ShaderPrograms.Ssao = ssaoBefore!;
            ShaderPrograms.Bilateralblur = blurBefore!;
            ShaderPrograms.SceneSsao = sceneSsaoBefore!;
            OptimumConfig.Taa = taaBefore;
            OptimumConfig.AmbientOcclusionShadersUseGtao = gtaoBefore;
            ClientSettings.SSAOQuality = qualityBefore;
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

        // ---------------------------------------------------------------- one run

        /// <summary>
        /// One frame: the three targets seeded, the step run on the chosen route, everything read
        /// back. The routes share the seeding and the readback, so a difference is the step's.
        /// </summary>
        public Frame Run(bool native)
        {
            Platform.NativePostChainEnabled = native;
            Platform.BeginFrame();
            SeedFrame();
            Platform.RunPostStepAmbientOcclusionForTests(projection);
            Viewport = ((int)Platform.stated.Viewport.Extent.Width, (int)Platform.stated.Viewport.Extent.Height);
            var frame = new Frame(
                Decode(ssao.ColorTextureIds[0]),
                Decode(blurVertical.ColorTextureIds[0]),
                Decode(primary.ColorTextureIds[0]),
                Platform.OptimumPostSsaoInScene);
            Platform.EndFrame();
            return frame;
        }

        /// <summary>The state the world stages leave for the post chain, and the inputs both routes read.</summary>
        private void SeedFrame()
        {
            VulkanDevice seam = Seam;

            seam.BindFramebuffer(transparent.FboId);
            seam.SetDrawBuffers(transparent.FboId, 0b111);
            seam.ClearColor(1, 0.8f, 0.8f, 0.8f, 1f);

            seam.BindFramebuffer(ssao.FboId);
            seam.SetDrawBuffers(ssao.FboId, 0b1);
            seam.ClearColor(0, 0.2f, 0.2f, 0.2f, 1f);
            seam.BindFramebuffer(blurHorizontal.FboId);
            seam.SetDrawBuffers(blurHorizontal.FboId, 0b1);
            seam.ClearColor(0, 0.3f, 0.3f, 0.3f, 1f);
            seam.BindFramebuffer(blurVertical.FboId);
            seam.SetDrawBuffers(blurVertical.FboId, 0b1);
            seam.ClearColor(0, 0.4f, 0.4f, 0.4f, 1f);

            seam.BindFramebuffer(primary.FboId);
            seam.SetDrawBuffers(primary.FboId, 0b1111);
            seam.ClearColor(0, 0.5f, 0.6f, 0.7f, 1f);
            seam.ClearColor(1, 0.1f, 0.2f, 0.3f, 1f);
            seam.ClearDepth(1f);
            Platform.CurrentFrameBuffer = primary;

            seam.SetViewport(0, 0, Size, Size);
            seam.SetBlend(true, EnumBlendMode.Standard);
            seam.SetDepthTest(true);
            seam.SetDepthMask(true);
            seam.SetCullFace(false);
        }

        // ---------------------------------------------------------------- readback

        /// <summary>
        /// Any attachment through an RGBA8 copy, because the seam's readback is four bytes per
        /// pixel from attachment 0. Both routes go through the same copy, so equal bytes here mean
        /// equal texels: the decode cannot hide a difference it applies to both sides identically.
        /// </summary>
        private unsafe byte[] Decode(int textureId)
        {
            VulkanDevice seam = Seam;
            seam.BindFramebuffer(decodeFramebuffer);
            seam.ClearColor(0, 0f, 0f, 0f, 1f);
            seam.UseProgram(decodeProgram);
            seam.SetSamplerUnit(decodeProgram, "source", 15);
            seam.BindTexture(15, textureId);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();

            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(decodeFramebuffer);
                seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }
            return pixels;
        }

        // ----------------------------------------------------------------- fixture

        private void BuildTargets()
        {
            VulkanDevice seam = Seam;

            // Primary with the SSAO G-buffer: colour, glow, normal, position. The AO step never
            // touches the motion attachment, so this target stops at the G-buffer.
            primary = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Texture(Size, EnumTextureInternalFormat.Rgba8),
                    Texture(Size, EnumTextureInternalFormat.Rgba8),
                    GBuffer(normals: true),
                    GBuffer(normals: false),
                },
                DepthTextureId = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
                    EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false),
            };
            for (int slot = 0; slot < 4; slot++)
            {
                seam.AttachTexture(primary.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    primary.ColorTextureIds[slot], 0);
            }
            seam.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
            Assert.True(seam.CheckFramebufferComplete(primary.FboId, out string primaryStatus), primaryStatus);

            transparent = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Texture(Size, EnumTextureInternalFormat.Rgba8),
                    Texture(Size, EnumTextureInternalFormat.Rgba8),
                    Texture(Size, EnumTextureInternalFormat.Rgba8),
                },
            };
            for (int slot = 0; slot < 3; slot++)
            {
                seam.AttachTexture(transparent.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    transparent.ColorTextureIds[slot], 0);
            }
            Assert.True(seam.CheckFramebufferComplete(transparent.FboId, out string status), status);

            // The SSAO target as the platform builds it: half resolution, an RGB float attachment
            // and the 16x16 rotation noise, which is a texture of the target rather than an
            // attachment of it.
            ssao = new FrameBufferRef
            {
                Width = HalfSize,
                Height = HalfSize,
                FboId = seam.CreateFramebuffer(HalfSize, HalfSize),
                ColorTextureIds = new int[2],
            };
            ssao.ColorTextureIds[0] = seam.CreateTexture2DRaw(HalfSize, HalfSize, 6407, IntPtr.Zero, 12);
            seam.AttachTexture(ssao.FboId, EnumFramebufferAttachment.ColorAttachment0, ssao.ColorTextureIds[0], 0);
            seam.SetDrawBuffers(ssao.FboId, 0b1);
            ssao.ColorTextureIds[1] = Noise(seam);

            blurVertical = Blur(seam);
            blurHorizontal = Blur(seam);

            GtaoVisibility = Visibility(seam);

            for (int i = 0; i <= 24; i++) buffers.Add(null!);
            buffers[0] = primary;
            buffers[1] = transparent;
            buffers[13] = ssao;
            buffers[14] = blurVertical;
            buffers[15] = blurHorizontal;

            decodeTarget = Texture(Size, EnumTextureInternalFormat.Rgba8);
            decodeFramebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(decodeFramebuffer, EnumFramebufferAttachment.ColorAttachment0, decodeTarget, 0);
            seam.SetDrawBuffers(decodeFramebuffer, 0b1);

            projection = Mat4f.Perspective(Mat4f.Create(), 70f * (float)Math.PI / 180f, 1f, 0.1f, 100f);
        }

        private int Texture(int size, EnumTextureInternalFormat format) =>
            Seam.CreateTexture2D(size, size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

        private FrameBufferRef Blur(VulkanDevice seam)
        {
            var target = new FrameBufferRef
            {
                Width = HalfSize,
                Height = HalfSize,
                FboId = seam.CreateFramebuffer(HalfSize, HalfSize),
                ColorTextureIds = new[] { Texture(HalfSize, EnumTextureInternalFormat.Rgba8) },
            };
            seam.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
            seam.SetDrawBuffers(target.FboId, 0b1);
            return target;
        }

        /// <summary>The SSAO rotation noise, built by the platform's own generator so the pattern is the shipped one.</summary>
        private static unsafe int Noise(VulkanDevice seam)
        {
            float[] noise = VulkanClientPlatform.BuildOptimumSsaoNoise(new Random(5), 16);
            int id;
            fixed (float* data = noise) id = seam.CreateTexture2DRaw(16, 16, 34836, (IntPtr)data, 16);
            seam.SetTextureParameter(id, OptimumGlConstants.TextureWrapS, OptimumGlConstants.Repeat);
            seam.SetTextureParameter(id, OptimumGlConstants.TextureWrapT, OptimumGlConstants.Repeat);
            return id;
        }

        /// <summary>
        /// A G-buffer attachment with a pattern the SSAO kernel actually responds to: view-space
        /// positions on a slope for the position target, and unit normals for the normal one.
        /// </summary>
        private unsafe int GBuffer(bool normals)
        {
            var texels = new float[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = (y * Size + x) * 4;
                    if (normals)
                    {
                        var normal = new Vec3f(0.1f + x * 0.01f, 0.15f, 1f);
                        normal.Normalize();
                        texels[i] = normal.X;
                        texels[i + 1] = normal.Y;
                        texels[i + 2] = normal.Z;
                        // w is vanilla's leaves flag; 0 keeps the ordinary occlusion branch.
                        texels[i + 3] = 0f;
                    }
                    else
                    {
                        // Alternating depth columns: vanilla's SSAO clamps every kernel tap to
                        // within 0.04 of the fragment's own texcoord, so occlusion only comes from
                        // a near neighbour, and a fine step is what every pixel can see one of.
                        texels[i] = (x - Size * 0.5f) * 0.12f + 0.011f;
                        texels[i + 1] = (y - Size * 0.5f) * 0.12f;
                        texels[i + 2] = -3f + ((x * 7 + y * 13) % 11) * 0.02f;
                        texels[i + 3] = 0.1f;
                    }
                }
            }
            fixed (float* data = texels)
            {
                return Seam.CreateTexture2DRaw(Size, Size, 34836, (IntPtr)data, 16);
            }
        }

        /// <summary>A visibility texture standing in for GTAO's output: R32F, nearest, clamped, as the platform sets it up.</summary>
        private unsafe int Visibility(VulkanDevice seam)
        {
            var texels = new float[Size * Size];
            for (int i = 0; i < texels.Length; i++) texels[i] = 0.25f + (i % 7) / 12f;
            int id;
            fixed (float* data = texels) id = seam.CreateTexture2DRaw(Size, Size, 0x822E, (IntPtr)data, 4);
            // Composed with texelFetch at the same resolution; nearest and clamp keep any sampling exact.
            seam.SetTextureParameter(id, OptimumGlConstants.TextureMinFilter, 9728);
            seam.SetTextureParameter(id, OptimumGlConstants.TextureMagFilter, 9728);
            seam.SetTextureParameter(id, OptimumGlConstants.TextureWrapS, OptimumGlConstants.ClampToEdge);
            seam.SetTextureParameter(id, OptimumGlConstants.TextureWrapT, OptimumGlConstants.ClampToEdge);
            return id;
        }

        private void LinkPrograms(string variantName)
        {
            VulkanDevice seam = Seam;
            ShaderCorpus.ShaderVariant variant = Variant(variantName);

            var ssaoProgram = new ShaderProgramSsao { PassName = "ssao" };
            Link(seam, ssaoProgram, "ssao", variant,
                new[] { "screenSize", "projection", "samples" },
                new[] { "gPosition", "gNormal", "texNoise", "revealage" },
                variant.TaaMotion == 1 ? new[] { "temporalFrameIndex" } : Array.Empty<string>());
            var blurProgram = new ShaderProgramBilateralblur { PassName = "bilateralblur" };
            Link(seam, blurProgram, "bilateralblur", variant, new[] { "frameSize", "isVertical" },
                new[] { "inputTexture", "depthTexture" }, Array.Empty<string>());
            var composite = new ShaderProgram { PassName = "scene-ssao" };
            Link(seam, composite, "scene-ssao", variant, new[] { "invRenderHeight" },
                variant.OptimumAo == 1
                    ? new[] { "ssaoScene", "gPositionScene", "revealageScene" }
                    : new[] { "ssaoScene" },
                variant.OptimumAo == 1 ? new[] { "optimumAoMode" } : Array.Empty<string>());

            ShaderPrograms.Ssao = ssaoProgram;
            ShaderPrograms.Bilateralblur = blurProgram;
            ShaderPrograms.SceneSsao = composite;

            decodeProgram = LinkDecode(seam);
        }

        internal static ShaderCorpus.ShaderVariant Variant(string name)
        {
            foreach (ShaderCorpus.ShaderVariant candidate in ShaderCorpus.Variants())
            {
                if (candidate.Name == name) return candidate;
            }
            throw new InvalidOperationException("the corpus has no " + name + " variant");
        }

        private static void Link(VulkanDevice seam, ShaderProgramBase program, string name,
            ShaderCorpus.ShaderVariant variant, string[] uniforms, string[] samplers, string[] optional)
        {
            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                name, ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant);

            var linked = new LinkedProgram { PassName = name };
            foreach (ShaderStageSource stage in stages)
            {
                var shader = new LinkedShader
                {
                    Type = stage.Stage,
                    Code = stage.Code,
                    PrefixCode = stage.PrefixCode,
                };
                Assert.True(seam.CompileShader(shader), seam.GetError() ?? name + " did not compile");
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
            // The GL route binds samplers by name through the program's location table.
            foreach (string sampler in samplers)
            {
                int location = seam.GetUniformLocation(id, sampler);
                Assert.True(location != -1, name + " has no location for " + sampler);
                program.uniformLocations[sampler] = location;
            }
            // Uniforms the shader only declares in some variants: registered where they exist, so
            // both routes leave them alone in the variants that do not have them.
            foreach (string uniform in optional)
            {
                int location = seam.GetUniformLocation(id, uniform);
                if (location != -1) program.uniformLocations[uniform] = location;
            }
        }

        /// <summary>The readback helper's own program: any attachment into RGBA8.</summary>
        private static int LinkDecode(VulkanDevice seam)
        {
            const string vertex = @"#version 330 core
out vec2 uv;
void main(void)
{
	vec2 position = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	uv = position;
	gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
}
";
            const string fragment = @"#version 330 core
uniform sampler2D source;
in vec2 uv;
layout(location = 0) out vec4 outColor;
void main(void)
{
	outColor = clamp(texture(source, uv), 0.0, 1.0);
}
";
            var linked = new LinkedProgram { PassName = "native-ssao-decode" };
            var vertexShader = new LinkedShader { Type = EnumShaderType.VertexShader, Code = vertex, PrefixCode = "" };
            var fragmentShader = new LinkedShader { Type = EnumShaderType.FragmentShader, Code = fragment, PrefixCode = "" };
            Assert.True(seam.CompileShader(vertexShader), seam.GetError() ?? "decode vertex shader");
            Assert.True(seam.CompileShader(fragmentShader), seam.GetError() ?? "decode fragment shader");
            linked.VertexShader = vertexShader;
            linked.FragmentShader = fragmentShader;
            int id = seam.LinkProgram(linked);
            Assert.True(id > 0, seam.GetError() ?? "decode link failed");
            return id;
        }

        private void InstallState()
        {
            typeof(ClientPlatformWindows).GetField("frameBuffers", Hidden)!.SetValue(Platform, buffers);
            typeof(ClientPlatformWindows).GetField("ssaaLevel", Hidden)!.SetValue(Platform, 1f);
            typeof(ClientPlatformWindows).GetField("RenderSSAO", Hidden)!.SetValue(Platform, true);
            // The composite only runs while TAA is actually accumulating, which is what the
            // OptimumTaaRequested && TaaTargetsReady guard says.
            typeof(ClientPlatformWindows).GetField("optimumTaaTargetsReady", Hidden)!
                .SetValue(Platform, OptimumConfig.Taa);
            FillSsaoKernel();

            Platform.BeginFrame();
            SeedFrame();
            RawSeed = Decode(ssao.ColorTextureIds[0]);
            BlurredSeed = Decode(blurVertical.ColorTextureIds[0]);
            SceneSeed = Decode(primary.ColorTextureIds[0]);
            Platform.EndFrame();
        }

        /// <summary>The 64-sample kernel, built the way the platform's frame-buffer setup builds it.</summary>
        private void FillSsaoKernel()
        {
            float[] kernel = Platform.OptimumSsaoKernel;
            var random = new Random(11);
            for (int sample = 0; sample < 64; sample++)
            {
                var value = new Vec3f((float)random.NextDouble() * 2f - 1f,
                    (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble());
                value.Normalize();
                value *= (float)random.NextDouble();
                float scale = sample / 64f;
                scale = GameMath.Lerp(0.1f, 1f, scale * scale);
                value *= scale;
                kernel[sample * 3] = value.X;
                kernel[sample * 3 + 1] = value.Y;
                kernel[sample * 3 + 2] = value.Z;
            }
        }
    }

    // ---------------------------------------------------------------- native shaders

    private static readonly Lazy<(string Directory, string Reason)> NativeManifest = new(BuildNativeShaders);

    private static (string, string) BuildNativeShaders()
    {
        if (!NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason)) return ("", reason);
        using (compiler)
        {
            var builder = new NativeShaderBuilder(compiler!);
            var merged = new NativeShaderBuildResult();
            merged.Manifest.Toolchain = compiler!.Identity;
            string source = Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk");
            foreach (string program in Programs)
            {
                NativeShaderBuildResult one = builder.Build(source, program);
                merged.Errors.AddRange(one.Errors);
                merged.Manifest.Programs.AddRange(one.Manifest.Programs);
                foreach ((string file, byte[] bytes) in one.Files) merged.Files[file] = bytes;
            }
            if (!merged.Success) return ("", string.Join("\n", merged.Errors));

            string root = Path.Combine(Path.GetTempPath(), "optimum-native-ssao-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
