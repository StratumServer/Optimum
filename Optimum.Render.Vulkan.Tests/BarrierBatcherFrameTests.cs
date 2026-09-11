using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// A representative frame through the device: a primary target with two colour
/// attachments and depth, a composition pass that writes attachment 0 while
/// sampling attachment 1, and an output pass that samples the scene colour and
/// the depth. Measures the image barriers and barrier commands per frame in
/// steady state.
/// </summary>
public class BarrierBatcherFrameTests
{
    private readonly ITestOutputHelper _output;

    public BarrierBatcherFrameTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.5, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    internal readonly record struct FrameCounts(long ImageBarriers, long BarrierCommands, byte[] Pixels);

    internal static unsafe FrameCounts RenderRepresentativeFrames(VulkanDevice seam, int warmup, int measured)
    {
        const int size = 16;

        int sceneProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            layout(location = 1) out vec4 outGlow;
            void main(void)
            {
                outColor = vec4(40.0 / 255.0, 90.0 / 255.0, 160.0 / 255.0, 1.0);
                outGlow = vec4(20.0 / 255.0, 0.0, 0.0, 1.0);
            }
            """, "scene");
        int composeProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D glow;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = vec4(texture(glow, uv).r + 40.0 / 255.0, 90.0 / 255.0, 160.0 / 255.0, 1.0); }
            """, "compose");
        int outputProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D scene;
            uniform sampler2D depthTex;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = vec4(texture(scene, uv).rgb, texture(depthTex, uv).r); }
            """, "output");

        int Texture(EnumTextureInternalFormat format) => seam.CreateTexture2D(
            size, size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

        int scene = Texture(EnumTextureInternalFormat.Rgba8);
        int glow = Texture(EnumTextureInternalFormat.Rgba8);
        int depth = Texture(EnumTextureInternalFormat.DepthComponent32);
        int final = Texture(EnumTextureInternalFormat.Rgba8);

        int primary = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(primary, EnumFramebufferAttachment.ColorAttachment0, scene, 0);
        seam.AttachTexture(primary, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(primary, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        seam.SetDrawBuffers(primary, 0b11);

        int output = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(output, EnumFramebufferAttachment.ColorAttachment0, final, 0);
        seam.SetDrawBuffers(output, 0b1);

        long barriersBefore = 0;
        long commandsBefore = 0;
        for (int frame = 0; frame < warmup + measured; frame++)
        {
            if (frame == warmup)
            {
                barriersBefore = VulkanStats.ImageBarriers;
                commandsBefore = BarrierCommands();
            }

            seam.BeginFrame();

            seam.BindFramebuffer(primary);
            seam.SetDrawBuffers(primary, 0b11);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(true);
            seam.SetDepthMask(true);
            seam.SetDepthFunc(0x203);
            seam.ClearDepth(1f);
            seam.ClearColor(0, 0, 0, 0, 0);
            seam.ClearColor(1, 0, 0, 0, 0);
            seam.UseProgram(sceneProgram);
            seam.DrawFullscreenTriangle();

            // Composition: write attachment 0, sample attachment 1.
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetDrawBuffers(primary, 0b1);
            seam.UseProgram(composeProgram);
            seam.SetSamplerUnit(composeProgram, "glow", 0);
            seam.BindTexture(0, glow);
            seam.DrawFullscreenTriangle();
            seam.SetDrawBuffers(primary, 0b11);

            // Output: sample the scene colour and the depth.
            seam.BindFramebuffer(output);
            seam.SetViewport(0, 0, size, size);
            seam.UseProgram(outputProgram);
            seam.SetSamplerUnit(outputProgram, "scene", 0);
            seam.SetSamplerUnit(outputProgram, "depthTex", 1);
            seam.BindTexture(0, scene);
            seam.BindTexture(1, depth);
            seam.DrawFullscreenTriangle();
            seam.BindTexture(0, 0);
            seam.BindTexture(1, 0);

            seam.Present();
        }

        long barriers = VulkanStats.ImageBarriers - barriersBefore;
        long commands = BarrierCommands() - commandsBefore;

        var pixels = new byte[size * size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(output);
            seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        return new FrameCounts(barriers, commands, pixels);
    }

    private static long BarrierCommands() => VulkanStats.BarrierCommands;

    /// <summary>
    /// Recorded at f187375 (feat/vulkan-native before Phase 2 step 1) with this
    /// exact frame: 18 image barriers over 3 steady-state frames, each its own
    /// vkCmdPipelineBarrier2 with ALL_COMMANDS stages (6 commands per frame).
    /// Centre pixel 60,90,160,191.
    /// </summary>
    private const long RecordedImageBarriers = 18;
    private const long RecordedBarrierCommands = 18;

    [SkippableFact]
    public void ARepresentativeFrameNeedsFewerBarrierCommandsAndStaysSyncClean()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            const int measured = 3;
            FrameCounts counts = RenderRepresentativeFrames(device!, warmup: 2, measured);
            _output.WriteLine("image barriers over " + measured + " frames: " + counts.ImageBarriers +
                              " (recorded " + RecordedImageBarriers + "), barrier commands: " +
                              counts.BarrierCommands + " (recorded " + RecordedBarrierCommands + ")");

            // The pixels are what they were before the barriers changed: glow
            // (20) added to the scene red (40) by the composition pass, the
            // scene's green and blue, and the depth (0.5 remapped to 0.75).
            int centre = (16 / 2 * 16 + 16 / 2) * 4;
            Assert.Equal(60, counts.Pixels[centre]);
            Assert.Equal(90, counts.Pixels[centre + 1]);
            Assert.Equal(160, counts.Pixels[centre + 2]);
            Assert.InRange(counts.Pixels[centre + 3], (byte)189, (byte)193);

            Assert.True(counts.ImageBarriers <= RecordedImageBarriers,
                "image barriers per frame grew: " + counts.ImageBarriers);
            Assert.True(counts.BarrierCommands < RecordedBarrierCommands,
                "barrier commands per frame did not drop: " + counts.BarrierCommands);
            Assert.True(counts.BarrierCommands <= counts.ImageBarriers);
            AssertClean(device!);
        }
    }
}
