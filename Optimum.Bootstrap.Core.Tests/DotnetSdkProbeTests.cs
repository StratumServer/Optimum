using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>
/// Ports the <c>check_dotnet10</c> selection from
/// <c>scripts/tests/install-linux-prerequisites.sh</c>: given a system dotnet on
/// SDK 9 and a user dotnet on SDK 10, detection picks the user one.
/// </summary>
public class DotnetSdkProbeTests
{
    [Fact]
    public void PicksTheCandidateThatReportsANet10Sdk()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/t/bin/dotnet:/home/tester/.dotnet/dotnet";
        probe.AddFile("/t/bin/dotnet");
        probe.AddFile("/home/tester/.dotnet/dotnet");
        probe.OnCommand("/t/bin/dotnet", "--list-sdks", "9.0.100 [/system/sdk]\n");
        probe.OnCommand("/home/tester/.dotnet/dotnet", "--list-sdks", "10.0.100 [/user/sdk]\n");

        Assert.Equal("/home/tester/.dotnet/dotnet", DotnetSdkProbe.Find(probe));
    }

    [Fact]
    public void ReturnsNullWhenNoCandidateReportsNet10()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/t/bin/dotnet";
        probe.AddFile("/t/bin/dotnet");
        probe.OnCommand("/t/bin/dotnet", "--list-sdks", "9.0.100 [/system/sdk]\n");

        Assert.Null(DotnetSdkProbe.Find(probe));
    }

    [Fact]
    public void PrefersDotnetOnPathBeforeTheCandidateList()
    {
        var probe = new FakeSystemProbe();
        probe.Path.Add("/usr/bin");
        probe.AddFile("/usr/bin/dotnet");
        probe.OnCommand("/usr/bin/dotnet", "--list-sdks", "10.0.203 [/usr/lib/dotnet/sdk]\n");

        Assert.Equal("/usr/bin/dotnet", DotnetSdkProbe.Find(probe));
    }
}
