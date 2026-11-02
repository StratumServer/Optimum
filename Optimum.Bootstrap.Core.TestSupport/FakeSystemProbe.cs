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
    public bool CanWrite { get; set; } = true;

    /// <summary>Files that exist but lack an execute bit. Everything else in <see cref="Files"/> is executable.</summary>
    public HashSet<string> NonExecutable { get; } = new();
    public Dictionary<string, string> FileContents { get; } = new();

    /// <summary>Keyed on <c>"exe|arg1 arg2"</c>. Falls back to <see cref="ProcessOutcome.NotStarted"/>.</summary>
    public Dictionary<string, ProcessOutcome> Commands { get; } = new();

    public FakeSystemProbe AddFile(string path, string? content = null)
    {
        Files.Add(Norm(path));
        if (content is not null)
            FileContents[Norm(path)] = content;
        return this;
    }

    public FakeSystemProbe AddDirectory(string path)
    {
        Directories.Add(Norm(path));
        return this;
    }

    public FakeSystemProbe AddSymlink(string path)
    {
        Symlinks.Add(Norm(path));
        return this;
    }

    public FakeSystemProbe AddNonExecutableFile(string path)
    {
        Files.Add(Norm(path));
        NonExecutable.Add(Norm(path));
        return this;
    }

    public FakeSystemProbe OnCommand(string exe, string args, string stdout = "", int exitCode = 0)
    {
        Commands[$"{Norm(exe)}|{args}"] = new ProcessOutcome(true, exitCode, stdout, string.Empty);
        return this;
    }

    string? ISystemProbe.GetEnvironmentVariable(string name) =>
        Environment.TryGetValue(name, out string? value) ? value : null;

    IReadOnlyList<string> ISystemProbe.PathDirectories => Path;

    bool ISystemProbe.FileExists(string path) => Files.Contains(Norm(path));

    bool ISystemProbe.IsExecutable(string path) => Files.Contains(Norm(path)) && !NonExecutable.Contains(Norm(path));

    bool ISystemProbe.DirectoryExists(string path) => Directories.Contains(Norm(path));

    bool ISystemProbe.PathExists(string path) =>
        Files.Contains(Norm(path)) || Directories.Contains(Norm(path)) || Symlinks.Contains(Norm(path));

    bool ISystemProbe.IsSymbolicLink(string path) => Symlinks.Contains(Norm(path));

    bool ISystemProbe.CanWriteToDirectory(string path) => CanWrite;

    string? ISystemProbe.ReadText(string path) =>
        FileContents.TryGetValue(Norm(path), out string? content) ? content : null;

    IEnumerable<string> ISystemProbe.EnumerateFiles(string directory, string searchPattern) =>
        Files.Where(f => DirOf(f) == Norm(directory) && Matches(f, searchPattern));

    IEnumerable<string> ISystemProbe.EnumerateDirectories(string directory, string searchPattern) =>
        Directories.Where(d => DirOf(d) == Norm(directory) && Matches(d, searchPattern));

    /// <summary>
    /// The fake models a POSIX filesystem with '/' separators. Production code
    /// builds candidate paths with <see cref="System.IO.Path.Combine"/>, which on
    /// Windows inserts '\'. Normalise both the stored keys and every lookup to '/'
    /// so the tests behave identically on Linux CI and a Windows developer box.
    /// </summary>
    private static string Norm(string path) => path.Replace('\\', '/');

    private static string? DirOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? (slash == 0 ? "/" : null) : path[..slash];
    }

    private static bool Matches(string path, string searchPattern)
    {
        if (searchPattern == "*")
            return true;
        string name = System.IO.Path.GetFileName(path);
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(searchPattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex);
    }

    ProcessOutcome ISystemProbe.Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout) =>
        Commands.TryGetValue($"{Norm(executable)}|{string.Join(' ', arguments)}", out ProcessOutcome outcome)
            ? outcome
            : ProcessOutcome.NotStarted;
}
