using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The liquid velocity pass (TAA P4, TAA-PLAN.md accuracy rule 7), driven
/// through the seam with the real chunkliquidmotion program and read back as
/// pixels.
///
/// The pass exists because the OIT liquid draw cannot write Primary's motion
/// attachment, so the liquid pools are drawn a second time into Primary by a
/// program that writes NOTHING but that attachment. Two things therefore have to
/// hold at once: the vector, reactive value and writer depth must be right, and
/// the shaded image must come out of the pass byte for byte as it went in. Both
/// are asserted here.
///
/// The projection is a minimal perspective-shaped matrix rather than the
/// identity the P3 writer tests use: <c>clip.w = -z_view</c> is what makes the
/// jitter shear (<c>P[8] -= 2*jx/W</c>) displace the raster position by exactly
/// jx pixels, and the jittered case is one of the things P3 never covered.
///
/// The motion attachment is RGBA16F and <c>ReadDefaultFramebuffer</c> reads four
/// bytes per pixel from colour attachment 0, so the values come back through a
/// second fullscreen pass that decodes them into an RGBA8 target. That is a
/// readback detail, not part of the contract.
/// </summary>
public class TaaLiquidMotionTests
{
    private readonly ITestOutputHelper _output;

    public TaaLiquidMotionTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>The value ChunkRenderer stamps into taaLiquidReactive.</summary>
    private const float LiquidReactive = 0.3f;

    /// <summary>
    /// The quad sits one unit in front of the camera, on the plane the matrix
    /// below maps to window depth 0.5.
    /// </summary>
    private const float QuadZ = -1f;

    /// <summary>
    /// chunkliquid.vsh's "pretend the surface is closer" w-offset:
    /// <c>gl_Position.w += 0.0008 / max(0.1, gl_Position.z)</c>. With the matrix
    /// below every vertex of the quad has clip.z = 0 and clip.w = 1, so the
    /// offset is a constant 0.008 on both the current and the previous clip
    /// position - which divides both projected positions, and therefore the
    /// motion vector, by this factor.
    /// </summary>
    private const float WOffsetFactor = 1.008f;

    /// <summary>
    /// A perspective-shaped projection, column-major: x and y pass through,
    /// <c>clip.w = -z</c> and <c>clip.z = -z - 1</c>. At the quad's z = -1 that
    /// is clip = (x, y, 0, 1), so NDC z is 0 and the window depth 0.5 - the same
    /// value the identity matrix gives the P3 writer tests, but with the w that
    /// makes a jitter shear behave the way it does in the game.
    /// </summary>
    private static readonly float[] Projection =
    {
        1, 0,  0,  0,
        0, 1,  0,  0,
        0, 0, -1, -1,
        0, 0, -1,  0,
    };

    private static float[] Jittered(float jitterX, float jitterY)
    {
        float[] sheared = (float[])Projection.Clone();
        sheared[8] -= 2f * jitterX / Size;
        sheared[9] -= 2f * jitterY / Size;
        return sheared;
    }

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// A camera that did not move produces no motion, the reactive value the
    /// renderer set, and the fragment's own window depth - which is the whole
    /// point of the pass: without a matching writer depth the resolve rejects
    /// the vector and camera-reprojects the water instead.
    /// </summary>
    [SkippableFact]
    public void AStillCameraWritesZeroMotionTheReactiveValueAndTheFragmentDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderLiquidMotion(device!, 0f, 0f);
            Decoded centre = result.At(Size / 2, Size / 2);

            _output.WriteLine($"still: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"reactive = {centre.Reactive}, writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            Assert.InRange(centre.Reactive, LiquidReactive - 0.01f, LiquidReactive + 0.01f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The camera translating by a known amount moves the water surface by
    /// exactly that amount in pixels, and the sign is "where the pixel was".
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
            Result result = RenderLiquidMotion(device!, deltaX, deltaY);
            Decoded centre = result.At(Size / 2, Size / 2);

            // prevRel = truePos + cameraPosDelta with both matrices as above, so
            // the previous clip position differs by exactly the delta and the
            // pixel difference is delta * 0.5 * Size, divided by the w-offset
            // both positions carry.
            float expectedX = deltaX * 0.5f * Size / WOffsetFactor;
            float expectedY = deltaY * 0.5f * Size / WOffsetFactor;

            _output.WriteLine($"delta ({deltaX}, {deltaY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.Reactive, LiquidReactive - 0.01f, LiquidReactive + 0.01f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The jittered case, which no P3 GPU test covered: the projection carries
    /// this frame's sub-pixel shear and the fragment shader is told the same
    /// offset in taaJitterPx. The two have to cancel - the vector describes where
    /// the surface went, not where the sampling grid went - so the result must be
    /// the unjittered result, not one displaced by the jitter.
    ///
    /// A writer that forgot the subtraction would be off by the jitter (up to
    /// half a pixel, which is the entire signal TAA is resolving); one that
    /// subtracted it with the wrong sign would be off by twice that.
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

            Result result = RenderLiquidMotion(device!, deltaX, deltaY, jitterX, jitterY);
            Decoded centre = result.At(Size / 2, Size / 2);

            float expectedX = deltaX * 0.5f * Size / WOffsetFactor;
            float expectedY = deltaY * 0.5f * Size / WOffsetFactor;

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
    /// The pass re-draws geometry that has already been shaded into Primary
    /// through the OIT merge, so the one thing it must not do is touch the
    /// image. The draw-buffer mask is the motion attachment alone, and colour
    /// attachment 0 has to come back holding exactly what it was cleared to -
    /// over the quad as well as beside it.
    /// </summary>
    [SkippableFact]
    public void TheVelocityPassLeavesColourAttachmentZeroUntouched()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderLiquidMotion(device!, 0.25f, 0f);

            // The quad covers the middle of the target and the motion attachment
            // proves the draw really happened there.
            Decoded centre = result.At(Size / 2, Size / 2);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);

            // ClearColour was (0.2, 0.4, 0.6, 1) - see RenderLiquidMotion.
            byte[] expected = { 51, 102, 153, 255 };
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int offset = (y * Size + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    Assert.True(Math.Abs(result.Colour[offset + channel] - expected[channel]) <= 1,
                        $"colour attachment 0 was written at ({x}, {y}) channel {channel}: " +
                        $"{result.Colour[offset + channel]} instead of {expected[channel]}");
                }
            }
        }
    }

    /// <summary>
    /// A pixel no liquid covered keeps a zero alpha, which is what makes the
    /// resolve's writer-depth test reject it and use the camera fallback.
    /// </summary>
    [SkippableFact]
    public void PixelsNoLiquidCoveredKeepAZeroWriterDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderLiquidMotion(device!, 0.25f, 0f);
            Decoded corner = result.At(2, 2);

            _output.WriteLine($"corner: mv = ({corner.MotionX}, {corner.MotionY}), " +
                              $"writerDepth = {corner.WriterDepth}");
            Assert.InRange(corner.WriterDepth, 0f, 0.01f);
        }
    }

    /// <summary>
    /// The liquid wave warp is replayed with the PREVIOUS frame's state, through
    /// the same code that produced this frame's position (the WarpState overload
    /// of applyLiquidWarping in the vertexwarp include).
    ///
    /// The warp is gradient noise, so there is no closed form to compare
    /// against; what is exactly known is its shape. applyLiquidWarpingState only
    /// ever displaces <c>worldPos.y</c>, and the noise it samples varies with
    /// <c>worldPos.x</c>. So with this frame's wave intensity at zero and the
    /// previous frame's at three - and the camera perfectly still - the motion has
    /// to be y-only, non-zero, and different from pixel to pixel across the quad.
    /// A writer that evaluated the warp with the current state instead would
    /// produce exactly zero everywhere, which is the failure this catches.
    /// </summary>
    [SkippableFact]
    public void ThePreviousLiquidWaveIsReplayedThroughTheSameWarp()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            // waterFlagsIn bit 0 = "should animate", which is the branch
            // chunkliquid.vsh takes into applyLiquidWarping with div = 5.
            Result result = RenderLiquidMotion(device!, 0f, 0f, waterFlags: 1, previousWaterWaveIntensity: 3f);

            int row = Size / 2;
            int moved = 0;
            var values = new List<float>();
            // The quad spans NDC [-0.5, 0.5], i.e. pixels 16..48.
            for (int x = 20; x < 44; x++)
            {
                Decoded pixel = result.At(x, row);
                Assert.InRange(pixel.WriterDepth, 0.48f, 0.52f);
                // The warp moves y and only y.
                Assert.InRange(pixel.MotionX, -0.3f, 0.3f);
                values.Add(pixel.MotionY);
                if (Math.Abs(pixel.MotionY) > 0.3f) moved++;
            }

            _output.WriteLine("previous wave, mv.y across the quad: " +
                              string.Join(", ", values.Select(v => v.ToString("0.0"))));

            Assert.True(moved > values.Count / 4,
                "the previous frame's liquid wave produced no vertical motion: " +
                "the warp is not being replayed with the previous state");
            Assert.True(values.Max() - values.Min() > 0.3f,
                "the vertical motion is constant across the quad, so it is not the noise field");
        }
    }

    /// <summary>
    /// A fragment whose previous position ends up BEHIND the previous camera has
    /// no motion vector - the perspective divide would flip it - so the writer
    /// bails out with a zero alpha and lets taa-resolve.fsh camera-reproject the
    /// pixel. What it must NOT drop on that path is the reactive value:
    /// taa-resolve.fsh reads motion.b whether or not the writer-depth test
    /// accepted the pixel (TAA-PLAN.md finding (h)), so a zero there would hand
    /// an animating water surface full history weight in exactly the frames the
    /// camera swung hardest.
    ///
    /// The previous view here mirrors z, so the quad at view z = -1 lands at
    /// z = +1 in the previous frame's view and the previous clip w (= -z) is
    /// negative. Before the P4 review fix this test failed on the reactive
    /// channel alone, with the vector and the depth already correct.
    /// </summary>
    [SkippableFact]
    public void APreviousPositionBehindThePreviousCameraStillCarriesTheReactiveValue()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            float[] mirrorZ =
            {
                1, 0,  0, 0,
                0, 1,  0, 0,
                0, 0, -1, 0,
                0, 0,  0, 1,
            };

            Result result = RenderLiquidMotion(device!, 0f, 0f, previousView: mirrorZ);
            Decoded centre = result.At(Size / 2, Size / 2);

            _output.WriteLine($"behind: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"reactive = {centre.Reactive}, writerDepth = {centre.WriterDepth}");

            // No vector, and the zero alpha that routes the pixel to the camera
            // fallback rather than pretending the writer owns it.
            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            Assert.InRange(centre.WriterDepth, 0f, 0.01f);
            // ... but the reactive value is still delivered.
            Assert.InRange(centre.Reactive, LiquidReactive - 0.01f, LiquidReactive + 0.01f);
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
    /// Draws one liquid quad with the real chunkliquidmotion program, into a
    /// Primary stand-in whose draw-buffer mask is the motion attachment alone -
    /// exactly what ClientPlatformWindows.BeginMotionOnlyWrite does - and returns
    /// the decoded motion attachment together with colour attachment 0.
    /// </summary>
    private unsafe Result RenderLiquidMotion(
        VulkanDevice device,
        float cameraDeltaX,
        float cameraDeltaY,
        float jitterX = 0f,
        float jitterY = 0f,
        int waterFlags = 0,
        float previousWaterWaveIntensity = 0f,
        float[]? previousView = null)
    {
        VulkanDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First(v => v.Name == "taa-no-ssao");
        Assert.Equal(1, variant.TaaMotion);
        Assert.Equal(2, variant.TaaMotionLocation);
        Assert.Equal(1, variant.WavingStuff);

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("chunkliquidmotion", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "chunkliquidmotion");

        // The writer only exists if the shader really declares it; without this
        // the test would pass on a shader that dropped the output entirely.
        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            "chunkliquidmotion declares no taaRenderSize, so it is not a motion writer");

        // Primary stand-in: colour, glow and the motion attachment at index 2,
        // which is where SetupDefaultFrameBuffers puts it without the SSAO
        // G-buffer and what TAAMOTIONLOCATION was stamped with above.
        int colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
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

        int mesh = seam.CreateMesh(BuildLiquidQuad(waterFlags), staticDraw: true);
        Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(scene);

        // Clear with every attachment enabled: ClearColor honours the
        // draw-buffer mask on the device, so a masked-out attachment would keep
        // undefined contents and the "untouched" check below would be vacuous.
        seam.SetDrawBuffers(scene, 0b111);
        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        // The motion-only window: attachment 2 alone, which is what makes the
        // second draw of already-shaded geometry harmless.
        seam.SetDrawBuffers(scene, 1 << 2);

        seam.UseProgram(program);
        SetMatrix(seam, program, "projectionMatrix", Jittered(jitterX, jitterY));
        SetMatrix(seam, program, "modelViewMatrix", Identity);
        // The previous projection is the UNJITTERED one, as the frame contract
        // hands it out: a previous position through a jittered matrix would carry
        // two frames' jitter difference instead of the surface's movement.
        SetMatrix(seam, program, "prevProjectionMatrix", Projection);
        SetMatrix(seam, program, "prevModelViewMatrix", previousView ?? Identity);
        SetFloat3(seam, program, "cameraPosDelta", cameraDeltaX, cameraDeltaY, 0f);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", jitterX, jitterY);
        SetFloat(seam, program, "taaLiquidReactive", LiquidReactive);
        SetWarpUniforms(seam, program, previousWaterWaveIntensity);

        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(true);
        seam.SetDepthMask(true);
        seam.SetDepthFunc(0x203);   // GL_LEQUAL
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(mesh);

        byte[] decodedMotion = DecodeMotion(seam, motion, reactive: false);
        byte[] decodedReactive = DecodeMotion(seam, motion, reactive: true);
        // Back to the full set before reading attachment 0, so the read is not
        // looking at a target whose only enabled attachment is the motion one.
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

    private static readonly float[] Identity =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    /// <summary>Colour attachment 0 of the scene target, read inside the frame.</summary>
    private static unsafe byte[] ReadColour(VulkanDevice seam, int scene)
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
    /// scale, so the 0.3 can be checked without the mv quantisation.
    /// </summary>
    private unsafe byte[] DecodeMotion(VulkanDevice seam, int motionTexture, bool reactive)
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
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-liquid-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-liquid-decode.fsh" },
        }, "taa-liquid-decode");

        var quad = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
        };
        int quadMesh = seam.CreateMesh(quad, staticDraw: true);
        Assert.True(quadMesh > 0, seam.GetError() ?? "decode mesh upload failed");

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
    /// A liquid quad with the attribute layout the liquid pool uses, which is
    /// what puts waterFlagsIn at location 6: xyz(0), uv(1), rgba(2), flags(3),
    /// two custom floats (4, the flow vector) and two custom ints (5 colormap
    /// data, 6 water flags) - the same parts ChunkRenderer allocates for
    /// EnumChunkRenderPass.Liquid.
    /// </summary>
    private static MeshData BuildLiquidQuad(int waterFlags)
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);

        float[] positions =
        {
            -0.5f, -0.5f, QuadZ,
             0.5f, -0.5f, QuadZ,
             0.5f,  0.5f, QuadZ,
            -0.5f,  0.5f, QuadZ,
        };
        float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };

        for (int i = 0; i < 4; i++)
        {
            mesh.AddVertexWithFlags(
                positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                uvs[i * 2], uvs[i * 2 + 1],
                Vintagestory.API.MathTools.ColorUtil.WhiteArgb,
                flags: 0);
        }

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }

        mesh.CustomFloats = new CustomMeshDataPartFloat
        {
            Values = new float[8],
            Count = 8,
            InterleaveOffsets = new int[1],
            InterleaveSizes = new int[1] { 2 },
            InterleaveStride = 8,
        };

        var customInts = new int[8];
        for (int i = 0; i < 4; i++)
        {
            customInts[i * 2] = 0;              // colormap data
            customInts[i * 2 + 1] = waterFlags; // water flags
        }
        mesh.CustomInts = new CustomMeshDataPartInt
        {
            Values = customInts,
            Count = 8,
            InterleaveOffsets = new int[2] { 0, 4 },
            InterleaveSizes = new int[2] { 1, 1 },
            InterleaveStride = 8,
            Conversion = DataConversion.Integer,
        };

        return mesh;
    }

    /// <summary>
    /// Both halves of the warp state. This frame's wave intensities are zero, so
    /// the current position is the plain quad; only the previous frame's water
    /// wave moves anything, and only when the caller asks for it. Set explicitly
    /// rather than left at zero: an unset uniform is a defined zero in GL but
    /// whatever the block happens to hold on the device path.
    /// </summary>
    private static void SetWarpUniforms(
        VulkanDevice seam, int program, float previousWaterWaveIntensity)
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
        SetFloat3(seam, program, "origin", 0f, 0f, 0f);

        SetFloat(seam, program, "prevTimeCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounterHighFreq", 0f);
        // Off a lattice point, so the gradient noise the wave samples is not
        // trivially zero along the quad.
        SetFloat(seam, program, "prevWaterWaveCounter", 1.234f);
        SetFloat(seam, program, "prevWindSpeed", 0f);
        SetFloat(seam, program, "prevGlobalWarpIntensity", 0f);
        SetFloat(seam, program, "prevGlitchWaviness", 0f);
        SetFloat(seam, program, "prevWindWaveIntensity", 0f);
        SetFloat(seam, program, "prevWaterWaveIntensity", previousWaterWaveIntensity);
        SetInt(seam, program, "prevPerceptionEffectId", 1);
        SetFloat(seam, program, "prevPerceptionEffectIntensity", 0f);
        SetFloat3(seam, program, "prevPlayerpos", 0f, 0f, 0f);
    }

    private static void SetFloat(VulkanDevice seam, int program, string name, float value)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, value);
    }

    private static void SetInt(VulkanDevice seam, int program, string name, int value)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, value);
    }

    private static void SetFloat2(VulkanDevice seam, int program, string name, float x, float y)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y);
    }

    private static void SetFloat3(VulkanDevice seam, int program, string name, float x, float y, float z)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y, z);
    }

    private static void SetMatrix(VulkanDevice seam, int program, string name, float[] matrix)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniformMatrix(program, location, matrix);
    }

    private static int LinkFromCorpus(
        VulkanDevice seam, List<ShaderStageSource> stages, string name)
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

    private static void AssertClean(VulkanDevice seam) => GpuTest.AssertClean(seam);

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
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2f value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
    }
}
