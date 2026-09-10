using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Covers the format-aware conversion in <see cref="TextureDump.Write" /> - in
/// particular that a 16-bit float attachment (the shape a TAA motion vector
/// target takes) is accepted and converted rather than read as 8-bit RGBA and
/// either overrun or garbled.
/// </summary>
public class TextureDumpTests
{
    [Fact]
    public void WritesRgba16FloatTextureAsPpm()
    {
        const int width = 4;
        const int height = 3;

        string directory = Path.Combine(Path.GetTempPath(), "optimum-texture-dump-tests-" + Guid.NewGuid());
        string? previousDir = Environment.GetEnvironmentVariable("OPTIMUM_DUMP_DIR");
        string? previousTrace = Environment.GetEnvironmentVariable("OPTIMUM_RENDER_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", directory);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", null);

            var texels = new Half[width * height * 4];
            for (int i = 0; i < texels.Length; i++)
            {
                // Cycle through channel values so every component participates.
                texels[i] = (Half)((i % 4) switch
                {
                    0 => 1f,
                    1 => 0.5f,
                    2 => 0f,
                    _ => 1f, // alpha, ignored by the PPM
                });
            }
            byte[] data = MemoryMarshal.AsBytes<Half>(texels).ToArray();

            bool written = TextureDump.Write(
                textureId: 1234,
                width: width,
                height: height,
                bgra: false,
                format: Format.R16G16B16A16Sfloat,
                data: data);

            Assert.True(written);

            string path = Directory.GetFiles(directory, "*-texture-1234-*.ppm").SingleOrDefault()
                ?? throw new Xunit.Sdk.XunitException("No dump file was written.");

            byte[] file = File.ReadAllBytes(path);
            string header = $"P6\n{width} {height}\n255\n";
            string actualHeader = System.Text.Encoding.ASCII.GetString(file, 0, header.Length);
            Assert.Equal(header, actualHeader);

            int expectedPixelBytes = width * height * 3;
            Assert.Equal(header.Length + expectedPixelBytes, file.Length);

            // First texel is (1, 0.5, 0) -> full red, half green, zero blue.
            int pixelStart = header.Length;
            Assert.Equal(255, file[pixelStart]);
            Assert.InRange(file[pixelStart + 1], 126, 128);
            Assert.Equal(0, file[pixelStart + 2]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", previousDir);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", previousTrace);
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// R16f (the OIT revealage attachment's format) is two bytes per texel. It
    /// used to fall through to the 4-byte default, so the readback was sized
    /// twice as large as the image and every row was decoded from the wrong
    /// offset. Known values in, known greys out.
    /// </summary>
    [Fact]
    public void WritesR16FloatTextureAsPpm()
    {
        const int width = 4;
        const int height = 2;

        Assert.Equal(2, TextureDump.BytesPerTexel(Format.R16Sfloat));

        string directory = Path.Combine(Path.GetTempPath(), "optimum-texture-dump-tests-" + Guid.NewGuid());
        string? previousDir = Environment.GetEnvironmentVariable("OPTIMUM_DUMP_DIR");
        string? previousTrace = Environment.GetEnvironmentVariable("OPTIMUM_RENDER_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", directory);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", null);

            // Motion-like mapping: value / 64 * 127 + 128, clamped to [0,255].
            float[] values = { 0f, 16f, -32f, 64f, -64f, 8f, -8f, 32f };
            byte[] expected = new byte[values.Length];
            var texels = new Half[width * height];
            for (int i = 0; i < texels.Length; i++)
            {
                texels[i] = (Half)values[i];
                expected[i] = (byte)Math.Clamp(values[i] / 64f * 127f + 128f, 0f, 255f);
            }
            byte[] data = MemoryMarshal.AsBytes<Half>(texels).ToArray();
            Assert.Equal(width * height * 2, data.Length);

            bool written = TextureDump.Write(
                textureId: 4321,
                width: width,
                height: height,
                bgra: false,
                format: Format.R16Sfloat,
                data: data);

            Assert.True(written);

            string path = Directory.GetFiles(directory, "*-texture-4321-*.ppm").SingleOrDefault()
                ?? throw new Xunit.Sdk.XunitException("No dump file was written.");

            byte[] file = File.ReadAllBytes(path);
            string header = $"P6\n{width} {height}\n255\n";
            Assert.Equal(header, System.Text.Encoding.ASCII.GetString(file, 0, header.Length));
            Assert.Equal(header.Length + width * height * 3, file.Length);

            for (int i = 0; i < texels.Length; i++)
            {
                int pixel = header.Length + i * 3;
                // Greyscale: all three channels carry the same converted value.
                Assert.Equal(expected[i], file[pixel]);
                Assert.Equal(expected[i], file[pixel + 1]);
                Assert.Equal(expected[i], file[pixel + 2]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", previousDir);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", previousTrace);
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }
}
