using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// The terrain, drawn twice on one Vulkan device: through the stated multi-draw the OpenGL
/// body takes (NativeChunksEnabled false) and through the native pass
/// VulkanClientPlatform.NativeChunks.cs records inside a BeginChunkPass / EndChunkPass scope
/// (docs/vulkan-native-render-systems.md, decision 5 stage 2).
///
/// Behavioural identity is the acceptance rule (decision 6). The same chunkopaque program, the
/// same pooled mesh and the same fixed state have to put the same pixels on every attachment of
/// Primary - the scene, the glow and, with a motion window open, the motion attachment bit for
/// bit - across the settings that change the chunk passes: blending on and off, culling on and
/// off, a motion window open and closed, and the motion-only window of the liquid velocity
/// redraw, whose whole point is that it writes the motion attachment and touches nothing else.
/// The shadow cascade, which draws a different program into a different target, gets the same
/// treatment.
/// </summary>
public class NativeChunkTests(ITestOutputHelper output)
{
    private const int Size = 32;

    /// <summary>The normal-up flags word a solid top face carries (ChunkTerrainRenderTests).</summary>
    private const int UpNormalFlags = 7 << 18;

    /// <summary>The platform with no window: both routes take their size from this seam.</summary>
    private sealed class ChunkPlatform : VulkanClientPlatform
    {
        public ChunkPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    // ------------------------------------------------------------------------- the tests

    /// <summary>
    /// The opaque terrain group: the native route draws what the stated route draws, on every
    /// attachment, under each of the blend and cull combinations ChunkRenderer's five Opaque
    /// groups run with.
    /// </summary>
    [SkippableTheory]
    [InlineData("chunk-opaque", true, true)]
    [InlineData("chunk-vegetation", true, false)]
    [InlineData("chunk-blendnocull", false, false)]
    [InlineData("chunk-decorative", true, true)]
    public void ANativeChunkGroupDrawsWhatTheStatedGroupDraws(string pass, bool blend, bool cull)
    {
        using Session session = Open(motion: false);

        byte[][] stated = session.RunGroup(pass, native: false, blend: blend, cull: cull);
        byte[][] native = session.RunGroup(pass, native: true, blend: blend, cull: cull);

        output.WriteLine("scene centre stated " + Centre(stated[0]) + " native " + Centre(native[0]));
        Assert.Equal(stated[0], native[0]);
        Assert.Equal(stated[1], native[1]);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The native route records the group as one declared pass and one indirect multi-draw -
    /// the shape the chunk path has to keep - and the stated route records neither.
    /// </summary>
    [SkippableFact]
    public void TheNativeGroupIsOneDeclaredPassAndOneIndirectMultiDraw()
    {
        using Session session = Open(motion: false);

        long passes = session.Seam.NativePassesForTests;
        long indirect = session.Seam.NativeIndirectDrawsForTests;
        long draws = session.Seam.NativeDrawsForTests;
        session.RunGroup("chunk-opaque", native: false, blend: true, cull: true);
        Assert.Equal(0, session.Seam.NativeDrawsForTests - draws);
        Assert.Equal(0, session.Seam.NativePassesForTests - passes);

        session.RunGroup("chunk-opaque", native: true, blend: true, cull: true);
        Assert.Equal(1, session.Seam.NativePassesForTests - passes);
        Assert.Equal(1, session.Seam.NativeIndirectDrawsForTests - indirect);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - draws);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// With a motion window open, the motion attachment is bit-identical between the two
    /// routes: the temporal contract does not change when the group moves to a native pass.
    /// </summary>
    [SkippableFact]
    public void TheMotionAttachmentIsIdenticalBetweenTheRoutes()
    {
        using Session session = Open(motion: true);

        byte[][] stated = session.RunGroup("chunk-opaque", native: false, blend: true, cull: false, motion: true);
        byte[][] native = session.RunGroup("chunk-opaque", native: true, blend: true, cull: false, motion: true);

        output.WriteLine("motion centre stated " + Centre(stated[2]) + " native " + Centre(native[2]));
        Assert.Equal(stated[0], native[0]);
        Assert.Equal(stated[1], native[1]);
        Assert.Equal(stated[2], native[2]);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The liquid velocity redraw's window is a colour-write mask, not a draw-buffer toggle:
    /// the motion attachment takes the draw and the scene and glow attachments keep exactly the
    /// contents the clear left, on both routes.
    /// </summary>
    [SkippableFact]
    public void TheMotionOnlyGroupWritesTheMotionAttachmentAndNothingElse()
    {
        using Session session = Open(motion: true);

        byte[][] stated = session.RunGroup("chunk-liquid-motion", native: false, blend: false, cull: false,
            motion: true, motionOnly: true);
        byte[][] native = session.RunGroup("chunk-liquid-motion", native: true, blend: false, cull: false,
            motion: true, motionOnly: true);

        Assert.Equal(stated[0], native[0]);
        Assert.Equal(stated[1], native[1]);
        Assert.Equal(stated[2], native[2]);

        // And the mask really is a mask: the shaded slots still hold the clear.
        Assert.True(IsClear(native[0], Session.SceneClear), "the motion-only group wrote the scene attachment");
        Assert.True(IsClear(native[1], Session.GlowClear), "the motion-only group wrote the glow attachment");
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The shadow cascade: a different program, a different target, one colour attachment, depth
    /// written and no blending. Same acceptance - the two routes agree on the depth the cascade
    /// leaves behind, which is the only thing the shadow map is read for.
    /// </summary>
    [SkippableFact]
    public void TheShadowCascadeMatchesBetweenTheRoutes()
    {
        using Session session = Open(motion: false);

        byte[] stated = session.RunShadowGroup(native: false);
        byte[] native = session.RunShadowGroup(native: true);

        Assert.Equal(stated, native);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The group's pipelines are built once and kept: a frame of terrain does not rebuild a
    /// pipeline per pool, which is what the stated per-draw key resolve used to do.
    /// </summary>
    [SkippableFact]
    public void TheGroupBuildsItsPipelinesOnceAndKeepsThem()
    {
        using Session session = Open(motion: false);

        session.RunGroup("chunk-opaque", native: true, blend: true, cull: true);
        int after = session.Seam.NativePipelinesForTests;
        session.RunGroup("chunk-opaque", native: true, blend: true, cull: true);
        session.RunGroup("chunk-opaque", native: true, blend: true, cull: true);

        Assert.Equal(after, session.Seam.NativePipelinesForTests);
        GpuTest.AssertClean(session.Seam);
    }

    // ------------------------------------------------------------------------- driving

    private static string Centre(byte[] pixels)
    {
        int i = (Size / 2 * Size + Size / 2) * 4;
        return pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," + pixels[i + 3];
    }

    private static bool IsClear(byte[] pixels, byte[] clear)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                if (Math.Abs(pixels[i + c] - clear[c]) > 1) return false;
            }
        }
        return true;
    }

    private Session Open(bool motion)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Session? session = Session.TryOpen(output, motion);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    /// <summary>
    /// The platform, its device, the Primary target the Opaque stage binds, a shadow map, the
    /// chunk programs and one pooled terrain mesh, installed the way the client installs them
    /// and put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        public static readonly byte[] SceneClear = { 32, 64, 128, 255 };
        public static readonly byte[] GlowClear = { 192, 128, 64, 255 };

        public ChunkPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Primary { get; private set; } = null!;
        public FrameBufferRef Shadow { get; private set; } = null!;

        private ShaderProgram opaque = null!;
        private ShaderProgram shadowmap = null!;
        private MeshRef pool = null!;
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";
        private bool motionAttachment;

        /// <summary>One pool group: MeshDataPool hands GL's 64-bit byte offsets as int pairs.</summary>
        private static readonly int[] GroupStarts = { 0, 0 };
        private static readonly int[] GroupSizes = { 6 };

        public static Session? TryOpen(ITestOutputHelper output, bool motion)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-chunks-" + Guid.NewGuid().ToString("N"));
            var platform = new ChunkPlatform
            {
                DeviceFactory = () =>
                {
                    VulkanDevice created = GpuTest.NewDevice();
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
                motionAttachment = motion,
            };
            ScreenManager.Platform = platform;
            platform.ShaderUniforms = new DefaultShaderUniforms();

            VulkanDevice seam = platform.GraphicsDevice!;
            session.Primary = CreatePrimary(seam, motion ? 3 : 2);
            session.Shadow = CreateShadow(seam);
            InstallFrameBuffers(platform, session.Primary);

            // The motion attachment is Primary's slot 2 without the SSAO G-buffer, exactly as
            // SetupDefaultFrameBuffers publishes it.
            platform.SetOptimumMotionAttachmentIndex(motion ? 2 : -1);

            ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First();
            variant.TaaMotion = motion ? 1 : 0;
            variant.TaaMotionLocation = 2;
            variant.UseSsbo = 0;
            session.opaque = session.LinkClientProgram(seam, "chunkopaque", variant);
            session.shadowmap = session.LinkClientProgram(seam, "chunkshadowmap", variant);

            session.pool = platform.UploadMesh(BuildBlockFace());
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            // The mesh goes first: VAO's finalizer reaches for ScreenManager.Platform, which is
            // about to be the client's again, and a live handle there would crash the test host.
            if (pool != null) Platform.DeleteMesh(pool);
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
        /// One ChunkRenderer draw group, as the client runs it: Primary bound and cleared, the
        /// GL-shaped state the group sets (which the OpenGL body still needs and the native pass
        /// ignores), the program's uniforms, then the scope and the pool's multi-draw.
        /// </summary>
        public byte[][] RunGroup(string pass, bool native, bool blend, bool cull,
            bool motion = false, bool motionOnly = false)
        {
            VulkanDevice seam = Seam;
            Platform.NativeChunksEnabled = native;

            Platform.BeginFrame();
            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, (1 << Primary.ColorTextureIds.Length) - 1);
            Clear(seam);

            Platform.CurrentFrameBuffer = Primary;
            seam.SetViewport(0, 0, Size, Size);
            seam.UseProgram(opaque.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = opaque;
            SetProgramUniforms(seam, opaque.ProgramId);

            // What BeginMotionWrite / BeginMotionOnlyWrite do once their guards pass: the window
            // flag, the draw-buffer set the stated route needs, and replace blending on the
            // motion attachment. The native pass reads the flag and states the rest itself.
            SetMotionWriteActive(motion);
            if (motion)
            {
                if (motionOnly) Platform.EnableMotionOnlyDrawBuffers();
                else Platform.EnableMotionDrawBuffers();
                Platform.ApplyOptimumMotionBlendState();
            }

            seam.SetDepthTest(true);
            seam.SetDepthMask(true);
            seam.SetCullFace(cull);
            seam.SetBlend(blend, EnumBlendMode.Standard);
            if (blend) Platform.ApplyOptimumMotionBlendState();

            Platform.BeginChunkPass(pass, blend, depthTest: true, depthWrite: true, cullFace: cull);
            try
            {
                Platform.RenderMesh(pool, GroupStarts, GroupSizes, 1, useSSBOs: false);
            }
            finally
            {
                Platform.EndChunkPass();
            }

            if (motion)
            {
                Platform.RestorePrimaryDrawBuffers();
                SetMotionWriteActive(false);
            }

            var attachments = new byte[Primary.ColorTextureIds.Length][];
            for (int slot = 0; slot < attachments.Length; slot++)
            {
                attachments[slot] = Read(seam, Primary.ColorTextureIds[slot]);
            }
            Platform.EndFrame();
            return attachments;
        }

        /// <summary>One shadow cascade group: the shadow map bound, depth written, no blending.</summary>
        public byte[] RunShadowGroup(bool native)
        {
            VulkanDevice seam = Seam;
            Platform.NativeChunksEnabled = native;

            Platform.BeginFrame();
            seam.BindFramebuffer(Shadow.FboId);
            seam.SetDrawBuffers(Shadow.FboId, 1);
            seam.ClearColor(0, 0f, 0f, 0f, 1f);
            seam.ClearDepth(1f);

            Platform.CurrentFrameBuffer = Shadow;
            seam.SetViewport(0, 0, Size, Size);
            seam.UseProgram(shadowmap.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = shadowmap;
            SetProgramUniforms(seam, shadowmap.ProgramId);

            seam.SetDepthMask(true);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.SetDepthTest(true);
            seam.SetCullFace(false);

            Platform.BeginChunkPass("chunk-shadow-opaque", blend: false, depthTest: true, depthWrite: true,
                cullFace: false);
            try
            {
                Platform.RenderMesh(pool, GroupStarts, GroupSizes, 1, useSSBOs: false);
            }
            finally
            {
                Platform.EndChunkPass();
            }

            byte[] pixels = Read(seam, Shadow.ColorTextureIds[0]);
            Platform.EndFrame();
            return pixels;
        }

        // ----------------------------------------------------------------- the fixtures

        private void Clear(VulkanDevice seam)
        {
            seam.ClearColor(0, SceneClear[0] / 255f, SceneClear[1] / 255f, SceneClear[2] / 255f, 1f);
            seam.ClearColor(1, GlowClear[0] / 255f, GlowClear[1] / 255f, GlowClear[2] / 255f, 1f);
            if (Primary.ColorTextureIds.Length > 2) seam.ClearColor(2, 0f, 0f, 0f, 0f);
            seam.ClearDepth(1f);
        }

        /// <summary>The window flag the platform's guards own; a test opens it directly.</summary>
        private void SetMotionWriteActive(bool active)
        {
            if (!motionAttachment) return;
            typeof(ClientPlatformWindows)
                .GetField("optimumMotionWriteActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Platform, active);
        }

        /// <summary>
        /// The uniforms a chunk program needs to draw anything (ChunkTerrainRenderTests: without
        /// the view distances every fragment fades out and the pass draws nothing), plus the
        /// textures - bound through the platform, because that is the seam the native route
        /// takes its handles from and the stated route its units.
        /// </summary>
        private void SetProgramUniforms(VulkanDevice seam, int programId)
        {
            float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            foreach (string name in new[]
                     {
                         "projectionMatrix", "modelViewMatrix", "mvpMatrix",
                         "prevProjectionMatrix", "prevModelViewMatrix",
                         "toShadowMapSpaceMatrixFar", "toShadowMapSpaceMatrixNear",
                     })
            {
                int location = seam.GetUniformLocation(programId, name);
                if (location >= 0) seam.SetUniformMatrix(programId, location, identity);
            }

            SetFloat(seam, programId, "viewDistance", 1024f);
            SetFloat(seam, programId, "viewDistanceLod0", 1024f);
            SetFloat(seam, programId, "alphaTest", 0.001f);
            SetFloat(seam, programId, "zNear", 0.1f);
            SetFloat(seam, programId, "zFar", 1024f);
            SetFloat(seam, programId, "shadowRangeFar", 1024f);
            SetFloat(seam, programId, "shadowRangeNear", 64f);
            SetFloat(seam, programId, "shadowMapWidthInv", 1f);
            SetFloat(seam, programId, "shadowMapHeightInv", 1f);
            int ambient = seam.GetUniformLocation(programId, "rgbaAmbientIn");
            if (ambient >= 0) seam.SetUniform(programId, ambient, 1f, 1f, 1f);
            int frameSize = seam.GetUniformLocation(programId, "frameSize");
            if (frameSize >= 0) seam.SetUniform(programId, frameSize, (float)Size, (float)Size);
        }

        private static void SetFloat(VulkanDevice seam, int programId, string name, float value)
        {
            int location = seam.GetUniformLocation(programId, name);
            if (location >= 0) seam.SetUniform(programId, location, value);
        }

        /// <summary>
        /// Links one vanilla chunk program from the corpus, as ShaderRegistry does, and points
        /// every sampler it declares at a small gradient through the platform seam - so a
        /// sampling difference between the routes would show as a pixel difference.
        /// </summary>
        private unsafe ShaderProgram LinkClientProgram(VulkanDevice seam, string name,
            ShaderCorpus.ShaderVariant variant)
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
                    PrefixCode = stage.PrefixCode ?? "",
                };
                Assert.True(seam.CompileShader(shader), name + ": " + (seam.GetError() ?? "compile failed"));
                if (stage.Stage == EnumShaderType.VertexShader) linked.VertexShader = shader;
                else if (stage.Stage == EnumShaderType.FragmentShader) linked.FragmentShader = shader;
                else linked.GeometryShader = shader;
            }

            int id = seam.LinkProgram(linked);
            Assert.True(id > 0, name + ": " + (seam.GetError() ?? "link failed"));

            var program = new ShaderProgram { PassName = name, ProgramId = id };
            int unit = 0;
            foreach (string sampler in seam.SamplerNamesOf(id))
            {
                Platform.BindProgramTexture2D(program, sampler, Gradient(seam, unit), unit);
                unit++;
            }
            return program;
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
            return pixels;
        }

        /// <summary>Primary as the Opaque stage has it: scene at 0, glow at 1, motion at 2, plus depth.</summary>
        private static FrameBufferRef CreatePrimary(VulkanDevice seam, int colorCount)
        {
            var textures = new int[colorCount];
            for (int slot = 0; slot < colorCount; slot++)
            {
                textures[slot] = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            }

            var primary = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = textures,
                DepthTextureId = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                    IntPtr.Zero, false),
            };
            Attach(seam, primary);
            seam.SetDrawBuffers(primary.FboId, (1 << colorCount) - 1);
            Assert.True(seam.CheckFramebufferComplete(primary.FboId, out string status), status);
            return primary;
        }

        /// <summary>A shadow cascade's target: one colour attachment and the depth the cascade writes.</summary>
        private static FrameBufferRef CreateShadow(VulkanDevice seam)
        {
            var shadow = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    seam.CreateTexture2D(Size, Size,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                },
                DepthTextureId = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                    IntPtr.Zero, false),
            };
            Attach(seam, shadow);
            seam.SetDrawBuffers(shadow.FboId, 1);
            Assert.True(seam.CheckFramebufferComplete(shadow.FboId, out string status), status);
            return shadow;
        }

        private static void Attach(VulkanDevice seam, FrameBufferRef target)
        {
            for (int slot = 0; slot < target.ColorTextureIds.Length; slot++)
            {
                seam.AttachTexture(target.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    target.ColorTextureIds[slot], 0);
            }
            seam.AttachTexture(target.FboId, EnumFramebufferAttachment.DepthAttachment, target.DepthTextureId, 0);
        }

        private static void InstallFrameBuffers(ChunkPlatform platform, FrameBufferRef primary)
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
        /// One tesselated block face in the layout the chunk tesselator emits, covering the
        /// middle of the target (ChunkTerrainRenderTests.BuildBlockFace).
        /// </summary>
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
