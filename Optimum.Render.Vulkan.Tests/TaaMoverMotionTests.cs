using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The standard-shader motion writer as the TAA P4 "movers" drive it: a
/// block-entity model - the helve hammer's head, the resonator's disc, the pot
/// lid, a falling block - drawn with <c>dontWarpVertices</c> set to "no warp" and
/// a previous model matrix from <c>OptimumStandardMotion</c>, <b>under a jittered
/// projection</b>.
///
/// The jitter is the point. Every P3 GPU test ran with <c>taaJitterPx = 0</c>, and
/// with the identity projection those tests use the NDC shear
/// <c>P[8] -= 2*jx/W</c> is a no-op on a quad at z = 0, so a jittered case there
/// would have proved nothing. This file uses the perspective-SHAPED matrix from
/// TaaLiquidMotionTests (clip.w = -z_view, quad at z = -1), where the shear
/// displaces the raster position by exactly jx pixels - so if the writer forgot
/// either half of the convention (subtract the jitter from the current pixel,
/// reproject the previous one through an UNJITTERED matrix) the vector comes back
/// off by the jitter and these assertions fail.
///
/// What is being proved:
/// - a mover's previous model matrix is exactly its pixel displacement, and the
///   answer does not change when the frame is jittered;
/// - a mover that did not move is exactly zero even under jitter, because a
///   converged block entity that shimmers is what a leaked jitter looks like;
/// - without usable history the vector is camera motion only, never the stale
///   previous matrix - the case every one of these renderers hits on its first
///   frame, on a mesh swap, and after a reset.
///
/// As in TaaStandardMotionWriterTests the RGBA16F attachment comes back through
/// an RGBA8 decode pass, because the seam's readback is fixed at four bytes per
/// pixel from colour attachment 0.
/// </summary>
public class TaaMoverMotionTests
{
    private readonly ITestOutputHelper _output;

    public TaaMoverMotionTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>Normal pointing up, no glow, no wind-mode bits, so no vertex warp runs.</summary>
    private const int UpNormalFlags = 7 << 18;

    /// <summary>
    /// standard.vsh: 0 = full warp, 2 = the held item's quarter warp, anything else
    /// = none. Block-entity models pass "none", which is what every renderer in
    /// this phase does.
    /// </summary>
    private const int WarpNone = 1;

    /// <summary>The view-space z the test quad sits at; clip.w = -z = 1 there.</summary>
    private const float QuadViewZ = -1f;

    private static readonly float[] Identity =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    private static float[] Translation(float x, float y, float z) => new[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        x,  y,  z,  1f,
    };

    /// <summary>
    /// The perspective-SHAPED projection, column-major, plus the NDC jitter shear
    /// the frame contract applies (`P[8] -= 2*jx/W; P[9] -= 2*jy/H`).
    ///
    /// clip = (x + m02*z, y + m12*z, -z - 1, -z). At z = -1 that is clip.w = 1, so
    /// ndc.xy = (x + 2*jx/W, y + 2*jy/H) and the raster position moves by exactly
    /// (jx, jy) pixels - the property that makes a jittered assertion mean
    /// something. ndc.z = 0, so window depth is 0.5.
    /// </summary>
    private static float[] Projection(float jitterX, float jitterY)
    {
        var p = new float[16];
        p[0] = 1f;                          // m00
        p[5] = 1f;                          // m11
        p[8] = -2f * jitterX / Size;        // m02, the jitter shear
        p[9] = -2f * jitterY / Size;        // m12
        p[10] = -1f;                        // m22
        p[11] = -1f;                        // m32: clip.w = -z_view
        p[14] = -1f;                        // m23
        p[15] = 0f;                         // m33
        return p;
    }

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// A block entity that is not moving, under a still camera, in a jittered
    /// frame, is exactly zero motion - and still stamps its own depth, so the
    /// resolve accepts the pixel instead of camera-reprojecting it.
    ///
    /// A writer that forgot `gl_FragCoord.xy - taaJitterPx` would report the jitter
    /// itself here, which is a sub-pixel wobble on every still block entity in the
    /// world and precisely the artefact TAA is supposed to remove.
    /// </summary>
    [SkippableTheory]
    [InlineData(0f, 0f)]
    [InlineData(0.37f, -0.24f)]
    [InlineData(-0.5f, 0.5f)]
    public void AStillMoverIsZeroMotionEvenUnderJitter(float jitterX, float jitterY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderMoverMotion(device!,
                previousModelMatrix: Identity,
                historyValid: 1,
                jitterX: jitterX,
                jitterY: jitterY,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f);

            _output.WriteLine($"jitter ({jitterX}, {jitterY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The model matrix the mover was drawn with last frame is its motion: with
    /// identity view matrices a translation of d displaces the pixel by exactly
    /// d * 0.5 * renderSize, and the sign is "where the pixel was", not "where it
    /// went".
    ///
    /// The jitter is deliberately large and asymmetric here: it enters the current
    /// pixel through the shear and must leave again through `- taaJitterPx`,
    /// leaving the same answer as the unjittered frame. The companion test below
    /// asserts that equality directly.
    /// </summary>
    [SkippableTheory]
    [InlineData(0.25f, 0f, 0.5f, -0.5f)]
    [InlineData(0f, -0.125f, -0.5f, 0.5f)]
    [InlineData(-0.1875f, 0.0625f, 0.31f, 0.47f)]
    public void APreviousModelMatrixIsTheExactPixelDisplacementUnderJitter(
        float modelX, float modelY, float jitterX, float jitterY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderMoverMotion(device!,
                previousModelMatrix: Translation(modelX, modelY, 0f),
                historyValid: 1,
                jitterX: jitterX,
                jitterY: jitterY,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f);

            float expectedX = modelX * 0.5f * Size;
            float expectedY = modelY * 0.5f * Size;

            _output.WriteLine($"model ({modelX}, {modelY}) jitter ({jitterX}, {jitterY}): " +
                              $"mv = ({centre.MotionX}, {centre.MotionY}), expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The convention says the vector excludes the jitter. That is only testable as
    /// an equality between two frames of the same scene that differ by nothing but
    /// the jitter phase, which is what this is: same previous matrix, same camera,
    /// two different Halton-sized offsets, one answer.
    /// </summary>
    [SkippableFact]
    public void TheVectorIsTheSameWhicheverJitterPhaseTheFrameIsOn()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            float[] previous = Translation(0.25f, -0.125f, 0f);

            Decoded unjittered = RenderMoverMotion(device!, previous, 1, 0f, 0f, 0f, 0f);
            Decoded jittered = RenderMoverMotion(device!, previous, 1, 0.5f, -0.5f, 0f, 0f);

            _output.WriteLine($"unjittered = ({unjittered.MotionX}, {unjittered.MotionY}), " +
                              $"jittered = ({jittered.MotionX}, {jittered.MotionY})");

            Assert.InRange(jittered.MotionX, unjittered.MotionX - 0.3f, unjittered.MotionX + 0.3f);
            Assert.InRange(jittered.MotionY, unjittered.MotionY - 0.3f, unjittered.MotionY + 0.3f);
        }
    }

    /// <summary>
    /// The case every one of these renderers hits on its first frame, whenever its
    /// mesh is re-uploaded, and after a reset: <c>OptimumStandardMotion.Apply</c>
    /// reports no usable history, so the writer must not touch the previous model
    /// matrix at all. It treats the surface as static in the world, so only the
    /// camera's own movement displaces it, and C# raises <c>taaReactive</c> for the
    /// same draw.
    ///
    /// The previous model matrix here is a large translation on purpose: if the
    /// shader took the history branch anyway, the vector would be that instead of
    /// the camera delta, and the test would say so rather than merely noticing
    /// "some motion".
    /// </summary>
    [SkippableFact]
    public void WithoutUsableHistoryTheMoverFallsBackToCameraMotionOnly()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float cameraDeltaX = 0.25f;
            const float cameraDeltaY = -0.125f;

            Decoded centre = RenderMoverMotion(device!,
                previousModelMatrix: Translation(-0.5f, 0.5f, 0f),
                historyValid: 0,
                jitterX: 0.42f,
                jitterY: 0.13f,
                cameraDeltaX: cameraDeltaX,
                cameraDeltaY: cameraDeltaY);

            float expectedX = cameraDeltaX * 0.5f * Size;
            float expectedY = cameraDeltaY * 0.5f * Size;

            _output.WriteLine($"no history: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
        }
    }

    // ---------------------------------------------------------------- harness

    private readonly struct Decoded
    {
        public Decoded(float motionX, float motionY, float writerDepth)
        {
            MotionX = motionX;
            MotionY = motionY;
            WriterDepth = writerDepth;
        }

        public float MotionX { get; }
        public float MotionY { get; }
        public float WriterDepth { get; }
    }

    /// <summary>
    /// Draws one block-entity-shaped quad with the real standard program compiled
    /// as a motion writer, then decodes the motion attachment and returns its
    /// centre pixel. The current model matrix is always the identity and this
    /// frame's warp state is pinned to a no-op, so every expectation is stated
    /// entirely in terms of the previous-frame inputs and the jitter.
    /// </summary>
    private unsafe Decoded RenderMoverMotion(
        VulkanDevice device,
        float[] previousModelMatrix,
        int historyValid,
        float jitterX,
        float jitterY,
        float cameraDeltaX,
        float cameraDeltaY)
    {
        IOptimumGraphicsDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = new ShaderCorpus.ShaderVariant
        {
            Name = "taa-mover",
            TaaMotion = 1,
            TaaMotionLocation = 2,
        };

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("standard", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "standard");

        Assert.True(seam.GetUniformLocation(program, "taaJitterPx") >= 0,
            "standard declares no taaJitterPx, so it cannot exclude the jitter");
        Assert.True(seam.GetUniformLocation(program, "prevModelMatrix") >= 0,
            "standard declares no prevModelMatrix, so a mover has no previous transform to use");

        BindEveryDeclaredSampler(device, seam, program);

        // Primary stand-in: colour, glow and the motion attachment at index 2.
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

        int scene = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment2, motion, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        seam.SetDrawBuffers(scene, 0b111);
        Assert.True(seam.CheckFramebufferComplete(scene, out string status), status);

        int mesh = seam.CreateMesh(BuildQuad(), staticDraw: true);
        Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(scene);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        seam.UseProgram(program);

        // This frame is jittered; the previous projection never is. That pair is
        // the whole convention under test.
        SetMatrix(seam, program, "projectionMatrix", Projection(jitterX, jitterY));
        SetMatrix(seam, program, "prevProjectionMatrix", Projection(0f, 0f));
        SetMatrix(seam, program, "viewMatrix", Identity);
        SetMatrix(seam, program, "prevViewMatrix", Identity);
        SetMatrix(seam, program, "modelMatrix", Identity);
        SetMatrix(seam, program, "prevModelMatrix", previousModelMatrix);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixFar", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixNear", Identity);

        SetSceneUniforms(seam, program);
        SetWarpUniforms(seam, program);

        SetInt(seam, program, "taaHistoryValid", historyValid);
        SetFloat(seam, program, "taaReactive", historyValid != 0 ? 0f : 1f);
        SetFloat3(seam, program, "cameraPosDelta", cameraDeltaX, cameraDeltaY, 0f);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", jitterX, jitterY);

        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(true);
        seam.SetDepthMask(true);
        seam.SetDepthFunc(0x203);   // GL_LEQUAL
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawMesh(mesh);

        byte[] decoded = DecodeMotion(seam, motion);
        seam.Present();

        int offset = ((Size / 2) * Size + Size / 2) * 4;

        AssertClean(seam);

        return new Decoded(
            (decoded[offset] / 255f * 2f - 1f) * DecodeScale,
            (decoded[offset + 1] / 255f * 2f - 1f) * DecodeScale,
            decoded[offset + 2] / 255f);
    }

    /// <summary>
    /// Reads the RGBA16F motion attachment through an RGBA8 decode pass, because
    /// the seam's readback is fixed at four bytes per pixel from attachment 0, and
    /// reads it back inside the same frame.
    /// </summary>
    private unsafe byte[] DecodeMotion(IOptimumGraphicsDevice seam, int motionTexture)
    {
        const string decodeVertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string decodeFragment = @"#version 330 core
uniform sampler2D motionTex;
uniform float decodeScale;
layout(location = 0) out vec4 outColor;
void main(void)
{
	vec4 m = texelFetch(motionTex, ivec2(gl_FragCoord.xy), 0);
	outColor = vec4(
		clamp(m.r / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.g / decodeScale * 0.5 + 0.5, 0.0, 1.0),
		clamp(m.a, 0.0, 1.0),
		1.0);
}
";
        int decode = LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-mover-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-mover-decode.fsh" },
        }, "taa-mover-decode");

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
    /// A quad in standard.vsh's attribute layout (xyz, uv, rgba, flags), placed at
    /// view-space z = -1 so the perspective-shaped projection gives it clip.w = 1
    /// and the jitter shear a real pixel displacement. It is deliberately smaller
    /// than the viewport so the centre pixel stays covered under every jitter.
    /// </summary>
    private static MeshData BuildQuad()
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);

        float[] positions =
        {
            -0.5f, -0.5f, QuadViewZ,
             0.5f, -0.5f, QuadViewZ,
             0.5f,  0.5f, QuadViewZ,
            -0.5f,  0.5f, QuadViewZ,
        };
        float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };

        for (int i = 0; i < 4; i++)
        {
            mesh.AddVertexWithFlags(
                positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                uvs[i * 2], uvs[i * 2 + 1],
                Vintagestory.API.MathTools.ColorUtil.WhiteArgb,
                flags: UpNormalFlags);
        }

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }
        return mesh;
    }

    /// <summary>
    /// Enough of the lighting, fog and overlay surface to keep the fragment alive:
    /// a discarded fragment writes no motion vector and the test would read the
    /// cleared attachment instead. alphaTest is pushed below zero so nothing can
    /// discard at all, and dontWarpVertices is the block-entity value.
    /// </summary>
    private static void SetSceneUniforms(IOptimumGraphicsDevice seam, int program)
    {
        SetInt(seam, program, "dontWarpVertices", WarpNone);
        SetInt(seam, program, "fadeFromSpheresFog", 0);
        SetInt(seam, program, "addRenderFlags", 0);
        SetInt(seam, program, "extraGlow", 0);
        SetFloat(seam, program, "extraZOffset", 0f);

        SetFloat(seam, program, "alphaTest", -1f);
        SetFloat(seam, program, "viewDistance", 1024f);
        SetFloat(seam, program, "viewDistanceLod0", 1024f);
        SetFloat(seam, program, "zNear", 0.1f);
        SetFloat(seam, program, "zFar", 1024f);
        SetFloat(seam, program, "fogMinIn", 0f);
        SetFloat(seam, program, "fogDensityIn", 0f);
        SetFloat(seam, program, "shadowRangeFar", 1024f);
        SetFloat(seam, program, "shadowRangeNear", 64f);
        SetFloat(seam, program, "shadowMapWidthInv", 1f);
        SetFloat(seam, program, "shadowMapHeightInv", 1f);
        SetFloat(seam, program, "shadowIntensity", 0f);
        SetFloat(seam, program, "damageEffect", 0f);
        SetFloat(seam, program, "overlayOpacity", 0f);
        SetFloat(seam, program, "extraGodray", 0f);
        SetFloat(seam, program, "ssaoAttn", 0f);
        SetInt(seam, program, "applySsao", 0);
        SetInt(seam, program, "tempGlowMode", 0);
        SetInt(seam, program, "normalShaded", 0);
        SetInt(seam, program, "skyShaded", 0);
        SetFloat3(seam, program, "rgbaAmbientIn", 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaLightIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaFogIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaGlowIn", 0f, 0f, 0f, 0f);
        SetFloat4(seam, program, "rgbaTint", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "averageColor", 1f, 1f, 1f, 1f);
        SetFloat2(seam, program, "frameSize", Size, Size);
    }

    /// <summary>
    /// Both halves of the warp state pinned to the same no-op, so nothing in this
    /// file's expectations comes from vertex animation - a block-entity model
    /// passes "no warp" anyway, and P3 already covers the warp branches.
    /// </summary>
    private static void SetWarpUniforms(IOptimumGraphicsDevice seam, int program)
    {
        foreach (string prefix in new[] { "", "prev" })
        {
            SetFloat(seam, program, Name(prefix, "timeCounter"), 0f);
            SetFloat(seam, program, Name(prefix, "windWaveCounter"), 0f);
            SetFloat(seam, program, Name(prefix, "windWaveCounterHighFreq"), 0f);
            SetFloat(seam, program, Name(prefix, "waterWaveCounter"), 0f);
            SetFloat(seam, program, Name(prefix, "windSpeed"), 0f);
            SetFloat(seam, program, Name(prefix, "globalWarpIntensity"), 0f);
            SetFloat(seam, program, Name(prefix, "glitchWaviness"), 0f);
            SetFloat(seam, program, Name(prefix, "windWaveIntensity"), 1f);
            SetFloat(seam, program, Name(prefix, "waterWaveIntensity"), 1f);
            SetInt(seam, program, Name(prefix, "perceptionEffectId"), 1);
            SetFloat(seam, program, Name(prefix, "perceptionEffectIntensity"), 0f);
            SetFloat3(seam, program, Name(prefix, "playerpos"), 0f, 0f, 0f);
        }
    }

    private static string Name(string prefix, string uniform)
    {
        if (prefix.Length == 0) return uniform;
        return prefix + char.ToUpperInvariant(uniform[0]) + uniform.Substring(1);
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

    private static void SetFloat4(
        IOptimumGraphicsDevice seam, int program, string name, float x, float y, float z, float w)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y, z, w);
    }

    private static void SetMatrix(IOptimumGraphicsDevice seam, int program, string name, float[] matrix)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniformMatrix(program, location, matrix);
    }

    private static unsafe int CreateWhiteTexture(IOptimumGraphicsDevice seam)
    {
        var white = new byte[] { 255, 255, 255, 255 };
        fixed (byte* pixels = white)
        {
            return seam.CreateTexture2D(1, 1,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
        }
    }

    private static int BindEveryDeclaredSampler(
        VulkanDevice device, IOptimumGraphicsDevice seam, int programId)
    {
        int unit = 0;
        foreach (string samplerName in device.SamplerNamesOf(programId))
        {
            int texture = CreateWhiteTexture(seam);
            seam.SetSamplerUnit(programId, samplerName, unit);
            seam.BindTexture(unit, texture);
            unit++;
        }
        return unit;
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
        public bool Oit { get; set; }
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
