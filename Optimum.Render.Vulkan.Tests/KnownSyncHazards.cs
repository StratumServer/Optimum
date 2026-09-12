using System;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>A synchronization-validation message a test is known to produce today.</summary>
/// <param name="Id">The layer's message id, e.g. SYNC-HAZARD-WRITE-AFTER-WRITE.</param>
/// <param name="TestClass">The test class that produces it.</param>
/// <param name="TestMethod">The test method that produces it.</param>
/// <param name="Defect">The backend defect behind it.</param>
/// <param name="RetiredBy">
/// The plan phase that removes the defect: "1B" or "2" - or "vendor" for a
/// hazard entirely inside a vendor runtime's own recorded commands, which no
/// phase of ours can retire. A "vendor" entry has to name, in
/// <paramref name="Defect" />, both sides of the hazard and the evidence that
/// neither is ours; it is the only kind of entry that is not a promise to fix
/// something.
/// </param>
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
    private const string DlssInternalClear =
        "inside NGX: vkCmdClearColorImage on NGX's own image (nv.ngx.dlss.resource) against the layout " +
        "transition NGX's own vkCmdPipelineBarrier made for it, both recorded by NGX inside its " +
        "nv.ngx.dlss.Evaluate debug scope. No Optimum image, barrier or command is named on either side, " +
        "and the images are ones NGX allocates on our device and never shows us, so there is nothing here " +
        "for us to synchronise. Measured 2026-09-12 on driver 615.71.09 with DLSS SDK 310.9.1. It happens " +
        "on the first evaluate of a feature whose internal images NGX has just allocated, which is once " +
        "per feature size rather than once per process: NgxRuntime's warm-up absorbs the process's first " +
        "one before any test takes its message mark, so the only test that still sees it is the one that " +
        "deliberately creates a second feature at a second size - the resize test below. Every other DLSS " +
        "test therefore asserts a genuinely clean run.";

    public static readonly KnownSyncHazard[] Entries =
    {
        new("SYNC-HAZARD-WRITE-AFTER-WRITE", nameof(DlssUpscalerTests),
            nameof(DlssUpscalerTests.AResizeRebuildsTheFeatureWithoutLeakingIt),
            DlssInternalClear, "vendor"),
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
