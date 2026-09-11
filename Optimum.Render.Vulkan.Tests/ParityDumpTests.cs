using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The Vulkan half of the per-attachment parity dump (OPTIMUM_PARITY_DUMP):
/// known patterns rendered into the formats the framebuffer list actually uses -
/// RGBA8, RGBA16F, R32F and depth - are read back through
/// <see cref="IOptimumGraphicsDevice.ReadTextureForParity" />, written by the
/// shared <see cref="OptimumParityDump" /> writer, and decoded from the files.
///
/// Every value is checked against the fragment that produced it, in GL row
/// order: file row k must be gl_FragCoord.y = k + 0.5. The size is odd and
/// non-square so a transposed or flipped image cannot pass, and the float
/// patterns carry HDR and negative values a clamp-to-byte decode would destroy.
/// </summary>
public class ParityDumpTests
{
    private const int Width = 13;
    private const int Height = 7;

    private readonly ITestOutputHelper _output;

    public ParityDumpTests(ITestOutputHelper output) => _output = output;

    private const string Vertex = """
        #version 330 core
        void main() {
            gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                               -1 + ((gl_VertexID & 2) << 1), 0, 1);
        }
        """;

    private const string Fragment = """
        #version 330 core
        layout(location = 0) out vec4 color;
        layout(location = 1) out vec4 hdr;
        layout(location = 2) out vec4 linearDepth;
        void main() {
            int x = int(gl_FragCoord.x);
            int y = int(gl_FragCoord.y);
            color = vec4(float(x * 17) / 255.0, float(y * 31) / 255.0,
                         float((x + 3 * y) % 256) / 255.0, float(250 - x - 2 * y) / 255.0);
            hdr = vec4(100.0 + float(x) * 1.5, -0.25 * float(y), float(x * y), 0.5 + float(x));
            linearDepth = vec4(1000.0 + float(x) * 0.125 + float(y) * 64.0, 0.0, 0.0, 1.0);
            gl_FragDepth = (float(x + y * 13) + 0.5) / 91.0;
        }
        """;

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

    [SkippableFact]
    public void RenderedPatternsDumpAndDecodeBackInGlRowOrder()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        string directory = Path.Combine(Path.GetTempPath(), "optimum-parity-dump-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new Dictionary<string, int>();
            using (device)
            {
                IOptimumGraphicsDevice seam = device!;
                int program = VulkanDeviceIntegrationTests.LinkProgram(seam, Vertex, Fragment, "parity-dump");

                int rgba8 = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int rgba16f = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.Rgba16f,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int r32f = seam.CreateTexture2DRaw(Width, Height, 0x822E, IntPtr.Zero, 4);
                int depth = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.DepthComponent32,
                    EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);

                int framebuffer = seam.CreateFramebuffer(Width, Height);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, rgba8, 0);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment1, rgba16f, 0);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment2, r32f, 0);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
                seam.SetDrawBuffers(framebuffer, 7);
                Assert.True(seam.CheckFramebufferComplete(framebuffer, out string status), status);

                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.SetViewport(0, 0, Width, Height);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.SetDepthTest(true);
                seam.SetDepthMask(true);
                seam.SetDepthFunc(0x0207); // GL_ALWAYS
                seam.UseProgram(program);
                seam.DrawFullscreenTriangle();

                // Readback inside the frame, after the draw and before Present -
                // where the client calls it.
                var attachments = new (string Label, int Texture)[]
                {
                    ("color0", rgba8), ("color1", rgba16f), ("color2", r32f), ("depth", depth),
                };
                foreach (var (label, texture) in attachments)
                {
                    OptimumTextureReadback? readback = seam.ReadTextureForParity(texture);
                    Assert.NotNull(readback);
                    files[label] = OptimumParityDump.Write(directory, 0, "Primary", label, readback!);
                }
                seam.Present();
            }

            Assert.Equal(2, files["color0"]);
            Assert.Equal(2, files["color1"]);
            Assert.Equal(1, files["color2"]);
            Assert.Equal(1, files["depth"]);

            string[] expectedNames =
            {
                "0-Primary-color0-rgba8.ppm", "0-Primary-color0-rgba8.pgm",
                "0-Primary-color1-rgba16f.pfm", "0-Primary-color1-rgba16f.alpha.pfm",
                "0-Primary-color2-r32f.pfm", "0-Primary-depth-depth.pfm",
            };
            string[] actualNames = Directory.GetFiles(directory);
            for (int i = 0; i < actualNames.Length; i++) actualNames[i] = Path.GetFileName(actualNames[i]);
            Array.Sort(actualNames, StringComparer.Ordinal);
            string[] sortedExpected = (string[])expectedNames.Clone();
            Array.Sort(sortedExpected, StringComparer.Ordinal);
            Assert.Equal(sortedExpected, actualNames);

            // RGBA8: PPM of RGB, PGM of alpha, exact bytes.
            byte[] rgb = ReadRaster(Path.Combine(directory, "0-Primary-color0-rgba8.ppm"), "P6", out _);
            byte[] alpha = ReadRaster(Path.Combine(directory, "0-Primary-color0-rgba8.pgm"), "P5", out _);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int texel = y * Width + x;
                Assert.Equal((byte)(x * 17), rgb[texel * 3]);
                Assert.Equal((byte)(y * 31), rgb[texel * 3 + 1]);
                Assert.Equal((byte)((x + 3 * y) % 256), rgb[texel * 3 + 2]);
                Assert.Equal((byte)(250 - x - 2 * y), alpha[texel]);
            }

            // RGBA16F: PF of RGB plus Pf of alpha, float32 little-endian, HDR and negatives intact.
            float[] hdr = ReadFloats(Path.Combine(directory, "0-Primary-color1-rgba16f.pfm"), "PF");
            float[] hdrAlpha = ReadFloats(Path.Combine(directory, "0-Primary-color1-rgba16f.alpha.pfm"), "Pf");
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int texel = y * Width + x;
                Assert.Equal(100f + x * 1.5f, hdr[texel * 3]);
                Assert.Equal(-0.25f * y, hdr[texel * 3 + 1]);
                Assert.Equal((float)(x * y), hdr[texel * 3 + 2]);
                Assert.Equal(0.5f + x, hdrAlpha[texel]);
            }

            // R32F: one channel, exact.
            float[] linear = ReadFloats(Path.Combine(directory, "0-Primary-color2-r32f.pfm"), "Pf");
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                Assert.Equal(1000f + x * 0.125f + y * 64f, linear[y * Width + x]);
            }

            // Depth: one channel, the fragment's gl_FragDepth.
            float[] depthValues = ReadFloats(Path.Combine(directory, "0-Primary-depth-depth.pfm"), "Pf");
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                float expected = (x + y * 13 + 0.5f) / 91f;
                Assert.InRange(depthValues[y * Width + x], expected - 1e-6f, expected + 1e-6f);
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

    /// <summary>Header: magic, width, height, maxval-or-scale, each on its own line.</summary>
    private static byte[] ReadRaster(string path, string magic, out string scale)
    {
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
        Assert.Equal(magic, header[0]);
        Assert.Equal(Width.ToString(CultureInfo.InvariantCulture), header[1]);
        Assert.Equal(Height.ToString(CultureInfo.InvariantCulture), header[2]);
        scale = header[3];
        int channels = magic is "P6" or "PF" ? 3 : 1;
        int bytesPerValue = magic.StartsWith("P", StringComparison.Ordinal) && magic[1] is 'F' or 'f' ? 4 : 1;
        Assert.Equal(offset + Width * Height * channels * bytesPerValue, file.Length);
        return file.AsSpan(offset).ToArray();
    }

    private static float[] ReadFloats(string path, string magic)
    {
        byte[] raster = ReadRaster(path, magic, out string scale);
        // Negative scale: little-endian, the encoding the dump promises.
        Assert.Equal("-1.0", scale);
        var values = new float[raster.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(raster.AsSpan(i * 4, 4));
        }
        return values;
    }
}
