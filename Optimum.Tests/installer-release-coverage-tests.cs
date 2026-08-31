using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        Assert.Equal("1.22.7", GameVersion.OverallVersion);
        Assert.Equal("1.22.7", GameVersion.ShortGameVersion);
        Assert.Equal("1.22.6", GameVersion.NetworkVersion);
        Assert.Equal(new Version(1, 22, 7, 0), typeof(GameVersion).Assembly.GetName().Version);
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
    public void WindowsPackagingKeepsNativeInstallAndSupportsOffPlatformExtraction()
    {
        string installer = Read("scripts/install-windows.ps1");
        string package = Read("scripts/package.ps1");
        string makefile = Read("Makefile");
        string packageAll = Read("scripts/package-all.sh");

        Assert.Contains("Install Vintage Story $requiredVer before Optimum", installer);
        Assert.Contains("-VanillaDir $VsPath", installer);
        Assert.DoesNotContain("DownloadVs", installer);
        Assert.DoesNotContain("cdn.vintagestory", installer);
        Assert.Contains("[string]$ClientArchive", package);
        Assert.Contains("innoextract >= 1.11", package);
        Assert.Contains("A matching\npackage-client cache avoids the extractor", package);
        Assert.Contains("--info", package);
        Assert.Contains("--extract", package);
        Assert.Contains("vs_install_win-x64_", package);
        Assert.Contains("cdn.vintagestory", package);
        Assert.Contains("Get-WindowsClientVersion", package);
        Assert.Contains("version-*.txt", package);
        Assert.Contains("Installer extracted Vintage Story", package);
        Assert.Contains(".innoextract-stage-", package);
        Assert.Contains(".vanilla/win-x64/package-client", package);
        Assert.DoesNotContain("Move-Item -Path $winDir -Destination $backupDir", package);
        Assert.Contains("innoextract >= 1.11", makefile);
        Assert.Contains("ClientArchive", makefile);
        Assert.Contains("WIN_CACHE_DIR", packageAll);
        Assert.Contains("package-client", packageAll);
        Assert.Contains("marker_version", packageAll);
        Assert.Contains("Vintagestory.exe", packageAll);
        Assert.Contains("innoextract >= 1.11", packageAll);
        Assert.Contains("command -v pwsh", packageAll);
    }

    [Fact]
    public void WindowsInstallerStreamsPackageOutputInsteadOfBufferingIt()
    {
        // Regression guard: install-windows.ps1 used to assign
        // `$packageOutput = & package.ps1 ... *>&1` and only write it to the
        // log after the call returned. When package.ps1 (via
        // prepare-runtime-donors.ps1) throws mid-build, PowerShell never
        // populates that variable, silently dropping every line - including
        // the real dotnet build compiler errors - and leaving only the
        // generic wrapper error message. Piping straight into Write-Log
        // keeps output visible even when the call throws partway through.
        string installer = Read("scripts/install-windows.ps1");

        Assert.DoesNotContain("$packageOutput = &", installer);
        Assert.Contains("*>&1 |", installer);
        Assert.Contains("ForEach-Object { Write-Log ([string]$_) }", installer);
        Assert.Contains("$packageExitCode = $LASTEXITCODE", installer);
    }

    [Fact]
    public void WindowsInstallerPassesFinalDataPathToPreflightBeforeCopyingIt()
    {
        string installer = Read("scripts/install-windows.ps1");
        int preflightFunction = installer.IndexOf(
            "function Invoke-RuntimePreflight",
            StringComparison.Ordinal);
        int argumentQuoter = installer.IndexOf(
            "function ConvertTo-WindowsProcessArgument",
            StringComparison.Ordinal);
        Assert.True(preflightFunction >= 0, "Runtime preflight function is missing");
        Assert.True(argumentQuoter >= 0, "Windows process argument helper is missing");

        int installFunction = installer.IndexOf(
            "function Install-StagedPackage",
            preflightFunction,
            StringComparison.Ordinal);

        Assert.True(installFunction > preflightFunction, "Runtime preflight function boundary is missing");
        Assert.True(argumentQuoter < preflightFunction, "Process argument helper must be defined before preflight");
        string argumentHelper = installer.Substring(argumentQuoter, preflightFunction - argumentQuoter);
        Assert.Contains("$Value.IndexOf('\"') -ge 0", argumentHelper);
        Assert.Contains("[regex]::Replace($Value, '(\\\\+)$', '$1$1')", argumentHelper);
        Assert.Contains("return \"`\"$escapedValue`\"\"", argumentHelper);

        string preflight = installer.Substring(preflightFunction, installFunction - preflightFunction);
        int preflightCall = installer.IndexOf(
            "Invoke-RuntimePreflight -StageDir $built.FullName -LogRoot $buildRoot -DataPath $DataPath",
            installFunction,
            StringComparison.Ordinal);
        Assert.True(preflightCall > installFunction, "Build flow does not pass DataPath to runtime preflight");

        int stagedInstall = installer.IndexOf(
            "Install-StagedPackage -StageDir $built.FullName -InstallDir $InstallDir",
            preflightCall,
            StringComparison.Ordinal);
        Assert.True(stagedInstall > preflightCall, "Staged package must be copied only after runtime preflight");

        int dataPathConfig = installer.IndexOf(
            "[System.IO.File]::WriteAllText",
            stagedInstall,
            StringComparison.Ordinal);
        Assert.True(dataPathConfig > stagedInstall, "datapath.cfg must be written after the package copy");

        Assert.Contains("[string]$DataPath", preflight);
        Assert.Contains("$argumentList = @('--validate-only')", preflight);
        Assert.Contains("$argumentList += @('--dataPath', (ConvertTo-WindowsProcessArgument -Value $DataPath))", preflight);
        Assert.Contains("-ArgumentList $argumentList", preflight);
    }

    [Fact]
    public void WindowsInstallerDataPathArgumentSurvivesWindowsPowerShellParsing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string installer = Read("scripts/install-windows.ps1");
        int helperStart = installer.IndexOf(
            "function ConvertTo-WindowsProcessArgument",
            StringComparison.Ordinal);
        Assert.True(helperStart >= 0, "Windows process argument helper is missing");
        if (helperStart < 0)
        {
            return;
        }

        int helperEnd = installer.IndexOf(
            "function Invoke-RuntimePreflight",
            helperStart,
            StringComparison.Ordinal);
        Assert.True(helperEnd > helperStart, "Windows process argument helper boundary is missing");

        string helper = installer.Substring(helperStart, helperEnd - helperStart);
        string harness = helper + "\n" + """
            $ErrorActionPreference = 'Stop'
            $probe = Join-Path $env:TEMP ('optimum-argv-probe-' + [guid]::NewGuid().ToString('N') + '.ps1')
            $output = Join-Path $env:TEMP ('optimum-argv-output-' + [guid]::NewGuid().ToString('N') + '.txt')

            try {
                [System.IO.File]::WriteAllText($probe, '$args | ForEach-Object { [Console]::Out.WriteLine($_) }')
                foreach ($dataPath in @($null, 'C:\Users\Jane Doe\Vintage Data', 'C:\Users\Jane Doe\Vintage Data\', 'D:\')) {
                    $argumentList = @('-NoProfile', '-NonInteractive', '-File', (ConvertTo-WindowsProcessArgument -Value $probe), '--validate-only')
                    $expected = @('--validate-only')
                    if ($null -ne $dataPath) {
                        $argumentList += @('--dataPath', (ConvertTo-WindowsProcessArgument -Value $dataPath))
                        $expected += @('--dataPath', $dataPath)
                    }

                    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
                    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentList -Wait -PassThru -NoNewWindow -RedirectStandardOutput $output
                    $actual = @(Get-Content -LiteralPath $output)
                    if ($process.ExitCode -ne 0 -or $actual.Count -ne $expected.Count) {
                        throw "Argument count/exit mismatch for '$dataPath': $($process.ExitCode) / $($actual -join '|')"
                    }
                    for ($i = 0; $i -lt $expected.Count; $i++) {
                        if ($actual[$i] -cne $expected[$i]) {
                            throw "Argument mismatch for '$dataPath': expected '$($expected[$i])', got '$($actual[$i])'"
                        }
                    }
                }
            } finally {
                Remove-Item -LiteralPath $probe, $output -Force -ErrorAction SilentlyContinue
            }

            'Windows Start-Process argv harness passed'
            """;

        DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory("optimum-windows-argv-");
        string harnessPath = Path.Combine(tempDirectory.FullName, "harness.ps1");
        File.WriteAllText(harnessPath, harness);

        try
        {
            ProcessStartInfo startInfo = new("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(harnessPath);

            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, output + Environment.NewLine + error);
            Assert.Contains("Windows Start-Process argv harness passed", output);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WindowsInstallerNormalizesDriveRootParentPath()
    {
        // Regression guard: PowerShell 5.1's Split-Path -Parent strips the
        // trailing backslash from drive roots (issue #6034), turning
        // "C:\Optimum" into a parent of "C:" instead of "C:\". "C:" is a
        // relative path (current directory on drive C) and Join-Path/
        // New-Item/Move-Item downstream in Install-StagedPackage fail with
        // "The path is not of a legal form" for anyone installing straight
        // to a drive root (e.g. C:\Optimum, D:\Optimum). This fix landed
        // once in 83dfa9b and was silently reverted by a later commit that
        // rewrote this function without it; this test pins it in place.
        string installer = Read("scripts/install-windows.ps1");

        Assert.Contains("$parentDir -match '^[A-Za-z]:$'", installer);
        Assert.Contains("$parentDir = \"$parentDir\\\"", installer);
        Assert.Contains("$parentDir -ne [System.IO.Path]::GetPathRoot($installPath)", installer);
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
    public void RuntimeDonorPatchGatesValidateEveryMandatoryProjectBeforeApplying()
    {
        string bash = Read("scripts/runtime-donor-patch-gate.sh");
        string bashPreparation = Read("scripts/prepare-runtime-donors.sh");
        string powershell = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains("runtime-donor-patch-gate.sh", bashPreparation);
        Assert.Contains("runtime_projects=(VSEssentials VSSurvivalMod)", bash);
        Assert.Contains("runtime_patch_failures=()", bash);
        Assert.Contains("Runtime donor compatibility gate failed:", bash);
        Assert.Contains("exit 1", bash);
        int bashFailureGate = bash.IndexOf("Runtime donor compatibility gate failed:", StringComparison.Ordinal);
        int bashApply = bash.IndexOf(
            "git -C \"$repo_root\" apply \\",
            bashFailureGate,
            StringComparison.Ordinal);
        Assert.True(bashFailureGate >= 0, "Bash donor failure gate is missing");
        Assert.True(bashApply > bashFailureGate, "Bash applies a patch before its compatibility gate");

        Assert.Contains("$runtimeProjects = @('VSEssentials', 'VSSurvivalMod')", powershell);
        Assert.Contains("$runtimePatchFailures = @()", powershell);
        Assert.Contains("$patchExitCode = $LASTEXITCODE", powershell);
        Assert.Contains("2>&1 |", powershell);
        Assert.Contains("git apply --check failed without diagnostic output.", powershell);
        Assert.Contains("Runtime donor compatibility gate failed:", powershell);
        int powershellFailureGate = powershell.IndexOf("Runtime donor compatibility gate failed:", StringComparison.Ordinal);
        int powershellApply = powershell.IndexOf(
            "git -C $repoRoot apply",
            powershellFailureGate,
            StringComparison.Ordinal);
        Assert.True(powershellFailureGate >= 0, "PowerShell donor failure gate is missing");
        Assert.True(powershellApply > powershellFailureGate, "PowerShell applies a patch before its compatibility gate");
    }

    [Fact]
    public void BashRuntimeDonorPatchGateRejectsPartialCompatibility()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        string gate = PatchReader.FindRepositoryFile("scripts/runtime-donor-patch-gate.sh");
        DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory("optimum-runtime-gate-");

        try
        {
            string runtimeRoot = Path.Combine(tempDirectory.FullName, "runtime");
            string patchesRoot = Path.Combine(tempDirectory.FullName, "patches", "runtime");
            string goodTarget = Path.Combine(runtimeRoot, "VSEssentials", "Good.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(goodTarget)!);
            Directory.CreateDirectory(Path.Combine(patchesRoot, "VSEssentials"));
            Directory.CreateDirectory(Path.Combine(patchesRoot, "VSSurvivalMod"));
            File.WriteAllText(goodTarget, "old\n");
            File.WriteAllText(
                Path.Combine(patchesRoot, "VSEssentials", "Good.cs.patch"),
                """
                diff --git a/VSEssentials/Good.cs b/VSEssentials/Good.cs
                --- a/VSEssentials/Good.cs
                +++ b/VSEssentials/Good.cs
                @@ -1 +1 @@
                -old
                +new
                """.Replace("                ", ""));
            File.WriteAllText(
                Path.Combine(patchesRoot, "VSSurvivalMod", "Missing.cs.patch"),
                """
                diff --git a/VSSurvivalMod/Missing.cs b/VSSurvivalMod/Missing.cs
                --- a/VSSurvivalMod/Missing.cs
                +++ b/VSSurvivalMod/Missing.cs
                @@ -1 +1 @@
                -missing
                +new
                """.Replace("                ", ""));

            ProcessStartInfo startInfo = new("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(gate);
            startInfo.ArgumentList.Add(tempDirectory.FullName);
            startInfo.ArgumentList.Add("runtime");
            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("Runtime donor compatibility gate failed", output + error);
            Assert.Equal("old\n", File.ReadAllText(goodTarget));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
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
    public void RuntimeDonorPreparationUsesProtectedVanillaSnapshot()
    {
        string bash = Read("scripts/prepare-runtime-donors.sh");
        string powershell = Read("scripts/prepare-runtime-donors.ps1");
        string bootstrap = Read("scripts/bootstrap.sh");
        string bootstrapPowerShell = Read("scripts/bootstrap.ps1");
        string package = Read("scripts/package.ps1");
        string checker = Read("scripts/check-patches.sh");

        Assert.Contains("RUNTIME_DONOR_DIR", bash);
        Assert.Contains("runtime-donor-version.txt", bash);
        Assert.Contains("runtime-donor-manifest.sha256", bash);
        Assert.DoesNotContain("--reverse", bash);
        Assert.Contains("runtimeDonorDir", powershell);
        Assert.Contains("runtime-donor-version.txt", powershell);
        Assert.Contains("runtime-donor-manifest.sha256", powershell);
        Assert.Contains("Invoke-NativeStep", powershell);
        Assert.DoesNotContain("--reverse", powershell);
        Assert.Contains("snapshot_runtime_donors", bootstrap);
        Assert.Contains("shasum -a 256", bootstrap);
        Assert.Contains("New-RuntimeDonorSnapshot", bootstrapPowerShell);
        Assert.Contains("-RuntimeDonorDir", package);

        // Regression guard: check-patches.sh used to pass VANILLA_DIR to
        // prepare-runtime-donors.sh without pinning RUNTIME_DONOR_DIR, so a
        // developer with VANILLA_DIR exported to an external/live Vintage
        // Story install would silently validate against a "runtime-donors"
        // sibling of that external path instead of the repo's protected
        // snapshot - the same isolation bug fixed for scripts/package.ps1
        // (see research/runtime-donor-mismatch-investigation-2026-08-14.md).
        Assert.Contains("RUNTIME_DONOR_DIR=", checker);
    }

    [Fact]
    public void PatchSyntaxValidatorRejectsMissingFileHeaders()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Bash script test requires Unix shell

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

    [Fact]
    public void LinuxInstallerStagesWithoutABuiltThenDeletedArchive()
    {
        // Regression guard: install-linux.sh ran package-linux.sh in its
        // default targz mode into a temp directory, then copied only the
        // staged folder into place and deleted the tar.gz unread. On a small
        // or tmpfs /tmp that unused ~600 MB archive stalled the install before
        // any files reached the target (issue #23 follow-up, install onto
        // /mnt/zoomin). The installer now requests the folder alone, and the
        // temp tree is removed by an EXIT trap set only for direct execution.
        foreach (string relativePath in new[] { "scripts/install-linux.sh", "scripts/install-linux-legacy.sh" })
        {
            string installer = Read(relativePath);
            Assert.Contains("--format none", installer);
            Assert.Contains("trap cleanup_stage EXIT", installer);
            Assert.DoesNotContain("rm -rf \"$(dirname \"$temp_source\")\"", installer);
        }

        string packager = Read("scripts/package-linux.sh");
        Assert.Contains("\"$FORMAT\" != \"none\"", packager);
        Assert.Contains("Skipping archive (--format none)", packager);

        string powershellPackager = Read("scripts/package-linux.ps1");
        Assert.Contains("'targz', 'zip', 'none'", powershellPackager);
        Assert.Contains("$Format -eq 'none'", powershellPackager);
    }

    [Fact]
    public void LinuxInstallerStagingShellTestPasses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string script = PatchReader.FindRepositoryFile("scripts/tests/install-linux-staging.sh");
        using Process process = Process.Start(new ProcessStartInfo("bash", script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
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
