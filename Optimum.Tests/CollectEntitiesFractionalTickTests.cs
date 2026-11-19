#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public class CollectEntitiesFractionalTickTests
{
    private const float Steady30HzDt = 1f / 30f;

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
    public void CollectEntitiesPatches_PreserveFractionalDeltaTimeInBothVariants()
    {
        string sourcePatch = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "patches/VSEssentials/Entity/Behavior/BehaviorCollectEntities.cs.patch"));
        string runtimePatch = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "patches/runtime/VSEssentials/Vintagestory/GameContent/EntityBehaviorCollectEntities.cs.patch"));

        Assert.DoesNotContain("collectedAny ? deltaTime : 0", sourcePatch);
        Assert.DoesNotContain("collectedAny ? deltaTime : 0f", runtimePatch);
        Assert.Contains("unconsumedDeltaTime = deltaTime;", sourcePatch);
        Assert.Contains("unconsumedDeltaTime = deltaTime;", runtimePatch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceBehavior_Steady30Hz_PreservesFractionalTimeAndCollectsAtVanillaRate(bool useSpatialPartitioning)
    {
        var drops = CreateDrops(30);
        var (collector, world, pushedEvents) = CreateScenario(drops, acceptedItemIds: null);
        var behavior = new EntityBehaviorCollectEntities(collector);

        if (useSpatialPartitioning)
        {
            var partitioning = CreateSourcePartitioning(world.Api, drops);
            SetField(behavior, "partitionUtil", partitioning);
        }

        // Tick 1 at 30 Hz (33.3 ms < 43.5 ms): nothing collected yet, fractional time preserved.
        behavior.OnGameTick(Steady30HzDt);
        Assert.Empty(collector.CollectedItemIds);
        Assert.InRange(GetFloatField(behavior, "unconsumedDeltaTime"), 0.033f, 0.034f);

        // Tick 2 at 30 Hz (66.7 ms > 43.5 ms): first item collected and marked PickedUp despawned.
        behavior.OnGameTick(Steady30HzDt);
        Assert.Single(collector.CollectedItemIds);
        Assert.False(drops[0].Alive);
        Assert.Equal(EnumDespawnReason.PickedUp, drops[0].DespawnReason?.Reason);
        Assert.Single(pushedEvents);
        Assert.InRange(GetFloatField(behavior, "unconsumedDeltaTime"), 0.022f, 0.024f);

        // Remaining 28 ticks complete 1 full second (30 ticks total): 23 items/sec steady rate.
        for (int tick = 2; tick < 30; tick++)
        {
            behavior.OnGameTick(Steady30HzDt);
        }

        Assert.Equal(23, collector.CollectedItemIds.Count);
        Assert.Equal(23, drops.Count(drop => !drop.Alive && drop.DespawnReason?.Reason == EnumDespawnReason.PickedUp));
        Assert.Equal(7, drops.Count(drop => drop.Alive));
        Assert.Equal(23, pushedEvents.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceBehavior_UncollectibleFirstCandidate_LeavesOtherDropsEligibleAt30Hz(bool useSpatialPartitioning)
    {
        var drops = CreateDrops(3);
        // Item 1 (first candidate) cannot fit in inventory; items 2 and 3 can.
        var (collector, world, _) = CreateScenario(drops, acceptedItemIds: new HashSet<int> { 2, 3 });
        var behavior = new EntityBehaviorCollectEntities(collector);

        if (useSpatialPartitioning)
        {
            var partitioning = CreateSourcePartitioning(world.Api, drops);
            SetField(behavior, "partitionUtil", partitioning);
        }

        // Tick 1: accumulates 33.3 ms.
        behavior.OnGameTick(Steady30HzDt);
        Assert.Empty(collector.CollectedItemIds);

        // Tick 2: first candidate fails TryGiveItemStack, second candidate is collected and despawned.
        behavior.OnGameTick(Steady30HzDt);
        Assert.Equal(new[] { 2 }, collector.CollectedItemIds);
        Assert.True(drops[0].Alive);
        Assert.False(drops[1].Alive);
        Assert.Equal(EnumDespawnReason.PickedUp, drops[1].DespawnReason?.Reason);
        Assert.True(drops[2].Alive);

        // Tick 3: third candidate is collected and despawned while uncollectible first drop stays alive.
        behavior.OnGameTick(Steady30HzDt);
        Assert.Equal(new[] { 2, 3 }, collector.CollectedItemIds);
        Assert.True(drops[0].Alive);
        Assert.False(drops[2].Alive);
        Assert.Equal(EnumDespawnReason.PickedUp, drops[2].DespawnReason?.Reason);

        // Tick 4: only uncollectible drop remains; no infinite loop and item stays alive.
        behavior.OnGameTick(Steady30HzDt);
        Assert.Equal(new[] { 2, 3 }, collector.CollectedItemIds);
        Assert.True(drops[0].Alive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceBehavior_NoCollectibleDropsInRange_ResetsUnconsumedDeltaTime(bool useSpatialPartitioning)
    {
        var drops = Array.Empty<EntityItem>();
        var (collector, world, _) = CreateScenario(drops, acceptedItemIds: null);
        var behavior = new EntityBehaviorCollectEntities(collector);

        if (useSpatialPartitioning)
        {
            var partitioning = CreateSourcePartitioning(world.Api, drops);
            SetField(behavior, "partitionUtil", partitioning);
        }

        // Two 30 Hz ticks exceed 1 / 23 s, trigger the empty check, and clear unconsumedDeltaTime.
        behavior.OnGameTick(Steady30HzDt);
        behavior.OnGameTick(Steady30HzDt);

        Assert.Equal(0f, GetFloatField(behavior, "unconsumedDeltaTime"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AssembledRuntimeDonor_Steady30Hz_PickupRemovalAndUncollectibleFallbackWork(bool useSpatialPartitioning)
    {
        string root = RepoRoot();
        string vanillaPath = Path.Combine(root, ".vanilla/win-x64/runtime-donors/Mods/VSEssentials.dll");
        string compiledPath = Path.Combine(root, ".build/runtime-donors/VSEssentials/bin/Release/net10.0/VSEssentials.dll");
        if (!File.Exists(vanillaPath) || !File.Exists(compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_collect_665_{Guid.NewGuid():N}.dll");
        CollectDonorLoadContext? alc = null;
        try
        {
            Assert.True(Optimum.Patcher.ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            Assert.True(File.Exists(tempOutput));

            alc = new CollectDonorLoadContext(tempOutput);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);

            Type behaviorType = patchedAsm.GetType("Vintagestory.GameContent.EntityBehaviorCollectEntities")!;
            Type partitioningType = patchedAsm.GetType("Vintagestory.GameContent.EntityPartitioning")!;
            MethodInfo onGameTick = behaviorType.GetMethod("OnGameTick", new[] { typeof(float) })!;
            Assert.NotNull(behaviorType);
            Assert.NotNull(partitioningType);
            Assert.NotNull(onGameTick);

            // 1) Steady 30 Hz pickup + entity removal (30 ticks = 23 items/sec).
            {
                var drops = CreateDrops(30);
                var (collector, world, pushedEvents) = CreateScenario(drops, acceptedItemIds: null);
                object behavior = Activator.CreateInstance(behaviorType, collector)!;

                if (useSpatialPartitioning)
                {
                    object partitioning = CreateRuntimePartitioning(partitioningType, world.Api, drops);
                    SetField(behavior, "partitionUtil", partitioning);
                }

                onGameTick.Invoke(behavior, new object[] { Steady30HzDt });
                Assert.Empty(collector.CollectedItemIds);
                Assert.InRange(GetFloatField(behavior, "unconsumedDeltaTime"), 0.033f, 0.034f);

                onGameTick.Invoke(behavior, new object[] { Steady30HzDt });
                Assert.Single(collector.CollectedItemIds);
                Assert.False(drops[0].Alive);
                Assert.Equal(EnumDespawnReason.PickedUp, drops[0].DespawnReason?.Reason);

                for (int tick = 2; tick < 30; tick++)
                {
                    onGameTick.Invoke(behavior, new object[] { Steady30HzDt });
                }

                Assert.Equal(23, collector.CollectedItemIds.Count);
                Assert.Equal(23, drops.Count(drop => !drop.Alive && drop.DespawnReason?.Reason == EnumDespawnReason.PickedUp));
                Assert.Equal(23, pushedEvents.Count);
            }

            // 2) Uncollectible first candidate still allows collecting subsequent drops at 30 Hz.
            {
                var drops = CreateDrops(3);
                var (collector, world, _) = CreateScenario(drops, acceptedItemIds: new HashSet<int> { 2, 3 });
                object behavior = Activator.CreateInstance(behaviorType, collector)!;

                if (useSpatialPartitioning)
                {
                    object partitioning = CreateRuntimePartitioning(partitioningType, world.Api, drops);
                    SetField(behavior, "partitionUtil", partitioning);
                }

                onGameTick.Invoke(behavior, new object[] { Steady30HzDt });
                onGameTick.Invoke(behavior, new object[] { Steady30HzDt });
                Assert.Equal(new[] { 2 }, collector.CollectedItemIds);
                Assert.True(drops[0].Alive);
                Assert.False(drops[1].Alive);
                Assert.Equal(EnumDespawnReason.PickedUp, drops[1].DespawnReason?.Reason);

                onGameTick.Invoke(behavior, new object[] { Steady30HzDt });
                Assert.Equal(new[] { 2, 3 }, collector.CollectedItemIds);
                Assert.True(drops[0].Alive);
                Assert.False(drops[2].Alive);
                Assert.Equal(EnumDespawnReason.PickedUp, drops[2].DespawnReason?.Reason);
            }
        }
        finally
        {
            alc?.Unload();
            try
            {
                if (File.Exists(tempOutput))
                {
                    File.Delete(tempOutput);
                }
            }
            catch
            {
                // Ignore temp cleanup failure on locked handles.
            }
        }
    }

    private static EntityItem[] CreateDrops(int count)
    {
        var drops = new EntityItem[count];
        for (int i = 0; i < count; i++)
        {
            int id = i + 1;
            var item = new Item
            {
                ItemId = id,
                Code = new AssetLocation("game", $"testdrop-{id}")
            };
            var stack = new ItemStack(item, 1);
            var drop = new EntityItem
            {
                EntityId = 1000 + id,
                Code = new AssetLocation("game", "item"),
                Alive = true,
                State = EnumEntityState.Active
            };
            drop.Slot.Itemstack = stack;
            drop.Pos.SetPos(10.2, 10.5, 10.2);
            drops[i] = drop;
        }

        return drops;
    }

    private static (TestCollectorEntity Collector, IWorldAccessor World, List<string> PushedEvents) CreateScenario(
        IReadOnlyList<EntityItem> drops,
        HashSet<int>? acceptedItemIds)
    {
        var pushedEvents = new List<string>();
        var blockAccessor = CreateProxy<IBlockAccessor>((method, _) => method.Name switch
        {
            "get_MapSizeX" => 1024,
            "get_MapSizeY" => 256,
            "get_MapSizeZ" => 1024,
            "get_ChunkSize" => 32,
            _ => DefaultValue(method.ReturnType)
        });

        var eventApi = CreateProxy<IEventAPI>((method, args) =>
        {
            if (method.Name == "PushEvent" && args is { Length: > 0 } && args[0] is string eventName)
            {
                pushedEvents.Add(eventName);
            }

            return DefaultValue(method.ReturnType);
        });

        IWorldAccessor world = null!;
        ICoreAPI api = CreateProxy<ICoreAPI>((method, _) => method.Name switch
        {
            "get_Side" => EnumAppSide.Server,
            "get_World" => world,
            "get_Event" => eventApi,
            "get_ModLoader" => null,
            _ => DefaultValue(method.ReturnType)
        });

        world = CreateProxy<IWorldAccessor>((method, args) =>
        {
            switch (method.Name)
            {
                case "get_Api":
                    return api;
                case "get_Side":
                    return EnumAppSide.Server;
                case "get_BlockAccessor":
                    return blockAccessor;
                case "get_ElapsedMilliseconds":
                    return 5000L;
                case "GetEntitiesAround":
                    var pos = (Vec3d)args![0]!;
                    float horRange = (float)args[1]!;
                    float vertRange = (float)args[2]!;
                    var matches = args.Length > 3 ? (ActionConsumable<Entity>?)args[3] : null;
                    var result = new List<Entity>();
                    foreach (var drop in drops)
                    {
                        if (drop.State == EnumEntityState.Despawned)
                        {
                            continue;
                        }

                        double dx = drop.Pos.X - pos.X;
                        double dy = drop.Pos.InternalY - pos.Y;
                        double dz = drop.Pos.Z - pos.Z;
                        if (dx * dx + dz * dz <= horRange * horRange && Math.Abs(dy) <= vertRange)
                        {
                            if (matches == null || matches(drop))
                            {
                                result.Add(drop);
                            }
                        }
                    }

                    return result.ToArray();
                case "PlaySoundAt":
                    return null;
                default:
                    return DefaultValue(method.ReturnType);
            }
        });

        foreach (var drop in drops)
        {
            drop.World = world;
            drop.Api = api;
        }

        var collector = new TestCollectorEntity(acceptedItemIds)
        {
            EntityId = 1,
            Code = new AssetLocation("game", "player"),
            Alive = true,
            State = EnumEntityState.Active,
            SelectionBox = new Cuboidf(-0.3f, 0f, -0.3f, 0.3f, 1.8f, 0.3f),
            World = world,
            Api = api
        };
        collector.Pos.SetPos(10.0, 10.0, 10.0);

        return (collector, world, pushedEvents);
    }

    private static EntityPartitioning CreateSourcePartitioning(ICoreAPI api, IReadOnlyList<EntityItem> drops)
    {
        var partitioning = new EntityPartitioning();
        SetField(partitioning, "api", api);
        MethodInfo partitionMethod = typeof(EntityPartitioning).GetMethod(
            "PartitionEntities",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        partitionMethod.Invoke(partitioning, new object[] { drops.Cast<Entity>().ToList() });
        return partitioning;
    }

    private static object CreateRuntimePartitioning(Type partitioningType, ICoreAPI api, IReadOnlyList<EntityItem> drops)
    {
        object partitioning = Activator.CreateInstance(partitioningType)!;
        SetField(partitioning, "api", api);
        MethodInfo partitionMethod = partitioningType.GetMethod(
            "PartitionEntities",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        partitionMethod.Invoke(partitioning, new object[] { drops.Cast<Entity>().ToList() });
        return partitioning;
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static float GetFloatField(object target, string fieldName)
    {
        FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(field);
        return (float)field!.GetValue(target)!;
    }

    private static T CreateProxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, CollectProxy>();
        ((CollectProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultValue(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    public class CollectProxy : DispatchProxy
    {
        public System.Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null || Handler == null)
            {
                return null;
            }

            return Handler(targetMethod, args);
        }
    }

    private sealed class CollectDonorLoadContext : AssemblyLoadContext
    {
        private readonly string _patchedPath;

        public CollectDonorLoadContext(string patchedPath) : base(isCollectible: true)
        {
            _patchedPath = patchedPath;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "VSEssentials", StringComparison.OrdinalIgnoreCase))
            {
                return LoadFromAssemblyPath(_patchedPath);
            }

            return null;
        }
    }

    private sealed class TestCollectorEntity : Entity
    {
        private readonly HashSet<int>? _acceptedItemIds;

        public List<int> CollectedItemIds { get; } = new();

        public TestCollectorEntity(HashSet<int>? acceptedItemIds)
        {
            _acceptedItemIds = acceptedItemIds;
        }

        public override bool TryGiveItemStack(ItemStack itemstack)
        {
            if (itemstack == null || itemstack.StackSize <= 0)
            {
                return false;
            }

            if (_acceptedItemIds != null && !_acceptedItemIds.Contains(itemstack.Id))
            {
                return false;
            }

            CollectedItemIds.Add(itemstack.Id);
            itemstack.StackSize = 0;
            return true;
        }
    }
}
