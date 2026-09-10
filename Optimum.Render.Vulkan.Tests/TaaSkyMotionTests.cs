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
/// The sky / volumetric-cloud motion pass (TAA P4), driven through the seam with
/// the real taa-skymotion program and read back as pixels.
///
/// The pass is a fullscreen triangle at window depth 1.0 drawn with GL_LEQUAL,
/// so it covers the sky and nothing else, and it writes the motion attachment
/// alone. Four things have to hold and are asserted here:
///
///  1. the vector is the camera-ROTATION-only reprojection of the pixel's view
///     direction - a point on the celestial sphere does not parallax, so the
///     previous view-projection's translation column has to be dropped (the
///     shader multiplies the direction with w = 0);
///  2. the reactive value follows the transparent layer's coverage, read from
///     the Transparent target's revealage attachment - 1 where a cloud fully
///     covers the pixel, 0 on clear sky, which is what keeps the dithered sky
///     gradient converging;
///  3. a pixel some surface already wrote depth for is rejected by the depth
///     test, so the terrain/entity/liquid/particle writers keep their vectors;
///  4. the shaded image is untouched.
///
/// The projection is the same perspective-SHAPED matrix the liquid test uses
/// (<c>clip.w = -z_view</c>), because that is what makes the jitter shear
/// <c>P[8] -= 2*jx/W</c> displace a raster position by exactly jx pixels; the
/// jittered case is covered below.
/// </summary>
public class TaaSkyMotionTests
{
    private readonly ITestOutputHelper _output;

    public TaaSkyMotionTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>The camera yaw, in radians, between the previous frame and this one.</summary>
    private const float Yaw = 0.1f;

    /// <summary>
    /// A perspective-shaped projection, column-major: x and y pass through,
    /// <c>clip.w = -z</c> and <c>clip.z = -z - 1</c>.
    /// </summary>
    private static readonly float[] Projection =
    {
        1, 0,  0,  0,
        0, 1,  0,  0,
        0, 0, -1, -1,
        0, 0, -1,  0,
    };

    /// <summary>
    /// The inverse of <see cref="Projection" /> after the jitter shear, in the
    /// form ClientPlatformWindows.RenderOptimumSkyMotion hands the shader.
    ///
    /// Sheared: <c>clip.x = x - (2jx/S) z</c>, <c>clip.y = y - (2jy/S) z</c>,
    /// <c>clip.z = -z - w</c>, <c>clip.w = -z</c>. Inverting gives
    /// <c>z = -d</c>, <c>w = d - c</c>, <c>x = a - (2jx/S) d</c>,
    /// <c>y = b - (2jy/S) d</c>.
    /// </summary>
    private static float[] InverseJittered(float jitterX, float jitterY) => new[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 0f, -1f,
        -2f * jitterX / Size, -2f * jitterY / Size, -1f, 1f,
    };

    /// <summary>
    /// The previous view-projection: <see cref="Projection" /> times a yaw
    /// rotation of <see cref="Yaw" />. Worked out by hand rather than by a
    /// matrix helper, so the test states the maths the shader has to reproduce
    /// instead of recomputing it the same way.
    /// </summary>
    private static float[] PreviousViewProjection(float yaw)
    {
        float c = MathF.Cos(yaw);
        float s = MathF.Sin(yaw);
        return new[]
        {
            c, 0f, s, s,
            0f, 1f, 0f, 0f,
            s, 0f, -c, -c,
            0f, 0f, -1f, 0f,
        };
    }

    /// <summary>
    /// The contract, restated independently of the shader: the view direction
    /// through the (unjittered-by-the-inverse) raster position, rotated into the
    /// previous camera, projected, and expressed as
    /// previousPixel - currentUnjitteredPixel.
    /// </summary>
    private static (float X, float Y) ExpectedMotion(int pixelX, int pixelY, float yaw, float jitterX, float jitterY)
    {
        float fragX = pixelX + 0.5f;
        float fragY = pixelY + 0.5f;
        float nx = fragX / Size * 2f - 1f - 2f * jitterX / Size;
        float ny = fragY / Size * 2f - 1f - 2f * jitterY / Size;

        float c = MathF.Cos(yaw);
        float s = MathF.Sin(yaw);
        // direction = (nx, ny, -1); rotated: (c*nx - s, ny, -s*nx - c);
        // clip.w = -z' = s*nx + c.
        float denominator = s * nx + c;
        float prevNdcX = (c * nx - s) / denominator;
        float prevNdcY = ny / denominator;

        float prevPixelX = (prevNdcX * 0.5f + 0.5f) * Size;
        float prevPixelY = (prevNdcY * 0.5f + 0.5f) * Size;

        return (prevPixelX - (fragX - jitterX), prevPixelY - (fragY - jitterY));
    }

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// The headline case: a fullscreen cloud at depth 1 with a pure rotation
    /// delta. The vector is the rotation reprojection at every pixel, the
    /// reactive value is 1 because the cloud covers the pixel completely, and
    /// the writer depth is 1.0 so the resolve accepts it against the sky depth.
    /// </summary>
    [SkippableFact]
    public void AFullscreenCloudAtDepthOneUnderPureRotationWritesTheRotationVectorAndReactiveOne()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderSkyMotion(device!, coverage: 1f);

            foreach ((int x, int y) in new[] { (32, 32), (48, 40), (20, 12), (56, 56) })
            {
                Decoded pixel = result.At(x, y);
                (float expectedX, float expectedY) = ExpectedMotion(x, y, Yaw, 0f, 0f);

                _output.WriteLine($"({x}, {y}): mv = ({pixel.MotionX}, {pixel.MotionY}), " +
                                  $"expected ({expectedX}, {expectedY}), reactive = {pixel.Reactive}, " +
                                  $"writerDepth = {pixel.WriterDepth}");

                Assert.InRange(pixel.MotionX, expectedX - 0.3f, expectedX + 0.3f);
                Assert.InRange(pixel.MotionY, expectedY - 0.3f, expectedY + 0.3f);
                Assert.InRange(pixel.Reactive, 0.99f, 1.01f);
                Assert.InRange(pixel.WriterDepth, 0.99f, 1.01f);
            }

            // A rotation really does move the sky: a test whose expected value
            // is zero everywhere cannot tell a sign error from a missing writer.
            Decoded centre = result.At(32, 32);
            Assert.True(Math.Abs(centre.MotionX) > 1f,
                "the yaw produced no horizontal motion, so the rotation is not being applied");
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
        }
    }

    /// <summary>
    /// Clear sky - nothing transparent over the pixel - keeps reactive 0, so the
    /// dithered sky gradient goes on converging. The vector is still written.
    /// </summary>
    [SkippableFact]
    public void ClearSkyKeepsAZeroReactiveValueAndStillGetsTheVector()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderSkyMotion(device!, coverage: 0f);
            Decoded centre = result.At(32, 32);
            (float expectedX, float expectedY) = ExpectedMotion(32, 32, Yaw, 0f, 0f);

            _output.WriteLine($"clear sky: mv = ({centre.MotionX}, {centre.MotionY}), reactive = {centre.Reactive}");

            Assert.InRange(centre.Reactive, 0f, 0.01f);
            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.99f, 1.01f);
        }
    }

    /// <summary>
    /// Half coverage lands strictly between the two, and strictly above the
    /// coverage itself - the shader interpolates from the coverage towards the
    /// full reactive value rather than stepping - so a thin wisp is neither
    /// ignored nor treated like a solid cloud bank.
    /// </summary>
    [SkippableFact]
    public void PartialCloudCoverageInterpolatesTheReactiveValue()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderSkyMotion(device!, coverage: 0.5f);
            Decoded centre = result.At(32, 32);

            _output.WriteLine($"half coverage: reactive = {centre.Reactive}");

            // mix(0.5, 1.0, 0.5) = 0.75.
            Assert.InRange(centre.Reactive, 0.73f, 0.77f);
        }
    }

    /// <summary>
    /// The one thing the pass must never do: take a pixel away from a real
    /// writer. The left half of the target is covered by a seed writer that
    /// leaves both a vector and a depth of 0.5; the sky pass draws at depth 1.0
    /// under GL_LEQUAL and has to be rejected there, while the right half - still
    /// at the far plane - is claimed.
    /// </summary>
    [SkippableFact]
    public void PixelsAnotherWriterAlreadyClaimedAreRejectedByTheDepthTest()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderSkyMotion(device!, coverage: 1f, seedLeftHalf: true);

            Decoded covered = result.At(12, 32);
            Decoded sky = result.At(52, 32);

            _output.WriteLine($"covered: mv = ({covered.MotionX}, {covered.MotionY}), " +
                              $"reactive = {covered.Reactive}, writerDepth = {covered.WriterDepth}");
            _output.WriteLine($"sky:     mv = ({sky.MotionX}, {sky.MotionY}), " +
                              $"reactive = {sky.Reactive}, writerDepth = {sky.WriterDepth}");

            // The seed's own values, untouched: (8, -8) px, reactive 0.25, depth 0.5.
            Assert.InRange(covered.MotionX, 7.7f, 8.3f);
            Assert.InRange(covered.MotionY, -8.3f, -7.7f);
            Assert.InRange(covered.Reactive, 0.24f, 0.26f);
            Assert.InRange(covered.WriterDepth, 0.48f, 0.52f);

            // And the sky half really was written, or the assertion above would
            // pass on a pass that drew nothing at all.
            Assert.InRange(sky.WriterDepth, 0.99f, 1.01f);
            Assert.InRange(sky.Reactive, 0.99f, 1.01f);
        }
    }

    /// <summary>
    /// The jittered case: the projection's inverse carries this frame's shear and
    /// the fragment shader is told the same offset in taaJitterPx. The two have
    /// to cancel - the vector describes where the sky went, not where the
    /// sampling grid went.
    /// </summary>
    [SkippableTheory]
    [InlineData(0.375f, -0.25f)]
    [InlineData(-0.5f, 0.5f)]
    public void TheJitterInTheInverseProjectionAndInTheUniformCancel(float jitterX, float jitterY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result jitteredResult = RenderSkyMotion(device!, coverage: 1f, jitterX: jitterX, jitterY: jitterY);

            foreach ((int x, int y) in new[] { (32, 32), (44, 20) })
            {
                Decoded pixel = jitteredResult.At(x, y);
                (float expectedX, float expectedY) = ExpectedMotion(x, y, Yaw, jitterX, jitterY);
                (float unjitteredX, float unjitteredY) = ExpectedMotion(x, y, Yaw, 0f, 0f);

                _output.WriteLine($"jitter ({jitterX}, {jitterY}) at ({x}, {y}): " +
                                  $"mv = ({pixel.MotionX}, {pixel.MotionY}), expected ({expectedX}, {expectedY}), " +
                                  $"unjittered would be ({unjitteredX}, {unjitteredY})");

                Assert.InRange(pixel.MotionX, expectedX - 0.3f, expectedX + 0.3f);
                Assert.InRange(pixel.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            }

            // The jitter is a whole decode step (0.25 px) or more, so a missing
            // or wrongly signed cancellation cannot hide inside the tolerance.
            Assert.True(Math.Abs(jitterX) >= 0.25f && Math.Abs(jitterY) >= 0.25f,
                "the jitter chosen is smaller than the decode quantisation");
        }
    }

    /// <summary>
    /// The pass runs after the OIT merge has already composed the frame into
    /// Primary, so colour attachment 0 has to come back exactly as it went in -
    /// which is what the motion-only draw-buffer mask is for.
    /// </summary>
    [SkippableFact]
    public void TheSkyPassLeavesColourAttachmentZeroUntouched()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Result result = RenderSkyMotion(device!, coverage: 1f);

            Assert.InRange(result.At(32, 32).WriterDepth, 0.99f, 1.01f);

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

    private unsafe Result RenderSkyMotion(
        VulkanDevice device,
        float coverage,
        float jitterX = 0f,
        float jitterY = 0f,
        bool seedLeftHalf = false)
    {
        IOptimumGraphicsDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First(v => v.Name == "taa-no-ssao");
        Assert.Equal(1, variant.TaaMotion);
        Assert.Equal(2, variant.TaaMotionLocation);

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("taa-skymotion", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "taa-skymotion");

        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            "taa-skymotion declares no taaRenderSize, so it is not a motion writer");

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

        // The Transparent target's revealage attachment, standing in for
        // frameBuffers[1].ColorTextureIds[1]: revealage = 1 - coverage.
        int reveal = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        seam.SetTextureParameter(reveal, OptimumGlConstants.TextureMinFilter, 9728);
        seam.SetTextureParameter(reveal, OptimumGlConstants.TextureMagFilter, 9728);
        int revealTarget = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(revealTarget, EnumFramebufferAttachment.ColorAttachment0, reveal, 0);
        seam.SetDrawBuffers(revealTarget, 0b1);

        int seedProgram = seamSeedProgram(seam);
        int seedMesh = seam.CreateMesh(BuildQuad(-1f, 0f, 0f), staticDraw: true);
        Assert.True(seedMesh > 0, seam.GetError() ?? "seed mesh upload failed");

        seam.BeginFrame();

        seam.BindFramebuffer(revealTarget);
        float revealValue = 1f - coverage;
        seam.ClearColor(0, revealValue, revealValue, revealValue, 1f);

        seam.BindFramebuffer(scene);
        seam.SetDrawBuffers(scene, 0b111);
        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        seam.SetViewport(0, 0, Size, Size);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.SetDepthFunc(0x203);   // GL_LEQUAL

        if (seedLeftHalf)
        {
            // A writer that owns the left half at depth 0.5, with the motion
            // attachment as the only enabled target - exactly the shape of a
            // motion-only window.
            seam.SetDrawBuffers(scene, 1 << 2);
            seam.SetDepthTest(true);
            seam.SetDepthMask(true);
            seam.UseProgram(seedProgram);
            seam.DrawMesh(seedMesh);
        }

        // The motion-only window: attachment 2 alone, which is what leaves the
        // already-composed image alone.
        seam.SetDrawBuffers(scene, 1 << 2);
        seam.UseProgram(program);
        seam.SetSamplerUnit(program, "transparentRevealTex", 14);
        seam.BindTexture(14, reveal);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", jitterX, jitterY);
        SetMatrix(seam, program, "taaInvViewProjJittered", InverseJittered(jitterX, jitterY));
        SetMatrix(seam, program, "taaPrevViewProj", PreviousViewProjection(Yaw));
        SetFloat(seam, program, "taaCloudReactive", 1f);

        // Depth test on, depth writes OFF: the pass reads the depth buffer to
        // decide where the sky is and must not change it.
        seam.SetDepthTest(true);
        seam.SetDepthMask(false);
        seam.DrawFullscreenTriangle();

        byte[] decodedMotion = DecodeMotion(seam, motion, reactive: false);
        byte[] decodedReactive = DecodeMotion(seam, motion, reactive: true);
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
    /// A stand-in for any real motion writer: covers the left half at window
    /// depth 0.5 and stamps a vector, a reactive value and its own depth into
    /// the attachment. Location 2 is hard-coded because this program is not
    /// built through the corpus and so has no TAAMOTIONLOCATION define.
    /// </summary>
    private static int seamSeedProgram(IOptimumGraphicsDevice seam)
    {
        const string vertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string fragment = @"#version 330 core
layout(location = 2) out vec4 outMotion;
void main(void) { outMotion = vec4(8.0, -8.0, 0.25, gl_FragCoord.z); }
";
        return LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = vertex, PrefixCode = "", Filename = "taa-sky-seed.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = fragment, PrefixCode = "", Filename = "taa-sky-seed.fsh" },
        }, "taa-sky-seed");
    }

    /// <summary>A quad spanning x in [minX, maxX], y in [-1, 1], at NDC z.</summary>
    private static MeshData BuildQuad(float minX, float maxX, float z)
    {
        return new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = new[] { minX, -1f, z, maxX, -1f, z, maxX, 1f, z, minX, 1f, z },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
        };
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
    /// </summary>
    private static unsafe byte[] DecodeMotion(IOptimumGraphicsDevice seam, int motionTexture, bool reactive)
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
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-sky-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-sky-decode.fsh" },
        }, "taa-sky-decode");

        int quadMesh = seam.CreateMesh(BuildQuad(-1f, 1f, 0f), staticDraw: true);
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

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device)
    {
        var created = new VulkanDevice { DebugMode = true };
        if (created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device = created;
            return true;
        }

        output.WriteLine("Vulkan unavailable: " + failureReason);
        created.Dispose();
        device = null;
        return false;
    }

    private static void AssertClean(IOptimumGraphicsDevice seam)
    {
        string? diagnostics = seam.GetError();
        Assert.True(string.IsNullOrEmpty(diagnostics), "device diagnostics:\n" + diagnostics);
    }

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
