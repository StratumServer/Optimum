using System;
using System.IO;
using System.Linq;

namespace Optimum.Tests;

/// <summary>
/// Reads a unified-diff patch file and extracts the effective "after" content:
/// context lines (no prefix) and added lines (+ prefix), stripped of the prefix.
/// This gives the same result as the patched source file without depending on
/// the decompiler producing the exact baseline the patch was generated against.
/// </summary>
public static class PatchReader
{
    /// <summary>
    /// Returns the effective content of a patch file: context lines and added
    /// lines concatenated. Removed lines (- prefix) and diff headers are excluded.
    /// </summary>
    public static string ReadPatchedContent(string patchFilePath)
    {
        var lines = File.ReadAllLines(patchFilePath);
        var patched = lines
            .Where(line =>
            {
                // Skip diff metadata
                if (line.StartsWith("diff --git")) return false;
                if (line.StartsWith("index ")) return false;
                if (line.StartsWith("--- ")) return false;
                if (line.StartsWith("+++ ")) return false;
                if (line.StartsWith("@@ ")) return false;
                // Skip removed lines
                if (line.StartsWith("-")) return false;
                // Include added lines (strip the + prefix)
                // Include context lines (no prefix)
                return true;
            })
            .Select(line => line.StartsWith("+") ? line.Substring(1) : line);

        return string.Join("\n", patched);
    }

    /// <summary>
    /// Finds a file relative to the repository root by walking up from the test assembly location.
    /// </summary>
    public static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// Convenience: find the patch file and return its effective patched content.
    /// </summary>
    public static string ReadPatch(string relativePatchPath)
    {
        return ReadPatchedContent(FindRepositoryFile(relativePatchPath));
    }
}
