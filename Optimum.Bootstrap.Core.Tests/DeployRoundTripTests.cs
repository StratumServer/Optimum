using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>
/// PackageDeployer and Uninstaller do real filesystem work, so these run against
/// temp directories with a real <see cref="SystemProbe"/>.
/// </summary>
public sealed class DeployRoundTripTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimum-deploy-test").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string StagePackage()
    {
        string package = Path.Combine(_root, "staged", "Optimum-v0.3.14-linux-x64");
        Directory.CreateDirectory(Path.Combine(package, ".optimum"));
        Directory.CreateDirectory(Path.Combine(package, "assets"));
        File.WriteAllText(Path.Combine(package, "run.sh"), "#!/bin/sh\nexec ./Optimum\n");
        File.WriteAllText(Path.Combine(package, "Optimum"), "binary");
        File.WriteAllText(Path.Combine(package, "assets", "gameicon.png"), "png");
        File.WriteAllText(Path.Combine(package, ".optimum", "version"), "0.3.14");
        return package;
    }

    [Fact]
    public void DeployThenUninstallLeavesNothingBehind()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");
        string dataPath = Path.Combine(_root, "data");

        DeployResult deploy = new PackageDeployer(probe).Deploy(
            new DeployRequest(package, installDir, dataPath));

        Assert.True(deploy.Ok, deploy.Message);
        Assert.True(File.Exists(Path.Combine(installDir, "run.sh")));
        Assert.True(File.Exists(Path.Combine(installDir, "assets", "gameicon.png")));
        Assert.True(File.Exists(Path.Combine(installDir, InstallManifest.RelativePath)));
        Assert.Equal(dataPath, File.ReadAllText(Path.Combine(installDir, "datapath.cfg")));

        string launcherName = probe.Os == OsKind.Windows ? "optimum-launch.cmd" : "optimum-launch.sh";
        Assert.True(File.Exists(Path.Combine(installDir, launcherName)));

        InstallManifest manifest = InstallManifest.Deserialize(
            File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)))!;
        Assert.Equal("0.3.14", manifest.OptimumVersion);

        UninstallResult uninstall = new Uninstaller(probe).Uninstall(installDir);

        Assert.True(uninstall.Ok);
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public void DeployRefusesANonEmptyDirectoryWithNoManifest()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string occupied = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "someone-elses-file"), "x");

        DeployResult result = new PackageDeployer(probe).Deploy(new DeployRequest(package, occupied));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.OutputExists, result.Reason);
        Assert.True(File.Exists(Path.Combine(occupied, "someone-elses-file")));
    }

    [Fact]
    public void DeployCleansANonEmptyDirectoryWhenCleanDestinationIsTrue()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string occupied = Path.Combine(_root, "occupied-clean");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "someone-elses-file"), "x");

        DeployResult result = new PackageDeployer(probe).Deploy(new DeployRequest(package, occupied, CleanDestination: true));

        Assert.True(result.Ok);
        Assert.False(File.Exists(Path.Combine(occupied, "someone-elses-file")));
        Assert.True(File.Exists(Path.Combine(occupied, "run.sh")));
    }

    [Fact]
    public void DeployReplacesAnExistingOptimumInstallInPlace()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");

        Assert.True(new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir)).Ok);
        File.WriteAllText(Path.Combine(installDir, "stale-from-old-install"), "old");

        Assert.True(new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir)).Ok);

        Assert.False(File.Exists(Path.Combine(installDir, "stale-from-old-install")));
        Assert.True(File.Exists(Path.Combine(installDir, "run.sh")));
        // No stage or backup directories left behind.
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(installDir)!, ".optimum-*"));
    }

    [Fact]
    public void AFailureDuringTheSwapRollsBackToThePreviousInstall()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");

        Assert.True(new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir)).Ok);
        string manifestBefore = File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath));
        File.WriteAllText(Path.Combine(installDir, "user-added-mod"), "keep across a failed reinstall");

        var deployer = new PackageDeployer(probe)
        {
            FailAtStep = step => { if (step == "swap") throw new IOException("simulated swap failure"); },
        };
        DeployResult result = deployer.Deploy(new DeployRequest(package, installDir));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.EngineInternal, result.Reason);
        Assert.Contains("rolled back", result.Message);
        // The previous install is intact, including the user's file.
        Assert.True(File.Exists(Path.Combine(installDir, "user-added-mod")));
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(installDir)!, ".optimum-*"));
    }

    [Fact]
    public void AFailureAfterTheSwapKeepsTheNewInstall()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");

        Assert.True(new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir)).Ok);
        File.WriteAllText(Path.Combine(installDir, "from-the-old-install"), "old");

        var deployer = new PackageDeployer(probe)
        {
            FailAtStep = step => { if (step == "commit") throw new IOException("simulated post-swap failure"); },
        };
        DeployResult result = deployer.Deploy(new DeployRequest(package, installDir));

        // Past the swap the install is complete; a cleanup failure is not fatal.
        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(Path.Combine(installDir, "from-the-old-install")));
        Assert.True(File.Exists(Path.Combine(installDir, "run.sh")));
        Assert.True(File.Exists(Path.Combine(installDir, InstallManifest.RelativePath)));
    }

    [Fact]
    public void AFailureBeforeTheSwapOnAFreshInstallLeavesNothing()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");

        var deployer = new PackageDeployer(probe)
        {
            FailAtStep = step => { if (step == "backup") throw new IOException("simulated early failure"); },
        };
        DeployResult result = deployer.Deploy(new DeployRequest(package, installDir));

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(installDir));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "install"), ".optimum-*"));
    }

    [Fact]
    public void ManifestRecordsTheVersionFromThePackageDirectoryName()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");

        Assert.True(new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir)).Ok);

        InstallManifest manifest = InstallManifest.Deserialize(
            File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)))!;
        Assert.Equal("0.3.14", manifest.OptimumVersion);
    }

    [Fact]
    public void UninstallSkipsAManifestEntryThatEscapesTheInstallDirectory()
    {
        var probe = SystemProbe.Default;
        string installDir = Path.Combine(_root, "install", "optimum");
        Directory.CreateDirectory(Path.Combine(installDir, ".optimum"));
        string outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "do not touch");

        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.14",
            InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = installDir,
            Entries = ["../outside.txt", "run.sh"],
        };
        File.WriteAllText(Path.Combine(installDir, InstallManifest.RelativePath), manifest.Serialize());
        File.WriteAllText(Path.Combine(installDir, "run.sh"), "x");

        UninstallResult result = new Uninstaller(probe).Uninstall(installDir);

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void MenuShortcutIsRecordedInTheManifestAndRemovedByUninstall()
    {
        if (OperatingSystem.IsWindows())
            return; // this path exercises the Linux .desktop writer

        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");
        string dataHome = Path.Combine(_root, "xdg-data");
        string configHome = Path.Combine(_root, "xdg-config");
        string? previousData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        try
        {
            Assert.True(new PackageDeployer(probe).Deploy(
                new DeployRequest(package, installDir, DataPath: null, ShortcutKinds.Menu)).Ok);

            string entry = Path.Combine(dataHome, "applications", "optimum.desktop");
            Assert.True(File.Exists(entry));

            InstallManifest manifest = InstallManifest.Deserialize(
                File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)))!;
            Assert.Contains(entry, manifest.Shortcuts);

            Assert.True(new Uninstaller(probe).Uninstall(installDir).Ok);
            Assert.False(File.Exists(entry));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousData);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
        }
    }

    [Fact]
    public void LinuxLauncherHandlesModInstallUriPreservesArgsAndRejectsMalformedInputs()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var probe = SystemProbe.Default;
        string package = StagePackage();
        File.WriteAllText(
            Path.Combine(package, "run.sh"),
            "#!/usr/bin/env bash\nset -euo pipefail\nfor a in \"$@\"; do printf '<%s>' \"$a\"; done\nprintf '\\n'\n");

        string installDir = Path.Combine(_root, "Install Dir With Spaces", "Optimum Client");
        string dataPath = Path.Combine(_root, "Custom Data Path", "VintageStory Data");

        DeployResult deploy = new PackageDeployer(probe).Deploy(
            new DeployRequest(package, installDir, dataPath));
        Assert.True(deploy.Ok, deploy.Message);

        string launcher = Path.Combine(installDir, "optimum-launch.sh");
        Assert.True(File.Exists(launcher));

        // 1. Valid ModDB URI -> passes configured --dataPath and -i <uri> as a single argument.
        ProcessOutcome validUri = probe.Run(
            launcher,
            ["vintagestorymodinstall://carrycapacity@1.8.0"],
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, validUri.ExitCode);
        Assert.Equal(
            $"<--dataPath><{dataPath}><-i><vintagestorymodinstall://carrycapacity@1.8.0>",
            validUri.StandardOutput.Trim());

        // 2. Uppercase scheme and trailing slash are normalized cleanly.
        ProcessOutcome trailingSlash = probe.Run(
            launcher,
            ["VINTAGESTORYMODINSTALL://primitivesurvival@3.7.5/"],
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, trailingSlash.ExitCode);
        Assert.Equal(
            $"<--dataPath><{dataPath}><-i><vintagestorymodinstall://primitivesurvival@3.7.5>",
            trailingSlash.StandardOutput.Trim());

        // 3. Ordinary launch arguments are preserved alongside configured --dataPath.
        ProcessOutcome ordinaryArgs = probe.Run(
            launcher,
            ["--openWorld", "My World", "-p", "creativebuilding"],
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, ordinaryArgs.ExitCode);
        Assert.Equal(
            $"<--dataPath><{dataPath}><--openWorld><My World><-p><creativebuilding>",
            ordinaryArgs.StandardOutput.Trim());

        // 4. Malformed URIs and injection attempts are rejected without invoking run.sh.
        string sentinel = Path.Combine(_root, "pwned");
        string[][] malformedInputs =
        [
            ["http://mods.vintagestory.at/carrycapacity"],
            ["vintagestorymodinstall://"],
            ["vintagestorymodinstall:///"],
            ["vintagestorymodinstall://-c"],
            ["vintagestorymodinstall://carrycapacity@1.8.0\n-c evil"],
            ["vintagestorymodinstall://carrycapacity@1.8.0\r-c evil"],
            ["vintagestorymodinstall://carrycapacity@1.8.0 --connect evil"],
            ["vintagestorymodinstall://carrycapacity@1.8.0", "--connect"],
            [$"vintagestorymodinstall://$(touch '{sentinel}')"],
            ["-i", "carrycapacity@1.8.0\n-c evil"],
        ];

        foreach (string[] badArgs in malformedInputs)
        {
            ProcessOutcome rejected = probe.Run(launcher, badArgs, TimeSpan.FromSeconds(5));
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Equal(string.Empty, rejected.StandardOutput.Trim());
        }
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public void ReinstallAndUninstallPreserveAndCleanUpLinuxUriHandlerAssociations()
    {
        if (OperatingSystem.IsWindows())
            return;

        var probe = SystemProbe.Default;
        string dataHome = Path.Combine(_root, "xdg-data-uri");
        string configHome = Path.Combine(_root, "xdg-config-uri");
        Directory.CreateDirectory(Path.Combine(dataHome, "applications"));
        Directory.CreateDirectory(configHome);

        // Pre-populate a vanilla Vintage Story handler choice.
        File.WriteAllText(Path.Combine(dataHome, "applications", "Vintagestory_url_mod.desktop"), "[Desktop Entry]\nName=VS\n");
        File.WriteAllText(Path.Combine(configHome, "mimeapps.list"), "[Default Applications]\nx-scheme-handler/vintagestorymodinstall=Vintagestory_url_mod.desktop\n");

        string? previousData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        try
        {
            string package = StagePackage();
            string installDir = Path.Combine(_root, "install", "optimum-uri");
            var writer = new ShortcutWriter(probe);

            // 1. Install with Menu + UriHandler -> takes over default and records previous handler.
            Assert.True(new PackageDeployer(probe).Deploy(
                new DeployRequest(package, installDir, DataPath: null, ShortcutKinds.Menu | ShortcutKinds.UriHandler)).Ok);
            Assert.Equal("optimum.desktop", writer.QueryDefaultUriHandler());

            InstallManifest firstManifest = InstallManifest.Deserialize(
                File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)))!;
            Assert.True(firstManifest.RegisteredUriHandler);
            Assert.Equal("Vintagestory_url_mod.desktop", firstManifest.PreviousUriHandler);

            // 2. Reinstall with Menu + UriHandler -> preserves original PreviousUriHandler.
            Assert.True(new PackageDeployer(probe).Deploy(
                new DeployRequest(package, installDir, DataPath: null, ShortcutKinds.Menu | ShortcutKinds.UriHandler)).Ok);
            InstallManifest secondManifest = InstallManifest.Deserialize(
                File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)))!;
            Assert.True(secondManifest.RegisteredUriHandler);
            Assert.Equal("Vintagestory_url_mod.desktop", secondManifest.PreviousUriHandler);

            // 3. Uninstall -> restores Vintagestory_url_mod.desktop and removes optimum.desktop.
            Assert.True(new Uninstaller(probe).Uninstall(installDir).Ok);
            Assert.Equal("Vintagestory_url_mod.desktop", writer.QueryDefaultUriHandler());
            Assert.False(File.Exists(Path.Combine(dataHome, "applications", "optimum.desktop")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousData);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
        }
    }

    [Fact]
    public async Task LinuxDesktopEndToEndForwardsModInstallUriOverSingleInstanceNamedPipeAndCoexists()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var probe = SystemProbe.Default;
        string dataHome = Path.Combine(_root, "e2e-xdg-data");
        string configHome = Path.Combine(_root, "e2e-xdg-config");
        string isolatedTmp = Path.Combine(_root, "e2e-tmp");
        Directory.CreateDirectory(dataHome);
        Directory.CreateDirectory(configHome);
        Directory.CreateDirectory(isolatedTmp);

        // Build a staged package whose ./Optimum script implements the exact
        // ClientProgram + UriHandler SingleInstanceVintageStoryWithUriScheme
        // pipe client/server protocol using an isolated Unix socket in TMPDIR.
        string package = StagePackage();
        File.WriteAllText(
            Path.Combine(package, "run.sh"),
            "#!/usr/bin/env bash\nset -euo pipefail\nexec ./Optimum \"$@\"\n");
        File.WriteAllText(
            Path.Combine(package, "Optimum"),
            """
            #!/usr/bin/env python3
            import os, socket, sys

            raw_args = sys.argv[1:]
            data_path = os.environ.get("DEFAULT_VS_DATA", "/default/data")
            install_mod = None
            i = 0
            while i < len(raw_args):
                arg = raw_args[i]
                if arg in ("-d", "--dataPath") and i + 1 < len(raw_args):
                    data_path = raw_args[i + 1]
                    i += 2
                elif arg.startswith("--dataPath="):
                    data_path = arg.split("=", 1)[1]
                    i += 1
                elif arg in ("-i", "--installmod") and i + 1 < len(raw_args):
                    install_mod = raw_args[i + 1]
                    i += 2
                elif arg.startswith("-i=") or arg.startswith("--installmod="):
                    install_mod = arg.split("=", 1)[1]
                    i += 1
                else:
                    i += 1

            if install_mod is not None:
                install_mod = install_mod.replace("vintagestorymodinstall://", "")

            sock_path = os.path.join(os.environ["TMPDIR"], "SingleInstanceVintageStoryWithUriScheme.sock")
            if os.path.exists(sock_path):
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                    client.connect(sock_path)
                    if install_mod is not None:
                        client.sendall(f"-i {install_mod}\n".encode("utf-8"))
                        sys.exit(0)
            # No existing instance listening (or no URI arg): Cold-start instance
            ColdLog = os.environ["COLD_START_LOG"]
            with open(ColdLog, "a", encoding="utf-8") as fp:
                fp.write(f"COLD_START|dataPath={data_path}|installMod={install_mod}\n")
            """);

        string installDirA = Path.Combine(_root, "Optimum Install A", "Client");
        string dataPathA = Path.Combine(_root, "Data Path A");
        string coldLog = Path.Combine(_root, "cold-start.log");

        string? prevData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? prevConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? prevTmp = Environment.GetEnvironmentVariable("TMPDIR");
        string? prevCold = Environment.GetEnvironmentVariable("COLD_START_LOG");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Environment.SetEnvironmentVariable("TMPDIR", isolatedTmp);
        Environment.SetEnvironmentVariable("COLD_START_LOG", coldLog);

        try
        {
            Assert.True(new PackageDeployer(probe).Deploy(
                new DeployRequest(package, installDirA, dataPathA, ShortcutKinds.Menu | ShortcutKinds.UriHandler)).Ok);

            string desktopEntryPath = Path.Combine(dataHome, "applications", "optimum.desktop");
            Assert.True(File.Exists(desktopEntryPath));
            string desktopContent = File.ReadAllText(desktopEntryPath);
            string expectedLauncher = Path.Combine(installDirA, "optimum-launch.sh");
            Assert.Contains($"Exec=\"{expectedLauncher}\" %u", desktopContent);
            Assert.Contains("MimeType=x-scheme-handler/vintagestorymodinstall;", desktopContent);

            // Scenario 1: No game running -> invoking the desktop handler cold-starts
            // Optimum with its configured --dataPath and stripped InstallModId.
            ProcessOutcome coldOutcome = probe.Run(
                expectedLauncher,
                ["vintagestorymodinstall://carrycapacity@1.8.0"],
                TimeSpan.FromSeconds(5));
            Assert.Equal(0, coldOutcome.ExitCode);
            Assert.Equal(
                $"COLD_START|dataPath={dataPathA}|installMod=carrycapacity@1.8.0",
                File.ReadAllText(coldLog).Trim());

            // Scenario 2: Running instance (simulating a running world or vanilla/other
            // Optimum coexistence) owns SingleInstanceVintageStoryWithUriScheme.
            // Repeated requests forward over the pipe without starting a second window.
            string sockPath = Path.Combine(isolatedTmp, "SingleInstanceVintageStoryWithUriScheme.sock");
            var receivedLines = new List<string>();
            using var listener = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified);
            listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(sockPath));
            listener.Listen(5);

            Task serverTask = Task.Run(async () =>
            {
                for (int req = 0; req < 2; req++)
                {
                    using System.Net.Sockets.Socket accepted = await listener.AcceptAsync();
                    using var stream = new System.Net.Sockets.NetworkStream(accepted);
                    using var reader = new StreamReader(stream);
                    string? line = await reader.ReadLineAsync();
                    if (line is not null)
                        receivedLines.Add(line);
                }
            });

            ProcessOutcome firstForward = probe.Run(
                expectedLauncher,
                ["vintagestorymodinstall://carrycapacity@1.8.0"],
                TimeSpan.FromSeconds(5));
            ProcessOutcome secondForward = probe.Run(
                expectedLauncher,
                ["vintagestorymodinstall://primitivesurvival@3.7.5/"],
                TimeSpan.FromSeconds(5));

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, firstForward.ExitCode);
            Assert.Equal(0, secondForward.ExitCode);
            Assert.Equal(["-i carrycapacity@1.8.0", "-i primitivesurvival@3.7.5"], receivedLines);
            // Cold-start log still has only the 1 line from Scenario 1 (no second game window started).
            Assert.Single(File.ReadAllLines(coldLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", prevData);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prevConfig);
            Environment.SetEnvironmentVariable("TMPDIR", prevTmp);
            Environment.SetEnvironmentVariable("COLD_START_LOG", prevCold);
        }
    }

    [Fact]
    public void DeployRejectsAnUnsafeInstallPath()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();

        DeployResult deploy = new PackageDeployer(probe).Deploy(
            new DeployRequest(package, probe.HomeDirectory));

        Assert.False(deploy.Ok);
        Assert.Equal(FailureReason.BadInput, deploy.Reason);
    }

    [Fact]
    public void UninstallStillRemovesShortcutsWhenAListedEntryIsAlreadyGone()
    {
        var probe = SystemProbe.Default;
        string installDir = Path.Combine(_root, "install", "optimum");
        Directory.CreateDirectory(Path.Combine(installDir, ".optimum"));
        string shortcut = Path.Combine(_root, "menu", "optimum.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        File.WriteAllText(shortcut, "[Desktop Entry]");

        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.14",
            InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = installDir,
            Entries = ["run.sh"],           // never created
            Shortcuts = [shortcut],
        };
        File.WriteAllText(Path.Combine(installDir, InstallManifest.RelativePath), manifest.Serialize());

        UninstallResult result = new Uninstaller(probe).Uninstall(installDir);

        Assert.True(result.Ok);
        Assert.False(File.Exists(shortcut));
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public void UninstallRefusesADirectoryWithNoManifest()
    {
        string bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "important.txt"), "keep me");

        UninstallResult result = new Uninstaller(SystemProbe.Default).Uninstall(bare);

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.True(File.Exists(Path.Combine(bare, "important.txt")));
    }
}
