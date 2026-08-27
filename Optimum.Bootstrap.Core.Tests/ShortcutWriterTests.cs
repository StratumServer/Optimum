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
        var probe = new FakeSystemProbe { Os = OsKind.Linux, HomeDirectory = _home };
        probe.Environment["XDG_DATA_HOME"] = Path.Combine(_home, ".local", "share");
        return probe;
    }

    [Fact]
    public void WritesAndRemovesTheLinuxMenuAndDesktopEntries()
    {
        FakeSystemProbe probe = LinuxProbe();
        string installDir = Path.Combine(_home, "games", "optimum");
        string launcher = Path.Combine(installDir, "optimum-launch.sh");
        Directory.CreateDirectory(installDir);

        var writer = new ShortcutWriter(probe);
        IReadOnlyList<string> created = writer.Create(installDir, launcher, ShortcutKinds.Menu | ShortcutKinds.Desktop);

        string menuEntry = Path.Combine(_home, ".local", "share", "applications", "optimum.desktop");
        string desktopEntry = Path.Combine(_home, "Desktop", "Optimum.desktop");
        Assert.Contains(menuEntry, created);
        Assert.Contains(desktopEntry, created);
        Assert.True(File.Exists(menuEntry));
        Assert.Contains($"Exec=\"{launcher}\"", File.ReadAllText(menuEntry));

        writer.Remove(created);
        Assert.False(File.Exists(menuEntry));
        Assert.False(File.Exists(desktopEntry));
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
