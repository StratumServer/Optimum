#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class RepulseAgentsGateTests : IDisposable
{
    private readonly bool originalGateEnabled = OptimumConfig.RepulsionGateEnabled;

    public RepulseAgentsGateTests()
    {
        OptimumConfig.RepulsionGateEnabled = true;
        OptimumDiagnostics.RepulseAgents.Reset();
    }

    public void Dispose()
    {
        OptimumConfig.RepulsionGateEnabled = originalGateEnabled;
        OptimumDiagnostics.RepulseAgents.Reset();
    }

    [Fact]
    public void CompiledVSEssentials_RepulseAgents_RefreshesDistanceGateWhilePlayerMoves()
    {
        AssertAllScenarios(typeof(EntityBehaviorRepulseAgents).Assembly);
    }

    [Fact]
    public void AssembledRuntimeDonor_RepulseAgents_RefreshesDistanceGateWhilePlayerMoves()
    {
        string root = RepoRoot();
        string vanillaPath = Path.Combine(root, ".vanilla/win-x64/runtime-donors/Mods/VSEssentials.dll");
        string compiledPath = Path.Combine(root, ".build/runtime-donors/VSEssentials/bin/Release/net10.0/VSEssentials.dll");
        if (!File.Exists(vanillaPath) || !File.Exists(compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_repulse_gate_{Guid.NewGuid():N}.dll");
        IsolatedEssentialsLoadContext? alc = null;
        try
        {
            Assert.True(Optimum.Patcher.ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            Assert.True(File.Exists(tempOutput));

            alc = new IsolatedEssentialsLoadContext(tempOutput);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);
            AssertAllScenarios(patchedAsm);
        }
        finally
        {
            alc?.Unload();
            try
            {
                File.Delete(tempOutput);
                string pdb = Path.ChangeExtension(tempOutput, ".pdb");
                if (File.Exists(pdb)) File.Delete(pdb);
            }
            catch
            {
            }
        }
    }

    private static void AssertAllScenarios(Assembly vsEssentialsAsm)
    {
        Type repulseType = vsEssentialsAsm.GetType("Vintagestory.GameContent.EntityBehaviorRepulseAgents", true)!;
        Type ellipsoidalType = vsEssentialsAsm.GetType("Vintagestory.GameContent.EntityBehaviorEllipsoidalRepulseAgents", true)!;
        Type partitioningType = vsEssentialsAsm.GetType("Vintagestory.GameContent.EntityPartitioning", true)!;

        // 1. Far at spawn -> skipped; walks up -> hit and pushed; teleports away -> skipped again.
        var farSpawnEnv = new ClientTestEnv(partitioningType);
        farSpawnEnv.Player.Pos.SetPos(100.0, 64.0, 100.0);
        var agent = farSpawnEnv.CreateAgent(125.0, 64.0, 100.0, width: 0.7f, height: 1.4f, repulseType, "{}");
        farSpawnEnv.SyncPlayerPhysics();

        AssertTick(agent.Behavior, expectedHits: 0, expectedSkips: 1);

        farSpawnEnv.Player.Pos.SetPos(124.65, 64.0, 100.0);
        farSpawnEnv.SyncPlayerPhysics();
        agent.Entity.Pos.Motion.Set(0, 0, 0);
        AssertTick(agent.Behavior, expectedHits: 1, expectedSkips: 0);
        Assert.True(agent.Entity.Pos.Motion.Length() > 0.0, "Agent within repulsion range should receive non-zero push motion");

        farSpawnEnv.Player.Pos.SetPos(85.0, 64.0, 100.0);
        farSpawnEnv.SyncPlayerPhysics();
        AssertTick(agent.Behavior, expectedHits: 0, expectedSkips: 1);

        // 2. Spawned near player -> hits immediately, then skips after player teleports away.
        var nearSpawnEnv = new ClientTestEnv(partitioningType);
        nearSpawnEnv.Player.Pos.SetPos(99.65, 64.0, 100.0);
        var nearAgent = nearSpawnEnv.CreateAgent(100.0, 64.0, 100.0, width: 0.7f, height: 1.4f, repulseType, "{}");
        nearSpawnEnv.SyncPlayerPhysics();

        nearAgent.Entity.Pos.Motion.Set(0, 0, 0);
        AssertTick(nearAgent.Behavior, expectedHits: 1, expectedSkips: 0);
        Assert.True(nearAgent.Entity.Pos.Motion.Length() > 0.0);

        nearSpawnEnv.Player.Pos.SetPos(60.0, 64.0, 100.0);
        nearSpawnEnv.SyncPlayerPhysics();
        AssertTick(nearAgent.Behavior, expectedHits: 0, expectedSkips: 1);

        // 3. Vertical offset is ignored by horizontal gate; horizontal boundary and dynamic resize are honored.
        nearSpawnEnv.Player.Pos.SetPos(99.65, 114.0, 100.0);
        nearSpawnEnv.SyncPlayerPhysics();
        AssertTick(nearAgent.Behavior, expectedHits: 1, expectedSkips: 0);

        // Width 0.7 -> halfWidth = 0.35 -> maxRepulseDist = 4.35
        nearSpawnEnv.Player.Pos.SetPos(95.66, 64.0, 100.0); // dx = 4.34 <= 4.35
        AssertTick(nearAgent.Behavior, expectedHits: 1, expectedSkips: 0);

        nearSpawnEnv.Player.Pos.SetPos(95.64, 64.0, 100.0); // dx = 4.36 > 4.35
        AssertTick(nearAgent.Behavior, expectedHits: 0, expectedSkips: 1);

        // Resize collision box to width 4.0 -> halfWidth = 2.0 -> maxRepulseDist = 6.0 after UpdateColSelBoxes()
        nearAgent.Entity.SetCollisionBox(4.0f, 1.4f);
        nearAgent.Entity.touchDistance = 2.0;
        nearAgent.Behavior.UpdateColSelBoxes();
        nearSpawnEnv.Player.Pos.SetPos(94.1, 64.0, 100.0); // dx = 5.9 <= 6.0
        AssertTick(nearAgent.Behavior, expectedHits: 1, expectedSkips: 0);
        nearSpawnEnv.Player.Pos.SetPos(93.9, 64.0, 100.0); // dx = 6.1 > 6.0
        AssertTick(nearAgent.Behavior, expectedHits: 0, expectedSkips: 1);

        // 4. Dimension mismatch skips even when X/Z match closely.
        nearSpawnEnv.Player.Pos.SetPos(99.65, 64.0, 100.0);
        nearSpawnEnv.Player.Pos.Dimension = 1;
        AssertTick(nearAgent.Behavior, expectedHits: 0, expectedSkips: 1);
        nearSpawnEnv.Player.Pos.Dimension = 0;

        // 5. Subclass omitting base.AfterInitialized (EntityBehaviorEllipsoidalRepulseAgents) lazily initializes maxRepulseDistSq.
        const string boatAttrs = "{\"offset\":{\"x\":0.1,\"y\":0.0,\"z\":1.5},\"radius\":{\"x\":1.9,\"y\":1.5,\"z\":4.6}}";
        var boat = farSpawnEnv.CreateAgent(100.0, 64.0, 120.0, width: 2.5f, height: 0.7f, ellipsoidalType, boatAttrs);
        farSpawnEnv.Player.Pos.SetPos(75.0, 64.0, 120.0);
        AssertTick(boat.Behavior, expectedHits: 0, expectedSkips: 1);

        farSpawnEnv.Player.Pos.SetPos(100.0, 64.0, 113.0); // dz = 7.0 <= 4.6 + 4.0 = 8.6
        AssertTick(boat.Behavior, expectedHits: 1, expectedSkips: 0);

        farSpawnEnv.Player.Pos.SetPos(100.0, 64.0, 110.5); // dz = 9.5 > 8.6
        AssertTick(boat.Behavior, expectedHits: 0, expectedSkips: 1);

        // 6. Exemptions: self-player, config flag off, null cworld.Player, and server side never skip.
        AssertTick(farSpawnEnv.PlayerBehavior, expectedHits: 1, expectedSkips: 0);

        farSpawnEnv.Player.Pos.SetPos(100.0, 64.0, 100.0);
        OptimumConfig.RepulsionGateEnabled = false;
        try
        {
            AssertTick(agent.Behavior, expectedHits: 1, expectedSkips: 0);
        }
        finally
        {
            OptimumConfig.RepulsionGateEnabled = true;
        }

        farSpawnEnv.ClientPlayerProxyTarget = null;
        AssertTick(agent.Behavior, expectedHits: 1, expectedSkips: 0);

        var serverBehavior = farSpawnEnv.CreateServerBehavior(repulseType);
        AssertTick(serverBehavior, expectedHits: 1, expectedSkips: 0);
    }

    private static void AssertTick(EntityBehavior behavior, long expectedHits, long expectedSkips)
    {
        OptimumDiagnostics.RepulseAgents.Reset();
        behavior.OnGameTick(0.05f);
        var snap = OptimumDiagnostics.RepulseAgents.Snapshot();
        Assert.Equal(expectedHits, snap.Hits);
        Assert.Equal(expectedSkips, snap.Skips);
    }

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

    private sealed class ClientTestEnv
    {
        private readonly object partitionUtil;
        private readonly IClientWorldAccessor clientWorld;
        private readonly IWorldAccessor serverWorld;
        private readonly ICoreClientAPI clientApi;
        private readonly ICoreAPI serverApi;
        private readonly TreeAttribute configTree = new();
        private readonly IClientPlayer clientPlayer;
        private readonly FieldInfo playerTargetField;
        private EntityPlayer? currentPlayerTarget;

        public EntityPlayer Player { get; }
        public EntityBehavior PlayerBehavior { get; }
        public EntityPlayer? ClientPlayerProxyTarget
        {
            get => currentPlayerTarget;
            set
            {
                currentPlayerTarget = value;
                playerTargetField.SetValue(clientPlayer, value);
            }
        }

        public ClientTestEnv(Type partitioningType)
        {
            partitionUtil = Activator.CreateInstance(partitioningType)!;
            (clientPlayer, playerTargetField) = ClientPlayerStubFactory.Create();

            var blockAccessor = HighImpactBugFixRegressionTests.StubProxy<IBlockAccessor>.Create((m, _) => m.Name switch
            {
                "get_MapSizeX" or "get_MapSizeZ" => 512,
                "get_MapSizeY" => 256,
                _ => DefaultValue(m.ReturnType)!,
            });

            var modLoader = HighImpactBugFixRegressionTests.StubProxy<IModLoader>.Create((m, _) => m.Name switch
            {
                "GetModSystem" => partitionUtil,
                _ => DefaultValue(m.ReturnType)!,
            });

            clientWorld = HighImpactBugFixRegressionTests.StubProxy<IClientWorldAccessor>.Create((m, _) => m.Name switch
            {
                "get_Side" => EnumAppSide.Client,
                "get_ElapsedMilliseconds" => 10_000L,
                "get_Config" => configTree,
                "get_Player" => clientPlayer,
                "get_BlockAccessor" => blockAccessor,
                _ => DefaultValue(m.ReturnType)!,
            });

            serverWorld = HighImpactBugFixRegressionTests.StubProxy<IWorldAccessor>.Create((m, _) => m.Name switch
            {
                "get_Side" => EnumAppSide.Server,
                "get_ElapsedMilliseconds" => 10_000L,
                "get_Config" => configTree,
                "get_BlockAccessor" => blockAccessor,
                _ => DefaultValue(m.ReturnType)!,
            });

            clientApi = HighImpactBugFixRegressionTests.StubProxy<ICoreClientAPI>.Create((m, _) => m.Name switch
            {
                "get_World" => clientWorld,
                "get_Side" => EnumAppSide.Client,
                "get_ModLoader" => modLoader,
                _ => DefaultValue(m.ReturnType)!,
            });

            serverApi = HighImpactBugFixRegressionTests.StubProxy<ICoreAPI>.Create((m, _) => m.Name switch
            {
                "get_World" => serverWorld,
                "get_Side" => EnumAppSide.Server,
                "get_ModLoader" => modLoader,
                _ => DefaultValue(m.ReturnType)!,
            });

            FieldInfo? apiField = partitioningType.GetField("api", BindingFlags.Instance | BindingFlags.NonPublic);
            apiField?.SetValue(partitionUtil, clientApi);

            Player = new EntityPlayer
            {
                World = clientWorld,
                Api = clientApi,
                State = EnumEntityState.Active,
                OnGround = true,
            };
            ConfigureBox(Player, 0.6f, 1.85f);
            PlayerBehavior = AttachBehavior(Player, partitioningType.Assembly.GetType("Vintagestory.GameContent.EntityBehaviorRepulseAgents", true)!, "{}");
            ClientPlayerProxyTarget = Player;
        }

        public (EntityAgent Entity, EntityBehavior Behavior) CreateAgent(
            double x, double y, double z, float width, float height, Type behaviorType, string attributesJson)
        {
            var entity = new EntityAgent
            {
                World = clientWorld,
                Api = clientApi,
                State = EnumEntityState.Active,
                OnGround = true,
            };
            entity.Pos.SetPos(x, y, z);
            ConfigureBox(entity, width, height);
            EntityBehavior bh = AttachBehavior(entity, behaviorType, attributesJson);
            return (entity, bh);
        }

        public EntityBehavior CreateServerBehavior(Type behaviorType)
        {
            var entity = new EntityAgent
            {
                World = serverWorld,
                Api = serverApi,
                State = EnumEntityState.Active,
                OnGround = true,
            };
            entity.Pos.SetPos(500.0, 64.0, 500.0);
            ConfigureBox(entity, 0.7f, 1.4f);
            return AttachBehavior(entity, behaviorType, "{}");
        }

        public void SyncPlayerPhysics()
        {
            Player.AfterPhysicsTick?.Invoke();
        }

        private static void ConfigureBox(Entity entity, float width, float height)
        {
            entity.SetCollisionBox(width, height);
            entity.SetSelectionBox(width, height);
            entity.touchDistance = width * 0.5;
            entity.touchDistanceSq = entity.touchDistance * entity.touchDistance;
        }

        private static EntityBehavior AttachBehavior(Entity entity, Type behaviorType, string attributesJson)
        {
            var bh = (EntityBehavior)Activator.CreateInstance(behaviorType, entity)!;
            var props = new EntityProperties();
            bh.Initialize(props, new JsonObject(JToken.Parse(attributesJson)));
            bh.AfterInitialized(onFirstSpawn: true);
            entity.AfterPhysicsTick?.Invoke();
            return bh;
        }

        private static object? DefaultValue(Type type)
        {
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private static class ClientPlayerStubFactory
    {
        private static readonly (Type StubType, FieldInfo TargetField) Compiled = BuildStubType();

        public static (IClientPlayer Instance, FieldInfo TargetField) Create()
        {
            var instance = (IClientPlayer)Activator.CreateInstance(Compiled.StubType)!;
            return (instance, Compiled.TargetField);
        }

        private static (Type StubType, FieldInfo TargetField) BuildStubType()
        {
            AssemblyBuilder asm = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("RepulseAgentsClientPlayerStub"),
                AssemblyBuilderAccess.Run);
            ModuleBuilder mod = asm.DefineDynamicModule("Main");

            TypeBuilder attrTb = mod.DefineType(
                "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                typeof(Attribute));
            FieldBuilder attrField = attrTb.DefineField("_assemblyName", typeof(string), FieldAttributes.Private);
            ConstructorBuilder attrCtor = attrTb.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                new[] { typeof(string) });
            ILGenerator attrIl = attrCtor.GetILGenerator();
            attrIl.Emit(OpCodes.Ldarg_0);
            attrIl.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
            attrIl.Emit(OpCodes.Ldarg_0);
            attrIl.Emit(OpCodes.Ldarg_1);
            attrIl.Emit(OpCodes.Stfld, attrField);
            attrIl.Emit(OpCodes.Ret);
            Type attrType = attrTb.CreateType()!;

            asm.SetCustomAttribute(new CustomAttributeBuilder(
                attrType.GetConstructor(new[] { typeof(string) })!,
                new object[] { typeof(IClientPlayer).Assembly.GetName().Name! }));

            TypeBuilder tb = mod.DefineType(
                "StubClientPlayer",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
            tb.AddInterfaceImplementation(typeof(IClientPlayer));
            FieldBuilder entityField = tb.DefineField("_entity", typeof(EntityPlayer), FieldAttributes.Public);

            foreach (Type iface in new[] { typeof(IClientPlayer), typeof(IPlayer) })
            {
                foreach (MethodInfo method in iface.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Type[] paramTypes = Array.ConvertAll(method.GetParameters(), p => p.ParameterType);
                    MethodBuilder mb = tb.DefineMethod(
                        $"{iface.FullName}.{method.Name}",
                        MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                        method.ReturnType,
                        paramTypes);
                    ILGenerator il = mb.GetILGenerator();
                    if (method.Name == "get_Entity" && paramTypes.Length == 0)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, entityField);
                    }
                    else if (method.ReturnType != typeof(void))
                    {
                        if (method.ReturnType.IsValueType)
                        {
                            LocalBuilder loc = il.DeclareLocal(method.ReturnType);
                            il.Emit(OpCodes.Ldloca_S, loc);
                            il.Emit(OpCodes.Initobj, method.ReturnType);
                            il.Emit(OpCodes.Ldloc_S, loc);
                        }
                        else
                        {
                            il.Emit(OpCodes.Ldnull);
                        }
                    }
                    il.Emit(OpCodes.Ret);
                    tb.DefineMethodOverride(mb, method);
                }
            }

            Type stubType = tb.CreateType()!;
            return (stubType, stubType.GetField("_entity", BindingFlags.Public | BindingFlags.Instance)!);
        }
    }

    private sealed class IsolatedEssentialsLoadContext : AssemblyLoadContext
    {
        private readonly string patchedEssentialsPath;

        public IsolatedEssentialsLoadContext(string patchedEssentialsPath)
            : base(isCollectible: true)
        {
            this.patchedEssentialsPath = patchedEssentialsPath;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "VSEssentials", StringComparison.OrdinalIgnoreCase))
            {
                return LoadFromAssemblyPath(patchedEssentialsPath);
            }
            return null;
        }
    }
}
