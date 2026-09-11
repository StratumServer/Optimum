using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Shared check that a captured validation-layer log holds no errors.
/// </summary>
internal static partial class ValidationAssert
{
    /// <summary>
    /// Fails when any message carries the error prefix. Only what the layers
    /// reported at error severity counts: advisories - a fragment output with
    /// no attachment, say - are prefixed as warnings and are not failures;
    /// treating every message as one made these assertions fire on notes
    /// about correct frames. Synchronization hazards are judged by
    /// <see cref="NoSyncHazards" /> against the pinned list instead, so a known
    /// renderer hazard does not fail every test that happens to trigger it.
    /// </summary>
    public static void NoErrors(IReadOnlyCollection<string> messages)
    {
        var errors = Snapshot(messages)
            .Where(m => m.StartsWith(VulkanContext.ErrorPrefix, StringComparison.Ordinal) && !IsSynchronization(m))
            .ToList();
        Assert.True(errors.Count == 0, "validation errors:\n" + string.Join("\n", errors));
    }
}
