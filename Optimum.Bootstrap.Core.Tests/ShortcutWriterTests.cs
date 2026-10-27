using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class ShortcutWriterTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("optimum-shortcut-test").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private FakeSystemProbe LinuxProbe()
    {
        string home = _home.Replace('\\', '/');
        var probe = new FakeSystemProbe { Os = OsKind.Linux, HomeDirectory = home };
        probe.Environment["XDG_DATA_HOME"] = home + "/.local/share";
        probe.Environment["XDG_CONFIG_HOME"] = home + "/.config";
        return probe;
    }

    [Fact]
    public void WritesAndRemovesTheLinuxMenuAndDesktopEntries()
    {
        FakeSystemProbe probe = LinuxProbe();
        // A Linux install uses '/' paths. Build them with '/' so the generated
        // .desktop Exec matches regardless of the host running the test (on
        // Windows Path.Combine would inject '\', which the Desktop Entry writer
        // then backslash-escapes, diverging from this assertion).
        string home = _home.Replace('\\', '/');
        string installDir = $"{home}/games/optimum";
        string launcher = $"{installDir}/optimum-launch.sh";
        Directory.CreateDirectory(installDir);

        var writer = new ShortcutWriter(probe);
        IReadOnlyList<string> created = writer.Create(installDir, launcher, ShortcutKinds.Menu | ShortcutKinds.Desktop);

        string menuEntry = $"{home}/.local/share/applications/optimum.desktop";
        string desktopEntry = $"{home}/Desktop/Optimum.desktop";
        Assert.Contains(menuEntry, created);
        Assert.Contains(desktopEntry, created);
        Assert.True(File.Exists(menuEntry));
        string content = File.ReadAllText(menuEntry);
        Assert.Contains($"Exec=\"{launcher}\" %u", content);
        Assert.Contains("MimeType=x-scheme-handler/vintagestorymodinstall;", content);
        Assert.DoesNotContain("NoDisplay=true", content);

        writer.Remove(created);
        Assert.False(File.Exists(menuEntry));
        Assert.False(File.Exists(desktopEntry));
    }

    [Fact]
    public void QuotesAndEscapesPathsWithSpacesAndSpecialCharactersInLinuxDesktopEntry()
    {
        FakeSystemProbe probe = LinuxProbe();
        string home = _home.Replace('\\', '/');
        string installDir = $"{home}/My Games/Optimum \"Special\" $1 100%";
        string launcher = $"{installDir}/optimum-launch.sh";

        var writer = new ShortcutWriter(probe);
        IReadOnlyList<string> created = writer.Create(installDir, launcher, ShortcutKinds.Menu);

        string menuEntry = Assert.Single(created);
        string content = File.ReadAllText(menuEntry);
        Assert.Contains($"Exec=\"{home}/My Games/Optimum \\\"Special\\\" \\$1 100%%/optimum-launch.sh\" %u", content);
        Assert.Contains($"Path={installDir}", content);
        Assert.Contains("MimeType=x-scheme-handler/vintagestorymodinstall;", content);
    }

    [Fact]
    public void RemovingDesktopShortcutAloneDoesNotClearActiveUriHandlerAssociation()
    {
        FakeSystemProbe probe = LinuxProbe();
        string home = _home.Replace('\\', '/');
        string installDir = $"{home}/games/optimum";
        string launcher = $"{installDir}/optimum-launch.sh";
        Directory.CreateDirectory(installDir);

        var writer = new ShortcutWriter(probe);
        IReadOnlyList<string> created = writer.Create(
            installDir,
            launcher,
            ShortcutKinds.Menu | ShortcutKinds.Desktop | ShortcutKinds.UriHandler);

        Assert.Equal("optimum.desktop", writer.QueryDefaultUriHandler());
        string desktopShortcut = created.Single(p => p.EndsWith("/Desktop/Optimum.desktop", StringComparison.Ordinal));

        writer.Remove([desktopShortcut]);
        Assert.Equal("optimum.desktop", writer.QueryDefaultUriHandler());
    }

    [Fact]
    public void RemoveUriHandlerAssociationDoesNotOverwriteNewerThirdPartyDefaultHandler()
    {
        FakeSystemProbe probe = LinuxProbe();
        string home = _home.Replace('\\', '/');
        string installDir = $"{home}/games/optimum";
        string launcher = $"{installDir}/optimum-launch.sh";
        string appsDir = $"{home}/.local/share/applications";
        string mimeApps = $"{home}/.config/mimeapps.list";
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(appsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(mimeApps)!);

        File.WriteAllText($"{appsDir}/Vintagestory_url_mod.desktop", "[Desktop Entry]\nName=VS\n");
        File.WriteAllText($"{appsDir}/custom-mod-manager.desktop", "[Desktop Entry]\nName=Custom\n");
        File.WriteAllText(mimeApps, "[Default Applications]\nx-scheme-handler/vintagestorymodinstall=Vintagestory_url_mod.desktop\n");

        var writer = new ShortcutWriter(probe);
        IReadOnlyList<string> created = writer.Create(installDir, launcher, ShortcutKinds.Menu | ShortcutKinds.UriHandler);
        Assert.Equal("optimum.desktop", writer.QueryDefaultUriHandler());

        // User switches their default handler to custom-mod-manager.desktop after installing Optimum.
        File.WriteAllText(mimeApps, "[Default Applications]\nx-scheme-handler/vintagestorymodinstall=custom-mod-manager.desktop\n");

        // Uninstalling Optimum must NOT overwrite custom-mod-manager.desktop with Vintagestory_url_mod.desktop.
        writer.Remove(created, removeUriAssociation: true, previousUriHandler: "Vintagestory_url_mod.desktop");
        Assert.Equal("custom-mod-manager.desktop", writer.QueryDefaultUriHandler());
    }

    [Fact]
    public void ExplicitUriHandlerSelectionRespectsAndRestoresExistingDefaultHandler()
    {
        FakeSystemProbe probe = LinuxProbe();
        string home = _home.Replace('\\', '/');
        string installDir = $"{home}/games/optimum";
        string launcher = $"{installDir}/optimum-launch.sh";
        string appsDir = $"{home}/.local/share/applications";
        string mimeApps = $"{home}/.config/mimeapps.list";
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(appsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(mimeApps)!);

        // Simulate an existing vanilla Vintage Story URI handler choice.
        File.WriteAllText($"{appsDir}/Vintagestory_url_mod.desktop", "[Desktop Entry]\nName=VS\n");
        File.WriteAllText(mimeApps, "[Default Applications]\nx-scheme-handler/vintagestorymodinstall=Vintagestory_url_mod.desktop\n");

        var writer = new ShortcutWriter(probe);
        Assert.Equal("Vintagestory_url_mod.desktop", writer.QueryDefaultUriHandler());

        // Menu shortcut alone must NOT overwrite the existing default handler.
        IReadOnlyList<string> menuOnly = writer.Create(installDir, launcher, ShortcutKinds.Menu);
        Assert.Equal("Vintagestory_url_mod.desktop", writer.QueryDefaultUriHandler());

        // Explicit UriHandler selection sets optimum.desktop as default.
        IReadOnlyList<string> withUri = writer.Create(installDir, launcher, ShortcutKinds.Menu | ShortcutKinds.UriHandler);
        Assert.Equal("optimum.desktop", writer.QueryDefaultUriHandler());

        // Removing with previousUriHandler restores Vintagestory_url_mod.desktop.
        writer.Remove(withUri, removeUriAssociation: true, previousUriHandler: "Vintagestory_url_mod.desktop");
        Assert.Equal("Vintagestory_url_mod.desktop", writer.QueryDefaultUriHandler());
        _ = menuOnly;
    }

    [Fact]
    public void UriHandlerWithoutMenuShortcutWritesHiddenDesktopEntryAndCleansUpOnRemove()
    {
        FakeSystemProbe probe = LinuxProbe();
        string home = _home.Replace('\\', '/');
        string installDir = $"{home}/games/optimum";
        string launcher = $"{installDir}/optimum-launch.sh";
        Directory.CreateDirectory(installDir);

        var writer = new ShortcutWriter(probe);
        IReadOnlyList<string> created = writer.Create(installDir, launcher, ShortcutKinds.UriHandler);

        string menuEntry = Assert.Single(created);
        string content = File.ReadAllText(menuEntry);
        Assert.Contains("NoDisplay=true", content);
        Assert.Contains("MimeType=x-scheme-handler/vintagestorymodinstall;", content);
        Assert.Equal("optimum.desktop", writer.QueryDefaultUriHandler());

        writer.Remove(created, removeUriAssociation: true, previousUriHandler: null);
        Assert.False(File.Exists(menuEntry));
        Assert.Null(writer.QueryDefaultUriHandler());
    }

    [Fact]
    public void NoneWritesNothing()
    {
        Assert.Empty(new ShortcutWriter(LinuxProbe()).Create("/x", "/x/launch", ShortcutKinds.None));
    }

    [Fact]
    public void CopiesTheHicolorIconWhenThePackageHasOne()
    {
        FakeSystemProbe probe = LinuxProbe();
        string installDir = Path.Combine(_home, "games", "optimum");
        Directory.CreateDirectory(Path.Combine(installDir, "assets"));
        File.WriteAllText(Path.Combine(installDir, "assets", "gameicon.png"), "PNG");

        new ShortcutWriter(probe).Create(installDir, Path.Combine(installDir, "optimum-launch.sh"), ShortcutKinds.Menu);

        Assert.True(File.Exists(Path.Combine(_home, ".local", "share", "icons", "hicolor", "256x256", "apps", "optimum.png")));
    }
}
