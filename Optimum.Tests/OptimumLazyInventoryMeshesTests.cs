#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class OptimumLazyInventoryMeshesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void LazyInventoryMeshes_DefaultsTrue_AndConfigRoundTrips()
    {
        Assert.True(OptimumConfig.LazyInventoryMeshesEnabled);
        Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.LazyInventoryMeshes));

        string dir = Path.Combine(Path.GetTempPath(), "optimum-lazy-inv-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            OptimumConfig.SetDataPath(dir);
            OptimumConfig.LazyInventoryMeshesEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.LazyInventoryMeshesEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.LazyInventoryMeshesEnabled);

            OptimumConfig.LazyInventoryMeshesEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.LazyInventoryMeshesEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.LazyInventoryMeshesEnabled);
        }
        finally
        {
            OptimumConfig.LazyInventoryMeshesEnabled = true;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void PatcherRegistersLazyInventoryTypesMembersAndTargets()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "Optimum.Patcher/Program.cs"));

        string[] expectedEntries =
        [
            "\"Vintagestory.Client.NoObf.OptimumLazyInventoryMeshRequest\"",
            "\"lazyReloadedShapes\"",
            "\"EnsureUnknownItemModelRef\"",
            "\"EnsureUnknownBlockModelRef\"",
            "\"HasItemRenderVariant\"",
            "\"GetInventoryItemMeshRef\"",
            "\"EnsureBlockInventoryMeshRefOnMainThread\"",
            "\"EnsureItemInventoryMeshRefOnMainThread\"",
            "\"ResetLazyItemInventoryMeshes\"",
            "\"EnsureCompositeShapeLoaded\"",
            "\"EnsureLoadedForLazy\"",
            "\"ReUnloadTemporaryShapes\"",
            "new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"TesselateBlocksAndItems\", 0)",
            "new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"TesselateBlocksForInventory_ASync\", 1)",
            "new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"TesselateItems_Pre\", 1)",
            "new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"TesselateItems\", 3)",
            "new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"GetDefaultBlockMeshRef\", 1)",
            "new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"GetDefaultItemMeshRef\", 1)",
            "\"FillItemStackRenderInfo\"",
            "new(\"Vintagestory.Client.NoObf.InventoryItemRenderer\", \"GetItemStackRenderInfo\", 4)",
        ];

        foreach (string entry in expectedEntries)
        {
            Assert.Contains(entry, program);
        }
    }

    [Fact]
    public void PatchFilesContainLazyGuardsAndRamMode2ReloadHandling()
    {
        string root = RepoRoot();
        string stmPatch = File.ReadAllText(Path.Combine(root, "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShapeTesselatorManager.cs.patch"));
        string iirPatch = File.ReadAllText(Path.Combine(root, "patches/VintagestoryLib/Vintagestory.Client.NoObf/InventoryItemRenderer.cs.patch"));
        string reqSrc = File.ReadAllText(Path.Combine(root, "sources/VintagestoryLib/Vintagestory.Client.NoObf/OptimumLazyInventoryMeshRequest.cs"));

        Assert.Contains("OptimumConfig.LazyInventoryMeshesEnabled", stmPatch);
        Assert.Contains("RuntimeEnv.MainThreadId", stmPatch);
        Assert.Contains("Volatile.Read", stmPatch);
        Assert.Contains("Volatile.Write", stmPatch);
        Assert.Contains("OptimizeRamMode == 2", stmPatch);
        Assert.Contains("EnsureCompositeShapeLoaded", stmPatch);
        Assert.Contains("ReUnloadTemporaryShapes", stmPatch);
        Assert.Contains("ResetLazyItemInventoryMeshes", stmPatch);

        Assert.Contains("GetDefaultBlockMeshRef(itemstack.Block)", iirPatch);
        Assert.Contains("HasItemRenderVariant(itemstack.Item, num)", iirPatch);
        Assert.Contains("GetInventoryItemMeshRef(itemstack.Item, num)", iirPatch);

        Assert.Contains("ManualResetEventSlim", reqSrc);
        Assert.Contains("game.disposed || game.threadsShouldExit", reqSrc);
    }

    [Fact]
    public void PatchedVintagestoryLib_WiresLazyInventoryPaths_WhenBuilt()
    {
        string patchedPath = Path.Combine(RepoRoot(), "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll");
        if (!File.Exists(patchedPath))
        {
            return;
        }

        using var asm = AssemblyDefinition.ReadAssembly(patchedPath);
        var module = asm.MainModule;

        var reqType = module.GetType("Vintagestory.Client.NoObf.OptimumLazyInventoryMeshRequest");
        Assert.NotNull(reqType);

        var rendererType = module.GetType("Vintagestory.Client.NoObf.InventoryItemRenderer");
        Assert.NotNull(rendererType);

        var fillMethod = rendererType!.Methods.FirstOrDefault(m => m.Name == "FillItemStackRenderInfo");
        Assert.NotNull(fillMethod);
        var fillCalledNames = fillMethod!.Body.Instructions
            .Where(i => i.Operand is MethodReference)
            .Select(i => ((MethodReference)i.Operand).Name)
            .ToHashSet();
        Assert.Contains("GetDefaultBlockMeshRef", fillCalledNames);
        Assert.Contains("HasItemRenderVariant", fillCalledNames);
        Assert.Contains("GetInventoryItemMeshRef", fillCalledNames);

        var fillFieldNames = fillMethod.Body.Instructions
            .Where(i => i.Operand is FieldReference)
            .Select(i => ((FieldReference)i.Operand).Name)
            .ToHashSet();
        Assert.DoesNotContain("blockModelRefsInventory", fillFieldNames);
        Assert.DoesNotContain("itemModelRefsInventory", fillFieldNames);
        Assert.DoesNotContain("altItemModelRefsInventory", fillFieldNames);

        var publicGetInfo = rendererType.Methods.FirstOrDefault(m => m.Name == "GetItemStackRenderInfo" && m.Parameters.Count == 4);
        Assert.NotNull(publicGetInfo);
        Assert.Contains(publicGetInfo!.Body.Instructions,
            i => i.Operand is MethodReference mr && mr.Name == "FillItemStackRenderInfo");

        var stmType = module.GetType("Vintagestory.Client.NoObf.ShapeTesselatorManager");
        Assert.NotNull(stmType);
        foreach (string name in new[]
        {
            "TesselateBlocksForInventory_ASync",
            "TesselateItems",
            "TesselateBlocksAndItems",
            "GetDefaultBlockMeshRef",
            "GetDefaultItemMeshRef",
            "GetInventoryItemMeshRef",
            "HasItemRenderVariant",
            "EnsureBlockInventoryMeshRefOnMainThread",
            "EnsureItemInventoryMeshRefOnMainThread",
            "ResetLazyItemInventoryMeshes",
            "EnsureCompositeShapeLoaded",
            "ReUnloadTemporaryShapes",
        })
        {
            Assert.Contains(stmType!.Methods, m => m.Name == name);
        }
    }

    [Fact]
    public void ShapeTesselatorManager_CacheHitAndBounds_WorkWhenDonorBuilt()
    {
        string donorPath = Path.Combine(RepoRoot(), "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (!File.Exists(donorPath))
        {
            return;
        }

        Assembly donorAsm = Assembly.LoadFrom(donorPath);
        Type? stmType = donorAsm.GetType("Vintagestory.Client.NoObf.ShapeTesselatorManager");
        Assert.NotNull(stmType);

        object stm = RuntimeHelpers.GetUninitializedObject(stmType!);
        var cachedBlockRef = new MultiTextureMeshRef(Array.Empty<MeshRef>(), Array.Empty<int>());
        var cachedItemRef = new MultiTextureMeshRef(Array.Empty<MeshRef>(), Array.Empty<int>());
        var cachedAltRef = new MultiTextureMeshRef(Array.Empty<MeshRef>(), Array.Empty<int>());

        stmType.GetField("blockModelRefsInventory")!.SetValue(stm, new MultiTextureMeshRef?[] { null, null, cachedBlockRef, null });
        stmType.GetField("itemModelRefsInventory")!.SetValue(stm, new MultiTextureMeshRef?[] { null, null, null, cachedItemRef });
        var altRefs = new MultiTextureMeshRef?[4][];
        altRefs[3] = new MultiTextureMeshRef?[] { null, cachedAltRef };
        stmType.GetField("altItemModelRefsInventory")!.SetValue(stm, altRefs);

        MethodInfo getBlockRef = stmType.GetMethod("GetDefaultBlockMeshRef", BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo getItemRef = stmType.GetMethod("GetDefaultItemMeshRef", BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo getItemVarRef = stmType.GetMethod("GetInventoryItemMeshRef", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo hasVariant = stmType.GetMethod("HasItemRenderVariant", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;

        var block = new Block { BlockId = 2 };
        var uncachedBlock = new Block { BlockId = 1 };
        var item = new Item(3);
        var uncachedItem = new Item(1);

        Assert.Null(getBlockRef.Invoke(stm, new object?[] { null }));
        Assert.Null(getItemRef.Invoke(stm, new object?[] { null }));
        Assert.Same(cachedBlockRef, getBlockRef.Invoke(stm, new object[] { block }));
        Assert.Same(cachedItemRef, getItemRef.Invoke(stm, new object[] { item }));
        Assert.Same(cachedAltRef, getItemVarRef.Invoke(stm, new object[] { item, 2 }));
        Assert.Null(getItemVarRef.Invoke(stm, new object[] { item, -1 }));

        Assert.True((bool)hasVariant.Invoke(stm, new object[] { item, 0 })!);
        Assert.True((bool)hasVariant.Invoke(stm, new object[] { item, 1 })!);
        Assert.True((bool)hasVariant.Invoke(stm, new object[] { item, 2 })!);
        Assert.False((bool)hasVariant.Invoke(stm, new object[] { item, 3 })!);
        Assert.False((bool)hasVariant.Invoke(stm, new object[] { item, -1 })!);
        Assert.False((bool)hasVariant.Invoke(stm, new object[] { uncachedItem, 1 })!);

        bool prev = OptimumConfig.LazyInventoryMeshesEnabled;
        try
        {
            OptimumConfig.LazyInventoryMeshesEnabled = false;
            Assert.Null(getBlockRef.Invoke(stm, new object[] { uncachedBlock }));
            Assert.Null(getItemRef.Invoke(stm, new object[] { uncachedItem }));
            Assert.Same(cachedBlockRef, getBlockRef.Invoke(stm, new object[] { block }));
            Assert.Same(cachedItemRef, getItemRef.Invoke(stm, new object[] { item }));
        }
        finally
        {
            OptimumConfig.LazyInventoryMeshesEnabled = prev;
        }
    }
}
