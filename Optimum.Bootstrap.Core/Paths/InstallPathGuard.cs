using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Paths;

public sealed record InstallPathRequest(
    string InstallDirectory,
    string? DataPath = null,
    string? VintageStoryDirectory = null,
    string? WorkspaceRoot = null,
    string? BuildRoot = null);

public sealed record InstallPathVerdict(bool Ok, string? Rejection)
{
    public static readonly InstallPathVerdict Allowed = new(true, null);

    public static InstallPathVerdict Reject(string reason) => new(false, reason);
}

/// <summary>
/// Consolidates <c>guard_install_dir</c> from <c>scripts/install-linux.sh</c> and
/// <c>Assert-SafeInstallerPaths</c> from <c>scripts/install-windows.ps1</c>, plus
/// a symlink-component walk. Every current installer refuses a different subset
/// of these; the new one refuses all of them on every platform.
/// </summary>
public static partial class InstallPathGuard
{
    public static InstallPathVerdict Check(ISystemProbe probe, InstallPathRequest request)
    {
        string raw = request.InstallDirectory;
        if (string.IsNullOrWhiteSpace(raw))
            return InstallPathVerdict.Reject("The install directory is empty.");

        if (IsFilesystemRoot(probe, raw))
            return InstallPathVerdict.Reject($"The install directory cannot be a filesystem or drive root: {raw.Trim()}");

        string install = Canonical(probe, raw);

        if (PathEquals(probe, install, Canonical(probe, probe.HomeDirectory)))
            return InstallPathVerdict.Reject("The install directory cannot be your home directory.");

        foreach (string reserved in ReservedDirectories(probe))
        {
            if (PathEquals(probe, install, reserved))
                return InstallPathVerdict.Reject($"The install directory cannot be {reserved}.");
        }

        // Leaf only: a symlinked home or a symlinked parent (a second drive
        // mounted at ~/Games) is normal and the OS resolves it consistently. A
        // symlinked install directory itself is the risk, because the
        // transactional install and uninstall would then operate on the link's
        // target rather than the directory the user named.
        if (probe.PathExists(install) && probe.IsSymbolicLink(install))
            return InstallPathVerdict.Reject($"The install directory is a symbolic link: {install}. Choose a real directory.");

        foreach (string vsDir in KnownVintageStoryDirectories(probe))
        {
            if (IsWithinOrEqual(probe, install, Canonical(probe, vsDir)))
                return InstallPathVerdict.Reject(
                    $"The install directory cannot be inside a Vintage Story installation ({vsDir}). Optimum installs to a separate location.");
        }

        if (LooksLikeVanillaGame(probe, install))
            return InstallPathVerdict.Reject(
                "The install directory already holds a vanilla Vintage Story installation. Optimum installs to a separate location.");

        foreach ((string? other, string name) in NamedNeighbours(request))
        {
            if (other is null)
                continue;
            string canonicalOther = Canonical(probe, other);
            if (IsWithinOrEqual(probe, install, canonicalOther) || IsWithinOrEqual(probe, canonicalOther, install))
                return InstallPathVerdict.Reject($"The install directory cannot overlap {name}.");
        }

        if (request.DataPath is { } dataRaw && !string.IsNullOrWhiteSpace(dataRaw))
        {
            string data = Canonical(probe, dataRaw);
            if (probe.PathExists(data) && probe.IsSymbolicLink(data))
                return InstallPathVerdict.Reject($"The data path is a symbolic link: {data}. Choose a real directory.");
            if (IsWithinOrEqual(probe, data, install))
                return InstallPathVerdict.Reject("The data path cannot be inside the install directory.");
            foreach (string vsDir in KnownVintageStoryDirectories(probe))
            {
                if (IsWithinOrEqual(probe, data, Canonical(probe, vsDir)))
                    return InstallPathVerdict.Reject("The data path cannot be inside a Vintage Story installation.");
            }
            foreach ((string? other, string name) in NamedNeighbours(request))
            {
                if (other is not null && IsWithinOrEqual(probe, data, Canonical(probe, other)))
                    return InstallPathVerdict.Reject($"The data path cannot be inside {name}.");
            }
        }

        return InstallPathVerdict.Allowed;
    }

    private static IEnumerable<(string? Path, string Name)> NamedNeighbours(InstallPathRequest request)
    {
        yield return (request.VintageStoryDirectory, "the Vintage Story directory");
        yield return (request.WorkspaceRoot, "the Optimum workspace");
        yield return (request.BuildRoot, "the temporary build directory");
    }

    private static bool IsFilesystemRoot(ISystemProbe probe, string raw)
    {
        string trimmed = raw.Trim();
        if (probe.Os == OsKind.Windows)
            return WindowsDriveRoot().IsMatch(trimmed) || trimmed is "\\" or "/";
        return trimmed == "/";
    }

    private static IEnumerable<string> ReservedDirectories(ISystemProbe probe)
    {
        if (probe.Os != OsKind.Windows)
        {
            string home = probe.HomeDirectory;
            string xdg = probe.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(home, ".local", "share");
            yield return Canonical(probe, xdg);
            yield return Canonical(probe, Path.Combine(home, ".local"));
        }
    }

    private static IEnumerable<string> KnownVintageStoryDirectories(ISystemProbe probe)
    {
        string home = probe.HomeDirectory;
        switch (probe.Os)
        {
            case OsKind.Windows:
                foreach (string var in new[] { "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)" })
                {
                    if (probe.GetEnvironmentVariable(var) is { Length: > 0 } value)
                        yield return Path.Combine(value, "Vintagestory");
                }
                break;
            case OsKind.MacOs:
                yield return Path.Combine(home, "Library", "Application Support", "vintagestory");
                yield return "/Applications/Vintagestory.app";
                break;
            default:
                yield return Path.Combine(home, ".local", "share", "vintagestory");
                yield return Path.Combine(home, "ApplicationData", "vintagestory");
                yield return "/opt/vintagestory";
                break;
        }
    }

    private static bool LooksLikeVanillaGame(ISystemProbe probe, string directory)
    {
        bool hasGame = probe.FileExists(Path.Combine(directory, "Vintagestory"))
            || probe.FileExists(Path.Combine(directory, "Vintagestory.exe"));
        bool hasOptimum = probe.FileExists(Path.Combine(directory, "Optimum"))
            || probe.FileExists(Path.Combine(directory, "Optimum.exe"));
        return hasGame && !hasOptimum;
    }

    private static string Canonical(ISystemProbe probe, string path)
    {
        string trimmed = path.Trim();

        if (ProbeMatchesHost(probe))
        {
            try { trimmed = Path.GetFullPath(trimmed); }
            catch (ArgumentException) { /* fall through to string normalization */ }
        }

        if (probe.Os == OsKind.Windows)
        {
            trimmed = trimmed.Replace('/', '\\');
            if (WindowsDriveRoot().IsMatch(trimmed))
                return trimmed.Length == 2 ? trimmed + "\\" : trimmed;
            return trimmed.TrimEnd('\\');
        }

        trimmed = trimmed.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static bool ProbeMatchesHost(ISystemProbe probe) => probe.Os switch
    {
        OsKind.Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        OsKind.MacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        _ => RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
    };

    private static StringComparison Comparison(ISystemProbe probe) =>
        probe.Os == OsKind.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool PathEquals(ISystemProbe probe, string a, string b) =>
        string.Equals(a, b, Comparison(probe));

    private static bool IsWithinOrEqual(ISystemProbe probe, string child, string parent)
    {
        if (PathEquals(probe, child, parent))
            return true;
        char sep = probe.Os == OsKind.Windows ? '\\' : '/';
        return child.StartsWith(parent + sep, Comparison(probe));
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]?$")]
    private static partial Regex WindowsDriveRoot();
}
