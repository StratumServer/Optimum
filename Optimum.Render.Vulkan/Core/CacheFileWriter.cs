using System;
using System.IO;
using System.Threading;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Replaces a cache file without a reader ever seeing half of it.
///
/// The bytes go to a temporary file unique to this process and call, which is then
/// moved over the destination. Two game instances saving at once lose one of the
/// two writes, never corrupt the file. On Windows a virus scanner briefly holds
/// newly written files open, which makes the move fail; the move is retried with
/// a short backoff before the write is given up (docs/research/vulkan-caching.md §7).
/// </summary>
internal static class CacheFileWriter
{
    internal const int MoveAttempts = 5;

    /// <summary>Writes <paramref name="bytes" /> to <paramref name="path" />; false when it could not.</summary>
    public static bool WriteAtomically(string path, ReadOnlySpan<byte> bytes) =>
        WriteAtomically(path, bytes, static (from, to) => File.Move(from, to, overwrite: true), Thread.Sleep);

    /// <summary>
    /// The same, with the replace step and the backoff sleep supplied: tests stand in for a
    /// scanner holding the new file open. <paramref name="replace" /> moves its first argument
    /// over its second; <paramref name="sleep" /> takes milliseconds.
    /// </summary>
    internal static bool WriteAtomically(string path, ReadOnlySpan<byte> bytes, Action<string, string> replace,
        Action<int> sleep)
    {
        string temporary = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    replace(temporary, path);
                    return true;
                }
                catch (Exception error) when (IsTransient(error) && attempt < MoveAttempts)
                {
                    sleep(10 << attempt);
                }
            }
        }
        catch (Exception error) when (IsTransient(error))
        {
            TryDelete(temporary);
            return false;
        }
    }

    /// <summary>The whole file, or null when it is missing or unreadable.</summary>
    public static byte[]? TryReadAll(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception error) when (IsTransient(error))
        {
            return null;
        }
    }

    private static bool IsTransient(Exception error) => error is IOException or UnauthorizedAccessException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (IsTransient(error))
        {
        }
    }
}
