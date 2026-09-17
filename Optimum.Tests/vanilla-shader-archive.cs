using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Optimum.Tests;

/// <summary>
/// Reads a vanilla shader or shader include out of the client release archive
/// under <c>.vanilla/archives/</c>.
///
/// Deliberately NOT out of <c>.vanilla/win-x64/vintagestory/assets/</c>:
/// <c>make deploy</c> copies every file in <c>sources/shaders</c> and
/// <c>sources/shaderincludes</c> straight into that tree, so from the first
/// deploy onwards a test that compared an override against it would be
/// comparing the file with itself - and silently pass whatever it was meant to
/// catch. That happened to the vertexwarp comparison in
/// <see cref="TaaTerrainMotionCoverageTests" /> (it failed loudly, which was
/// luck: the include had grown the WarpState overloads the test asserts are
/// absent from vanilla).
///
/// Returns null when the checkout has not been bootstrapped - the vanilla
/// assets are proprietary and never committed - so callers skip rather than
/// fail.
/// </summary>
public static class VanillaShaderArchive
{
    /// <summary>A file under the archive's <c>assets/game/</c>, e.g.
    /// "shaders/particlescube.vsh" or "shaderincludes/vertexwarp.vsh".</summary>
    public static string? TryRead(string assetRelativePath)
    {
        string? archive = TryFindArchive();
        if (archive == null) return null;

        string suffix = "assets/game/" + assetRelativePath;

        using FileStream file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry() is { } entry)
        {
            if (!entry.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (entry.DataStream == null) continue;

            using var text = new StreamReader(entry.DataStream, Encoding.UTF8);
            return text.ReadToEnd();
        }

        return null;
    }

    private static string? TryFindArchive()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, ".vanilla", "archives");
            if (Directory.Exists(candidate))
            {
                string[] archives = Directory.GetFiles(candidate, "vs_client_*.tar.gz");
                if (archives.Length > 0)
                {
                    Array.Sort(archives, StringComparer.Ordinal);
                    return archives[^1];
                }
            }
            directory = directory.Parent;
        }
        return null;
    }
}
