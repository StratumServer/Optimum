using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

/// <summary>
/// Writes and removes the application-menu and desktop shortcuts for an install.
/// Every operation is best effort: a shortcut that will not write is logged, not
/// fatal. Ports the shortcut handling from all three current installers.
/// </summary>
public sealed class ShortcutWriter(ISystemProbe probe)
{
    public const string ModInstallMimeType = "x-scheme-handler/vintagestorymodinstall";
    public const string LinuxDesktopFileName = "optimum.desktop";

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

    public void Remove(IEnumerable<string> shortcutPaths, bool removeUriAssociation = false, string? previousUriHandler = null)
    {
        bool removedLinuxDesktop = false;
        foreach (string path in shortcutPaths)
        {
            if (path.EndsWith("/applications/" + LinuxDesktopFileName, StringComparison.Ordinal)
                || path.EndsWith("\\applications\\" + LinuxDesktopFileName, StringComparison.Ordinal))
            {
                removedLinuxDesktop = true;
            }

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

        if (probe.Os == OsKind.Linux && (removeUriAssociation || removedLinuxDesktop))
        {
            if (removeUriAssociation
                || string.Equals(QueryDefaultUriHandler(), LinuxDesktopFileName, StringComparison.OrdinalIgnoreCase))
            {
                RemoveUriHandlerAssociation(previousUriHandler);
            }
            else
            {
                string applicationsDir = Posix(DataHome(), "applications");
                RefreshDesktopDatabase(applicationsDir);
                RemoveDesktopFromMimeCache(Posix(applicationsDir, "mimeinfo.cache"), LinuxDesktopFileName);
            }
        }
    }

    /// <summary>
    /// Queries the current default desktop handler for <c>x-scheme-handler/vintagestorymodinstall</c>
    /// on Linux, or returns <c>null</c> when none is configured or on non-Linux hosts.
    /// </summary>
    public string? QueryDefaultUriHandler(bool allowExternalProcess = true)
    {
        if (probe.Os != OsKind.Linux)
            return null;

        string configHome = ConfigHome();
        string dataHome = DataHome();
        string[] mimeAppsFiles =
        [
            Posix(configHome, "mimeapps.list"),
            Posix(dataHome, "applications", "mimeapps.list"),
            Posix(dataHome, "applications", "defaults.list"),
        ];

        foreach (string file in mimeAppsFiles)
        {
            if (TryReadIniAssociation(ReadFileText(file), "Default Applications", ModInstallMimeType) is { } handler)
                return handler;
        }

        if (allowExternalProcess)
        {
            ProcessOutcome query = probe.Run("xdg-mime", ["query", "default", ModInstallMimeType], TimeSpan.FromSeconds(5));
            if (query.Started && query.ExitCode == 0)
            {
                string trimmed = query.StandardOutput.Trim();
                if (trimmed.Length > 0)
                {
                    string? externalHandler = trimmed
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault(e => !string.Equals(e, LinuxDesktopFileName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(externalHandler))
                        return externalHandler;
                }
            }
        }

        string mimeCache = Posix(dataHome, "applications", "mimeinfo.cache");
        return TryReadIniAssociation(ReadFileText(mimeCache), "MIME Cache", ModInstallMimeType, excludeDesktopId: LinuxDesktopFileName);
    }

    /// <summary>
    /// Removes Optimum's default URI handler association on Linux and restores
    /// <paramref name="previousUriHandler"/> when that desktop entry still exists.
    /// </summary>
    public void RemoveUriHandlerAssociation(string? previousUriHandler = null)
    {
        if (probe.Os != OsKind.Linux)
            return;

        string configHome = ConfigHome();
        string dataHome = DataHome();
        string applicationsDir = Posix(dataHome, "applications");
        string? currentExplicit = QueryDefaultUriHandler(allowExternalProcess: false);
        string? restoreTarget = !string.IsNullOrWhiteSpace(previousUriHandler)
            && !string.Equals(previousUriHandler, LinuxDesktopFileName, StringComparison.OrdinalIgnoreCase)
            && DesktopEntryExists(previousUriHandler)
            ? previousUriHandler.Trim()
            : null;

        string[] mimeAppsFiles =
        [
            Posix(configHome, "mimeapps.list"),
            Posix(applicationsDir, "mimeapps.list"),
            Posix(applicationsDir, "defaults.list"),
        ];

        bool removedDefault = false;
        foreach (string file in mimeAppsFiles)
        {
            if (RemoveOrRestoreMimeAppsEntry(file, LinuxDesktopFileName, restoreTarget))
                removedDefault = true;
        }

        if (restoreTarget is not null
            && (removedDefault
                || string.IsNullOrWhiteSpace(currentExplicit)
                || string.Equals(currentExplicit, LinuxDesktopFileName, StringComparison.OrdinalIgnoreCase)))
        {
            SetDefaultUriHandler(restoreTarget);
        }

        RefreshDesktopDatabase(applicationsDir);
        string menuDesktopPath = Posix(applicationsDir, LinuxDesktopFileName);
        if ((File.Exists(menuDesktopPath) || probe.FileExists(menuDesktopPath)) && restoreTarget is not null)
        {
            UpdateMimeCache(Posix(applicationsDir, "mimeinfo.cache"), LinuxDesktopFileName, restoreTarget);
        }
        else if (!File.Exists(menuDesktopPath) && !probe.FileExists(menuDesktopPath))
        {
            RemoveDesktopFromMimeCache(Posix(applicationsDir, "mimeinfo.cache"), LinuxDesktopFileName);
        }
        else
        {
            UpdateMimeCache(Posix(applicationsDir, "mimeinfo.cache"), LinuxDesktopFileName, preferredFirst: null);
        }
    }

    private List<string> CreateLinux(string installDirectory, string launcherPath, ShortcutKinds kinds)
    {
        var written = new List<string>();
        string home = probe.HomeDirectory;
        string dataHome = DataHome();
        string applicationsDir = Posix(dataHome, "applications");
        string? priorDefault = QueryDefaultUriHandler();
        string? preferredDefault = kinds.HasFlag(ShortcutKinds.UriHandler)
            ? LinuxDesktopFileName
            : (!string.IsNullOrWhiteSpace(priorDefault)
                && !string.Equals(priorDefault, LinuxDesktopFileName, StringComparison.OrdinalIgnoreCase)
                ? priorDefault
                : null);

        string? icon = InstallIcon(installDirectory, Posix(dataHome, "icons", "hicolor", "256x256", "apps", "optimum.png"));

        if (kinds.HasFlag(ShortcutKinds.Menu) || kinds.HasFlag(ShortcutKinds.UriHandler))
        {
            bool noDisplay = !kinds.HasFlag(ShortcutKinds.Menu);
            string menuEntry = DesktopEntry(launcherPath, installDirectory, icon, noDisplay);
            written.AddRange(WriteText(Posix(applicationsDir, LinuxDesktopFileName), menuEntry, executable: true));
            RefreshDesktopDatabase(applicationsDir);
            UpdateMimeCache(Posix(applicationsDir, "mimeinfo.cache"), LinuxDesktopFileName, preferredDefault);
        }

        if (kinds.HasFlag(ShortcutKinds.Desktop))
        {
            string desktopEntry = DesktopEntry(launcherPath, installDirectory, icon, noDisplay: false);
            written.AddRange(WriteText(Posix(home, "Desktop", "Optimum.desktop"), desktopEntry, executable: true));
        }

        if (kinds.HasFlag(ShortcutKinds.UriHandler))
        {
            SetDefaultUriHandler(LinuxDesktopFileName);
        }
        else if (kinds.HasFlag(ShortcutKinds.Menu)
            && !string.IsNullOrWhiteSpace(priorDefault)
            && !string.Equals(priorDefault, LinuxDesktopFileName, StringComparison.OrdinalIgnoreCase))
        {
            // Keep the user's prior choice pinned in mimeapps.list so rebuilding
            // mimeinfo.cache never silently steals the association based on
            // alphabetical .desktop ordering.
            SetDefaultUriHandler(priorDefault);
        }

        return written;
    }

    private string DataHome() =>
        probe.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Posix(probe.HomeDirectory, ".local", "share");

    private string ConfigHome() =>
        probe.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Posix(probe.HomeDirectory, ".config");

    private void SetDefaultUriHandler(string desktopFileId)
    {
        string mimeAppsPath = Posix(ConfigHome(), "mimeapps.list");
        UpsertMimeAppsDefault(mimeAppsPath, ModInstallMimeType, desktopFileId);
        _ = probe.Run("xdg-mime", ["default", desktopFileId, ModInstallMimeType], TimeSpan.FromSeconds(5));
    }

    private void RefreshDesktopDatabase(string applicationsDir)
    {
        _ = probe.Run("update-desktop-database", [applicationsDir], TimeSpan.FromSeconds(5));
    }

    private bool DesktopEntryExists(string desktopFileId)
    {
        if (string.IsNullOrWhiteSpace(desktopFileId)
            || desktopFileId.Contains('/')
            || desktopFileId.Contains('\\'))
        {
            return false;
        }

        string trimmedId = desktopFileId.Trim();
        string userCandidate = Posix(DataHome(), "applications", trimmedId);
        if (probe.FileExists(userCandidate) || File.Exists(userCandidate))
            return true;

        string xdgDataDirs = probe.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs
            ? dirs
            : "/usr/local/share:/usr/share";

        foreach (string dir in xdgDataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Posix(dir, "applications", trimmedId);
            if (probe.FileExists(candidate))
                return true;
        }
        return false;
    }

    private string? ReadFileText(string path)
    {
        try
        {
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        catch (IOException) { /* fall through to probe */ }
        catch (UnauthorizedAccessException) { /* fall through to probe */ }

        return probe.ReadText(path);
    }

    private static string? TryReadIniAssociation(string? content, string sectionName, string mimeType, string? excludeDesktopId = null)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        string targetHeader = $"[{sectionName}]";
        bool inSection = false;
        string prefix = mimeType + "=";

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = string.Equals(line, targetHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rhs = line[prefix.Length..].Trim();
            string? first = rhs
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(e => excludeDesktopId is null
                    || !string.Equals(e, excludeDesktopId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(first))
                return first;
        }

        return null;
    }

    private void UpsertMimeAppsDefault(string path, string mimeType, string desktopFileId)
    {
        const string sectionHeader = "[Default Applications]";
        string keyPrefix = mimeType + "=";
        string newEntry = $"{mimeType}={desktopFileId}";
        string? existing = ReadFileText(path);

        var lines = existing is not null
            ? existing.Replace("\r\n", "\n").Split('\n').ToList()
            : new List<string>();

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        int sectionIndex = -1;
        int nextSectionIndex = lines.Count;
        int keyIndex = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    sectionIndex = i;
                }
                else if (sectionIndex >= 0 && nextSectionIndex == lines.Count)
                {
                    nextSectionIndex = i;
                }
                continue;
            }

            if (sectionIndex >= 0 && nextSectionIndex == lines.Count
                && trimmed.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                keyIndex = i;
            }
        }

        if (keyIndex >= 0)
        {
            lines[keyIndex] = newEntry;
        }
        else if (sectionIndex >= 0)
        {
            lines.Insert(nextSectionIndex, newEntry);
        }
        else
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);
            lines.Add(sectionHeader);
            lines.Add(newEntry);
        }

        _ = WriteText(path, string.Join('\n', lines) + "\n", executable: false);
    }

    private bool RemoveOrRestoreMimeAppsEntry(string path, string desktopToRemove, string? restoreDesktop)
    {
        string? existing = ReadFileText(path);
        if (existing is null)
            return false;

        string keyPrefix = ModInstallMimeType + "=";
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        string currentSection = string.Empty;
        bool modified = false;
        bool removedFromDefaults = false;
        var updated = new List<string>(lines.Count);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed;
                updated.Add(line);
                continue;
            }

            if ((string.Equals(currentSection, "[Default Applications]", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(currentSection, "[Added Associations]", StringComparison.OrdinalIgnoreCase))
                && trimmed.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rhs = trimmed[keyPrefix.Length..];
                bool hadTrailingSemicolon = rhs.TrimEnd().EndsWith(';');
                var entries = rhs
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                int removedCount = entries.RemoveAll(e => string.Equals(e, desktopToRemove, StringComparison.OrdinalIgnoreCase));
                if (removedCount > 0)
                {
                    modified = true;
                    bool isDefaultsSection = string.Equals(currentSection, "[Default Applications]", StringComparison.OrdinalIgnoreCase);
                    if (isDefaultsSection)
                        removedFromDefaults = true;
                    if (restoreDesktop is not null
                        && isDefaultsSection
                        && !entries.Any(e => string.Equals(e, restoreDesktop, StringComparison.OrdinalIgnoreCase)))
                    {
                        entries.Insert(0, restoreDesktop);
                    }

                    if (entries.Count > 0)
                    {
                        string joined = string.Join(';', entries) + (hadTrailingSemicolon || entries.Count > 1 ? ";" : string.Empty);
                        updated.Add($"{ModInstallMimeType}={joined}");
                    }
                    continue;
                }
            }

            updated.Add(line);
        }

        if (modified)
        {
            while (updated.Count > 0 && updated[^1].Length == 0)
                updated.RemoveAt(updated.Count - 1);
            _ = WriteText(path, updated.Count == 0 ? string.Empty : string.Join('\n', updated) + "\n", executable: false);
        }

        return removedFromDefaults;
    }

    private void UpdateMimeCache(string mimeCachePath, string desktopFileId, string? preferredFirst = null)
    {
        const string header = "[MIME Cache]";
        string keyPrefix = ModInstallMimeType + "=";
        string? existing = ReadFileText(mimeCachePath);
        var lines = existing is not null
            ? existing.Replace("\r\n", "\n").Split('\n').ToList()
            : new List<string> { header };

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (!lines.Any(l => string.Equals(l.Trim(), header, StringComparison.OrdinalIgnoreCase)))
            lines.Insert(0, header);

        string? validPreferred = !string.IsNullOrWhiteSpace(preferredFirst)
            && (string.Equals(preferredFirst, desktopFileId, StringComparison.OrdinalIgnoreCase)
                || DesktopEntryExists(preferredFirst))
            ? preferredFirst.Trim()
            : null;

        int idx = lines.FindIndex(l => l.Trim().StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var entries = lines[idx].Trim()[keyPrefix.Length..]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (!entries.Any(e => string.Equals(e, desktopFileId, StringComparison.OrdinalIgnoreCase)))
                entries.Add(desktopFileId);
            if (validPreferred is not null)
            {
                entries.RemoveAll(e => string.Equals(e, validPreferred, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, validPreferred);
            }
            lines[idx] = $"{ModInstallMimeType}={string.Join(';', entries)};";
        }
        else
        {
            string lineValue = validPreferred is not null && !string.Equals(validPreferred, desktopFileId, StringComparison.OrdinalIgnoreCase)
                ? $"{validPreferred};{desktopFileId};"
                : $"{desktopFileId};";
            lines.Add($"{ModInstallMimeType}={lineValue}");
        }

        _ = WriteText(mimeCachePath, string.Join('\n', lines) + "\n", executable: false);
    }

    private void RemoveDesktopFromMimeCache(string mimeCachePath, string desktopFileId)
    {
        string? existing = ReadFileText(mimeCachePath);
        if (existing is null)
            return;

        string keyPrefix = ModInstallMimeType + "=";
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        bool modified = false;
        var updated = new List<string>(lines.Count);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var entries = trimmed[keyPrefix.Length..]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (entries.RemoveAll(e => string.Equals(e, desktopFileId, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    modified = true;
                    if (entries.Count > 0)
                        updated.Add($"{ModInstallMimeType}={string.Join(';', entries)};");
                    continue;
                }
            }
            updated.Add(line);
        }

        if (modified)
        {
            while (updated.Count > 0 && updated[^1].Length == 0)
                updated.RemoveAt(updated.Count - 1);
            _ = WriteText(mimeCachePath, updated.Count == 0 ? string.Empty : string.Join('\n', updated) + "\n", executable: false);
        }
    }

    /// <summary>
    /// Joins Linux paths with '/', the separator the target platform uses, rather
    /// than System.IO.Path.Combine (which emits '\' on a Windows build host and
    /// would produce mixed separators in the generated entries).
    /// </summary>
    private static string Posix(string root, params string[] parts) =>
        root.TrimEnd('/', '\\') + "/" + string.Join('/', parts);

    private List<string> CreateMac(string installDirectory, ShortcutKinds kinds)
    {
        var written = new List<string>();
        string home = probe.HomeDirectory;

        // The link target is a real .app bundle: a nested Optimum.app, or the
        // install directory itself when the bundle contents were laid there.
        string nested = Path.Combine(installDirectory, "Optimum.app");
        string target = File.Exists(Path.Combine(nested, "Contents", "Info.plist"))
            ? nested
            : installDirectory;
        bool isBundle = File.Exists(Path.Combine(target, "Contents", "Info.plist"));
        string linkName = isBundle ? "Optimum.app" : "Optimum";

        if (kinds.HasFlag(ShortcutKinds.Menu))
            written.AddRange(Symlink(Path.Combine(home, "Applications", linkName), target));
        if (kinds.HasFlag(ShortcutKinds.Desktop))
            written.AddRange(Symlink(Path.Combine(home, "Desktop", linkName), target));

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

    private static string DesktopEntry(string launcherPath, string workingDirectory, string? icon, bool noDisplay = false)
    {
        string displayLine = noDisplay ? "NoDisplay=true\n" : string.Empty;
        return
            $"""
            [Desktop Entry]
            Type=Application
            Name=Optimum
            Comment=High-performance client for Vintage Story
            Exec="{EscapeExec(launcherPath)}" %u
            Path={workingDirectory}
            Icon={icon ?? "optimum"}
            Terminal=false
            Categories=Game;
            MimeType={ModInstallMimeType};
            {displayLine}StartupWMClass=Optimum

            """;
    }

    /// <summary>
    /// Escapes a value for a quoted <c>Exec</c> per the Desktop Entry spec:
    /// backslash, double quote, backtick, and dollar are backslash-escaped,
    /// and literal percent signs are doubled so they are not parsed as field codes.
    /// </summary>
    private static string EscapeExec(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            if (c == '%')
            {
                sb.Append("%%");
                continue;
            }
            if (c is '\\' or '"' or '`' or '$')
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

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
