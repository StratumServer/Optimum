using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The NGX lifetime contract, pinned without a GPU and without NGX: every rule the
/// 2026-09-12 crash made expensive is asserted here on an owner of the test's own,
/// so the process's own lifetime (<c>NgxLifetime.Process</c>) is never touched.
///
/// The rules, in the words of the bug report they come from:
/// <list type="bullet">
/// <item>a settings change - the upscaler switched off, a different preset, a
/// runtime stand-down - never shuts NGX down;</item>
/// <item><c>Shutdown1</c> happens at most once, at teardown, after the last
/// feature is released and the frame timeline is drained;</item>
/// <item>a second teardown cannot reach <c>Shutdown1</c>, which
/// <c>ShutdownGraphics</c> can be on the fallback path;</item>
/// <item>NGX is never brought up again afterwards.</item>
/// </list>
/// </summary>
public sealed class NgxLifetimeContractTests
{
    private readonly ITestOutputHelper _output;

    public NgxLifetimeContractTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>An owner whose Shutdown1 is a recorder, plus the record it writes into.</summary>
    private static NgxLifetimeOwner NewOwner(List<string> log)
    {
        return new NgxLifetimeOwner(device =>
        {
            log.Add("Shutdown1(" + device + ")");
            return NgxResult.Success;
        });
    }

    private static NgxLifetimeOutcome BringUp(NgxLifetimeOwner owner, List<string> log)
    {
        return owner.Initialize(new IntPtr(0x1234), () =>
        {
            log.Add("Init");
            return NgxResult.Success;
        }, out _);
    }

    [Fact]
    public void TeardownReleasesThenDrainsThenShutsDownExactlyOnce()
    {
        var log = new List<string>();
        NgxLifetimeOwner owner = NewOwner(log);
        Assert.Equal(NgxLifetimeOutcome.Done, BringUp(owner, log));

        owner.FeatureCreated();
        NgxLifetimeOutcome outcome = owner.ShutDown(
            () => { log.Add("release"); owner.FeatureRetired(); },
            () => { log.Add("drain"); return 1; });

        Assert.Equal(NgxLifetimeOutcome.Done, outcome);
        Assert.Equal(new[] { "Init", "release", "drain", "Shutdown1(4660)" }, log);
        Assert.Equal(1, owner.ShutdownCalls);
        Assert.False(owner.Initialized);
        Assert.True(owner.Spent);
        _output.WriteLine(string.Join(" -> ", log));
    }

    [Fact]
    public void ASecondTeardownCannotReachShutdown()
    {
        var log = new List<string>();
        NgxLifetimeOwner owner = NewOwner(log);
        BringUp(owner, log);
        Assert.Equal(NgxLifetimeOutcome.Done, owner.ShutDown(null, null));

        // ShutdownGraphics runs on the fallback path as well, so teardown really can
        // happen twice. The second one must not touch NGX: a second Shutdown1 is not
        // survivable, and neither is the release the caller would do before it.
        Assert.Equal(NgxLifetimeOutcome.AlreadyShutDown, owner.ShutDown(
            () => log.Add("release again"), () => { log.Add("drain again"); return 0; }));
        Assert.Equal(NgxLifetimeOutcome.AlreadyShutDown, owner.ShutDown(null, null));

        Assert.Equal(1, owner.ShutdownCalls);
        Assert.DoesNotContain("release again", log);
        Assert.DoesNotContain("drain again", log);
    }

    [Fact]
    public void NgxIsNeverBroughtUpAgainAfterTheShutdown()
    {
        var log = new List<string>();
        NgxLifetimeOwner owner = NewOwner(log);
        BringUp(owner, log);
        owner.ShutDown(null, null);

        Assert.Equal(NgxLifetimeOutcome.AlreadyShutDown, BringUp(owner, log));
        Assert.Equal(1, owner.InitCalls);
        Assert.False(owner.Initialized);
    }

    [Fact]
    public void ASecondBringUpIsRefusedWhileNgxIsUp()
    {
        var log = new List<string>();
        NgxLifetimeOwner owner = NewOwner(log);
        BringUp(owner, log);

        Assert.Equal(NgxLifetimeOutcome.AlreadyInitialized, BringUp(owner, log));
        Assert.Equal(1, owner.InitCalls);
    }

    [Fact]
    public void AFailedBringUpSpendsNothingButStillRefusesTheShutdown()
    {
        var log = new List<string>();
        NgxLifetimeOwner owner = NewOwner(log);

        NgxLifetimeOutcome brought = owner.Initialize(
            new IntPtr(7), () => NgxResult.FailFeatureNotSupported, out NgxResult result);

        Assert.Equal(NgxLifetimeOutcome.NotInitialized, brought);
        Assert.Equal(NgxResult.FailFeatureNotSupported, result);
        // Nothing was initialised, so the teardown has nothing to call - and it must
        // not call it: Shutdown1 without an Init is not a defined thing to do.
        Assert.Equal(NgxLifetimeOutcome.NotInitialized, owner.ShutDown(null, null));
        Assert.Equal(0, owner.ShutdownCalls);
        Assert.Empty(log);
    }

    [Fact]
    public void AFeatureStillLiveAfterTheDrainRefusesTheShutdown()
    {
        var log = new List<string>();
        NgxLifetimeOwner owner = NewOwner(log);
        BringUp(owner, log);
        owner.FeatureCreated();

        // The release did not retire it - the pair that takes the process down is
        // "Shutdown1, then ReleaseFeature", so NGX stays up instead.
        NgxLifetimeOutcome outcome = owner.ShutDown(null, () => 0, line => log.Add(line));

        Assert.Equal(NgxLifetimeOutcome.FeatureStillLive, outcome);
        Assert.Equal(0, owner.ShutdownCalls);
        Assert.True(owner.Initialized);
        Assert.Contains(log, line => line.Contains("still live"));
    }

    /// <summary>
    /// The sequence from the bug report, without a GPU: preset, preset, off, on. Every
    /// one of them retires the feature and creates another, and not one of them may
    /// end an NGX lifetime - the process could not bring it back.
    /// </summary>
    [Fact]
    public void SettingsChangesRetireFeaturesAndNeverShutNgxDown()
    {
        var log = new List<string>();
        NgxLifetimeOwner owner = NewOwner(log);
        BringUp(owner, log);

        for (int change = 0; change < 4; change++)
        {
            owner.FeatureCreated();
            owner.FeatureRetired();
            Assert.True(owner.Initialized);
            Assert.False(owner.Spent);
            Assert.Equal(0, owner.ShutdownCalls);
        }

        Assert.Equal(0, owner.LiveFeatures);
        Assert.Equal(new[] { "Init" }, log);
    }
}
