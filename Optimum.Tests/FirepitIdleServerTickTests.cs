#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class FirepitIdleServerTickTests
{
    private static string FindDll(string relativePath)
    {
        string nearTests = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(relativePath));
        if (File.Exists(nearTests))
        {
            return nearTests;
        }
        return PatchReader.FindRepositoryFile(relativePath);
    }

    [Fact]
    public void FirepitIdleServerTick_DefaultsTrue_ConfigAndDiagnosticsRoundTrip()
    {
        bool orig = OptimumConfig.FirepitIdleServerTickEnabled;
        string dir = Path.Combine(Path.GetTempPath(), "optimum-firepit-srv-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(OptimumConfig.FirepitIdleServerTickEnabled);
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.FirepitIdleServerTick));

            OptimumConfig.SetDataPath(dir);
            OptimumConfig.FirepitIdleServerTickEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.FirepitIdleServerTickEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.FirepitIdleServerTickEnabled);

            OptimumConfig.FirepitIdleServerTickEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.FirepitIdleServerTickEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.FirepitIdleServerTickEnabled);

            OptimumDiagnostics.ResetAllCounters();
            OptimumDiagnostics.RecordFirepitServerTickPaused();
            OptimumDiagnostics.RecordFirepitServerTickWoken();
            OptimumDiagnostics.RecordFirepitServerTickDynamicOrSkipped();
            string summary = OptimumDiagnostics.GetFirepitServerTickSummary();
            Assert.Contains("enabled=True", summary);
            Assert.Contains("paused=1", summary);
            Assert.Contains("woken=1", summary);
            Assert.Contains("dynamicOrSkipped=1", summary);

            OptimumDiagnostics.ResetAllCounters();
            Assert.Equal(0, OptimumDiagnostics.FirepitServerTicksPaused);
            Assert.Equal(0, OptimumDiagnostics.FirepitServerTicksWoken);
            Assert.Equal(0, OptimumDiagnostics.FirepitServerTicksDynamicOrSkipped);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.FirepitIdleServerTickEnabled = orig;
            OptimumDiagnostics.ResetAllCounters();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void FirepitIdleServerTick_ForkAndRuntimePatchesAndManifestAreComplete()
    {
        string forkFirepitPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/BlockEntity/Firepit/BEFirepit.cs.patch"));
        string forkBlockPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/Block/BlockFirepit.cs.patch"));
        string runtimeFirepitPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityFirepit.cs.patch"));
        string runtimeBlockPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockFirepit.cs.patch"));

        Assert.Contains("FirepitIdleServerTickEnabled", forkFirepitPatch);
        Assert.Contains("syncTickListenerId", forkFirepitPatch);
        Assert.Contains("SyncServerTickState", forkFirepitPatch);
        Assert.Contains("WakeIdleServerTicks", forkFirepitPatch);
        Assert.Contains("WakeIdleServerTicks", forkBlockPatch);

        Assert.Contains("FirepitIdleServerTickEnabled", runtimeFirepitPatch);
        Assert.Contains("syncTickListenerId", runtimeFirepitPatch);
        Assert.Contains("SyncServerTickState", runtimeFirepitPatch);
        Assert.Contains("WakeIdleServerTicks", runtimeFirepitPatch);
        Assert.Contains("WakeIdleServerTicks", runtimeBlockPatch);

        var lambdaPattern = new Regex(@"^\+.*=>", RegexOptions.Multiline);
        Assert.DoesNotMatch(lambdaPattern, runtimeFirepitPatch);
        Assert.DoesNotMatch(lambdaPattern, runtimeBlockPatch);

        var manifestMethod = typeof(Optimum.Patcher.ModPatcher)
            .GetMethod("SurvivalManifest", BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;

        var members = (Dictionary<string, List<string>>)manifest.GetType().GetProperty("Members")!.GetValue(manifest)!;
        List<string> firepitMembers = members["Vintagestory.GameContent.BlockEntityFirepit"];
        Assert.Contains("syncTickListenerId", firepitMembers);
        Assert.Contains("SupportsIdleServerTickPause", firepitMembers);
        Assert.Contains("IsServerIdleAtRest", firepitMembers);
        Assert.Contains("SyncServerTickState", firepitMembers);
        Assert.Contains("WakeIdleServerTicks", firepitMembers);

        var methods = (IEnumerable<Optimum.Patcher.MethodTarget>)manifest.GetType().GetProperty("Methods")!.GetValue(manifest)!;
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockFirepit" && m.MethodName == "OnTryIgniteBlockOver" && m.ParamCount == 4);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "OnSlotModified" && m.ParamCount == 1);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "On500msTick" && m.ParamCount == 1);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "OnBurnTick" && m.ParamCount == 1);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "CoolNow" && m.ParamCount == 2);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "igniteWithFuel" && m.ParamCount == 1);
    }

    [Fact]
    public void FirepitIdleServerTick_FullLifecycle_SmeltingBurnoutCoolingWakeAndSubclassGuards()
    {
        string survivalDll = FindDll("bin/Release/net10.0/VSSurvivalMod.dll");
        Assert.True(File.Exists(survivalDll), $"Missing VSSurvivalMod.dll at {survivalDll}");

        using (var asm = AssemblyDefinition.ReadAssembly(survivalDll))
        {
            TypeDefinition beTypeDef = asm.MainModule.GetType("Vintagestory.GameContent.BlockEntityFirepit");
            Assert.NotNull(beTypeDef);
            Assert.Contains(beTypeDef.Fields, f => f.Name == "syncTickListenerId");
            Assert.Contains(beTypeDef.Methods, m => m.Name == "SyncServerTickState");
            Assert.Contains(beTypeDef.Methods, m => m.Name == "WakeIdleServerTicks");
        }

        string? vanillaDonor = null;
        string? compiledDonor = null;
        try
        {
            vanillaDonor = PatchReader.FindRepositoryFile(".vanilla/win-x64/runtime-donors/Mods/VSSurvivalMod.dll");
            compiledDonor = PatchReader.FindRepositoryFile(".build/runtime-donors/VSSurvivalMod/bin/Release/net10.0/VSSurvivalMod.dll");
        }
        catch (FileNotFoundException)
        {
        }

        string tempOut = Path.Combine(Path.GetTempPath(), "optimum-firepit-srv-donor-" + Guid.NewGuid().ToString("N") + ".dll");
        bool origEnabled = OptimumConfig.FirepitIdleServerTickEnabled;
        try
        {
            if (vanillaDonor != null && compiledDonor != null && File.Exists(vanillaDonor) && File.Exists(compiledDonor))
            {
                Assert.True(Optimum.Patcher.ModPatcher.Patch("vssurvivalmod", vanillaDonor, compiledDonor, tempOut));
                Assert.True(File.Exists(tempOut));
            }

            string targetDll = File.Exists(tempOut) ? tempOut : survivalDll;
            RunLifecycleSuite(targetDll, Path.GetDirectoryName(survivalDll)!);
        }
        finally
        {
            OptimumConfig.FirepitIdleServerTickEnabled = origEnabled;
            OptimumDiagnostics.ResetAllCounters();
            try { File.Delete(tempOut); } catch { }
        }
    }

    private static void RunLifecycleSuite(string targetSurvivalDll, string survivalDir)
    {
        var alc = new AssemblyLoadContext("FirepitServerTickTest", isCollectible: true);
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
            Assembly modAsm = alc.LoadFromAssemblyPath(targetSurvivalDll);
            Type firepitType = modAsm.GetType("Vintagestory.GameContent.BlockEntityFirepit", throwOnError: true)!;
            Type stoveType = modAsm.GetType("Vintagestory.GameContent.BlockEntityStove", throwOnError: true)!;
            Type blockFirepitType = modAsm.GetType("Vintagestory.GameContent.BlockFirepit", throwOnError: true)!;

            FieldInfo invField = firepitType.GetField("inventory", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo tempField = firepitType.GetField("furnaceTemperature", BindingFlags.Public | BindingFlags.Instance)!;
            FieldInfo prevTempField = firepitType.GetField("prevFurnaceTemperature", BindingFlags.Public | BindingFlags.Instance)!;
            FieldInfo canIgniteField = firepitType.GetField("canIgniteFuel", BindingFlags.Public | BindingFlags.Instance)!;
            FieldInfo fuelBurnField = firepitType.GetField("fuelBurnTime", BindingFlags.Public | BindingFlags.Instance)!;
            FieldInfo cookingTimeField = firepitType.GetField("inputStackCookingTime", BindingFlags.Public | BindingFlags.Instance)!;

            OptimumConfig.FirepitIdleServerTickEnabled = true;
            OptimumDiagnostics.ResetAllCounters();

            // 1. Cold & empty firepit on Server pauses both 100ms & 500ms ticks cleanly at Initialize.
            var harness = new FirepitServerHarness();
            var blockPos = new BlockPos(10, 64, 10, 0);
            harness.CurrentBlock = harness.ColdBlock;
            var be = (BlockEntity)Activator.CreateInstance(firepitType)!;
            be.Pos = blockPos;
            be.Block = harness.ColdBlock;
            harness.CurrentBlockEntity = be;
            ICoreServerAPI serverApi = harness.CreateServerApi();
            be.Initialize(serverApi);

            Assert.Equal(0, harness.CountTickListeners(100));
            Assert.Equal(0, harness.CountTickListeners(500));
            Assert.Equal(1, harness.CountTickListeners(10000));
            Assert.Equal(1, OptimumDiagnostics.FirepitServerTicksPaused);

            // Adding firewood to fuelSlot while unlit keeps ticks paused until ignited.
            var fuelItem = new Item
            {
                ItemId = 1,
                Code = new AssetLocation("game", "firewood"),
                CombustibleProps = new CombustibleProperties
                {
                    BurnTemperature = 1200,
                    BurnDuration = 10.0f
                }
            };
            var inv = (InventoryBase)invField.GetValue(be)!;
            inv[0].Itemstack = new ItemStack(fuelItem, 2);
            inv[0].MarkDirty();
            Assert.Equal(0, harness.CountTickListeners(100));
            Assert.Equal(0, harness.CountTickListeners(500));

            // 2. Igniting via BlockFirepit.OnTryIgniteBlockOver wakes 100ms & 500ms server ticks immediately.
            var bf = (Block)Activator.CreateInstance(blockFirepitType)!;
            bf.Code = harness.ColdBlock.Code;
            blockFirepitType.BaseType?.GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(bf, serverApi);
            EnumHandling handling = EnumHandling.PassThrough;
            MethodInfo onIgniteOver = blockFirepitType.GetMethod("OnTryIgniteBlockOver")!;
            onIgniteOver.Invoke(bf, new object?[] { null, blockPos, 3.5f, handling });

            Assert.True((bool)canIgniteField.GetValue(be)!);
            Assert.Equal(1, harness.CountTickListeners(100));
            Assert.Equal(1, harness.CountTickListeners(500));
            Assert.Equal(1, OptimumDiagnostics.FirepitServerTicksWoken);

            // Stepping burn ticks ignites fuel -> "lit", consumes firewood, heats up, then melts an input item!
            var roastedItem = new Item { ItemId = 2, Code = new AssetLocation("game", "roastednugget") };
            var rawOre = new Item
            {
                ItemId = 3,
                Code = new AssetLocation("game", "rawore"),
                CombustibleProps = new CombustibleProperties
                {
                    MeltingPoint = 100,
                    MeltingDuration = 0.25f,
                    MaxTemperature = 900,
                    RequiresContainer = false,
                    SmeltedStack = new JsonItemStack { ResolvedItemstack = new ItemStack(roastedItem, 1), Quantity = 1 }
                }
            };
            inv[1].Itemstack = new ItemStack(rawOre, 1);
            inv[1].MarkDirty();

            harness.StepTicks(2.5f, stepSeconds: 0.25f);
            Assert.Equal("lit", harness.CurrentBlock.Variant["burnstate"]);
            Assert.True((float)tempField.GetValue(be)! > 20f);
            Assert.True((float)fuelBurnField.GetValue(be)! > 0f);

            // Step until cooking completes & output receives roasted item, then empty input/output while burning.
            harness.StepTicks(12.0f, stepSeconds: 0.25f);
            Assert.Null(inv[1].Itemstack);
            Assert.NotNull(inv[2].Itemstack);
            Assert.Equal(roastedItem.Code, inv[2].Itemstack!.Item.Code);

            // Consume fuel -> becomes "extinct", must KEEP ticking through extinct phase even after reaching 20 C.
            harness.StepTicks(15.0f, stepSeconds: 0.5f);
            inv[2].Itemstack = null;
            inv[2].MarkDirty();
            Assert.Equal("extinct", harness.CurrentBlock.Variant["burnstate"]);
            harness.StepTicks(180.0f, stepSeconds: 1.0f);
            Assert.Equal(20f, (float)tempField.GetValue(be)!);
            Assert.Equal(1, harness.CountTickListeners(100));
            Assert.Equal(1, harness.CountTickListeners(500));

            // Advance world calendar > 2 in-game hours -> extinct transitions to "cold", 500ms tick settles & pauses!
            harness.TotalHours += 2.5;
            harness.StepTicks(1.0f, stepSeconds: 0.5f);
            Assert.Equal("cold", harness.CurrentBlock.Variant["burnstate"]);
            Assert.False((bool)canIgniteField.GetValue(be)!);
            Assert.Equal(0, harness.CountTickListeners(100));
            Assert.Equal(0, harness.CountTickListeners(500));
            Assert.Equal(2, OptimumDiagnostics.FirepitServerTicksPaused);

            // 3. Placing an item into inputSlot on a paused cold firepit wakes server ticks immediately; clearing re-pauses.
            inv[1].Itemstack = new ItemStack(rawOre, 1);
            inv[1].MarkDirty();
            Assert.Equal(1, harness.CountTickListeners(100));
            Assert.Equal(1, harness.CountTickListeners(500));

            inv[1].Itemstack = null;
            cookingTimeField.SetValue(be, 0f);
            inv[1].MarkDirty();
            Assert.Equal(0, harness.CountTickListeners(100));
            Assert.Equal(0, harness.CountTickListeners(500));

            // 4. CoolNow quench keeps state consistent when cooling down an active burning firepit.
            canIgniteField.SetValue(be, true);
            inv[0].Itemstack = new ItemStack(fuelItem, 1);
            inv[0].MarkDirty();
            harness.StepTicks(0.2f, stepSeconds: 0.1f);
            Assert.True((float)fuelBurnField.GetValue(be)! > 0f);
            MethodInfo coolNow = firepitType.GetMethod("CoolNow")!;
            coolNow.Invoke(be, new object?[] { 100f, null });
            Assert.Equal("cold", harness.CurrentBlock.Variant["burnstate"]);
            Assert.False((bool)canIgniteField.GetValue(be)!);

            // Safe unload/remove with no leaked or thrown listeners.
            be.OnBlockUnloaded();
            be.OnBlockRemoved();
            Assert.Equal(0, harness.ActiveTickListenerCount);

            // 5. BlockEntityStove subclass pauses when cold & empty.
            var stoveHarness = new FirepitServerHarness { CurrentBlock = harness.ColdBlock };
            var beStove = (BlockEntity)Activator.CreateInstance(stoveType)!;
            beStove.Pos = new BlockPos(20, 64, 20, 0);
            beStove.Block = stoveHarness.ColdBlock;
            beStove.Initialize(stoveHarness.CreateServerApi());
            Assert.Equal(0, stoveHarness.CountTickListeners(100));
            Assert.Equal(0, stoveHarness.CountTickListeners(500));

            // 6. Custom third-party BlockEntityFirepit subclass stays dynamic/unpaused and increments DynamicOrSkipped.
            Type customFirepitType = CustomFirepitSubclassBuilder.GetOrCreate(firepitType);
            var modHarness = new FirepitServerHarness { CurrentBlock = harness.ColdBlock };
            long skippedBefore = OptimumDiagnostics.FirepitServerTicksDynamicOrSkipped;
            var beCustom = (BlockEntity)Activator.CreateInstance(customFirepitType)!;
            beCustom.Pos = new BlockPos(30, 64, 30, 0);
            beCustom.Block = modHarness.ColdBlock;
            beCustom.Initialize(modHarness.CreateServerApi());
            Assert.Equal(1, modHarness.CountTickListeners(100));
            Assert.Equal(1, modHarness.CountTickListeners(500));
            Assert.Equal(skippedBefore + 1, OptimumDiagnostics.FirepitServerTicksDynamicOrSkipped);

            // 7. Opt-out FirepitIdleServerTickEnabled = false restores baseline ticks and increments DynamicOrSkipped.
            OptimumConfig.FirepitIdleServerTickEnabled = false;
            var optOutHarness = new FirepitServerHarness { CurrentBlock = harness.ColdBlock };
            long skippedOptBefore = OptimumDiagnostics.FirepitServerTicksDynamicOrSkipped;
            var beOptOut = (BlockEntity)Activator.CreateInstance(firepitType)!;
            beOptOut.Pos = new BlockPos(40, 64, 40, 0);
            beOptOut.Block = optOutHarness.ColdBlock;
            beOptOut.Initialize(optOutHarness.CreateServerApi());
            Assert.Equal(1, optOutHarness.CountTickListeners(100));
            Assert.Equal(1, optOutHarness.CountTickListeners(500));
            Assert.Equal(skippedOptBefore + 1, OptimumDiagnostics.FirepitServerTicksDynamicOrSkipped);

            // 8. FromTreeAttributes while Api != null wakes if loaded hot or paused if loaded cold.
            OptimumConfig.FirepitIdleServerTickEnabled = true;
            var treeHarness = new FirepitServerHarness { CurrentBlock = harness.ColdBlock };
            var beReloaded = (BlockEntity)Activator.CreateInstance(firepitType)!;
            beReloaded.Pos = new BlockPos(50, 64, 50, 0);
            beReloaded.Block = treeHarness.ColdBlock;
            beReloaded.Initialize(treeHarness.CreateServerApi());
            Assert.Equal(0, treeHarness.CountTickListeners(100));

            var hotTree = new TreeAttribute();
            hotTree.SetInt("posx", 50);
            hotTree.SetInt("posy", 64);
            hotTree.SetInt("posz", 50);
            hotTree.SetFloat("furnaceTemperature", 600f);
            hotTree.SetBool("canIgniteFuel", false);
            beReloaded.FromTreeAttributes(hotTree, treeHarness.ServerWorld);
            Assert.Equal(1, treeHarness.CountTickListeners(100));
            Assert.Equal(1, treeHarness.CountTickListeners(500));
        }
        finally
        {
            alc.Unload();
        }
    }

    private static class CustomFirepitSubclassBuilder
    {
        public static Type GetOrCreate(Type firepitBase)
        {
            var asmName = new System.Reflection.AssemblyName("CustomModFirepit_" + Guid.NewGuid().ToString("N"));
            AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.RunAndCollect);
            ModuleBuilder mb = ab.DefineDynamicModule("Main");
            TypeBuilder tb = mb.DefineType(
                "CustomModFirepit",
                System.Reflection.TypeAttributes.Public,
                firepitBase);
            return tb.CreateType()!;
        }
    }

    private sealed class FirepitServerHarness
    {
        private readonly Dictionary<long, (int IntervalMs, Action<float>? Callback)> activeListeners = new();
        private long nextListenerId = 1;

        public readonly Block ColdBlock;
        public readonly Block ExtinctBlock;
        public readonly Block LitBlock;
        public Block CurrentBlock;
        public BlockEntity? CurrentBlockEntity;
        public double TotalHours = 100.0;
        public IServerWorldAccessor ServerWorld { get; private set; } = null!;

        public FirepitServerHarness()
        {
            ColdBlock = MakeVariantBlock(101, "cold");
            ExtinctBlock = MakeVariantBlock(102, "extinct");
            LitBlock = MakeVariantBlock(103, "lit");
            CurrentBlock = ColdBlock;
        }

        private static Block MakeVariantBlock(int id, string burnState)
        {
            var variantDict = new Vintagestory.API.Datastructures.OrderedDictionary<string, string> { ["burnstate"] = burnState };
            var block = new Block
            {
                BlockId = id,
                Code = new AssetLocation("game", "firepit-" + burnState),
                VariantStrict = variantDict,
                Variant = new Vintagestory.API.Util.RelaxedReadOnlyDictionary<string, string>(variantDict),
            };
            return block;
        }

        public int ActiveTickListenerCount => activeListeners.Count;
        public int CountTickListeners(int intervalMs) => activeListeners.Values.Count(v => v.IntervalMs == intervalMs);

        public void StepTicks(float durationSec, float stepSeconds)
        {
            int steps = Math.Max(1, (int)Math.Round(durationSec / stepSeconds));
            for (int i = 0; i < steps; i++)
            {
                float elapsed = (i + 1) * stepSeconds;
                var listeners = activeListeners.Values.ToArray();
                foreach (var listener in listeners)
                {
                    if (listener.Callback == null) continue;
                    if (listener.IntervalMs == 100)
                    {
                        listener.Callback(stepSeconds);
                    }
                    else if (listener.IntervalMs == 500 && Math.Abs((elapsed * 1000f) % 500f) < (stepSeconds * 500f))
                    {
                        listener.Callback(0.5f);
                    }
                }
            }
        }

        public ICoreServerAPI CreateServerApi()
        {
            var blockAccessor = CreateProxy<IBlockAccessor>((m, args) =>
            {
                switch (m.Name)
                {
                    case "GetBlock":
                        return CurrentBlock;
                    case "GetBlockEntity":
                        return CurrentBlockEntity;
                    case "ExchangeBlock" when args?.Length >= 1 && args[0] is int blockId:
                        CurrentBlock = blockId == LitBlock.BlockId ? LitBlock
                            : blockId == ExtinctBlock.BlockId ? ExtinctBlock
                            : ColdBlock;
                        return null;
                    default:
                        return DefaultValue(m.ReturnType);
                }
            });

            var calendar = CreateProxy<IGameCalendar>((m, _) => m.Name switch
            {
                "get_TotalHours" => TotalHours,
                _ => DefaultValue(m.ReturnType),
            });

            var invUtil = CreateProxy<IInventoryNetworkUtil>((_, mArgs) => null);
            var classRegistry = CreateProxy<IClassRegistryAPI>((m, _) => m.Name switch
            {
                "CreateInvNetworkUtil" => invUtil,
                _ => DefaultValue(m.ReturnType),
            });
            var modLoader = CreateProxy<IModLoader>((m, _) => DefaultValue(m.ReturnType));
            var rand = new Random(12345);

            ServerWorld = CreateProxy<IServerWorldAccessor>((m, args) =>
            {
                switch (m.Name)
                {
                    case "get_BlockAccessor":
                        return blockAccessor;
                    case "get_Calendar":
                        return calendar;
                    case "get_Rand":
                        return rand;
                    case "GetBlock" when args?.Length >= 1 && args[0] is AssetLocation loc:
                        if (loc.Path.EndsWith("-lit", StringComparison.Ordinal)) return LitBlock;
                        if (loc.Path.EndsWith("-extinct", StringComparison.Ordinal)) return ExtinctBlock;
                        return ColdBlock;
                    default:
                        return DefaultValue(m.ReturnType);
                }
            });

            var serverEvents = CreateProxy<IServerEventAPI>((m, args) => HandleEventCall(m, args));
            return CreateProxy<ICoreServerAPI>((m, _) => m.Name switch
            {
                "get_Side" => EnumAppSide.Server,
                "get_World" => ServerWorld,
                "get_Event" => serverEvents,
                "get_ClassRegistry" => classRegistry,
                "get_ModLoader" => modLoader,
                _ => DefaultValue(m.ReturnType),
            });
        }

        private object? HandleEventCall(MethodInfo m, object?[]? args)
        {
            switch (m.Name)
            {
                case "RegisterGameTickListener":
                {
                    Action<float>? cb = null;
                    int interval = 0;
                    if (args != null)
                    {
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (args[i] is Action<float> action) cb = action;
                            else if (args[i] is int ms && interval == 0) interval = ms;
                        }
                    }
                    long id = nextListenerId++;
                    activeListeners[id] = (interval, cb);
                    return id;
                }
                case "UnregisterGameTickListener" when args?.Length >= 1 && args[0] is long lid:
                    activeListeners.Remove(lid);
                    return null;
                default:
                    return DefaultValue(m.ReturnType);
            }
        }

        private static T CreateProxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, LocalDispatchProxy>() as LocalDispatchProxy
                ?? throw new InvalidOperationException();
            proxy.Handler = handler;
            return (T)(object)proxy;
        }

        public class LocalDispatchProxy : DispatchProxy
        {
            public System.Func<MethodInfo, object?[]?, object?>? Handler;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                return targetMethod != null && Handler != null ? Handler(targetMethod, args) : null;
            }
        }

        private static object? DefaultValue(Type returnType)
        {
            if (returnType != typeof(void) && returnType.IsValueType)
            {
                return Activator.CreateInstance(returnType);
            }
            return null;
        }
    }
}
