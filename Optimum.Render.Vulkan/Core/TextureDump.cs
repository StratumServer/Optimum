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
/// files land in OPTIMUM_DUMP_DIR when that names an absolute path, else beside
/// the render trace, else under the temp directory - never the working
/// directory - as binary PPM - a five-line header and raw RGB, which needs no encoder here
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

    /// <summary>
    /// The ids still to write, as a snapshot safe to iterate while removing.
    /// Ids stay pending until <see cref="Complete" /> reports a successful write,
    /// so a texture that does not exist yet or whose write fails is retried on a
    /// later frame instead of being silently dropped.
    /// </summary>
    public static int[] Take()
    {
        var ids = new int[Pending.Count];
        Pending.CopyTo(ids);
        return ids;
    }

    /// <summary>Removes an id from the pending set once it has been written successfully.</summary>
    public static void Complete(int textureId) => Pending.Remove(textureId);

    /// <summary>
    /// Where the files go. An explicit OPTIMUM_DUMP_DIR must be absolute so
    /// the launching environment names the location outright rather than
    /// relative to whatever the working directory happens to be; otherwise
    /// the files sit beside the render trace, and failing both, under a
    /// dedicated folder in the temp directory. Never the working directory.
    /// </summary>
    private static string? Directory()
    {
        string? explicitDir = Environment.GetEnvironmentVariable("OPTIMUM_DUMP_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return Path.IsPathRooted(explicitDir) ? Path.GetFullPath(explicitDir) : null;
        }

        string? tracePath = Environment.GetEnvironmentVariable("OPTIMUM_RENDER_TRACE");
        if (!string.IsNullOrWhiteSpace(tracePath) && Path.IsPathRooted(tracePath))
        {
            string? beside = Path.GetDirectoryName(Path.GetFullPath(tracePath));
            if (!string.IsNullOrWhiteSpace(beside)) return beside;
        }

        return Path.Combine(Path.GetTempPath(), "optimum-texture-dumps");
    }

    /// <summary>
    /// One prefix per process, so two runs into the same directory never
    /// overwrite each other's files and a run never overwrites its own.
    /// </summary>
    private static readonly string RunPrefix =
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Environment.ProcessId;

    /// <summary>
    /// Writes RGBA or BGRA bytes as a binary PPM.
    /// </summary>
    /// <returns>True if the file was written.</returns>
    public static bool Write(int textureId, int width, int height, bool bgra, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return false;

        try
        {
            string? directory = Directory();
            if (directory == null) return false;
            System.IO.Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{RunPrefix}-texture-{textureId}-{width}x{height}.ppm");

            // CreateNew: an existing file is never truncated, whatever named it.
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
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

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
