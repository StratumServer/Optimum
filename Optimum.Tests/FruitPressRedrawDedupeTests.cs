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
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public sealed class FruitPressRedrawDedupeTests
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
    public void FruitPressPatchesAndManifest_WireServerAndClientDeduplication()
    {
        string root = RepoRoot();
        string sourcePatch = File.ReadAllText(Path.Combine(
            root, "patches/VSSurvivalMod/BlockEntity/BEFruitpress.cs.patch"));
        string runtimePatch = File.ReadAllText(Path.Combine(
            root, "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityFruitPress.cs.patch"));
        string modPatcher = File.ReadAllText(Path.Combine(
            root, "Optimum.Patcher/mod-patcher.cs"));

        Assert.Contains("optimumCachedBucketStack", sourcePatch);
        Assert.Contains("optimumCachedBucketContentSize", sourcePatch);
        Assert.Contains("optimumCachedBucketMeshKey", sourcePatch);
        Assert.Contains("MarkDirty(bucketChanged);", sourcePatch);
        Assert.Contains("bucketMesh.VerticesCount > 0", sourcePatch);

        Assert.Contains("optimumCachedBucketStack", runtimePatch);
        Assert.Contains("optimumCachedBucketContentSize", runtimePatch);
        Assert.Contains("optimumCachedBucketMeshKey", runtimePatch);
        Assert.Contains("MarkDirty(redrawOnClient);", runtimePatch);
        Assert.Contains("bucketMesh.VerticesCount > 0", runtimePatch);

        Assert.Contains("\"Vintagestory.GameContent.BlockEntityFruitPress\"", modPatcher);
        Assert.Contains("\"optimumCachedBucketStack\"", modPatcher);
        Assert.Contains("\"optimumCachedBucketContentSize\"", modPatcher);
        Assert.Contains("\"optimumCachedBucketMeshKey\"", modPatcher);
        Assert.Contains("new(\"Vintagestory.GameContent.BlockEntityFruitPress\", \"onTick100msServer\", 1)", modPatcher);
        Assert.Contains("new(\"Vintagestory.GameContent.BlockEntityFruitPress\", \"genBucketMesh\", 0)", modPatcher);
    }

    [Fact]
    public void SourceBuild_FruitPressRedrawDedupe_GatesServerRedrawsAndCachesClientBucketMesh()
    {
        string root = RepoRoot();
        string sourceDll = Path.Combine(root, "bin/Release/net10.0/VSSurvivalMod.dll");
        if (!File.Exists(sourceDll))
        {
            sourceDll = Path.Combine(AppContext.BaseDirectory, "VSSurvivalMod.dll");
        }
        Assert.True(File.Exists(sourceDll), $"Missing VSSurvivalMod.dll at {sourceDll}");

        RunBehavioralSuite(sourceDll, Path.GetDirectoryName(sourceDll)!);
    }

    [Fact]
    public void AssembledRuntimeDonor_FruitPressRedrawDedupe_GatesServerRedrawsAndCachesClientBucketMesh()
    {
        string root = RepoRoot();
        string vanillaPath = Path.Combine(root, ".vanilla/win-x64/runtime-donors/Mods/VSSurvivalMod.dll");
        string compiledPath = Path.Combine(root, ".build/runtime-donors/VSSurvivalMod/bin/Release/net10.0/VSSurvivalMod.dll");
        if (!File.Exists(vanillaPath) || !File.Exists(compiledPath))
        {
            return;
        }

        string tempOut = Path.Combine(Path.GetTempPath(), $"optimum_fruitpress_672_{Guid.NewGuid():N}.dll");
        try
        {
            Assert.True(Optimum.Patcher.ModPatcher.Patch("vssurvivalmod", vanillaPath, compiledPath, tempOut));
            Assert.True(File.Exists(tempOut));

            RunBehavioralSuite(tempOut, Path.Combine(root, "bin/Release/net10.0"));
        }
        finally
        {
            try
            {
                if (File.Exists(tempOut)) File.Delete(tempOut);
                string pdb = Path.ChangeExtension(tempOut, ".pdb");
                if (File.Exists(pdb)) File.Delete(pdb);
            }
            catch
            {
            }
        }
    }

    private static void RunBehavioralSuite(string targetSurvivalDll, string survivalDir)
    {
        var alc = new AssemblyLoadContext("FruitPressRedrawTest", isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            if (name.Name == "VintagestoryAPI") return typeof(ICoreAPI).Assembly;
            if (name.Name == "Optimum.Api.Contracts") return typeof(OptimumConfig).Assembly;
            if (name.Name == "VSEssentials") return typeof(InWorldContainer).Assembly;
            string candidate = Path.Combine(survivalDir, name.Name + ".dll");
            if (File.Exists(candidate)) return alc.LoadFromAssemblyPath(candidate);
            string baseCandidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (File.Exists(baseCandidate)) return alc.LoadFromAssemblyPath(baseCandidate);
            return null;
        };

        try
        {
            Assembly asm = alc.LoadFromAssemblyPath(targetSurvivalDll);
            RunServerRedrawGatingTests(asm);
            RunClientBucketMeshCacheTests(asm);
        }
        finally
        {
            alc.Unload();
        }
    }

    private static void RunServerRedrawGatingTests(Assembly asm)
    {
        Type pressType = asm.GetType("Vintagestory.GameContent.BlockEntityFruitPress", true)!;
        Type liquidBaseType = asm.GetType("Vintagestory.GameContent.BlockLiquidContainerBase", true)!;
        Type meshSourceType = asm.GetType("Vintagestory.GameContent.IContainedMeshSource", true)!;

        Item cranberryJuice = CreateLiquidItem(101, "juiceportion-cranberry");
        Item waterJuice = CreateLiquidItem(102, "waterportion");
        Item mashItem = CreateJuiceableMashItem(103, "pressedmash-cranberry", "game:juiceportion-cranberry");
        var (bucketBlock, _) = CreateCountingLiquidContainerBlock(liquidBaseType, meshSourceType, 201, "woodbucket-test", capacityLitres: 10f);

        var itemsById = new Dictionary<int, Item>
        {
            [cranberryJuice.ItemId] = cranberryJuice,
            [waterJuice.ItemId] = waterJuice,
            [mashItem.ItemId] = mashItem,
        };
        var itemsByCode = new Dictionary<string, Item>(StringComparer.Ordinal)
        {
            ["game:juiceportion-cranberry"] = cranberryJuice,
            ["game:waterportion"] = waterJuice,
            ["game:pressedmash-cranberry"] = mashItem,
        };
        var blocksById = new Dictionary<int, Block>
        {
            [bucketBlock.BlockId] = bucketBlock,
        };

        var env = new ServerTestEnv(itemsById, itemsByCode, blocksById);
        bucketBlock.OnLoadedNative(env.Api);
        cranberryJuice.OnLoadedNative(env.Api);
        waterJuice.OnLoadedNative(env.Api);
        mashItem.OnLoadedNative(env.Api);

        var be = (BlockEntity)Activator.CreateInstance(pressType)!;
        be.Api = env.Api;
        be.Pos = new BlockPos(10, 64, 10);

        var compressAnimation = new Animation { Code = "compress", QuantityFrames = 30 };
        var animator = new StubAnimator(compressAnimation);
        RunningAnimation compressAnim = animator.GetAnimationState("compress")!;
        compressAnim.Active = true;
        compressAnim.CurrentFrame = 15f;
        var animUtil = new BlockEntityAnimationUtil(env.Api, be) { animator = animator };
        animUtil.activeAnimationsByAnimCode["compress"] = new AnimationMetaData { Code = "compress", Animation = "compress", AnimationSpeed = 0.5f };

        var animBehavior = new BEBehaviorAnimatable(be);
        typeof(BEBehaviorAnimatable).GetField("animUtil", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(animBehavior, animUtil);
        be.Behaviors.Add(animBehavior);

        ItemSlot mashSlot = (ItemSlot)pressType.GetProperty("MashSlot")!.GetValue(be)!;
        ItemSlot bucketSlot = (ItemSlot)pressType.GetProperty("BucketSlot")!.GetValue(be)!;
        MethodInfo onTick = pressType.GetMethod("onTick100msServer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo squeezedLeftField = pressType.GetField("squeezedLitresLeft", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo pressSqueezeRelField = pressType.GetField("pressSqueezeRel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo screwPercentField = pressType.GetField("screwPercent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo lastHoursField = pressType.GetField("lastLiquidTransferTotalHours", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo soundPlayedField = pressType.GetField("squeezeSoundPlayed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo listenerIdField = pressType.GetField("listenerId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo getCurrentLitres = liquidBaseType.GetMethod("GetCurrentLitres", new[] { typeof(ItemStack) })!;
        MethodInfo tryPutLiquid = liquidBaseType.GetMethod("TryPutLiquid", new[] { typeof(ItemStack), typeof(ItemStack), typeof(float) })!;

        ItemStack mashStack = new ItemStack(mashItem, 1);
        mashStack.Attributes.SetDouble("juiceableLitresLeft", 10.0);
        mashStack.Attributes.SetDouble("juiceableLitresTransfered", 0.0);
        mashStack.Attributes.SetDouble("squeezeRel", 0.5);
        mashSlot.Itemstack = mashStack;
        bucketSlot.Itemstack = new ItemStack(bucketBlock, 1);

        squeezedLeftField.SetValue(be, 5.0);
        pressSqueezeRelField.SetValue(be, 0.5);
        screwPercentField.SetValue(be, 0.5);
        lastHoursField.SetValue(be, 100.0);
        listenerIdField.SetValue(be, 42L);
        env.TotalHours = 100.0;

        // 1. Zero-transfer tick (dt rounds to 0 litres): BE dirty synced, sound played, 0 chunk redraws.
        onTick.Invoke(be, new object[] { 0.025f });
        Assert.Equal(1, env.MarkBlockEntityDirtyCount);
        Assert.Equal(0, env.MarkBlockDirtyCount);
        Assert.True((bool)soundPlayedField.GetValue(be)!);
        Assert.Equal(1, env.SoundPlayCount);

        // 2. Positive transfer tick: liquid enters bucket -> chunk redraw requested once.
        env.ResetCounters();
        env.TotalHours = 100.005;
        onTick.Invoke(be, new object[] { 0.025f });
        float bucketLitres = (float)getCurrentLitres.Invoke(bucketBlock, new object[] { bucketSlot.Itemstack! })!;
        Assert.True(bucketLitres > 0f, "Expected liquid to reach the bucket");
        Assert.Equal(1, env.MarkBlockEntityDirtyCount);
        Assert.Equal(1, env.MarkBlockDirtyCount);
        Assert.True(mashStack.Attributes.GetDouble("juiceableLitresLeft") < 10.0);

        // 3. Incompatible liquid in bucket (water): TryPutLiquid adds 0 -> 0 chunk redraws.
        var waterBucket = new ItemStack(bucketBlock, 1);
        tryPutLiquid.Invoke(bucketBlock, new object[] { waterBucket, new ItemStack(waterJuice, 100), 1.0f });
        bucketSlot.Itemstack = waterBucket;
        env.ResetCounters();
        env.TotalHours = 100.010;
        onTick.Invoke(be, new object[] { 0.025f });
        Assert.Equal(1.0f, (float)getCurrentLitres.Invoke(bucketBlock, new object[] { bucketSlot.Itemstack! })!, precision: 3);
        Assert.Equal(1, env.MarkBlockEntityDirtyCount);
        Assert.Equal(0, env.MarkBlockDirtyCount);

        // 4. Full bucket (10 L) draining onto floor: mash drains, 0 chunk redraws.
        var fullBucket = new ItemStack(bucketBlock, 1);
        tryPutLiquid.Invoke(bucketBlock, new object[] { fullBucket, new ItemStack(cranberryJuice, 1000), 10.0f });
        bucketSlot.Itemstack = fullBucket;
        double leftBeforeFull = mashStack.Attributes.GetDouble("juiceableLitresLeft");
        env.ResetCounters();
        env.TotalHours = 100.015;
        onTick.Invoke(be, new object[] { 0.025f });
        Assert.True(mashStack.Attributes.GetDouble("juiceableLitresLeft") < leftBeforeFull);
        Assert.Equal(1, env.MarkBlockEntityDirtyCount);
        Assert.Equal(0, env.MarkBlockDirtyCount);

        // 5. No bucket draining onto floor: mash drains, 0 chunk redraws.
        bucketSlot.Itemstack = null;
        double leftBeforeNone = mashStack.Attributes.GetDouble("juiceableLitresLeft");
        env.ResetCounters();
        env.TotalHours = 100.020;
        onTick.Invoke(be, new object[] { 0.025f });
        Assert.True(mashStack.Attributes.GetDouble("juiceableLitresLeft") < leftBeforeNone);
        Assert.Equal(1, env.MarkBlockEntityDirtyCount);
        Assert.Equal(0, env.MarkBlockDirtyCount);

        // 6. Pressing stops (squeezedLitresLeft == 0 and animation inactive): listener unregistered + final redraw.
        squeezedLeftField.SetValue(be, 0.0);
        compressAnim.Active = false;
        animUtil.activeAnimationsByAnimCode.Clear();
        env.ResetCounters();
        onTick.Invoke(be, new object[] { 0.025f });
        Assert.Equal(0L, (long)listenerIdField.GetValue(be)!);
        Assert.Equal(1, env.UnregisterListenerCount);
        Assert.Equal(1, env.MarkBlockDirtyCount);
    }

    private static void RunClientBucketMeshCacheTests(Assembly asm)
    {
        Type pressType = asm.GetType("Vintagestory.GameContent.BlockEntityFruitPress", true)!;
        Type liquidBaseType = asm.GetType("Vintagestory.GameContent.BlockLiquidContainerBase", true)!;
        Type blockBucketType = asm.GetType("Vintagestory.GameContent.BlockBucket", true)!;
        Type liquidTopType = asm.GetType("Vintagestory.GameContent.BlockLiquidContainerTopOpened", true)!;
        Type meshSourceType = asm.GetType("Vintagestory.GameContent.IContainedMeshSource", true)!;

        Item cranberryJuice = CreateLiquidItem(101, "juiceportion-cranberry");
        Item currantJuice = CreateLiquidItem(102, "juiceportion-redcurrant");
        var (bucketA, ctrlA) = CreateCountingLiquidContainerBlock(liquidBaseType, meshSourceType, 201, "woodbucket-a", capacityLitres: 10f);
        var (bucketB, ctrlB) = CreateCountingLiquidContainerBlock(liquidBaseType, meshSourceType, 202, "woodbucket-b", capacityLitres: 10f);

        var vanillaBucket = (Block)Activator.CreateInstance(blockBucketType)!;
        vanillaBucket.BlockId = 203;
        vanillaBucket.Code = new AssetLocation("game", "woodbucket");
        vanillaBucket.Attributes = new JsonObject(new JObject
        {
            ["capacityLitres"] = 10f,
            ["liquidContainerProps"] = new JObject(),
        });
        liquidTopType.GetField("origcontainermesh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vanillaBucket, CreateTestMesh());

        var itemsById = new Dictionary<int, Item>
        {
            [cranberryJuice.ItemId] = cranberryJuice,
            [currantJuice.ItemId] = currantJuice,
        };
        var blocksById = new Dictionary<int, Block>
        {
            [bucketA.BlockId] = bucketA,
            [bucketB.BlockId] = bucketB,
            [vanillaBucket.BlockId] = vanillaBucket,
        };

        var env = new ClientTestEnv(itemsById, blocksById);
        bucketA.OnLoadedNative(env.Api);
        bucketB.OnLoadedNative(env.Api);
        vanillaBucket.OnLoadedNative(env.Api);
        cranberryJuice.OnLoadedNative(env.Api);
        currantJuice.OnLoadedNative(env.Api);

        var be = (BlockEntity)Activator.CreateInstance(pressType)!;
        be.Api = env.Api;
        be.Pos = new BlockPos(12, 64, 12);
        pressType.GetField("capi", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(be, env.Api);

        ItemSlot bucketSlot = (ItemSlot)pressType.GetProperty("BucketSlot")!.GetValue(be)!;
        FieldInfo bucketMeshField = pressType.GetField("bucketMesh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo genBucketMesh = pressType.GetMethod("genBucketMesh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo tryPutLiquid = liquidBaseType.GetMethod("TryPutLiquid", new[] { typeof(ItemStack), typeof(ItemStack), typeof(float) })!;
        MethodInfo setContent = liquidBaseType.GetMethod("SetContent", new[] { typeof(ItemStack), typeof(ItemStack) })!;

        void SyncBucket(ItemStack? stack, double squeezedLeft = 2.0)
        {
            var tree = new TreeAttribute();
            var invTree = new TreeAttribute();
            var slotsTree = new TreeAttribute();
            if (stack != null)
            {
                slotsTree["1"] = new ItemstackAttribute(stack.Clone());
            }
            invTree["slots"] = slotsTree;
            invTree.SetInt("qslots", 2);
            tree["inventory"] = invTree;
            tree.SetDouble("squeezedLitresLeft", squeezedLeft);
            be.FromTreeAttributes(tree, env.World);
        }

        // 1. Initial bucket with 1.0 L cranberry juice -> builds once.
        ItemStack stackA = new ItemStack(bucketA, 1);
        ItemStack juice1L = new ItemStack(cranberryJuice, 100);
        tryPutLiquid.Invoke(bucketA, new object[] { stackA, juice1L, 1.0f });
        SyncBucket(stackA);
        Assert.Equal(1, ctrlA.GenMeshCalls);
        MeshData mesh1 = Assert.IsType<MeshData>(bucketMeshField.GetValue(be));
        Assert.True(mesh1.VerticesCount > 0);

        // 2. 20 unchanged syncs (including ticking transitionstate freshness) -> 0 rebuilds.
        for (int i = 0; i < 20; i++)
        {
            ItemStack copy = stackA.Clone();
            var contents = copy.Attributes.GetTreeAttribute("contents");
            ItemStack? inner = contents?.GetItemstack("0");
            if (inner != null)
            {
                inner.ResolveBlockOrItem(env.World);
                var ts = new TreeAttribute();
                ts.SetFloat("freshHoursLeft", 48f - i * 0.25f);
                inner.Attributes["transitionstate"] = ts;
                contents!.SetItemstack("0", inner);
            }
            SyncBucket(copy, squeezedLeft: 2.0 - i * 0.01);
        }
        Assert.Equal(1, ctrlA.GenMeshCalls);
        Assert.Same(mesh1, bucketMeshField.GetValue(be));

        // 3. Fill level increase 1.0 L -> 2.0 L -> rebuilds.
        tryPutLiquid.Invoke(bucketA, new object[] { stackA, new ItemStack(cranberryJuice, 100), 1.0f });
        SyncBucket(stackA);
        Assert.Equal(2, ctrlA.GenMeshCalls);
        MeshData mesh2 = Assert.IsType<MeshData>(bucketMeshField.GetValue(be));
        Assert.NotSame(mesh1, mesh2);

        // 4. External bucketMesh.Clear() (as InteractGround take-out does) + identical rollback sync -> rebuilds!
        mesh2.Clear();
        Assert.Equal(0, mesh2.VerticesCount);
        SyncBucket(stackA);
        Assert.Equal(3, ctrlA.GenMeshCalls);
        MeshData mesh3 = Assert.IsType<MeshData>(bucketMeshField.GetValue(be));
        Assert.True(mesh3.VerticesCount > 0);

        // 5. Bucket removed -> cleared; placing identical bucket back -> rebuilds.
        SyncBucket(null);
        Assert.Equal(0, ((MeshData)bucketMeshField.GetValue(be)!).VerticesCount);
        SyncBucket(stackA);
        Assert.Equal(4, ctrlA.GenMeshCalls);

        // 6. Different liquid at identical fill level (2.0 L redcurrant) -> rebuilds.
        ItemStack currantStack = new ItemStack(bucketA, 1);
        tryPutLiquid.Invoke(bucketA, new object[] { currantStack, new ItemStack(currantJuice, 200), 2.0f });
        SyncBucket(currantStack);
        Assert.Equal(5, ctrlA.GenMeshCalls);

        // 7. Different container at identical fill level (bucketB with 2.0 L redcurrant) -> rebuilds.
        ItemStack stackB = new ItemStack(bucketB, 1);
        tryPutLiquid.Invoke(bucketB, new object[] { stackB, new ItemStack(currantJuice, 200), 2.0f });
        SyncBucket(stackB);
        Assert.Equal(1, ctrlB.GenMeshCalls);

        // 8. In-place mutation of BucketSlot.Itemstack liquid fill -> rebuilds (cache does not alias slot stack).
        setContent.Invoke(bucketB, new object[] { bucketSlot.Itemstack!, new ItemStack(currantJuice, 350) });
        genBucketMesh.Invoke(be, null);
        Assert.Equal(2, ctrlB.GenMeshCalls);

        // 9. Custom IContainedMeshSource key change -> rebuilds.
        ctrlB.MeshKey = "custom-variant-v2";
        genBucketMesh.Invoke(be, null);
        Assert.Equal(3, ctrlB.GenMeshCalls);

        // 10. Vanilla BlockBucket fast path (useMeshKey == false): caches across syncs and rebuilds after Clear().
        SyncBucket(new ItemStack(vanillaBucket, 1));
        MeshData vanillaMesh = Assert.IsType<MeshData>(bucketMeshField.GetValue(be));
        Assert.True(vanillaMesh.VerticesCount > 0);
        for (int i = 0; i < 10; i++)
        {
            SyncBucket(new ItemStack(vanillaBucket, 1));
        }
        Assert.Same(vanillaMesh, bucketMeshField.GetValue(be));

        vanillaMesh.Clear();
        Assert.Equal(0, vanillaMesh.VerticesCount);
        SyncBucket(new ItemStack(vanillaBucket, 1));
        MeshData vanillaRestored = Assert.IsType<MeshData>(bucketMeshField.GetValue(be));
        Assert.True(vanillaRestored.VerticesCount > 0);
    }

    private static Item CreateLiquidItem(int id, string code)
    {
        return new Item
        {
            ItemId = id,
            Code = new AssetLocation("game", code),
            MaxStackSize = 1000,
            Attributes = new JsonObject(new JObject
            {
                ["waterTightContainerProps"] = new JObject
                {
                    ["containable"] = true,
                    ["itemsPerLitre"] = 100f,
                }
            }),
        };
    }

    private static Item CreateJuiceableMashItem(int id, string code, string liquidCode)
    {
        return new Item
        {
            ItemId = id,
            Code = new AssetLocation("game", code),
            MaxStackSize = 64,
            Attributes = new JsonObject(new JObject
            {
                ["juiceableProperties"] = new JObject
                {
                    ["liquidStack"] = new JObject
                    {
                        ["type"] = "item",
                        ["code"] = liquidCode,
                        ["stacksize"] = 1,
                    },
                    ["pressedDryRatio"] = 0.8f,
                }
            }),
        };
    }

    private static MeshData CreateTestMesh()
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);
        mesh.AddVertex(0f, 0f, 0f, 0f, 0f, 0);
        mesh.AddVertex(1f, 0f, 0f, 1f, 0f, 0);
        mesh.AddVertex(1f, 1f, 0f, 1f, 1f, 0);
        mesh.AddVertex(0f, 1f, 0f, 0f, 1f, 0);
        mesh.AddIndex(0);
        mesh.AddIndex(1);
        mesh.AddIndex(2);
        mesh.AddIndex(0);
        mesh.AddIndex(2);
        mesh.AddIndex(3);
        return mesh;
    }

    public sealed class CountingMeshController
    {
        public int GenMeshCalls;
        public string MeshKey = "default-key";

        public MeshData GenMesh()
        {
            GenMeshCalls++;
            return CreateTestMesh();
        }
    }

    private static (Block Block, CountingMeshController Controller) CreateCountingLiquidContainerBlock(
        Type liquidBaseType, Type containedMeshSourceType, int blockId, string code, float capacityLitres)
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"FruitPressTestBucket_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.RunAndCollect);
        var mb = ab.DefineDynamicModule("Main");
        var tb = mb.DefineType(
            "CountingLiquidBucketBlock",
            TypeAttributes.Public | TypeAttributes.Class,
            liquidBaseType,
            new[] { containedMeshSourceType });

        FieldBuilder ctrlField = tb.DefineField("Controller", typeof(CountingMeshController), FieldAttributes.Public);

        MethodBuilder genMesh = tb.DefineMethod(
            "GenMesh",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
            typeof(MeshData),
            new[] { typeof(ItemSlot), typeof(ITextureAtlasAPI), typeof(BlockPos) });
        ILGenerator genIl = genMesh.GetILGenerator();
        genIl.Emit(OpCodes.Ldarg_0);
        genIl.Emit(OpCodes.Ldfld, ctrlField);
        genIl.Emit(OpCodes.Callvirt, typeof(CountingMeshController).GetMethod(nameof(CountingMeshController.GenMesh))!);
        genIl.Emit(OpCodes.Ret);

        MethodBuilder getKey = tb.DefineMethod(
            "GetMeshCacheKey",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
            typeof(string),
            new[] { typeof(ItemSlot) });
        ILGenerator keyIl = getKey.GetILGenerator();
        keyIl.Emit(OpCodes.Ldarg_0);
        keyIl.Emit(OpCodes.Ldfld, ctrlField);
        keyIl.Emit(OpCodes.Ldfld, typeof(CountingMeshController).GetField(nameof(CountingMeshController.MeshKey))!);
        keyIl.Emit(OpCodes.Ret);

        Type dynamicType = tb.CreateType()!;
        var block = (Block)Activator.CreateInstance(dynamicType)!;
        var controller = new CountingMeshController();
        dynamicType.GetField("Controller")!.SetValue(block, controller);
        block.BlockId = blockId;
        block.Code = new AssetLocation("game", code);
        block.Attributes = new JsonObject(new JObject
        {
            ["capacityLitres"] = capacityLitres,
            ["liquidContainerProps"] = new JObject(),
        });
        return (block, controller);
    }

    private sealed class StubAnimator : AnimatorBase
    {
        public override int MaxJointId => 0;

        public StubAnimator(Animation compressAnimation)
            : base(null, new[] { compressAnimation })
        {
        }

        public override void OnFrame(Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode, float dt)
        {
        }

        protected override void calculateMatrices(float dt)
        {
        }
    }

    private sealed class ServerTestEnv
    {
        public ICoreServerAPI Api { get; }
        public double TotalHours { get; set; }
        public int MarkBlockEntityDirtyCount { get; private set; }
        public int MarkBlockDirtyCount { get; private set; }
        public int SoundPlayCount { get; private set; }
        public int UnregisterListenerCount { get; private set; }

        public ServerTestEnv(
            Dictionary<int, Item> itemsById,
            Dictionary<string, Item> itemsByCode,
            Dictionary<int, Block> blocksById)
        {
            var blockAccessor = HighImpactBugFixRegressionTests.StubProxy<IBlockAccessor>.Create((m, _) =>
            {
                switch (m.Name)
                {
                    case "MarkBlockEntityDirty":
                        MarkBlockEntityDirtyCount++;
                        return null!;
                    case "MarkBlockDirty":
                        MarkBlockDirtyCount++;
                        return null!;
                    default:
                        return DefaultValue(m.ReturnType)!;
                }
            });

            var calendar = HighImpactBugFixRegressionTests.StubProxy<IGameCalendar>.Create((m, _) => m.Name switch
            {
                "get_TotalHours" => TotalHours,
                _ => DefaultValue(m.ReturnType)!,
            });

            var network = HighImpactBugFixRegressionTests.StubProxy<IServerNetworkAPI>.Create((m, _) => DefaultValue(m.ReturnType)!);

            var events = HighImpactBugFixRegressionTests.StubProxy<IServerEventAPI>.Create((m, _) =>
            {
                if (m.Name == "UnregisterGameTickListener")
                {
                    UnregisterListenerCount++;
                    return null!;
                }
                return DefaultValue(m.ReturnType)!;
            });

            var world = HighImpactBugFixRegressionTests.StubProxy<IServerWorldAccessor>.Create((m, args) =>
            {
                switch (m.Name)
                {
                    case "get_Side":
                        return EnumAppSide.Server;
                    case "get_BlockAccessor":
                        return blockAccessor;
                    case "get_Calendar":
                        return calendar;
                    case "GetItem":
                        if (args.Length == 1 && args[0] is int iid) return itemsById.GetValueOrDefault(iid)!;
                        if (args.Length == 1 && args[0] is AssetLocation loc) return itemsByCode.GetValueOrDefault(loc.ToString())!;
                        return null!;
                    case "GetBlock":
                        if (args.Length == 1 && args[0] is int bid) return blocksById.GetValueOrDefault(bid)!;
                        return null!;
                    case "PlaySoundAt":
                        SoundPlayCount++;
                        return null!;
                    default:
                        return DefaultValue(m.ReturnType)!;
                }
            });

            var logger = HighImpactBugFixRegressionTests.StubProxy<ILogger>.Create((m, _) => DefaultValue(m.ReturnType)!);

            Api = HighImpactBugFixRegressionTests.StubProxy<ICoreServerAPI>.Create((m, _) => m.Name switch
            {
                "get_Side" => EnumAppSide.Server,
                "get_World" => world,
                "get_Network" => network,
                "get_Event" => events,
                "get_Logger" => logger,
                _ => DefaultValue(m.ReturnType)!,
            });
        }

        public void ResetCounters()
        {
            MarkBlockEntityDirtyCount = 0;
            MarkBlockDirtyCount = 0;
            SoundPlayCount = 0;
            UnregisterListenerCount = 0;
        }
    }

    private sealed class ClientTestEnv
    {
        public ICoreClientAPI Api { get; }
        public IClientWorldAccessor World { get; }

        public ClientTestEnv(Dictionary<int, Item> itemsById, Dictionary<int, Block> blocksById)
        {
            var blockAtlas = HighImpactBugFixRegressionTests.StubProxy<IBlockTextureAtlasAPI>.Create((m, _) => DefaultValue(m.ReturnType)!);
            var logger = HighImpactBugFixRegressionTests.StubProxy<ILogger>.Create((m, _) => DefaultValue(m.ReturnType)!);
            var objectCache = new Dictionary<string, object>();
            var collectibles = new List<CollectibleObject>();

            World = HighImpactBugFixRegressionTests.StubProxy<IClientWorldAccessor>.Create((m, args) => m.Name switch
            {
                "get_Side" => EnumAppSide.Client,
                "get_Collectibles" => collectibles,
                "GetItem" when args.Length == 1 && args[0] is int iid => itemsById.GetValueOrDefault(iid)!,
                "GetBlock" when args.Length == 1 && args[0] is int bid => blocksById.GetValueOrDefault(bid)!,
                _ => DefaultValue(m.ReturnType)!,
            });

            Api = HighImpactBugFixRegressionTests.StubProxy<ICoreClientAPI>.Create((m, _) => m.Name switch
            {
                "get_Side" => EnumAppSide.Client,
                "get_World" => World,
                "get_BlockTextureAtlas" => blockAtlas,
                "get_ObjectCache" => objectCache,
                "get_Logger" => logger,
                _ => DefaultValue(m.ReturnType)!,
            });
        }
    }

    private static object? DefaultValue(Type type)
    {
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
