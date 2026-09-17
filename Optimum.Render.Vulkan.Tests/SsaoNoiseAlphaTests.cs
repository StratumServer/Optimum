using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

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
