using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// <c>sources/shaders-vk/include/motion.glsl</c>, the only motion writer of the native shaders
/// (docs/vulkan-native-shaders.md section 7), rendered on the device and read back.
///
/// The include is written in the common subset of GLSL 330 and 450, so a fixture fragment shader
/// that inlines its text links through the device the way today's programs do; native linking
/// from SPIR-V does not exist yet. The three cases the contract fixes:
///
///  1. a previous position in front of the previous camera writes
///     <c>rg = previousPixel - (gl_FragCoord.xy - jitter)</c>, <c>b = reactive</c>, <c>a = writerDepth</c>;
///  2. a previous position behind it (<c>w &lt;= 1e-6</c>) writes zero <c>rg</c> and <c>a</c> and
///     KEEPS <c>b</c> (temporal-frame-contract section 3.2);
///  3. the reactive-only writer writes <c>(0, 0, reactive, 0)</c>.
///
/// The fixture's previous clip position is the current NDC position plus a fixed NDC offset, so
/// the expected vector is the same at every pixel and small enough to decode with a fine scale.
/// </summary>
public class TaaMotionIncludeTests
{
    private readonly ITestOutputHelper _output;

    public TaaMotionIncludeTests(ITestOutputHelper output) => _output = output;

    private const int Size = 32;

    /// <summary>Pixels per unit in the decode pass: mv/DecodeScale * 0.5 + 0.5 into an RGBA8 channel.</summary>
    private const float DecodeScale = 8f;

    /// <summary>One RGBA8 step of the decoded vector, in pixels.</summary>
    private const float VectorTolerance = DecodeScale / 127.5f + 1e-3f;

    private const float ChannelTolerance = 1.5f / 255f;

    /// <summary>The sentinel the motion target is cleared to, so a written zero is distinguishable.</summary>
    private const float Sentinel = 0.75f;

    [SkippableTheory]
    [InlineData(0.0f, 0.0f)]
    [InlineData(0.25f, -0.375f)]
    [InlineData(-0.5f, 0.125f)]
    public void AValidPreviousPositionWritesTheUnjitteredVectorReactiveAndWriterDepth(float jitterX, float jitterY)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            // Offset NDC (3/16, -2/16) at Size 32 is (3, -2) pixels; the jitter adds itself because
            // the current pixel is gl_FragCoord minus the jitter.
            Pixel[] pixels = Render(device!, mode: 0, previousW: 2f, offsetPixelsX: 3f, offsetPixelsY: -2f,
                jitterX, jitterY, reactive: 0.3f, writerDepth: 0.625f);

            foreach (Pixel pixel in pixels)
            {
                Assert.InRange(pixel.MotionX, 3f + jitterX - VectorTolerance, 3f + jitterX + VectorTolerance);
                Assert.InRange(pixel.MotionY, -2f + jitterY - VectorTolerance, -2f + jitterY + VectorTolerance);
                Assert.InRange(pixel.Reactive, 0.3f - ChannelTolerance, 0.3f + ChannelTolerance);
                Assert.InRange(pixel.WriterDepth, 0.625f - ChannelTolerance, 0.625f + ChannelTolerance);
            }
        }
    }

    [SkippableTheory]
    [InlineData(-1f)]
    [InlineData(0f)]
    [InlineData(1e-6f)]
    public void APreviousPositionBehindThePreviousCameraKeepsReactiveAndZeroesTheRest(float previousW)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Pixel[] pixels = Render(device!, mode: 0, previousW, offsetPixelsX: 3f, offsetPixelsY: -2f,
                jitterX: 0.25f, jitterY: -0.375f, reactive: 0.3f, writerDepth: 0.625f);

            foreach (Pixel pixel in pixels)
            {
                Assert.InRange(pixel.MotionX, -VectorTolerance, VectorTolerance);
                Assert.InRange(pixel.MotionY, -VectorTolerance, VectorTolerance);
                Assert.InRange(pixel.Reactive, 0.3f - ChannelTolerance, 0.3f + ChannelTolerance);
                Assert.InRange(pixel.WriterDepth, 0f, ChannelTolerance);
            }
        }
    }

    [SkippableFact]
    public void TheReactiveOnlyWriterWritesOnlyReactive()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Pixel[] pixels = Render(device!, mode: 1, previousW: 2f, offsetPixelsX: 3f, offsetPixelsY: -2f,
                jitterX: 0.25f, jitterY: -0.375f, reactive: 0.7f, writerDepth: 0.625f);

            foreach (Pixel pixel in pixels)
            {
                Assert.InRange(pixel.MotionX, -VectorTolerance, VectorTolerance);
                Assert.InRange(pixel.MotionY, -VectorTolerance, VectorTolerance);
                Assert.InRange(pixel.Reactive, 0.7f - ChannelTolerance, 0.7f + ChannelTolerance);
                Assert.InRange(pixel.WriterDepth, 0f, ChannelTolerance);
            }
        }
    }

    private readonly record struct Pixel(float MotionX, float MotionY, float Reactive, float WriterDepth);

    private static unsafe Pixel[] Render(
        VulkanDevice seam, int mode, float previousW, float offsetPixelsX, float offsetPixelsY,
        float jitterX, float jitterY, float reactive, float writerDepth)
    {
        string motionInclude = File.ReadAllText(Path.Combine(NativeShaderTree.IncludeDirectory, "motion.glsl"));

        const string vertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
uniform vec2 offsetNdc;
uniform float previousW;
out vec4 prevClip;
void main(void)
{
	gl_Position = vec4(xyz, 1.0);
	prevClip = vec4((xyz.xy + offsetNdc) * previousW, 0.5 * previousW, previousW);
}
";
        string fragment = "#version 330 core\n" + motionInclude + @"
in vec4 prevClip;
uniform vec2 renderSize;
uniform vec2 jitterPx;
uniform float reactive;
uniform float writerDepth;
uniform int mode;
layout(location = 0) out vec4 outMotion;
void main(void)
{
	if (mode == 0) outMotion = optimumWriteMotion(prevClip, renderSize, jitterPx, reactive, writerDepth);
	else outMotion = optimumWriteReactiveOnly(reactive);
}
";
        int program = Link(seam, vertex, fragment, "motion-include-fixture");

        int motion = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMinFilter, 9728);
        seam.SetTextureParameter(motion, OptimumGlConstants.TextureMagFilter, 9728);
        int target = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, motion, 0);
        seam.SetDrawBuffers(target, 0b1);
        Assert.True(seam.CheckFramebufferComplete(target, out string status), status);

        int quad = seam.CreateMesh(BuildQuad(), staticDraw: true);
        Assert.True(quad > 0, seam.GetError() ?? "quad upload failed");

        seam.BeginFrame();
        seam.BindFramebuffer(target);
        seam.ClearColor(0, Sentinel, Sentinel, Sentinel, Sentinel);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);

        seam.UseProgram(program);
        SetFloat2(seam, program, "offsetNdc", offsetPixelsX * 2f / Size, offsetPixelsY * 2f / Size);
        SetFloat(seam, program, "previousW", previousW);
        SetFloat2(seam, program, "renderSize", Size, Size);
        SetFloat2(seam, program, "jitterPx", jitterX, jitterY);
        SetFloat(seam, program, "reactive", reactive);
        SetFloat(seam, program, "writerDepth", writerDepth);
        SetInt(seam, program, "mode", mode);
        seam.DrawMesh(quad);

        byte[] decoded = Decode(seam, motion, quad);
        seam.Present();
        GpuTest.AssertClean(seam);

        var pixels = new Pixel[Size * Size];
        for (int i = 0; i < pixels.Length; i++)
        {
            int o = i * 4;
            pixels[i] = new Pixel(
                (decoded[o] / 255f * 2f - 1f) * DecodeScale,
                (decoded[o + 1] / 255f * 2f - 1f) * DecodeScale,
                decoded[o + 2] / 255f,
                decoded[o + 3] / 255f);
        }
        return pixels;
    }

    /// <summary>
    /// The seam reads four bytes per pixel from colour attachment 0, so the RGBA16F attachment is
    /// decoded into RGBA8 by a texelFetch pass inside the same frame.
    /// </summary>
    private static unsafe byte[] Decode(VulkanDevice seam, int motionTexture, int quad)
    {
        const string vertex = @"#version 330 core
layout(location = 0) in vec3 xyz;
void main(void) { gl_Position = vec4(xyz, 1.0); }
";
        const string fragment = @"#version 330 core
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
        int decode = Link(seam, vertex, fragment, "motion-include-decode");
        int colour = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, colour, 0);
        seam.SetDrawBuffers(framebuffer, 0b1);

        seam.BindFramebuffer(framebuffer);
        seam.ClearColor(0, 0f, 0f, 0f, 0f);
        seam.UseProgram(decode);
        seam.SetSamplerUnit(decode, "motionTex", 15);
        seam.BindTexture(15, motionTexture);
        SetFloat(seam, decode, "decodeScale", DecodeScale);
        seam.SetViewport(0, 0, Size, Size);
        seam.DrawMesh(quad);

        var bytes = new byte[Size * Size * 4];
        fixed (byte* destination = bytes)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return bytes;
    }

    private static MeshData BuildQuad() =>
        new(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
        {
            xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
        };

    private static int Link(VulkanDevice seam, string vertex, string fragment, string name)
    {
        var vertexShader = new FixtureShader { Type = EnumShaderType.VertexShader, Code = vertex };
        var fragmentShader = new FixtureShader { Type = EnumShaderType.FragmentShader, Code = fragment };
        Assert.True(seam.CompileShader(vertexShader), name + ": " + (seam.GetError() ?? "vertex compile failed"));
        Assert.True(seam.CompileShader(fragmentShader), name + ": " + (seam.GetError() ?? "fragment compile failed"));
        int program = seam.LinkProgram(new FixtureProgram
        {
            PassName = name,
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
        });
        Assert.True(program > 0, name + ": " + (seam.GetError() ?? "link failed"));
        return program;
    }

    private static void SetFloat(VulkanDevice seam, int program, string name, float value)
    {
        int location = seam.GetUniformLocation(program, name);
        Assert.True(location >= 0, name + " has no location");
        seam.SetUniform(program, location, value);
    }

    private static void SetInt(VulkanDevice seam, int program, string name, int value)
    {
        int location = seam.GetUniformLocation(program, name);
        Assert.True(location >= 0, name + " has no location");
        seam.SetUniform(program, location, value);
    }

    private static void SetFloat2(VulkanDevice seam, int program, string name, float x, float y)
    {
        int location = seam.GetUniformLocation(program, name);
        Assert.True(location >= 0, name + " has no location");
        seam.SetUniform(program, location, x, y);
    }

    private sealed class FixtureShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    private sealed class FixtureProgram : IShaderProgram
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
