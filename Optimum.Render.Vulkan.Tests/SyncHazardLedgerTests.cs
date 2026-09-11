using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer(
    "Optimum.Render.Vulkan.Tests.LedgerRunsLastOrderer", "Optimum.Render.Vulkan.Tests")]

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// xunit's default collection order, with the ledger collection moved to the
/// end so it sees every other test's observations. The suite is serial
/// (AssemblyInfo.cs), so "last" is well defined.
/// </summary>
public sealed class LedgerRunsLastOrderer : ITestCollectionOrderer
{
    private readonly DefaultTestCollectionOrderer _default;

    public LedgerRunsLastOrderer(IMessageSink diagnosticMessageSink) =>
        _default = new DefaultTestCollectionOrderer();

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        List<ITestCollection> ordered = _default.OrderTestCollections(testCollections).ToList();
        List<ITestCollection> ledger = ordered
            .Where(c => c.DisplayName == SyncHazardLedgerTests.CollectionName)
            .ToList();
        ordered.RemoveAll(c => c.DisplayName == SyncHazardLedgerTests.CollectionName);
        ordered.AddRange(ledger);
        return ordered;
    }
}

/// <summary>
/// Keeps <see cref="KnownSyncHazards" /> honest: every entry is well formed
/// and still happens. Pattern: KnownDonorGaps in
/// Optimum.Tests/mod-patcher-manifest-consistency-tests.cs.
/// </summary>
[Collection(CollectionName)]
public class SyncHazardLedgerTests
{
    public const string CollectionName = "Synchronization hazard ledger (runs last)";

    private readonly ITestOutputHelper _output;

    public SyncHazardLedgerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EveryPinnedHazardStillOccurs()
    {
        string summary = SyncHazardLedger.Summary();
        _output.WriteLine("validation features: '" + GpuTest.ValidationFeatures + "'");
        _output.WriteLine(summary);

        string? summaryPath = Environment.GetEnvironmentVariable("OPTIMUM_TEST_VALIDATION_SUMMARY");
        if (!string.IsNullOrEmpty(summaryPath)) System.IO.File.WriteAllText(summaryPath, summary);

        List<string> stale = SyncHazardLedger.StaleEntries(KnownSyncHazards.Entries);
        Assert.True(stale.Count == 0,
            "These pinned synchronization hazards no longer occur; remove them from KnownSyncHazards:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>
    /// The companion rule on synthetic data: an entry goes stale only when its
    /// test asserted and the hazard was absent, never because the test did not run.
    /// </summary>
    [Fact]
    public void AnEntryIsStaleOnlyWhenItsTestAssertedWithoutTheHazard()
    {
        const string testClass = nameof(SyncHazardLedgerTests) + "Synthetic";
        var entries = new[]
        {
            new KnownSyncHazard("SYNC-HAZARD-WRITE-AFTER-WRITE", testClass, "StillOccurs", "synthetic", "1B"),
            new KnownSyncHazard("SYNC-HAZARD-WRITE-AFTER-WRITE", testClass, "Vanished", "synthetic", "1B"),
            new KnownSyncHazard("SYNC-HAZARD-WRITE-AFTER-WRITE", testClass, "NeverRan", "synthetic", "2"),
        };

        SyncHazardLedger.Observe(testClass, "StillOccurs", new[] { "SYNC-HAZARD-WRITE-AFTER-WRITE" });
        SyncHazardLedger.Observe(testClass, "Vanished", Array.Empty<string>());

        Assert.Equal(
            new[] { "SYNC-HAZARD-WRITE-AFTER-WRITE | " + testClass + ".Vanished" },
            SyncHazardLedger.StaleEntries(entries));
    }

    /// <summary>
    /// An unpinned synchronization message fails NoSyncHazards and is left out
    /// of NoErrors; a best-practices warning fails neither.
    /// </summary>
    [Fact]
    public void AnUnlistedSynchronizationMessageFailsAndABestPracticesWarningDoesNot()
    {
        var advice = new List<string> { "[warning] [BestPractices-synthetic] advice" };
        ValidationAssert.NoErrors(advice);
        ValidationAssert.NoSyncHazards(advice);

        var hazard = new List<string> { "[error] [SYNC-HAZARD-WRITE-AFTER-WRITE] synthetic hazard" };
        ValidationAssert.NoErrors(hazard);
        XunitException failure = Assert.ThrowsAny<XunitException>(() => ValidationAssert.NoSyncHazards(hazard));
        Assert.Contains(
            "SYNC-HAZARD-WRITE-AFTER-WRITE | SyncHazardLedgerTests | " +
            nameof(AnUnlistedSynchronizationMessageFailsAndABestPracticesWarningDoesNot),
            failure.Message);
    }

    [Fact]
    public void EveryPinnedHazardNamesARealTestItsDefectAndTheRetiringPhase()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Assembly assembly = typeof(SyncHazardLedgerTests).Assembly;

        foreach (KnownSyncHazard entry in KnownSyncHazards.Entries)
        {
            string key = entry.Id + " | " + entry.TestClass + "." + entry.TestMethod;
            if (!seen.Add(key)) problems.Add(key + ": listed twice");
            if (!entry.Id.StartsWith("SYNC-", StringComparison.Ordinal)) problems.Add(key + ": not a SYNC- id");
            if (string.IsNullOrWhiteSpace(entry.Defect)) problems.Add(key + ": no defect named");
            if (entry.RetiredBy is not ("1B" or "2")) problems.Add(key + ": retiring phase must be 1B or 2");

            Type? type = assembly.GetType("Optimum.Render.Vulkan.Tests." + entry.TestClass);
            MethodInfo? method = type?.GetMethod(entry.TestMethod, BindingFlags.Public | BindingFlags.Instance);
            if (method == null || method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length == 0)
            {
                problems.Add(key + ": no such test");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("[error] [SYNC-HAZARD-WRITE-AFTER-WRITE] vkQueueSubmit(): ...", "SYNC-HAZARD-WRITE-AFTER-WRITE", true, false)]
    [InlineData("[warning] [BestPractices-vkCreateDevice-physical-device-features-not-retrieved] ...", "BestPractices-vkCreateDevice-physical-device-features-not-retrieved", false, true)]
    [InlineData("[error] [VUID-vkCmdDraw-None-08600] ...", "VUID-vkCmdDraw-None-08600", false, false)]
    [InlineData("[error] no tag here", null, false, false)]
    public void MessageIdsComeFromTheBracketTagAfterTheSeverity(
        string message, string? id, bool synchronization, bool bestPractices)
    {
        Assert.Equal(id, ValidationAssert.MessageId(message));
        Assert.Equal(synchronization, ValidationAssert.IsSynchronization(message));
        Assert.Equal(bestPractices, ValidationAssert.IsBestPractices(message));
    }
}
