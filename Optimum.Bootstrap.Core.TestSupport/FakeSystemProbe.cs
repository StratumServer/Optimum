using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>In-memory <see cref="ISystemProbe"/> for detection tests.</summary>
public sealed class FakeSystemProbe : ISystemProbe
{
    public OsKind Os { get; set; } = OsKind.Linux;
    public Architecture Arch { get; set; } = Architecture.X64;
    public string HomeDirectory { get; set; } = "/home/tester";

    public Dictionary<string, string?> Environment { get; } = new();
    public List<string> Path { get; } = [];
    public HashSet<string> Files { get; } = new();
    public HashSet<string> Directories { get; } = new();
    public HashSet<string> Symlinks { get; } = new();

    /// <summary>Files that exist but lack an execute bit. Everything else in <see cref="Files"/> is executable.</summary>
    public HashSet<string> NonExecutable { get; } = new();
    public Dictionary<string, string> FileContents { get; } = new();

    /// <summary>Keyed on <c>"exe|arg1 arg2"</c>. Falls back to <see cref="ProcessOutcome.NotStarted"/>.</summary>
    public Dictionary<string, ProcessOutcome> Commands { get; } = new();

    public FakeSystemProbe AddFile(string path, string? content = null)
    {
        Files.Add(path);
        if (content is not null)
            FileContents[path] = content;
        return this;
    }

    public FakeSystemProbe AddDirectory(string path)
    {
        Directories.Add(path);
        return this;
    }

    public FakeSystemProbe AddSymlink(string path)
    {
        Symlinks.Add(path);
        return this;
    }

    public FakeSystemProbe AddNonExecutableFile(string path)
    {
        Files.Add(path);
        NonExecutable.Add(path);
        return this;
    }

    public FakeSystemProbe OnCommand(string exe, string args, string stdout = "", int exitCode = 0)
    {
        Commands[$"{exe}|{args}"] = new ProcessOutcome(true, exitCode, stdout, string.Empty);
        return this;
    }

    string? ISystemProbe.GetEnvironmentVariable(string name) =>
        Environment.TryGetValue(name, out string? value) ? value : null;

    IReadOnlyList<string> ISystemProbe.PathDirectories => Path;

    bool ISystemProbe.FileExists(string path) => Files.Contains(path);

    bool ISystemProbe.IsExecutable(string path) => Files.Contains(path) && !NonExecutable.Contains(path);

    bool ISystemProbe.DirectoryExists(string path) => Directories.Contains(path);

    bool ISystemProbe.PathExists(string path) =>
        Files.Contains(path) || Directories.Contains(path) || Symlinks.Contains(path);

    bool ISystemProbe.IsSymbolicLink(string path) => Symlinks.Contains(path);

    string? ISystemProbe.ReadText(string path) =>
        FileContents.TryGetValue(path, out string? content) ? content : null;

    IEnumerable<string> ISystemProbe.EnumerateFiles(string directory, string searchPattern) =>
        Files.Where(f => System.IO.Path.GetDirectoryName(f) == directory && Matches(f, searchPattern));

    IEnumerable<string> ISystemProbe.EnumerateDirectories(string directory, string searchPattern) =>
        Directories.Where(d => System.IO.Path.GetDirectoryName(d) == directory && Matches(d, searchPattern));

    private static bool Matches(string path, string searchPattern)
    {
        if (searchPattern == "*")
            return true;
        string name = System.IO.Path.GetFileName(path);
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(searchPattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex);
    }

    ProcessOutcome ISystemProbe.Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout) =>
        Commands.TryGetValue($"{executable}|{string.Join(' ', arguments)}", out ProcessOutcome outcome)
            ? outcome
            : ProcessOutcome.NotStarted;
}
