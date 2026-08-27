using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Optimum.Bootstrap.Core.Platform;

public enum OsKind
{
    Windows,
    Linux,
    MacOs,
}

/// <summary>The outcome of a short probe command such as <c>dotnet --list-sdks</c>.</summary>
public readonly record struct ProcessOutcome(bool Started, int ExitCode, string StandardOutput, string StandardError)
{
    public static readonly ProcessOutcome NotStarted = new(false, -1, string.Empty, string.Empty);
}

/// <summary>
/// The seam between Core and the machine. Every detection path takes an
/// <see cref="ISystemProbe"/> so tests supply a fake filesystem and fake command
/// output instead of touching the host. The real implementation is
/// <see cref="SystemProbe"/>.
/// </summary>
public interface ISystemProbe
{
    OsKind Os { get; }
    Architecture Arch { get; }
    string HomeDirectory { get; }
    string? GetEnvironmentVariable(string name);
    IReadOnlyList<string> PathDirectories { get; }

    /// <summary>True for a regular file (<c>[[ -f ]]</c>).</summary>
    bool FileExists(string path);

    /// <summary>
    /// True when the file exists and carries an execute bit (<c>[[ -x ]]</c>).
    /// On Windows a file whose name matches an executable extension counts.
    /// </summary>
    bool IsExecutable(string path);

    /// <summary>True for a directory (<c>[[ -d ]]</c>).</summary>
    bool DirectoryExists(string path);

    /// <summary>True for anything at that path, including a broken symlink (<c>[[ -e ]]</c>).</summary>
    bool PathExists(string path);

    /// <summary>True when the leaf at <paramref name="path"/> is a symbolic link.</summary>
    bool IsSymbolicLink(string path);

    string? ReadText(string path);

    IEnumerable<string> EnumerateFiles(string directory, string searchPattern);

    /// <summary>
    /// Runs a short-lived command and returns its output. Never throws: a spawn
    /// failure comes back as <see cref="ProcessOutcome.NotStarted"/>. The caller
    /// bounds the wait through <paramref name="timeout"/>.
    /// </summary>
    ProcessOutcome Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout);
}

public sealed class SystemProbe : ISystemProbe
{
    public static readonly SystemProbe Default = new();

    public OsKind Os { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OsKind.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OsKind.MacOs
        : OsKind.Linux;

    public Architecture Arch => RuntimeInformation.OSArchitecture;

    public string HomeDirectory { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public IReadOnlyList<string> PathDirectories { get; } =
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    public bool FileExists(string path) => File.Exists(path);

    public bool IsExecutable(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool IsSymbolicLink(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return new DirectoryInfo(path).LinkTarget is not null;
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        if (!Directory.Exists(directory))
            return [];
        try { return Directory.EnumerateFiles(directory, searchPattern); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public ProcessOutcome Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return ProcessOutcome.NotStarted;

            // Drain both pipes concurrently so a child that fills one buffer
            // while we block on the other cannot deadlock the probe.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return ProcessOutcome.NotStarted;
            }

            return new ProcessOutcome(true, process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ProcessOutcome.NotStarted;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
