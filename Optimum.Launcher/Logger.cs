using System;
using System.IO;

namespace Optimum.Launcher;

/// <summary>
/// Best-effort file logging for the launcher. Optimum.exe is a WinExe (no
/// console window, matching the vanilla Vintagestory.exe), so Console output
/// alone is invisible on Windows once double-clicked - this is the only
/// durable trace of what the launcher did on a given run, which is what past
/// patch-mismatch incidents were diagnosed from. Every write is wrapped: a
/// failure to log (locked file, read-only data path, etc.) must never stop
/// the game from launching.
/// </summary>
internal static class Logger
{
    private static readonly object Sync = new();
    private static string? _logPath;

    public static bool IsInitialized => _logPath is not null;

    /// <summary>
    /// Starts a fresh log file at {dataPath}/Logs/optimum-launcher.log for this
    /// run. Safe to call with an unwritable dataPath: logging degrades to
    /// Console-only (itself a no-op when no console is attached) rather than
    /// throwing.
    /// </summary>
    public static void Init(string dataPath)
    {
        try
        {
            var logsDir = Path.Combine(dataPath, "Logs");
            Directory.CreateDirectory(logsDir);
            _logPath = Path.Combine(logsDir, "optimum-launcher.log");
            File.WriteAllText(_logPath, $"{Timestamp()} Optimum launcher log started{Environment.NewLine}");
        }
        catch
        {
            _logPath = null;
        }
    }

    public static void Log(string line = "")
    {
        Console.WriteLine(line);
        WriteToFile(line);
    }

    public static void LogError(string line = "")
    {
        Console.Error.WriteLine(line);
        WriteToFile(line);
    }

    private static void WriteToFile(string line)
    {
        var path = _logPath;
        if (path is null) return;

        try
        {
            lock (Sync)
            {
                File.AppendAllText(path, $"{Timestamp()} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort only; a logging failure must never break the launch.
        }
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
}
