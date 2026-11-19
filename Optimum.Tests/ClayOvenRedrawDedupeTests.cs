#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public sealed class ClayOvenRedrawDedupeTests
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
    public void ClayOvenPatchesAndManifest_WireVisualStateDeduplication()
    {
        string root = RepoRoot();
        string sourcePatch = File.ReadAllText(Path.Combine(
            root, "patches/VSSurvivalMod/Systems/Cooking/Clayoven/BEClayOven.cs.patch"));
        string runtimePatch = File.ReadAllText(Path.Combine(
            root, "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityOven.cs.patch"));
        string modPatcher = File.ReadAllText(Path.Combine(
            root, "Optimum.Patcher/mod-patcher.cs"));

        Assert.Contains("UpdateOptimumVisualState", sourcePatch);
        Assert.Contains("optimumLastRotationDeg", sourcePatch);
        Assert.Contains("optimumLastSlotHeightMuls", sourcePatch);

        Assert.Contains("UpdateOptimumVisualState", runtimePatch);
        Assert.Contains("optimumLastRotationDeg", runtimePatch);
        Assert.Contains("optimumLastSlotHeightMuls", runtimePatch);

        Assert.Contains("\"Vintagestory.GameContent.BlockEntityOven\"", modPatcher);
        Assert.Contains("\"UpdateOptimumVisualState\"", modPatcher);
        Assert.Contains("new(\"Vintagestory.GameContent.BlockEntityOven\", \"FromTreeAttributes\", 2)", modPatcher);
    }

    [Fact]
    public void SourceBuild_ClayOvenRedrawDedupe_SkipsUnchangedSyncsAndRedrawsAllVisualChanges()
    {
        string root = RepoRoot();
        string sourceDll = Path.Combine(root, "bin/Release/net10.0/VSSurvivalMod.dll");
        if (!File.Exists(sourceDll))
        {
            sourceDll = Path.Combine(AppContext.BaseDirectory, "VSSurvivalMod.dll");
        }
        Assert.True(File.Exists(sourceDll), $"Missing VSSurvivalMod.dll at {sourceDll}");

        RunClayOvenBehavioralSuite(sourceDll, Path.GetDirectoryName(sourceDll)!);
    }

    [Fact]
    public void AssembledRuntimeDonor_ClayOvenRedrawDedupe_SkipsUnchangedSyncsAndRedrawsAllVisualChanges()
    {
        string root = RepoRoot();
        string vanillaPath = Path.Combine(root, ".vanilla/win-x64/runtime-donors/Mods/VSSurvivalMod.dll");
        string compiledPath = Path.Combine(root, ".build/runtime-donors/VSSurvivalMod/bin/Release/net10.0/VSSurvivalMod.dll");
        if (!File.Exists(vanillaPath) || !File.Exists(compiledPath))
        {
            return;
        }

        string tempOut = Path.Combine(Path.GetTempPath(), $"optimum_clayoven_669_{Guid.NewGuid():N}.dll");
        try
        {
            Assert.True(Optimum.Patcher.ModPatcher.Patch("vssurvivalmod", vanillaPath, compiledPath, tempOut));
            Assert.True(File.Exists(tempOut));

            RunClayOvenBehavioralSuite(tempOut, Path.Combine(root, "bin/Release/net10.0"));
        }
        finally
        {
            try
            {
                if (File.Exists(tempOut)) File.Delete(tempOut);
            }
            catch
            {
                // Ignore temp cleanup failure on locked handles.
            }
        }
    }

    private static void RunClayOvenBehavioralSuite(string targetSurvivalDll, string survivalDir)
    {
        var alc = new AssemblyLoadContext("ClayOvenRedrawTest", isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            if (name.Name == "VintagestoryAPI") return typeof(ICoreAPI).Assembly;
            if (name.Name == "Optimum.Api.Contracts") return typeof(OptimumConfig).Assembly;
            if (name.Name == "VSEssentials") return typeof(Vintagestory.GameContent.InWorldContainer).Assembly;
            string candidate = Path.Combine(survivalDir, name.Name + ".dll");
            if (File.Exists(candidate)) return alc.LoadFromAssemblyPath(candidate);
            string baseCandidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (File.Exists(baseCandidate)) return alc.LoadFromAssemblyPath(baseCandidate);
            return null;
        };

        try
        {
            Assembly asm = alc.LoadFromAssemblyPath(targetSurvivalDll);
            Type ovenType = asm.GetType("Vintagestory.GameContent.BlockEntityOven")!;
            Assert.NotNull(ovenType);

            var doughItem = CreateBakeableItem(101, "dough-spelt", largeItem: false);
            var partBakedItem = CreateBakeableItem(102, "bread-spelt-partbaked", largeItem: false);
            var breadItem = CreateBakeableItem(103, "bread-spelt-perfect", largeItem: false);
            var firewoodItem = CreateFirewoodItem(104, "firewood");
            Type containedMeshSourceType = asm.GetType("Vintagestory.GameContent.IContainedMeshSource")!;
            Assert.NotNull(containedMeshSourceType);
            var (pieItem, setPieKey) = CreateDynamicMeshKeyBakeableItem(
                containedMeshSourceType, 105, "pie-raw", largeItem: true, "pie-raw-crust-apple");

            var itemsById = new Dictionary<int, Item>
            {
                [doughItem.ItemId] = doughItem,
                [partBakedItem.ItemId] = partBakedItem,
                [breadItem.ItemId] = breadItem,
                [firewoodItem.ItemId] = firewoodItem,
                [pieItem.ItemId] = pieItem,
            };

            // 1) Multiple filled ovens receiving unchanged 500ms syncs: 0 chunk redraws.
            {
                var harness = new OvenHarness(ovenType, itemsById);
                var ovens = new BlockEntity[4];
                for (int o = 0; o < ovens.Length; o++)
                {
                    ovens[o] = harness.CreateInitializedOven(new BlockPos(10 + o, 64, 10), "north");
                    harness.SyncOven(ovens[o], new[] { new ItemStack(breadItem, 1), new ItemStack(breadItem, 1), new ItemStack(breadItem, 1), new ItemStack(breadItem, 1) },
                        new[] { 1f, 1f, 1f, 1f }, burning: false, rotationDeg: 90, ovenTemp: 180f, fuelBurnTime: 0f);
                }

                // Initial transition from empty -> filled redrew each of the 4 ovens once.
                Assert.Equal(4, harness.MarkBlockDirtyCount);
                harness.MarkBlockDirtyCount = 0;

                // 20 consecutive 500ms syncs (10 seconds) per oven with cooling temperature only.
                for (int sync = 0; sync < 20; sync++)
                {
                    float temp = 180f - sync * 2.5f;
                    for (int o = 0; o < ovens.Length; o++)
                    {
                        harness.SyncOven(ovens[o], new[] { new ItemStack(breadItem, 1), new ItemStack(breadItem, 1), new ItemStack(breadItem, 1), new ItemStack(breadItem, 1) },
                            new[] { 1f, 1f, 1f, 1f }, burning: false, rotationDeg: 90, ovenTemp: temp, fuelBurnTime: 0f);
                    }
                }

                Assert.Equal(0, harness.MarkBlockDirtyCount);
                float lastTemp = (float)ovenType.GetField("ovenTemperature")!.GetValue(ovens[0])!;
                Assert.Equal(180f - 19 * 2.5f, lastTemp, precision: 3);
            }

            // 2) Rising dough (CurHeightMul), finished bread transition, and rotationDeg all redraw.
            {
                var harness = new OvenHarness(ovenType, itemsById);
                var oven = harness.CreateInitializedOven(new BlockPos(20, 64, 20), "north");
                Assert.Equal(0, harness.MarkBlockDirtyCount);

                // Place dough at heightMul = 0.50 -> redraws (1)
                harness.SyncOven(oven, new[] { new ItemStack(doughItem, 1) }, new[] { 0.50f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 90, ovenTemp: 220f, fuelBurnTime: 0f);
                Assert.Equal(1, harness.MarkBlockDirtyCount);

                // Unchanged heightMul = 0.50 while heating -> 0 redraws
                harness.SyncOven(oven, new[] { new ItemStack(doughItem, 1) }, new[] { 0.50f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 90, ovenTemp: 215f, fuelBurnTime: 0f);
                Assert.Equal(1, harness.MarkBlockDirtyCount);

                // Dough rises: heightMul 0.50 -> 0.58 -> redraws (2)
                harness.SyncOven(oven, new[] { new ItemStack(doughItem, 1) }, new[] { 0.58f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 90, ovenTemp: 210f, fuelBurnTime: 0f);
                Assert.Equal(2, harness.MarkBlockDirtyCount);

                // Dough rises again: heightMul 0.58 -> 0.72 -> redraws (3)
                harness.SyncOven(oven, new[] { new ItemStack(doughItem, 1) }, new[] { 0.72f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 90, ovenTemp: 205f, fuelBurnTime: 0f);
                Assert.Equal(3, harness.MarkBlockDirtyCount);

                // Transitions to partbaked bread -> redraws (4)
                harness.SyncOven(oven, new[] { new ItemStack(partBakedItem, 1) }, new[] { 0.85f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 90, ovenTemp: 200f, fuelBurnTime: 0f);
                Assert.Equal(4, harness.MarkBlockDirtyCount);

                // Transitions to finished bread -> redraws (5)
                harness.SyncOven(oven, new[] { new ItemStack(breadItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 90, ovenTemp: 195f, fuelBurnTime: 0f);
                Assert.Equal(5, harness.MarkBlockDirtyCount);

                // Finished bread sits unchanged -> 0 redraws
                harness.SyncOven(oven, new[] { new ItemStack(breadItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 90, ovenTemp: 190f, fuelBurnTime: 0f);
                Assert.Equal(5, harness.MarkBlockDirtyCount);

                // Rotation changes (90 -> 180) -> redraws (6)
                harness.SyncOven(oven, new[] { new ItemStack(breadItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 180, ovenTemp: 188f, fuelBurnTime: 0f);
                Assert.Equal(6, harness.MarkBlockDirtyCount);

                // Same rotation (180) -> 0 redraws
                harness.SyncOven(oven, new[] { new ItemStack(breadItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 180, ovenTemp: 185f, fuelBurnTime: 0f);
                Assert.Equal(6, harness.MarkBlockDirtyCount);
            }

            // 3) Firewood stack sizes (1x -> 2x -> 6x), burning transitions, and steady burning syncs.
            {
                var harness = new OvenHarness(ovenType, itemsById);
                var oven = harness.CreateInitializedOven(new BlockPos(30, 64, 30), "east");

                // Add 1 firewood -> redraws (1)
                harness.SyncOven(oven, new[] { new ItemStack(firewoodItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 0, ovenTemp: 20f, fuelBurnTime: 0f);
                Assert.Equal(1, harness.MarkBlockDirtyCount);

                // Add 2nd firewood -> redraws (2)
                harness.SyncOven(oven, new[] { new ItemStack(firewoodItem, 2) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 0, ovenTemp: 20f, fuelBurnTime: 0f);
                Assert.Equal(2, harness.MarkBlockDirtyCount);

                // Fill to 6 firewood -> redraws (3)
                harness.SyncOven(oven, new[] { new ItemStack(firewoodItem, 6) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 0, ovenTemp: 20f, fuelBurnTime: 0f);
                Assert.Equal(3, harness.MarkBlockDirtyCount);

                // Idle with 6 firewood -> 0 redraws
                harness.SyncOven(oven, new[] { new ItemStack(firewoodItem, 6) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 0, ovenTemp: 20f, fuelBurnTime: 0f);
                Assert.Equal(3, harness.MarkBlockDirtyCount);

                // Ignite (burning false -> true) -> clientSidePrevBurning path triggers MarkDirty(true) (4)
                harness.SyncOven(oven, new[] { new ItemStack(firewoodItem, 6) }, new[] { 1f, 1f, 1f, 1f },
                    burning: true, rotationDeg: 0, ovenTemp: 80f, fuelBurnTime: 70f);
                Assert.Equal(4, harness.MarkBlockDirtyCount);

                // Burning continues while temperature rises and fuelBurnTime falls -> 0 redraws
                harness.SyncOven(oven, new[] { new ItemStack(firewoodItem, 6) }, new[] { 1f, 1f, 1f, 1f },
                    burning: true, rotationDeg: 0, ovenTemp: 150f, fuelBurnTime: 55f);
                harness.SyncOven(oven, new[] { new ItemStack(firewoodItem, 6) }, new[] { 1f, 1f, 1f, 1f },
                    burning: true, rotationDeg: 0, ovenTemp: 240f, fuelBurnTime: 30f);
                Assert.Equal(4, harness.MarkBlockDirtyCount);

                // Burnout (burning true -> false, fuel cleared) -> redraws (5)
                harness.SyncOven(oven, Array.Empty<ItemStack?>(), new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 0, ovenTemp: 280f, fuelBurnTime: 0f);
                Assert.Equal(5, harness.MarkBlockDirtyCount);

                // Empty hot oven cooling down -> 0 redraws
                harness.SyncOven(oven, Array.Empty<ItemStack?>(), new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 0, ovenTemp: 260f, fuelBurnTime: 0f);
                Assert.Equal(5, harness.MarkBlockDirtyCount);
            }

            // 4) Custom IContainedMeshSource key change (pie state) and client prediction before sync.
            {
                var harness = new OvenHarness(ovenType, itemsById);
                var oven = harness.CreateInitializedOven(new BlockPos(40, 64, 40), "south");

                // Simulate client-side interaction mutating ovenInv[0] before server sync arrives
                var invProp = ovenType.GetProperty("Inventory")!;
                var inv = (InventoryBase)invProp.GetValue(oven)!;
                inv[0].Itemstack = new ItemStack(pieItem, 1);

                // Server sync confirms the pie: must still redraw against the last synced (empty) state!
                harness.SyncOven(oven, new[] { new ItemStack(pieItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 270, ovenTemp: 200f, fuelBurnTime: 0f);
                Assert.Equal(1, harness.MarkBlockDirtyCount);

                // Same IContainedMeshSource key -> 0 redraws
                harness.SyncOven(oven, new[] { new ItemStack(pieItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 270, ovenTemp: 195f, fuelBurnTime: 0f);
                Assert.Equal(1, harness.MarkBlockDirtyCount);

                // Pie mesh key changes (e.g. crust browned) with same ItemId and CurHeightMul -> redraws (2)
                setPieKey("pie-baked-crust-apple");
                harness.SyncOven(oven, new[] { new ItemStack(pieItem, 1) }, new[] { 1f, 1f, 1f, 1f },
                    burning: false, rotationDeg: 270, ovenTemp: 190f, fuelBurnTime: 0f);
                Assert.Equal(2, harness.MarkBlockDirtyCount);
            }
        }
        finally
        {
            alc.Unload();
        }
    }

    private static Item CreateBakeableItem(int id, string code, bool largeItem)
    {
        var attrs = new JObject
        {
            ["bakingProperties"] = new JObject
            {
                ["temp"] = 160,
                ["levelFrom"] = 0.0,
                ["levelTo"] = 1.0,
                ["startScaleY"] = 0.5,
                ["endScaleY"] = 1.0,
                ["largeItem"] = largeItem,
            }
        };
        return new Item
        {
            ItemId = id,
            Code = new AssetLocation("game", code),
            Attributes = new JsonObject(attrs),
        };
    }

    private static Item CreateFirewoodItem(int id, string code)
    {
        var attrs = new JObject
        {
            ["isClayOvenFuel"] = true
        };
        return new Item
        {
            ItemId = id,
            Code = new AssetLocation("game", code),
            Attributes = new JsonObject(attrs),
            MaxStackSize = 32,
        };
    }

    private static (Item Item, Action<string> SetKey) CreateDynamicMeshKeyBakeableItem(
        Type containedMeshSourceType, int id, string code, bool largeItem, string initialKey)
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"ClayOvenDynamicItem_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.RunAndCollect);
        var mb = ab.DefineDynamicModule("Main");
        var tb = mb.DefineType(
            "DynamicMeshKeyItem",
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(Item),
            new[] { containedMeshSourceType });

        var keyField = tb.DefineField("DynamicMeshKey", typeof(string), FieldAttributes.Public);

        var genMesh = tb.DefineMethod(
            "GenMesh",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
            typeof(MeshData),
            new[] { typeof(ItemSlot), typeof(ITextureAtlasAPI), typeof(BlockPos) });
        var genIl = genMesh.GetILGenerator();
        genIl.Emit(OpCodes.Ldnull);
        genIl.Emit(OpCodes.Ret);

        var getKey = tb.DefineMethod(
            "GetMeshCacheKey",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
            typeof(string),
            new[] { typeof(ItemSlot) });
        var keyIl = getKey.GetILGenerator();
        keyIl.Emit(OpCodes.Ldarg_0);
        keyIl.Emit(OpCodes.Ldfld, keyField);
        keyIl.Emit(OpCodes.Ret);

        Type dynamicType = tb.CreateType()!;
        var item = (Item)Activator.CreateInstance(dynamicType)!;
        var attrs = new JObject
        {
            ["bakingProperties"] = new JObject
            {
                ["temp"] = 160,
                ["levelFrom"] = 0.0,
                ["levelTo"] = 1.0,
                ["startScaleY"] = 1.0,
                ["endScaleY"] = 1.0,
                ["largeItem"] = largeItem,
            }
        };
        item.ItemId = id;
        item.Code = new AssetLocation("game", code);
        item.Attributes = new JsonObject(attrs);

        FieldInfo runtimeField = dynamicType.GetField("DynamicMeshKey")!;
        runtimeField.SetValue(item, initialKey);
        return (item, newKey => runtimeField.SetValue(item, newKey));
    }

    private sealed class OvenHarness
    {
        private readonly Type _ovenType;
        private readonly ICoreClientAPI _capi;
        private readonly IClientWorldAccessor _world;

        public int MarkBlockDirtyCount { get; set; }

        public OvenHarness(Type ovenType, Dictionary<int, Item> itemsById)
        {
            _ovenType = ovenType;
            var loadedSound = CreateProxy<ILoadedSound>((m, _) => m.Name switch
            {
                "get_IsPlaying" => false,
                _ => DefaultValue(m.ReturnType)
            });

            var blockAccessor = CreateProxy<IBlockAccessor>((m, _) =>
            {
                if (m.Name == "MarkBlockDirty")
                {
                    MarkBlockDirtyCount++;
                }
                return DefaultValue(m.ReturnType);
            });

            var eventApi = CreateProxy<IClientEventAPI>((m, _) => DefaultValue(m.ReturnType));
            var invNetUtil = CreateProxy<IInventoryNetworkUtil>((m, _) => DefaultValue(m.ReturnType));
            var modLoader = CreateProxy<IModLoader>((m, _) => DefaultValue(m.ReturnType));
            var classRegistry = CreateProxy<IClassRegistryAPI>((m, _) => m.Name switch
            {
                "CreateInvNetworkUtil" => invNetUtil,
                _ => DefaultValue(m.ReturnType)
            });

            ICoreClientAPI capi = null!;
            IClientWorldAccessor world = CreateProxy<IClientWorldAccessor>((m, args) => m.Name switch
            {
                "get_Api" => capi,
                "get_Side" => EnumAppSide.Client,
                "get_BlockAccessor" => blockAccessor,
                "GetItem" when args is { Length: 1 } && args[0] is int itemId =>
                    itemsById.TryGetValue(itemId, out Item? item) ? item : null,
                "LoadSound" => loadedSound,
                _ => DefaultValue(m.ReturnType)
            });

            capi = CreateProxy<ICoreClientAPI>((m, _) => m.Name switch
            {
                "get_Side" => EnumAppSide.Client,
                "get_World" => world,
                "get_Event" => eventApi,
                "get_ClassRegistry" => classRegistry,
                "get_ModLoader" => modLoader,
                _ => DefaultValue(m.ReturnType)
            });

            _capi = capi;
            _world = world;

            FieldInfo? collectibleApiField = typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (Item item in itemsById.Values)
            {
                collectibleApiField?.SetValue(item, capi);
                item.OnLoaded(capi);
            }
        }

        public BlockEntity CreateInitializedOven(BlockPos pos, string side)
        {
            var oven = (BlockEntity)Activator.CreateInstance(_ovenType)!;
            oven.Pos = pos;
            oven.Block = new Block
            {
                Code = new AssetLocation("game", "clayoven-" + side),
                Variant = new Vintagestory.API.Util.RelaxedReadOnlyDictionary<string, string>(
                    new Dictionary<string, string> { ["side"] = side })
            };
            oven.Initialize(_capi);
            return oven;
        }

        public void SyncOven(
            BlockEntity oven,
            ItemStack?[] stacks,
            float[] heightMuls,
            bool burning,
            int rotationDeg,
            float ovenTemp,
            float fuelBurnTime)
        {
            var tree = new TreeAttribute();
            tree.SetInt("posx", oven.Pos.X);
            tree.SetInt("posy", oven.Pos.Y);
            tree.SetInt("posz", oven.Pos.Z);
            tree.SetInt("burn", burning ? 1 : 0);
            tree.SetInt("rota", rotationDeg);
            tree.SetFloat("temp", ovenTemp);
            tree.SetFloat("tfuel", fuelBurnTime);

            var slotsTree = new TreeAttribute();
            for (int i = 0; i < stacks.Length; i++)
            {
                if (stacks[i] != null)
                {
                    slotsTree[i.ToString()] = new ItemstackAttribute(stacks[i]);
                }
            }
            tree["slots"] = slotsTree;
            tree.SetInt("qslots", 5);

            for (int i = 0; i < 4; i++)
            {
                float hm = i < heightMuls.Length ? heightMuls[i] : 1f;
                tree.SetFloat("brown" + i, 160f);
                tree.SetFloat("tbake" + i, 100f);
                tree.SetFloat("baked" + i, 0f);
                tree.SetFloat("heightmul" + i, hm);
                tree.SetFloat("temp" + i, ovenTemp);
            }

            oven.FromTreeAttributes(tree, _world);
        }
    }

    private static T CreateProxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, OvenProxy>();
        ((OvenProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultValue(Type returnType)
    {
        if (returnType == typeof(void)) return null;
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    public class OvenProxy : DispatchProxy
    {
        public System.Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null || Handler == null) return null;
            return Handler(targetMethod, args);
        }
    }
}
