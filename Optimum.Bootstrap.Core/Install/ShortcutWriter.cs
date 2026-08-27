using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

/// <summary>
/// Writes and removes the application-menu and desktop shortcuts for an install.
/// Every operation is best effort: a shortcut that will not write is logged, not
/// fatal. Ports the shortcut handling from all three current installers.
/// </summary>
public sealed class ShortcutWriter(ISystemProbe probe)
{
    /// <summary>Creates the requested shortcuts and returns the paths that were written.</summary>
    public IReadOnlyList<string> Create(string installDirectory, string launcherPath, ShortcutKinds kinds)
    {
        if (kinds == ShortcutKinds.None)
            return [];

        return probe.Os switch
        {
            OsKind.Windows => CreateWindows(installDirectory, launcherPath, kinds),
            OsKind.MacOs => CreateMac(installDirectory, kinds),
            _ => CreateLinux(installDirectory, launcherPath, kinds),
        };
    }

    public void Remove(IEnumerable<string> shortcutPaths)
    {
        foreach (string path in shortcutPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                else if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    private List<string> CreateLinux(string installDirectory, string launcherPath, ShortcutKinds kinds)
    {
        var written = new List<string>();
        string home = probe.HomeDirectory;
        string dataHome = probe.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(home, ".local", "share");

        string? icon = InstallIcon(installDirectory, Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", "optimum.png"));
        string entry = DesktopEntry(launcherPath, installDirectory, icon);

        if (kinds.HasFlag(ShortcutKinds.Menu))
            written.AddRange(WriteText(Path.Combine(dataHome, "applications", "optimum.desktop"), entry, executable: true));
        if (kinds.HasFlag(ShortcutKinds.Desktop))
            written.AddRange(WriteText(Path.Combine(home, "Desktop", "Optimum.desktop"), entry, executable: true));

        return written;
    }

    private List<string> CreateMac(string installDirectory, ShortcutKinds kinds)
    {
        var written = new List<string>();
        string home = probe.HomeDirectory;
        string target = Directory.Exists(Path.Combine(installDirectory, "Optimum.app"))
            ? Path.Combine(installDirectory, "Optimum.app")
            : installDirectory;

        if (kinds.HasFlag(ShortcutKinds.Menu))
            written.AddRange(Symlink(Path.Combine(home, "Applications", "Optimum" + (target.EndsWith(".app", StringComparison.Ordinal) ? ".app" : "")), target));
        if (kinds.HasFlag(ShortcutKinds.Desktop))
            written.AddRange(Symlink(Path.Combine(home, "Desktop", "Optimum" + (target.EndsWith(".app", StringComparison.Ordinal) ? ".app" : "")), target));

        return written;
    }

    private List<string> CreateWindows(string installDirectory, string launcherPath, ShortcutKinds kinds)
    {
        var written = new List<string>();
        if (!OperatingSystem.IsWindows())
            return written;

        string? appData = probe.GetEnvironmentVariable("APPDATA");
        string? userProfile = probe.GetEnvironmentVariable("USERPROFILE") ?? probe.HomeDirectory;
        string exe = Path.Combine(installDirectory, "Optimum.exe");
        string linkTarget = File.Exists(exe) ? exe : launcherPath;

        if (kinds.HasFlag(ShortcutKinds.Menu) && appData is not null)
        {
            string dir = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Optimum");
            written.AddRange(WriteWindowsLink(Path.Combine(dir, "Optimum.lnk"), linkTarget, installDirectory));
        }
        if (kinds.HasFlag(ShortcutKinds.Desktop) && userProfile is not null)
            written.AddRange(WriteWindowsLink(Path.Combine(userProfile, "Desktop", "Optimum.lnk"), linkTarget, installDirectory));

        return written;
    }

    private static string DesktopEntry(string launcherPath, string workingDirectory, string? icon) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Optimum
        Comment=High-performance client for Vintage Story
        Exec="{launcherPath}"
        Path={workingDirectory}
        Icon={icon ?? "optimum"}
        Terminal=false
        Categories=Game;
        StartupWMClass=Optimum

        """;

    private string? InstallIcon(string installDirectory, string destination)
    {
        string[] sources =
        [
            Path.Combine(installDirectory, "assets", "gameicon.png"),
            Path.Combine(installDirectory, "logo.png"),
        ];
        foreach (string source in sources)
        {
            if (!File.Exists(source))
                continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                return destination;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        return null;
    }

    private static IEnumerable<string> WriteText(string path, string contents, bool executable)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            if (executable && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
            return [path];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Symlink(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            if (File.Exists(link) || Directory.Exists(link))
                File.Delete(link);
            File.CreateSymbolicLink(link, target);
            return [link];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> WriteWindowsLink(string linkPath, string target, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return [];
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = target;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = target + ",0";
            link.Save();
            return [linkPath];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }
}
