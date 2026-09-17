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

                    // bgra: false - this reads at the device level, where texels come
                    // back in the target's own R8G8B8A8 order. The client goes through
                    // VulkanClientPlatform.ReadDefaultFramebuffer, which converts that
                    // to the GL path's B G R A (PixelOrder.SwapRedAndBlue) and so
                    // passes bgra: true; both spellings write the same file, which is
                    // what BgraAndRgbaPixelsWriteTheSameFile holds.
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
    /// The conversion <c>VulkanClientPlatform.ReadDefaultFramebuffer</c> applies on
    /// top of the device readback.
    ///
    /// The OpenGL body of that virtual is
    /// <c>glReadPixels(..., GL_BGRA, GL_UNSIGNED_BYTE, ...)</c>, and its callers
    /// depend on it: <c>Screenshot.GrabScreenshot</c> - the screenshot key and the
    /// AVI recorder - decodes into an <c>SKBitmap</c> declared
    /// <c>SKColorType.Bgra8888</c>, and the harness writes its PPMs from the same
    /// call. The device's default colour target is R8G8B8A8, so without the swap
    /// every Vulkan screenshot came out with red and blue exchanged. A greyscale
    /// pattern cannot see that, so this one is saturated red and blue, with green
    /// and alpha left where they are to catch a rotation rather than a swap.
    /// </summary>
    [Fact]
    public unsafe void TheClientSeamTurnsTheDevicesRgbaIntoTheGlPathsBgra()
    {
        byte[] texels = [255, 17, 0, 255, 0, 34, 255, 200];
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 2);
        }

        // Red in, B G R A out - and back again, because the conversion is its own
        // inverse, which is what lets one writer serve both backends.
        Assert.Equal([0, 17, 255, 255, 255, 34, 0, 200], texels);
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 2);
        }
        Assert.Equal([255, 17, 0, 255, 0, 34, 255, 200], texels);

        // Nothing to convert is not a crash.
        PixelOrder.SwapRedAndBlue(IntPtr.Zero, 4);
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 0);
            PixelOrder.SwapRedAndBlue((IntPtr)data, -1);
        }
        Assert.Equal([255, 17, 0, 255, 0, 34, 255, 200], texels);
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
