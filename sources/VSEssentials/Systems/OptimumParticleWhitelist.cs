namespace Vintagestory.GameContent;

using System;
using Vintagestory.API.Common;

/// <summary>
/// Whitelist for survival-critical particle emitters (charcoal pits, forges, bloomeries, firepits, chimneys, bubbles)
/// whose visual smoke and fire feedback must not be culled by the particle distance gate.
/// </summary>
public static class OptimumParticleWhitelist
{
    public static bool IsCriticalSurvivalEmitter(Block? block)
    {
        if (block == null) return false;

        string? path = block.Code?.Path;
        if (!string.IsNullOrEmpty(path))
        {
            if (path.Contains("charcoalpit") ||
                path.Contains("forge") ||
                path.Contains("bloomery") ||
                path.Contains("firepit") ||
                path.Contains("chimney") ||
                path.Contains("bubble"))
            {
                return true;
            }
        }

        string? entityClass = block.EntityClass;
        if (!string.IsNullOrEmpty(entityClass))
        {
            if (entityClass.Equals("CharcoalPit", StringComparison.OrdinalIgnoreCase) ||
                entityClass.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
                entityClass.Equals("Bloomery", StringComparison.OrdinalIgnoreCase) ||
                entityClass.Equals("Firepit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
