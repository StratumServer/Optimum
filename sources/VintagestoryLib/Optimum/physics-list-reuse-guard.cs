namespace Optimum;

/// <summary>
/// Tracks whether every reader of <c>PhysicsManager.ClientList</c> from the
/// previous tick is known to have finished.
///
/// Optimum reuses one <c>List&lt;ConnectedClient&gt;</c> across ticks instead of
/// allocating a fresh one every tick (Stratum PR #111). Every tick that fans
/// physics work out to background threads hands those threads the shared list,
/// and two fan-out waits are bounded to 1000 ms. If a wait expires, the
/// surviving reader is still enumerating the list when the next tick calls
/// <c>List.Clear()</c> on it, which throws inside <c>PhysicsOffthreadTasks</c>,
/// logs, and silently drops that batch of attribute and spawn packets. The
/// server stays up with client-side entity state drifting out of sync.
///
/// The rule this type encodes: the shared list may be reused only immediately
/// after a tick whose fan-out was observed to finish. When a wait expires the
/// old list is abandoned, never touched again, because the next tick allocates
/// a different one.
///
/// Every call site runs on the main server thread, so a plain bool is enough.
/// </summary>
internal static class PhysicsListReuseGuard
{
    private static bool readersJoined;

    /// <summary>
    /// Records that a tick's fan-out was observed to finish, which makes the
    /// shared list safe to reuse on the next tick.
    /// </summary>
    internal static void MarkJoinComplete()
    {
        readersJoined = true;
    }

    /// <summary>
    /// Records that a bounded wait expired, so a reader from the tick that just
    /// ended may still be enumerating the shared list. The next tick has to
    /// allocate instead of reusing.
    /// </summary>
    internal static void MarkJoinIncomplete()
    {
        readersJoined = false;
    }

    /// <summary>
    /// Whether the last observed fan-out completed, and so the shared list is
    /// free of surviving readers.
    /// </summary>
    internal static bool CanReuse()
    {
        return readersJoined;
    }
}
