namespace Vintagestory.API.Config;

/// <summary>
/// Runtime config for Optimum optimizations that need cross-project
/// visibility (VintagestoryLib sets, forks read). VintagestoryLib syncs
/// these from ClientSettings at startup and on settings change.
/// </summary>
public static class OptimumConfig
{
    public static bool RepulsionGateEnabled = true;
    public static int RepulsionDistance = 64;
    public static double RepulsionDistanceSq = 64.0 * 64.0;

    public static bool AnimBlockLodEnabled = true;

    public static void SetRepulsionDistance(int blocks)
    {
        RepulsionDistance = blocks;
        RepulsionDistanceSq = (double)blocks * blocks;
    }
}
