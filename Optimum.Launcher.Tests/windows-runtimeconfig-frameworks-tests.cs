using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Optimum.Launcher.Tests;

/// <summary>
/// The launcher is the host process for the whole game (AssemblyLoader loads the
/// vanilla assemblies into it), so Optimum.runtimeconfig.json decides which shared
/// frameworks the game can see. The csproj produces a config that names only
/// Microsoft.NETCore.App. Vanilla's Windows client also names the Windows Desktop
/// framework, which is where PresentationFramework (Eto.Wpf, used by
/// XPlatImpl.ShowMessageBox) lives. Without it a startup dialog throws
/// FileNotFoundException for PresentationFramework 6.0.2.0 and hides the real error.
///
/// scripts/sync-runtimeconfig-frameworks.ps1 copies every framework reference that
/// vanilla's Vintagestory.runtimeconfig.json declares into the staged launcher
/// config, and scripts/package.ps1 runs it after staging the launcher. These tests
/// drive the script through pwsh and, on Windows, Windows PowerShell 5.1 with
/// synthetic configs; they cannot prove that a real Windows install resolves
/// PresentationFramework.
/// </summary>
public class WindowsRuntimeConfigFrameworksTests
{
    private const string ScriptPath = "scripts/sync-runtimeconfig-frameworks.ps1";

    private const string LauncherConfig = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
            "configProperties": {
              "System.Reflection.Metadata.MetadataUpdater.IsSupported": false
            }
          }
        }
        """;

    private const string VanillaWithDesktop = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "frameworks": [
              { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
              { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
            ],
            "configProperties": { "System.Runtime.TieredPGO": true }
          }
        }
        """;

    private const string VanillaCoreOnly = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
          }
        }
        """;

    [PwshFact]
    public void CopiesWindowsDesktopFrameworkFromVanillaConfig()
    {
        string result = Run(LauncherConfig, VanillaWithDesktop);

        JsonObject options = JsonNode.Parse(result)!["runtimeOptions"]!.AsObject();
        Assert.False(options.ContainsKey("framework"));
        string[] names = options["frameworks"]!.AsArray()
            .Select(f => f!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" }, names);
        Assert.Equal("10.0.0", options["frameworks"]![1]!["version"]!.GetValue<string>());
    }

    [PwshFact]
    public void KeepsLauncherConfigProperties()
    {
        string result = Run(LauncherConfig, VanillaWithDesktop);

        JsonObject props = JsonNode.Parse(result)!["runtimeOptions"]!["configProperties"]!.AsObject();
        Assert.False(props["System.Reflection.Metadata.MetadataUpdater.IsSupported"]!.GetValue<bool>());
        Assert.False(props.ContainsKey("System.Runtime.TieredPGO"));
    }

    [PwshFact]
    public void LeavesFrameworksAloneWhenVanillaNamesOnlyTheCoreRuntime()
    {
        string result = Run(LauncherConfig, VanillaCoreOnly);

        JsonObject options = JsonNode.Parse(result)!["runtimeOptions"]!.AsObject();
        string[] names = options.ContainsKey("frameworks")
            ? options["frameworks"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToArray()
            : new[] { options["framework"]!["name"]!.GetValue<string>() };
        Assert.Equal(new[] { "Microsoft.NETCore.App" }, names);
    }

    [PwshFact]
    public void IsIdempotent()
    {
        string once = Run(LauncherConfig, VanillaWithDesktop);
        string twice = Run(once, VanillaWithDesktop);

        Assert.Equal(
            JsonNode.Parse(once)!.ToJsonString(),
            JsonNode.Parse(twice)!.ToJsonString());
    }

    [WindowsPowerShell51Fact]
    public void WindowsPowerShell51CanMergeRuntimeFrameworks()
    {
        string result = Run(LauncherConfig, VanillaWithDesktop, WindowsPowerShell51Path());

        JsonObject options = JsonNode.Parse(result)!["runtimeOptions"]!.AsObject();
        string[] names = options["frameworks"]!.AsArray()
            .Select(f => f!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" }, names);
    }

    [Fact]
    public void WindowsPackagingScriptRunsTheSyncAfterStagingTheLauncherConfig()
    {
        string script = File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "scripts/package.ps1"));

        int stageLauncher = script.IndexOf("foreach ($launcherFile in @('Optimum.exe', 'Optimum.dll'", StringComparison.Ordinal);
        int sync = script.IndexOf("sync-runtimeconfig-frameworks.ps1", StringComparison.Ordinal);

        Assert.True(stageLauncher >= 0, "launcher staging loop not found");
        Assert.True(sync > stageLauncher, "package.ps1 must run sync-runtimeconfig-frameworks.ps1 after staging the launcher files");
        Assert.Contains("Vintagestory.runtimeconfig.json", script[sync..Math.Min(script.Length, sync + 400)]);
    }

    private static string Run(string launcherJson, string vanillaJson, string executable = "pwsh")
    {
        string dir = Directory.CreateTempSubdirectory("optimum-rtcfg-").FullName;
        try
        {
            string launcher = Path.Combine(dir, "Optimum.runtimeconfig.json");
            string vanilla = Path.Combine(dir, "Vintagestory.runtimeconfig.json");
            File.WriteAllText(launcher, launcherJson);
            File.WriteAllText(vanilla, vanillaJson);

            string script = Path.Combine(FindRepositoryRoot().FullName, ScriptPath);
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script,
                "-LauncherConfig", launcher, "-VanillaConfig", vanilla })
                psi.ArgumentList.Add(arg);

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"pwsh exit {p.ExitCode}: {stdout}{stderr}");
            return File.ReadAllText(launcher);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string WindowsPowerShell51Path() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx")))
                return directory;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root with VintageStory.slnx not found.");
    }
}

/// <summary>Runs the helper with the in-box Windows PowerShell 5.1 executable.</summary>
public sealed class WindowsPowerShell51FactAttribute : FactAttribute
{
    public WindowsPowerShell51FactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows PowerShell 5.1 is available only on Windows";
            return;
        }

        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable))
            Skip = "Windows PowerShell 5.1 is not installed";
    }
}

/// <summary>Skips the test, with a reason in the run output, when pwsh is not on PATH.</summary>
public sealed class PwshFactAttribute : FactAttribute
{
    public PwshFactAttribute()
    {
        if (!PwshAvailable())
            Skip = "pwsh is not installed on this host";
    }

    private static bool PwshAvailable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return false;
        string[] names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" };
        return path.Split(Path.PathSeparator).Any(dir => names.Any(n => File.Exists(Path.Combine(dir, n))));
    }
}
