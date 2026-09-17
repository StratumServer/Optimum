using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

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
            int program = VulkanDeviceIntegrationTests.LinkProgram(
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
    /// The OPTIMUMAO variant (docs/research/ambient-occlusion.md C.9, C.11): the GTAO visibility is
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
            int program = VulkanDeviceIntegrationTests.LinkProgram(
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
