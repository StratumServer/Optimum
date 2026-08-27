using Optimum.Bootstrap.Core;
using Optimum.Cli;
using Xunit;

namespace Optimum.Cli.Tests;

public class CliRunnerTests
{
    [Fact]
    public void VersionPrintsOnePlainLineAndExitsZero()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int code = CliRunner.Run(["--version"], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, code);
        Assert.Equal(CoreInfo.Version, stdout.ToString().Trim());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void UnknownInvocationWritesUsageToStderrAndExitsTwo()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int code = CliRunner.Run(["frobnicate"], stdout, stderr);

        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("usage: optimum", stderr.ToString());
    }
}
