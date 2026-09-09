using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Writes a texture's contents, as they actually sit on the GPU, to a file.
///
/// A wrong image and wrong texture coordinates look identical from the far end
/// of the pipeline, and the two can be argued about indefinitely. Reading the
/// image back settles it: whatever comes out is what every sample of that
/// texture saw, with no inference in between.
///
/// Off unless OPTIMUM_DUMP_TEXTURES lists texture ids, comma separated. The
/// files land beside the render trace, or in OPTIMUM_DUMP_DIR when that is set,
/// as binary PPM - a five-line header and raw RGB, which needs no encoder here
/// and which every image tool reads.
/// </summary>
internal static class TextureDump
{
    private static readonly string? Requested =
        Environment.GetEnvironmentVariable("OPTIMUM_DUMP_TEXTURES");

    /// <summary>Texture ids still waiting to be written.</summary>
    private static readonly HashSet<int> Pending = Parse(Requested);

    /// <summary>
    /// Set by OPTIMUM_DUMP_TEXTURES=terrain, which asks for whatever the chunk
    /// pass binds rather than for an id.
    ///
    /// Atlas ids are only handed out once a world loads, and are not stable
    /// between runs, so naming one up front means guessing. Latching onto the
    /// first storage-buffer multi-draw instead catches the block atlas at the
    /// one moment it is certainly the texture the terrain is being drawn with.
    /// </summary>
    private static bool _wantsTerrain =
        string.Equals(Requested?.Trim(), "terrain", StringComparison.OrdinalIgnoreCase);

    public static bool WantsTerrain => _wantsTerrain;

    /// <summary>Records the textures a chunk draw is using, and stops asking.</summary>
    public static void RequestTerrain(int baseTexture, int linearTexture)
    {
        _wantsTerrain = false;
        if (baseTexture > 0) Pending.Add(baseTexture);
        if (linearTexture > 0 && linearTexture != baseTexture) Pending.Add(linearTexture);
    }

    /// <summary>True while any requested texture has not been written yet.</summary>
    public static bool Wanted => Pending.Count > 0;

    private static HashSet<int> Parse(string? value)
    {
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(value)) return ids;

        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>The ids still to write, as a snapshot safe to iterate while removing.</summary>
    public static int[] Take()
    {
        var ids = new int[Pending.Count];
        Pending.CopyTo(ids);
        Pending.Clear();
        return ids;
    }

    private static string Directory()
    {
        string? explicitDir = Environment.GetEnvironmentVariable("OPTIMUM_DUMP_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir)) return explicitDir;

        string? tracePath = Environment.GetEnvironmentVariable("OPTIMUM_RENDER_TRACE");
        string? beside = string.IsNullOrWhiteSpace(tracePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(tracePath));

        return string.IsNullOrWhiteSpace(beside) ? "." : beside;
    }

    /// <summary>
    /// Writes RGBA or BGRA bytes as a binary PPM.
    /// </summary>
    /// <returns>The file written, or null if it could not be.</returns>
    public static string? Write(int textureId, int width, int height, bool bgra, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return null;

        try
        {
            string directory = Directory();
            System.IO.Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"texture-{textureId}-{width}x{height}.ppm");

            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(file);

            foreach (char c in $"P6\n{width} {height}\n255\n") writer.Write((byte)c);

            int red = bgra ? 2 : 0;
            int blue = bgra ? 0 : 2;

            var row = new byte[width * 3];
            for (int y = 0; y < height; y++)
            {
                int source = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    row[x * 3] = rgba[source + x * 4 + red];
                    row[x * 3 + 1] = rgba[source + x * 4 + 1];
                    row[x * 3 + 2] = rgba[source + x * 4 + blue];
                }
                writer.Write(row);
            }

            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
