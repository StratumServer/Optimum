using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

using LinkedProgram = Optimum.Render.Vulkan.Tests.GpuTest.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.GpuTest.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The two GUI and text systems that draw through the native device API, each drawn twice on
/// one Vulkan device: through the seam's neutral body (the OpenGL body's RenderMesh, the route
/// every system that has not moved still takes) and through the native pass
/// VulkanClientPlatform records (docs/vulkan.md, decision 5 stage 2).
///
/// - the texture-into-texture blit, which bakes every Cairo-drawn GUI and text surface into a
///   texture (ClientMain.RenderTextureIntoFrameBuffer, the texture2texture program);
/// - the aiming reticle's line draws (SystemRenderPlayerAimAcc, the gui program with noTexture
///   set), which are the first native draws with line topology and a caller-chosen line width.
///
/// Behavioural identity is the acceptance rule (decision 6): the same shader, the same mesh and
/// the same fixed state have to put the same pixels on the target, with blending on and off and
/// at every line width the callers ask for, and the native route must not touch the GL state
/// tracker, a texture unit or a draw-buffer mask while its pass is open.
/// </summary>
public class NativeGuiTests(ITestOutputHelper output)
{
    private const int Size = 16;

    /// <summary>The platform with no window: both routes take their size from this seam.</summary>
    private sealed class GuiPlatform : VulkanClientPlatform
    {
        public GuiPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    // ------------------------------------------------------------------ the tests

    /// <summary>
    /// The native texture-blit pass draws what the seam's neutral body draws, with blending on
    /// (the alphaTest >= 0 case, which is every Cairo bake) and with it off: one declared pass,
    /// one native mesh draw, and the same pixels.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public unsafe void TheNativeTextureBlitMatchesTheSeamsNeutralBody(bool blend)
    {
        using Session session = Open();

        byte[] stated = session.RunTextureQuad(native: false, blend);

        long passesBefore = session.Seam.NativePassesForTests;
        long meshDrawsBefore = session.Seam.NativeMeshDrawsForTests;
        byte[] native = session.RunTextureQuad(native: true, blend);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeMeshDrawsForTests - meshDrawsBefore);

        output.WriteLine("blit centre stated " + Centre(stated) + " native " + Centre(native));
        Assert.Equal(stated, native);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The native line overlay draws what the seam's neutral body draws, at both widths the
    /// aiming reticle asks for - 0.5 for the accuracy rectangle and 1 for the crosshair lines.
    /// The clamp to the device's lineWidthRange is on both routes, so a driver whose minimum is
    /// 1.0 rasterizes the 0.5 draw the same way through either.
    /// </summary>
    [SkippableTheory]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public unsafe void TheNativeLineOverlayMatchesTheSeamsNeutralBody(float lineWidth)
    {
        using Session session = Open();

        byte[] stated = session.RunOverlayLines(native: false, lineWidth);

        long meshDrawsBefore = session.Seam.NativeMeshDrawsForTests;
        byte[] native = session.RunOverlayLines(native: true, lineWidth);

        Assert.Equal(1, session.Seam.NativeMeshDrawsForTests - meshDrawsBefore);

        output.WriteLine("line row stated " + Row(stated) + " native " + Row(native));
        Assert.Equal(stated, native);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The seams' neutral bodies draw through the generic stated route and the native route does
    /// not: the switch is real, and "OFF is vanilla" holds for the route the OpenGL path takes.
    /// </summary>
    [SkippableFact]
    public unsafe void TheNeutralBodiesDrawThroughTheStatedRouteAndTheNativeRouteDoesNot()
    {
        using Session session = Open();

        long nativeDrawsBefore = session.Seam.NativeDrawsForTests;
        long statedBefore = session.Platform.StatedDrawsForTests;
        session.RunTextureQuad(native: false, blend: true);
        session.RunOverlayLines(native: false, 1.0f);
        Assert.Equal(0, session.Seam.NativeDrawsForTests - nativeDrawsBefore);
        Assert.True(session.Platform.StatedDrawsForTests - statedBefore > 0);

        session.RunTextureQuad(native: true, blend: true);
        session.RunOverlayLines(native: true, 1.0f);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The two systems are two pipelines and stay two, frame after frame - a Cairo bake that
    /// happens hundreds of times in a frame must not build a pipeline per call - and the line
    /// width is part of the pipeline's identity, so the reticle's 0.5 and 1.0 draws are two
    /// entries rather than one entry drawn twice at whichever width came last.
    /// </summary>
    [SkippableFact]
    public unsafe void TheGuiPassesKeepTheirPipelinesAndSeparateTheLineWidths()
    {
        using Session session = Open();

        session.RunTextureQuad(native: true, blend: true);
        session.RunOverlayLines(native: true, 1.0f);
        int afterBoth = session.Seam.NativePipelinesForTests;

        session.RunTextureQuad(native: true, blend: true);
        session.RunOverlayLines(native: true, 1.0f);
        Assert.Equal(afterBoth, session.Seam.NativePipelinesForTests);

        // A different line width is a different pipeline, not the same one re-emitted.
        session.RunOverlayLines(native: true, 0.5f);
        Assert.Equal(afterBoth + 1, session.Seam.NativePipelinesForTests);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// A Cairo bake is a fresh texture every time. Twenty of them through the native route must
    /// cost twenty bindless slot resolutions out of the frame's own arena and still exactly one
    /// pipeline: the per-frame descriptor churn the stage brief warns about has to land in the
    /// arena, not in a permanent descriptor per texture.
    /// </summary>
    [SkippableFact]
    public unsafe void EveryFreshTextureResolvesIntoTheFrameArenaAndBuildsNoNewPipeline()
    {
        using Session session = Open();

        session.RunTextureQuad(native: true, blend: true);
        int pipelines = session.Seam.NativePipelinesForTests;

        var textures = new List<int>();
        for (int i = 0; i < 20; i++) textures.Add(session.NewGradient(i + 3));

        long drawsBefore = session.Seam.NativeMeshDrawsForTests;
        foreach (int texture in textures) session.RunTextureQuad(native: true, blend: true, texture);

        Assert.Equal(textures.Count, session.Seam.NativeMeshDrawsForTests - drawsBefore);
        Assert.Equal(pipelines, session.Seam.NativePipelinesForTests);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// An atlas composition samples the texture it writes (BlendedTextureManager copies one atlas
    /// region into another region of the same atlas). The native pass takes the same pooled
    /// ReadSelf copy the stated route takes, instead of refusing the draw: two native mesh
    /// draws, the same pixels, validation clean.
    /// </summary>
    [SkippableFact]
    public unsafe void TheNativeTextureBlitReadsItsOwnTargetThroughACopy()
    {
        using Session session = Open();

        byte[] stated = session.RunSelfBlit(native: false);

        long meshDrawsBefore = session.Seam.NativeMeshDrawsForTests;
        byte[] native = session.RunSelfBlit(native: true);

        Assert.Equal(2, session.Seam.NativeMeshDrawsForTests - meshDrawsBefore);

        output.WriteLine("self blit row stated " + Row(stated) + " native " + Row(native));
        Assert.Equal(stated, native);
        // The right half is the copied left half, not the clear colour.
        int left = (Size / 2 * Size + 1) * 4;
        int right = (Size / 2 * Size + Size / 2 + 1) * 4;
        Assert.Equal(stated[left + 1], stated[right + 1]);
        GpuTest.AssertClean(session.Seam);
    }

    // ---------------------------------------------------------------------- driving

    private static string Centre(byte[] pixels)
    {
        int i = (Size / 2 * Size + Size / 2) * 4;
        return pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," + pixels[i + 3];
    }

    /// <summary>A whole row through the middle, where the overlay's line lands.</summary>
    private static string Row(byte[] pixels)
    {
        var text = new System.Text.StringBuilder();
        for (int x = 0; x < Size; x++)
        {
            int i = (Size / 2 * Size + x) * 4;
            text.Append(pixels[i]).Append(':').Append(pixels[i + 3]).Append(' ');
        }
        return text.ToString();
    }

    private Session Open()
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);

        Session? session = Session.TryOpen(output, manifest);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    /// <summary>
    /// The platform, its device, the target the GUI draws into, both programs and both meshes,
    /// installed the way the client installs them and put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private static readonly float[] Identity =
            { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public GuiPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Target { get; private set; } = null!;
        public MeshRef Quad { get; private set; } = null!;
        public MeshRef Lines { get; private set; } = null!;
        public int SourceTexture { get; private set; }

        private ShaderProgram blit = null!;
        private ShaderProgram gui = null!;
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";

        public static unsafe Session? TryOpen(ITestOutputHelper output, string manifestDirectory)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-gui-" + Guid.NewGuid().ToString("N"));
            var platform = new GuiPlatform
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
            };
            ScreenManager.Platform = platform;
            platform.ShaderUniforms = new DefaultShaderUniforms();

            VulkanDevice seam = platform.GraphicsDevice!;
            session.Target = CreateTarget(seam);
            InstallFrameBuffers(platform, session.Target);

            var blitProgram = new ShaderProgram { PassName = "texture2texture" };
            Link(seam, blitProgram, "texture2texture",
                new[] { "xs", "ys", "width", "height", "texu", "texv", "texw", "texh", "alphaTest" });
            session.blit = blitProgram;

            var guiProgram = new ShaderProgram { PassName = "gui" };
            Link(seam, guiProgram, "gui",
                new[] { "projectionMatrix", "modelViewMatrix", "rgbaIn", "noTexture", "applyColor", "alphaTest" });
            session.gui = guiProgram;

            session.SourceTexture = Gradient(seam, 0);
            BindSamplerUnits(seam, blitProgram, session.SourceTexture);
            BindSamplerUnits(seam, guiProgram, session.SourceTexture);

            session.Quad = platform.UploadMesh(BuildQuad());
            session.Lines = platform.UploadMesh(BuildLines());
            return session;
        }

        /// <summary>
        /// The units the client's program setters bind: what the stated route resolves its
        /// samplers through. The native route passes the handles instead.
        /// </summary>
        private static void BindSamplerUnits(VulkanDevice seam, ShaderProgramBase program, int texture)
        {
            string[] names = seam.SamplerNamesOf(program.ProgramId);
            for (int i = 0; i < names.Length; i++)
            {
                int unit = program.uniformLocations.Count + i;
                seam.SetSamplerUnit(program.ProgramId, names[i], unit);
                // Only the first sampler ever carries a texture here: texture2texture has one,
                // and gui's overlay sampler is unused while noTexture is 1, which is exactly the
                // reticle's case - so both routes see nothing bound for it.
                seam.BindTexture(unit, i == 0 ? texture : 0);
            }
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            // The meshes go first: VAO's finalizer reaches for ScreenManager.Platform, which is
            // about to be the client's again, and a live handle there would crash the test host.
            if (Quad != null) Platform.DeleteMesh(Quad);
            if (Lines != null) Platform.DeleteMesh(Lines);
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

        /// <summary>A fresh source texture, as a Cairo bake produces one per surface.</summary>
        public unsafe int NewGradient(int phase) => Gradient(Seam, phase);

        /// <summary>
        /// One frame at the point RenderTextureIntoFrameBuffer reaches its draw: the
        /// destination framebuffer bound and cleared, the depth test off, blending exactly as
        /// that method's alphaTest decided, the program's uniforms set, then the seam.
        /// </summary>
        public unsafe byte[] RunTextureQuad(bool native, bool blend, int textureId = 0)
        {
            VulkanDevice seam = Seam;
            Platform.NativeGuiEnabled = native;
            int source = textureId != 0 ? textureId : SourceTexture;

            Platform.BeginFrame();
            BeginTarget(seam);
            seam.SetBlend(blend, EnumBlendMode.Standard);

            seam.UseProgram(blit.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = blit;
            // The destination rectangle covers the whole target and the source rectangle the
            // whole texture: RenderTextureIntoFrameBuffer's own normalisation, at its limits.
            Set(seam, blit, "xs", 0f);
            Set(seam, blit, "ys", 0f);
            Set(seam, blit, "width", 1f);
            Set(seam, blit, "height", 1f);
            Set(seam, blit, "texu", 0f);
            Set(seam, blit, "texv", 0f);
            Set(seam, blit, "texw", 1f);
            Set(seam, blit, "texh", 1f);
            Set(seam, blit, "alphaTest", blend ? 0.005f : -1f);
            if (textureId != 0) seam.BindTexture(blit.uniformLocations.Count, textureId);

            Platform.RenderTextureQuad(Quad, source, blend);

            byte[] pixels = Read(seam);
            Platform.EndFrame();
            return pixels;
        }

        /// <summary>
        /// One frame of an atlas composition: the gradient blitted over the whole target, then
        /// the target's left half blitted into its right half with the target's own texture as
        /// the source, blending off (BlendedTextureManager's base copy).
        /// </summary>
        public unsafe byte[] RunSelfBlit(bool native)
        {
            VulkanDevice seam = Seam;
            Platform.NativeGuiEnabled = native;

            Platform.BeginFrame();
            BeginTarget(seam);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.UseProgram(blit.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = blit;

            SetRects(seam, 0f, 1f, 0f, 1f);
            seam.BindTexture(blit.uniformLocations.Count, SourceTexture);
            Platform.RenderTextureQuad(Quad, SourceTexture, false);

            int own = Target.ColorTextureIds[0];
            SetRects(seam, 0.5f, 0.5f, 0f, 0.5f);
            seam.BindTexture(blit.uniformLocations.Count, own);
            Platform.RenderTextureQuad(Quad, own, false);
            seam.BindTexture(blit.uniformLocations.Count, SourceTexture);

            byte[] pixels = Read(seam);
            Platform.EndFrame();
            return pixels;
        }

        private void SetRects(VulkanDevice seam, float xs, float width, float texu, float texw)
        {
            Set(seam, blit, "xs", xs);
            Set(seam, blit, "ys", 0f);
            Set(seam, blit, "width", width);
            Set(seam, blit, "height", 1f);
            Set(seam, blit, "texu", texu);
            Set(seam, blit, "texv", 0f);
            Set(seam, blit, "texw", texw);
            Set(seam, blit, "texh", 1f);
            Set(seam, blit, "alphaTest", -1f);
        }

        /// <summary>
        /// One frame at the point SystemRenderPlayerAimAcc reaches one of its draws: the gui
        /// program current with noTexture set, blending on, the line width it just chose.
        /// </summary>
        public unsafe byte[] RunOverlayLines(bool native, float lineWidth)
        {
            VulkanDevice seam = Seam;
            Platform.NativeGuiEnabled = native;

            Platform.BeginFrame();
            BeginTarget(seam);
            seam.SetBlend(true, EnumBlendMode.Standard);
            seam.SetLineWidth(lineWidth);

            seam.UseProgram(gui.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = gui;
            seam.SetUniformMatrix(gui.ProgramId, gui.uniformLocations["projectionMatrix"], Identity);
            seam.SetUniformMatrix(gui.ProgramId, gui.uniformLocations["modelViewMatrix"], Identity);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["rgbaIn"], 1f, 0.5f, 0.25f, 1f);
            Set(seam, gui, "noTexture", 1f);
            seam.SetUniform(gui.ProgramId, gui.uniformLocations["applyColor"], 0);
            Set(seam, gui, "alphaTest", 0f);

            Platform.RenderOverlayLines(Lines, 0, lineWidth, blend: true);

            byte[] pixels = Read(seam);
            Platform.EndFrame();
            return pixels;
        }

        private static void Set(VulkanDevice seam, ShaderProgramBase program, string name, float value) =>
            seam.SetUniform(program.ProgramId, program.uniformLocations[name], value);

        /// <summary>The target bound, cleared and put into the state both GUI systems draw in.</summary>
        private void BeginTarget(VulkanDevice seam)
        {
            seam.BindFramebuffer(Target.FboId);
            seam.SetDrawBuffers(Target.FboId, 1);
            seam.ClearColor(0, 0.1f, 0.2f, 0.3f, 1f);

            Platform.CurrentFrameBuffer = Target;
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetCullFace(false);
        }

        private unsafe byte[] Read(VulkanDevice seam)
        {
            int reader = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(reader, EnumFramebufferAttachment.ColorAttachment0, Target.ColorTextureIds[0], 0);
            seam.SetDrawBuffers(reader, 1);
            seam.BindFramebuffer(reader);

            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }
            seam.BindFramebuffer(Target.FboId);
            return pixels;
        }

        // ----------------------------------------------------------------- fixtures

        /// <summary>Links one vanilla program as ShaderRegistry does and fills the locations the test sets.</summary>
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

        /// <summary>
        /// The destination a Cairo bake writes into and the default framebuffer the Ortho stage
        /// draws into have the same shape here: one colour attachment, no depth.
        /// </summary>
        private static FrameBufferRef CreateTarget(VulkanDevice seam)
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

        private static void InstallFrameBuffers(GuiPlatform platform, FrameBufferRef target)
        {
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = target;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);
        }

        /// <summary>A small gradient, so a sampling difference between the routes would show.</summary>
        private static unsafe int Gradient(VulkanDevice seam, int phase)
        {
            var pixels = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int i = (y * 8 + x) * 4;
                    pixels[i] = (byte)(16 + x * 30 + phase * 7);
                    pixels[i + 1] = (byte)(32 + y * 25);
                    pixels[i + 2] = (byte)(((x + y) & 1) * 200 + 20);
                    pixels[i + 3] = (byte)(96 + ((x + y) & 3) * 40);
                }
            }
            fixed (byte* first = pixels)
            {
                return seam.CreateTexture2D(8, 8,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)first, false);
            }
        }

        /// <summary>The unit quad ClientMain keeps for its 2D draws: positions and UVs.</summary>
        private static MeshData BuildQuad()
        {
            var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: false);
            float[] positions =
            {
                -1f, -1f, 0f,
                 1f, -1f, 0f,
                 1f,  1f, 0f,
                -1f,  1f, 0f,
            };
            float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            for (int i = 0; i < 4; i++)
            {
                mesh.AddVertexWithFlags(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                    uvs[i * 2], uvs[i * 2 + 1], ColorUtil.WhiteArgb, 0);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(index);
            return mesh;
        }

        /// <summary>
        /// One line across the middle, in the shape SystemRenderPlayerAimAcc tesselates its
        /// reticle in: EnumDrawMode.Lines, positions and a per-vertex colour, no textures.
        /// </summary>
        private static MeshData BuildLines()
        {
            var mesh = new MeshData(2, 2, withNormals: false, withUv: true, withRgba: true, withFlags: false);
            mesh.SetMode(EnumDrawMode.Lines);
            mesh.AddVertexWithFlags(-0.8f, 0f, 0f, 0f, 0f, ColorUtil.WhiteArgb, 0);
            mesh.AddVertexWithFlags(0.8f, 0f, 0f, 1f, 0f, ColorUtil.WhiteArgb, 0);
            mesh.AddIndex(0);
            mesh.AddIndex(1);
            return mesh;
        }
    }

    // ---------------------------------------------------------------- native shaders

    /// <summary>The two programs' manifest, built once for the whole class.</summary>
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
            foreach (string program in new[] { "texture2texture", "gui" })
            {
                NativeShaderBuildResult one = builder.Build(source, program);
                merged.Errors.AddRange(one.Errors);
                merged.Manifest.Programs.AddRange(one.Manifest.Programs);
                foreach ((string file, byte[] bytes) in one.Files) merged.Files[file] = bytes;
            }
            if (!merged.Success) return ("", string.Join("\n", merged.Errors));

            string root = Path.Combine(Path.GetTempPath(), "optimum-native-gui-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
