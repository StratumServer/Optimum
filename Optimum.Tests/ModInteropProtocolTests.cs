using System;
using System.Linq;
using Komet.Interop;
using Komet.Runtime;
using Vintagestory.API.Config;
using Xunit;
using OptimumInterop = Vintagestory.API.Config.VsModInterop;

namespace Optimum.Tests;

/// <summary>
/// Issue #119 follow-up: the interop protocol ("vsmod-interop/1") that lets a peer ask Optimum
/// to stand a feature down. These tests pin the provider half (a request is visible, scoped to
/// its requester, refused for unknown features, and restores the user's setting when released)
/// and the consumer half (a peer is found by its protocol constants, and a peer that is absent,
/// foreign or silent changes nothing).
/// </summary>
[Collection("OptimumConfig")]
public sealed class ModInteropProtocolTests : IDisposable
{
    public ModInteropProtocolTests()
    {
        OptimumInterop.ResetYieldsForTests();
        OptimumConfig.AdaptiveRadiusEnabled = true;
        KometInterop.AnswersFeature = true;
        KometInterop.OverrideState = null;
        ModInterop.ResetCacheForTests();
    }

    public void Dispose()
    {
        OptimumInterop.ResetYieldsForTests();
        OptimumConfig.AdaptiveRadiusEnabled = true;
        KometInterop.AnswersFeature = true;
        KometInterop.OverrideState = null;
        ModInterop.ResetCacheForTests();
        // The protocol-vs-field test turns Komet detection on; the SIMD/MDI tests in the same
        // collection read that flag, so it has to go back the way it was found.
        OptimumCompatibilityGuard.ResetForTests();
        InflowBrake.Enabled = false;
    }

    [Fact]
    public void NegotiationHoldsTheFeatureAndHandsItBackWithoutTouchingThePreference()
    {
        Assert.True(OptimumConfig.EffectiveAdaptiveRadiusEnabled);

        string[] held = OptimumInterop.Negotiate(OptimumInterop.RequesterKomet,
            new[] { OptimumInterop.ReasonKometInflow });

        Assert.Contains(OptimumInterop.FeatureAdaptiveRadius, held);
        Assert.True(OptimumInterop.IsYielded(OptimumInterop.FeatureAdaptiveRadius));
        // The saved preference is untouched: only the effective state changes.
        Assert.True(OptimumConfig.AdaptiveRadiusEnabled);
        Assert.False(OptimumConfig.EffectiveAdaptiveRadiusEnabled);
        Assert.Contains("komet", OptimumInterop.DescribeInterop());
        Assert.Contains("yielded to komet", OptimumConfig.DescribeToggles()
            .Single(toggle => toggle.Name == "AdaptiveRadius").Value);

        // Reporting the feature gone is the release: the reconciled answer is empty.
        held = OptimumInterop.Negotiate(OptimumInterop.RequesterKomet, Array.Empty<string>());

        Assert.Empty(held);
        Assert.False(OptimumInterop.IsYielded(OptimumInterop.FeatureAdaptiveRadius));
        Assert.True(OptimumConfig.EffectiveAdaptiveRadiusEnabled);
    }

    [Fact]
    public void OneRequestersHoldDoesNotClearAnothers()
    {
        OptimumInterop.TryYield(OptimumInterop.FeatureAdaptiveRadius, "some-other-mod",
            "testing", out _);
        OptimumInterop.TryYield(OptimumInterop.FeatureAdaptiveRadius, OptimumInterop.RequesterKomet,
            OptimumInterop.ReasonKometInflow, out _);

        OptimumInterop.Release(OptimumInterop.FeatureAdaptiveRadius, OptimumInterop.RequesterKomet);

        Assert.True(OptimumInterop.IsYielded(OptimumInterop.FeatureAdaptiveRadius));
        OptimumInterop.Release(OptimumInterop.FeatureAdaptiveRadius, "some-other-mod");
        Assert.False(OptimumInterop.IsYielded(OptimumInterop.FeatureAdaptiveRadius));
    }

    [Fact]
    public void UnknownFeatureAndMissingRequesterAreRefusedWithAReason()
    {
        Assert.False(OptimumInterop.TryYield("optimum.not-a-feature", "someone", "testing", out string unknown));
        Assert.Contains("unsupported feature", unknown);
        Assert.False(OptimumInterop.TryGetFeatureState("optimum.not-a-feature", out _));

        Assert.False(OptimumInterop.TryYield(OptimumInterop.FeatureAdaptiveRadius, "", "testing", out string noRequester));
        Assert.Contains("missing requester", noRequester);

        // Repeating a request keeps the hold and reports it as already held.
        Assert.True(OptimumInterop.TryYield(OptimumInterop.FeatureAdaptiveRadius, "someone", "testing", out _));
        Assert.True(OptimumInterop.TryYield(OptimumInterop.FeatureAdaptiveRadius, "someone", "testing again", out string repeated));
        Assert.Contains("already yielded", repeated);
        Assert.True(OptimumInterop.IsYielded(OptimumInterop.FeatureAdaptiveRadius));
    }

    [Fact]
    public void FeatureStateReportsTheEffectiveValue()
    {
        Assert.True(OptimumInterop.TryGetFeatureState(OptimumInterop.FeatureAdaptiveRadius, out bool active));
        Assert.True(active);

        OptimumInterop.Negotiate(OptimumInterop.RequesterKomet, new[] { OptimumInterop.ReasonKometInflow });
        Assert.True(OptimumInterop.TryGetFeatureState(OptimumInterop.FeatureAdaptiveRadius, out active));
        Assert.False(active);

        // A user who turned the feature off is reported as off whether or not anyone asked.
        OptimumInterop.Release(OptimumInterop.FeatureAdaptiveRadius, OptimumInterop.RequesterKomet);
        OptimumConfig.AdaptiveRadiusEnabled = false;
        Assert.True(OptimumInterop.TryGetFeatureState(OptimumInterop.FeatureAdaptiveRadius, out active));
        Assert.False(active);
    }

    [Fact]
    public void ProviderIdsAreDiscoverableMetadataConstants()
    {
        Assert.Equal("optimum", typeof(OptimumInterop).GetField("ProviderId")?.GetRawConstantValue());
        Assert.Equal("komet", typeof(KometInterop).GetField("ProviderId")?.GetRawConstantValue());
    }

    [Fact]
    public void PeerIsFoundByItsProtocolConstants()
    {
        Assert.True(ModInterop.TryGetFeatureState(KometInterop.ProviderId, KometInterop.FeatureAdaptiveChunkInflow, out bool active));

        KometInterop.OverrideState = true;
        Assert.True(ModInterop.TryGetFeatureState(KometInterop.ProviderId, KometInterop.FeatureAdaptiveChunkInflow, out active));
        Assert.True(active);

        KometInterop.OverrideState = false;
        Assert.True(ModInterop.TryGetFeatureState(KometInterop.ProviderId, KometInterop.FeatureAdaptiveChunkInflow, out active));
        Assert.False(active);
    }

    [Fact]
    public void AbsentOrSilentPeerAnswersNothing()
    {
        Assert.False(ModInterop.TryGetFeatureState("no-such-mod", "anything", out bool active));
        Assert.False(active);
        Assert.Empty(ModInterop.Negotiate("no-such-mod", "komet", Array.Empty<string>()));
        Assert.Empty(ModInterop.CoordinatedFeatureIds("no-such-mod"));
        ModInterop.Release("no-such-mod", "anything", "komet");
    }

    [Fact]
    public void ProtocolAnswerWinsOverTheReflectedField()
    {
        OptimumCompatibilityGuard.ResetForTests();
        ModInterop.ResetCacheForTests();
        OptimumConfig.KometDetected = true;

        // Komet answers for the feature and the answer disagrees with the reflected field:
        // the protocol is authoritative, because it is the state Komet itself reports.
        KometInterop.OverrideState = false;
        InflowBrake.Enabled = true;
        Assert.False(OptimumCompatibilityGuard.IsKometAdaptiveChunkInflowEnabled());

        KometInterop.OverrideState = true;
        InflowBrake.Enabled = false;
        Assert.True(OptimumCompatibilityGuard.IsKometAdaptiveChunkInflowEnabled());

        // A Komet that does not know the feature falls back to the reflected field.
        KometInterop.OverrideState = null;
        KometInterop.AnswersFeature = false;
        InflowBrake.Enabled = true;
        Assert.True(OptimumCompatibilityGuard.IsKometAdaptiveChunkInflowEnabled());
    }
}
