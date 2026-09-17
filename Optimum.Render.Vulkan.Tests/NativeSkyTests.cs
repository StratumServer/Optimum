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

using LinkedProgram = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The sky dome, drawn twice on one Vulkan device: through the seam's neutral body (the OpenGL
/// body's RenderMesh, the route every vanilla system still takes) and through the native pass
/// VulkanClientPlatform.RenderSkyDome records (docs/vulkan-native-render-systems.md, decision 5
/// stage 2 - the first world system on the native device API).
///
/// Behavioural identity is the acceptance rule (decision 6): the same shader, the same mesh and
/// the same fixed state have to put the same pixels on Primary's scene and glow attachments,
/// and the native route must not touch the GL state tracker, a texture unit or a draw-buffer
/// mask while its pass is open.
/// </summary>
public class NativeSkyTests(ITestOutputHelper output)
{
    private const int Size = 16;

    /// <summary>The platform with no window: both routes take their size from this seam.</summary>
    private sealed class SkyPlatform : VulkanClientPlatform
    {
        public SkyPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    // ------------------------------------------------------------------ the tests

    /// <summary>
    /// The native sky pass draws what the seam's neutral body draws: one declared pass, one
    /// native mesh draw, and the same scene and glow pixels.
    /// </summary>
    [SkippableFact]
    public unsafe void TheNativeSkyPassMatchesTheSeamsNeutralBody()
    {
        using Session session = Open();

        (byte[] statedScene, byte[] statedGlow) = session.RunFrame(native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        long meshDrawsBefore = session.Seam.NativeMeshDrawsForTests;
        (byte[] nativeScene, byte[] nativeGlow) = session.RunFrame(native: true);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeMeshDrawsForTests - meshDrawsBefore);

        output.WriteLine("scene centre stated " + Centre(statedScene) + " native " + Centre(nativeScene));
        Assert.Equal(statedScene, nativeScene);
        Assert.Equal(statedGlow, nativeGlow);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The seam's neutral body draws through the generic stated route and the native route does not:
    /// the switch is real, and "OFF is vanilla" holds for the route the OpenGL path takes.
    /// </summary>
    [SkippableFact]
    public unsafe void TheNeutralBodyDrawsThroughTheStatedRouteAndTheNativeRouteDoesNot()
    {
        using Session session = Open();

        long nativeDrawsBefore = session.Seam.NativeDrawsForTests;
        long statedBefore = session.Platform.StatedDrawsForTests;
        session.RunFrame(native: false);
        Assert.Equal(0, session.Seam.NativeDrawsForTests - nativeDrawsBefore);
        Assert.True(session.Platform.StatedDrawsForTests - statedBefore > 0);

        session.RunFrame(native: true);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The pass is declared for the mesh's own vertex layout, so the pipeline it draws through
    /// is a mesh pipeline and one per target, reused across frames rather than rebuilt.
    /// </summary>
    [SkippableFact]
    public unsafe void TheSkyPassBuildsOneMeshPipelineAndKeepsIt()
    {
        using Session session = Open();

        session.RunFrame(native: true);
        int after = session.Seam.NativePipelinesForTests;
        session.RunFrame(native: true);
        session.RunFrame(native: true);

        Assert.Equal(after, session.Seam.NativePipelinesForTests);
        Assert.Equal(3, session.Seam.NativeMeshDrawsForTests);
        GpuTest.AssertClean(session.Seam);
    }

    // ---------------------------------------------------------------------- driving

    private static string Centre(byte[] pixels)
    {
        int i = (Size / 2 * Size + Size / 2) * 4;
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

    /// <summary>
    /// The platform, its device, the Primary target the Opaque stage binds, the sky program and
    /// the dome mesh, installed the way the client installs them and put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        /// <summary>The model-view matrix the seam carries: identity, so the dome's clip positions stand.</summary>
        private static readonly float[] Identity =
            { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public SkyPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Primary { get; private set; } = null!;
        public MeshRef Dome { get; private set; } = null!;
        public int SkyTexture { get; private set; }
        public int GlowTexture { get; private set; }

        private ShaderProgram sky = null!;
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";

        public static unsafe Session? TryOpen(ITestOutputHelper output, string manifestDirectory)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-sky-" + Guid.NewGuid().ToString("N"));
            var platform = new SkyPlatform
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
            session.Primary = CreatePrimary(seam);
            InstallFrameBuffers(platform, session.Primary);

            var program = new ShaderProgram { PassName = "sky" };
            Link(seam, program, "sky", new[] { "projectionMatrix", "modelViewMatrix" });
            session.sky = program;

            session.SkyTexture = Gradient(seam, 0);
            session.GlowTexture = Gradient(seam, 1);
            foreach (string name in seam.SamplerNamesOf(program.ProgramId))
            {
                // The units the client's ShaderProgramSky setters bind: what the stated route
                // resolves its samplers through. The native route passes the handles instead.
                int unit = program.uniformLocations.Count + seam.SamplerNamesOf(program.ProgramId).IndexOf(name);
                seam.SetSamplerUnit(program.ProgramId, name, unit);
                seam.BindTexture(unit, name == "sky" ? session.SkyTexture
                    : name == "glow" ? session.GlowTexture : session.SkyTexture);
            }

            session.Dome = platform.UploadMesh(BuildDome());
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            // The mesh goes first: VAO's finalizer reaches for ScreenManager.Platform, which is
            // about to be the client's again, and a live handle there would crash the test host.
            if (Dome != null) Platform.DeleteMesh(Dome);
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
        /// One frame of the Opaque stage at the point the sky renderer runs: Primary bound and
        /// cleared, depth test off, no culling, the program's uniforms set, then the seam.
        /// </summary>
        public unsafe (byte[] Scene, byte[] Glow) RunFrame(bool native)
        {
            VulkanDevice seam = Seam;
            Platform.NativeSkyEnabled = native;

            Platform.BeginFrame();
            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, 0b11);
            seam.ClearColor(0, 0.125f, 0.25f, 0.5f, 1f);
            seam.ClearColor(1, 0.75f, 0.5f, 0.25f, 1f);
            seam.ClearDepth(1f);

            Platform.CurrentFrameBuffer = Primary;
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);

            seam.UseProgram(sky.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = sky;
            seam.SetUniformMatrix(sky.ProgramId, sky.uniformLocations["projectionMatrix"], Identity);
            seam.SetUniformMatrix(sky.ProgramId, sky.uniformLocations["modelViewMatrix"], Identity);

            Platform.RenderSkyDome(Dome, SkyTexture, GlowTexture, Identity);

            byte[] scene = Read(seam, Primary.ColorTextureIds[0]);
            byte[] glow = Read(seam, Primary.ColorTextureIds[1]);
            Platform.EndFrame();
            return (scene, glow);
        }

        /// <summary>One attachment's pixels, read through a framebuffer that holds only it.</summary>
        private unsafe byte[] Read(VulkanDevice seam, int texture)
        {
            int reader = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(reader, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(reader, 1);
            seam.BindFramebuffer(reader);

            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }
            seam.BindFramebuffer(Primary.FboId);
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

        /// <summary>Primary as the Opaque stage has it: scene at 0, glow at 1, plus depth.</summary>
        private static FrameBufferRef CreatePrimary(VulkanDevice seam)
        {
            var primary = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    seam.CreateTexture2D(Size, Size,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                    seam.CreateTexture2D(Size, Size,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                },
                DepthTextureId = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                    IntPtr.Zero, false),
            };
            for (int slot = 0; slot < primary.ColorTextureIds.Length; slot++)
            {
                seam.AttachTexture(primary.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    primary.ColorTextureIds[slot], 0);
            }
            seam.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
            seam.SetDrawBuffers(primary.FboId, 0b11);
            Assert.True(seam.CheckFramebufferComplete(primary.FboId, out string status), status);
            return primary;
        }

        private static void InstallFrameBuffers(SkyPlatform platform, FrameBufferRef primary)
        {
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = primary;

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
                    pixels[i + 3] = 255;
                }
            }
            fixed (byte* first = pixels)
            {
                return seam.CreateTexture2D(8, 8,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)first, false);
            }
        }

        /// <summary>
        /// The dome, as the sky program sees it: positions and a per-vertex colour, no UVs -
        /// the shape SystemRenderSkyColor uploads (genIcosahedron with Uv nulled), reduced to
        /// two triangles that cover the target so every pixel is comparable.
        /// </summary>
        private static MeshData BuildDome()
        {
            var mesh = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: true, withFlags: false);
            float[] positions =
            {
                -0.9f, -0.9f, 0.5f,
                 0.9f, -0.9f, 0.5f,
                 0.9f,  0.9f, 0.5f,
                -0.9f,  0.9f, 0.5f,
            };
            for (int i = 0; i < 4; i++)
            {
                mesh.AddVertexSkipTex(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                    ColorUtil.WhiteArgb);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(index);
            return mesh;
        }
    }

    // ---------------------------------------------------------------- native shaders

    /// <summary>The sky program's manifest, built once for the whole class.</summary>
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
            NativeShaderBuildResult one = builder.Build(source, "sky");
            merged.Errors.AddRange(one.Errors);
            merged.Manifest.Programs.AddRange(one.Manifest.Programs);
            foreach ((string file, byte[] bytes) in one.Files) merged.Files[file] = bytes;
            if (!merged.Success) return ("", string.Join("\n", merged.Errors));

            string root = Path.Combine(Path.GetTempPath(), "optimum-native-sky-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
