using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

public class InstallerReleaseCoverageTests
{
    [Fact]
    public void ReleaseVersionSurfacesMatch()
    {
        string version = Read("VERSION").Trim();
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        string project = Read("build/Vintagestory/Vintagestory.csproj");
        string readme = Read("README.md");

        Assert.Equal("0.2.11", version);
        Assert.Equal(version, Match(config, "public const string Version = \"([^\"]+)\""));
        Assert.Equal(version, Match(project, "<Version>([^<]+)</Version>"));
        Assert.Contains($"Optimum-v{version}-linux-x64.AppImage", readme);
        Assert.DoesNotContain("Optimum-v0.2.8", readme);
        Assert.DoesNotContain("Optimum-v0.2.9", readme);
    }

    [Fact]
    public void InstallerAndBootstrapsShareIlspycmdCompatibilityFile()
    {
        using JsonDocument document = JsonDocument.Parse(Read(".config/ilspycmd-compat.json"));
        JsonElement prefixes = document.RootElement.GetProperty("acceptedPrefixes");

        Assert.Equal("10.1.0.", prefixes[0].GetString());
        Assert.Equal("10.1.1.", prefixes[1].GetString());
        Assert.Contains(".config/ilspycmd-compat.json", Read("scripts/install-linux.sh"));
        Assert.Contains(".config/ilspycmd-compat.json", Read("scripts/bootstrap.sh"));
        Assert.Contains(".config/ilspycmd-compat.json", Read("scripts/bootstrap.ps1"));
    }

    [Fact]
    public void LinuxInstallerUsesDiscoveredDotnetForIlspycmd()
    {
        string installer = Read("scripts/install-linux.sh");

        Assert.Contains("\"$DOTNET_BIN\" tool update -g ilspycmd", installer);
        Assert.Contains("local order=(git curl python3 perl tar dotnet ilspycmd)", installer);
        Assert.Contains("if [[ \"${BASH_SOURCE[0]}\" == \"$0\" ]]", installer);
    }

    [Fact]
    public void LinuxPrerequisiteShellTestsPass()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string script = PatchReader.FindRepositoryFile("scripts/tests/install-linux-prerequisites.sh");
        using Process process = Process.Start(new ProcessStartInfo("bash", script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static string Match(string source, string pattern)
    {
        System.Text.RegularExpressions.Match match = Regex.Match(source, pattern);
        Assert.True(match.Success, $"Pattern not found: {pattern}");
        return match.Groups[1].Value;
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
