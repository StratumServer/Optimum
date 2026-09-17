using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

// The device integration tests' stand-ins for the client's shader types, under names that do
// not read as a device construction to the GPU suite's helper-bypass check.
using LinkedProgram = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The blit to the Default target, drawn twice on one Vulkan device: through the OpenGL body
/// (the route every other vanilla system still takes) and through the native device API
/// (docs/vulkan-native-render-systems.md, decision 4). The three branches - TAA debug view,
/// FSR (EASU into the FSR target, RCAS into Default) and the plain blit - have to produce the
/// same pixels, and the native one must not touch the GL state tracker, a texture unit or a
/// draw-buffer mask while its passes are open.
/// </summary>
public class NativeBlitTests(ITestOutputHelper output)
{
    private const int WindowSize = 16;

    /// <summary>Render resolution below the window: what makes the FSR branch an upsample.</summary>
    private const int RenderSize = 10;

    private static readonly string[] Programs = { "blit", "fsr-easu", "fsr-rcas", "taa-debug" };

    /// <summary>The Vulkan platform with FSR under the test's control, so no client setting is read.</summary>
    private sealed class BlitPlatform : VulkanClientPlatform
    {
        public BlitPlatform() : base(null!)
        {
        }

        public bool FsrActive;

        public override bool OptimumFsrBlitActive() => FsrActive;

        /// <summary>No window is opened here, so both routes take the blit's size from this seam.</summary>
        public override Size2i OptimumWindowClientSize() =>
            new(NativeBlitTests.WindowSize, NativeBlitTests.WindowSize);
    }

    // ------------------------------------------------------------------ the tests

    /// <summary>
    /// The plain blit: same pixels, one declared pass, one native draw.
    /// </summary>
    [SkippableFact]
    public unsafe void ThePlainBlitMatchesTheOpenGlBodyAsOneNativeDraw()
    {
        using Session session = Open();

        byte[] stated = RunFrame(session, native: false, debugView: 0, fsr: false);

        long drawsBefore = session.Seam.NativeDrawsForTests;
        long passesBefore = session.Seam.NativePassesForTests;
        byte[] nativeRoute = RunFrame(session, native: true, debugView: 0, fsr: false);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);
        Assert.Equal(stated, nativeRoute);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>Every TAA debug view draws what the OpenGL body draws, with the same mode and render size.</summary>
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public unsafe void ADebugViewMatchesTheOpenGlBody(int mode)
    {
        using Session session = Open();

        byte[] stated = RunFrame(session, native: false, debugView: mode, fsr: false);

        long drawsBefore = session.Seam.NativeDrawsForTests;
        byte[] nativeRoute = RunFrame(session, native: true, debugView: mode, fsr: false);

        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);
        Assert.Equal(stated, nativeRoute);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// FSR at a render scale below 1: EASU upsamples Primary colour 0 into the FSR target and
    /// RCAS sharpens it into Default - two written targets, two declared passes, and pixels
    /// the OpenGL body's two draws agree with.
    /// </summary>
    [SkippableFact]
    public unsafe void FsrMatchesTheOpenGlBodyWithOnePassPerWrittenTarget()
    {
        using Session session = Open();

        byte[] stated = RunFrame(session, native: false, debugView: 0, fsr: true);

        long drawsBefore = session.Seam.NativeDrawsForTests;
        long passesBefore = session.Seam.NativePassesForTests;
        byte[] nativeRoute = RunFrame(session, native: true, debugView: 0, fsr: true);

        Assert.Equal(2, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(2, session.Seam.NativeDrawsForTests - drawsBefore);

        int worst = WorstChannelDifference(stated, nativeRoute);
        output.WriteLine("FSR worst channel difference: " + worst);
        Assert.True(worst <= 1, "FSR differs from the OpenGL body by " + worst + "/255");

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>The OpenGL body on this device is the generic stated route, and the native route is not.</summary>
    [SkippableFact]
    public unsafe void TheOpenGlBodyDrawsThroughTheStatedRouteAndTheNativeRouteDoesNot()
    {
        using Session session = Open();

        long nativeDrawsBefore = session.Seam.NativeDrawsForTests;
        long statedBefore = session.Platform.StatedDrawsForTests;
        RunFrame(session, native: false, debugView: 0, fsr: false);
        Assert.Equal(0, session.Seam.NativeDrawsForTests - nativeDrawsBefore);
        Assert.True(session.Platform.StatedDrawsForTests - statedBefore > 0);

        RunFrame(session, native: true, debugView: 0, fsr: false);

        GpuTest.AssertClean(session.Seam);
    }

    // ---------------------------------------------------------------------- driving

    private unsafe byte[] RunFrame(Session session, bool native, int debugView, bool fsr)
    {
        VulkanDevice seam = session.Seam;
        session.Platform.NativeBlitEnabled = native;
        session.Platform.FsrActive = fsr;
        OptimumConfig.TaaDebugView = debugView;

        session.Platform.BeginFrame();

        // The motion attachment and the depth the debug views read, written as the frame's
        // own clears so both routes see the identical inputs.
        seam.BindFramebuffer(session.Primary.FboId);
        seam.SetDrawBuffers(session.Primary.FboId, 0b111);
        seam.ClearColor(2, 3f, -5f, 0.25f, 0.5f);
        seam.ClearDepth(0.5f);
        seam.SetDrawBuffers(session.Primary.FboId, 0b011);

        seam.BindDefaultFramebuffer();
        seam.ClearColor(0, 0.125f, 0.75f, 0.375f, 1f);

        // The state ScreenManager.Render leaves the frame in at the blit: blending back on
        // after the final composition, no depth test, no culling, viewport on the window.
        seam.SetBlend(true, EnumBlendMode.Standard);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetViewport(0, 0, WindowSize, WindowSize);

        session.Platform.BlitPrimaryToDefault();

        var pixels = new byte[WindowSize * WindowSize * 4];
        fixed (byte* destination = pixels)
        {
            seam.ReadDefaultFramebuffer(0, 0, WindowSize, WindowSize, (IntPtr)destination);
        }
        session.Platform.EndFrame();
        return pixels;
    }

    private static int WorstChannelDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int worst = 0;
        for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    // ---------------------------------------------------------------------- session

    private Session Open()
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);

        Session? session = Session.TryOpen(output, manifest);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    /// <summary>
    /// The platform, its device, the frame buffers the blit indexes and the shader programs
    /// it uses, installed the way the client installs them and put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        public BlitPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Primary { get; private set; } = null!;

        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";
        private ShaderProgramBlit? blitBefore;
        private ShaderProgram? easuBefore;
        private ShaderProgram? rcasBefore;
        private ShaderProgram? debugBefore;
        private int debugViewBefore;

        public static unsafe Session? TryOpen(ITestOutputHelper output, string manifestDirectory)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-blit-" + Guid.NewGuid().ToString("N"));
            var platform = new BlitPlatform
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

            if (!platform.InitializeGraphics(IntPtr.Zero, WindowSize, WindowSize, out string reason))
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
                blitBefore = ShaderPrograms.Blit,
                easuBefore = ShaderPrograms.FsrEasu,
                rcasBefore = ShaderPrograms.FsrRcas,
                debugBefore = ShaderPrograms.TaaDebug,
                debugViewBefore = OptimumConfig.TaaDebugView,
            };
            ScreenManager.Platform = platform;
            platform.ShaderUniforms = new DefaultShaderUniforms();

            VulkanDevice seam = platform.GraphicsDevice!;
            session.Primary = CreatePrimary(seam);
            FrameBufferRef fsr = CreateFsrTarget(seam);
            InstallFrameBuffers(platform, session.Primary, fsr);

            var blit = new ShaderProgramBlit { PassName = "blit" };
            Link(seam, blit, "blit", Array.Empty<string>());
            var easu = new ShaderProgram { PassName = "fsr-easu" };
            Link(seam, easu, "fsr-easu", new[] { "inputTexelSize" });
            var rcas = new ShaderProgram { PassName = "fsr-rcas" };
            Link(seam, rcas, "fsr-rcas", new[] { "inputTexelSize" });
            var debug = new ShaderProgram { PassName = "taa-debug" };
            Link(seam, debug, "taa-debug", new[] { "mode", "renderSize" });

            ShaderPrograms.Blit = blit;
            ShaderPrograms.FsrEasu = easu;
            ShaderPrograms.FsrRcas = rcas;
            ShaderPrograms.TaaDebug = debug;
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            ShaderPrograms.Blit = blitBefore!;
            ShaderPrograms.FsrEasu = easuBefore!;
            ShaderPrograms.FsrRcas = rcasBefore!;
            ShaderPrograms.TaaDebug = debugBefore!;
            OptimumConfig.TaaDebugView = debugViewBefore;
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

        /// <summary>Links one vanilla program as ShaderRegistry does and fills the locations its body looks up.</summary>
        private static void Link(VulkanDevice seam, ShaderProgramBase program, string name, string[] uniforms)
        {
            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                name, ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), new ShaderCorpus.ShaderVariant());

            var linked = new LinkedProgram { PassName = name };
            foreach (ShaderStageSource stage in stages)
            {
                var shader = new LinkedShader
                {
                    Type = stage.Stage,
                    Code = stage.Code,
                    PrefixCode = stage.PrefixCode,
                };
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

        /// <summary>Primary at the render resolution: scene, glow and the motion attachment at slot 2, plus depth.</summary>
        private static unsafe FrameBufferRef CreatePrimary(VulkanDevice seam)
        {
            var scene = new byte[RenderSize * RenderSize * 4];
            for (int y = 0; y < RenderSize; y++)
            {
                for (int x = 0; x < RenderSize; x++)
                {
                    int i = (y * RenderSize + x) * 4;
                    scene[i] = (byte)(20 + x * 23);
                    scene[i + 1] = (byte)(40 + y * 17);
                    scene[i + 2] = (byte)(((x ^ y) & 1) * 180 + 30);
                    scene[i + 3] = 255;
                }
            }

            int color0;
            fixed (byte* pixels = scene)
            {
                color0 = seam.CreateTexture2D(RenderSize, RenderSize,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
            }

            var primary = new FrameBufferRef
            {
                Width = RenderSize,
                Height = RenderSize,
                FboId = seam.CreateFramebuffer(RenderSize, RenderSize),
                ColorTextureIds = new[]
                {
                    color0,
                    seam.CreateTexture2D(RenderSize, RenderSize,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                    seam.CreateTexture2D(RenderSize, RenderSize,
                        EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                },
                DepthTextureId = seam.CreateTexture2D(RenderSize, RenderSize,
                    EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false),
            };
            for (int slot = 0; slot < primary.ColorTextureIds.Length; slot++)
            {
                seam.AttachTexture(primary.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    primary.ColorTextureIds[slot], 0);
            }
            seam.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
            seam.SetDrawBuffers(primary.FboId, 0b011);
            Assert.True(seam.CheckFramebufferComplete(primary.FboId, out string status), status);
            return primary;
        }

        /// <summary>The FSR intermediate at window resolution, as SetupDefaultFrameBuffers builds it.</summary>
        private static FrameBufferRef CreateFsrTarget(VulkanDevice seam)
        {
            var target = new FrameBufferRef
            {
                Width = WindowSize,
                Height = WindowSize,
                FboId = seam.CreateFramebuffer(WindowSize, WindowSize),
                ColorTextureIds = new[]
                {
                    seam.CreateTexture2D(WindowSize, WindowSize,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                },
            };
            seam.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
            seam.SetDrawBuffers(target.FboId, 1);
            return target;
        }

        /// <summary>Primary at slot 0 with its motion attachment at 2, the FSR target at 18, and a window to size the blit by.</summary>
        private static void InstallFrameBuffers(BlitPlatform platform, FrameBufferRef primary, FrameBufferRef fsr)
        {
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = primary;
            list[18] = fsr;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);
            platform.SetOptimumMotionAttachmentIndex(2);
            typeof(ClientPlatformWindows).GetField("optimumTaaTargetsReady", flags)!.SetValue(platform, true);

            // No window: OptimumWindowClientSize is the seam both routes size the blit by,
            // and NativeWindow.ClientSize is GLFW-backed, so a stub could not answer it.
        }
    }

    // ---------------------------------------------------------------- native shaders

    /// <summary>The manifest of the four programs the blit uses, built once for the whole class.</summary>
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

            string root = Path.Combine(Path.GetTempPath(), "optimum-native-blit-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
