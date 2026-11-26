#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

// Issue #769: BetterEntityInteraction 1.0.5 transpiles GameMain.RayTraceForSelection(6)
// by locating the `blockSelection = null;` store (ldarg.3; ldnull; stind.ref), scanning for
// `Vec3d.SquareDistanceTo(Vec3d)`, and walking backwards to the preceding `ldarg.2` (`ray`)
// to inject its plant/snow/leaves penetration jump. Reusing a per-instance scratch Vec3d keeps
// the zero-allocation hot path from issue #132 while preserving that exact vanilla IL anchor.
public sealed class GameMainRayTraceCompatTests
{
    private const string GameMainPatchPath =
        "patches/VintagestoryLib/Vintagestory.Common/GameMain.cs.patch";

    [Fact]
    public void SourcePatchAndPatcher_UseScratchVec3dAndRegisterField()
    {
        string patch = ReadRepoFile(GameMainPatchPath);
        Assert.Contains("private Vec3d optimumRayTraceHitPosTmp;", patch);
        Assert.Contains("Vec3d hitPos = optimumRayTraceHitPosTmp ??= new Vec3d();", patch);
        Assert.Contains("float num4 = ray.origin.SquareDistanceTo(hitPos);", patch);
        Assert.Contains("float num5 = ray.origin.SquareDistanceTo(hitPos);", patch);

        string program = ReadRepoFile("Optimum.Patcher/Program.cs");
        Assert.Contains("\"optimumRayTraceHitPosTmp\"", program);
        Assert.Contains("new(\"Vintagestory.Common.GameMain\", \"RayTraceForSelection\", 6,", program);
    }

    [Fact]
    public void CompiledAndShippedRayTraceForSelection_PreserveBetterEntityInteractionTranspilerAnchors()
    {
        string? compiledLibPath = FindRepoFileIfExists(
            "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (compiledLibPath != null)
        {
            using var compiled = AssemblyDefinition.ReadAssembly(compiledLibPath);
            AssertRayTraceTranspilerAnchors(compiled.MainModule, "compiled VintagestoryLib.dll");
        }

        string? shippedPatchedPath = FindRepoFileIfExists(
            "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll");
        if (shippedPatchedPath != null)
        {
            using var shipped = AssemblyDefinition.ReadAssembly(shippedPatchedPath);
            AssertRayTraceTranspilerAnchors(shipped.MainModule, "VintagestoryLib-patched.dll");
        }
    }

    [Fact]
    public void RayTraceForSelection_DistanceComparison_MatchesVanillaAndReusesScratchVec3d()
    {
        string? donorPath = FindRepoFileIfExists(
            "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (donorPath == null)
        {
            return;
        }

        Assembly donorAsm = Assembly.LoadFrom(donorPath);
        Type gameMainType = donorAsm.GetType("Vintagestory.Common.GameMain", throwOnError: true)!;
        Type clientMainType = donorAsm.GetType("Vintagestory.Client.NoObf.ClientMain", throwOnError: true)!;

        FieldInfo scratchField = gameMainType.GetField(
            "optimumRayTraceHitPosTmp",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo entitySelTmpField = gameMainType.GetField(
            "entitySelTmp",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo intersectionTesterField = gameMainType.GetField(
            "interesectionTester",
            BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo rayTraceMethod = gameMainType.GetMethod(
            "RayTraceForSelection",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(IWorldIntersectionSupplier),
                typeof(Ray),
                typeof(BlockSelection).MakeByRefType(),
                typeof(EntitySelection).MakeByRefType(),
                typeof(BlockFilter),
                typeof(EntityFilter),
            ],
            modifiers: null)!;

        Assert.NotNull(scratchField);
        Assert.NotNull(entitySelTmpField);
        Assert.NotNull(intersectionTesterField);
        Assert.NotNull(rayTraceMethod);

        Block solidBlock = new Block
        {
            CollisionBoxes = [new Cuboidf(0f, 0f, 0f, 1f, 1f, 1f)],
            SelectionBoxes = [new Cuboidf(0f, 0f, 0f, 1f, 1f, 1f)],
        };

        // Block at (3, 64, 0) spans x in [3, 4], between origin (0.5, 64.5, 0.5) and fartherEntity.
        var blockPos = new BlockPos(3, 64, 0, 0);
        var airBlock = new Block
        {
            SideSolid = default,
            SelectionBoxes = null,
            CollisionBoxes = null,
        };
        var closerEntity = new HitTestEntity(10, 2.0, 64.5, 0.5, hitDistance: 1.5);
        var fartherEntity = new HitTestEntity(11, 5.0, 64.5, 0.5, hitDistance: 4.5);
        Entity[] currentEntities = [closerEntity];

        IBlockAccessor blockAccessor = CreateProxy<IBlockAccessor>((method, _) =>
            method?.Name == "GetBlock"
                ? airBlock
                : method?.ReturnType == typeof(void) || method?.ReturnType.IsValueType != true
                    ? null
                    : Activator.CreateInstance(method.ReturnType));

        IWorldIntersectionSupplier supplier = CreateProxy<IWorldIntersectionSupplier>((method, args) =>
            method?.Name switch
            {
                "GetBlock" when args is { Length: >= 1 } && args[0] is BlockPos p && p.Equals(blockPos) => solidBlock,
                "GetBlock" => airBlock,
                "GetBlockIntersectionBoxes" when args is { Length: >= 1 } && args[0] is BlockPos p && p.Equals(blockPos) => solidBlock.SelectionBoxes,
                "GetBlockIntersectionBoxes" => Array.Empty<Cuboidf>(),
                "IsValidPos" => true,
                "GetEntitiesAround" => currentEntities,
                "get_blockAccessor" => blockAccessor,
                _ => method?.ReturnType == typeof(void) || method?.ReturnType.IsValueType != true
                    ? null
                    : Activator.CreateInstance(method.ReturnType),
            });

        object game = RuntimeHelpers.GetUninitializedObject(clientMainType);
        entitySelTmpField.SetValue(game, new EntitySelection());
        intersectionTesterField.SetValue(game, new AABBIntersectionTest(supplier));

        var ray = Ray.FromPositions(new Vec3d(0.5, 64.5, 0.5), new Vec3d(6.5, 64.5, 0.5));

        // 1. Entity closer than block: entitySelection wins, blockSelection becomes null.
        object?[] args1 = [supplier, ray, null, null, null, null];
        rayTraceMethod.Invoke(game, args1);
        Assert.Null(args1[2]);
        var entitySel1 = Assert.IsType<EntitySelection>(args1[3]);
        Assert.Same(closerEntity, entitySel1.Entity);

        Vec3d? scratchAfterFirst = (Vec3d?)scratchField.GetValue(game);
        Assert.NotNull(scratchAfterFirst);

        // 2. Block closer than entity: blockSelection wins, entitySelection stays null.
        currentEntities = [fartherEntity];
        object?[] args2 = [supplier, ray, null, null, null, null];
        rayTraceMethod.Invoke(game, args2);
        var blockSel2 = Assert.IsType<BlockSelection>(args2[2]);
        Assert.Equal(blockPos, blockSel2.Position);
        Assert.Null(args2[3]);

        // Scratch Vec3d instance is reused across calls instead of reallocating per raytrace.
        Vec3d? scratchAfterSecond = (Vec3d?)scratchField.GetValue(game);
        Assert.Same(scratchAfterFirst, scratchAfterSecond);
    }

    private static void AssertRayTraceTranspilerAnchors(ModuleDefinition module, string sourceLabel)
    {
        var gameMain = module.GetType("Vintagestory.Common.GameMain");
        Assert.NotNull(gameMain);

        var method = gameMain!.Methods.Single(m =>
            !m.IsStatic
            && m.Name == "RayTraceForSelection"
            && m.Parameters.Count == 6
            && m.Parameters[0].ParameterType.FullName == "Vintagestory.API.MathTools.IWorldIntersectionSupplier");

        var instructions = method.Body.Instructions;

        // 1. BEI destination anchor: `ldarg.3` (or `ldarg.s 3`), `ldnull`, `stind.ref`.
        int destinationIndex = -1;
        for (int i = 0; i < instructions.Count - 2; i++)
        {
            if (!IsLdarg3(instructions[i])
                || instructions[i + 1].OpCode != OpCodes.Ldnull
                || instructions[i + 2].OpCode != OpCodes.Stind_Ref)
            {
                continue;
            }

            destinationIndex = i;
            break;
        }

        Assert.True(destinationIndex >= 0, $"{sourceLabel}: missing BEI destination anchor (blockSelection = null).");

        // 2. BEI insertion anchor: first call to Vec3d.SquareDistanceTo(Vec3d), walking back to `ldarg.2`.
        int sqDistCallCount = 0;
        int firstSqDistIndex = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (IsVec3dSquareDistanceToVec3dCall(instructions[i]))
            {
                if (firstSqDistIndex < 0)
                {
                    firstSqDistIndex = i;
                }
                sqDistCallCount++;
            }
        }

        Assert.Equal(2, sqDistCallCount);
        Assert.True(firstSqDistIndex > 0, $"{sourceLabel}: missing Vec3d.SquareDistanceTo(Vec3d) call.");

        int injectionIndex = -1;
        for (int j = firstSqDistIndex; j >= 0; j--)
        {
            if (IsLdarg2(instructions[j]))
            {
                injectionIndex = j;
                break;
            }
        }

        Assert.True(injectionIndex >= 0, $"{sourceLabel}: missing ldarg.2 before SquareDistanceTo(Vec3d).");
        Assert.True(
            injectionIndex < destinationIndex,
            $"{sourceLabel}: injectionIndex ({injectionIndex}) must precede destinationIndex ({destinationIndex}).");

        // The ldarg.2 anchor must be the start of `ray.origin.SquareDistanceTo(hitPos)`.
        Assert.Equal(OpCodes.Ldfld, instructions[injectionIndex + 1].OpCode);
        Assert.Equal("origin", ((FieldReference)instructions[injectionIndex + 1].Operand).Name);

        // 3. Simulate BEI's exact IL injection and verify IL stack balance passes IlStackVerifier.
        var il = method.Body.GetILProcessor();
        var bypassTarget = instructions[destinationIndex];
        var anchor = instructions[injectionIndex];
        var predicateRef = module.ImportReference(typeof(GameMainRayTraceCompatTests).GetMethod(
            nameof(SimulatedCanPenetrate),
            BindingFlags.Static | BindingFlags.NonPublic)!);

        il.InsertBefore(anchor, il.Create(OpCodes.Ldarg_3));
        il.InsertBefore(anchor, il.Create(OpCodes.Ldind_Ref));
        il.InsertBefore(anchor, il.Create(OpCodes.Call, predicateRef));
        il.InsertBefore(anchor, il.Create(OpCodes.Brtrue, bypassTarget));

        var verificationErrors = IlStackVerifier.VerifyMethod(method);
        Assert.Empty(verificationErrors);
    }

    private static bool SimulatedCanPenetrate(BlockSelection? blockSelection) => blockSelection != null;

    private static bool IsLdarg2(Instruction instr) =>
        instr.OpCode == OpCodes.Ldarg_2
        || (instr.OpCode == OpCodes.Ldarga_S && instr.Operand is ParameterDefinition p && p.Index == 1);

    private static bool IsLdarg3(Instruction instr) =>
        instr.OpCode == OpCodes.Ldarg_3
        || (instr.OpCode == OpCodes.Ldarg_S && instr.Operand is ParameterDefinition p && p.Index == 2);

    private static bool IsVec3dSquareDistanceToVec3dCall(Instruction instr)
    {
        if ((instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt)
            || instr.Operand is not MethodReference mr)
        {
            return false;
        }

        return mr.DeclaringType.FullName == "Vintagestory.API.MathTools.Vec3d"
            && mr.Name == "SquareDistanceTo"
            && mr.Parameters.Count == 1
            && mr.Parameters[0].ParameterType.FullName == "Vintagestory.API.MathTools.Vec3d";
    }

    private sealed class HitTestEntity : Entity
    {
        private readonly double hitDistance;

        public HitTestEntity(long entityId, double x, double y, double z, double hitDistance)
        {
            EntityId = entityId;
            State = EnumEntityState.Active;
            Pos.SetPos(x, y, z);
            this.hitDistance = hitDistance;
        }

        public override bool IntersectsRay(Ray ray, AABBIntersectionTest intersectionTester, out double intersectionDistance, ref int selectionBoxIndex)
        {
            intersectionDistance = hitDistance;
            selectionBoxIndex = 0;
            intersectionTester.hitPosition.Set(Pos.X, Pos.Y, Pos.Z);
            intersectionTester.hitOnBlockFace = BlockFacing.WEST;
            return true;
        }
    }

    public class CompatDispatchProxy : DispatchProxy
    {
        public System.Func<MethodInfo?, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod, args);
    }

    private static T CreateProxy<T>(System.Func<MethodInfo?, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, CompatDispatchProxy>();
        ((CompatDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static string ReadRepoFile(string relativePath)
    {
        string? path = FindRepoFileIfExists(relativePath);
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    private static string? FindRepoFileIfExists(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
