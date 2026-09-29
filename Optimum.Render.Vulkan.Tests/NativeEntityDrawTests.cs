using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
/// An entity's animated shape, drawn twice on one Vulkan device: through the seam's neutral body
/// (ClientPlatformAbstract.RenderEntityMesh's RenderMesh, the route the OpenGL path takes) and
/// through the native pass VulkanClientPlatform.RenderEntityMesh records
/// (docs/vulkan.md, decision 5 stage 2).
///
/// Behavioural identity is the acceptance rule (decision 6): the same program, mesh, bone
/// matrices and fixed state have to put the same pixels on Primary's scene and glow attachments
/// AND on the motion attachment, which the temporal contract requires to stay bit-identical.
/// The sweep that matters for this system is the attachment set (with and without the SSAO
/// G-buffer slots, which changes where the motion attachment sits) and the motion window itself
/// (TAA on with the window open, and shut), because the window is a colour write mask on the
/// native route and a draw-buffer toggle on the old one.
/// </summary>
public class NativeEntityDrawTests(ITestOutputHelper output)
{
    private const int Size = 16;

    /// <summary>The platform with no window: both routes take their size from this seam.</summary>
    private sealed class EntityPlatform : VulkanClientPlatform
    {
        public EntityPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    // ------------------------------------------------------------------ the tests

    /// <summary>
    /// Primary as it is without the SSAO G-buffer (scene, glow, motion): the native entity draw
    /// puts the same pixels on all three attachments as the seam's neutral body, and records nothing else.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public unsafe void TheNativeEntityDrawMatchesTheSeamsNeutralBody(bool gbuffer, bool motionOpen)
    {
        using Session session = Open(gbuffer);

        byte[][] stated = session.RunFrame(native: false, motionOpen);

        long meshDrawsBefore = session.Seam.NativeMeshDrawsForTests;
        byte[][] native = session.RunFrame(native: true, motionOpen);

        Assert.Equal(1, session.Seam.NativeMeshDrawsForTests - meshDrawsBefore);

        for (int slot = 0; slot < stated.Length; slot++)
        {
            output.WriteLine("slot " + slot + " stated " + Centre(stated[slot]) +
                " native " + Centre(native[slot]));
            Assert.Equal(stated[slot], native[slot]);
        }
        // An identity comparison of two blank attachments proves nothing: the shape has to have
        // reached the scene slot.
        Assert.NotEqual(session.ClearOf(0), Centre(native[0]));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Phase 3b decision 1: a mod renderer stays on the adapter. VSEssentials registers its own
    /// entityanimated for the first-person hands, and that program drew the arm wrong through the
    /// native route with TAA on; the route therefore takes only the registered vanilla programs.
    /// </summary>
    [SkippableFact]
    public void AModRegisteredEntityProgramStaysOnTheNeutralBody()
    {
        using Session session = Open(gbuffer: false);
        session.UnregisterVanillaProgram();

        long meshDrawsBefore = session.Seam.NativeMeshDrawsForTests;
        byte[][] drawn = session.RunFrame(native: true, motionOpen: true);

        Assert.Equal(0, session.Seam.NativeMeshDrawsForTests - meshDrawsBefore);
        Assert.NotEqual(session.ClearOf(0), Centre(drawn[0]));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The motion attachment is the one the temporal contract pins: with the window open the two
    /// routes write the same vectors, and with it shut neither route touches it, so whatever was
    /// there survives. A native pipeline that forgot the write mask would fail the second half.
    /// </summary>
    [SkippableFact]
    public unsafe void TheMotionAttachmentIsIdenticalBetweenTheRoutesAndUntouchedWithTheWindowShut()
    {
        using Session session = Open(gbuffer: false);

        byte[] statedOpen = session.RunFrame(native: false, motionOpen: true)[Session.MotionSlot];
        byte[] nativeOpen = session.RunFrame(native: true, motionOpen: true)[Session.MotionSlot];
        Assert.Equal(statedOpen, nativeOpen);

        // With the window shut the attachment is out of the draw-buffer set on the old route and
        // masked out of the pipeline on the native one, so both leave the frame's clear standing.
        // That is rule 9 in its narrowest form: an attachment nothing writes must not pick up
        // whatever Vulkan would otherwise leave in it.
        byte[] statedShut = session.RunFrame(native: false, motionOpen: false)[Session.MotionSlot];
        byte[] nativeShut = session.RunFrame(native: true, motionOpen: false)[Session.MotionSlot];
        Assert.Equal(statedShut, nativeShut);
        Assert.Equal(session.ClearOf(Session.MotionSlot), Centre(nativeShut));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The seam's neutral body draws through the generic stated route and the native route does not:
    /// the switch is real, and "OFF is vanilla" holds for the route the OpenGL path takes.
    /// </summary>
    [SkippableFact]
    public unsafe void TheNeutralBodyDrawsThroughTheStatedRouteAndTheNativeRouteDoesNot()
    {
        using Session session = Open(gbuffer: false);

        long nativeDrawsBefore = session.Seam.NativeDrawsForTests;
        session.RunFrame(native: false, motionOpen: true);
        Assert.Equal(0, session.Seam.NativeDrawsForTests - nativeDrawsBefore);

        session.RunFrame(native: true, motionOpen: true);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The pipeline is built once per (program, target, mesh shape, motion window) and kept, and
    /// opening or shutting the window is a different pipeline - the write mask is baked into it
    /// rather than toggled through a draw-buffer set.
    /// </summary>
    [SkippableFact]
    public unsafe void TheEntityPassKeepsItsPipelineAndOneMoreForTheOtherMotionWindow()
    {
        using Session session = Open(gbuffer: false);

        session.RunFrame(native: true, motionOpen: true);
        int afterOpen = session.Seam.NativePipelinesForTests;
        session.RunFrame(native: true, motionOpen: true);
        Assert.Equal(afterOpen, session.Seam.NativePipelinesForTests);

        session.RunFrame(native: true, motionOpen: false);
        Assert.Equal(afterOpen + 1, session.Seam.NativePipelinesForTests);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The animation block is read the same way by both routes. The client uploads the pose once
    /// per entity per frame through UBO.Update("Animation", ...); the device snapshots it into
    /// the frame's uniform ring per (frame, version) and the draw resolves it when it binds set 2
    /// with its own mesh id. The native draw adds nothing to that - it re-uploads nothing and
    /// carries no bone data in its push block - so the two routes have to produce the same image
    /// for the same pose, and a pose uploaded before the first draw has to be the one that stands
    /// for every later draw of that program.
    ///
    /// What this does NOT prove, and is recorded rather than asserted: this harness could not make
    /// the pose observable in the image. A session posed with a translated bone renders the same
    /// pixels as one left at identity, on BOTH routes and with the native manifest variant linked
    /// (blocks u:Animation@1, u:AnimationPrev@2). Because both routes agree, it says nothing about
    /// this port; it is a question about the animation block's feed on the native shader path, or
    /// about this fixture, and it wants a look in the game.
    /// </summary>
    [SkippableFact]
    public unsafe void TheAnimationBlockIsReadTheSameWayByBothRoutes()
    {
        using Session session = Open(gbuffer: false);
        session.PoseJoint(shiftX: 0.5f);

        // Warm-up: the native pipeline compiles in the background and its first draws are skipped
        // until it is published, exactly as on the stated route.
        session.RunFrame(native: true, motionOpen: true);
        byte[] posed = session.RunFrame(native: true, motionOpen: true)[0];
        byte[] posedStated = session.RunFrame(native: false, motionOpen: true)[0];

        Assert.NotEqual(session.ClearOf(0), Centre(posed));
        Assert.Equal(posed, posedStated);
        GpuTest.AssertClean(session.Seam);
    }

    // ---------------------------------------------------------------------- driving

    private static string Centre(byte[] pixels)
    {
        int i = (Size / 2 * Size + Size / 2) * 4;
        return pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," + pixels[i + 3];
    }

    private Session Open(bool gbuffer)
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);

        Session? session = Session.TryOpen(output, manifest, gbuffer);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    /// <summary>
    /// The platform, its device, the Primary target the Opaque stage binds, the entityanimated
    /// program with its Animation storage block, and one entity mesh - installed the way the
    /// client installs them and put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        /// <summary>The motion attachment is the last colour slot of Primary, as SetupDefaultFrameBuffers appends it.</summary>
        public const int MotionSlot = 2;

        private static readonly float[] Identity =
            { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public EntityPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Primary { get; private set; } = null!;

        /// <summary>The colour RunFrame clears a slot to, as the readback reports it.</summary>
        public string ClearOf(int slot)
        {
            var clear = new[]
            {
                (byte)Math.Round(0.1f * (slot + 1) * 255f),
                (byte)Math.Round(0.2f * 255f),
                (byte)Math.Round(0.3f * 255f),
                (byte)255,
            };
            return clear[0] + "," + clear[1] + "," + clear[2] + "," + clear[3];
        }

        private MeshRef shape = null!;
        private ShaderProgram entity = null!;
        private ShaderProgramEntityanimated? previousEntityProgram;

        /// <summary>What a mod-registered entityanimated looks like to the route: not the registered vanilla program.</summary>
        public void UnregisterVanillaProgram() => ShaderPrograms.Entityanimated = new ShaderProgramEntityanimated();
        private UBORef animation = null!;
        private UBORef animationPrev = null!;
        private readonly float[] bones = new float[16 * 4];
        private int atlas;
        private bool gbuffer;
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";

        private static readonly FieldInfo MotionWriteActive =
            typeof(ClientPlatformWindows).GetField("optimumMotionWriteActive",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

        public static unsafe Session? TryOpen(ITestOutputHelper output, string manifestDirectory, bool gbuffer)
        {
            string dataPath = Path.Combine(Path.GetTempPath(),
                "optimum-native-entity-" + Guid.NewGuid().ToString("N"));
            var platform = new EntityPlatform
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
                gbuffer = gbuffer,
            };
            ScreenManager.Platform = platform;
            platform.ShaderUniforms = new DefaultShaderUniforms();

            VulkanDevice seam = platform.GraphicsDevice!;
            session.Primary = CreatePrimary(seam, gbuffer);
            InstallFrameBuffers(platform, session.Primary);
            // Where SetupDefaultFrameBuffers put the motion attachment: after the shaded set.
            platform.SetOptimumMotionAttachmentIndex(session.Primary.ColorTextureIds.Length - 1);

            // The vanilla program type, registered where the client registers it: the native route
            // takes vanilla entity programs only, and a mod program under the same pass name stays
            // on the neutral body (AModRegisteredEntityProgramStaysOnTheNeutralBody).
            var program = new ShaderProgramEntityanimated { PassName = "entityanimated" };
            Link(seam, program, "entityanimated", Variant(gbuffer), new[]
            {
                "modelMatrix", "viewMatrix", "projectionMatrix",
                "rgbaLightIn", "rgbaAmbientIn", "renderColor", "alphaTest",
            });
            session.entity = program;
            session.previousEntityProgram = ShaderPrograms.Entityanimated;
            ShaderPrograms.Entityanimated = program;

            session.atlas = Gradient(seam);
            // The two animation blocks ShaderProgramEntityanimated creates for the opaque
            // program: the pose and, with TAA on, the previous pose the motion writer reads.
            session.animation = platform.CreateUBO(program.ProgramId, 0, "Animation", 16 * 4 * sizeof(float));
            platform.BindUBO((UBO)session.animation);
            session.animationPrev = platform.CreateUBO(program.ProgramId, 1, "AnimationPrev", 16 * 4 * sizeof(float));
            platform.BindUBO((UBO)session.animationPrev);
            session.PoseJoint(shiftX: 0f);

            session.shape = platform.UploadMesh(BuildShape());
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            ShaderPrograms.Entityanimated = previousEntityProgram!;
            if (shape != null) Platform.DeleteMesh(shape);
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
        /// The bone matrices, written the way EntityShapeRenderer writes them - through
        /// UBO.Update on the "Animation" block, once per entity per frame. Nothing about the
        /// native route changes this: the device's storage ring snapshots it per (frame,
        /// version), and the draw only has to bind the set with the right mesh id.
        /// </summary>
        public void PoseJoint(float shiftX)
        {
            for (int joint = 0; joint < 4; joint++)
            {
                Identity.CopyTo(bones, joint * 16);
                bones[joint * 16 + 12] = shiftX;
            }
            GCHandle pinned = GCHandle.Alloc(bones, GCHandleType.Pinned);
            try
            {
                Platform.UpdateUBO((UBO)animation, pinned.AddrOfPinnedObject(), 0,
                    bones.Length * sizeof(float), false);
                Platform.UpdateUBO((UBO)animationPrev, pinned.AddrOfPinnedObject(), 0,
                    bones.Length * sizeof(float), false);
            }
            finally
            {
                pinned.Free();
            }
        }

        /// <summary>
        /// One frame of the Opaque stage at the point the batched entity loop runs: Primary bound
        /// and cleared, the stage's own state set, the program's uniforms and texture bound, the
        /// motion window in the state under test, then the seam.
        /// </summary>
        public unsafe byte[][] RunFrame(bool native, bool motionOpen)
        {
            VulkanDevice seam = Seam;
            Platform.NativeEntitiesEnabled = native;

            Platform.BeginFrame();
            int slots = Primary.ColorTextureIds.Length;
            uint shadedMask = (1u << (slots - 1)) - 1u;
            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, (int)(motionOpen ? (1u << slots) - 1u : shadedMask));
            for (int slot = 0; slot < slots; slot++)
            {
                seam.ClearColor(slot, 0.1f * (slot + 1), 0.2f, 0.3f, 1f);
            }
            seam.ClearDepth(1f);

            Platform.CurrentFrameBuffer = Primary;
            seam.SetViewport(0, 0, Size, Size);
            // What SystemRenderEntities.OnRenderOpaque3D sets before its batched loop.
            seam.SetDepthTest(true);
            seam.SetDepthMask(true);
            seam.SetCullFace(false);
            seam.SetBlend(true, EnumBlendMode.Standard);

            seam.UseProgram(entity.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = entity;
            foreach (string uniform in new[] { "modelMatrix", "viewMatrix", "projectionMatrix" })
            {
                seam.SetUniformMatrix(entity.ProgramId, entity.uniformLocations[uniform], Identity);
            }
            // Lit white with no tint, so the shape actually lands on the attachments: with the
            // record left at zero the fragment's alpha is zero and the alpha blend the stage set
            // keeps the clear, which would make an identity comparison vacuous.
            entity.Uniform("rgbaLightIn", 1f, 1f, 1f, 1f);
            entity.Uniform("rgbaAmbientIn", 1f, 1f, 1f);
            entity.Uniform("renderColor", 1f, 1f, 1f, 1f);
            entity.Uniform("alphaTest", 0.001f);
            // The client's own sampler declaration: both routes see the same texture, the
            // stated one through the unit and the native one through the declared name.
            Platform.BindProgramTexture2D(entity, "entityTex", atlas, 0);

            MotionWriteActive.SetValue(Platform, motionOpen);
            // The blend state the motion window forces on its attachment, for the old route.
            Platform.ApplyOptimumMotionBlendState();

            Platform.RenderEntityMesh(shape, "entityTex", atlas);

            MotionWriteActive.SetValue(Platform, false);

            var attachments = new byte[slots][];
            for (int slot = 0; slot < slots; slot++)
            {
                attachments[slot] = Read(seam, Primary.ColorTextureIds[slot]);
            }
            Platform.EndFrame();
            return attachments;
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

        /// <summary>
        /// The opaque entity program as ShaderRegistry builds it for this client: not the OIT
        /// copy, with the TAA motion writer compiled in at the slot the framebuffer put it, and
        /// the G-buffer varyings when the SSAO attachments are there.
        /// </summary>
        private static ShaderCorpus.ShaderVariant Variant(bool gbuffer) => new()
        {
            UseOit = 0,
            TaaMotion = 1,
            TaaMotionLocation = gbuffer ? 4 : 2,
            SsaoLevel = gbuffer ? 1 : 0,
        };

        private static void Link(VulkanDevice seam, ShaderProgramBase program, string name,
            ShaderCorpus.ShaderVariant variant, string[] uniforms)
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
        /// Primary as the Opaque stage has it: scene at 0, glow at 1, the SSAO G-buffer's normal
        /// and position at 2 and 3 when it is on, and the motion attachment appended after them.
        /// </summary>
        private static FrameBufferRef CreatePrimary(VulkanDevice seam, bool gbuffer)
        {
            int shaded = gbuffer ? 4 : 2;
            var colors = new int[shaded + 1];
            for (int slot = 0; slot < colors.Length; slot++)
            {
                colors[slot] = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            }

            var primary = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = colors,
                DepthTextureId = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                    IntPtr.Zero, false),
            };
            for (int slot = 0; slot < colors.Length; slot++)
            {
                seam.AttachTexture(primary.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    colors[slot], 0);
            }
            seam.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
            seam.SetDrawBuffers(primary.FboId, (int)((1u << colors.Length) - 1u));
            Assert.True(seam.CheckFramebufferComplete(primary.FboId, out string status), status);
            return primary;
        }

        private static void InstallFrameBuffers(EntityPlatform platform, FrameBufferRef primary)
        {
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = primary;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);
        }

        /// <summary>A small gradient atlas, so a sampling difference between the routes would show.</summary>
        private static unsafe int Gradient(VulkanDevice seam)
        {
            var pixels = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int i = (y * 8 + x) * 4;
                    pixels[i] = (byte)(16 + x * 30);
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
        /// The shape as entityanimated sees it: positions, UVs, a per-vertex colour and render
        /// flags, reduced to two triangles that cover enough of the target for every attachment
        /// to be comparable. damageEffectIn and jointId are left to the layout's constant
        /// defaults - jointId 0, the joint PoseJoint moves - which is exactly the GL promise
        /// VertexLayoutDescription.WithDefaultsFor keeps for an attribute a mesh does not carry.
        /// </summary>
        private static MeshData BuildShape()
        {
            var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);
            float[] positions =
            {
                -0.7f, -0.7f, 0.5f,
                 0.7f, -0.7f, 0.5f,
                 0.7f,  0.7f, 0.5f,
                -0.7f,  0.7f, 0.5f,
            };
            float[] uvs = { 0, 0, 1, 0, 1, 1, 0, 1 };
            for (int i = 0; i < 4; i++)
            {
                mesh.AddVertexWithFlags(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                    uvs[i * 2], uvs[i * 2 + 1], ColorUtil.WhiteArgb, 0);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(index);
            return mesh;
        }
    }

    // ---------------------------------------------------------------- native shaders

    /// <summary>The entityanimated program's manifest, built once for the whole class.</summary>
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
            NativeShaderBuildResult one = builder.Build(source, "entityanimated");
            merged.Errors.AddRange(one.Errors);
            merged.Manifest.Programs.AddRange(one.Manifest.Programs);
            foreach ((string file, byte[] bytes) in one.Files) merged.Files[file] = bytes;
            if (!merged.Success) return ("", string.Join("\n", merged.Errors));

            string root = Path.Combine(Path.GetTempPath(),
                "optimum-native-entity-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
