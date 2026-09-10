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
}
