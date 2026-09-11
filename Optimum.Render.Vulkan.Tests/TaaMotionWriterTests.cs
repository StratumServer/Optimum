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
/// The terrain motion-vector writers (TAA P3), driven through the seam with the
/// real chunkopaque and chunktopsoil programs and read back as pixels.
///
/// The contract under test is the one taa-resolve.fsh consumes: the motion
/// attachment carries <c>rg</c> = previousPixel - currentUnjitteredPixel in
/// render pixels, <c>b</c> = reactive, <c>a</c> = the writer's window depth. A
/// sign flip, an axis swap or a forgotten 0.5 in the NDC-to-pixel conversion all
/// look the same in a "motion is zero when nothing moves" test, so every case
/// here is a known displacement with an exact expected magnitude, and the still
/// case is only the baseline.
///
/// The motion attachment is RGBA16F and <c>ReadDefaultFramebuffer</c> reads four
/// bytes per pixel from colour attachment 0, so the values come back through a
/// second fullscreen pass that decodes them into an RGBA8 target. That is a
/// readback detail, not part of the contract: the decode is a plain
/// <c>texelFetch</c> with a fixed scale.
/// </summary>
public class TaaMotionWriterTests
{
    private readonly ITestOutputHelper _output;

    public TaaMotionWriterTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Normal pointing up, no glow, no z-offset and - crucially - no wind mode bits.</summary>
    private const int UpNormalFlags = 7 << 18;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    private static readonly float[] Identity =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    // ------------------------------------------------------------------ tests

    /// <summary>
    /// A camera that did not move produces no motion at all, and the writer
    /// still stamps its own depth so the resolve accepts the pixel rather than
    /// silently falling back to camera reprojection.
    /// </summary>
    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunktopsoil")]
    public void AStillCameraWritesZeroMotionAndTheFragmentDepth(string programName)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderMotion(device!, programName, 0f, 0f, previousGlobalWarp: 0f);

            _output.WriteLine($"{programName} still: mv = ({centre.MotionX}, {centre.MotionY}), writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            // Identity matrices put the quad on the near-ish middle of the
            // depth range: 0 in NDC, which is 0.5 as a window depth on both
            // backends (the translator's (z+w)*0.5 remap lands on the same
            // value Vulkan's [0,1] clip range expects).
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The camera translating by a known amount moves every static surface by
    /// exactly that amount, converted into pixels: with identity matrices a
    /// camera-relative displacement of d in NDC is d * 0.5 * renderSize pixels,
    /// and the sign is "where the pixel was", not "where it went".
    /// </summary>
    [SkippableTheory]
    [InlineData("chunkopaque", 0.25f, 0f)]
    [InlineData("chunkopaque", 0f, -0.125f)]
    [InlineData("chunkopaque", -0.1875f, 0.0625f)]
    [InlineData("chunktopsoil", 0.25f, -0.125f)]
    public void ACameraTranslationShowsUpAsTheExactPixelDisplacement(
        string programName, float deltaX, float deltaY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded centre = RenderMotion(device!, programName, deltaX, deltaY, previousGlobalWarp: 0f);

            // prevRel = truePos + cameraPosDelta, both matrices identity, so the
            // previous clip position differs from the current one by exactly the
            // delta and the pixel difference is delta * 0.5 * Size.
            float expectedX = deltaX * 0.5f * Size;
            float expectedY = deltaY * 0.5f * Size;

            _output.WriteLine($"{programName} delta ({deltaX}, {deltaY}): mv = " +
                              $"({centre.MotionX}, {centre.MotionY}), expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// Vertex animation that differed last frame is motion too.
    ///
    /// The previous warp state is fed through the same code as the current one
    /// (the WarpState overloads in the vertexwarp include), so a previous global
    /// warp intensity that this frame no longer has must displace the previous
    /// position and nothing else. The values are chosen so applyGlobalWarping's
    /// phase argument saturates at zero over the whole quad, which makes the warp
    /// a constant offset and the expected motion exactly computable rather than a
    /// "not zero" assertion.
    /// </summary>
    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunktopsoil")]
    public void APreviousWarpStateThatDiffersFromThisFrameProducesItsOwnMotion(string programName)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float previousWarp = 8f;
            Decoded centre = RenderMotion(device!, programName, 0f, 0f, previousWarp);

            // applyGlobalWarpingState with a phase of zero:
            //   worldPos.x += (sin(0) + sin(0.5) + sin(1)/3) / 30 * intensity
            // and nothing on y, so the whole quad shifts by a constant in x only.
            double offsetX = (Math.Sin(0.0) + Math.Sin(0.5) + Math.Sin(1.0) / 3.0) / 30.0 * previousWarp;
            float expectedX = (float)(offsetX * 0.5 * Size);

            _output.WriteLine($"{programName} warp-only: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, 0)");

            // The check only means something if the displacement is well clear of
            // the decode quantisation and of zero.
            Assert.True(Math.Abs(expectedX) > 1f, "the warp displacement chosen is too small to test");
            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
        }
    }

    /// <summary>
    /// A pixel no writer covered keeps a zero alpha, which is what makes the
    /// resolve's writer-depth test reject it and use the camera fallback. If the
    /// motion attachment were in the default draw-buffer set, or the clear were
    /// skipped, this would hold another surface's vector instead.
    /// </summary>
    [SkippableFact]
    public void PixelsNoWriterCoveredKeepAZeroWriterDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            Decoded corner = RenderMotion(device!, "chunkopaque", 0.25f, 0f, 0f, sampleCorner: true);

            _output.WriteLine($"corner: mv = ({corner.MotionX}, {corner.MotionY}), writerDepth = {corner.WriterDepth}");
            Assert.InRange(corner.WriterDepth, 0f, 0.01f);
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
    /// Draws one block face with the given terrain program compiled as a motion
    /// writer, then decodes the motion attachment and returns the centre (or
    /// corner) pixel.
    /// </summary>
    private unsafe Decoded RenderMotion(
        VulkanDevice device,
        string programName,
        float cameraDeltaX,
        float cameraDeltaY,
        float previousGlobalWarp,
        bool sampleCorner = false)
    {
        IOptimumGraphicsDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First(v => v.Name == "taa-no-ssao");
        Assert.Equal(1, variant.TaaMotion);
        Assert.Equal(2, variant.TaaMotionLocation);

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(programName, files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, programName);

        // The writer only exists if the shader really declares it; without this
        // the test would pass on a shader that dropped the output entirely.
        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            programName + " declares no taaRenderSize, so it is not a motion writer");

        int nextUnit = BindEveryDeclaredSampler(device, seam, program);
        int atlas = CreateWhiteTexture(seam);
        foreach (string samplerName in new[] { "terrainTex", "terrainTexLinear" })
        {
            seam.SetSamplerUnit(program, samplerName, nextUnit);
            seam.BindTexture(nextUnit, atlas);
            nextUnit++;
        }

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

        int scene = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.ColorAttachment2, motion, 0);
        seam.AttachTexture(scene, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        // The motion attachment enabled for the duration of the writing pass -
        // exactly what ClientPlatformWindows.BeginMotionWrite does to Primary.
        seam.SetDrawBuffers(scene, 0b111);
        Assert.True(seam.CheckFramebufferComplete(scene, out string status), status);

        int mesh = seam.CreateMesh(BuildBlockFace(), staticDraw: true);
        Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(scene);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0f, 0f, 0f, 1f);
        seam.ClearColor(2, 0f, 0f, 0f, 0f);
        seam.ClearDepth(1f);

        seam.UseProgram(program);
        SetMatrix(seam, program, "projectionMatrix", Identity);
        SetMatrix(seam, program, "modelViewMatrix", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixFar", Identity);
        SetMatrix(seam, program, "toShadowMapSpaceMatrixNear", Identity);
        SetViewUniforms(seam, program);
        SetWarpUniforms(seam, program, previousGlobalWarp);
        SetMatrix(seam, program, "prevProjectionMatrix", Identity);
        SetMatrix(seam, program, "prevModelViewMatrix", Identity);
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

        int x = sampleCorner ? 2 : Size / 2;
        int y = sampleCorner ? 2 : Size / 2;
        int offset = (y * Size + x) * 4;

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
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-motion-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-motion-decode.fsh" },
        }, "taa-motion-decode");

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
    /// Both halves of the warp state, pinned so the current frame's warp is a
    /// no-op and only the previous one moves. Set explicitly rather than left at
    /// zero: an unset uniform is a defined zero in GL but the value that happens
    /// to be in the block on the device path, and this test's whole point is the
    /// difference between the two states.
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
        SetFloat3(seam, program, "origin", 0f, 0f, 0f);

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

    private static void SetViewUniforms(IOptimumGraphicsDevice seam, int program)
    {
        SetFloat(seam, program, "viewDistance", 1024f);
        SetFloat(seam, program, "viewDistanceLod0", 1024f);
        SetFloat(seam, program, "alphaTest", 0.001f);
        SetFloat(seam, program, "zNear", 0.1f);
        SetFloat(seam, program, "zFar", 1024f);
        SetFloat(seam, program, "shadowRangeFar", 1024f);
        SetFloat(seam, program, "shadowRangeNear", 64f);
        SetFloat(seam, program, "shadowMapWidthInv", 1f);
        SetFloat(seam, program, "shadowMapHeightInv", 1f);
        SetFloat(seam, program, "subpixelPaddingX", 0f);
        SetFloat(seam, program, "subpixelPaddingY", 0f);
        SetFloat2(seam, program, "blockTextureSize", 1f, 1f);
        SetFloat3(seam, program, "rgbaAmbientIn", 1f, 1f, 1f);
        SetFloat2(seam, program, "frameSize", Size, Size);
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

    private static unsafe int CreateWhiteTexture(IOptimumGraphicsDevice seam)
    {
        var white = new byte[] { 255, 255, 255, 255 };
        fixed (byte* pixels = white)
        {
            return seam.CreateTexture2D(1, 1,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
        }
    }

    private static unsafe int BindEveryDeclaredSampler(
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
