using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vanilla ModLoader hands instantiateMods a list already ordered by dependency (libraries before
/// the mods that need them) and orders it with the stable LINQ OrderBy: by RequiresCompilation,
/// then the mod systems by ExecuteOrder, and CollectMods by file name. List&lt;T&gt;.Sort is an
/// in-place introsort, unstable above 16 elements, so using it moved libraries behind their
/// dependents. A run with 25 mods instantiated 14 mods before one of their dependencies.
/// </summary>
public sealed class ModLoaderStableOrderTests
{
    private const string PatchPath = "patches/VintagestoryLib/Vintagestory.Common/ModLoader.cs.patch";

    private static string PatchedModLoader() =>
        PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(PatchPath));

    [Fact]
    public void ModLoaderDoesNotUseTheUnstableListSortOnModsOrSystems()
    {
        string source = PatchedModLoader();

        Assert.DoesNotContain("mods.Sort(", source);
        Assert.DoesNotContain("list.Sort(", source);
        Assert.DoesNotContain("result.Sort(", source);
    }

    [Fact]
    public void ModLoaderOrdersWithTheStableOrderByVanillaUses()
    {
        string source = PatchedModLoader();

        Assert.Contains("mods = mods.OrderBy(KeyModRequiresCompilation).ToList();", source);
        Assert.Contains("list = list.OrderBy(KeyModSystemExecuteOrder).ToList();", source);
        Assert.Contains("return result.OrderBy(KeyModFileName, StringComparer.OrdinalIgnoreCase).ToList();", source);
    }

    [Fact]
    public void TheKeySelectorsMatchWhatVanillasLambdasReturned()
    {
        string source = PatchedModLoader();

        Assert.Contains("return mod.RequiresCompilation;", source);
        Assert.Contains("return mod.FileName;", source);
        Assert.Contains("return system.ExecuteOrder();", source);
    }

    [Fact]
    public void OrderByKeepsEqualKeysInTheirDependencyOrderForMoreThan16Elements()
    {
        // The invariant the patch relies on: 17 or more elements with the same key stay in place.
        var dependencyOrder = Enumerable.Range(0, 25).Select(i => (Id: i, RequiresCompilation: false)).ToList();

        List<int> ordered = dependencyOrder.OrderBy(mod => mod.RequiresCompilation).Select(mod => mod.Id).ToList();

        Assert.Equal(Enumerable.Range(0, 25), ordered);
    }
}
