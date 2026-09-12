using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class UpscaleSsaoTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    public unsafe void OcclusionMultipliesOnlySceneColorBeforeReconstruction(int quality)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 8, frames = 8;
            var files = ShaderCorpus.LoadShaderFiles();
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, files["upscale-ssao.vsh"],
                files["upscale-ssao.fsh"].Replace("#version 330 core", "#version 330 core\n#define SSAOLEVEL " + quality));
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
                depths[i] = seam.CreateUpscaleTexture(size, size, Format.D32Sfloat, storage: false);
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
            // The sequence is complete before any readback or CPU wait.
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
}
