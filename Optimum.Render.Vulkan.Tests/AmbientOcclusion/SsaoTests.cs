// Source: Optimum.Render.Vulkan.Tests/SceneSsaoTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The AO multiply the TAA path runs before the resolve: scene-ssao draws into Primary
/// with only colour 0 selected and the Multiply blend, so the resolve accumulates the
/// occlusion together with the scene. Every other Primary attachment and the depth have
/// to come out untouched, frame after frame.
/// </summary>
public class SceneSsaoTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    public unsafe void OcclusionMultipliesOnlySceneColourBeforeTheResolve(int quality)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 8, frames = 8;
            var files = ShaderCorpus.LoadShaderFiles();
            int program = GpuTest.LinkProgram(
                seam,
                files["scene-ssao.vsh"],
                files["scene-ssao.fsh"].Replace("#version 330 core", "#version 330 core\n#define SSAOLEVEL " + quality),
                "scene-ssao" + quality);

            // Rows alternate between two AO levels, so SSAOLEVEL 2's min with the row
            // above is visible: every row sees the darker of the two.
            var ao = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                for (int c = 0; c < 4; c++) ao[(y * size + x) * 4 + c] = (byte)(y % 2 == 0 ? 64 : 192);
            int occlusion;
            fixed (byte* data = ao) occlusion = seam.CreateTexture2DRaw(size, size, 0x8058, (IntPtr)data, 4);

            var colors = new int[frames][];
            var depths = new int[frames];
            var targets = new int[frames];
            for (int i = 0; i < frames; i++)
            {
                targets[i] = seam.CreateFramebuffer(size, size);
                colors[i] = new int[5];
                for (int slot = 0; slot < 5; slot++)
                {
                    colors[i][slot] = seam.CreateTexture2DRaw(size, size, 0x8058, IntPtr.Zero, 4);
                    seam.AttachTexture(targets[i], (EnumFramebufferAttachment)(36064 + slot), colors[i][slot], 0);
                }
                depths[i] = seam.CreateTexture2D(size, size,
                    EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
                seam.AttachTexture(targets[i], EnumFramebufferAttachment.DepthAttachment, depths[i], 0);
            }

            seam.SetViewport(0, 0, size, size);
            seam.SetCullFace(false);
            seam.SetDepthTest(false);
            seam.SetSamplerUnit(program, "ssaoScene", 0);
            seam.SetUniform(program, seam.GetUniformLocation(program, "invRenderHeight"), 1f / size);
            for (int i = 0; i < frames; i++)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(targets[i]);
                seam.SetDrawBuffers(targets[i], 31);
                seam.ClearColor(0, (i + 1) / 16f, (i + 1) / 16f, (i + 1) / 16f, 1);
                for (int slot = 1; slot < 5; slot++) seam.ClearColor(slot, slot / 8f, slot / 8f, slot / 8f, 1);
                seam.ClearDepth(0.375f);
                seam.SetDrawBuffers(targets[i], 1);
                seam.SetBlend(true, EnumBlendMode.Multiply);
                seam.UseProgram(program);
                seam.BindTexture(0, occlusion);
                seam.DrawFullscreenTriangle();
                seam.SetBlend(true, EnumBlendMode.Standard);
                seam.SetDrawBuffers(targets[i], 31);
                seam.Present();
            }

            // The sequence completes before any readback or CPU wait.
            seam.BeginFrame();
            for (int i = 0; i < frames; i++)
            {
                for (int slot = 0; slot < 5; slot++)
                {
                    byte[] pixels = seam.ReadBackLevel0ForTests(colors[i][slot]);
                    for (int y = 1; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        double factor = quality == 2 || y % 2 == 0 ? 64 : 192;
                        double expected = slot == 0 ? Math.Round((i + 1) / 16.0 * 255) * factor / 255 : slot / 8.0 * 255;
                        int offset = (y * size + x) * 4;
                        for (int c = 0; c < 3; c++) Assert.InRange((double)pixels[offset + c], expected - 1.1, expected + 1.1);
                        Assert.Equal(255, pixels[offset + 3]);
                    }
                }
                foreach (float depth in MemoryMarshal.Cast<byte, float>(seam.ReadBackLevel0ForTests(depths[i])))
                    Assert.Equal(0.375f, depth);
            }
            seam.Present();
            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The OPTIMUMAO variant (docs/vulkan.md#ambient-occlusion C.9, C.11): the GTAO visibility is
    /// fetched at render resolution with no min-of-two-rows, attenuated by vanilla's water, fog and
    /// OIT term (gPosition.w + 0.75 * (1 - revealage)), and still multiplies only colour 0.
    /// </summary>
    [SkippableFact]
    public unsafe void GtaoVisibilityIsComposedWithTheVanillaAttenuationAndNoRowMin()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 8, frames = 4;
            const float positionW = 0.25f, revealage = 0.6f;
            var files = ShaderCorpus.LoadShaderFiles();
            int program = GpuTest.LinkProgram(
                seam,
                files["scene-ssao.vsh"],
                files["scene-ssao.fsh"].Replace("#version 330 core", "#version 330 core\n#define SSAOLEVEL 2\n#define OPTIMUMAO 1"),
                "scene-ssao-gtao");

            var ao = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                for (int c = 0; c < 4; c++) ao[(y * size + x) * 4 + c] = (byte)(y % 2 == 0 ? 64 : 192);
            int visibility;
            fixed (byte* data = ao) visibility = seam.CreateTexture2DRaw(size, size, 0x8058, (IntPtr)data, 4);

            int position = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int reveal = seam.CreateTexture2DRaw(size, size, 0x8058, IntPtr.Zero, 4);
            int inputs = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(inputs, EnumFramebufferAttachment.ColorAttachment0, position, 0);
            seam.AttachTexture(inputs, EnumFramebufferAttachment.ColorAttachment1, reveal, 0);
            seam.SetDrawBuffers(inputs, 3);

            var colors = new int[frames][];
            var targets = new int[frames];
            for (int i = 0; i < frames; i++)
            {
                targets[i] = seam.CreateFramebuffer(size, size);
                colors[i] = new int[3];
                for (int slot = 0; slot < 3; slot++)
                {
                    colors[i][slot] = seam.CreateTexture2DRaw(size, size, 0x8058, IntPtr.Zero, 4);
                    seam.AttachTexture(targets[i], (EnumFramebufferAttachment)(36064 + slot), colors[i][slot], 0);
                }
            }

            seam.SetViewport(0, 0, size, size);
            seam.SetCullFace(false);
            seam.SetDepthTest(false);
            seam.SetSamplerUnit(program, "ssaoScene", 0);
            seam.SetSamplerUnit(program, "gPositionScene", 1);
            seam.SetSamplerUnit(program, "revealageScene", 2);
            seam.SetUniform(program, seam.GetUniformLocation(program, "invRenderHeight"), 1f / size);
            seam.SetUniform(program, seam.GetUniformLocation(program, "optimumAoMode"), 1);
            for (int i = 0; i < frames; i++)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(inputs);
                seam.ClearColor(0, 0f, 0f, 0f, positionW);
                seam.ClearColor(1, revealage, 0f, 0f, 1f);
                seam.BindFramebuffer(targets[i]);
                seam.SetDrawBuffers(targets[i], 7);
                seam.ClearColor(0, 0.75f, 0.75f, 0.75f, 1f);
                seam.ClearColor(1, 0.25f, 0.25f, 0.25f, 1f); // glow: never touched
                seam.ClearColor(2, 0.5f, 0.5f, 0.5f, 1f);
                seam.SetDrawBuffers(targets[i], 1);
                seam.SetBlend(true, EnumBlendMode.Multiply);
                seam.UseProgram(program);
                seam.BindTexture(0, visibility);
                seam.BindTexture(1, position);
                seam.BindTexture(2, reveal);
                seam.DrawFullscreenTriangle();
                seam.SetBlend(true, EnumBlendMode.Standard);
                seam.SetDrawBuffers(targets[i], 7);
                seam.Present();
            }

            double attenuate = positionW + (1.0 - Math.Round(revealage * 255) / 255.0) * 0.75;
            seam.BeginFrame();
            for (int i = 0; i < frames; i++)
            {
                for (int slot = 0; slot < 3; slot++)
                {
                    byte[] pixels = seam.ReadBackLevel0ForTests(colors[i][slot]);
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        double v = (y % 2 == 0 ? 64 : 192) / 255.0;
                        double factor = Math.Clamp(1.0 - (1.0 - v) * (1.0 - attenuate), 0.0, 1.0);
                        double expected = slot switch
                        {
                            0 => Math.Round(0.75 * 255) * factor,
                            1 => Math.Round(0.25 * 255),
                            _ => Math.Round(0.5 * 255),
                        };
                        int offset = (y * size + x) * 4;
                        for (int c = 0; c < 3; c++) Assert.InRange((double)pixels[offset + c], expected - 1.1, expected + 1.1);
                    }
                }
            }
            seam.Present();
            GpuTest.AssertClean(seam);
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/SsaoNoiseAlphaTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Parity gap from the Phase 0 and Phase 1 dumps: framebuffer slot 13 (SSAO) colour
/// attachment 1, the 16x16 rotation-noise texture, read alpha 1.0 in every texel on
/// OpenGL and 0.0 on Vulkan.
///
/// Not a masked write (no shader renders into that texture): the GL path allocates
/// GL_RGBA32F and uploads GL_RGB float data, and GL's pixel transfer fills the absent
/// alpha with 1. The device path uploaded four channels with a padding 0, and the device
/// copies channels verbatim. The fix is in the data (<see cref="VulkanClientPlatform.BuildOptimumSsaoNoise" />);
/// these tests read the texture back through the parity dump's own readback, inside a
/// frame, with sync + best-practices validation on.
/// </summary>
public class SsaoNoiseAlphaTests
{
    private const int NoiseSize = 16;
    private const int GlRgba32f = 0x8814; // 34836, what both paths allocate

    private readonly ITestOutputHelper _output;

    public SsaoNoiseAlphaTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The GL path's generation verbatim (ClientPlatformWindows.SetupDefaultFrameBuffers):
    /// three floats per texel, uploaded as GL_RGB.
    /// </summary>
    private static float[] GlRgbNoise(Random random)
    {
        float[] rgb = new float[NoiseSize * NoiseSize * 3];
        Vec3f direction = new Vec3f();
        for (int texel = 0; texel < NoiseSize * NoiseSize; texel++)
        {
            direction.Set((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, 0f).Normalize();
            rgb[texel * 3] = direction.X;
            rgb[texel * 3 + 1] = direction.Y;
            rgb[texel * 3 + 2] = direction.Z;
        }
        return rgb;
    }

    /// <summary>
    /// Same colour values and the same random draws as the GL path, alpha 1 where GL fills
    /// it, and the stream position afterwards unchanged, so the kernel drawn next matches.
    /// </summary>
    [Fact]
    public void NoiseMatchesTheGlUploadIncludingTheFilledAlpha()
    {
        var glRandom = new Random(5);
        var deviceRandom = new Random(5);
        float[] rgb = GlRgbNoise(glRandom);
        float[] rgba = VulkanClientPlatform.BuildOptimumSsaoNoise(deviceRandom, NoiseSize);

        Assert.Equal(NoiseSize * NoiseSize * 4, rgba.Length);
        for (int texel = 0; texel < NoiseSize * NoiseSize; texel++)
        {
            Assert.Equal(rgb[texel * 3], rgba[texel * 4]);
            Assert.Equal(rgb[texel * 3 + 1], rgba[texel * 4 + 1]);
            Assert.Equal(rgb[texel * 3 + 2], rgba[texel * 4 + 2]);
            Assert.Equal(1f, rgba[texel * 4 + 3]);
        }
        Assert.Equal(glRandom.NextDouble(), deviceRandom.NextDouble());
    }

    /// <summary>
    /// GPU readback through <see cref="VulkanDevice.ReadTextureForParity" />, the path that
    /// writes 13-SSAO-color1-rgba32f.alpha.pfm. The platform's noise reads alpha 1.0 in all
    /// 256 texels with the GL colour values; the pre-fix upload (identical colour, padding
    /// alpha 0) on the same device reads 0.0, which is the Vulkan dump before the fix.
    /// </summary>
    [SkippableFact]
    public void SsaoNoiseTextureReadsAlphaOneLikeOpenGl()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        float[] noise = VulkanClientPlatform.BuildOptimumSsaoNoise(new Random(5), NoiseSize);
        float[] preFix = (float[])noise.Clone();
        for (int texel = 0; texel < NoiseSize * NoiseSize; texel++) preFix[texel * 4 + 3] = 0f;
        float[] glRgb = GlRgbNoise(new Random(5));

        using (device)
        {
            VulkanDevice seam = device!;
            int fixedTexture = Upload(seam, noise);
            int preFixTexture = Upload(seam, preFix);

            seam.BeginFrame();
            OptimumTextureReadback? fixedReadback = seam.ReadTextureForParity(fixedTexture);
            OptimumTextureReadback? preFixReadback = seam.ReadTextureForParity(preFixTexture);
            seam.Present();
            GpuTest.AssertClean(seam);

            Assert.NotNull(fixedReadback);
            Assert.NotNull(preFixReadback);
            Assert.Equal(GlRgba32f, fixedReadback!.GlInternalFormat);
            Assert.Equal(NoiseSize, fixedReadback.Width);
            Assert.Equal(NoiseSize, fixedReadback.Height);
            Assert.NotNull(fixedReadback.Floats);
            Assert.NotNull(preFixReadback!.Floats);

            int alphaOne = 0;
            int preFixAlphaZero = 0;
            for (int texel = 0; texel < NoiseSize * NoiseSize; texel++)
            {
                Assert.Equal(glRgb[texel * 3], fixedReadback.Floats![texel * 4]);
                Assert.Equal(glRgb[texel * 3 + 1], fixedReadback.Floats[texel * 4 + 1]);
                Assert.Equal(glRgb[texel * 3 + 2], fixedReadback.Floats[texel * 4 + 2]);
                if (fixedReadback.Floats[texel * 4 + 3] == 1f) alphaOne++;
                if (preFixReadback.Floats![texel * 4 + 3] == 0f) preFixAlphaZero++;
            }
            _output.WriteLine("alpha 1.0 texels: " + alphaOne + "/256; pre-fix upload alpha 0.0 texels: " + preFixAlphaZero + "/256");
            Assert.Equal(NoiseSize * NoiseSize, preFixAlphaZero);
            Assert.Equal(NoiseSize * NoiseSize, alphaOne);
        }
    }

    private static int Upload(VulkanDevice seam, float[] texels)
    {
        GCHandle handle = GCHandle.Alloc(texels, GCHandleType.Pinned);
        try
        {
            return seam.CreateTexture2DRaw(NoiseSize, NoiseSize, GlRgba32f, handle.AddrOfPinnedObject(), 16);
        }
        finally
        {
            handle.Free();
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/SsaoTemporalDitherTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// GTAO roadmap step 2. Vanilla's ssao.fsh rotates its sample kernel with a Bayer-128
/// dither locked to the screen grid: under a jittered camera every surface point draws
/// a different kernel every frame, and a temporal accumulator can only fight that, never
/// average it. Optimum's override advances the dither by the golden ratio per frame,
/// using the temporal pipeline's own frame index, so successive frames sample
/// complementary spiral directions.
///
/// The two things worth pinning are both here, on the real shader, run on the device:
/// with the temporal pipeline on (TAAMOTION 1) the AO of a fixed scene changes between
/// consecutive frames, and with it off (TAAMOTION 0) it does not change at all - the
/// override has to be byte-identical to vanilla there, because a per-frame-varying
/// dither with nothing accumulating behind it is strictly worse than a fixed one.
/// </summary>
public class SsaoTemporalDitherTests(ITestOutputHelper output)
{
    private const int Size = 128;
    private const int GlRgba32f = 0x8814;
    private const int GlRgba8 = 0x8058;
    private const int KernelSize = 64;

    /// <summary>Frame index the AO is rendered at, in order. The third repeats the first.</summary>
    private static readonly float[] FrameIndices = [0f, 1f, 0f, 2f];

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheAoVariesPerFrameOnlyWhileTheTemporalPipelineIsOn(int quality)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");

        using (device)
        {
            VulkanDevice seam = device!;
            var files = ShaderCorpus.LoadShaderFiles();

            byte[][] withTemporal = RenderSequence(seam, files, quality, taaMotion: 1);
            byte[][] withoutTemporal = RenderSequence(seam, files, quality, taaMotion: 0);

            // The scene really is occluded: several AO levels, not a flat 255. Printed
            // because a scene that stops occluding would make every comparison below
            // trivially pass with the temporal term removed.
            var seen = new SortedSet<int>();
            for (int i = 0; i < Size * Size; i++) seen.Add(withTemporal[0][i * 4]);
            output.WriteLine("distinct AO values in frame 0: " + seen.Count + " -> " + string.Join(",", seen));
            Assert.True(seen.Count > 1, "the test scene produced no occlusion at all");

            int changed = Differing(withTemporal[0], withTemporal[1]);
            int repeated = Differing(withTemporal[0], withTemporal[2]);
            int changedAgain = Differing(withTemporal[1], withTemporal[3]);
            output.WriteLine($"SSAOLEVEL {quality}: temporal on - frame 0 vs 1: {changed} px differ, " +
                             $"1 vs 2: {changedAgain} px differ, frame 0 vs the frame that repeats index 0: {repeated} px differ");

            // A different frame index really does rotate the kernel: a good share of the
            // scene lands on a different occlusion value. The threshold is a tenth of the
            // image, far below what the pass actually moves, so it fails on "the uniform
            // is ignored", not on a driver's rounding.
            Assert.True(changed > Size * Size / 10, $"AO did not vary between frames (only {changed} px)");
            Assert.True(changedAgain > Size * Size / 10, $"AO did not vary between frames (only {changedAgain} px)");
            // ... and the same index gives the same AO, so what varies is the frame index
            // and not the device.
            Assert.Equal(0, repeated);

            int off = Differing(withoutTemporal[0], withoutTemporal[1]);
            int offAgain = Differing(withoutTemporal[1], withoutTemporal[3]);
            output.WriteLine($"SSAOLEVEL {quality}: temporal off - frame 0 vs 1: {off} px differ, 1 vs 3: {offAgain} px differ");
            Assert.Equal(0, off);
            Assert.Equal(0, offAgain);

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// Renders the same scene once per entry in <see cref="FrameIndices" />, each into its
    /// own target, and reads them all back afterwards - the sequence has to complete before
    /// any CPU wait, or the readback is what makes the frames differ.
    /// </summary>
    private static byte[][] RenderSequence(
        VulkanDevice seam, Dictionary<string, string> files, int quality, int taaMotion)
    {
        string defines = "#version 330 core\n#define SSAOLEVEL " + quality + "\n#define TAAMOTION " + taaMotion + "\n";
        int program = GpuTest.LinkProgram(
            seam,
            files["ssao.vsh"],
            files["ssao.fsh"].Replace("#version 330 core", defines),
            "ssao" + quality + "-taa" + taaMotion);

        (float[] positions, float[] normals) = Scene();
        int gPosition = UploadFloat(seam, positions);
        int gNormal = UploadFloat(seam, normals);
        int revealage = UploadOpaqueRed(seam);
        // The pass never reads texNoise (the dither replaced it), but the sampler is
        // declared, so it still needs something bound.
        int noise = UploadFloat(seam, new float[Size * Size * 4]);

        int frames = FrameIndices.Length;
        var targets = new int[frames];
        var colors = new int[frames];
        for (int i = 0; i < frames; i++)
        {
            targets[i] = seam.CreateFramebuffer(Size, Size);
            colors[i] = seam.CreateTexture2DRaw(Size, Size, GlRgba8, IntPtr.Zero, 4);
            seam.AttachTexture(targets[i], EnumFramebufferAttachment.ColorAttachment0, colors[i], 0);
        }

        seam.SetViewport(0, 0, Size, Size);
        seam.SetCullFace(false);
        seam.SetDepthTest(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.SetSamplerUnit(program, "gPosition", 0);
        seam.SetSamplerUnit(program, "gNormal", 1);
        seam.SetSamplerUnit(program, "texNoise", 2);
        seam.SetSamplerUnit(program, "revealage", 3);
        seam.SetUniform(program, seam.GetUniformLocation(program, "screenSize"), (float)Size, (float)Size);
        seam.SetUniformMatrix(program, seam.GetUniformLocation(program, "projection"), Projection());
        seam.SetUniformArray3(program, seam.GetUniformLocation(program, "samples"), KernelSize, Kernel());

        // -1 when the shader was built without the temporal pipeline: the uniform is
        // inside #if TAAMOTION == 1, exactly like the pass's own set is inside
        // "if (OptimumConfig.EffectiveTaa)".
        int frameIndexLocation = seam.GetUniformLocation(program, "temporalFrameIndex");
        Assert.Equal(taaMotion == 1, frameIndexLocation >= 0);

        for (int i = 0; i < frames; i++)
        {
            seam.BeginFrame();
            seam.BindFramebuffer(targets[i]);
            seam.SetDrawBuffers(targets[i], 1);
            seam.ClearColor(0, 0, 0, 0, 1);
            seam.UseProgram(program);
            if (frameIndexLocation >= 0) seam.SetUniform(program, frameIndexLocation, FrameIndices[i]);
            seam.BindTexture(0, gPosition);
            seam.BindTexture(1, gNormal);
            seam.BindTexture(2, noise);
            seam.BindTexture(3, revealage);
            seam.DrawFullscreenTriangle();
            seam.Present();
        }

        var readback = new byte[frames][];
        seam.BeginFrame();
        for (int i = 0; i < frames; i++) readback[i] = seam.ReadBackLevel0ForTests(colors[i]);
        seam.Present();
        return readback;
    }

    /// <summary>
    /// A view-space G-buffer the projection above reproduces exactly: a wall at 10 m
    /// with 4x4 pixel blocks raised to 9.7 m, normals facing the camera. The raised
    /// blocks put occluders within the shader's depth window all over the image, so
    /// the AO of most pixels depends on which way the kernel points.
    /// </summary>
    private static (float[] Positions, float[] Normals) Scene()
    {
        var positions = new float[Size * Size * 4];
        var normals = new float[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool raised = ((x / 4) + (y / 4)) % 2 == 0;
                float distance = raised ? 9.7f : 10f;
                float ndcX = (x + 0.5f) / Size * 2f - 1f;
                float ndcY = (y + 0.5f) / Size * 2f - 1f;
                int texel = (y * Size + x) * 4;
                positions[texel] = ndcX * distance * TanHalfFov;
                positions[texel + 1] = ndcY * distance * TanHalfFov;
                positions[texel + 2] = -distance;
                positions[texel + 3] = 0f;   // attenuate
                normals[texel + 2] = 1f;
                normals[texel + 3] = 0f;     // leavesHack off
            }
        }
        return (positions, normals);
    }

    private const float TanHalfFov = 0.7002075f; // tan(70 deg / 2)

    /// <summary>Column-major perspective, 70 degrees, square, near 0.1, far 100.</summary>
    private static float[] Projection()
    {
        var m = new float[16];
        float f = 1f / TanHalfFov;
        const float near = 0.1f, far = 100f;
        m[0] = f;
        m[5] = f;
        m[10] = (far + near) / (near - far);
        m[11] = -1f;
        m[14] = 2f * far * near / (near - far);
        return m;
    }

    /// <summary>
    /// A hemisphere kernel in the shape the client uploads: directions in the +z
    /// hemisphere, pulled towards the origin quadratically so near samples dominate.
    /// </summary>
    private static float[] Kernel()
    {
        var random = new Random(7);
        var kernel = new float[KernelSize * 3];
        for (int i = 0; i < KernelSize; i++)
        {
            double x = random.NextDouble() * 2.0 - 1.0;
            double y = random.NextDouble() * 2.0 - 1.0;
            double z = random.NextDouble();
            double length = Math.Sqrt(x * x + y * y + z * z);
            double scale = 0.1 + 0.9 * ((double)i / KernelSize) * ((double)i / KernelSize);
            kernel[i * 3] = (float)(x / length * scale);
            kernel[i * 3 + 1] = (float)(y / length * scale);
            kernel[i * 3 + 2] = (float)(z / length * scale);
        }
        return kernel;
    }

    private static unsafe int UploadFloat(VulkanDevice seam, float[] texels)
    {
        fixed (float* data = texels) return seam.CreateTexture2DRaw(Size, Size, GlRgba32f, (IntPtr)data, 16);
    }

    /// <summary>revealage = 1 everywhere, which is "nothing transparent here".</summary>
    private static unsafe int UploadOpaqueRed(VulkanDevice seam)
    {
        var texels = new byte[Size * Size * 4];
        for (int i = 0; i < Size * Size; i++) texels[i * 4] = 255;
        fixed (byte* data = texels) return seam.CreateTexture2DRaw(Size, Size, GlRgba8, (IntPtr)data, 4);
    }

    private static int Differing(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int count = 0;
        for (int texel = 0; texel < a.Length / 4; texel++)
        {
            if (a[texel * 4] != b[texel * 4]) count++;
        }
        return count;
    }
}
}
