using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Platform;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// World/UI separation on Vulkan (VulkanClientPlatform.UiSeparation.cs): the frame is rendered
/// HUD-less, the GUI lands in its own image with real coverage, and the compose puts it back.
///
/// Driven through the real platform bodies - the scope's open, the stated GUI-shaped draws that
/// only name Default, the compose, the snapshot - on a headless device, whose Default target is
/// the window-sized image a headless run reads back. Every judged frame follows unjudged ones with
/// EndFrame between them and no readback in the loop, so an image that is really an earlier
/// frame's fails here. Validation runs with sync and best practices on, as in every GPU test.
/// </summary>
public class UiSeparationTests(ITestOutputHelper output)
{
    private const int Size = 16;
    private const int Half = Size / 2;

    // The scene under the UI, and two straight-alpha UI layers: one over the whole image at 0.4,
    // one over the left half at 0.6. Distinguishable in every channel.
    private static readonly double[] Scene = { 40, 90, 200, 255 };
    private static readonly double[] LayerA = { 200, 100, 50, 0.4 };
    private static readonly double[] LayerB = { 20, 180, 240, 0.6 };

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private static readonly string LayerAFragment = """
        #version 330 core
        out vec4 outColor;
        void main(void) { outColor = vec4(200.0 / 255.0, 100.0 / 255.0, 50.0 / 255.0, 0.4); }
        """;

    private static readonly string LayerBFragment = """
        #version 330 core
        out vec4 outColor;
        void main(void)
        {
            if (gl_FragCoord.x >= 8.0) discard;
            outColor = vec4(20.0 / 255.0, 180.0 / 255.0, 240.0 / 255.0, 0.6);
        }
        """;

    // The shipped sources/shaders/ui-compose.{vsh,fsh}, read from the tree so the test links
    // exactly what ships (its native twin is pinned by NativeShaderParityTests).
    private static string ComposeSource(string extension) =>
        File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders", "ui-compose." + extension));

    private sealed class SeparationPlatform : VulkanClientPlatform
    {
        public SeparationPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    /// <summary>
    /// The three images at their two moments. Before the compose the window image holds the scene
    /// and nothing else; the UI image holds exactly the GUI - the over-operator's premultiplied
    /// colour and coverage where it drew, transparent black where it did not, including where an
    /// earlier frame drew; after the compose the window image is ui.rgb + scene * (1 - ui.a), which
    /// is what the same layers drawn straight onto the scene give.
    /// </summary>
    [SkippableFact]
    public void TheGuiLandsInItsOwnImageAndIsComposedOverTheScene()
    {
        using Session session = Open();
        SeparationPlatform platform = session.Platform;
        VulkanDevice seam = session.Seam;
        FrameBufferRef ui = platform.UiTargetFrameBuffer!;
        Assert.Equal(VulkanClientPlatform.OptimumUiTargetIndex, platform.UiTargetFrameBufferIndex);
        Assert.Equal(Size, ui.Width);

        for (int frame = 0; frame < 3; frame++)
        {
            session.RunFrame(layerA: true, layerB: true, read: false);
        }

        // Both layers: coverage accumulates as the over-operator, not as alpha squared.
        Frame both = session.RunFrame(layerA: true, layerB: true, read: true);
        double[] uiRight = Premultiplied(LayerA);
        double[] uiLeft = Over(Premultiplied(LayerB), uiRight);
        AssertHalf(both.Ui, left: false, uiRight, "UI image, layer A alone");
        AssertHalf(both.Ui, left: true, uiLeft, "UI image, layer B over layer A");
        AssertHalf(both.Before, left: true, Scene, "window before the compose");
        AssertHalf(both.Before, left: false, Scene, "window before the compose");
        AssertHalf(both.After, left: false, Composed(uiRight), "window after the compose");
        AssertHalf(both.After, left: true, Composed(uiLeft), "window after the compose");

        // Only layer B: the right half of the UI image is empty again, and the window shows the
        // scene there - the image is cleared every frame, not accumulated across frames.
        Frame onlyB = session.RunFrame(layerA: false, layerB: true, read: true);
        AssertHalf(onlyB.Ui, left: false, new double[] { 0, 0, 0, 0 }, "UI image, nothing drawn");
        AssertHalf(onlyB.Ui, left: true, Premultiplied(LayerB), "UI image, layer B alone");
        AssertHalf(onlyB.After, left: false, Scene, "window where no UI drew");
        AssertHalf(onlyB.After, left: true, Composed(Premultiplied(LayerB)), "window after the compose");

        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// The scope is a window of the frame and nothing more. Open, Default resolves to the UI image
    /// and Standard takes over-operator alpha; the compose closes both, and a second compose in the
    /// frame records nothing. A scope nobody composed (a GUI renderer that threw) is closed by the
    /// next BeginFrame, so the world pass after it blends with vanilla's factors: one 0.4 layer over
    /// transparent black keeps 0.4 * 0.4 = 0.16 coverage, not 0.4.
    /// </summary>
    [SkippableFact]
    public void TheScopeClosesAtTheComposeAndAtTheNextFrame()
    {
        using Session session = Open();
        SeparationPlatform platform = session.Platform;
        VulkanDevice seam = session.Seam;
        FrameBufferRef ui = platform.UiTargetFrameBuffer!;

        platform.BeginFrame();
        platform.OpenUiScope();
        Assert.True(platform.UiScopeOpen);
        Assert.Equal(ui.FboId, seam.DefaultFramebufferRedirect);
        platform.OptimumComposeUiTarget();
        Assert.False(platform.UiScopeOpen);
        Assert.Equal(0, seam.DefaultFramebufferRedirect);
        long passes = seam.NativePassesForTests;
        platform.OptimumComposeUiTarget();
        Assert.Equal(passes, seam.NativePassesForTests);
        platform.EndFrame();

        // Left open, as by a GUI renderer that threw past both compose call sites.
        platform.BeginFrame();
        platform.OpenUiScope();
        platform.EndFrame();

        platform.BeginFrame();
        Assert.False(platform.UiScopeOpen);
        Assert.Equal(0, seam.DefaultFramebufferRedirect);

        // A world-shaped Standard draw into a target cleared to transparent black.
        FrameBufferRef world = platform.SceneNoHudFrameBuffer!;
        seam.ClearNativeColor(world.FboId, 0, 0f, 0f, 0f, 0f);
        platform.CurrentFrameBuffer = world;
        session.DrawLayer(session.LayerAProgram);
        byte[] pixels = seam.ReadBackLevel0ForTests(world.ColorTextureIds[0]);
        platform.CurrentFrameBuffer = null;
        platform.EndFrame();

        int alpha = pixels[(Half * Size + Half) * 4 + 3];
        output.WriteLine("world-pass coverage after an uncomposed scope: " + alpha + " (vanilla 41, scoped 102)");
        Assert.InRange(alpha, 39, 43);
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// The snapshot is the composited scene texel for texel: Primary colour 0 copied into slot 23
    /// at the end of the composition, and flagged as captured for that frame only.
    /// </summary>
    [SkippableFact]
    public void TheSnapshotIsTheCompositedScene()
    {
        using Session session = Open();
        SeparationPlatform platform = session.Platform;
        VulkanDevice seam = session.Seam;
        FrameBufferRef primary = platform.FrameBuffers[0];
        FrameBufferRef snapshot = platform.SceneNoHudFrameBuffer!;
        Assert.Equal(VulkanClientPlatform.OptimumSceneNoHudIndex, platform.SceneNoHudFrameBufferIndex);

        byte[] captured = Array.Empty<byte>();
        for (int frame = 0; frame < 4; frame++)
        {
            platform.BeginFrame();
            // A different scene every frame, so a copy of an earlier one cannot pass.
            float phase = frame / 4f;
            seam.ClearNativeColor(primary.FboId, 0, 0.1f + phase * 0.5f, 0.7f - phase * 0.4f, 0.3f, 1f);
            platform.CaptureSceneNoHud();
            Assert.True(platform.SceneNoHudCaptured);
            if (frame == 3) captured = seam.ReadBackLevel0ForTests(snapshot.ColorTextureIds[0]);
            platform.EndFrame();
        }

        var expected = new double[] { (0.1 + 0.75 * 0.5) * 255, (0.7 - 0.75 * 0.4) * 255, 0.3 * 255, 255 };
        AssertHalf(captured, left: true, expected, "snapshot");
        AssertHalf(captured, left: false, expected, "snapshot");
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// The factor rule without a device: only Standard's exact factor set changes, only its source
    /// alpha factor, and only for a draw into the UI image while the scope is open.
    /// </summary>
    [Fact]
    public void OnlyStandardDrawnIntoTheUiImageTakesOverOperatorAlpha()
    {
        AttachmentBlend standard = AttachmentBlend.For(true, EnumBlendMode.Standard).ForUiImage();
        Assert.Equal(BlendFactor.SrcAlpha, standard.SrcColor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, standard.DstColor);
        Assert.Equal(BlendFactor.One, standard.SrcAlpha);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, standard.DstAlpha);

        foreach (EnumBlendMode mode in new[]
                 {
                     EnumBlendMode.PremultipliedAlpha, EnumBlendMode.Brighten, EnumBlendMode.Multiply,
                     EnumBlendMode.Glow, EnumBlendMode.Overlay,
                 })
        {
            AttachmentBlend plain = AttachmentBlend.For(true, mode);
            Assert.Equal(plain, plain.ForUiImage());
        }

        var stated = new StatedRenderState();
        stated.SetBlendEnabled(true);
        stated.SetBlendMode(EnumBlendMode.Standard);
        Assert.Equal(BlendFactor.SrcAlpha, stated.AttachmentFor(PassDeclaration.DefaultFramebuffer, 0).SrcAlpha);

        stated.UiImageFramebuffer = 42;
        Assert.Equal(BlendFactor.One, stated.AttachmentFor(PassDeclaration.DefaultFramebuffer, 0).SrcAlpha);
        Assert.Equal(BlendFactor.One, stated.AttachmentFor(42, 0).SrcAlpha);
        Assert.Equal(BlendFactor.SrcAlpha, stated.AttachmentFor(7, 0).SrcAlpha);

        stated.UiImageFramebuffer = 0;
        Assert.Equal(BlendFactor.SrcAlpha, stated.AttachmentFor(PassDeclaration.DefaultFramebuffer, 0).SrcAlpha);
    }

    // ------------------------------------------------------------------------ arithmetic

    /// <summary>A straight-alpha layer (rgb in bytes, alpha 0..1) as premultiplied bytes.</summary>
    private static double[] Premultiplied(double[] layer) =>
        new[] { layer[0] * layer[3], layer[1] * layer[3], layer[2] * layer[3], layer[3] * 255 };

    /// <summary>The over-operator on premultiplied bytes.</summary>
    private static double[] Over(double[] top, double[] under)
    {
        double keep = 1 - top[3] / 255;
        return new[] { top[0] + under[0] * keep, top[1] + under[1] * keep, top[2] + under[2] * keep, top[3] + under[3] * keep };
    }

    /// <summary>The window after the compose: the UI over the opaque scene.</summary>
    private static double[] Composed(double[] ui) => Over(ui, Scene);

    private void AssertHalf(byte[] pixels, bool left, double[] expected, string what)
    {
        int wrong = 0;
        string first = "";
        for (int y = 0; y < Size; y++)
        {
            for (int x = left ? 0 : Half; x < (left ? Half : Size); x++)
            {
                int i = (y * Size + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    if (Math.Abs(pixels[i + c] - expected[c]) <= 2) continue;
                    if (wrong == 0)
                    {
                        first = $" first at ({x},{y}): {pixels[i]},{pixels[i + 1]},{pixels[i + 2]},{pixels[i + 3]}";
                    }
                    wrong++;
                    break;
                }
            }
        }
        output.WriteLine($"{what} ({(left ? "left" : "right")}): expected " +
            $"{expected[0]:F0},{expected[1]:F0},{expected[2]:F0},{expected[3]:F0}, wrong {wrong}/{Half * Size}{first}");
        Assert.True(wrong == 0, what + ": " + wrong + " pixels off by more than 2/255." + first);
    }

    // ------------------------------------------------------------------------ driving

    private readonly record struct Frame(byte[] Ui, byte[] Before, byte[] After);

    private Session Open()
    {
        Session? session = Session.TryOpen(output);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    private sealed class Session : IDisposable
    {
        public SeparationPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public int LayerAProgram { get; private set; }
        public int LayerBProgram { get; private set; }

        private ShaderProgram? previousCompose;
        private string dataPath = "";

        public static Session? TryOpen(ITestOutputHelper output)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-ui-separation-" + Guid.NewGuid().ToString("N"));
            var platform = new SeparationPlatform
            {
                DeviceFactory = GpuTest.NewDevice,
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
                dataPath = dataPath,
                previousCompose = ShaderPrograms.UiCompose,
            };
            VulkanDevice seam = platform.GraphicsDevice!;

            // Primary as the composition leaves it, plus the two separation images, installed
            // the way SetupDefaultFrameBuffers installs them.
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = ColorTarget(seam);
            platform.AllocateUiSeparationTargets(list, Size, Size);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);

            session.LayerAProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, LayerAFragment, "ui-layer-a");
            session.LayerBProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, LayerBFragment, "ui-layer-b");
            int compose = VulkanDeviceIntegrationTests.LinkProgram(
                seam, ComposeSource("vsh"), ComposeSource("fsh"), "ui-compose");
            ShaderPrograms.UiCompose = new ShaderProgram { ProgramId = compose, PassName = "ui-compose" };
            return session;
        }

        /// <summary>
        /// One frame from the blit on: the window image painted with the scene, the scope opened
        /// as the blit's end opens it, the GUI-shaped draws naming only Default, and the compose.
        /// </summary>
        public Frame RunFrame(bool layerA, bool layerB, bool read)
        {
            VulkanDevice seam = Seam;
            FrameBufferRef ui = Platform.UiTargetFrameBuffer!;
            Platform.BeginFrame();
            seam.ClearNativeColor(PassDeclaration.DefaultFramebuffer, 0,
                (float)(Scene[0] / 255), (float)(Scene[1] / 255), (float)(Scene[2] / 255), 1f);

            Platform.OpenUiScope();
            Platform.CurrentFrameBuffer = null;
            if (layerA) DrawLayer(LayerAProgram);
            if (layerB) DrawLayer(LayerBProgram);

            byte[] uiPixels = read ? seam.ReadBackLevel0ForTests(ui.ColorTextureIds[0]) : Array.Empty<byte>();
            byte[] before = read ? ReadWindow(seam) : Array.Empty<byte>();
            Platform.OptimumComposeUiTarget();
            byte[] after = read ? ReadWindow(seam) : Array.Empty<byte>();
            Platform.EndFrame();
            return new Frame(uiPixels, before, after);
        }

        /// <summary>A straight-alpha fullscreen layer under Standard, into whatever is bound.</summary>
        public void DrawLayer(int program)
        {
            Platform.GlViewport(0, 0, Size, Size);
            Platform.GlDisableDepthTest();
            Platform.GlDepthMask(false);
            Platform.GlDisableCullFace();
            Platform.GlToggleBlend(true, EnumBlendMode.Standard);
            Platform.UseShaderProgram(program);
            Platform.RenderFullscreenTriangle(null!);
            Platform.UseShaderProgram(0);
        }

        /// <summary>The window image itself, whatever Default resolves to right now, in RGBA.</summary>
        private static unsafe byte[] ReadWindow(VulkanDevice seam)
        {
            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                seam.ReadFramebufferColor(seam.DefaultFramebufferId, 0, 0, Size, Size, (IntPtr)destination);
            }
            if (seam.DefaultColorFormat == Format.B8G8R8A8Unorm || seam.DefaultColorFormat == Format.B8G8R8A8Srgb)
            {
                for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
            return pixels;
        }

        private static FrameBufferRef ColorTarget(VulkanDevice seam)
        {
            var target = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    seam.CreateTexture2D(Size, Size,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                },
            };
            seam.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
            seam.SetDrawBuffers(target.FboId, 1);
            Assert.True(seam.CheckFramebufferComplete(target.FboId, out string status), status);
            return target;
        }

        public void Dispose()
        {
            ShaderPrograms.UiCompose = previousCompose;
            Platform.ShutdownGraphics();
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
