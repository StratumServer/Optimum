using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace Vintagestory.API.Config;

/// <summary>
/// Finds registered texture-source types that may carry mutable singleton state.
/// </summary>
public static class OptimumTesselationSafetyGate
{
    private static readonly HashSet<string> SafeAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "VintagestoryAPI",
        "VintagestoryLib",
        "VSEssentials",
        "VSSurvivalMod",
        "VSCreativeMod"
    };

    /// <summary>
    /// Returns foreign texture-source type names from the supplied registered types.
    /// </summary>
    public static List<string> FindForeignTextureSources(IEnumerable<Type> registeredTypes)
    {
        var foreignTypes = new SortedSet<string>(StringComparer.Ordinal);
        if (registeredTypes == null)
        {
            return new List<string>();
        }

        foreach (Type type in registeredTypes)
        {
            if (type == null || !typeof(ITexPositionSource).IsAssignableFrom(type))
            {
                continue;
            }

            string assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            if (!SafeAssemblyNames.Contains(assemblyName))
            {
                foreignTypes.Add(type.FullName ?? type.Name);
            }
        }

        return new List<string>(foreignTypes);
    }

    /// <summary>
    /// Caps tessellation workers when a foreign texture source can race.
    /// </summary>
    public static int CapWorkerCount(int requestedWorkers, IEnumerable<Type> registeredTypes)
    {
        int requested = Math.Max(1, requestedWorkers);
        return FindForeignTextureSources(registeredTypes).Count == 0 ? requested : 1;
    }
}
