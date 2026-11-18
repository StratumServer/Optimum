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

    [Fact]
    public void LazyInventoryMeshPrep_DefaultsTrue_ConfigAndDiagnosticsRoundTrip()
    {
        Assert.True(OptimumConfig.LazyInventoryMeshPrepEnabled);
        Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.LazyInventoryMeshPrep));

        string dir = Path.Combine(Path.GetTempPath(), "optimum-lazy-inv-prep-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool prevEnabled = OptimumConfig.LazyInventoryMeshPrepEnabled;
        OptimumDiagnostics.ResetInventoryMeshPrep();
        try
        {
            OptimumConfig.SetDataPath(dir);
            OptimumConfig.LazyInventoryMeshPrepEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.LazyInventoryMeshPrepEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.LazyInventoryMeshPrepEnabled);

            OptimumConfig.LazyInventoryMeshPrepEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.LazyInventoryMeshPrepEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.LazyInventoryMeshPrepEnabled);

            OptimumDiagnostics.RecordInventoryMeshPreparedHit();
            OptimumDiagnostics.RecordInventoryMeshBatchPrep();
            OptimumDiagnostics.RecordInventoryMeshLookaheadPrep();
            OptimumDiagnostics.RecordInventoryMeshSyncFallback();

            var metrics = OptimumDiagnostics.CaptureBenchmarkMetrics();
            Assert.Equal(1d, metrics["inventory.meshPrep.hits.count"]);
            Assert.Equal(1d, metrics["inventory.meshPrep.batchPreps.count"]);
            Assert.Equal(1d, metrics["inventory.meshPrep.lookaheadPreps.count"]);
            Assert.Equal(1d, metrics["inventory.meshPrep.syncFallbacks.count"]);

            OptimumDiagnostics.ResetInventoryMeshPrep();
            var cleared = OptimumDiagnostics.CaptureBenchmarkMetrics();
            Assert.Equal(0d, cleared["inventory.meshPrep.hits.count"]);
            Assert.Equal(0d, cleared["inventory.meshPrep.batchPreps.count"]);
            Assert.Equal(0d, cleared["inventory.meshPrep.lookaheadPreps.count"]);
            Assert.Equal(0d, cleared["inventory.meshPrep.syncFallbacks.count"]);
        }
        finally
        {
            OptimumConfig.LazyInventoryMeshPrepEnabled = prevEnabled;
            OptimumDiagnostics.ResetInventoryMeshPrep();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void InventorySearchBridge_AsciiAndUnicodeWeightsAndOrder_MatchSemantics()
    {
        var inv = new InventoryGeneric(6, "searchtest", "0", null);
        for (int i = 0; i < 6; i++)
        {
            inv[i].Itemstack = new ItemStack(new Item(i + 1) { Code = new AssetLocation($"game:item-{i}") });
        }

        var available = new Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot>();
        var rendered = new Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot>();
        for (int i = 0; i < 6; i++)
        {
            available.Add(i, inv[i]);
        }

        var names = new System.Collections.Generic.Dictionary<int, string>
        {
            [0] = "cast iron ingot",
            [1] = "iron",
            [2] = "ironwood plank",
            [3] = "raw iron",
            [4] = "bronze pickaxe",
            [5] = "медная кирка",
        };
        var cached = new System.Collections.Generic.Dictionary<int, string>
        {
            [0] = "forged cast iron ingot",
            [1] = "pure iron",
            [2] = "dense ironwood plank",
            [3] = "raw iron ore",
            [4] = "iron-tipped bronze pickaxe",
            [5] = "прочная медная кирка",
        };

        string? searchText = null;
        int rows = 0;
        OptimumApiBridge.FilterSlotGridItems(
            ref searchText,
            available,
            rendered,
            inv,
            null,
            5,
            ref rows,
            "iron",
            cached,
            names);

        // Expected weight order: [1] exact=0, [3] end-word=0.25, [0] mid-word=0.5, [2] prefix=0.75, [4] desc-contains=3.
        Assert.Equal(new[] { 1, 3, 0, 2, 4 }, rendered.Keys.ToArray());

        OptimumApiBridge.FilterSlotGridItems(
            ref searchText,
            available,
            rendered,
            inv,
            null,
            5,
            ref rows,
            "КИРКА",
            cached,
            names);

        Assert.Equal(new[] { 5 }, rendered.Keys.ToArray());
    }

    [Fact]
    public void InventoryMeshPreparer_PinCompositeShape_RefCountsAcrossOverlappingBatches()
    {
        string donorPath = Path.Combine(RepoRoot(), "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (!File.Exists(donorPath))
        {
            return;
        }

        Assembly donorAsm = Assembly.LoadFrom(donorPath);
        Type stmType = donorAsm.GetType("Vintagestory.Client.NoObf.ShapeTesselatorManager")!;
        Type clientMainType = donorAsm.GetType("Vintagestory.Client.NoObf.ClientMain")!;
        Type unloadableShapeType = donorAsm.GetType("Vintagestory.Client.NoObf.UnloadableShape")!;

        object stm = RuntimeHelpers.GetUninitializedObject(stmType);
        object game = RuntimeHelpers.GetUninitializedObject(clientMainType);
        stmType.GetField("game", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(stm, game);

        var loadedField = unloadableShapeType.GetField("Loaded", BindingFlags.Instance | BindingFlags.Public)!;
        var keepLoadedField = unloadableShapeType.GetField("keepLoaded", BindingFlags.Instance | BindingFlags.Public)!;
        var elementsField = unloadableShapeType.GetField("Elements", BindingFlags.Instance | BindingFlags.Public)!;

        object sharedShape = Activator.CreateInstance(unloadableShapeType)!;
        loadedField.SetValue(sharedShape, true);
        keepLoadedField.SetValue(sharedShape, false);
        elementsField.SetValue(sharedShape, Array.Empty<ShapeElement>());

        object transientShape = Activator.CreateInstance(unloadableShapeType)!;
        loadedField.SetValue(transientShape, true);
        keepLoadedField.SetValue(transientShape, false);
        elementsField.SetValue(transientShape, Array.Empty<ShapeElement>());

        var dictType = typeof(Vintagestory.API.Datastructures.OrderedDictionary<,>).MakeGenericType(typeof(AssetLocation), unloadableShapeType);
        object shapesDict = Activator.CreateInstance(dictType)!;
        var setItem = dictType.GetProperty("Item", new[] { typeof(AssetLocation) })!;
        var sharedLoc = new AssetLocation("game:block/basic/cube");
        var transientLoc = new AssetLocation("game:block/basic/transient");
        setItem.SetValue(shapesDict, sharedShape, new object[] { sharedLoc });
        setItem.SetValue(shapesDict, transientShape, new object[] { transientLoc });
        stmType.GetField("shapes", BindingFlags.Instance | BindingFlags.Public)!.SetValue(stm, shapesDict);

        MethodInfo prepMethod = stmType.GetMethod("EnsureInventoryMeshPreparer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object preparer = prepMethod.Invoke(stm, null)!;
        MethodInfo pinMethod = preparer.GetType().GetMethod("PinCompositeShape", BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo unpinMethod = preparer.GetType().GetMethod("UnpinShapes", BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo unloadUnpinnedMethod = preparer.GetType().GetMethod("UnloadAllUnpinnedTemporaryShapes", BindingFlags.Instance | BindingFlags.Public)!;
        FieldInfo overrideField = preparer.GetType().GetField("ShapePinningModeOverride", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        int prevOverride = (int)overrideField.GetValue(preparer)!;
        try
        {
            overrideField.SetValue(preparer, 2);
            var composite = new CompositeShape { Base = sharedLoc, Format = EnumShapeFormat.VintageStory };
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(unloadableShapeType);
            System.Collections.IList batchA = (System.Collections.IList)Activator.CreateInstance(listType)!;
            System.Collections.IList batchB = (System.Collections.IList)Activator.CreateInstance(listType)!;

            pinMethod.Invoke(preparer, new object?[] { composite, batchA });
            pinMethod.Invoke(preparer, new object?[] { composite, batchB });
            Assert.Single(batchA);
            Assert.Single(batchB);

            // LoadDone() must unload unpinned transientShape while leaving pinned sharedShape loaded.
            unloadUnpinnedMethod.Invoke(preparer, null);
            Assert.False((bool)loadedField.GetValue(transientShape)!);
            Assert.True((bool)loadedField.GetValue(sharedShape)!);

            // Unpinning batchA keeps sharedShape loaded while batchB still holds a pin.
            unpinMethod.Invoke(preparer, new object?[] { batchA });
            Assert.True((bool)loadedField.GetValue(sharedShape)!);

            // Unpinning batchB drops the refcount to zero and unloads sharedShape.
            unpinMethod.Invoke(preparer, new object?[] { batchB });
            Assert.False((bool)loadedField.GetValue(sharedShape)!);
        }
        finally
        {
            overrideField.SetValue(preparer, prevOverride);
        }
    }

    [Fact]
    public void PatchedAssemblies_WireIssue651InventoryMeshPrepAndSlotGrid_WhenBuilt()
    {
        string patchedLib = Path.Combine(RepoRoot(), "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll");
        string patchedApi = Path.Combine(RepoRoot(), "bin/Release/net10.0/VintagestoryAPI-patched.dll");
        if (!File.Exists(patchedLib) || !File.Exists(patchedApi))
        {
            return;
        }

        using (var libAsm = AssemblyDefinition.ReadAssembly(patchedLib))
        {
            var libMod = libAsm.MainModule;
            Assert.NotNull(libMod.GetType("Vintagestory.Client.NoObf.OptimumInventoryMeshPreparer"));

            var stmType = libMod.GetType("Vintagestory.Client.NoObf.ShapeTesselatorManager");
            Assert.NotNull(stmType);
            Assert.Contains(stmType!.Methods, m => m.Name == "PrepareSlotGridWindow");
            Assert.Contains(stmType.Methods, m => m.Name == "BeginInitialInventoryMeshPrefetch");

            var startupType = libMod.GetType("Vintagestory.Client.NoObf.ClientSystemStartup");
            var onJoined = startupType!.Methods.Single(m => m.Name == "OnAllAssetsLoadedAndClientJoined");
            Assert.Contains(onJoined.Body.Instructions, i => i.Operand is MethodReference mr && mr.Name == "BeginInitialInventoryMeshPrefetch");

            var guiMgr = libMod.GetType("Vintagestory.Client.NoObf.GuiManager");
            var onPlayerData = guiMgr!.Methods.Single(m => m.Name == "OnOwnPlayerDataReceived");
            Assert.Contains(onPlayerData.Body.Instructions, i => i.Operand is MethodReference mr && mr.Name == "BeginInitialInventoryMeshPrefetch");
        }

        using (var apiAsm = AssemblyDefinition.ReadAssembly(patchedApi))
        {
            var apiMod = apiAsm.MainModule;
            var gridType = apiMod.GetType("Vintagestory.API.Client.GuiElementItemSlotGridBase");
            Assert.NotNull(gridType);

            var renderMethod = gridType!.Methods.Single(m => m.Name == "RenderInteractiveElements");
            Assert.Contains(renderMethod.Body.Instructions, i => i.Operand is MethodReference mr && mr.Name == "PrepareSlotGridInventoryMeshes");

            var filterMethod = gridType.Methods.Single(m => m.Name == "FilterItemsBySearchText");
            Assert.Contains(filterMethod.Body.Instructions, i => i.Operand is MethodReference mr && mr.Name == "TryComposeFilteredSlotGrid");

            var postRender = gridType.Methods.Single(m => m.Name == "PostRenderInteractiveElements");
            Assert.Contains(postRender.Body.Instructions, i => i.Operand is FieldReference fr && fr.Name == "renderedSlots");

            var composeOverlays = gridType.Methods.Single(m => m.Name == "ComposeSlotOverlays");
            Assert.Contains(composeOverlays.Body.Instructions, i => i.Operand is FieldReference fr && fr.Name == "renderedSlots");
        }
    }
}
