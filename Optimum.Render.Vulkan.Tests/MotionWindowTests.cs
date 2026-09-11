using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 2 contract C4 on a real device, once per colour write tier (forced the
/// way OPTIMUM_VULKAN_COLOR_WRITE_TIER forces it, through the context options):
/// draw buffers and the TAA motion windows are write masks inside one rendering
/// scope, never scope restarts. Every test reads back only after the draws, in
/// the frame, and asserts bytes rather than approximations.
/// </summary>
public class MotionWindowTests
{
    private readonly ITestOutputHelper _output;

    public MotionWindowTests(ITestOutputHelper output) => _output = output;

    private const int Size = 8;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private static string FourOutputs(string colour, string glow, string motion, string extra) => $$"""
        #version 330 core
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        layout(location = 2) out vec4 outMotion;
        layout(location = 3) out vec4 outExtra;
        void main(void)
        {
            outColor = vec4({{colour}});
            outGlow = vec4({{glow}});
            outMotion = vec4({{motion}});
            outExtra = vec4({{extra}});
        }
        """;

    /// <summary>The OPTIMUM_VULKAN_COLOR_WRITE_TIER tokens.</summary>
    public static TheoryData<string> Tiers => new() { "enable", "mask", "pipeline" };

    private static ColorWriteTier Tier(string token) =>
        DeviceCaps.ParseColorWriteTier(token) ?? throw new ArgumentException("unknown tier " + token);

    private bool TryCreateDevice(ColorWriteTier tier, out VulkanDevice? device)
    {
        VulkanDevice created = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = created.ConfigureContextOptions;
        created.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.ColorWriteTier = tier;
        };

        if (!created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            _output.WriteLine("Vulkan unavailable: " + failureReason);
            created.Dispose();
            device = null;
            return false;
        }
        if (created.ColorWriteTierForTests != tier)
        {
            _output.WriteLine("device lacks tier " + tier + "; selected " + created.ColorWriteTierForTests);
            created.Dispose();
            device = null;
            return false;
        }
        device = created;
        return true;
    }

    private sealed class Scene
    {
        public int Framebuffer;
        public int Colour;
        public int Glow;
        public int Motion;
        public int Extra;
    }

    /// <summary>Colour and glow RGBA8, motion RGBA16F, a fourth RGBA8 slot: Primary's shape with TAA on.</summary>
    private static Scene CreateScene(VulkanDevice seam)
    {
        var scene = new Scene
        {
            Colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
            Glow = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
            Motion = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
            Extra = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
        };
        scene.Framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment0, scene.Colour, 0);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment1, scene.Glow, 0);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment2, scene.Motion, 0);
        seam.AttachTexture(scene.Framebuffer, EnumFramebufferAttachment.ColorAttachment3, scene.Extra, 0);
        Assert.True(seam.CheckFramebufferComplete(scene.Framebuffer, out string status), status);
        return scene;
    }

    private static void BaseState(VulkanDevice seam)
    {
        seam.SetViewport(0, 0, Size, Size);
        seam.SetScissorEnabled(false);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.SetColorMask(true, true, true, true);
    }

    /// <summary>A frame that clears every attachment to a known value, with every draw buffer selected.</summary>
    private static void SeedFrame(VulkanDevice seam, Scene scene)
    {
        seam.BeginFrame();
        seam.BindFramebuffer(scene.Framebuffer);
        BaseState(seam);
        seam.SetDrawBuffers(scene.Framebuffer, 0b1111);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0.2f, 0.4f, 0.6f, 1f);
        seam.ClearColor(2, 0.125f, 0.25f, 0.375f, 0.5f);
        seam.ClearColor(3, 0.4f, 0.6f, 0.8f, 1f);
        seam.Present();
    }

    private static void AssertEveryPixel(byte[] texels, int bytesPerPixel, byte[] expected, string what)
    {
        Assert.Equal(Size * Size * bytesPerPixel, texels.Length);
        for (int p = 0; p < texels.Length; p += bytesPerPixel)
        {
            for (int c = 0; c < bytesPerPixel; c++)
            {
                if (texels[p + c] != expected[c])
                {
                    Assert.Fail($"{what}: pixel {p / bytesPerPixel} byte {c} is {texels[p + c]}, expected {expected[c]}");
                }
            }
        }
    }

    private static byte[] Half(float r, float g, float b, float a)
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), (Half)r);
        BitConverter.TryWriteBytes(bytes.AsSpan(2, 2), (Half)g);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (Half)b);
        BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), (Half)a);
        return bytes;
    }

    /// <summary>
    /// A pass over Primary with the default colour set, a motion window (all
    /// three), a motion-only window, a restore, a masked-out clear and an
    /// all-false colour-mask clear. The fourth slot never has its draw buffer on
    /// in the pass, though every program writes it: bit-identical to the previous
    /// frame's seed. The motion-only draw writes exactly the motion attachment.
    /// All of it in one rendering scope; no restart from a mask change.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Tiers))]
    public void MotionWindowsAreWriteMasksInsideOneScope(string tierToken)
    {
        ColorWriteTier tier = Tier(tierToken);
        Skip.IfNot(TryCreateDevice(tier, out VulkanDevice? device), "Vulkan or tier " + tier + " unavailable.");
        using (device)
        {
            VulkanDevice seam = device!;
            int opaque = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex,
                FourOutputs("1.0, 0.0, 0.0, 1.0", "0.0, 1.0, 0.0, 1.0", "0.75, 0.75, 0.75, 0.75", "1.0, 1.0, 1.0, 1.0"),
                "mw-opaque");
            int motionOnly = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex,
                FourOutputs("0.0, 0.0, 1.0, 1.0", "0.0, 0.0, 1.0, 1.0", "1.0, 0.5, 0.0, 1.0", "0.0, 0.0, 0.0, 0.0"),
                "mw-motion-only");
            Scene scene = CreateScene(seam);
            SeedFrame(seam, scene);

            seam.BeginFrame();
            seam.BindFramebuffer(scene.Framebuffer);
            BaseState(seam);
            long scopesBefore = device!.ScopesOpenedForTests;

            // Default colour set: colour and glow; motion and extra masked.
            seam.SetDrawBuffers(scene.Framebuffer, 0b0011);
            seam.UseProgram(opaque);
            seam.DrawFullscreenTriangle();

            // Motion window (EnableMotionDrawBuffers), then the motion-only window.
            seam.SetDrawBuffers(scene.Framebuffer, 0b0111);
            seam.DrawFullscreenTriangle();
            seam.SetDrawBuffers(scene.Framebuffer, 0b0100);
            seam.UseProgram(motionOnly);
            seam.DrawFullscreenTriangle();

            // Restore, then two clears that must not land: motion masked out, and
            // colour through an all-false glColorMask.
            seam.SetDrawBuffers(scene.Framebuffer, 0b0011);
            seam.ClearColor(2, 0f, 0f, 0f, 0f);
            seam.SetColorMask(false, false, false, false);
            seam.ClearColor(0, 0f, 0f, 1f, 1f);
            seam.SetColorMask(true, true, true, true);
            seam.UseProgram(opaque);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0001);
            seam.DrawFullscreenTriangle();

            long scopes = device.ScopesOpenedForTests - scopesBefore;
            long maskRestarts = device.MaskRestartsForTests;
            long splits = device.FeedbackSplitsForTests;

            byte[] colour = device.ReadBackLevel0ForTests(scene.Colour);
            byte[] glow = device.ReadBackLevel0ForTests(scene.Glow);
            byte[] motion = device.ReadBackLevel0ForTests(scene.Motion);
            byte[] extra = device.ReadBackLevel0ForTests(scene.Extra);
            seam.Present();

            _output.WriteLine($"tier={tier} scopes={scopes} mask_restarts={maskRestarts} feedback_splits={splits}");
            Assert.Equal(1, scopes);
            Assert.Equal(0, maskRestarts);
            Assert.Equal(0, splits);

            AssertEveryPixel(colour, 4, new byte[] { 255, 0, 0, 255 }, "colour");
            AssertEveryPixel(glow, 4, new byte[] { 0, 255, 0, 255 }, "glow");
            AssertEveryPixel(motion, 8, Half(1f, 0.5f, 0f, 1f), "motion (motion-only window wrote it last)");
            AssertEveryPixel(extra, 4, new byte[] { 102, 153, 204, 255 }, "extra (draw buffer never on)");

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The OIT merge: standard blending on colour, additive (ONE, ONE) on the
    /// motion attachment (ApplyOptimumMotionAccumulateBlendState), a program that
    /// adds only to motion's blue. Red, green and alpha of the RGBA16F motion
    /// texels stay bit-exact; glow, which the program does not write, is untouched.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Tiers))]
    public void OitMergeAdditiveOnMotionLeavesRedGreenAndAlphaBitExact(string tierToken)
    {
        ColorWriteTier tier = Tier(tierToken);
        Skip.IfNot(TryCreateDevice(tier, out VulkanDevice? device), "Vulkan or tier " + tier + " unavailable.");
        using (device)
        {
            VulkanDevice seam = device!;
            int merge = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                layout(location = 0) out vec4 outColor;
                layout(location = 2) out vec4 outMotion;
                void main(void)
                {
                    outColor = vec4(1.0, 1.0, 1.0, 0.5);
                    outMotion = vec4(0.0, 0.0, 0.25, 0.0);
                }
                """, "mw-oit-merge");
            Scene scene = CreateScene(seam);
            SeedFrame(seam, scene);

            seam.BeginFrame();
            seam.BindFramebuffer(scene.Framebuffer);
            BaseState(seam);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0111);
            seam.UseProgram(merge);
            // ApplyTransparentMergeBlendState, then the motion accumulate override.
            seam.SetBlend(true, EnumBlendMode.Standard);
            seam.SetBlendFuncSeparate(0, 770, 771, 770, 771);
            seam.SetBlendEquation(2, 32774);
            seam.SetBlendFuncSeparate(2, 1, 1, 1, 1);
            seam.DrawFullscreenTriangle();

            // The accumulate override must not outlive the draw: a second draw with
            // replace blending on motion keeps the scope and rewrites blue only.
            seam.SetBlendFuncSeparate(2, 1, 0, 1, 0);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0001);
            seam.DrawFullscreenTriangle();

            long maskRestarts = device!.MaskRestartsForTests;
            byte[] colour = device.ReadBackLevel0ForTests(scene.Colour);
            byte[] glow = device.ReadBackLevel0ForTests(scene.Glow);
            byte[] motion = device.ReadBackLevel0ForTests(scene.Motion);
            seam.Present();

            Assert.Equal(0, maskRestarts);
            byte[] seed = Half(0.125f, 0.25f, 0.375f, 0.5f);
            byte[] merged = Half(0.125f, 0.25f, 0.625f, 0.5f);
            for (int p = 0; p < motion.Length; p += 8)
            {
                Assert.Equal(seed.AsSpan(0, 4).ToArray(), motion.AsSpan(p, 4).ToArray());   // red, green
                Assert.Equal(seed.AsSpan(6, 2).ToArray(), motion.AsSpan(p + 6, 2).ToArray()); // alpha
            }
            AssertEveryPixel(motion, 8, merged, "motion after additive merge");
            AssertEveryPixel(glow, 4, new byte[] { 51, 102, 153, 255 }, "glow (not written by the merge)");

            // Colour: two draws of (1,1,1,0.5) over black with SRC_ALPHA blending.
            Assert.InRange(colour[0], 189, 193);
            Assert.Equal(colour[0], colour[1]);

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The final composition shape: draw buffers select Primary 0 only while the
    /// program samples Primary 1. The sampled slot leaves the scope (one feedback
    /// split), colour receives glow's texels exactly, glow keeps them; selecting
    /// glow again lets it rejoin and a write lands. Validation stays clean.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Tiers))]
    public void CompositionSamplesAnAttachmentItsDrawBuffersExclude(string tierToken)
    {
        ColorWriteTier tier = Tier(tierToken);
        Skip.IfNot(TryCreateDevice(tier, out VulkanDevice? device), "Vulkan or tier " + tier + " unavailable.");
        using (device)
        {
            VulkanDevice seam = device!;
            int compose = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D glowTex;
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = texture(glowTex, uv); }
                """, "mw-compose");
            int writeGlow = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex,
                FourOutputs("0.0, 0.0, 0.0, 1.0", "1.0, 0.0, 0.0, 1.0", "0.0, 0.0, 0.0, 0.0", "0.0, 0.0, 0.0, 0.0"),
                "mw-write-glow");
            Scene scene = CreateScene(seam);
            SeedFrame(seam, scene);

            seam.BeginFrame();
            seam.BindFramebuffer(scene.Framebuffer);
            BaseState(seam);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0001);
            seam.ClearColor(0, 0f, 0f, 0f, 1f); // opens the scope with glow in it, masked
            seam.UseProgram(compose);
            seam.SetSamplerUnit(compose, "glowTex", 0);
            seam.BindTexture(0, scene.Glow);
            seam.DrawFullscreenTriangle();
            byte[] composed = device!.ReadBackLevel0ForTests(scene.Colour);
            byte[] glowAfterCompose = device.ReadBackLevel0ForTests(scene.Glow);
            long splitsAfterCompose = device.FeedbackSplitsForTests;

            seam.BindTexture(0, 0);
            seam.BindFramebuffer(scene.Framebuffer);
            seam.SetDrawBuffers(scene.Framebuffer, 0b0010);
            seam.UseProgram(writeGlow);
            seam.DrawFullscreenTriangle();
            long maskRestarts = device.MaskRestartsForTests;
            byte[] glowAfterWrite = device.ReadBackLevel0ForTests(scene.Glow);
            seam.Present();

            _output.WriteLine($"tier={tier} splits_after_compose={splitsAfterCompose} mask_restarts={maskRestarts}");
            Assert.Equal(1, splitsAfterCompose);
            Assert.Equal(0, maskRestarts);
            AssertEveryPixel(composed, 4, new byte[] { 51, 102, 153, 255 }, "colour = sampled glow");
            AssertEveryPixel(glowAfterCompose, 4, new byte[] { 51, 102, 153, 255 }, "glow untouched by composition");
            AssertEveryPixel(glowAfterWrite, 4, new byte[] { 255, 0, 0, 255 }, "glow written after it rejoined");

            GpuTest.AssertClean(seam);
        }
    }
}
