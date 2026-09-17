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

using LinkedProgram = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestProgram;
using LinkedShader = Optimum.Render.Vulkan.Tests.VulkanDeviceIntegrationTests.TestShader;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The post and TAA chain on the Vulkan platform (docs/vulkan-native-render-systems.md, stage 1).
/// Optimum owns the chain's order; its first two passes - the OIT merge and sky motion - draw
/// through the native device API, and the rest run the OpenGL body until their own stage moves
/// them.
///
/// What is asserted here:
///  1. the OIT merge draws the same pixels as the OpenGL body, with and without the motion
///     window, into the shaded image, the glow attachment and the motion attachment;
///  2. sky motion writes the same motion attachment as the OpenGL body and leaves the shaded
///     image alone;
///  3. each native pass records only its own draws (structural since the GL emulation went);
///  4. over several frames the chain runs its steps in the declared order and the TAA resolve
///     keeps accumulating - the history parity alternates and the motion attachment the resolve
///     reads was written by the two passes that run before it;
///  5. the TAA resolve and the TAA sharpen draw the same pixels natively as on the OpenGL body,
///     from a cold history and from a warm one, and the sharpen is skipped on both routes when
///     there is nothing to sharpen;
///  6. several frames of the native resolve with no readback between them keep accumulating -
///     the result after five frames is not the result after one, and it is the OpenGL body's.
/// </summary>
public class NativePostChainTests(ITestOutputHelper output)
{
    private const int Size = 16;

    private static readonly string[] Programs =
    {
        "transparentcompose", "taa-skymotion", "taa-resolve", "taa-sharpen", "blit",
        "findbright", "blur", "godrays", "luma", "final",
    };

    /// <summary>
    /// A jitter phase per loop step, pinned so two runs of the same length see the same sequence
    /// whatever the global frame counter happens to be. The values are Halton-shaped: sub-pixel,
    /// never zero, never repeating inside one run.
    /// </summary>
    private static readonly (float X, float Y)[] JitterPhases =
    {
        (0.25f, -0.375f), (-0.125f, 0.25f), (0.375f, 0.125f),
        (-0.375f, -0.25f), (0.125f, 0.375f), (-0.25f, -0.125f),
    };

    /// <summary>The Vulkan platform without a window: the size seam answers for one.</summary>
    private sealed class ChainPlatform : VulkanClientPlatform
    {
        public ChainPlatform() : base(null!)
        {
        }

        /// <summary>
        /// The window the size seam answers with. Render scale below 1 is this size divided by
        /// the render targets' size, exactly as the client computes it: client 32 at ssaa 0.5
        /// gives the same 16-pixel targets as client 16 at ssaa 1.
        /// </summary>
        public Size2i ClientSize { get; set; } = new(NativePostChainTests.Size, NativePostChainTests.Size);

        /// <summary>
        /// The god-rays pass takes its time uniform from here. Pinned so the two routes of a
        /// differential run cannot be handed different values by the wall clock.
        /// </summary>
        public override long EllapsedMs => 4242;

        public override Size2i OptimumWindowClientSize() => ClientSize;

        /// <summary>
        /// No window is opened here, and the base's cases size their viewports from
        /// NativeWindow.ClientSize, which is GLFW-backed. Every post target in this fixture is
        /// built at exactly the size its case computes - Primary and Luma at the render
        /// resolution, the bloom pair at half and at a quarter of it, god rays at half - so
        /// binding the target and taking the viewport from it is the same bind and the same
        /// viewport, for the OpenGL route and the native one alike.
        /// </summary>
        public override void LoadFrameBuffer(EnumFrameBuffer framebuffer)
        {
            if (framebuffer == EnumFrameBuffer.Primary)
            {
                CurrentFrameBuffer = FrameBuffers[0];
                return;
            }
            switch (framebuffer)
            {
            case EnumFrameBuffer.BlurHorizontalMedRes:
            case EnumFrameBuffer.BlurVerticalMedRes:
            case EnumFrameBuffer.BlurHorizontalLowRes:
            case EnumFrameBuffer.BlurVerticalLowRes:
            case EnumFrameBuffer.GodRays:
                CurrentFrameBuffer = FrameBuffers[(int)framebuffer];
                return;
            }
            base.LoadFrameBuffer(framebuffer);
        }
    }

    // ------------------------------------------------------------------ the tests

    /// <summary>
    /// The OIT merge with the motion window open: the shaded image, the glow attachment and the
    /// motion attachment all have to come out of the native pass exactly as the OpenGL body
    /// leaves them, including the additive (ONE, ONE) blend that only touches the reactive
    /// channel.
    /// </summary>
    [SkippableFact]
    public void TheOitMergeMatchesTheOpenGlBodyWithTheMotionWindowOpen()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        Frame stated = RunMerge(session, native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        Frame nativeRoute = RunMerge(session, native: true);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);

        Assert.Equal(stated.Scene, nativeRoute.Scene);
        Assert.Equal(stated.Glow, nativeRoute.Glow);
        Assert.Equal(stated.Motion, nativeRoute.Motion);

        // The merge really did add into the reactive channel, or the comparison above would
        // pass on two routes that both wrote nothing.
        Assert.True(Reactive(nativeRoute.Motion, Size / 2, Size / 2) > Reactive(session.MotionSeed, 0, 0),
            "the merge did not accumulate coverage into the reactive channel");

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The same merge with TAA off: the motion window never opens, so the pass writes the two
    /// world attachments and leaves the motion attachment exactly as it found it.
    /// </summary>
    [SkippableFact]
    public void TheOitMergeMatchesTheOpenGlBodyWithoutTheMotionWindow()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: false);

        Frame stated = RunMerge(session, native: false);
        Frame nativeRoute = RunMerge(session, native: true);

        Assert.Equal(stated.Scene, nativeRoute.Scene);
        Assert.Equal(stated.Glow, nativeRoute.Glow);
        Assert.Equal(stated.Motion, nativeRoute.Motion);
        Assert.Equal(session.MotionSeed, nativeRoute.Motion);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Sky motion: the motion attachment the native pass writes is the OpenGL body's, and the
    /// shaded image is untouched - the pass writes one colour slot and no depth.
    /// </summary>
    [SkippableFact]
    public void SkyMotionMatchesTheOpenGlBody()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        Frame stated = RunSkyMotion(session, native: false);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        Frame nativeRoute = RunSkyMotion(session, native: true);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);

        Assert.Equal(stated.Motion, nativeRoute.Motion);
        Assert.Equal(stated.Scene, nativeRoute.Scene);
        Assert.Equal(session.SceneSeed, nativeRoute.Scene);

        // The pass covered the sky, or "the two routes agree" would be vacuous.
        Assert.True(Reactive(nativeRoute.Motion, Size / 2, Size / 2) > 0.5f,
            "sky motion wrote no reactive value, so the depth test rejected the whole target");

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>The OpenGL body on this device is the generic stated route; the native chain is not.</summary>
    [SkippableFact]
    public void TheOpenGlRouteDrawsThroughTheStatedRouteAndTheNativeChainDoesNot()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        long nativeDrawsBefore = session.Seam.NativeDrawsForTests;
        long statedBefore = session.Platform.StatedDrawsForTests;
        RunMerge(session, native: false);
        Assert.Equal(0, session.Seam.NativeDrawsForTests - nativeDrawsBefore);
        Assert.True(session.Platform.StatedDrawsForTests - statedBefore > 0);

        RunMerge(session, native: true);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Several frames through the native chain: every frame runs the steps in the declared
    /// order, and TAA keeps accumulating - the resolve runs each frame, the history parity
    /// alternates so each frame reads the slot the last one wrote, and the motion attachment it
    /// reads carries what the merge and sky motion wrote before it.
    ///
    /// The final composition is the one declared step not driven here: its body reads
    /// NativeWindow.ClientSize directly, which a windowless test cannot answer. Its position in
    /// the chain is pinned by the source coverage test (Optimum.Tests, native-post-chain).
    /// </summary>
    [SkippableFact]
    public void TheChainKeepsItsOrderAcrossFramesAndTaaKeepsAccumulating()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);

        VulkanDevice seam = session.Seam;
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = true;

        var expected = new List<VulkanClientPlatform.NativePostStep>();
        foreach (VulkanClientPlatform.NativePostStep step in VulkanClientPlatform.NativePostChainOrder)
        {
            if (step != VulkanClientPlatform.NativePostStep.FinalComposition) expected.Add(step);
        }

        var resolvedTextures = new List<int>();
        const int frames = 4;
        for (int frame = 0; frame < frames; frame++)
        {
            session.AdvanceTemporalFrame();
            var log = new List<VulkanClientPlatform.NativePostStep>();
            platform.NativePostStepLog = log;

            platform.BeginFrame();
            session.SeedFrame();

            platform.CurrentFrameBuffer = session.Primary;
            platform.MergeTransparentRenderPass();
            platform.CurrentFrameBuffer = session.Primary;
            platform.RenderOptimumSkyMotion();
            platform.RenderPostprocessingEffects(null);
            platform.BlitPrimaryToDefault();

            platform.NativePostStepLog = null;
            Assert.Equal(expected, log);
            Assert.True(platform.TaaResolvedThisFrame, "the resolve did not run on frame " + frame);
            resolvedTextures.Add(ResolvedColorTexture(platform));

            byte[] motion = session.ReadMotion();
            Assert.True(Reactive(motion, Size / 2, Size / 2) > 0.5f,
                "frame " + frame + " reached the resolve with an empty motion attachment, " +
                "so the merge and sky motion did not run before it");

            platform.EndFrame();
        }

        // The resolve alternates its history slots, so every frame reads the one the previous
        // frame wrote: that alternation is the accumulation.
        int slotA = session.History(0).ColorTextureIds[0];
        int slotB = session.History(1).ColorTextureIds[0];
        for (int frame = 0; frame < frames; frame++)
        {
            Assert.Equal(frame % 2 == 0 ? slotA : slotB, resolvedTextures[frame]);
        }
        Assert.True(HistoryValid(platform), "the resolve left the history invalid");

        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// The TAA resolve, natively: all three attachments of the history slot it writes - the
    /// resolved colour, the resolved glow and the linear depth - have to be the OpenGL body's,
    /// from a cold history (the reset frame, which copies the scene through) and from a warm one
    /// (the frame that actually blends history in).
    ///
    /// The temporal invariants are the shader's, and both routes run the same taa-resolve.fsh: what is
    /// asserted here is that the native route feeds it the same seven textures and the same nine
    /// uniform values, so the 3x3 nearest-depth disocclusion and the luminance anti-flicker
    /// weighting see identical inputs and produce identical pixels.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTaaResolveMatchesTheOpenGlBody(bool warmHistory)
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);
        session.PatternedScene = true;

        Resolved stated = RunResolve(session, native: false, warmHistory);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        Resolved nativeRoute = RunResolve(session, native: true, warmHistory);

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);

        Assert.Equal(stated.Color, nativeRoute.Color);
        Assert.Equal(stated.Glow, nativeRoute.Glow);
        Assert.Equal(stated.Depth, nativeRoute.Depth);

        // The pass wrote a resolved image over the seed, or the comparison above would hold
        // for two routes that both wrote nothing.
        Assert.NotEqual(session.HistorySeedColor, nativeRoute.Color);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The TAA resolve with TAA off: the lib body returns before any draw, so the native route
    /// declares no pass at all and the history is marked invalid on both routes.
    /// </summary>
    [SkippableFact]
    public void TheTaaResolveDrawsNothingWithTaaOff()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);
        OptimumConfig.Taa = false;

        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = true;
        long passesBefore = session.Seam.NativePassesForTests;

        platform.BeginFrame();
        session.SeedFrame();
        platform.CurrentFrameBuffer = session.Primary;
        Assert.False(platform.RenderOptimumTaaResolve());
        platform.EndFrame();

        Assert.Equal(0, session.Seam.NativePassesForTests - passesBefore);
        Assert.False(platform.TaaResolvedThisFrame);
        Assert.False(HistoryValid(platform));

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The TAA sharpen, natively: the sharpen target's single attachment has to be the OpenGL
    /// body's at every strength the setting can take, and the texture the pass hands on to the
    /// rest of the chain has to be the sharpen target either way.
    /// </summary>
    [SkippableTheory]
    [InlineData(1f)]
    [InlineData(0.35f)]
    public void TheTaaSharpenMatchesTheOpenGlBody(float sharpness)
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);
        session.PatternedScene = true;
        OptimumConfig.TaaSharpness = sharpness;

        byte[] stated = RunSharpen(session, native: false, out int statedTexture);

        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;
        byte[] nativeRoute = RunSharpen(session, native: true, out int nativeTexture);

        // One native pass for the resolve that has to run first, one for the sharpen.
        Assert.Equal(2, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(2, session.Seam.NativeDrawsForTests - drawsBefore);

        Assert.Equal(session.Sharpen.ColorTextureIds[0], statedTexture);
        Assert.Equal(session.Sharpen.ColorTextureIds[0], nativeTexture);
        Assert.Equal(stated, nativeRoute);
        Assert.NotEqual(session.SharpenSeed, nativeRoute);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The sharpen's conditions live in the lib body, so both routes skip it on exactly the same
    /// frames: sharpness at zero hands the resolved texture straight on and draws nothing.
    /// </summary>
    [SkippableFact]
    public void TheTaaSharpenIsSkippedOnBothRoutesWhenThereIsNothingToSharpen()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);
        OptimumConfig.TaaSharpness = 0f;

        ChainPlatform platform = session.Platform;
        foreach (bool native in new[] { false, true })
        {
            platform.NativePostChainEnabled = native;
            long passesBefore = session.Seam.NativePassesForTests;

            platform.BeginFrame();
            session.SeedFrame();
            platform.CurrentFrameBuffer = session.Primary;
            Assert.True(platform.RenderOptimumTaaResolve());
            int resolved = platform.OptimumPostSceneTexture();
            Assert.Equal(resolved, platform.RenderOptimumTaaSharpen(resolved));
            platform.EndFrame();

            // The resolve's pass on the native route, and nothing for the sharpen.
            Assert.Equal(native ? 1 : 0, session.Seam.NativePassesForTests - passesBefore);
        }

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// Several frames of the native resolve with nothing read back between them: the history is
    /// still being accumulated, not replaced. Five frames do not land where one frame lands -
    /// each frame reprojects the previous slot at its own sub-pixel jitter and blends it in -
    /// and where they land is the OpenGL body's answer to the identical sequence.
    ///
    /// A single-frame readback cannot see this: it passed while the R32F-history and
    /// masked-clear bugs were live (P2, 2026-09-10), which is why the loop below reads nothing.
    /// </summary>
    [SkippableFact]
    public void TheNativeResolveKeepsAccumulatingHistoryAcrossFrames()
    {
        using Session session = Open();
        session.EnableTaa(jitterActive: true);
        session.PatternedScene = true;

        // The same last frame - the same jitter phase, the same scene, the same slot - reached
        // two ways: cold, and after four frames of history. Pinning the phase is what makes the
        // difference between them history and nothing else.
        byte[] lastFrameAlone = RunResolveFrames(session, native: true, frames: 1, startPhase: 4);
        byte[] fiveFrames = RunResolveFrames(session, native: true, frames: 5, startPhase: 0);
        Assert.NotEqual(lastFrameAlone, fiveFrames);

        byte[] statedFive = RunResolveFrames(session, native: false, frames: 5, startPhase: 0);
        Assert.Equal(statedFive, fiveFrames);

        GpuTest.AssertClean(session.Seam);
    }

    // ------------------------------------------------- the chain's tail, both routes

    /// <summary>
    /// The settings that change the chain's tail. Each row is one run of the whole chain on both
    /// routes: bloom, god rays, FXAA, vanilla SSAO, TAA, the render scale (client size over target
    /// size), the AO debug view and which AO texture the frame produced.
    /// </summary>
    public static TheoryData<string, bool, bool, bool, bool, bool, float, int, bool, bool> TailSettings()
    {
        var data = new TheoryData<string, bool, bool, bool, bool, bool, float, int, bool, bool>();
        //      name                 bloom  rays   fxaa   ssao   taa    ssaa  client  debug  gtao
        data.Add("everything-off",   false, false, false, false, false, 1f,   Size,   false, false);
        data.Add("bloom",            true,  false, false, false, false, 1f,   Size,   false, false);
        data.Add("god-rays",         false, true,  false, false, false, 1f,   Size,   false, false);
        data.Add("fxaa",             false, false, true,  false, false, 1f,   Size,   false, false);
        data.Add("bloom-rays-fxaa",  true,  true,  true,  false, false, 1f,   Size,   false, false);
        data.Add("ssao",             true,  true,  false, true,  false, 1f,   Size,   false, false);
        data.Add("ssao-debug-view",  false, false, false, true,  false, 1f,   Size,   true,  false);
        data.Add("ssao-gtao",        false, false, false, true,  false, 1f,   Size,   false, true);
        data.Add("ssao-gtao-debug",  false, false, false, true,  false, 1f,   Size,   true,  true);
        data.Add("taa",              true,  true,  true,  false, true,  1f,   Size,   false, false);
        data.Add("taa-ssao",         true,  true,  true,  true,  true,  1f,   Size,   false, false);
        data.Add("render-scale-half", true, true,  true,  true,  false, 0.5f, Size * 2, false, false);
        return data;
    }

    /// <summary>
    /// Behavioural identity (decision 6) for the four passes this stage made native: the bloom
    /// chain, god rays, the Luma step and the final composition. The whole chain runs twice from
    /// identically seeded targets - once on the OpenGL body, once natively - and every target the
    /// tail writes has to come out the same, bitwise: the find-bright image, the low-resolution
    /// bloom result the composition reads, the god-ray target, the Luma target and Primary colour
    /// 0. The motion attachment is checked too, because the composition keeps it in its scope
    /// while writing colour 0 and must not touch it.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(TailSettings))]
    public void TheChainTailMatchesTheOpenGlBodyAcrossThePostSettings(string name, bool bloom, bool godRays,
        bool fxaa, bool ssao, bool taa, float ssaa, int clientSize, bool debugView, bool gtao)
    {
        using Session session = Open();
        if (taa) session.EnableTaa(jitterActive: true);
        else OptimumConfig.Taa = false;
        OptimumConfig.AmbientOcclusionDebugView = debugView;
        session.ApplyPostSettings(bloom, godRays, fxaa, ssao, ssaa, clientSize);

        int aoTexture = gtao ? session.SsaoBlurTexture : 0;
        TailFrame stated = RunTail(session, native: false, aoInScene: gtao, aoTexture: aoTexture);

        long passesBefore = session.Seam.NativePassesForTests;
        long copiesBefore = session.Seam.ReadSelfCopiesForTests.Created;
        TailFrame nativeRoute = RunTail(session, native: true, aoInScene: gtao, aoTexture: aoTexture);

        // The Luma step and the final composition always draw; bloom adds five passes, god rays
        // one, and TAA one more for the resolve (the sharpen declares no pass of its own). No
        // native pass reached the generic stated route, and the composition's self-read took no
        // feedback copy - the declared attachment subset is what makes it safe.
        long expectedPasses = 2 + (bloom ? 5 : 0) + (godRays ? 1 : 0) + (taa ? 1 : 0);
        Assert.Equal(expectedPasses, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(copiesBefore, session.Seam.ReadSelfCopiesForTests.Created);

        Assert.Equal(stated.FindBright, nativeRoute.FindBright);
        Assert.Equal(stated.BloomLow, nativeRoute.BloomLow);
        Assert.Equal(stated.GodRays, nativeRoute.GodRays);
        Assert.Equal(stated.Luma, nativeRoute.Luma);
        Assert.Equal(stated.Final, nativeRoute.Final);
        Assert.Equal(stated.Motion, nativeRoute.Motion);

        // The composition really wrote something, or "the two routes agree" would be vacuous.
        Assert.NotEqual(session.SceneSeed, nativeRoute.Final);

        GpuTest.AssertClean(session.Seam);
    }

    /// <summary>
    /// The final composition samples Primary colour 1 as the glow on a frame with no TAA resolve,
    /// while it writes Primary colour 0. The pass declares the attachment subset, so colour 1 is
    /// moved to the shader-read layout for the pass and back afterwards - the frame takes no
    /// feedback copy - and the glow attachment itself comes out of the pass unchanged.
    /// </summary>
    [SkippableFact]
    public void TheFinalCompositionReadsPrimaryColourOneWithoutAFeedbackCopy()
    {
        using Session session = Open();
        OptimumConfig.Taa = false;
        OptimumConfig.AmbientOcclusionDebugView = false;
        session.ApplyPostSettings(bloom: false, godRays: false, fxaa: false, ssao: false, ssaa: 1f,
            clientSize: Size);

        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = true;
        session.ResetTaaHistory();

        long copiesBefore = session.Seam.ReadSelfCopiesForTests.Created;
        long passesBefore = session.Seam.NativePassesForTests;
        long drawsBefore = session.Seam.NativeDrawsForTests;

        platform.BeginFrame();
        session.SeedFrame();
        platform.CurrentFrameBuffer = session.Primary;
        Assert.False(platform.TaaResolvedThisFrame);

        platform.RenderFinalComposition();

        byte[] glow = session.ReadGlow();
        byte[] scene = session.ReadScene();
        platform.EndFrame();

        Assert.Equal(1, session.Seam.NativePassesForTests - passesBefore);
        Assert.Equal(1, session.Seam.NativeDrawsForTests - drawsBefore);
        Assert.Equal(copiesBefore, session.Seam.ReadSelfCopiesForTests.Created);
        Assert.NotEqual(session.SceneSeed, scene);
        Assert.Equal(session.GlowSeed, glow);

        GpuTest.AssertClean(session.Seam);
    }

    // ---------------------------------------------------------------------- driving

    private readonly record struct Frame(byte[] Scene, byte[] Glow, byte[] Motion);

    /// <summary>The three attachments of the history slot a resolve wrote.</summary>
    private readonly record struct Resolved(byte[] Color, byte[] Glow, byte[] Depth);

    /// <summary>
    /// One TAA resolve on the route under test, from an identically seeded frame: the history
    /// parity pinned to slot A, both history slots seeded, and the jitter pinned to phase 0 so
    /// the two routes see the same sub-pixel offset.
    /// </summary>
    private Resolved RunResolve(Session session, bool native, bool warmHistory)
    {
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;
        session.AdvanceTemporalFrame(0);
        SetParity(platform, 0);
        SetHistoryValid(platform, warmHistory);

        platform.BeginFrame();
        session.SeedFrame();
        session.SeedHistory();
        platform.CurrentFrameBuffer = session.Primary;

        Assert.True(platform.RenderOptimumTaaResolve(), "the resolve did not run");
        Assert.Equal(1, Parity(platform));

        var resolved = new Resolved(session.ReadHistoryColor(0), session.ReadHistoryGlow(0),
            session.ReadHistoryDepth(0));
        platform.EndFrame();
        return resolved;
    }

    /// <summary>One resolve and one sharpen on the route under test, from an identically seeded frame.</summary>
    private byte[] RunSharpen(Session session, bool native, out int handedOn)
    {
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;
        session.AdvanceTemporalFrame(0);
        SetParity(platform, 0);
        SetHistoryValid(platform, true);

        platform.BeginFrame();
        session.SeedFrame();
        session.SeedHistory();
        session.SeedSharpen();
        platform.CurrentFrameBuffer = session.Primary;

        Assert.True(platform.RenderOptimumTaaResolve(), "the resolve did not run");
        handedOn = platform.RenderOptimumTaaSharpen(platform.OptimumPostSceneTexture());

        byte[] sharpened = session.ReadSharpen();
        platform.EndFrame();
        return sharpened;
    }

    /// <summary>
    /// <paramref name="frames" /> resolves on the route under test, with no readback inside the
    /// loop - only the frame boundary between them - and the history read once at the end. The
    /// run starts cold, so the first frame is the reset frame and every frame after it blends.
    /// An odd frame count always ends on slot A, so two runs of different length are comparable.
    /// </summary>
    private byte[] RunResolveFrames(Session session, bool native, int frames, int startPhase)
    {
        Assert.True(frames % 2 == 1, "an even frame count would end on the other history slot");
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;
        SetParity(platform, 0);
        SetHistoryValid(platform, false);

        platform.BeginFrame();
        session.SeedHistory();
        platform.EndFrame();

        for (int frame = 0; frame < frames; frame++)
        {
            session.AdvanceTemporalFrame(startPhase + frame);
            platform.BeginFrame();
            session.SeedFrame();
            platform.CurrentFrameBuffer = session.Primary;
            Assert.True(platform.RenderOptimumTaaResolve(), "the resolve did not run on frame " + frame);
            platform.EndFrame();
        }

        platform.BeginFrame();
        byte[] history = session.ReadHistoryColor(0);
        platform.EndFrame();
        return history;
    }

    /// <summary>Everything the chain's tail writes, on one route.</summary>
    private readonly record struct TailFrame(byte[] FindBright, byte[] BloomLow, byte[] GodRays,
        byte[] Luma, byte[] Final, byte[] Motion);

    /// <summary>
    /// One whole post chain plus the final composition, on the route under test, from identically
    /// seeded targets. The two AO fields are set between the two calls because the chain's first
    /// step - the AO step, which is still the OpenGL body on both routes - resets them, exactly as
    /// a real frame's GTAO pass would then fill them in.
    /// </summary>
    private TailFrame RunTail(Session session, bool native, bool aoInScene, int aoTexture)
    {
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;
        session.ResetTaaHistory();

        platform.BeginFrame();
        session.SeedFrame();
        // The history the resolve starts from, so both routes run the chain from identical
        // contents; SeedFrame leaves the history alone so frames can accumulate across it.
        session.SeedHistory();
        platform.CurrentFrameBuffer = session.Primary;

        platform.RenderPostprocessingEffects(null);
        session.ApplyAmbientOcclusionState(aoInScene, aoTexture);
        platform.RenderFinalComposition();

        var frame = new TailFrame(
            session.ReadPostTarget(4),
            session.ReadPostTarget(8),
            session.ReadPostTarget(7),
            session.ReadPostTarget(10),
            session.ReadScene(),
            session.ReadMotion());
        platform.EndFrame();
        return frame;
    }

    /// <summary>One OIT merge, on the route under test, from an identically seeded frame.</summary>
    private Frame RunMerge(Session session, bool native)
    {
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;

        platform.BeginFrame();
        session.SeedFrame();
        platform.CurrentFrameBuffer = session.Primary;

        platform.MergeTransparentRenderPass();

        var frame = new Frame(session.ReadScene(), session.ReadGlow(), session.ReadMotion());
        platform.EndFrame();
        return frame;
    }

    /// <summary>One sky-motion pass, on the route under test, from an identically seeded frame.</summary>
    private Frame RunSkyMotion(Session session, bool native)
    {
        ChainPlatform platform = session.Platform;
        platform.NativePostChainEnabled = native;
        session.AdvanceTemporalFrame();

        platform.BeginFrame();
        session.SeedFrame();
        platform.CurrentFrameBuffer = session.Primary;

        bool drawn = platform.RenderOptimumSkyMotion();
        Assert.True(drawn, "the sky motion pass did not run");

        var frame = new Frame(session.ReadScene(), session.ReadGlow(), session.ReadMotion());
        platform.EndFrame();
        return frame;
    }

    /// <summary>The reactive channel of a decoded motion pixel (motion.b, in [0, 1]).</summary>
    private static float Reactive(byte[] decoded, int x, int y) => decoded[(y * Size + x) * 4 + 2] / 255f;

    private static int ResolvedColorTexture(ClientPlatformWindows platform) =>
        (int)typeof(ClientPlatformWindows)
            .GetField("taaResolvedColorTexture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(platform)!;

    private static int Parity(ClientPlatformWindows platform) =>
        (int)ParityField.GetValue(platform)!;

    private static void SetParity(ClientPlatformWindows platform, int parity) =>
        ParityField.SetValue(platform, parity);

    private static void SetHistoryValid(ClientPlatformWindows platform, bool valid) =>
        HistoryValidField.SetValue(platform, valid);

    private static readonly FieldInfo ParityField = typeof(ClientPlatformWindows)
        .GetField("_taaFrameParity", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo HistoryValidField = typeof(ClientPlatformWindows)
        .GetField("_taaHistoryValid", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static bool HistoryValid(ClientPlatformWindows platform) =>
        (bool)typeof(ClientPlatformWindows)
            .GetField("_taaHistoryValid", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(platform)!;

    // ---------------------------------------------------------------------- session

    private Session Open()
    {
        (string manifest, string reason) = NativeManifest.Value;
        Skip.If(manifest.Length == 0, reason);
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        Session? session = Session.TryOpen(output, manifest);
        Skip.If(session == null, "No usable Vulkan device.");
        return session!;
    }

    /// <summary>
    /// The platform, its device, the targets the chain indexes, the programs its passes use and
    /// the client statics they read, all put back afterwards.
    /// </summary>
    private sealed class Session : IDisposable
    {
        public ChainPlatform Platform { get; private init; } = null!;
        public VulkanDevice Seam => Platform.GraphicsDevice!;
        public FrameBufferRef Primary { get; private set; } = null!;
        public FrameBufferRef Transparent { get; private set; } = null!;
        public FrameBufferRef Sharpen { get; private set; } = null!;

        /// <summary>
        /// True leaves Primary's colour 0 as the pattern it was created with instead of clearing
        /// it flat: the TAA resolve's variance clip box collapses on a flat image, so a flat
        /// scene would make every frame after the first return the current frame untouched and
        /// hide whether history is being blended in at all.
        /// </summary>
        public bool PatternedScene { get; set; }

        /// <summary>The history slots' seed and the sharpen target's, as the readbacks decode them.</summary>
        public byte[] HistorySeedColor { get; private set; } = Array.Empty<byte>();
        public byte[] SharpenSeed { get; private set; } = Array.Empty<byte>();

        /// <summary>The motion attachment's seed, decoded the way <see cref="ReadMotion" /> decodes it.</summary>
        public byte[] MotionSeed { get; private set; } = Array.Empty<byte>();

        /// <summary>The shaded image's seed, as read back.</summary>
        public byte[] SceneSeed { get; private set; } = Array.Empty<byte>();

        /// <summary>The glow attachment's seed, decoded the way <see cref="ReadGlow" /> decodes it.</summary>
        public byte[] GlowSeed { get; private set; } = Array.Empty<byte>();

        private readonly List<FrameBufferRef> buffers = new();
        private int oitReveal;
        private int oitAccumulation;
        private int scenePattern;
        private int decodeProgram;
        private int decodeTarget;
        private int decodeFramebuffer;
        private readonly Dictionary<(int Width, int Height), int> decodeFramebuffers = new();
        private ClientPlatformAbstract? previousPlatform;
        private string dataPath = "";
        private ShaderProgramTransparentcompose? composeBefore;
        private ShaderProgram? skyMotionBefore;
        private ShaderProgram? resolveBefore;
        private ShaderProgram? sharpenBefore;
        private ShaderProgramBlit? blitBefore;
        private ShaderProgramFindbright? findbrightBefore;
        private ShaderProgramBlur? blurBefore;
        private ShaderProgramGodrays? godraysBefore;
        private ShaderProgramLuma? lumaBefore;
        private ShaderProgramFinal? finalBefore;
        private bool taaBefore;
        private float sharpnessBefore;
        private bool debugViewBefore;
        private object? oitRevealBefore;
        private object? oitAccumBefore;
        private DefaultShaderUniforms uniforms = new();

        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

        public FrameBufferRef History(int parity) => buffers[parity == 0 ? 19 : 20];

        public static Session? TryOpen(ITestOutputHelper output, string manifestDirectory)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-native-post-" + Guid.NewGuid().ToString("N"));
            var platform = new ChainPlatform
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
                composeBefore = ShaderPrograms.Transparentcompose,
                skyMotionBefore = ShaderPrograms.TaaSkyMotion,
                resolveBefore = ShaderPrograms.TaaResolve,
                sharpenBefore = ShaderPrograms.TaaSharpen,
                blitBefore = ShaderPrograms.Blit,
                findbrightBefore = ShaderPrograms.Findbright,
                blurBefore = ShaderPrograms.Blur,
                godraysBefore = ShaderPrograms.Godrays,
                lumaBefore = ShaderPrograms.Luma,
                finalBefore = ShaderPrograms.Final,
                taaBefore = OptimumConfig.Taa,
                sharpnessBefore = OptimumConfig.TaaSharpness,
                debugViewBefore = OptimumConfig.AmbientOcclusionDebugView,
            };
            ScreenManager.Platform = platform;
            ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static (string _) => { });
            platform.ShaderUniforms = session.uniforms;

            session.BuildTargets();
            session.LinkPrograms();
            session.InstallState();
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            ShaderPrograms.Transparentcompose = composeBefore!;
            ShaderPrograms.TaaSkyMotion = skyMotionBefore!;
            ShaderPrograms.TaaResolve = resolveBefore!;
            ShaderPrograms.TaaSharpen = sharpenBefore!;
            ShaderPrograms.Blit = blitBefore!;
            ShaderPrograms.Findbright = findbrightBefore!;
            ShaderPrograms.Blur = blurBefore!;
            ShaderPrograms.Godrays = godraysBefore!;
            ShaderPrograms.Luma = lumaBefore!;
            ShaderPrograms.Final = finalBefore!;
            OptimumConfig.Taa = taaBefore;
            OptimumConfig.TaaSharpness = sharpnessBefore;
            OptimumConfig.AmbientOcclusionDebugView = debugViewBefore;
            OptimumTemporal.Frame.JitterActive = false;
            typeof(SystemRenderOITLayers).GetField("revealTextureId", HiddenStatic)!.SetValue(null, oitRevealBefore);
            typeof(SystemRenderOITLayers).GetField("accumTextureId", HiddenStatic)!.SetValue(null, oitAccumBefore);
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

        // ------------------------------------------------------------ per-frame state

        /// <summary>
        /// The state the AfterOIT stage leaves for the merge, and the inputs both routes read:
        /// Primary cleared to the seeds, the Transparent target's OIT attachments cleared, the
        /// world draw-buffer mask, and the OIT textures on the units the OIT renderer uses.
        /// </summary>
        public void SeedFrame()
        {
            VulkanDevice seam = Seam;

            seam.BindFramebuffer(Transparent.FboId);
            seam.SetDrawBuffers(Transparent.FboId, 0b111001);
            seam.ClearColor(0, 0.6f, 0.45f, 0.3f, 1f);
            seam.ClearColor(3, 0.30f, 0.10f, 0.05f, 0.5f);
            seam.ClearColor(4, 0.10f, 0.25f, 0.05f, 0.35f);
            seam.ClearColor(5, 0.05f, 0.10f, 0.30f, 0.2f);

            SeedPostTargets();

            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, 0b111);
            if (!PatternedScene) seam.ClearColor(0, 0.25f, 0.5f, 0.75f, 1f);
            seam.ClearColor(1, 0.125f, 0.25f, 0.375f, 1f);
            seam.ClearColor(2, 0f, 0f, 0.125f, 0.5f);
            seam.ClearDepth(1f);
            if (PatternedScene) DrawScenePattern();
            // Primary's default colour set: two attachments without the SSAO G-buffer.
            seam.SetDrawBuffers(Primary.FboId, 0b011);

            seam.SetViewport(0, 0, Size, Size);
            seam.SetBlend(true, EnumBlendMode.Standard);
            seam.SetDepthTest(false);
            seam.SetDepthMask(true);
            seam.SetCullFace(false);

            // The units SystemRenderOITLayers points the merge's two OIT samplers at.
            ShaderProgramTransparentcompose compose = ShaderPrograms.Transparentcompose;
            seam.SetSamplerUnit(compose.ProgramId, "OITreveal", 6);
            seam.SetSamplerUnit(compose.ProgramId, "OITaccumulation", 7);
            seam.BindTexture(6, oitReveal);
            seam.BindTexture(7, oitAccumulation);
        }

        /// <summary>The post chain's targets, indexed by their EnumFrameBuffer slot.</summary>
        private static readonly int[] PostTargetIndices = { 2, 3, 4, 7, 8, 9, 10, 14 };

        /// <summary>
        /// Every post target seeded to its own constant: the final composition samples the bloom
        /// and god-ray targets whether or not their passes ran this frame, so both routes have to
        /// start a run from identical contents. The TAA history is NOT seeded here - a frame of
        /// the chain must be able to run after another one and find the history the previous
        /// frame wrote, which is what the accumulation test measures. Seed it with
        /// <see cref="SeedHistory" /> where a run needs a known starting history.
        /// </summary>
        private void SeedPostTargets()
        {
            VulkanDevice seam = Seam;
            for (int i = 0; i < PostTargetIndices.Length; i++)
            {
                FrameBufferRef target = buffers[PostTargetIndices[i]];
                if (target == null) continue;
                float level = 0.08f + i * 0.09f;
                seam.BindFramebuffer(target.FboId);
                seam.SetDrawBuffers(target.FboId, 0b1);
                seam.ClearColor(0, level, 1f - level, level * 0.5f, 1f);
            }
        }

        /// <summary>
        /// The TAA resolve's history slot selection put back to its starting state, so two
        /// differential runs of the same chain resolve into the same slot from the same history.
        /// </summary>
        public void ResetTaaHistory()
        {
            typeof(ClientPlatformWindows).GetField("_taaFrameParity", Hidden)!.SetValue(Platform, 0);
            typeof(ClientPlatformWindows).GetField("_taaHistoryValid", Hidden)!.SetValue(Platform, false);
        }

        /// <summary>
        /// The per-frame post switches window_RenderFrame computes, and the render scale: private
        /// fields of ClientPlatformWindows, which is where the chain reads them from on both
        /// routes (through OptimumRenderBloom and friends on the native one).
        /// </summary>
        public void ApplyPostSettings(bool bloom, bool godRays, bool fxaa, bool ssao, float ssaa, int clientSize)
        {
            SetField("RenderBloom", bloom);
            SetField("RenderGodRays", godRays);
            SetField("RenderFXAA", fxaa);
            SetField("RenderSSAO", ssao);
            SetField("ssaaLevel", ssaa);
            Platform.ClientSize = new Size2i(clientSize, clientSize);
        }

        /// <summary>The two AO fields the final composition reads, as the AO step would have left them.</summary>
        public void ApplyAmbientOcclusionState(bool inScene, int platformTexture)
        {
            SetField("optimumSsaoInScene", inScene);
            SetField("optimumAmbientOcclusionTexture", platformTexture);
        }

        /// <summary>The blurred vanilla-SSAO target the final composition binds when AO is on.</summary>
        public int SsaoBlurTexture => buffers[14].ColorTextureIds[0];

        private void SetField(string name, object value) =>
            typeof(ClientPlatformWindows).GetField(name, Hidden)!.SetValue(Platform, value);

        /// <summary>
        /// The scene pattern into Primary's colour 0, the same every frame: a clear can only
        /// write a flat image, and a flat image collapses the resolve's variance clip box.
        /// </summary>
        private void DrawScenePattern()
        {
            VulkanDevice seam = Seam;
            seam.SetDrawBuffers(Primary.FboId, 0b001);
            seam.UseProgram(decodeProgram);
            seam.SetSamplerUnit(decodeProgram, "source", 15);
            seam.BindTexture(15, scenePattern);
            SetInt(seam, decodeProgram, "motionMode", 0);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();
        }

        /// <summary>
        /// Both history slots to a flat seed, so a resolve starts from known content on either
        /// route. Inside a frame: a clear between frames is a no-op on this seam.
        /// </summary>
        public void SeedHistory()
        {
            VulkanDevice seam = Seam;
            for (int parity = 0; parity < 2; parity++)
            {
                FrameBufferRef slot = History(parity);
                seam.BindFramebuffer(slot.FboId);
                seam.SetDrawBuffers(slot.FboId, 0b111);
                seam.ClearColor(0, 0.9f, 0.1f, 0.4f, 1f);
                seam.ClearColor(1, 0.4f, 0.9f, 0.1f, 1f);
                seam.ClearColor(2, 12.5f, 0f, 0f, 1f);
            }
            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, 0b011);
        }

        /// <summary>The sharpen target to a flat seed, so "the pass wrote something" is checkable.</summary>
        public void SeedSharpen()
        {
            VulkanDevice seam = Seam;
            seam.BindFramebuffer(Sharpen.FboId);
            seam.SetDrawBuffers(Sharpen.FboId, 0b1);
            seam.ClearColor(0, 0.05f, 0.95f, 0.55f, 1f);
            seam.BindFramebuffer(Primary.FboId);
            seam.SetDrawBuffers(Primary.FboId, 0b011);
        }

        /// <summary>TAA on, with the jitter window open or closed, and no sharpen pass.</summary>
        public void EnableTaa(bool jitterActive)
        {
            OptimumConfig.Taa = true;
            OptimumConfig.TaaSharpness = 0f;
            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            frame.JitterSequencePx.X = 0.25f;
            frame.JitterSequencePx.Y = -0.375f;
            frame.JitterActive = jitterActive;
            AdvanceTemporalFrame();
            AdvanceTemporalFrame();
            frame.JitterActive = jitterActive;
        }

        /// <summary>
        /// One frame of the temporal contract with the jitter pinned to <paramref name="phase" />
        /// of <see cref="JitterPhases" />, so two runs of the same length are comparable whatever
        /// the global frame counter is at.
        /// </summary>
        public void AdvanceTemporalFrame(int phase)
        {
            AdvanceTemporalFrame();
            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            (float x, float y) = JitterPhases[phase % JitterPhases.Length];
            frame.JitterSequencePx.X = x;
            frame.JitterSequencePx.Y = y;
            // The setter is what copies the sequence into the applied jitter.
            frame.JitterActive = true;
        }

        /// <summary>
        /// One frame of the temporal contract: the camera and the projection captured, so the
        /// sky-motion and resolve passes have a previous view to reproject through.
        /// </summary>
        public void AdvanceTemporalFrame()
        {
            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            bool jitter = frame.JitterActive;
            frame.Advance(16f, Size, Size, 1f, 0.1f, 100f, 70f, uniforms);
            double[] projection = Mat4d.Perspective(Mat4d.Create(), 70.0 * Math.PI / 180.0, 1.0, 0.1, 100.0);
            double[] view = Mat4d.Identity(Mat4d.Create());
            frame.RecordProjection(EnumTemporalView.World, projection);
            frame.CaptureCamera(view, view);
            frame.JitterActive = jitter;
        }

        // ---------------------------------------------------------------- readback

        public byte[] ReadScene() => ReadAttachmentZero(Primary.FboId, Size, Size);

        public byte[] ReadGlow() => Decode(Primary.ColorTextureIds[1], Size, Size, motion: false);

        public byte[] ReadMotion() => Decode(Primary.ColorTextureIds[2], Size, Size, motion: true);

        public byte[] ReadHistoryColor(int parity) => Decode(History(parity).ColorTextureIds[0], Size, Size, motion: false);

        public byte[] ReadHistoryGlow(int parity) => Decode(History(parity).ColorTextureIds[1], Size, Size, motion: false);

        /// <summary>
        /// The history's linear depth, through the motion decode: it is an R32F in view-space
        /// metres, which the clamping colour decode would flatten to white everywhere.
        /// </summary>
        public byte[] ReadHistoryDepth(int parity) => Decode(History(parity).ColorTextureIds[2], Size, Size, motion: true);

        public byte[] ReadSharpen() => Decode(Sharpen.ColorTextureIds[0], Size, Size, motion: false);

        /// <summary>One post target's colour 0, decoded at that target's own size.</summary>
        public byte[] ReadPostTarget(int index)
        {
            FrameBufferRef target = buffers[index];
            return Decode(target.ColorTextureIds[0], target.Width, target.Height, motion: false);
        }

        private unsafe byte[] ReadAttachmentZero(int framebufferId, int width, int height)
        {
            var pixels = new byte[width * height * 4];
            fixed (byte* destination = pixels)
            {
                Seam.BindFramebuffer(framebufferId);
                Seam.ReadDefaultFramebuffer(0, 0, width, height, (IntPtr)destination);
            }
            return pixels;
        }

        /// <summary>
        /// Any attachment through an RGBA8 copy of its own size, because the seam's readback is
        /// four bytes per pixel from attachment 0. The motion mode encodes the vector into the two
        /// low channels so a difference in it cannot hide behind a clamp.
        /// </summary>
        private byte[] Decode(int textureId, int width, int height, bool motion)
        {
            VulkanDevice seam = Seam;
            int framebuffer = DecodeFramebuffer(width, height);
            seam.BindFramebuffer(framebuffer);
            seam.ClearColor(0, 0f, 0f, 0f, 1f);
            seam.UseProgram(decodeProgram);
            seam.SetSamplerUnit(decodeProgram, "source", 15);
            seam.BindTexture(15, textureId);
            SetInt(seam, decodeProgram, "motionMode", motion ? 1 : 0);
            seam.SetViewport(0, 0, width, height);
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();
            return ReadAttachmentZero(framebuffer, width, height);
        }

        /// <summary>The RGBA8 decode target of one size, made once.</summary>
        private int DecodeFramebuffer(int width, int height)
        {
            if (width == Size && height == Size) return decodeFramebuffer;
            if (decodeFramebuffers.TryGetValue((width, height), out int existing)) return existing;

            int texture = Seam.CreateTexture2D(width, height, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = Seam.CreateFramebuffer(width, height);
            Seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            Seam.SetDrawBuffers(framebuffer, 0b1);
            decodeFramebuffers[(width, height)] = framebuffer;
            return framebuffer;
        }

        private static void SetInt(VulkanDevice seam, int program, string name, int value)
        {
            int location = seam.GetUniformLocation(program, name);
            if (location >= 0) seam.SetUniform(program, location, value);
        }

        // ------------------------------------------------------------------- fixture

        private void BuildTargets()
        {
            VulkanDevice seam = Seam;

            Primary = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Texture(EnumTextureInternalFormat.Rgba8),
                    Texture(EnumTextureInternalFormat.Rgba8),
                    Texture(EnumTextureInternalFormat.Rgba16f),
                },
                DepthTextureId = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
                    EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false),
            };
            for (int slot = 0; slot < 3; slot++)
            {
                seam.AttachTexture(Primary.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    Primary.ColorTextureIds[slot], 0);
            }
            seam.AttachTexture(Primary.FboId, EnumFramebufferAttachment.DepthAttachment, Primary.DepthTextureId, 0);
            Assert.True(seam.CheckFramebufferComplete(Primary.FboId, out string primaryStatus), primaryStatus);

            // The Transparent target as the client leaves it once the OIT renderer has replaced
            // attachment 0 with its reveal target and attached the accumulation array's three
            // layers at 3, 4 and 5. ColorTextureIds keeps the vanilla ids, which is what the
            // merge binds as accumulation, revealage and in-glow.
            oitReveal = Texture(EnumTextureInternalFormat.Rgba8);
            oitAccumulation = seam.CreateTexture2DArray(Size, Size, 3,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba);
            Transparent = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Seeded(0.7f),
                    Seeded(0.35f),
                    Seeded(0.2f),
                },
            };
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment0, oitReveal, 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment1,
                Transparent.ColorTextureIds[1], 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment2,
                Transparent.ColorTextureIds[2], 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment3, oitAccumulation, 0);
            seam.AttachTexture(Transparent.FboId, EnumFramebufferAttachment.ColorAttachment4, oitAccumulation, 1);
            seam.AttachTexture(Transparent.FboId, (EnumFramebufferAttachment)36069, oitAccumulation, 2);
            Assert.True(seam.CheckFramebufferComplete(Transparent.FboId, out string status), status);

            for (int i = 0; i <= 24; i++) buffers.Add(null!);
            buffers[0] = Primary;
            buffers[1] = Transparent;
            // The post chain's targets at the sizes and formats SetupDefaultFrameBuffers builds
            // them at: the bloom ping-pongs at half and quarter resolution, find-bright, god rays
            // and Luma at full, and the blurred vanilla-SSAO target the final composition binds.
            buffers[2] = SingleTarget(Size / 2, Size / 2, EnumTextureInternalFormat.Rgba8);
            buffers[3] = SingleTarget(Size / 2, Size / 2, EnumTextureInternalFormat.Rgba8);
            buffers[4] = SingleTarget(Size, Size, EnumTextureInternalFormat.Rgba16f);
            buffers[7] = SingleTarget(Size / 2, Size / 2, EnumTextureInternalFormat.Rgba16f);
            buffers[8] = SingleTarget(Size / 4, Size / 4, EnumTextureInternalFormat.Rgba8);
            buffers[9] = SingleTarget(Size / 4, Size / 4, EnumTextureInternalFormat.Rgba8);
            buffers[10] = SingleTarget(Size, Size, EnumTextureInternalFormat.Rgba16f);
            buffers[14] = SingleTarget(Size, Size, EnumTextureInternalFormat.Rgba8);
            buffers[19] = HistoryTarget();
            buffers[20] = HistoryTarget();
            Sharpen = SingleTarget(Size, Size, EnumTextureInternalFormat.Rgba16f);
            buffers[21] = Sharpen;

            scenePattern = Seeded(0.45f);
            decodeTarget = Texture(EnumTextureInternalFormat.Rgba8);
            decodeFramebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(decodeFramebuffer, EnumFramebufferAttachment.ColorAttachment0, decodeTarget, 0);
            seam.SetDrawBuffers(decodeFramebuffer, 0b1);
        }

        private int Texture(EnumTextureInternalFormat format) =>
            Seam.CreateTexture2D(Size, Size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

        /// <summary>A texture with a per-pixel pattern around <paramref name="level" />, so a difference shows.</summary>
        private unsafe int Seeded(float level)
        {
            var pixels = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = (y * Size + x) * 4;
                    pixels[i] = (byte)Math.Clamp(level * 255f + x * 3, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(level * 255f + y * 5, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(level * 255f + ((x ^ y) & 7) * 9, 0, 255);
                    pixels[i + 3] = (byte)Math.Clamp(level * 255f, 0, 255);
                }
            }
            fixed (byte* data = pixels)
            {
                return Seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
            }
        }

        private FrameBufferRef SingleTarget(int width, int height, EnumTextureInternalFormat format)
        {
            var target = new FrameBufferRef
            {
                Width = width,
                Height = height,
                FboId = Seam.CreateFramebuffer(width, height),
                ColorTextureIds = new[]
                {
                    Seam.CreateTexture2D(width, height, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                },
            };
            Seam.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
            Seam.SetDrawBuffers(target.FboId, 0b1);
            return target;
        }

        /// <summary>A TAA history slot: colour, aux and the linear-depth R32F, as the platform builds them.</summary>
        private FrameBufferRef HistoryTarget()
        {
            var target = new FrameBufferRef
            {
                Width = Size,
                Height = Size,
                FboId = Seam.CreateFramebuffer(Size, Size),
                ColorTextureIds = new[]
                {
                    Texture(EnumTextureInternalFormat.Rgba16f),
                    Texture(EnumTextureInternalFormat.Rgba8),
                    Seam.CreateTexture2DRaw(Size, Size, 0x822E, IntPtr.Zero, 0),
                },
            };
            for (int slot = 0; slot < 3; slot++)
            {
                Seam.AttachTexture(target.FboId,
                    (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                    target.ColorTextureIds[slot], 0);
            }
            Seam.SetDrawBuffers(target.FboId, 0b111);
            return target;
        }

        private void LinkPrograms()
        {
            VulkanDevice seam = Seam;
            ShaderCorpus.ShaderVariant variant = TaaVariant();

            var compose = new ShaderProgramTransparentcompose { PassName = "transparentcompose" };
            Link(seam, compose, "transparentcompose", variant, Array.Empty<string>());
            var skyMotion = new ShaderProgram { PassName = "taa-skymotion" };
            Link(seam, skyMotion, "taa-skymotion", variant, new[]
            {
                "taaRenderSize", "taaJitterPx", "taaInvViewProjJittered", "taaPrevViewProj", "taaCloudReactive",
            });
            var sharpen = new ShaderProgram { PassName = "taa-sharpen" };
            Link(seam, sharpen, "taa-sharpen", variant, new[] { "inputTexelSize", "sharpness" });
            var resolve = new ShaderProgram { PassName = "taa-resolve" };
            Link(seam, resolve, "taa-resolve", variant, new[]
            {
                "renderSize", "jitterPx", "invViewProjJittered", "prevViewProj", "viewMatrix",
                "cameraDelta", "resetHistory", "blendAlpha", "varianceGamma",
            });
            var blit = new ShaderProgramBlit { PassName = "blit" };
            Link(seam, blit, "blit", variant, Array.Empty<string>());

            // The chain's tail: find-bright, the blur ladder, god rays, the FXAA luma prepass and
            // the final composition, with every uniform the OpenGL body sets on them registered
            // so the old route can run too.
            var findbright = new ShaderProgramFindbright { PassName = "findbright" };
            Link(seam, findbright, "findbright", variant, new[] { "ambientBloomLevel", "extraBloom" });
            var blur = new ShaderProgramBlur { PassName = "blur" };
            Link(seam, blur, "blur", variant, new[] { "frameSize", "isVertical" });
            var godrays = new ShaderProgramGodrays { PassName = "godrays" };
            Link(seam, godrays, "godrays", variant, new[]
            {
                "invFrameSizeIn", "maxGodRaySamples", "sunPosScreenIn", "sunPos3dIn",
                "playerViewVector", "dusk", "iGlobalTimeIn",
            });
            var luma = new ShaderProgramLuma { PassName = "luma" };
            Link(seam, luma, "luma", variant, Array.Empty<string>());
            var final = new ShaderProgramFinal { PassName = "final" };
            Link(seam, final, "final", variant, new[]
            {
                "ambientBloomLevel", "optimumSsaoInScene", "optimumAoDebug", "invFrameSizeIn",
                "gammaLevel", "extraGamma", "contrastLevel", "brightnessLevel", "sepiaLevel",
                "windWaveCounter", "glitchEffectStrength", "sunPosScreenIn", "sunPos3dIn",
                "playerViewVector", "damageVignetting", "damageVignettingSide", "frostVignetting",
            });

            ShaderPrograms.Transparentcompose = compose;
            ShaderPrograms.TaaSkyMotion = skyMotion;
            ShaderPrograms.TaaResolve = resolve;
            ShaderPrograms.TaaSharpen = sharpen;
            ShaderPrograms.Blit = blit;
            ShaderPrograms.Findbright = findbright;
            ShaderPrograms.Blur = blur;
            ShaderPrograms.Godrays = godrays;
            ShaderPrograms.Luma = luma;
            ShaderPrograms.Final = final;

            decodeProgram = LinkDecode(seam);
        }

        /// <summary>TAA on without the SSAO G-buffer: the motion attachment at colour 2.</summary>
        internal static ShaderCorpus.ShaderVariant TaaVariant()
        {
            foreach (ShaderCorpus.ShaderVariant candidate in ShaderCorpus.Variants())
            {
                if (candidate.Name == "taa-no-ssao") return candidate;
            }
            throw new InvalidOperationException("the corpus has no taa-no-ssao variant");
        }

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
uniform int motionMode;
layout(location = 0) out vec4 outColor;
void main(void)
{
	vec4 texel = texelFetch(source, ivec2(gl_FragCoord.xy), 0);
	if (motionMode != 0) {
		outColor = vec4(
			clamp(texel.r / 32.0 * 0.5 + 0.5, 0.0, 1.0),
			clamp(texel.g / 32.0 * 0.5 + 0.5, 0.0, 1.0),
			clamp(texel.b, 0.0, 1.0),
			clamp(texel.a, 0.0, 1.0));
		return;
	}
	outColor = clamp(texel, 0.0, 1.0);
}
";
            var linked = new LinkedProgram { PassName = "native-post-decode" };
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

        /// <summary>The platform's frame buffers, motion attachment and TAA readiness, as the setup leaves them.</summary>
        private void InstallState()
        {
            typeof(ClientPlatformWindows).GetField("frameBuffers", Hidden)!.SetValue(Platform, buffers);
            typeof(ClientPlatformWindows).GetField("ssaaLevel", Hidden)!.SetValue(Platform, 1f);
            Platform.SetOptimumMotionAttachmentIndex(2);
            typeof(ClientPlatformWindows).GetField("optimumTaaTargetsReady", Hidden)!.SetValue(Platform, true);

            FieldInfo reveal = typeof(SystemRenderOITLayers).GetField("revealTextureId", HiddenStatic)!;
            FieldInfo accumulation = typeof(SystemRenderOITLayers).GetField("accumTextureId", HiddenStatic)!;
            oitRevealBefore = reveal.GetValue(null);
            oitAccumBefore = accumulation.GetValue(null);
            reveal.SetValue(null, oitReveal);
            accumulation.SetValue(null, oitAccumulation);

            // The seeds the comparisons quote, read once through the same decode the tests use.
            Platform.BeginFrame();
            SeedFrame();
            SeedHistory();
            SeedSharpen();
            SceneSeed = ReadScene();
            GlowSeed = ReadGlow();
            MotionSeed = ReadMotion();
            HistorySeedColor = ReadHistoryColor(0);
            SharpenSeed = ReadSharpen();
            Platform.EndFrame();
        }
    }

    // ---------------------------------------------------------------- native shaders

    /// <summary>The manifest of the programs the chain's two native passes and the resolve use.</summary>
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

            string root = Path.Combine(Path.GetTempPath(), "optimum-native-post-shaders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NativeShaderBuilder.Write(merged, root);
            return (Path.Combine(root, NativeShaderManifest.DirectoryName), "");
        }
    }
}
