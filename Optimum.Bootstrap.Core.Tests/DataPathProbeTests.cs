using Optimum.Bootstrap.Core.DataPath;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>Ports the <c>prompt_data_path</c> heuristic from <c>scripts/install-linux.sh</c>.</summary>
public class DataPathProbeTests
{
    [Fact]
    public void PrefersACandidateWithAnActiveSessionOverOneThatMerelyExists()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/.config/VintagestoryData");
        probe.AddDirectory("/home/tester/.config/OptimumVintagestoryData");
        probe.AddFile("/home/tester/.config/OptimumVintagestoryData/clientsettings.json",
            """{ "playeruid": "abc123" }""");

        DataPathDetection detection = DataPathProbe.Detect(probe);

        Assert.Equal("/home/tester/.config/OptimumVintagestoryData", detection.Path);
        Assert.True(detection.HasActiveSession);
    }

    [Fact]
    public void FallsBackToTheFirstDirectoryThatExists()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/.config/VintagestoryData");

        DataPathDetection detection = DataPathProbe.Detect(probe);

        Assert.Equal("/home/tester/.config/VintagestoryData", detection.Path);
        Assert.False(detection.HasActiveSession);
    }

    [Fact]
    public void ReturnsNothingWhenNoCandidateExists()
    {
        DataPathDetection detection = DataPathProbe.Detect(new FakeSystemProbe());
        Assert.Null(detection.Path);
    }
}
