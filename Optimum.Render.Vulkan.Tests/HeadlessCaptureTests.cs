using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The headless render harness's capture path on a device that has no surface at
/// all - no window, no swapchain, <c>Initialize(IntPtr.Zero, ...)</c>.
///
/// This is the claim the harness rests on: the frames it writes come from
/// <see cref="VulkanDevice.ReadDefaultFramebuffer" />, the same polymorphic call
/// the in-game screenshot makes, which is a device-side copy of whatever target
/// is bound - never an OS window capture. So a window that was never mapped (and,
/// here, a device with no surface whatsoever) still produces frames.
///
/// Several consecutive frames are rendered with a Present between them and a
/// per-frame value baked into the pattern, so a capture that silently reused one
/// frame, or wrote the same file every time, fails. The files are decoded back
/// and checked byte for byte in GL row order; the size is odd and non-square so a
/// transposed or flipped image cannot pass, and one channel pair is asymmetric so
/// the BGRA/RGBA distinction the writer takes as an argument is real.
/// </summary>
public class HeadlessCaptureTests
{
    private const int Width = 11;
    private const int Height = 5;
    private const int Frames = 4;

    private readonly ITestOutputHelper _output;

    public HeadlessCaptureTests(ITestOutputHelper output) => _output = output;

    private const string Vertex = """
        #version 330 core
        void main() {
            gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                               -1 + ((gl_VertexID & 2) << 1), 0, 1);
        }
        """;

    // Red carries x, green carries the frame index, blue carries y: three
    // distinguishable axes, so a transpose, a flip, a stale frame and a channel
    // swap each break a different assertion.
    private const string Fragment = """
        #version 330 core
        uniform float frameIndex;
        layout(location = 0) out vec4 color;
        void main() {
            int x = int(gl_FragCoord.x);
            int y = int(gl_FragCoord.y);
            color = vec4(float(x * 23) / 255.0, (frameIndex * 40.0) / 255.0,
                         float(y * 37) / 255.0, 1.0);
        }
        """;

    [SkippableFact]
    public void OffscreenModeProducesFramesWithoutASurface()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        string directory = Path.Combine(Path.GetTempPath(), "optimum-headless-tests-" + Guid.NewGuid().ToString("N"));
        long[] plan = OptimumHeadless.PlanFrames(0, Frames, 1);
        try
        {
            using (device)
            {
                VulkanDevice seam = device!;
                int program = VulkanDeviceIntegrationTests.LinkProgram(seam, Vertex, Fragment, "headless-capture");

                int color = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int framebuffer = seam.CreateFramebuffer(Width, Height);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, color, 0);
                seam.SetDrawBuffers(framebuffer, 1);
                Assert.True(seam.CheckFramebufferComplete(framebuffer, out string status), status);

                byte[] pixels = new byte[Width * Height * 4];
                for (long frame = 0; frame < Frames; frame++)
                {
                    Assert.True(OptimumHeadless.ShouldCapture(plan, frame));

                    seam.BeginFrame();
                    seam.BindFramebuffer(framebuffer);
                    seam.SetViewport(0, 0, Width, Height);
                    seam.SetCullFace(false);
                    seam.SetBlend(false, EnumBlendMode.Standard);
                    seam.SetDepthTest(false);
                    seam.UseProgram(program);
                    seam.SetUniform(program, seam.GetUniformLocation(program, "frameIndex"), (float)frame);
                    seam.DrawFullscreenTriangle();

                    // Exactly what ClientPlatformWindows.OptimumHeadlessCaptureFrame
                    // does: read the bound target back inside the frame, then write.
                    GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        seam.ReadDefaultFramebuffer(0, 0, Width, Height, handle.AddrOfPinnedObject());
                    }
                    finally
                    {
                        handle.Free();
                    }

                    // bgra: false - the device's colour targets are R8G8B8A8 and the
                    // readback copies texels untouched, which is exactly what
                    // VulkanClientPlatform.OptimumDefaultFramebufferIsBgra reports.
                    Assert.True(OptimumParityDump.WriteFrame(
                        Path.Combine(directory, OptimumHeadless.FrameFileName(frame)),
                        Width, Height, pixels, bgra: false));

                    // No readback in the presentation path: the frame is closed the
                    // way the client closes it, so the next one is genuinely new.
                    seam.Present();
                    Assert.Equal(frame >= Frames - 1, OptimumHeadless.CaptureFinished(plan, frame));
                }

                GpuTest.AssertClean(seam);
            }

            for (long frame = 0; frame < Frames; frame++)
            {
                string path = Path.Combine(directory, OptimumHeadless.FrameFileName(frame));
                Assert.True(File.Exists(path), path + " was not written");
                byte[] rgb = ReadPpm(path);
                for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int texel = y * Width + x;
                    Assert.Equal((byte)(x * 23), rgb[texel * 3]);
                    Assert.Equal((byte)(frame * 40), rgb[texel * 3 + 1]);
                    Assert.Equal((byte)(y * 37), rgb[texel * 3 + 2]);
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// The harness with an upscaler running - where the headless stream and the
    /// upscaler slot meet.
    ///
    /// The frame an upscaler produces is built at the render size and only
    /// reaches the display size in <c>BlitPrimaryToDefault</c>, which blits the
    /// composite into <c>EnumFrameBuffer.Default</c> at the window's client size.
    /// The harness captures after that blit and sizes its buffer from the window,
    /// so the file it writes must be display-resolution and must carry the
    /// magnified image - not the render-resolution one, and not a display-sized
    /// buffer with a render-sized image in its corner.
    ///
    /// That is what this reproduces on the device: a render-resolution pattern,
    /// the magnifying blit the passthrough slot does, then the harness's own
    /// capture-and-write of the bound target at the display size. If someone ever
    /// moves the capture before the blit, or sizes it from the plan instead of the
    /// window, the block pattern stops landing on block boundaries and this fails.
    /// </summary>
    [SkippableTheory]
    [InlineData("passthrough", "performance")]
    [InlineData("passthrough", "quality")]
    public void TheCaptureIsDisplayResolutionWhileAnUpscalerIsRunning(string upscaler, string quality)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        string directory = Path.Combine(Path.GetTempPath(), "optimum-headless-tests-" + Guid.NewGuid().ToString("N"));
        string previousUpscaler = OptimumConfig.Upscaler;
        string previousQuality = OptimumConfig.UpscalerQuality;
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerQuality = quality;

            using (device)
            {
                VulkanDevice seam = device!;

                // The sizes the frame would really be built at, from the setting.
                Assert.True(PassthroughUpscaler.TryPlanForFrame(
                    DisplayWidth, DisplayHeight, out int renderWidth, out int renderHeight, out UpscalePlan plan));
                Assert.True(renderWidth < DisplayWidth && renderHeight < DisplayHeight);
                _output.WriteLine("plan: " + plan);

                int source = 0, composite = 0, framebuffer = 0;
                byte[] pixels = new byte[DisplayWidth * DisplayHeight * 4];
                try
                {
                    source = CreateBlockPattern(seam, renderWidth, renderHeight);
                    composite = seam.CreateTexture2D(DisplayWidth, DisplayHeight,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                    framebuffer = seam.CreateFramebuffer(DisplayWidth, DisplayHeight);
                    seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, composite, 0);
                    seam.SetDrawBuffers(framebuffer, 1);
                    Assert.True(seam.CheckFramebufferComplete(framebuffer, out string status), status);

                    seam.BeginFrame();

                    // The upscaler's own step: render size in, display size out.
                    Assert.True(seam.BlitColorScaled(source, composite, linear: false),
                        "the driver refused the magnifying blit");

                    // And then exactly what OptimumHeadlessCaptureFrame does: the
                    // bound target, sized from the window, read back inside the
                    // frame and handed to the writer.
                    seam.BindFramebuffer(framebuffer);
                    GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        seam.ReadDefaultFramebuffer(0, 0, DisplayWidth, DisplayHeight, handle.AddrOfPinnedObject());
                    }
                    finally
                    {
                        handle.Free();
                    }

                    Assert.True(OptimumParityDump.WriteFrame(
                        Path.Combine(directory, OptimumHeadless.FrameFileName(0)),
                        DisplayWidth, DisplayHeight, pixels, bgra: false));

                    seam.Present();
                    GpuTest.AssertClean(seam);
                }
                finally
                {
                    if (framebuffer > 0) seam.DeleteFramebuffer(framebuffer);
                    if (composite > 0) seam.DeleteTexture(composite);
                    if (source > 0) seam.DeleteTexture(source);
                }
            }

            // The file is the display size, and it is the render image magnified:
            // every block interior is still saturated one way or the other, which a
            // capture of the render-size image (or of the wrong extent) cannot do.
            string path = Path.Combine(directory, OptimumHeadless.FrameFileName(0));
            byte[] rgb = ReadPpm(path, DisplayWidth, DisplayHeight);
            Assert.Equal(DisplayWidth * DisplayHeight * 3, rgb.Length);

            int checkedTexels = 0;
            for (int by = 0; by < Blocks; by++)
            for (int bx = 0; bx < Blocks; bx++)
            {
                // The interior of each block, away from any filtered edge.
                int x = (bx * DisplayWidth + DisplayWidth / 2) / Blocks;
                int y = (by * DisplayHeight + DisplayHeight / 2) / Blocks;
                int texel = y * DisplayWidth + x;
                byte value = rgb[texel * 3];
                if ((bx + by) % 2 == 0) Assert.True(value > 230, "bright block " + bx + "," + by + " = " + value);
                else Assert.True(value < 25, "dark block " + bx + "," + by + " = " + value);
                checkedTexels++;
            }
            Assert.Equal(Blocks * Blocks, checkedTexels);
        }
        finally
        {
            OptimumConfig.Upscaler = previousUpscaler;
            OptimumConfig.UpscalerQuality = previousQuality;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.ClearUpscalerPlan();
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    /// <summary>A display size that is odd and non-square, like the capture's.</summary>
    private const int DisplayWidth = 320;
    private const int DisplayHeight = 176;

    /// <summary>The alternating block pattern, at whatever size it is asked for.</summary>
    private const int Blocks = 4;

    private static unsafe int CreateBlockPattern(VulkanDevice seam, int width, int height)
    {
        byte[] texels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool bright = (x * Blocks / width + y * Blocks / height) % 2 == 0;
            byte value = bright ? (byte)255 : (byte)0;
            int texel = (y * width + x) * 4;
            texels[texel] = value;
            texels[texel + 1] = value;
            texels[texel + 2] = value;
            texels[texel + 3] = 255;
        }

        fixed (byte* data = texels)
        {
            return seam.CreateTexture2D(width, height, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
        }
    }

    /// <summary>
    /// The BGRA half of the same writer, on bytes rather than a GPU: the OpenGL
    /// path reads GL_BGRA and the Vulkan one RGBA, and the file must come out the
    /// same either way, or every cross-backend comparison is a red/blue swap.
    /// </summary>
    [Fact]
    public void BgraAndRgbaPixelsWriteTheSameFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "optimum-headless-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] rgba = new byte[Width * Height * 4];
            byte[] bgra = new byte[Width * Height * 4];
            for (int texel = 0; texel < Width * Height; texel++)
            {
                byte r = (byte)(texel * 7);
                byte g = (byte)(texel * 13 + 1);
                byte b = (byte)(texel * 29 + 2);
                rgba[texel * 4] = r; rgba[texel * 4 + 1] = g; rgba[texel * 4 + 2] = b; rgba[texel * 4 + 3] = 255;
                bgra[texel * 4] = b; bgra[texel * 4 + 1] = g; bgra[texel * 4 + 2] = r; bgra[texel * 4 + 3] = 255;
            }

            string fromRgba = Path.Combine(directory, "rgba.ppm");
            string fromBgra = Path.Combine(directory, "bgra.ppm");
            Assert.True(OptimumParityDump.WriteFrame(fromRgba, Width, Height, rgba, bgra: false));
            Assert.True(OptimumParityDump.WriteFrame(fromBgra, Width, Height, bgra, bgra: true));
            Assert.Equal(File.ReadAllBytes(fromRgba), File.ReadAllBytes(fromBgra));

            byte[] written = ReadPpm(fromBgra);
            for (int texel = 0; texel < Width * Height; texel++)
            {
                Assert.Equal((byte)(texel * 7), written[texel * 3]);
                Assert.Equal((byte)(texel * 13 + 1), written[texel * 3 + 1]);
                Assert.Equal((byte)(texel * 29 + 2), written[texel * 3 + 2]);
            }

            // A buffer that does not describe the frame is refused, not written.
            Assert.False(OptimumParityDump.WriteFrame(Path.Combine(directory, "short.ppm"),
                Width, Height, new byte[4], bgra: false));
            Assert.False(File.Exists(Path.Combine(directory, "short.ppm")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    private static byte[] ReadPpm(string path) => ReadPpm(path, Width, Height);

    /// <summary>
    /// Decodes a P6 PPM and holds it to the size it is supposed to be: the header
    /// is where a capture that used the wrong extent - the render size instead of
    /// the display size, say - shows up first.
    /// </summary>
    private static byte[] ReadPpm(string path, int width, int height)
    {
        Assert.True(File.Exists(path), path + " was not written");
        byte[] file = File.ReadAllBytes(path);
        int offset = 0;
        string[] header = new string[4];
        for (int i = 0; i < 4; i++)
        {
            int start = offset;
            while (file[offset] != (byte)' ' && file[offset] != (byte)'\n') offset++;
            header[i] = Encoding.ASCII.GetString(file, start, offset - start);
            offset++;
        }
        Assert.Equal("P6", header[0]);
        Assert.Equal(width.ToString(CultureInfo.InvariantCulture), header[1]);
        Assert.Equal(height.ToString(CultureInfo.InvariantCulture), header[2]);
        Assert.Equal("255", header[3]);
        Assert.Equal(offset + width * height * 3, file.Length);
        return file.AsSpan(offset).ToArray();
    }
}
