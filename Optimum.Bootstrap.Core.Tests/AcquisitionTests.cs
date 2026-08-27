using Optimum.Bootstrap.Core.Acquisition;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class AcquisitionTests
{
    [Fact]
    public void IlspycmdToolArgumentsMatchTheLoggedInvocation()
    {
        // scripts/tests/install-linux-prerequisites.sh asserts exactly this line.
        Assert.Equal(
            "tool update -g ilspycmd --version 10.1.1.8388 --allow-downgrade",
            string.Join(' ', IlspycmdAcquisition.ToolArguments("10.1.1.8388")));
    }

    [Fact]
    public void SdkPlanHonoursGlobalJsonWhenItIsPresent()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");
        probe.AddFile("/repo/global.json");

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.True(decision.CanRunScript);
        Assert.NotNull(decision.Plan);
        Assert.Contains("--jsonfile", decision.Plan!.Arguments);
        Assert.Contains("/repo/global.json", decision.Plan.Arguments);
        Assert.Contains("--no-path", decision.Plan.Arguments);
        Assert.EndsWith("dotnet-install.sh", decision.Plan.ScriptUrl);
    }

    [Fact]
    public void SdkPlanFallsBackToTheChannelWhenGlobalJsonIsAbsent()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.NotNull(decision.Plan);
        Assert.Contains("--channel", decision.Plan!.Arguments);
        Assert.Contains("10.0", decision.Plan.Arguments);
    }
}
