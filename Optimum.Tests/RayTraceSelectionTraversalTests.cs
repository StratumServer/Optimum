#if !NO_DONOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Xunit;

namespace Optimum.Tests;

public sealed class RayTraceSelectionTraversalTests
{
    private static readonly MethodInfo FindNearestMethod = typeof(GameMain).GetMethod(
        "FindNearestRayIntersectingEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(16d, 64d, 16d, 36f, true)]
    [InlineData(16d, 64d, 16d, 36f, false)]
    [InlineData(-16d, -16d, -16d, 20f, true)]
    [InlineData(-16d, -16d, -16d, 20f, false)]
    public void Direct_chunk_traversal_matches_vanilla_candidates(
        double originX,
        double originY,
        double originZ,
        float range,
        bool filterEntity)
    {
        Ray ray = new();
        ray.origin.Set(originX, originY, originZ);

        Entity primary = new RayTraceTestEntity(1, originX + 2, originY + 2, originZ + 2, true, 2.0);
        Entity filtered = new RayTraceTestEntity(2, originX + 3, originY + 2, originZ + 2, true, 0.5);
        Entity despawned = new RayTraceTestEntity(3, originX + 4, originY + 2, originZ + 2, true, 0.25)
        {
            State = EnumEntityState.Despawned,
        };
        Entity hiddenAfterNull = new RayTraceTestEntity(4, originX + 5, originY + 2, originZ + 2, true, 0.1);
        Entity equalHitInLaterChunk = new RayTraceTestEntity(5, originX + range, originY, originZ, true, 2.0);
        Entity outsideRangeInsideScannedChunk = new RayTraceTestEntity(6, originX + range + 1, originY + 2, originZ + 2, true, 0.0);

        (int X, int Y, int Z) firstChunk = ChunkAt(primary);
        (int X, int Y, int Z) laterChunk = ChunkAt(equalHitInLaterChunk);
        (int X, int Y, int Z) outsideChunk = ChunkAt(outsideRangeInsideScannedChunk);
        Assert.NotEqual(firstChunk, laterChunk);
        Assert.Equal(laterChunk, outsideChunk);

        var chunks = new Dictionary<(int X, int Y, int Z), IWorldChunk>
        {
            [firstChunk] = CreateChunk([primary, filtered, despawned, null!, hiddenAfterNull], entitiesCount: 3),
            [laterChunk] = CreateChunk([equalHitInLaterChunk, outsideRangeInsideScannedChunk], entitiesCount: 2),
        };
        TestGameMain game = new(chunks);
        EntityFilter efilter = filterEntity ? entity => entity.EntityId != filtered.EntityId : null;

        (Entity Selected, double Distance) expected = SelectWithVanillaCandidateQuery(game, ray, range, efilter);
        foreach (RayTraceTestEntity candidate in new[]
                 {
                     (RayTraceTestEntity)primary,
                     (RayTraceTestEntity)filtered,
                     (RayTraceTestEntity)despawned,
                     (RayTraceTestEntity)hiddenAfterNull,
                     (RayTraceTestEntity)equalHitInLaterChunk,
                     (RayTraceTestEntity)outsideRangeInsideScannedChunk,
                 })
        {
            candidate.RayIntersectionCalls = 0;
        }

        object?[] arguments = [ray, range, efilter, null, double.MaxValue];
        FindNearestMethod.Invoke(game, arguments);

        Assert.Same(expected.Selected, arguments[3]);
        Assert.Equal(expected.Distance, (double)arguments[4]!);
        Assert.Same(filterEntity ? primary : filtered, arguments[3]);
        Assert.Equal(filterEntity ? 0 : 1, ((RayTraceTestEntity)filtered).RayIntersectionCalls);
        Assert.Equal(0, ((RayTraceTestEntity)despawned).RayIntersectionCalls);
        Assert.Equal(0, ((RayTraceTestEntity)hiddenAfterNull).RayIntersectionCalls);
        Assert.Equal(1, ((RayTraceTestEntity)equalHitInLaterChunk).RayIntersectionCalls);
        Assert.Equal(0, ((RayTraceTestEntity)outsideRangeInsideScannedChunk).RayIntersectionCalls);
    }

    private static (Entity Selected, double Distance) SelectWithVanillaCandidateQuery(
        GameMain game,
        Ray ray,
        float range,
        EntityFilter efilter)
    {
        ActionConsumable<Entity> matches = entity => efilter == null || efilter(entity);
        Entity[] entities = game.GetEntitiesAround(ray.origin, range, range, matches);
        Entity selected = null;
        double nearestDistance = double.MaxValue;
        int selectionBoxIndex = 0;
        foreach (Entity entity in entities)
        {
            if (entity.IntersectsRay(ray, game.interesectionTester, out double intersectionDistance, ref selectionBoxIndex) && intersectionDistance < nearestDistance)
            {
                selected = entity;
                nearestDistance = intersectionDistance;
            }
        }
        return (selected, nearestDistance);
    }

    private static (int X, int Y, int Z) ChunkAt(Entity entity) =>
        ((int)Math.Floor(entity.Pos.X / 32.0), (int)Math.Floor(entity.Pos.InternalY / 32.0), (int)Math.Floor(entity.Pos.Z / 32.0));

    private static IWorldChunk CreateChunk(Entity[] entities, int entitiesCount)
    {
        return CreateProxy<IWorldChunk>((method, _) => method?.Name switch
        {
            "get_Entities" => entities,
            "get_EntitiesCount" => entitiesCount,
            _ => DefaultValue(method?.ReturnType),
        });
    }

    private sealed class RayTraceTestEntity : Entity
    {
        private readonly bool intersects;
        private readonly double hitDistance;

        public int RayIntersectionCalls { get; set; }

        public RayTraceTestEntity(long entityId, double x, double y, double z, bool intersects, double hitDistance)
        {
            EntityId = entityId;
            State = EnumEntityState.Active;
            Pos.SetPos(x, y, z);
            this.intersects = intersects;
            this.hitDistance = hitDistance;
        }

        public override bool IntersectsRay(Ray ray, AABBIntersectionTest intersectionTester, out double intersectionDistance, ref int selectionBoxIndex)
        {
            RayIntersectionCalls++;
            intersectionDistance = hitDistance;
            selectionBoxIndex = 0;
            intersectionTester.hitPosition.Set(Pos.X, Pos.InternalY, Pos.Z);
            return intersects;
        }
    }

    private sealed class TestGameMain : GameMain
    {
        private readonly IWorldAccessor world;
        private readonly IBlockAccessor accessor;

        public override ClassRegistry ClassRegistryInt { get; set; }
        public override IWorldAccessor World => world;
        protected override WorldMap worldmap => null;
        public override IBlockAccessor blockAccessor => accessor;

        public TestGameMain(Dictionary<(int X, int Y, int Z), IWorldChunk> chunks)
        {
            accessor = CreateProxy<IBlockAccessor>((method, args) =>
            {
                if (method?.Name == "GetChunk" && args is { Length: >= 3 })
                {
                    var key = ((int)args[0]!, (int)args[1]!, (int)args[2]!);
                    return chunks.TryGetValue(key, out IWorldChunk chunk) ? chunk : null;
                }
                return DefaultValue(method?.ReturnType);
            });
            world = CreateProxy<IWorldAccessor>((method, _) =>
                method?.Name == "get_BlockAccessor" ? accessor : DefaultValue(method?.ReturnType));
        }

        public override Block GetBlock(BlockPos pos) => null;

        public override bool IsValidPos(BlockPos pos) => true;
    }

    public class TestDispatchProxy : DispatchProxy
    {
        public System.Func<MethodInfo?, object?[]?, object?> Handler { get; set; }

        protected override object Invoke(MethodInfo targetMethod, object[] args) => Handler(targetMethod, args);
    }

    private static T CreateProxy<T>(System.Func<MethodInfo?, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, TestDispatchProxy>();
        ((TestDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object DefaultValue(Type returnType) =>
        returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
}
#endif
