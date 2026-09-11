using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The instanced motion-vector writer (TAA P3), driven through the seam with the
/// real instanced program and read back as pixels. This is the writer every
/// mechanical-power block goes through - axles, gears, clutches, transmissions,
/// creative rotors, pulverizers - all of them drawn as instances of one mesh.
///
/// What it has to get right, and what a "the gear moved, so motion is non-zero"
/// assertion could not tell apart from a sign flip, an axis swap or a shared
/// uniform standing in for a per-instance attribute:
/// - a previous instance transform turns into exactly that displacement in render
///   pixels, with the sign "where the pixel was";
/// - a gear that did not turn under a camera that did not move is exactly (0, 0);
/// - two instances in ONE draw get their OWN previous transforms - the failure
///   mode of the whole design is every instance reading one gear's matrix;
/// - an instance whose history the C# side could not match (a new device, a
///   reordered buffer) falls back to camera-only motion and carries reactive 1,
///   instead of reprojecting by whatever matrix landed in its slot.
///
/// As in TaaStandardMotionWriterTests the RGBA16F attachment comes back through
/// an RGBA8 decode pass, because the seam's readback is fixed at four bytes per
/// pixel from colour attachment 0. Decode quantisation is 2*DecodeScale/255 px,
/// so the tolerances stay above it.
/// </summary>
public class TaaInstancedMotionWriterTests
{
    private readonly ITestOutputHelper _output;

    public TaaInstancedMotionWriterTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 32f;

    /// <summary>Normal pointing up, no glow.</summary>
    private const int UpNormalFlags = 7 << 18;

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
    /// A gear that did not turn, under a camera that did not move, is zero motion -
    /// and the writer still stamps its own depth, so the resolve accepts the pixel
    /// instead of silently falling back to camera reprojection.
    /// </summary>
    [SkippableFact]
    public void AnUnmovedInstanceWritesZeroMotionAndTheFragmentDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            var instance = new Instance(Identity, Identity, historyValid: true);
            Decoded centre = RenderInstancedMotion(device!, new[] { instance }, 0f, 0f)[Size / 2];

            _output.WriteLine($"still: mv = ({centre.MotionX}, {centre.MotionY}), writerDepth = {centre.WriterDepth}");

            Assert.InRange(centre.MotionX, -0.3f, 0.3f);
            Assert.InRange(centre.MotionY, -0.3f, 0.3f);
            // Identity matrices put the quad at NDC z = 0, which is window depth
            // 0.5 on both backends.
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The transform this instance was drawn with last frame is its motion: with
    /// identity camera matrices a translation of d moves the pixel by exactly
    /// d * 0.5 * renderSize, and the sign is "where the pixel was", not "where it
    /// went".
    /// </summary>
    [SkippableTheory]
    [InlineData(0.25f, 0f)]
    [InlineData(0f, -0.125f)]
    [InlineData(-0.1875f, 0.0625f)]
    public void APreviousInstanceTransformShowsUpAsTheExactPixelDisplacement(float prevX, float prevY)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            var instance = new Instance(Identity, Translation(prevX, prevY, 0f), historyValid: true);
            Decoded centre = RenderInstancedMotion(device!, new[] { instance }, 0f, 0f)[Size / 2];

            float expectedX = prevX * 0.5f * Size;
            float expectedY = prevY * 0.5f * Size;

            _output.WriteLine($"prev ({prevX}, {prevY}): mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY})");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.WriterDepth, 0.48f, 0.52f);
        }
    }

    /// <summary>
    /// The point of putting the previous transform in the instance stream rather
    /// than in a uniform: two devices drawn by ONE instanced draw call each get
    /// their own previous transform. A writer that took it from a uniform - or
    /// that keyed history on the buffer slot instead of the device - would give
    /// both halves of this image the same vector, which is what a whole gear
    /// network smearing into one direction looks like on screen.
    /// </summary>
    [SkippableFact]
    public void EachInstanceInOneDrawGetsItsOwnPreviousTransform()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float leftPrevX = 0.25f;
            const float rightPrevY = -0.125f;

            // The current transforms put instance 0 in the left half of the image
            // and instance 1 in the right half; the quad is one unit across, so the
            // halves do not overlap.
            var left = new Instance(
                Translation(-0.5f, 0f, 0f),
                MultiplyTranslations(-0.5f + leftPrevX, 0f),
                historyValid: true);
            var right = new Instance(
                Translation(0.5f, 0f, 0f),
                MultiplyTranslations(0.5f, rightPrevY),
                historyValid: true);

            Decoded[] row = RenderInstancedMotion(device!, new[] { left, right }, 0f, 0f);
            Decoded leftPixel = row[Size / 4];
            Decoded rightPixel = row[Size * 3 / 4];

            float expectedLeftX = leftPrevX * 0.5f * Size;
            float expectedRightY = rightPrevY * 0.5f * Size;

            _output.WriteLine($"left: mv = ({leftPixel.MotionX}, {leftPixel.MotionY}), expected ({expectedLeftX}, 0)");
            _output.WriteLine($"right: mv = ({rightPixel.MotionX}, {rightPixel.MotionY}), expected (0, {expectedRightY})");

            Assert.InRange(leftPixel.MotionX, expectedLeftX - 0.3f, expectedLeftX + 0.3f);
            Assert.InRange(leftPixel.MotionY, -0.3f, 0.3f);
            Assert.InRange(rightPixel.MotionX, -0.3f, 0.3f);
            Assert.InRange(rightPixel.MotionY, expectedRightY - 0.3f, expectedRightY + 0.3f);
        }
    }

    /// <summary>
    /// Without usable history - a device placed this frame, a chunk that streamed
    /// in, a buffer whose instances were reordered - the writer must not read the
    /// previous transform at all. It falls back to treating the block as static in
    /// the world, so only the camera's own movement displaces it, and the reactive
    /// channel the C# side stamped comes through so the resolve leans on this frame.
    ///
    /// The previous transform here is deliberately a large translation: if the
    /// shader took the history branch anyway, the vector would be that instead.
    /// </summary>
    [SkippableFact]
    public void WithoutUsableHistoryTheVectorIsCameraMotionOnlyAndReactive()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            const float cameraDeltaX = 0.25f;
            const float cameraDeltaY = -0.125f;

            var instance = new Instance(Identity, Translation(-0.5f, 0.5f, 0f), historyValid: false);
            Decoded centre = RenderInstancedMotion(device!, new[] { instance }, cameraDeltaX, cameraDeltaY)[Size / 2];

            float expectedX = cameraDeltaX * 0.5f * Size;
            float expectedY = cameraDeltaY * 0.5f * Size;

            _output.WriteLine($"no history: mv = ({centre.MotionX}, {centre.MotionY}), " +
                              $"expected ({expectedX}, {expectedY}), reactive = {centre.Reactive}");

            Assert.InRange(centre.MotionX, expectedX - 0.3f, expectedX + 0.3f);
            Assert.InRange(centre.MotionY, expectedY - 0.3f, expectedY + 0.3f);
            Assert.InRange(centre.Reactive, 0.9f, 1.1f);
        }
    }

    // ---------------------------------------------------------------- harness

    private readonly struct Instance
    {
        public Instance(float[] transform, float[] previousTransform, bool historyValid)
        {
            Transform = transform;
            PreviousTransform = previousTransform;
            HistoryValid = historyValid;
        }

        public float[] Transform { get; }
        public float[] PreviousTransform { get; }
        public bool HistoryValid { get; }
    }

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

    /// <summary>Two translations composed: the current placement plus the previous offset.</summary>
    private static float[] MultiplyTranslations(float x, float y) => Translation(x, y, 0f);

    /// <summary>
    /// Draws the given instances with the real instanced program compiled as a
    /// motion writer, then decodes the motion attachment and returns the middle
    /// scanline. Every expectation is stated in terms of the previous-frame inputs:
    /// both camera matrices are the identity and no jitter is applied.
    /// </summary>
    private unsafe Decoded[] RenderInstancedMotion(
        VulkanDevice device, Instance[] instances, float cameraDeltaX, float cameraDeltaY)
    {
        IOptimumGraphicsDevice seam = device;

        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        var variant = new ShaderCorpus.ShaderVariant
        {
            Name = "taa-instanced",
            TaaMotion = 1,
            TaaMotionLocation = 2,
        };

        List<ShaderStageSource> stages = ShaderCorpus.BuildProgram("instanced", files, includes, variant);
        Assert.NotEmpty(stages);
        int program = LinkFromCorpus(seam, stages, "instanced");

        Assert.True(seam.GetUniformLocation(program, "taaRenderSize") >= 0,
            "instanced declares no taaRenderSize, so it is not a motion writer");
        Assert.True(seam.GetUniformLocation(program, "prevModelViewMatrix") >= 0,
            "instanced declares no prevModelViewMatrix, so it has no previous camera");

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

        int mesh = seam.CreateMesh(BuildInstancedQuad(instances), staticDraw: false);
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
        SetSceneUniforms(seam, program);

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
        seam.DrawMeshInstanced(mesh, instances.Length);

        byte[] decoded = DecodeMotion(seam, motion);
        seam.Present();

        AssertClean(seam);

        var row = new Decoded[Size];
        for (int x = 0; x < Size; x++)
        {
            int offset = ((Size / 2) * Size + x) * 4;
            row[x] = new Decoded(
                (decoded[offset] / 255f * 2f - 1f) * DecodeScale,
                (decoded[offset + 1] / 255f * 2f - 1f) * DecodeScale,
                decoded[offset + 2] / 255f,
                decoded[offset + 3] / 255f);
        }
        return row;
    }

    /// <summary>
    /// Reads the RGBA16F motion attachment through an RGBA8 decode pass, because
    /// the seam's readback is fixed at four bytes per pixel from attachment 0.
    /// Unlike the other writers' harnesses this one carries the reactive channel
    /// too, because "no history" and "reactive" are one decision here.
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
		clamp(m.b, 0.0, 1.0),
		clamp(m.a, 0.0, 1.0));
}
";
        int decode = LinkFromCorpus(seam, new List<ShaderStageSource>
        {
            new() { Stage = EnumShaderType.VertexShader, Code = decodeVertex, PrefixCode = "", Filename = "taa-instanced-decode.vsh" },
            new() { Stage = EnumShaderType.FragmentShader, Code = decodeFragment, PrefixCode = "", Filename = "taa-instanced-decode.fsh" },
        }, "taa-instanced-decode");

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
    /// A quad in instanced.vsh's attribute layout - xyz, uv, rgbaBlockIn, flags,
    /// with no normals, which is what puts uv on location 1 - plus the instance
    /// stream in OptimumInstanceMotion's layout: light rgba at 4, transform at
    /// 5..8, previous transform at 9..12 and the TAA metadata at 13. The instance
    /// values are written by hand rather than through
    /// OptimumInstanceMotion.WriteInstance so the shader contract is tested
    /// independently of the history bookkeeping (which
    /// Optimum.Tests/taa-instanced-motion-tests.cs drives directly).
    /// </summary>
    private static MeshData BuildInstancedQuad(Instance[] instances)
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
                ColorUtil.WhiteArgb,
                flags: UpNormalFlags);
        }

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }

        CustomMeshDataPartFloat part = OptimumInstanceMotion.CreateInstanceFloats(instances.Length);
        for (int i = 0; i < instances.Length; i++)
        {
            int j = i * OptimumInstanceMotion.InstanceFloats;
            part.Values[j + OptimumInstanceMotion.LightOffset] = 1f;
            part.Values[j + OptimumInstanceMotion.LightOffset + 1] = 1f;
            part.Values[j + OptimumInstanceMotion.LightOffset + 2] = 1f;
            part.Values[j + OptimumInstanceMotion.LightOffset + 3] = 1f;
            Array.Copy(instances[i].Transform, 0, part.Values, j + OptimumInstanceMotion.TransformOffset, 16);
            Array.Copy(instances[i].PreviousTransform, 0, part.Values, j + OptimumInstanceMotion.PrevTransformOffset, 16);
            part.Values[j + OptimumInstanceMotion.MetaOffset] = instances[i].HistoryValid ? 1f : 0f;
            part.Values[j + OptimumInstanceMotion.MetaOffset + 1] = instances[i].HistoryValid ? 0f : 1f;
        }
        part.Count = instances.Length * OptimumInstanceMotion.InstanceFloats;
        mesh.CustomFloats = part;
        return mesh;
    }

    /// <summary>
    /// Enough of the lighting, fog and shadow surface to keep the fragment alive:
    /// a fragment below alphaTest is discarded before it can write a motion
    /// vector, and the test would read the cleared attachment instead.
    /// </summary>
    private static void SetSceneUniforms(IOptimumGraphicsDevice seam, int program)
    {
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
        SetFloat(seam, program, "extraGodray", 0f);
        SetFloat(seam, program, "ssaoAttn", 0f);
        SetInt(seam, program, "applySsao", 0);
        SetInt(seam, program, "normalShaded", 0);
        SetInt(seam, program, "skyShaded", 0);
        SetFloat3(seam, program, "rgbaAmbientIn", 1f, 1f, 1f);
        SetFloat4(seam, program, "rgbaFogIn", 1f, 1f, 1f, 1f);
        SetFloat4(seam, program, "averageColor", 1f, 1f, 1f, 1f);
        SetFloat2(seam, program, "frameSize", Size, Size);
        SetFloat3(seam, program, "playerpos", 0f, 0f, 0f);
        SetFloat(seam, program, "timeCounter", 0f);
        SetFloat(seam, program, "windWaveCounter", 0f);
        SetFloat(seam, program, "windWaveCounterHighFreq", 0f);
        SetFloat(seam, program, "waterWaveCounter", 0f);
        SetFloat(seam, program, "windSpeed", 0f);
        SetFloat(seam, program, "globalWarpIntensity", 0f);
        SetFloat(seam, program, "glitchWaviness", 0f);
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
