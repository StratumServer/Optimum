using System.Runtime.InteropServices;

namespace Optimum.Bootstrap.Core.Platform;

/// <summary>Builds a process-local macOS PATH that also works outside a login shell.</summary>
public static class MacOsPath
{
    public static IReadOnlyList<string> Build(
        string? inheritedPath,
        string homeDirectory,
        OsKind os,
        Architecture architecture,
        Func<string, bool>? directoryExists = null)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;
            string normalized = directory.Trim();
            if (normalized.Length == 0 || !seen.Add(normalized))
                return;
            if (directoryExists is null || directoryExists(normalized))
                directories.Add(normalized);
        }

        foreach (string directory in (inheritedPath ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Add(directory);

        if (os == OsKind.MacOs)
        {
            // Select the native Homebrew prefix for this process architecture.
            if (architecture == Architecture.Arm64)
            {
                Add("/opt/homebrew/bin");
                Add("/opt/homebrew/sbin");
            }
            else if (architecture == Architecture.X64)
            {
                Add("/usr/local/bin");
                Add("/usr/local/sbin");
            }

            // Microsoft installer locations and the per-user .NET global tools directory.
            Add("/usr/local/bin");
            Add("/usr/local/sbin");
            Add("/usr/local/share/dotnet");
            if (architecture == Architecture.Arm64)
                Add("/usr/local/share/dotnet/x64");
            Add(Path.Combine(homeDirectory, ".dotnet", "tools"));

            // Command Line Tools and full Xcode provide additional developer
            // executables even when the app was launched outside a login shell.
            Add("/Library/Developer/CommandLineTools/usr/bin");
            Add("/Applications/Xcode.app/Contents/Developer/usr/bin");
        }

        return directories;
    }
}
