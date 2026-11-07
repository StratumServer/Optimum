using System;
using System.IO;
using System.Linq;
using Xunit;
#if !NO_DONOR
using Mono.Cecil;
using Mono.Cecil.Cil;
#endif

namespace Optimum.Tests;

public sealed class InventoryCraftingGridTests
{
    [Fact]
    public void InventoryCraftingGridPatch_UsesHashSetDeduplication()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Common/InventoryCraftingGrid.cs.patch");
        Assert.True(File.Exists(patchPath), "InventoryCraftingGrid.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("HashSet<GridRecipe> seen = new HashSet<GridRecipe>();", patched);
        Assert.Contains("seen.Add(gridRecipe)", patched);
        Assert.DoesNotContain("list.AddIfNotPresent(gridRecipe)", patched);
    }

#if !NO_DONOR
    [Fact]
    public void FindMatchingRecipe_TransplantedWithHashSetDeduplication()
    {
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, "VintagestoryLib.dll");
        if (!File.Exists(assemblyPath))
        {
            assemblyPath = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        }
        if (!File.Exists(assemblyPath)) return;

        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var type = assembly.MainModule.GetType("Vintagestory.Common.InventoryCraftingGrid");
        Assert.NotNull(type);

        var method = type.Methods.FirstOrDefault(m => m.Name == "FindMatchingRecipe");
        Assert.NotNull(method);
        Assert.True(method.HasBody);

        // Verify HashSet.Add is invoked in FindMatchingRecipe
        bool usesHashSetAdd = method.Body.Instructions.Any(inst =>
            inst.OpCode == OpCodes.Callvirt &&
            inst.Operand is MethodReference target &&
            target.Name == "Add" &&
            target.DeclaringType.Name.StartsWith("HashSet"));

        Assert.True(usesHashSetAdd, "FindMatchingRecipe must call HashSet.Add for candidate deduplication");
    }
#endif
}
