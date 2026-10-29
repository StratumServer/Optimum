using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>Ports <c>scripts/tests/install-linux-nixos.sh</c>.</summary>
public class NixEnvironmentTests
{
    [Fact]
    public void DownloadedSdkRunsOnADefaultGlibcHost()
    {
        var probe = new FakeSystemProbe { Arch = Architecture.X64 };
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");

        Assert.True(NixEnvironment.DownloadedSdkRunnable(probe));
    }

    [Theory]
    [InlineData(OsKind.Windows)]
    [InlineData(OsKind.MacOs)]
    public void TheNonFhsCheckIsLinuxOnly(OsKind os)
    {
        var probe = new FakeSystemProbe { Os = os };
        // A leftover NIX_STORE / interpreter override must not make a native
        // Windows or macOS host look non-FHS.
        probe.Environment["NIX_STORE"] = "/nix/store";
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";

        Assert.False(NixEnvironment.IsNixOs(probe));
        Assert.Equal(string.Empty, NixEnvironment.GlibcInterpreterPath(probe));
        Assert.True(NixEnvironment.DownloadedSdkRunnable(probe));
    }

    [Fact]
    public void SdkAcquisitionBuildsAWindowsPlan()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows, HomeDirectory = @"C:\Users\tester" };

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, @"C:\repo");

        Assert.True(decision.CanRunScript);
        Assert.NotNull(decision.Plan);
        Assert.EndsWith("dotnet-install.ps1", decision.Plan!.ScriptUrl);
        Assert.Contains("-NoPath", decision.Plan.Arguments);
    }

    [Fact]
    public void DownloadedSdkDoesNotRunWhenTheInterpreterIsMissing()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";

        Assert.Equal("/tmp/missing-ld-linux", NixEnvironment.GlibcInterpreterPath(probe));
        Assert.False(NixEnvironment.DownloadedSdkRunnable(probe));
    }

    [Fact]
    public void DetectNixOsFollowsNixStoreAndTheMarkerFile()
    {
        var probe = new FakeSystemProbe();
        Assert.False(NixEnvironment.IsNixOs(probe));

        probe.Environment["NIX_STORE"] = "/nix/store";
        Assert.True(NixEnvironment.IsNixOs(probe));

        probe.Environment.Remove("NIX_STORE");
        probe.AddFile("/etc/NIXOS");
        Assert.True(NixEnvironment.IsNixOs(probe));
    }

    [Fact]
    public void DevelopmentShellCommandIsRepositoryScopedAndEnablesFlakesForThatInvocation()
    {
        Assert.Equal(
            "nix --extra-experimental-features 'nix-command flakes' develop --no-update-lock-file '/repo path'",
            NixEnvironment.DevelopmentShellCommand("/repo path"));
        Assert.Equal(
            "nix --extra-experimental-features 'nix-command flakes' develop --no-update-lock-file '/repo/it'\\''s'",
            NixEnvironment.DevelopmentShellCommand("/repo/it's"));
    }

    [Fact]
    public void SdkAcquisitionRefusesOnNixOs()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["NIX_STORE"] = "/nix/store";

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.False(decision.CanRunScript);
        Assert.Null(decision.Plan);
        Assert.Contains("NixOS", decision.RefusalReason);
    }

    [Fact]
    public void SdkAcquisitionRefusesOnANonFhsHost()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.False(decision.CanRunScript);
        Assert.Contains("non-FHS", decision.RefusalReason);
    }

    [Fact]
    public void PrerequisiteScannerRoutesTheSdkRowThroughTheProjectDevelopmentShellOnNixOs()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["NIX_STORE"] = "/nix/store";
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult dotnet = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(PrerequisiteState.Missing, dotnet.State);
        Assert.Contains("project Nix shell", dotnet.Label);
        Assert.Equal(NixEnvironment.DevelopmentShellCommand("/repo"), dotnet.AcquisitionCommand);
        Assert.Contains("restart the installer", dotnet.Guidance);
    }

    [Fact]
    public void PrerequisiteScannerShowsProjectShellForMissingBootstrapCommandsOnNixOs()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["NIX_STORE"] = "/nix/store";
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult[] missing = new PrerequisiteScanner(probe, "/repo").Scan()
            .Where(result => result.State == PrerequisiteState.Missing
                && result.Definition.Level == RequirementLevel.Required)
            .ToArray();

        Assert.NotEmpty(missing);
        Assert.All(missing, result =>
        {
            Assert.Equal(AcquisitionKind.Manual, result.Acquisition);
            Assert.Equal(NixEnvironment.DevelopmentShellCommand("/repo"), result.AcquisitionCommand);
            Assert.Contains("global.json", result.Guidance);
        });
    }

    [Fact]
    public void PrerequisiteScannerExplainsUnsupportedNixArchitectureAndFallback()
    {
        var probe = new FakeSystemProbe { Arch = Architecture.X86 };
        probe.Environment["NIX_STORE"] = "/nix/store";
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult dotnet = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(result => result.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(AcquisitionKind.Manual, dotnet.Acquisition);
        Assert.Equal("nix search nixpkgs 'dotnet-sdk_10'", dotnet.AcquisitionCommand);
        Assert.Contains("X86", dotnet.Guidance);
        Assert.Contains("user development shell", dotnet.Guidance);
    }

    [Fact]
    public void PrerequisiteScannerFlagsANonFhsHostWithNoInstallCommand()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult dotnet = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(PrerequisiteState.Missing, dotnet.State);
        Assert.Contains("non-FHS", dotnet.Label);
        Assert.Null(dotnet.AcquisitionCommand);
    }
}
