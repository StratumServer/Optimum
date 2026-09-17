using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

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
        int program = VulkanDeviceIntegrationTests.LinkProgram(
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
