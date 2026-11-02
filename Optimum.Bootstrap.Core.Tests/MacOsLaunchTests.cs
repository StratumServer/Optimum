using Optimum.Bootstrap.Core.Install;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class MacOsLaunchTests
{
    [Fact]
    public void MacOsLauncherStartsTheNativeAppExecutable()
    {
        string launcher = PackageDeployer.BuildMacOsLauncherScript(null);

        Assert.Contains("cd \"$(dirname \"$0\")\"", launcher);
        Assert.Contains("exec ./Optimum \"$@\"", launcher);
        Assert.DoesNotContain("run.sh", launcher);
    }

    [Fact]
    public void MacOsLauncherUsesConfiguredDataPathUnlessOverridden()
    {
        string launcher = PackageDeployer.BuildMacOsLauncherScript("/Users/tester/My Data");

        Assert.Contains("exec ./Optimum --dataPath '/Users/tester/My Data' \"$@\"", launcher);
        Assert.Contains("case \"$arg\" in", launcher);
        Assert.Contains("exec ./Optimum \"$@\"", launcher);
        Assert.DoesNotContain("run.sh", launcher);
    }
}
