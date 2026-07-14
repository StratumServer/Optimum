namespace Vintagestory.API.Config;

/// <summary>
/// Pure helpers for the animated-block LOD gate in AnimationUtil.
/// Kept free of game state so Optimum.Tests can pin the behavior.
/// </summary>
public static class OptimumAnimLod
{
    /// <summary>Stable 0-3 phase from the block position, spreads mid-tier updates across the stride window.</summary>
    public static int Phase(int x, int y, int z) => ((x * 3) ^ (y * 5) ^ (z * 7)) & 3;

    /// <summary>True when this mid-tier frame should run the animator.</summary>
    public static bool MidTierDue(int frameCounter, int phase) => ((frameCounter + phase) & 3) == 0;

    /// <summary>
    /// True when the per-frame animator budget has room this frame. Consumes
    /// one slot and resets the window on a new frame stamp. Budget &lt;= 0
    /// means uncapped and always returns true.
    /// </summary>
    public static bool BudgetWindow(long nowMs, ref long stampMs, ref int used, int budget)
    {
        if (budget <= 0) return true;

        if (nowMs != stampMs)
        {
            stampMs = nowMs;
            used = 0;
        }

        if (used >= budget) return false;

        used++;
        return true;
    }
}
