using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class InstallerReleaseCoverageTests
{
    [Fact]
    public void ReleaseVersionSurfacesMatch()
    {
        string version = Read("VERSION").Trim();
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        string readme = Read("README.md");

        Assert.False(string.IsNullOrWhiteSpace(version), "VERSION file is empty");
        Assert.Equal(version, Match(config, "public const string Version = \"([^\"]+)\""));
        Assert.Contains($"Optimum-v{version}-linux-x64.AppImage", readme);
    }

    [Fact]
    public void GameAssemblyIdentityMatchesTheOwnedVanillaVersion()
    {
        Assert.Equal("1.22.5", GameVersion.OverallVersion);
        Assert.Equal("1.22.5", GameVersion.ShortGameVersion);
        Assert.Equal("1.22.6", GameVersion.NetworkVersion);
        Assert.Equal(new Version(1, 22, 5, 0), typeof(GameVersion).Assembly.GetName().Version);
    }

    [Fact]
    public void InstallerAndBootstrapsShareIlspycmdCompatibilityFile()
    {
        using JsonDocument document = JsonDocument.Parse(Read(".config/ilspycmd-compat.json"));
        JsonElement minimum = document.RootElement.GetProperty("minimumVersion");
        JsonElement maximum = document.RootElement.GetProperty("maximumVersion");

        Assert.Equal("10.1.0.8386", minimum.GetString());
        Assert.Equal("10.1.1.8388", maximum.GetString());
        Assert.Contains(".config/ilspycmd-compat.json", Read("scripts/install-linux.sh"));
        Assert.Contains(".config/ilspycmd-compat.json", Read("scripts/bootstrap.sh"));
        Assert.Contains(".config/ilspycmd-compat.json", Read("scripts/bootstrap.ps1"));
        Assert.Contains(".config\\ilspycmd-compat.json", Read("scripts/install-windows.ps1"));
        Assert.Contains("Get-Accepted-ILSpyVersionRange", Read("scripts/install-windows.ps1"));
        Assert.Contains("Get-IlspycmdVersionRange", Read("scripts/bootstrap.ps1"));
        Assert.Contains("minimumVersion", Read("scripts/install-windows.ps1"));
        Assert.Contains("maximumVersion", Read("scripts/install-windows.ps1"));
        Assert.Contains("Accepted range:", Read("scripts/install-windows.ps1"));
        Assert.DoesNotContain("$actual -ne $required", Read("scripts/install-windows.ps1"));
        Assert.Contains("^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$", Read("scripts/install-linux.sh"));
        Assert.Contains("^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$", Read("scripts/bootstrap.sh"));
        Assert.Contains("^\\d+\\.\\d+\\.\\d+\\.\\d+$", Read("scripts/bootstrap.ps1"));
        Assert.Contains("ilspycmd_version_at_least", Read("scripts/install-linux.sh"));
        Assert.Contains("ilspycmd_version_at_most", Read("scripts/install-linux.sh"));
        Assert.DoesNotContain("acceptedVersions", Read("scripts/install-linux.sh"));
        Assert.DoesNotContain("acceptedPrefixes", Read("scripts/install-linux.sh"));
        Assert.DoesNotContain("Get-AcceptedIlspycmdPrefixes", Read("scripts/bootstrap.ps1"));
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

        // The test script mocks a world without dotnet 10 by sandboxing PATH/HOME.
        // When the host system has .NET 10 at a hardcoded candidate path (like
        // /usr/lib/dotnet/dotnet), the mock can't isolate it and the test becomes
        // an integration test that makes real network calls. Skip in that case.
        string? sdks = null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/lib/dotnet/dotnet", "--list-sdks")
            { RedirectStandardOutput = true, UseShellExecute = false });
            sdks = p?.StandardOutput.ReadToEnd();
            p?.WaitForExit();
        }
        catch { }
        if (sdks != null && sdks.Contains("10."))
        {
            return; // System dotnet 10 leaks into the sandbox; skip.
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

    [Theory]
    [InlineData("scripts/package-linux.sh")]
    [InlineData("scripts/package-macos.sh")]
    public void BashPackagesShipExactPatchedDllPdbPairs(string relativePath)
    {
        string script = Read(relativePath);

        // The patcher produces VintagestoryLib-patched.dll from the vanilla copy
        Assert.Contains("VintagestoryLib-patched.dll", script);
        Assert.Contains("VintagestoryLib.vanilla.dll", script);
        // Patched lib ships as VintagestoryLib.dll in the stage directory
        Assert.Contains("VintagestoryLib.dll", script);
        // API and mod DLLs ship from the build output
        Assert.Contains("VintagestoryAPI.dll", script);
        Assert.Contains("VSEssentials.dll", script);
        Assert.Contains("VSSurvivalMod.dll", script);
        Assert.Contains("Optimum.Api.Contracts.dll", script);
    }

    [Theory]
    [InlineData("scripts/package-macos.ps1")]
    public void PowerShellPackagesShipExactPatchedDllPdbPairs(string relativePath)
    {
        string script = Read(relativePath);

        Assert.Contains("VintagestoryLib-patched.dll", script);
        Assert.Contains("VintagestoryLib.vanilla.dll", script);
        Assert.Contains("VintagestoryLib.dll", script);
        Assert.Contains("VintagestoryAPI.dll", script);
        Assert.Contains("VSEssentials.dll", script);
        Assert.Contains("VSSurvivalMod.dll", script);
    }

    [Fact]
    public void WindowsInstallerRequiresAnExistingVintageStoryInstallation()
    {
        string installer = Read("scripts/install-windows.ps1");
        string package = Read("scripts/package.ps1");

        Assert.Contains("Install Vintage Story $requiredVer before Optimum", installer);
        Assert.Contains("-VanillaDir $VsPath", installer);
        Assert.DoesNotContain("DownloadVs", installer);
        Assert.DoesNotContain("cdn.vintagestory", installer);
        Assert.DoesNotContain("cdn.vintagestory", package);
        Assert.DoesNotContain("innoextract", package);
    }

    [Fact]
    public void WindowsInstallerCapturesPackageSuccessBeforeReadingExitCode()
    {
        string installer = Read("scripts/install-windows.ps1");
        int successIndex = installer.IndexOf("$packageSucceeded = $?", StringComparison.Ordinal);
        int exitCodeIndex = installer.IndexOf("$packageExitCode = $LASTEXITCODE", StringComparison.Ordinal);

        Assert.True(successIndex >= 0);
        Assert.True(exitCodeIndex > successIndex);
    }

    [Fact]
    public void CacheValidationRequiresEveryPatchedAssembly()
    {
        string cacheManager = Read("Optimum.Launcher/CacheManager.cs");
        string program = Read("Optimum.Launcher/Program.cs");

        Assert.Contains("IReadOnlyCollection<string>? requiredAssemblies", cacheManager);
        Assert.Contains("requiredAssembly", cacheManager);
        Assert.Contains("ValidateCache(requiredAssemblies)", program);
        Assert.Contains("ValidatePatchedRuntime", program);
    }

    [Fact]
    public void WindowsPackageShipsTheRuntimeLauncherAndDonors()
    {
        string package = Read("scripts/package.ps1");

        Assert.Contains("prepare-runtime-donors.ps1", package);
        Assert.Contains("Optimum.Patcher.dll", package);
        Assert.Contains("VintagestoryLib.Donor.dll", package);
        Assert.Contains("VSEssentials.Donor.dll", package);
        Assert.Contains("VSSurvivalMod.Donor.dll", package);
        Assert.Contains("Vintagestory.exe", package);
        Assert.Contains("package-complete", package);
    }

    [Fact]
    public void RuntimeDonorScriptUsesTheCapturedNativeCommandHelper()
    {
        string script = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains(". \"$scriptDir/_exec.ps1\"", script);
        Assert.Contains("Invoke-NativeStep { & $Command }", script);
    }

    [Fact]
    public void RuntimePatchesHaveASeparateExactDonorPipeline()
    {
        string bootstrap = Read("scripts/bootstrap.sh");
        string extractor = Read("scripts/extract-patches.sh");
        string checker = Read("scripts/check-patches.sh");

        Assert.Contains("-not -path '*/runtime/*'", bootstrap);
        Assert.Contains("validate-patch-syntax.sh", bootstrap);
        Assert.Contains("cp -a \"$patches_dir/runtime/.\"", extractor);
        Assert.Contains("validate-patch-syntax.sh", extractor);
        Assert.Contains("prepare-runtime-donors.sh", checker);
        Assert.Contains("Test-PatchSyntax", Read("scripts/bootstrap.ps1"));
    }

    [Fact]
    public void PatchSyntaxValidatorRejectsMissingFileHeaders()
    {
        string validator = PatchReader.FindRepositoryFile("scripts/validate-patch-syntax.sh");
        DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory("optimum-patch-syntax-");

        try
        {
            string patch = Path.Combine(tempDirectory.FullName, "malformed.patch");
            File.WriteAllText(patch, """
                diff --git a/VintagestoryLib/SystemRenderOITLayers.cs b/VintagestoryLib/SystemRenderOITLayers.cs
                index bba43f6..151f02b 100644
                @@ -1 +1 @@
                -old
                +new
                """);

            ProcessStartInfo startInfo = new("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(validator)!
            };
            startInfo.ArgumentList.Add(validator);
            startInfo.ArgumentList.Add(tempDirectory.FullName);
            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("missing unified-diff file header", output + error);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CommandHandbookReferencesHaveAnImplementedPageType()
    {
        string commandHandbook = PatchReader.FindRepositoryFile(
            "VSSurvivalMod/Systems/Handbook/CommandHandbook.cs");
        string pageType = Path.Combine(
            Path.GetDirectoryName(commandHandbook)!,
            "Gui",
            "GuiHandbookCommandPage.cs");

        Assert.Contains("GuiHandbookCommandPage", File.ReadAllText(commandHandbook));
        Assert.True(File.Exists(pageType), $"Missing handbook page type: {pageType}");
        Assert.Contains("class GuiHandbookCommandPage", File.ReadAllText(pageType));
    }

    [Theory]
    [InlineData("optimum-api-contracts/optimum-api-contracts.csproj")]
    [InlineData("optimum-game-content/optimum-game-content.csproj")]
    public void OptimumOwnedRuntimeAssembliesEmitPortableSymbols(string relativePath)
    {
        string project = Read(relativePath);

        Assert.Contains("<DebugType>portable</DebugType>", project);
        Assert.Contains("<DebugSymbols>true</DebugSymbols>", project);
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
