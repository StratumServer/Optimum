using System;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>A synchronization-validation message a test is known to produce today.</summary>
/// <param name="Id">The layer's message id, e.g. SYNC-HAZARD-WRITE-AFTER-WRITE.</param>
/// <param name="TestClass">The test class that produces it.</param>
/// <param name="TestMethod">The test method that produces it.</param>
/// <param name="Defect">The backend defect behind it.</param>
/// <param name="RetiredBy">The plan phase that removes the defect: "1B" or "2".</param>
internal sealed record KnownSyncHazard(string Id, string TestClass, string TestMethod, string Defect, string RetiredBy);

/// <summary>
/// Synchronization hazards the renderer produces today, pinned per test so a
/// new one fails (<see cref="ValidationAssert.NoSyncHazards" />) and a fixed
/// one must be deleted (<see cref="SyncHazardLedgerTests" />): the list can
/// only shrink. Nothing here is fixed by editing the list; each entry is
/// retired by the plan phase it names.
/// </summary>
internal static class KnownSyncHazards
{
    public static readonly KnownSyncHazard[] Entries =
    {
    };

    public static bool Covers(string id, string testClass, string testMethod)
    {
        foreach (KnownSyncHazard entry in Entries)
        {
            if (entry.Id == id && entry.TestClass == testClass && entry.TestMethod == testMethod) return true;
        }
        return false;
    }
}
