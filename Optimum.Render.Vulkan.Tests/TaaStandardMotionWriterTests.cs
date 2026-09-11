using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The standard-shader motion-vector writer (TAA P3), driven through the seam with
/// the real standard program and read back as pixels. This is the writer every
/// held item, first-person item, dropped item and block-entity model goes through.
///
/// What it has to get right, and what a "motion is non-zero when the item moved"
/// assertion could not tell apart from a sign flip or an axis swap:
/// - a previous model matrix turns into exactly that displacement in render pixels;
/// - a still object under a still camera is exactly (0, 0), because a converged
///   item that wobbles is what a wrong previous transform looks like on screen;
/// - without usable history the vector is camera motion only, never the stale
///   previous model matrix;
/// - the previous warp state is replayed through the caller's own
///   dontWarpVertices branch, not unconditionally: a draw that asks for no warp
///   at all must produce no warp motion even when the previous warp state differs.
///
/// As in TaaEntityMotionWriterTests the RGBA16F attachment comes back through an
/// RGBA8 decode pass, because the seam's readback is fixed at four bytes per pixel
/// from colour attachment 0. Decode quantisation is 2*DecodeScale/255 px, so the
/// tolerances stay above it.
/// </summary>
public class TaaStandardMotionWriterTests
{
    private readonly ITestOutputHelper _output;

    public TaaStandardMotionWriterTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>Normal pointing up, no glow, and no wind-mode bits, so no vertex warp runs.</summary>
    private const int UpNormalFlags = 7 << 18;

    /// <summary>standard.vsh: 0 = full warp, 2 = the held item's quarter warp, anything else = none.</summary>
    private const int WarpFull = 0;
    private const int WarpNone = 1;

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

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// An object that did not move, under a camera that did not move, is zero
    /// motion - and the writer still stamps its own depth, so the resolve accepts
    /// the pixel instead of silently falling back to camera reprojection.
    /// </summary>
    [SkippableFact]
    public void AnUnmovedItemWritesZeroMotionAndTheFragmentDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderStandardMotion(device!,
                previousModelMatrix: Identity,
                historyValid: 1,
                dontWarpVertices: WarpFull,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: 0f);

            _output.WriteLine($"still: mv = ({centre.MotionX}, {centre.MotionY}), writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            // Identity matrices put the quad at NDC z = 0, which is window depth
            // 0.5 on both backends.
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The model matrix this item was drawn with last frame is its motion: with
    /// identity camera matrices a translation of d moves the pixel by exactly
    /// d * 0.5 * renderSize, and the sign is "where the pixel was", not "where it
    /// went".
    /// </summary>
    [SkippableTheory]
    [InlineData(0.25f, 0f)]
    [InlineData(0f, -0.125f)]
    [InlineData(-0.1875f, 0.0625f)]
    public void APreviousModelMatrixShowsUpAsTheExactPixelDisplacement(float modelX, float modelY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderStandardMotion(device!,
                previousModelMatrix: Translation(modelX, modelY, 0f),
                historyValid: 1,
                dontWarpVertices: WarpFull,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: 0f);

            float expectedX = modelX * 0.5f * Size;
            float expectedY = modelY * 0.5f * Size;

            _output.WriteLine($"model ({modelX}, {modelY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// Without usable history - the item appeared, the held stack was swapped, the
    /// object was not drawn last frame, the camera switched between first and
    /// third person - the writer must not read the previous model matrix at all.
    /// It falls back to treating the surface as static in the world, so only the
    /// camera's own movement displaces it; C# raises taaReactive for the same draw
    /// so the resolve leans on this frame.
    ///
    /// The previous model matrix here is deliberately a large translation: if the
    /// shader took the history branch anyway, the vector would be that instead.
    /// </summary>
    [SkippableFact]
    public void WithoutUsableHistoryTheVectorIsCameraMotionOnly()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float cameraDeltaX = 0.25f;
            const float cameraDeltaY = -0.125f;
            Decoded centre = RenderStandardMotion(device!,
                previousModelMatrix: Translation(-0.5f, 0.5f, 0f),
                historyValid: 0,
                dontWarpVertices: WarpFull,
                cameraDeltaX: cameraDeltaX,
                cameraDeltaY: cameraDeltaY,
                previousGlobalWarp: 0f);

            float expectedX = cameraDeltaX * 0.5f * Size;
            float expectedY = cameraDeltaY * 0.5f * Size;

            _output.WriteLine($"no history: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
        }
    }

    /// <summary>
    /// Vertex animation that differed last frame is motion too. The values are
    /// chosen so applyGlobalWarping's phase argument saturates at zero over the
    /// whole quad, which turns the warp into a constant offset with a closed-form
    /// expectation instead of a "not zero" assertion.
    ///
    /// The second half is the branch test: the very same previous warp state with
    /// dontWarpVertices set to "no warp" must produce no warp motion. A writer
    /// that applied the warp unconditionally - the easy mistake, since the current
    /// position's branch is three lines further up - would pass the first half and
    /// fail this one, and would give every unwarped block-entity model a motion
    /// vector it never had.
    /// </summary>
    [SkippableFact]
    public void ThePreviousWarpStateIsReplayedThroughTheCallersOwnBranch()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float previousWarp = 8f;

            Decoded warped = RenderStandardMotion(device!,
                previousModelMatrix: Identity,
                historyValid: 1,
                dontWarpVertices: WarpFull,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: previousWarp);

            double offsetX = (Math.Sin(0.0) + Math.Sin(0.5) + Math.Sin(1.0) / 3.0) / 30.0 * previousWarp;
            float expectedX = (float)(offsetX * 0.5 * Size);

            _output.WriteLine($"warped: mv = ({warped.MotionX}, {warped.MotionY}), expected ({expectedX}, 0)");

            Assert.True(Math.Abs(expectedX) > 1f, "the warp displacement chosen is too small to test");
            Assert.InRange(warped.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(warped.MotionY, -0.3f, 0.3f);

            Decoded unwarped = RenderStandardMotion(device!,
                previousModelMatrix: Identity,
                historyValid: 1,
                dontWarpVertices: WarpNone,
                cameraDeltaX: 0f,
                cameraDeltaY: 0f,
                previousGlobalWarp: previousWarp);

            _output.WriteLine($"unwarped: mv = ({unwarped.MotionX}, {unwarped.MotionY}), expected (0, 0)");

            Assert.InRange(unwarped.MotionX, -0.3f, 0.3f);
            Assert.InRange(unwarped.MotionY, -0.3f, 0.3f);
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
    /// Draws one quad with the real standard program compiled as a motion writer,
    /// then decodes the motion attachment and returns its centre pixel. The
    /// current model matrix is always the identity and this frame's warp state is
    /// pinned to a no-op, so every expectation is stated entirely in terms of the
    /// previous-frame inputs.
    /// </summary>
    private unsafe Decoded RenderStandardMotion(
        VulkanDevice device,
        float[] previousModelMatrix,
        int historyValid,
        int dontWarpVertices,
        float cameraDeltaX,
        float cameraDeltaY,
        float previousGlobalWarp)
    {
        IOptimumGraphicsDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = new ShaderCorpus.ShaderVariant
        {
            Name = "taa-standard",
            TaaMotion = 1,
            TaaMotionLocation = 2,
        };

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("standard", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "standard");

        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            "standard declares no taaRenderSize, so it is not a motion writer");
        Assert.True(seam.GetUniformLocation(program, "taaHistoryValid") >= 0,
            "standard declares no taaHistoryValid, so it cannot reject stale history");
        Assert.True(seam.GetUniformLocation(program, "prevModelMatrix") >= 0,
            "standard declares no prevModelMatrix, so it has no previous transform to use");

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
        SetMatrix(seam, program, "projectionMatrix", Identity);
        SetMatrix(seam, program, "viewMatrix", Identity);
        SetMatrix(seam, program, "modelMatrix", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixFar", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixNear", Identity);
        SetSceneUniforms(seam, program, dontWarpVertices);
        SetWarpUniforms(seam, program, previousGlobalWarp);

        SetMatrix(seam, program, "prevProjectionMatrix", Identity);
        SetMatrix(seam, program, "prevViewMatrix", Identity);
        SetMatrix(seam, program, "prevModelMatrix", previousModelMatrix);
        SetInt(seam, program, "taaHistoryValid", historyValid);
        SetFloat(seam, program, "taaReactive", historyValid != 0 ? 0f : 1f);
        SetFloat3(seam, program, "cameraPosDelta", cameraDeltaX, cameraDeltaY, 0f);
        SetFloat2(seam, program, "taaRenderSize", Size, Size);
        SetFloat2(seam, program, "taaJitterPx", 0f, 0f);

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
    /// the seam's readback is fixed at four bytes per pixel from attachment 0.
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
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-standard-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-standard-decode.fsh" },
        }, "taa-standard-decode");

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
    /// A quad in standard.vsh's attribute layout: xyz, uv, rgba, flags. Normals
    /// are absent, which is what puts uv on location 1 the way the shader declares
    /// it; GLOWSUB is not defined, so there is no fifth attribute.
    /// </summary>
    private static MeshData BuildQuad()
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
    /// a fully transparent fragment is discarded before it can write a motion
    /// vector, and the test would read the cleared attachment instead. alphaTest
    /// is pushed below zero so nothing can discard at all.
    /// </summary>
    private static void SetSceneUniforms(IOptimumGraphicsDevice seam, int program, int dontWarpVertices)
    {
        SetInt(seam, program, "dontWarpVertices", dontWarpVertices);
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
    /// Both halves of the warp state, pinned so this frame's warp is a no-op and
    /// only the previous one moves anything.
    /// </summary>
    private static void SetWarpUniforms(IOptimumGraphicsDevice seam, int program, float previousGlobalWarp)
    {
        SetFloat(seam, program, "timeCounter", 0f);
        SetFloat(seam, program, "windWaveCounter", 0f);
        SetFloat(seam, program, "windWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "waterWaveCounter", 0f);
        SetFloat(seam, program, "windSpeed", 0f);
        SetFloat(seam, program, "globalWarpIntensity", 0f);
        SetFloat(seam, program, "glitchWaviness", 0f);
        SetFloat(seam, program, "windWaveIntensity", 1f);
        SetFloat(seam, program, "waterWaveIntensity", 1f);
        SetInt(seam, program, "perceptionEffectId", 1);
        SetFloat(seam, program, "perceptionEffectIntensity", 0f);
        SetFloat3(seam, program, "playerpos", 0f, 0f, 0f);

        SetFloat(seam, program, "prevTimeCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounter", 0f);
        SetFloat(seam, program, "prevWindWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "prevWaterWaveCounter", 0f);
        SetFloat(seam, program, "prevWindSpeed", 0f);
        SetFloat(seam, program, "prevGlobalWarpIntensity", previousGlobalWarp);
        SetFloat(seam, program, "prevGlitchWaviness", 0f);
        SetFloat(seam, program, "prevWindWaveIntensity", 1f);
        SetFloat(seam, program, "prevWaterWaveIntensity", 1f);
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
