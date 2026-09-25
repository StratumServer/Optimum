using System.Linq;
using System.Threading;
using Komet.Runtime;
using Vintagestory.API.Config;
using Xunit;
using OptimumInterop = Vintagestory.API.Config.VsModInterop;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class KometAdaptiveChunkInflowGuardTests : System.IDisposable
{
    public KometAdaptiveChunkInflowGuardTests()
    {
        OptimumCompatibilityGuard.ResetForTests();
        OptimumInterop.ResetYieldsForTests();
        InflowBrake.Enabled = false;
        OptimumConfig.AdaptiveRadiusEnabled = true;
    }

    public void Dispose()
    {
        OptimumCompatibilityGuard.ResetForTests();
        OptimumInterop.ResetYieldsForTests();
        InflowBrake.Enabled = false;
        OptimumConfig.AdaptiveRadiusEnabled = true;
    }

    [Fact]
    public void ReadsKometRuntimeSwitch_NotOnlyItsConfigPresence()
    {
        OptimumConfig.KometDetected = true;

        InflowBrake.Enabled = true;
        Assert.True(OptimumCompatibilityGuard.IsKometAdaptiveChunkInflowEnabled());

        InflowBrake.Enabled = false;
        Assert.False(OptimumCompatibilityGuard.IsKometAdaptiveChunkInflowEnabled());
    }

    [Fact]
    public void ActiveKometInflowDisablesEffectiveRadius_AndKeepsSavedPreference()
    {
        OptimumConfig.KometDetected = true;
        InflowBrake.Enabled = true;
        OptimumInterop.Negotiate(OptimumInterop.RequesterKomet, new[] { OptimumInterop.ReasonKometInflow });
        var controller = new OptimumAdaptiveRadiusController(12);

        controller.Tick(500, 12);

        Assert.True(OptimumConfig.AdaptiveRadiusEnabled);
        Assert.False(OptimumConfig.EffectiveAdaptiveRadiusEnabled);
        Assert.Equal(12, controller.EffectiveRadius);
        Assert.Equal(12, OptimumConfig.AdaptiveRadiusEffective);
        Assert.Contains("yielded to komet",
            OptimumConfig.DescribeToggles().Single(toggle => toggle.Name == "AdaptiveRadius").Value);
    }

    [Fact]
    public void ControllerResumesAdaptiveRadiusWhenKometStopsInfluxBraking()
    {
        OptimumConfig.KometDetected = true;
        InflowBrake.Enabled = true;
        OptimumInterop.Negotiate(OptimumInterop.RequesterKomet, new[] { OptimumInterop.ReasonKometInflow });
        var controller = new OptimumAdaptiveRadiusController(12);

        controller.Tick(100, 12);
        Assert.Equal(12, controller.EffectiveRadius);

        // The runtime probe is deliberately throttled to four reads per second.
        Thread.Sleep(300);
        InflowBrake.Enabled = false;
        OptimumInterop.Negotiate(OptimumInterop.RequesterKomet, System.Array.Empty<string>());
        controller.Tick(100, 12);

        Assert.True(OptimumConfig.EffectiveAdaptiveRadiusEnabled);
        Assert.Equal(11, controller.EffectiveRadius);
    }

    [Fact]
    public void KometPresentButAdaptiveInflowOffDoesNotSuppressAdaptiveRadius()
    {
        OptimumConfig.KometDetected = true;
        InflowBrake.Enabled = false;
        var controller = new OptimumAdaptiveRadiusController(12);

        controller.Tick(100, 12);

        Assert.True(OptimumConfig.EffectiveAdaptiveRadiusEnabled);
        Assert.Equal(11, controller.EffectiveRadius);
    }

    [Fact]
    public void KometFeatureNotDetectedDoesNotSuppressAdaptiveRadius()
    {
        InflowBrake.Enabled = true;
        var controller = new OptimumAdaptiveRadiusController(12);

        controller.Tick(100, 12);

        Assert.True(OptimumConfig.EffectiveAdaptiveRadiusEnabled);
        Assert.Equal(11, controller.EffectiveRadius);
    }
}
