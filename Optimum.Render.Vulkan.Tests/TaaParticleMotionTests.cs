using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The TAA P4 particle policies, driven through the seam with the real
/// programs and read back as pixels.
///
/// Two halves, because particles are two different render classes:
///
/// (a) Cube particles are drawn on Primary inside the Opaque stage, blended,
///     with depth writes on, so they write the motion attachment themselves -
///     the camera-only vector (there is no per-particle history in the instance
///     buffer), reactive 1 and their own window depth.
/// (b) Quad particles - and every other OIT transparent - cannot: their six
///     oit.fsh outputs fill the Transparent target. They get their reactive
///     value from the OIT merge instead, which adds `1 - revealage` into the
///     motion attachment's blue channel while leaving the vector and the writer
///     depth of the surface underneath untouched.
///
/// The projection is the perspective-SHAPED matrix the liquid tests introduced
/// rather than the identity the P3 writer tests use: <c>clip.w = -z_view</c> is
/// what makes the jitter shear (<c>P[8] -= 2*jx/W</c>) displace the raster
/// position by exactly jx pixels, so the jittered case proves something.
/// </summary>
public class TaaParticleMotionTests
{
    private readonly ITestOutputHelper _output;

    public TaaParticleMotionTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>The particle quad sits on the plane the matrix below maps to window depth 0.5.</summary>
    private const float QuadZ = -1f;

    /// <summary>
    /// A perspective-shaped projection, column-major: x and y pass through,
    /// <c>clip.w = -z</c> and <c>clip.z = -z - 1</c>. At z = -1 that is
    /// clip = (x, y, 0, 1), so NDC z is 0 and the window depth 0.5.
    /// </summary>
    private static readonly float[] Projection =
    {
        1, 0,  0,  0,
        0, 1,  0,  0,
        0, 0, -1, -1,
        0, 0, -1,  0,
    };

    private static readonly float[] Identity =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    private static float[] Jittered(float jitterX, float jitterY)
    {
        float[] sheared = (float[])Projection.Clone();
        sheared[8] -= 2f * jitterX / Size;
        sheared[9] -= 2f * jitterY / Size;
        return sheared;
    }

    // ------------------------------------------------ (a) cube particle writer

    /// <summary>
    /// A camera that did not move produces no motion, reactive 1 - the value
    /// that makes the resolve take this frame's pixel outright - and the
    /// fragment's own window depth, which is what gets the reactive value past
    /// the resolve's writer-depth test in the first place.
    /// </summary>
    [SkippableFact]
    public void ACubeParticleWritesZeroMotionFullReactiveAndItsOwnDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderCubeParticles(device!, 0f, 0f);
            Decoded centre = result.At(Size / 2, Size / 2);

            _output.WriteLine($"still: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"reactive = {centre.Reactive}, writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            Assert.InRange(centre.Reactive, 0.99f, 1.01f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The camera translating by a known amount moves the particle by exactly
    /// that amount in pixels, and the sign is "where the pixel was". The
    /// instance positions are camera-relative and rebased against
    /// EntityPlayer.CameraPos every frame, which is the origin cameraPosDelta is
    /// measured in, so the previous position is position + cameraPosDelta - the
    /// same rule accuracy rule 4 gives a chunk vertex.
    /// </summary>
    [SkippableTheory]
    [InlineData(0.25f, 0f)]
    [InlineData(0f, -0.125f)]
    [InlineData(-0.1875f, 0.0625f)]
    public void ACameraTranslationShowsUpAsTheExactPixelDisplacement(float deltaX, float deltaY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderCubeParticles(device!, deltaX, deltaY);
            Decoded centre = result.At(Size / 2, Size / 2);

            float expectedX = deltaX * 0.5f * Size;
            float expectedY = deltaY * 0.5f * Size;

            _output.WriteLine($"delta ({deltaX}, {deltaY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.Reactive, 0.99f, 1.01f);
        }
    }

    /// <summary>
    /// The jittered case, which no P3 GPU test covered: the projection carries
    /// this frame's sub-pixel shear and the fragment shader is told the same
    /// offset in taaJitterPx. The two have to cancel - the vector describes where
    /// the surface went, not where the sampling grid went - so the result must be
    /// the unjittered one. A writer that forgot the subtraction would be off by
    /// the jitter; one that subtracted it with the wrong sign, by twice that.
    /// </summary>
    [SkippableTheory]
    [InlineData(0.375f, -0.25f)]
    [InlineData(-0.5f, 0.5f)]
    public void TheJitterInTheProjectionAndInTheUniformCancel(float jitterX, float jitterY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float deltaX = 0.25f;
            const float deltaY = -0.125f;

            Result result = RenderCubeParticles(device!, deltaX, deltaY, jitterX, jitterY);
            Decoded centre = result.At(Size / 2, Size / 2);

            float expectedX = deltaX * 0.5f * Size;
            float expectedY = deltaY * 0.5f * Size;

            _output.WriteLine($"jitter ({jitterX}, {jitterY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            // The jitter is a whole decode step (0.25 px) or more, so a missing
            // or wrongly signed subtraction cannot hide inside this tolerance.
            Assert.True(Math.Abs(jitterX) >= 0.25f && Math.Abs(jitterY) >= 0.25f,
                "the jitter chosen is smaller than the decode quantisation");
            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// A pixel no particle covered keeps a zero alpha, which is what makes the
    /// resolve's writer-depth test reject it and use the camera fallback - and
    /// what keeps reactive 1 from leaking onto the whole screen.
    /// </summary>
    [SkippableFact]
    public void PixelsNoParticleCoveredKeepAZeroWriterDepthAndNoReactive()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderCubeParticles(device!, 0.25f, 0f);
            Decoded corner = result.At(2, 2);

            _output.WriteLine($"corner: mv = ({corner.MotionX}, {corner.MotionY}), " +
                              $"reactive = {corner.Reactive}, writerDepth = {corner.WriterDepth}");
            Assert.InRange(corner.WriterDepth, 0f, 0.01f);
            Assert.InRange(corner.Reactive, 0f, 0.01f);
        }
    }

    /// <summary>
    /// The writer is an addition, not a replacement: the particle still shades
    /// into colour attachment 0. A shader that lost its colour output - or a
    /// draw-buffer window that masked it out, which is what the liquid velocity
    /// pass deliberately does - would leave the clear value behind.
    /// </summary>
    [SkippableFact]
    public void TheCubeParticleStillShadesIntoColourAttachmentZero()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderCubeParticles(device!, 0f, 0f);

            int offset = ((Size / 2) * Size + Size / 2) * 4;
            byte[] cleared = { 51, 102, 153, 255 };   // the (0.2, 0.4, 0.6, 1) clear
            bool changed = false;
            for (int channel = 0; channel < 4; channel++)
            {
                if (Math.Abs(result.Colour[offset + channel] - cleared[channel]) > 1) changed = true;
            }

            _output.WriteLine("centre colour: " +
                              string.Join(", ", Enumerable.Range(0, 4).Select(c => result.Colour[offset + c])));
            Assert.True(changed, "the particle wrote no colour: the motion output replaced the shading path");
        }
    }

    // ------------------------------------------- (b) OIT merge reactive

    /// <summary>
    /// The merge's whole contract in one readback: where transparent content
    /// covers the pixel the reactive channel becomes 1 - revealage, and where it
    /// does not the motion attachment is left exactly as the opaque writer left
    /// it. The vector and the writer depth survive in both cases - they are what
    /// the resolve reprojects the opaque surface behind the transparency with.
    /// </summary>
    [SkippableTheory]
    // revealage, expected reactive contributed by the merge
    [InlineData(1.0f, 0f)]
    [InlineData(0.25f, 0.75f)]
    [InlineData(0f, 1f)]
    public void TheMergeAddsOneMinusRevealageIntoTheReactiveChannelOnly(
        float revealage, float expectedReactive)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            // What an opaque writer left in the attachment before the merge ran.
            const float seedMotionX = 4f;
            const float seedMotionY = -8f;
            const float seedReactive = 0f;
            const float seedDepth = 0.5f;

            Result result = RenderTransparentCompose(
                device!, revealage, seedMotionX, seedMotionY, seedReactive, seedDepth);
            Decoded centre = result.At(Size / 2, Size / 2);

            _output.WriteLine($"revealage {revealage}: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"reactive = {centre.Reactive}, writerDepth = {centre.WriterDepth}");

            // rg and a are the opaque writer's, untouched.
            Assert.InRange(centre.MotionX, seedMotionX - 0.3f, seedMotionX + 0.3f);
            Assert.InRange(centre.MotionY, seedMotionY - 0.3f, seedMotionY + 0.3f);
            Assert.InRange(centre.WriterDepth, seedDepth - 0.01f, seedDepth + 0.01f);

            // b is the transparent layer's coverage.
            Assert.InRange(centre.Reactive, expectedReactive - 0.02f, expectedReactive + 0.02f);
        }
    }

    /// <summary>
    /// Additive rather than replacing: a surface that already declared itself
    /// reactive stays reactive after the merge, whatever the transparency over
    /// it says. (The resolve clamps the sum, so "more than 1" only ever means
    /// "distrust the history", which both contributors were asking for.)
    /// </summary>
    [SkippableFact]
    public void TheMergeDoesNotClearAnExistingReactiveValue()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            // A fully opaque pixel under fully clear glass: revealage 1, so the
            // merge contributes nothing, and the writer's own reactive survives.
            Result result = RenderTransparentCompose(device!, 1f, 2f, 2f, 1f, 0.5f);
            Decoded centre = result.At(Size / 2, Size / 2);

            _output.WriteLine($"existing reactive kept: {centre.Reactive}");
            Assert.InRange(centre.Reactive, 0.99f, 1.01f);
        }
    }

    // ---------------------------------------------------------------- harness

    private readonly struct Decoded
    {
        public Decoded(float motionX, float motionY, float reactive, float writerDepth)
        {
            MotionX = motionX;
            MotionY = motionY;
            Reactive = reactive;
            WriterDepth = writerDepth;
        }

        public float MotionX { get; }
        public float MotionY { get; }
        public float Reactive { get; }
        public float WriterDepth { get; }
    }

    private sealed class Result
    {
        public byte[] Motion = Array.Empty<byte>();
        public byte[] Reactive = Array.Empty<byte>();
        public byte[] Colour = Array.Empty<byte>();

        public Decoded At(int x, int y)
        {
            int offset = (y * Size + x) * 4;
            return new Decoded(
                (Motion[offset] / 255f * 2f - 1f) * DecodeScale,
                (Motion[offset + 1] / 255f * 2f - 1f) * DecodeScale,
                Reactive[offset] / 255f,
                Motion[offset + 2] / 255f);
        }
    }

    /// <summary>
    /// Draws one instanced cube particle with the real particlescube program
    /// into a Primary stand-in whose motion attachment is enabled - what
    /// SystemRenderParticles does through BeginMotionWrite - and returns the
    /// decoded motion attachment together with colour attachment 0.
    /// </summary>
    private unsafe Result RenderCubeParticles(
        VulkanDevice device,
        float cameraDeltaX,
        float cameraDeltaY,
        float jitterX = 0f,
        float jitterY = 0f)
    {
        IOptimumGraphicsDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First(v => v.Name == "taa-no-ssao");
        Assert.Equal(1, variant.TaaMotion);
        Assert.Equal(2, variant.TaaMotionLocation);

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("particlescube", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "particlescube");

        // The writer only exists if the shader really declares it; without this
        // the test would pass on a shader that dropped the output entirely.
        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            "particlescube declares no taaRenderSize, so it is not a motion writer");

        (int scene, int motion) = CreatePrimaryStandIn(seam, out int colour);

        int mesh = seam.CreateMesh(BuildParticleCube(), staticDraw: true);
        Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(scene);
        seam.SetDrawBuffers(scene, 0b111);
        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        seam.UseProgram(program);
        SetMatrix(seam, program, "projectionMatrix", Jittered(jitterX, jitterY));
        SetMatrix(seam, program, "modelViewMatrix", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixFar", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixNear", Identity);
        // The previous projection is the UNJITTERED one, as the frame contract
        // hands it out: a previous position through a jittered matrix would carry
        // two frames' jitter difference instead of the surface's movement.
        SetMatrix(seam, program, "prevProjectionMatrix", Projection);
        SetMatrix(seam, program, "prevModelViewMatrix", Identity);
        SetFloat3(seam, program, "cameraPosDelta", cameraDeltaX, cameraDeltaY, 0f);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", jitterX, jitterY);
        SetFloat3(seam, program, "rgbaAmbientIn", 1f, 1f, 1f);
        SetFloat(seam, program, "fogMinIn", 0f);
        SetFloat(seam, program, "fogDensityIn", 0f);
        SetWarpUniforms(seam, program);

        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(true);
        seam.SetDepthMask(true);
        seam.SetDepthFunc(0x203);   // GL_LEQUAL
        seam.SetCullFace(false);
        // Cube particles are drawn blended (SystemRenderParticles calls
        // GlToggleBlend(on: true)); the motion attachment is forced to replace
        // blending by ApplyOptimumMotionBlendState, which is what this mirrors.
        seam.SetBlend(true, EnumBlendMode.Standard);
        seam.SetBlendFuncSeparate(0, 770, 771, 770, 771);
        seam.SetBlendEquation(2, 32774);
        seam.SetBlendFuncSeparate(2, 1, 0, 1, 0);
        seam.DrawMeshInstanced(mesh, 1);

        byte[] decodedMotion = DecodeMotion(seam, motion, reactive: false);
        byte[] decodedReactive = DecodeMotion(seam, motion, reactive: true);
        seam.BindFramebuffer(scene);
        seam.SetDrawBuffers(scene, 0b111);
        var result = new Result
        {
            Motion = decodedMotion,
            Reactive = decodedReactive,
            Colour = ReadColour(seam, scene),
        };
        seam.Present();

        AssertClean(seam);
        return result;
    }

    /// <summary>
    /// Runs the real transparentcompose program over a motion attachment that
    /// already holds an opaque writer's vector and depth, with the same
    /// per-attachment blend state
    /// ClientPlatformWindows.ApplyOptimumMotionAccumulateBlendState sets.
    /// </summary>
    private unsafe Result RenderTransparentCompose(
        VulkanDevice device,
        float revealage,
        float seedMotionX,
        float seedMotionY,
        float seedReactive,
        float seedDepth)
    {
        IOptimumGraphicsDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First(v => v.Name == "taa-no-ssao");

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("transparentcompose", files, includes, variant);
        Assert.NotEmpty(stages);
        int compose = LinkFromCorpus(seam, stages, "transparentcompose");

        (int scene, int motion) = CreatePrimaryStandIn(seam, out int colour);

        // The inputs the merge samples. Only revealage matters to the reactive
        // value; the rest are present so the pass is the real one.
        int revealageTexture = SolidTexture(seam, revealage, 0f, 0f, 1f);
        int accumulation = SolidTexture(seam, 0f, 0f, 0f, 0f);
        int inGlow = SolidTexture(seam, 0f, 0f, 0f, 1f);
        int oitReveal = SolidTexture(seam, 0f, 0f, 0f, 0f);
        int oitAccumulation = seam.CreateTexture2DArray(Size, Size, 3,
            EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba);

        int quad = FullscreenQuad(seam);

        seam.BeginFrame();
        seam.BindFramebuffer(scene);
        seam.SetDrawBuffers(scene, 0b111);
        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        // The opaque writer's contribution: a motion-only pass, exactly the
        // shape BeginMotionOnlyWrite gives the liquid velocity pass.
        int seedProgram = SeedMotionProgram(seam);
        seam.SetDrawBuffers(scene, 1 << 2);
        seam.UseProgram(seedProgram);
        SetFloat(seam, seedProgram, "seedR", seedMotionX);
        SetFloat(seam, seedProgram, "seedG", seedMotionY);
        SetFloat(seam, seedProgram, "seedB", seedReactive);
        SetFloat(seam, seedProgram, "seedA", seedDepth);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(quad);

        // The merge itself: colour attachments back in the set, the global
        // blend mode MergeTransparentRenderPass sets, and the motion attachment
        // on FUNC_ADD (ONE, ONE) so its rg and a survive.
        seam.SetDrawBuffers(scene, 0b111);
        seam.SetDepthTest(false);
        seam.SetBlend(true, EnumBlendMode.Standard);
        seam.SetBlendFuncSeparate(0, 770, 771, 770, 771);
        seam.SetBlendEquation(2, 32774);
        seam.SetBlendFuncSeparate(2, 1, 1, 1, 1);

        seam.UseProgram(compose);
        seam.SetSamplerUnit(compose, "revealage", 10);
        seam.BindTexture(10, revealageTexture);
        seam.SetSamplerUnit(compose, "accumulation", 11);
        seam.BindTexture(11, accumulation);
        seam.SetSamplerUnit(compose, "inGlow", 12);
        seam.BindTexture(12, inGlow);
        seam.SetSamplerUnit(compose, "OITreveal", 13);
        seam.BindTexture(13, oitReveal);
        seam.SetSamplerUnit(compose, "OITaccumulation", 14);
        seam.BindTexture(14, oitAccumulation);
        seam.DrawMesh(quad);

        byte[] decodedMotion = DecodeMotion(seam, motion, reactive: false);
        byte[] decodedReactive = DecodeMotion(seam, motion, reactive: true);
        seam.BindFramebuffer(scene);
        seam.SetDrawBuffers(scene, 0b111);
        var result = new Result
        {
            Motion = decodedMotion,
            Reactive = decodedReactive,
            Colour = ReadColour(seam, scene),
        };
        seam.Present();

        AssertClean(seam);
        return result;
    }

    /// <summary>Colour, glow and an RGBA16F motion attachment at index 2 - what
    /// SetupDefaultFrameBuffers builds without the SSAO G-buffer.</summary>
    private static (int Scene, int Motion) CreatePrimaryStandIn(IOptimumGraphicsDevice seam, out int colour)
    {
        colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int glow = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int motion = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int depth = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
            EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMinFilter, 9728);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMagFilter, 9728);
        seam.SetTextureParameter(colour, OptimumGlConstants.TextureMinFilter, 9728);
        seam.SetTextureParameter(colour, OptimumGlConstants.TextureMagFilter, 9728);

        int scene = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment2, motion, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        Assert.True(seam.CheckFramebufferComplete(scene, out string status), status);
        return (scene, motion);
    }

    private static unsafe int SolidTexture(
        IOptimumGraphicsDevice seam, float r, float g, float b, float a)
    {
        var pixels = new byte[Size * Size * 4];
        byte[] value =
        {
            (byte)Math.Clamp((int)MathF.Round(r * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(g * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(b * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255),
        };
        for (int i = 0; i < pixels.Length; i += 4) Array.Copy(value, 0, pixels, i, 4);

        fixed (byte* source = pixels)
        {
            int texture = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, (IntPtr)source, false);
            seam.SetTextureParameter(texture, OptimumGlConstants.TextureMinFilter, 9728);
            seam.SetTextureParameter(texture, OptimumGlConstants.TextureMagFilter, 9728);
            return texture;
        }
    }

    /// <summary>The opaque writer's stand-in: writes the motion attachment and
    /// nothing else, the way chunkliquidmotion does.</summary>
    private static int SeedMotionProgram(IOptimumGraphicsDevice seam)
    {
        const string vertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string fragment = @"#version 330 core
uniform float seedR;
uniform float seedG;
uniform float seedB;
uniform float seedA;
layout(location = 2) out vec4 outMotion;
void main(void) { outMotion = vec4(seedR, seedG, seedB, seedA); }
";
        return LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = vertex, PrefixCode = "", Filename = "taa-particle-seed.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = fragment, PrefixCode = "", Filename = "taa-particle-seed.fsh" },
        }, "taa-particle-seed");
    }

    private static int FullscreenQuad(IOptimumGraphicsDevice seam)
    {
        var quad = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
        };
        int mesh = seam.CreateMesh(quad, staticDraw: true);
        Assert.True(mesh > 0, seam.GetError() ?? "quad upload failed");
        return mesh;
    }

    /// <summary>Colour attachment 0 of the scene target, read inside the frame.</summary>
    private static unsafe byte[] ReadColour(IOptimumGraphicsDevice seam, int scene)
    {
        var pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(scene);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    /// <summary>
    /// Reads the RGBA16F motion attachment through an RGBA8 decode pass, because
    /// the seam's readback is fixed at four bytes per pixel from attachment 0.
    /// With <paramref name="reactive" /> the blue channel is put in red at full
    /// scale, so reactive can be checked without the mv quantisation.
    /// </summary>
    private unsafe byte[] DecodeMotion(IOptimumGraphicsDevice seam, int motionTexture, bool reactive)
    {
        const string decodeVertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string decodeFragment = @"#version 330 core
uniform sampler2D motionTex;
uniform float decodeScale;
uniform int reactiveOnly;
layout(location = 0) out vec4 outColor;
void main(void)
{
	vec4 m = texelFetch(motionTex, ivec2(gl_FragCoord.xy), 0);
	if (reactiveOnly != 0) {
		outColor = vec4(clamp(m.b, 0.0, 1.0), 0.0, 0.0, 1.0);
		return;
	}
	outColor = vec4(
		clamp(m.r / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.g / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.a, 0.0, 1.0),
		1.0);
}
";
        int decode = LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-particle-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-particle-decode.fsh" },
        }, "taa-particle-decode");

        int quadMesh = FullscreenQuad(seam);

        int target = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
        seam.SetDrawBuffers(framebuffer, 0b1);

        seam.BindFramebuffer(framebuffer);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.UseProgram(decode);
        seam.SetSamplerUnit(decode, "motionTex", 15);
        seam.BindTexture(15, motionTexture);
        SetFloat(seam, decode, "decodeScale", DecodeScale);
        SetInt(seam, decode, "reactiveOnly", reactive ? 1 : 0);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(quadMesh);

        var pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    /// <summary>
    /// One cube particle with the attribute layout ParticlePoolQuads allocates:
    /// xyz(0), normals(1), uv(2), flags(3) - the model has no rgba - then the
    /// instanced custom floats as particlePosition(4) and scale(5), and the
    /// instanced custom bytes as particleDir(6), rgbaLightIn(7) and
    /// rgbaBlockIn(8).
    ///
    /// The "cube" is one quad on the z = -1 plane, which is all the writer needs
    /// to cover the middle of the target.
    /// </summary>
    private static MeshData BuildParticleCube()
    {
        var mesh = new MeshData(4, 6, withNormals: true, withUv: true, withRgba: false, withFlags: true);

        mesh.xyz = new[]
        {
            -0.5f, -0.5f, 0f,
             0.5f, -0.5f, 0f,
             0.5f,  0.5f, 0f,
            -0.5f,  0.5f, 0f,
        };
        mesh.Uv = new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
        // The packed normal is only read for lighting and the SSAO G-normal,
        // never for the position or the motion vector.
        mesh.Normals = new int[4];
        mesh.NormalsCount = 4;
        mesh.VerticesCount = 4;

        mesh.Indices = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.IndicesCount = 6;

        // particlePosition (3) + scale (1), stride 16 - ParticlePoolQuads' own
        // CustomFloats shape. The particle sits on the plane the projection maps
        // to window depth 0.5.
        mesh.CustomFloats = new CustomMeshDataPartFloat
        {
            Instanced = true,
            StaticDraw = false,
            Values = new[] { 0f, 0f, QuadZ, 1f },
            Count = 4,
            InterleaveSizes = new[] { 3, 1 },
            InterleaveOffsets = new[] { 0, 12 },
            InterleaveStride = 16,
        };

        // particleDir (4) + rgbaLightIn (4) + rgbaBlockIn (4), normalized bytes.
        var bytes = new byte[12];
        for (int i = 0; i < 12; i++) bytes[i] = 255;
        mesh.CustomBytes = new CustomMeshDataPartByte
        {
            Conversion = DataConversion.NormalizedFloat,
            Instanced = true,
            StaticDraw = false,
            Values = bytes,
            Count = 12,
            InterleaveSizes = new[] { 4, 4, 4 },
            InterleaveOffsets = new[] { 0, 4, 8 },
            InterleaveStride = 12,
        };

        // renderFlags is per instance, as ParticlePoolQuads uploads it. The
        // array is vertex-count long because MeshData derives FlagsCount from
        // VerticesCount; only the first entry is read, for the one instance.
        mesh.Flags = new int[4];
        mesh.FlagsInstanced = true;

        return mesh;
    }

    /// <summary>
    /// Both halves of the warp state, set explicitly: an unset uniform is a
    /// defined zero in GL but whatever the block happens to hold on the device
    /// path. Every intensity is zero, so neither the current nor the previous
    /// position is warped and the motion is the camera's alone.
    /// </summary>
    private static void SetWarpUniforms(IOptimumGraphicsDevice seam, int program)
    {
        SetFloat(seam, program, "timeCounter", 0f);
        SetFloat(seam, program, "windWaveCounter", 0f);
        SetFloat(seam, program, "windWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "waterWaveCounter", 0f);
        SetFloat(seam, program, "windSpeed", 0f);
        SetFloat(seam, program, "globalWarpIntensity", 0f);
        SetFloat(seam, program, "glitchWaviness", 0f);
        SetFloat(seam, program, "windWaveIntensity", 0f);
        SetFloat(seam, program, "waterWaveIntensity", 0f);
        SetInt(seam, program, "perceptionEffectId", 1);
        SetFloat(seam, program, "perceptionEffectIntensity", 0f);
        SetFloat3(seam, program, "playerpos", 0f, 0f, 0f);

        SetFloat(seam, program, "prevTimeCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "prevWaterWaveCounter", 0f);
        SetFloat(seam, program, "prevWindSpeed", 0f);
        SetFloat(seam, program, "prevGlobalWarpIntensity", 0f);
        SetFloat(seam, program, "prevGlitchWaviness", 0f);
        SetFloat(seam, program, "prevWindWaveIntensity", 0f);
        SetFloat(seam, program, "prevWaterWaveIntensity", 0f);
        SetInt(seam, program, "prevPerceptionEffectId", 1);
        SetFloat(seam, program, "prevPerceptionEffectIntensity", 0f);
        SetFloat3(seam, program, "prevPlayerpos", 0f, 0f, 0f);
    }

    private static void SetFloat(IOptimumGraphicsDevice seam, int program, string name, float value)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, value);
    }

    private static void SetInt(IOptimumGraphicsDevice seam, int program, string name, int value)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, value);
    }

    private static void SetFloat2(IOptimumGraphicsDevice seam, int program, string name, float x, float y)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y);
    }

    private static void SetFloat3(IOptimumGraphicsDevice seam, int program, string name, float x, float y, float z)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y, z);
    }

    private static void SetMatrix(IOptimumGraphicsDevice seam, int program, string name, float[] matrix)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniformMatrix(program, location, matrix);
    }

    private static int LinkFromCorpus(
        IOptimumGraphicsDevice seam, List<ShaderStageSource> stages, string name)
    {
        var program = new CorpusProgram { PassName = name };

        foreach (ShaderStageSource stage in stages)
        {
            var shader = new CorpusShader
            {
                Type = stage.Stage,
                Code = stage.Code,
                PrefixCode = stage.PrefixCode ?? "",
            };
            Assert.True(seam.CompileShader(shader), name + ": " + (seam.GetError() ?? "compile failed"));

            if (stage.Stage == EnumShaderType.VertexShader) program.VertexShader = shader;
            else if (stage.Stage == EnumShaderType.FragmentShader) program.FragmentShader = shader;
            else program.GeometryShader = shader;
        }

        int programId = seam.LinkProgram(program);
        Assert.True(programId > 0, name + ": " + (seam.GetError() ?? "link failed"));
        return programId;
    }

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device) =>
        GpuTest.TryCreateDevice(output, out device);

    private static void AssertClean(IOptimumGraphicsDevice seam) => GpuTest.AssertClean(seam);

    private sealed class CorpusShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    private sealed class CorpusProgram : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId { get; set; }
        public string PassName { get; set; } = "";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; } = true;
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();
        public bool Compile() => true;
        public bool HasUniform(string uniformName) => false;
        public void Use() { }
        public void Stop() { }
        public void Dispose() { }
        public void Uniform(string uniformName, float value) { }
        public void Uniform(string uniformName, int value) { }
        public void Uniform(string uniformName, Vec2f value) { }
        public void Uniform(string uniformName, Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
    }
}
