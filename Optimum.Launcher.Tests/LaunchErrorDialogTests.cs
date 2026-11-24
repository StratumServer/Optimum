using System;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class LaunchErrorDialogTests
{
    [Fact]
    public void ShouldShowOnlyOnInteractiveWindowsRuns()
    {
        Assert.True(LaunchErrorDialog.ShouldShow(
            isWindows: true, userInteractive: true, nonInteractiveMode: false, noDialogEnv: null, ciEnv: null));

        Assert.False(LaunchErrorDialog.ShouldShow(
            isWindows: false, userInteractive: true, nonInteractiveMode: false, noDialogEnv: null, ciEnv: null));
        Assert.False(LaunchErrorDialog.ShouldShow(
            isWindows: true, userInteractive: false, nonInteractiveMode: false, noDialogEnv: null, ciEnv: null));
        Assert.False(LaunchErrorDialog.ShouldShow(
            isWindows: true, userInteractive: true, nonInteractiveMode: true, noDialogEnv: null, ciEnv: null));
        Assert.False(LaunchErrorDialog.ShouldShow(
            isWindows: true, userInteractive: true, nonInteractiveMode: false, noDialogEnv: "1", ciEnv: null));
        Assert.False(LaunchErrorDialog.ShouldShow(
            isWindows: true, userInteractive: true, nonInteractiveMode: false, noDialogEnv: null, ciEnv: "true"));
    }

    [Fact]
    public void FormatMessageIncludesReasonAndLogPath()
    {
        string message = LaunchErrorDialog.FormatMessage(
            "VintagestoryAPI.dll is not the official Vintage Story 1.22.7 build.",
            @"G:\VS Data\VintagestoryData\Logs\optimum-launcher.log");

        Assert.Contains("Optimum could not launch Vintage Story.", message);
        Assert.Contains("VintagestoryAPI.dll is not the official Vintage Story 1.22.7 build.", message);
        Assert.Contains(@"G:\VS Data\VintagestoryData\Logs\optimum-launcher.log", message);
    }

    [Fact]
    public void FormatMessageHandlesBlankReasonAndOversizedReason()
    {
        string fallback = LaunchErrorDialog.FormatMessage("   ", null);
        Assert.Contains("An unexpected error prevented Optimum from starting.", fallback);
        Assert.DoesNotContain("Log file:", fallback);

        string longReason = new('x', LaunchErrorDialog.MaxReasonLength + 200);
        string truncated = LaunchErrorDialog.FormatMessage(longReason, "/tmp/optimum-launcher.log");
        Assert.Contains("(See the log file for full details.)", truncated);
        Assert.Contains("/tmp/optimum-launcher.log", truncated);
    }

    [Theory]
    [InlineData(new[] { "--validate-only" }, true)]
    [InlineData(new[] { "--optimum-gui-profile-fingerprint" }, true)]
    [InlineData(new[] { "--optimum-gui-validate-certification" }, true)]
    [InlineData(new[] { "--optimum-gui-device-preflight" }, true)]
    [InlineData(new[] { "--dataPath", @"G:\VS Data\VintagestoryData" }, false)]
    [InlineData(new string[0], false)]
    public void IsNonInteractiveControlModeDetectsAutomationFlags(string[] args, bool expected)
    {
        Assert.Equal(expected, Program.IsNonInteractiveControlMode(args));
    }
}
