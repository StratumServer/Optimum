using System;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Thread-confinement canary. Catches the "value fetched on thread A, used on
/// thread B" bug class that bare [ThreadStatic] cannot detect. Near-zero cost
/// when disabled (single bool check on the hot path).
///
/// Enable via env var OPTIMUM_THREAD_GUARD=1 or by calling Enable().
/// </summary>
internal sealed class OptimumThreadGuard
{
    private static volatile bool _enabled = Environment.GetEnvironmentVariable("OPTIMUM_THREAD_GUARD") == "1";
    private static int _violationCount;

    private int _ownerThreadId;

    /// <summary>Total violations detected across all guards since process start.</summary>
    public static int ViolationCount => Volatile.Read(ref _violationCount);

    /// <summary>Whether the guard is active. When false, Mark/Verify are no-ops.</summary>
    public static bool Enabled => _enabled;

    /// <summary>Enable the guard globally. Takes effect on next Mark/Verify call.</summary>
    public static void Enable() => _enabled = true;

    /// <summary>Disable the guard globally.</summary>
    public static void Disable() => _enabled = false;

    /// <summary>Reset the global violation counter (for testing).</summary>
    public static void ResetViolations() => Interlocked.Exchange(ref _violationCount, 0);

    /// <summary>
    /// Record the current thread as the owner. Call at the point where the
    /// value is produced or fetched.
    /// </summary>
    public void Mark()
    {
        if (!_enabled) return;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Assert the current thread matches the marked owner. Increments the
    /// violation counter on mismatch. When <paramref name="throwOnViolation"/>
    /// is true, also throws.
    /// </summary>
    /// <returns>True if the check passed or the guard is disabled.</returns>
    public bool Verify(bool throwOnViolation = false)
    {
        if (!_enabled) return true;

        int current = Environment.CurrentManagedThreadId;
        int owner = _ownerThreadId;

        if (owner == 0) return true; // Never marked, skip.

        if (current != owner)
        {
            Interlocked.Increment(ref _violationCount);
            if (throwOnViolation)
                throw new InvalidOperationException(
                    $"OptimumThreadGuard violation: value marked on thread {owner}, used on thread {current}.");
            return false;
        }

        return true;
    }
}
