using System;
using System.Collections.Generic;

namespace Vintagestory.API.Config;

/// <summary>
/// Identifies assemblies that Optimum can audit for parallel worldgen.
/// </summary>
public static class OptimumWorldgenSafetyGate
{
    private static readonly HashSet<string> SafeAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "VintagestoryLib",
        "VSEssentials",
        "VSSurvivalMod",
        "VSCreativeMod"
    };

    public static bool IsKnownSafeAssembly(string assemblyName)
    {
        return !string.IsNullOrWhiteSpace(assemblyName) && SafeAssemblyNames.Contains(assemblyName);
    }

    public static List<string> FindForeignAssemblies(IEnumerable<string> assemblyNames)
    {
        var foreign = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (assemblyNames == null)
        {
            return new List<string>();
        }

        foreach (string assemblyName in assemblyNames)
        {
            if (!IsKnownSafeAssembly(assemblyName))
            {
                foreign.Add(string.IsNullOrWhiteSpace(assemblyName) ? "<unknown>" : assemblyName);
            }
        }

        return new List<string>(foreign);
    }
}
