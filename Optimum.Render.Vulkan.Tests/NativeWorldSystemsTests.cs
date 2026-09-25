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
/// The world systems Phase 3b stage 2 moved onto the native device API after the sky dome - the
/// night sky box, the moon, the cube particle pool and the decal pool - each drawn twice on one
/// Vulkan device: through its seam's neutral body (the OpenGL body's own draw, the route every
/// system that has not moved still takes) and through the native pass the Vulkan platform
/// records (docs/vulkan.md, decision 5 stage 2).
///
/// Behavioural identity is the acceptance rule (decision 6): the same program, the same mesh and
/// the same fixed state have to put the same pixels on every attachment of Primary - the motion
/// attachment included, bit for bit, because a world pass that writes motion has to land exactly
/// what the GL path lands there - and the native route must not touch the GL state tracker, a
/// texture unit or a draw-buffer mask while its pass is open.
///
/// The sky dome itself is pinned by NativeSkyTests and the device's mesh-draw entry points by
/// NativeMeshDrawTests; those files are not duplicated here.
/// </summary>
public class NativeWorldSystemsTests(ITestOutputHelper output)
{
    private const int Size = 16;

    /// <summary>Primary's colour slots in this fixture: scene, glow, then the motion attachment.</summary>
    private const int SceneSlot = 0;
    private const int GlowSlot = 1;
    private const int MotionSlot = 2;

    /// <summary>The platform with no window: both routes take their size from this seam.</summary>
    private sealed class WorldPlatform : VulkanClientPlatform
    {
        public WorldPlatform() : base(null!)
        {
        }

        public override Size2i OptimumWindowClientSize() => new(Size, Size);
    }

    // ------------------------------------------------------------------------- night sky

    /// <summary>
    /// The star box's native pass draws what its seam's neutral body draws: one declared pass,
    /// one native mesh draw of the cube through the samplerCube the program declares, and the same pixels on every attachment.
    /// </summary>
    [SkippableFact]
    public void TheNativeNightSkyPassMatchesTheSeamsNeutralBody()
    {
        using Session session = Open("nightsky");
        int cube = session.CubeGradient();

        byte[][] stated = session.RunFrame(native: false, blending: false, depth: false, motion: false,
            s => s.Platform.RenderNightSkyBox(s.Mesh, cube));

        long passes = session.Seam.NativePassesForTests;
        long meshes = session.Seam.NativeMeshDrawsForTests;
        byte[][] native = session.RunFrame(native: true, blending: false, depth: false, motion: false,
            s => s.Platform.RenderNightSkyBox(s.Mesh, cube));

        Assert.Equal(1, session.Seam.NativePassesForTests - passes);
        Assert.Equal(1, session.Seam.NativeMeshDrawsForTests - meshes);
        AssertSameAttachments(stated, native, "nightsky");
        GpuTest.AssertClean(session.Seam);
    }

    // -------------------------------------------------------------------------- celestial

    /// <summary>
    /// The moon's native pass matches its seam's neutral body, with the body texture resolved
    /// from its handle and the sky and glow frame textures resolved from theirs rather than from
    /// the units ShaderProgramCelestialobject's setters bound them to.
    /// </summary>
    [SkippableFact]
    public void TheNativeCelestialPassMatchesTheSeamsNeutralBody()
    {
        using Session session = Open("celestialobject");
        int body = session.Gradient(0);
        int sky = session.Gradient(1);
        int glow = session.Gradient(2);

        byte[][] stated = session.RunFrame(native: false, blending: true, depth: false, motion: false,
            s => s.Platform.RenderCelestialQuad(s.Mesh, body, sky, glow));

        long meshes = session.Seam.NativeMeshDrawsForTests;
        byte[][] native = session.RunFrame(native: true, blending: true, depth: false, motion: false,
            s => s.Platform.RenderCelestialQuad(s.Mesh, body, sky, glow));

        Assert.Equal(1, session.Seam.NativeMeshDrawsForTests - meshes);
        AssertSameAttachments(stated, native, "celestialobject");
        GpuTest.AssertClean(session.Seam);
    }

    // -------------------------------------------------------------------------- sun

    /// <summary>
    /// The sun's native pass matches its seam's neutral body: standard's samplers resolved from the
    /// pipeline's own declaration, the sun texture from its handle, blended with no depth test.
    /// </summary>
    [SkippableFact]
    public void TheSunMatchesTheSeamsNeutralBody()
    {
        using Session session = Open("standard");
        int sun = session.Gradient(0);

        void Draw(Session s)
        {
            // What SystemRenderSunMoon writes, reduced to what makes the quad land: lit white,
            // untinted, identity transforms, a low alpha test.
            float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            s.Program.UniformMatrix("modelMatrix", identity);
            s.Program.UniformMatrix("viewMatrix", identity);
            s.Program.Uniform("rgbaTint", 1f, 1f, 1f, 1f);
            s.Program.Uniform("rgbaLightIn", 1f, 1f, 1f, 1f);
            s.Program.Uniform("rgbaAmbientIn", 1f, 1f, 1f);
            s.Program.Uniform("alphaTest", 0.01f);
            // The fixture's frame block is zero, and the global warp reads it; the route under
            // test does not depend on the warp, so the fixture skips it.
            s.Program.Uniform("dontWarpVertices", 1);
            s.Platform.BindProgramTexture2D(s.Program, "tex", sun, 0);
            s.Platform.RenderSunQuad(s.Mesh, sun);
        }

        byte[][] stated = session.RunFrame(native: false, blending: true, depth: false, motion: false, Draw);

        long meshes = session.Seam.NativeMeshDrawsForTests;
        byte[][] native = session.RunFrame(native: true, blending: true, depth: false, motion: false, Draw);

        Assert.Equal(1, session.Seam.NativeMeshDrawsForTests - meshes);
        AssertSameAttachments(stated, native, "standard");
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>A mod program registered under "standard" is not the vanilla one and stays on the neutral body.</summary>
    [SkippableFact]
    public void AModStandardProgramStaysOnTheNeutralBody()
    {
        using Session session = Open("standard");
        int sun = session.Gradient(0);
        ShaderProgramStandard registered = ShaderPrograms.Standard;
        ShaderPrograms.Standard = new ShaderProgramStandard();
        try
        {
            long meshes = session.Seam.NativeMeshDrawsForTests;
            session.RunFrame(native: true, blending: true, depth: false, motion: false,
                s => s.Platform.RenderSunQuad(s.Mesh, sun));
            Assert.Equal(0, session.Seam.NativeMeshDrawsForTests - meshes);
        }
        finally
        {
            ShaderPrograms.Standard = registered;
        }
        GpuTest.AssertClean(session.Seam);
    }

    // -------------------------------------------------------------------------- particles

    /// <summary>
    /// The cube pool's native pass matches its seam's neutral body, and the draw is recorded as
    /// an instanced draw rather than as as many single draws.
    /// Known gap: the fixture's quad carries no per-instance attributes, so the cubes do not land
    /// on the scene slot and the pixel comparison is between two untouched attachments. What this
    /// pins is the route and the draw kind, not the pixels.
    /// </summary>
    [SkippableFact]
    public void TheNativeParticlePassMatchesTheSeamsNeutralBody()
    {
        using Session session = Open("particlescube");

        byte[][] stated = session.RunFrame(native: false, blending: true, depth: true, motion: false,
            s => s.Platform.RenderParticles(s.Mesh, 4, 0));

        long instanced = session.Seam.NativeInstancedDrawsForTests;
        byte[][] native = session.RunFrame(native: true, blending: true, depth: true, motion: false,
            s => s.Platform.RenderParticles(s.Mesh, 4, 0));

        Assert.Equal(1, session.Seam.NativeInstancedDrawsForTests - instanced);
        AssertSameAttachments(stated, native, "particlescube", mustDraw: false);
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Inside a motion window the cube pool's native pass takes the motion attachment into its
    /// colour slots, and every attachment - the motion one bit for bit - comes out of the native
    /// route exactly as it comes out of the neutral body's draw under the same window. This is
    /// the temporal contract: a world pass that writes motion lands what the GL path lands.
    /// </summary>
    [SkippableFact]
    public void TheNativeParticlePassLeavesTheMotionAttachmentIdentical()
    {
        using Session session = Open("particlescube");
        session.OpenMotionWindow();

        byte[][] stated = session.RunFrame(native: false, blending: true, depth: true, motion: true,
            s => s.Platform.RenderParticles(s.Mesh, 4, 0));
        byte[][] native = session.RunFrame(native: true, blending: true, depth: true, motion: true,
            s => s.Platform.RenderParticles(s.Mesh, 4, 0));

        Assert.Equal(stated[MotionSlot], native[MotionSlot]);
        AssertSameAttachments(stated, native, "particlescube (motion window)", mustDraw: false);
        GpuTest.AssertClean(session.Seam);
    }

    // ----------------------------------------------------------------------------- decals

    /// <summary>
    /// The decal pool's native pass matches its seam's neutral body, and the draw is recorded as
    /// one indirect multi-draw out of the per-slot indirect ring rather than one draw per group.
    /// </summary>
    [SkippableFact]
    public void TheNativeDecalPassMatchesTheSeamsNeutralBody()
    {
        using Session session = Open("decals");
        int decal = session.Gradient(0);
        int block = session.Gradient(1);
        int[] starts = { 0, 0, 3 * 4, 0 };
        int[] sizes = { 3, 3 };

        byte[][] stated = session.RunFrame(native: false, blending: true, depth: true, motion: false,
            s =>
            {
                // The lib's route: the scope opens, vanilla MeshDataPool.Draw's own RenderMesh
                // multi-draw runs inside it, the scope closes.
                // What ShaderProgramDecals' setters do before the scope: the atlases on units.
                s.Platform.BindProgramTexture2D(s.Program, "blockTexture", block, 0);
                s.Platform.BindProgramTexture2D(s.Program, "decalTexture", decal, 1);
                s.Platform.BeginDecalPass(decal, block);
                try
                {
                    s.Platform.RenderMesh(s.Mesh, starts, sizes, 2, false);
                }
                finally
                {
                    s.Platform.EndDecalPass();
                }
            });

        long indirect = session.Seam.NativeIndirectDrawsForTests;
        byte[][] native = session.RunFrame(native: true, blending: true, depth: true, motion: false,
            s =>
            {
                // The lib's route: the scope opens, vanilla MeshDataPool.Draw's own RenderMesh
                // multi-draw runs inside it, the scope closes.
                // What ShaderProgramDecals' setters do before the scope: the atlases on units.
                s.Platform.BindProgramTexture2D(s.Program, "blockTexture", block, 0);
                s.Platform.BindProgramTexture2D(s.Program, "decalTexture", decal, 1);
                s.Platform.BeginDecalPass(decal, block);
                try
                {
                    s.Platform.RenderMesh(s.Mesh, starts, sizes, 2, false);
                }
                finally
                {
                    s.Platform.EndDecalPass();
                }
            });

        Assert.Equal(1, session.Seam.NativeIndirectDrawsForTests - indirect);
        AssertSameAttachments(stated, native, "decals");
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Inside a motion window the decal pass's motion attachment is identical between the two
    /// routes, for the same reason the particle pass's is: a decal writes the motion vector of
    /// the surface it sits on, with its own nudged depth.
    /// </summary>
    [SkippableFact]
    public void TheNativeDecalPassLeavesTheMotionAttachmentIdentical()
    {
        using Session session = Open("decals");
        session.OpenMotionWindow();
        int decal = session.Gradient(0);
        int block = session.Gradient(1);
        int[] starts = { 0, 0, 3 * 4, 0 };
        int[] sizes = { 3, 3 };

        byte[][] stated = session.RunFrame(native: false, blending: true, depth: true, motion: true,
            s =>
            {
                // The lib's route: the scope opens, vanilla MeshDataPool.Draw's own RenderMesh
                // multi-draw runs inside it, the scope closes.
                // What ShaderProgramDecals' setters do before the scope: the atlases on units.
                s.Platform.BindProgramTexture2D(s.Program, "blockTexture", block, 0);
                s.Platform.BindProgramTexture2D(s.Program, "decalTexture", decal, 1);
                s.Platform.BeginDecalPass(decal, block);
                try
                {
                    s.Platform.RenderMesh(s.Mesh, starts, sizes, 2, false);
                }
                finally
                {
                    s.Platform.EndDecalPass();
                }
            });
        byte[][] native = session.RunFrame(native: true, blending: true, depth: true, motion: true,
            s =>
            {
                // The lib's route: the scope opens, vanilla MeshDataPool.Draw's own RenderMesh
                // multi-draw runs inside it, the scope closes.
                // What ShaderProgramDecals' setters do before the scope: the atlases on units.
                s.Platform.BindProgramTexture2D(s.Program, "blockTexture", block, 0);
                s.Platform.BindProgramTexture2D(s.Program, "decalTexture", decal, 1);
                s.Platform.BeginDecalPass(decal, block);
                try
                {
                    s.Platform.RenderMesh(s.Mesh, starts, sizes, 2, false);
                }
                finally
                {
                    s.Platform.EndDecalPass();
                }
            });

        Assert.Equal(stated[MotionSlot], native[MotionSlot]);
        AssertSameAttachments(stated, native, "decals (motion window)");
        GpuTest.AssertClean(session.Seam);
    }

    // ------------------------------------------------------------------- switch and slots

    /// <summary>
    /// The neutral body draws through the generic stated route and the native route does not: the
    /// switch is real, and "OFF is vanilla" holds for the route the OpenGL path takes.
    /// </summary>
    [SkippableFact]
    public void TheNeutralBodiesDrawThroughTheStatedRouteAndTheNativeRouteDoesNot()
    {
        using Session session = Open("particlescube");

        long nativeBefore = session.Seam.NativeDrawsForTests;
        long statedBefore = session.Platform.StatedDrawsForTests;
        session.RunFrame(native: false, blending: true, depth: true, motion: false,
            s => s.Platform.RenderParticles(s.Mesh, 2, 0));
        Assert.Equal(0, session.Seam.NativeDrawsForTests - nativeBefore);
        Assert.True(session.Platform.StatedDrawsForTests - statedBefore > 0);

        session.RunFrame(native: true, blending: true, depth: true, motion: false,
            s => s.Platform.RenderParticles(s.Mesh, 2, 0));
        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The colour slots a native world pass declares are the set the stated route's
    /// draw-buffer mask holds at the same point in the frame, derived from the platform's own
    /// motion-window state: Primary's default colour set, plus the motion attachment exactly
    /// while a window is open, and every bound slot with TAA off.
    /// </summary>
    [SkippableFact]
    public void TheDeclaredColourSlotsAreTheOnesTheStatedMaskWouldHold()
    {
        using Session session = Open("particlescube");
        MethodInfo slots = typeof(VulkanClientPlatform).GetMethod("NativeWorldPassColorSlots",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // TAA off: the attachment index is negative and the pass takes every bound slot.
        session.Platform.SetOptimumMotionAttachmentIndex(-1);
        Assert.Equal(0b111u, (uint)slots.Invoke(session.Platform, new object[] { session.Primary })!);

        // TAA on, window closed: Primary's default colour set, the motion attachment out.
        session.Platform.SetOptimumMotionAttachmentIndex(MotionSlot);
        SetMotionWriteActive(session.Platform, false);
        Assert.Equal(0b011u, (uint)slots.Invoke(session.Platform, new object[] { session.Primary })!);

        // TAA on, window open: the motion attachment joins the set.
        SetMotionWriteActive(session.Platform, true);
        Assert.Equal(0b111u, (uint)slots.Invoke(session.Platform, new object[] { session.Primary })!);
    }

    // ---------------------------------------------------------------------------- helpers

    /// <summary>The scene slot's centre as RunFrame clears it (0.125, 0.25, 0.5).</summary>
    private const string ClearedSceneCentre = "32,64,127,255";

    private void AssertSameAttachments(byte[][] stated, byte[][] native, string what, bool mustDraw = true)
    {
        for (int slot = 0; slot < stated.Length; slot++)
        {
            output.WriteLine(what + " slot " + slot + " centre stated " + Centre(stated[slot]) +
                " native " + Centre(native[slot]));
            Assert.Equal(stated[slot], native[slot]);
        }
        // Two untouched attachments are equal too. Until the fixture seeded the frame block and
        // the transforms, every comparison in this file was exactly that.
        if (mustDraw) Assert.NotEqual(ClearedSceneCentre, Centre(native[SceneSlot]));
    }

    private static string Centre(byte[] pixels)
    {
        int i = (Size / 2 * Size + Size / 2) * 4;
        return pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," + pixels[i + 3];
    }

    /// <summary>
    /// The window flag ClientPlatformWindows keeps private. The tests set it directly rather
    /// than through BeginMotionWrite, which also wants a temporal frame, a jitter window and the
    /// TAA targets - none of which change what is under test here.
    /// </summary>
    private static void SetMotionWriteActive(VulkanClientPlatform platform, bool active) =>
        typeof(ClientPlatformWindows)
            .GetField("optimumMotionWriteActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(platform, active);

    private Session Open(string program)
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);

        Session? session = Session.TryOpen(output, manifest, program);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    // ---------------------------------------------------------------------------- driving

    /// <summary>
    /// The platform, its device, the Primary target its stage binds (scene, glow and the motion
    /// attachment), one vanilla program and one mesh, installed the way the client installs them
    /// and put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private static readonly float[] Identity =
            { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public WorldPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Primary { get; private set; } = null!;
        public MeshRef Mesh { get; private set; } = null!;

        private ShaderProgram program = null!;
        private ClientPlatformAbstract? previousPlatform;
        private ShaderProgramStandard? previousStandard;
        private bool registeredStandard;

        /// <summary>The linked program, for a test that sets uniforms of its own.</summary>
        public ShaderProgram Program => program;
        private string dataPath = "";
        private int gradients;

        public static unsafe Session? TryOpen(ITestOutputHelper output, string manifestDirectory, string programName)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-world-" + Guid.NewGuid().ToString("N"));
            var platform = new WorldPlatform
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
            // TAA on with the motion attachment appended after Primary's default colour set, so
            // the window-derived slot masks under test mean something.
            platform.SetOptimumMotionAttachmentIndex(MotionSlot);

            // standard is the one vanilla program the native route checks by identity (a mod can
            // register its own under the same name), so it is built as the vanilla type and
            // registered where the client registers it.
            bool standard = programName == "standard";
            ShaderProgram linked = standard
                ? new ShaderProgramStandard { PassName = programName }
                : new ShaderProgram { PassName = programName };
            Link(seam, linked, programName, standard
                ? new[] { "projectionMatrix", "modelMatrix", "viewMatrix", "rgbaTint", "rgbaLightIn", "rgbaAmbientIn", "alphaTest", "dontWarpVertices" }
                : new[] { "projectionMatrix" });
            session.program = linked;
            if (standard)
            {
                session.previousStandard = ShaderPrograms.Standard;
                ShaderPrograms.Standard = (ShaderProgramStandard)linked;
                session.registeredStandard = true;
            }
            session.Mesh = platform.UploadMesh(BuildQuad());
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            if (registeredStandard) ShaderPrograms.Standard = previousStandard!;
            // The mesh goes first: VAO's finalizer reaches for ScreenManager.Platform, which is
            // about to be the client's again, and a live handle there would crash the test host.
            if (Mesh != null) Platform.DeleteMesh(Mesh);
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

        /// <summary>Opens the caller's motion window, the way BeginMotionWrite leaves the platform.</summary>
        public void OpenMotionWindow() => SetMotionWriteActive(Platform, true);

        /// <summary>
        /// One frame at the point the system under test runs: Primary bound and cleared, the
        /// draw-buffer mask the motion window would have left, the caller's blend, depth and
        /// cull state, the program in use, then the seam.
        /// </summary>
        public unsafe byte[][] RunFrame(bool native, bool blending, bool depth, bool motion, Action<Session> draw)
        {
            VulkanDevice seam = Seam;
            Platform.NativeWorldEnabled = native;
            uint mask = motion ? 0b111u : 0b011u;

            Platform.BeginFrame();
            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, 0b111);
            seam.ClearColor(SceneSlot, 0.125f, 0.25f, 0.5f, 1f);
            seam.ClearColor(GlowSlot, 0.75f, 0.5f, 0.25f, 1f);
            seam.ClearColor(MotionSlot, 0.375f, 0.625f, 0.875f, 1f);
            seam.ClearDepth(1f);
            seam.SetDrawBuffers(Primary.FboId, (int)mask);

            Platform.CurrentFrameBuffer = Primary;
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(depth);
            seam.SetDepthMask(depth);
            seam.SetCullFace(false);
            seam.SetBlend(blending, EnumBlendMode.Standard);
            // The replace-blending the window forces on the motion attachment, which the native
            // pass states per attachment instead.
            if (motion) Platform.ApplyOptimumMotionBlendState();

            seam.UseProgram(program.ProgramId);
            ShaderProgramBase.CurrentShaderProgram = program;
            seam.SetUniformMatrix(program.ProgramId, program.uniformLocations["projectionMatrix"], Identity);
            SeedFrameGlobals(seam, program.ProgramId);
            SeedDrawUniforms(seam, program.ProgramId);

            draw(this);

            var pixels = new byte[Primary.ColorTextureIds.Length][];
            for (int slot = 0; slot < pixels.Length; slot++) pixels[slot] = Read(seam, Primary.ColorTextureIds[slot]);
            Platform.EndFrame();
            return pixels;
        }

        /// <summary>
        /// The frame globals ShaderProgramBase.Use() would have written. RunFrame binds the program
        /// directly, so without these the shared frame block stays zero - and a zero viewDistance
        /// makes standard.vsh's distance fade a division by zero that discards every fragment, which
        /// is how these comparisons were once equal without anything having been drawn.
        /// </summary>
        private static void SeedFrameGlobals(VulkanDevice seam, int programId)
        {
            foreach ((string name, float value) in new[]
            {
                ("zNear", 0.1f), ("zFar", 1000f), ("viewDistance", 1000f), ("viewDistanceLod0", 1000f),
            })
            {
                int location = seam.GetUniformLocation(programId, name);
                if (location != -1) seam.SetUniform(programId, location, value);
            }
        }

        /// <summary>
        /// Identity transforms and white light for whichever of them the program declares, so the
        /// quad lands on the scene slot. Left at zero, the matrices collapse every vertex to one
        /// point and the comparison is between two untouched attachments.
        /// </summary>
        private static void SeedDrawUniforms(VulkanDevice seam, int programId)
        {
            foreach (string matrix in new[] { "modelMatrix", "viewMatrix", "modelViewMatrix" })
            {
                int location = seam.GetUniformLocation(programId, matrix);
                if (location != -1) seam.SetUniformMatrix(programId, location, Identity);
            }
            foreach (string colour in new[] { "rgbaAmbientIn", "rgbaLightIn", "rgbaTint" })
            {
                int location = seam.GetUniformLocation(programId, colour);
                if (location != -1) seam.SetUniform(programId, location, 1f, 1f, 1f, 1f);
            }
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

        /// <summary>A small 2D gradient, so a sampling difference between the routes would show.</summary>
        public unsafe int Gradient(int phase)
        {
            gradients++;
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
                return Seam.CreateTexture2D(8, 8,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)first, false);
            }
        }

        /// <summary>
        /// The star cube map: six faces, one gradient each, uploaded the way
        /// ClientPlatformWindows.Load3DTextureCube uploads SystemRenderNightSky's stars. The
        /// native pass has to resolve it into the bindless table's cube array, not the 2D one.
        /// </summary>
        public unsafe int CubeGradient()
        {
            const int face = 8;
            var faces = new byte[6][];
            var pointers = new IntPtr[6];
            var handles = new System.Runtime.InteropServices.GCHandle[6];
            for (int f = 0; f < 6; f++)
            {
                faces[f] = new byte[face * face * 4];
                for (int y = 0; y < face; y++)
                {
                    for (int x = 0; x < face; x++)
                    {
                        int i = (y * face + x) * 4;
                        faces[f][i] = (byte)(f * 40);
                        faces[f][i + 1] = (byte)(x * 30);
                        faces[f][i + 2] = (byte)(y * 30);
                        faces[f][i + 3] = 255;
                    }
                }
                handles[f] = System.Runtime.InteropServices.GCHandle.Alloc(
                    faces[f], System.Runtime.InteropServices.GCHandleType.Pinned);
                pointers[f] = handles[f].AddrOfPinnedObject();
            }

            try
            {
                return Seam.CreateTextureCube(face, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, pointers);
            }
            finally
            {
                for (int f = 0; f < 6; f++) handles[f].Free();
            }
        }

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

        /// <summary>Primary as a world stage has it: scene at 0, glow at 1, motion at 2, plus depth.</summary>
        private static FrameBufferRef CreatePrimary(VulkanDevice seam)
        {
            var primary = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new int[3],
                DepthTextureId = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                    IntPtr.Zero, false),
            };
            for (int slot = 0; slot < primary.ColorTextureIds.Length; slot++)
            {
                primary.ColorTextureIds[slot] = seam.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                seam.AttachTexture(primary.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    primary.ColorTextureIds[slot], 0);
            }
            seam.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
            seam.SetDrawBuffers(primary.FboId, 0b111);
            Assert.True(seam.CheckFramebufferComplete(primary.FboId, out string status), status);
            return primary;
        }

        private static void InstallFrameBuffers(WorldPlatform platform, FrameBufferRef primary)
        {
            var list = new List<FrameBufferRef>();
            for (int i = 0; i <= 24; i++) list.Add(null!);
            list[0] = primary;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);
        }

        /// <summary>
        /// Two triangles covering the target, with positions, UVs, a colour and flags - enough
        /// for every program under test, whose remaining vertex inputs the layout fills with the
        /// constant defaults GL promises. Six indices, so a multi-draw can take them as two
        /// groups of three.
        /// </summary>
        private static MeshData BuildQuad()
        {
            var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);
            float[] positions =
            {
                -0.9f, -0.9f, 0.5f,
                 0.9f, -0.9f, 0.5f,
                 0.9f,  0.9f, 0.5f,
                -0.9f,  0.9f, 0.5f,
            };
            float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            for (int i = 0; i < 4; i++)
            {
                mesh.AddVertex(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                    uvs[i * 2], uvs[i * 2 + 1], ColorUtil.WhiteArgb);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.AddIndex(index);
            return mesh;
        }
    }

    // ---------------------------------------------------------------- native shaders

    /// <summary>The four programs' manifest, built once for the whole class.</summary>
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
            foreach (string program in new[] { "nightsky", "celestialobject", "particlescube", "decals", "standard" })
            {
                NativeShaderBuildResult one = builder.Build(source, program);
                merged.Errors.AddRange(one.Errors);
                merged.Manifest.Programs.AddRange(one.Manifest.Programs);
                foreach ((string file, byte[] bytes) in one.Files) merged.Files[file] = bytes;
            }
            if (!merged.Success) return ("", string.Join("\n", merged.Errors));

            string root = Path.Combine(Path.GetTempPath(), "optimum-native-world-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
