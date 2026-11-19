#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
public sealed class FirepitIdleRendererTests
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
    public void FirepitIdleRenderer_DefaultsTrue_AndConfigRoundTrips()
    {
        bool orig = OptimumConfig.FirepitIdleRendererEnabled;
        string dir = Path.Combine(Path.GetTempPath(), "optimum-firepit-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(OptimumConfig.FirepitIdleRendererEnabled);
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.FirepitIdleRenderer));

            OptimumConfig.SetDataPath(dir);
            OptimumConfig.FirepitIdleRendererEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.FirepitIdleRendererEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.FirepitIdleRendererEnabled);

            OptimumConfig.FirepitIdleRendererEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.FirepitIdleRendererEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.FirepitIdleRendererEnabled);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.FirepitIdleRendererEnabled = orig;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void FirepitIdleRenderer_ForkAndRuntimePatchesAndManifestAreComplete()
    {
        string forkFirepitPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/BlockEntity/Firepit/BEFirepit.cs.patch"));
        string forkRendererPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/BlockEntityRenderer/FirepitContentsRenderer.cs.patch"));
        string runtimeFirepitPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityFirepit.cs.patch"));
        string runtimeRendererPatch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/FirepitContentsRenderer.cs.patch"));

        Assert.Contains("SyncClientRendererAndTickState", forkFirepitPatch);
        Assert.Contains("DisposeIdleRenderer", forkFirepitPatch);
        Assert.Contains("FirepitIdleRendererEnabled", forkFirepitPatch);
        Assert.Contains("HasActiveContent", forkRendererPatch);

        Assert.Contains("SyncClientRendererAndTickState", runtimeFirepitPatch);
        Assert.Contains("DisposeIdleRenderer", runtimeFirepitPatch);
        Assert.Contains("FirepitIdleRendererEnabled", runtimeFirepitPatch);
        Assert.Contains("HasActiveContent", runtimeRendererPatch);

        var lambdaPattern = new Regex(@"^\+.*=>", RegexOptions.Multiline);
        Assert.DoesNotMatch(lambdaPattern, runtimeFirepitPatch);
        Assert.DoesNotMatch(lambdaPattern, runtimeRendererPatch);

        var manifestMethod = typeof(Optimum.Patcher.ModPatcher)
            .GetMethod("SurvivalManifest", BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;

        var members = (Dictionary<string, List<string>>)manifest.GetType().GetProperty("Members")!.GetValue(manifest)!;
        Assert.Contains("rendererRegistered", members["Vintagestory.GameContent.BlockEntityFirepit"]);
        Assert.Contains("burnTickListenerId", members["Vintagestory.GameContent.BlockEntityFirepit"]);
        Assert.Contains("EnsureRenderer", members["Vintagestory.GameContent.BlockEntityFirepit"]);
        Assert.Contains("SyncClientRendererAndTickState", members["Vintagestory.GameContent.BlockEntityFirepit"]);
        Assert.Contains("ClearIdleRenderer", members["Vintagestory.GameContent.BlockEntityFirepit"]);
        Assert.Contains("DisposeIdleRenderer", members["Vintagestory.GameContent.BlockEntityFirepit"]);
        Assert.Contains("HasActiveContent", members["Vintagestory.GameContent.FirepitContentsRenderer"]);

        var methods = (IEnumerable<Optimum.Patcher.MethodTarget>)manifest.GetType().GetProperty("Methods")!.GetValue(manifest)!;
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "Initialize" && m.ParamCount == 1);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "OnSetTransform" && m.ParamCount == 3);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "OnBlockRemoved" && m.ParamCount == 0);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BlockEntityFirepit" && m.MethodName == "OnBlockUnloaded" && m.ParamCount == 0);
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.FirepitContentsRenderer" && m.MethodName == "Dispose" && m.ParamCount == 0);
    }

    [Fact]
    public void FirepitIdleRenderer_BlockEntityLifecycle_SkipsIdleClientRendererAndTicksWhenEnabled()
    {
        string survivalDll = FindDll("bin/Release/net10.0/VSSurvivalMod.dll");
        Assert.True(File.Exists(survivalDll), $"Missing VSSurvivalMod.dll at {survivalDll}");

        using (var asm = AssemblyDefinition.ReadAssembly(survivalDll))
        {
            TypeDefinition beTypeDef = asm.MainModule.GetType("Vintagestory.GameContent.BlockEntityFirepit");
            Assert.NotNull(beTypeDef);
            Assert.Contains(beTypeDef.Fields, f => f.Name == "rendererRegistered");
            Assert.Contains(beTypeDef.Fields, f => f.Name == "burnTickListenerId");
            Assert.Contains(beTypeDef.Methods, m => m.Name == "SyncClientRendererAndTickState");
            Assert.Contains(beTypeDef.Methods, m => m.Name == "DisposeIdleRenderer");
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

        string tempOut = Path.Combine(Path.GetTempPath(), "optimum-firepit-donor-" + Guid.NewGuid().ToString("N") + ".dll");
        bool orig = OptimumConfig.FirepitIdleRendererEnabled;
        try
        {
            if (vanillaDonor != null && compiledDonor != null && File.Exists(vanillaDonor) && File.Exists(compiledDonor))
            {
                Assert.True(Optimum.Patcher.ModPatcher.Patch("vssurvivalmod", vanillaDonor, compiledDonor, tempOut));
                Assert.True(File.Exists(tempOut));
            }

            string targetSurvivalDll = File.Exists(tempOut) ? tempOut : survivalDll;
            string survivalDir = Path.GetDirectoryName(survivalDll)!;
            var alc = new AssemblyLoadContext("FirepitLifecycleTest", isCollectible: true);
            alc.Resolving += (_, name) =>
            {
                if (name.Name == "VintagestoryAPI") return typeof(ICoreClientAPI).Assembly;
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

                // 1. Enabled on empty/idle client firepit: 0 renderers, 0 firepit tick listeners (only 10 s container OnTick), safe unload/remove.
                OptimumConfig.FirepitIdleRendererEnabled = true;
                var clientHarness = new FirepitApiHarness(EnumAppSide.Client);
                var beIdle = (BlockEntity)Activator.CreateInstance(firepitType)!;
                beIdle.Pos = new BlockPos(10, 64, 10, 0);
                beIdle.Block = new Block { Code = new AssetLocation("game", "firepit-cold") };
                beIdle.Initialize(clientHarness.CreateClientApi());

                Assert.Equal(0, clientHarness.ActiveRendererCount);
                Assert.Equal(0, clientHarness.CountTickListeners(100));
                Assert.Equal(0, clientHarness.CountTickListeners(500));
                Assert.Equal(1, clientHarness.CountTickListeners(10000));

                // Calling OnSetTransform with non-matching target exits cleanly while renderer == null.
                var tfTree = new TreeAttribute();
                tfTree.SetString("target", "otherTransform");
                MethodInfo onSetTf = firepitType.GetMethod("OnSetTransform", BindingFlags.NonPublic | BindingFlags.Instance)!;
                EnumHandling handling = EnumHandling.PassThrough;
                onSetTf.Invoke(beIdle, new object?[] { "onsettransform", handling, tfTree });
                Assert.Equal(0, clientHarness.ActiveRendererCount);

                // Cold -> Hot -> Cold transition via IInFirepitRendererSupplier (pot / crucible path).
                Type ifaceType = modAsm.GetType("Vintagestory.GameContent.IInFirepitRendererSupplier", throwOnError: true)!;
                Type dynamicPotType = DynamicPotTypeBuilder.GetOrCreate(modAsm, ifaceType);
                var potBlock = (Block)Activator.CreateInstance(dynamicPotType)!;
                potBlock.Code = new AssetLocation("game", "cookingpot-cooked");
                var hotPotStack = new ItemStack(potBlock);

                FieldInfo invField = firepitType.GetField("inventory", BindingFlags.NonPublic | BindingFlags.Instance)!;
                FieldInfo tempField = firepitType.GetField("furnaceTemperature", BindingFlags.Public | BindingFlags.Instance)!;
                MethodInfo updateRenderer = firepitType.GetMethod("UpdateRenderer", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var inv = (InventoryBase)invField.GetValue(beIdle)!;
                inv[1].Itemstack = hotPotStack;
                tempField.SetValue(beIdle, 20f);
                updateRenderer.Invoke(beIdle, null);
                Assert.Equal(0, clientHarness.ActiveRendererCount);
                Assert.Equal(0, clientHarness.CountTickListeners(100));

                tempField.SetValue(beIdle, 800f);
                updateRenderer.Invoke(beIdle, null);
                Assert.Equal(1, clientHarness.ActiveRendererCount);
                Assert.Equal(1, clientHarness.CountTickListeners(100));

                tempField.SetValue(beIdle, 20f);
                updateRenderer.Invoke(beIdle, null);
                Assert.Equal(0, clientHarness.ActiveRendererCount);
                Assert.Equal(0, clientHarness.CountTickListeners(100));

                beIdle.OnBlockUnloaded();
                beIdle.OnBlockRemoved();
                Assert.Equal(0, clientHarness.ActiveRendererCount);
                Assert.Equal(0, clientHarness.ActiveTickListenerCount);

                // 2. Opt-out disabled on client: restores eager renderer + both 100 ms and 500 ms tick listeners.
                OptimumConfig.FirepitIdleRendererEnabled = false;
                var optOutHarness = new FirepitApiHarness(EnumAppSide.Client);
                var beOptOut = (BlockEntity)Activator.CreateInstance(firepitType)!;
                beOptOut.Pos = new BlockPos(11, 64, 11, 0);
                beOptOut.Block = new Block { Code = new AssetLocation("game", "firepit-cold") };
                beOptOut.Initialize(optOutHarness.CreateClientApi());

                Assert.Equal(1, optOutHarness.ActiveRendererCount);
                Assert.Equal(1, optOutHarness.CountTickListeners(100));
                Assert.Equal(1, optOutHarness.CountTickListeners(500));
                Assert.Equal(3, optOutHarness.ActiveTickListenerCount);

                // 3. Client-only FirepitIdleRendererEnabled with server idle tick paused disabled: registers both 100 ms and 500 ms server listeners and 0 renderers.
                bool origServerTick = OptimumConfig.FirepitIdleServerTickEnabled;
                OptimumConfig.FirepitIdleRendererEnabled = true;
                OptimumConfig.FirepitIdleServerTickEnabled = false;
                try
                {
                    var serverHarness = new FirepitApiHarness(EnumAppSide.Server);
                    var beServer = (BlockEntity)Activator.CreateInstance(firepitType)!;
                    beServer.Pos = new BlockPos(12, 64, 12, 0);
                    beServer.Block = new Block { Code = new AssetLocation("game", "firepit-cold") };
                    beServer.Initialize(serverHarness.CreateServerApi());

                    Assert.Equal(0, serverHarness.ActiveRendererCount);
                    Assert.Equal(1, serverHarness.CountTickListeners(100));
                    Assert.Equal(1, serverHarness.CountTickListeners(500));
                    Assert.Equal(3, serverHarness.ActiveTickListenerCount);
                }
                finally
                {
                    OptimumConfig.FirepitIdleServerTickEnabled = origServerTick;
                }
            }
            finally
            {
                alc.Unload();
            }
        }
        finally
        {
            OptimumConfig.FirepitIdleRendererEnabled = orig;
            try { File.Delete(tempOut); } catch { }
        }
    }

    private sealed class FirepitApiHarness
    {
        private readonly EnumAppSide side;
        private readonly List<IRenderer> registeredRenderers = new();
        private readonly Dictionary<long, int> activeListeners = new();
        private long nextListenerId = 1;

        public FirepitApiHarness(EnumAppSide side)
        {
            this.side = side;
        }

        public int ActiveRendererCount => registeredRenderers.Count;
        public int ActiveTickListenerCount => activeListeners.Count;
        public int CountTickListeners(int intervalMs) => activeListeners.Values.Count(v => v == intervalMs);

        public ICoreClientAPI CreateClientApi()
        {
            var blockAccessor = CreateProxy<IBlockAccessor>((_, _) => null);
            var invUtil = CreateProxy<IInventoryNetworkUtil>((m, _) => DefaultValue(m.ReturnType));
            var classRegistry = CreateProxy<IClassRegistryAPI>((m, _) => m.Name switch
            {
                "CreateInvNetworkUtil" => invUtil,
                _ => DefaultValue(m.ReturnType),
            });
            var modLoader = CreateProxy<IModLoader>((m, _) => DefaultValue(m.ReturnType));
            var clientWorld = CreateProxy<IClientWorldAccessor>((m, _) => m.Name switch
            {
                "get_BlockAccessor" => blockAccessor,
                _ => DefaultValue(m.ReturnType),
            });

            var clientEvents = CreateProxy<IClientEventAPI>((m, args) => HandleEventCall(m, args));
            return CreateProxy<ICoreClientAPI>((m, _) => m.Name switch
            {
                "get_Side" => side,
                "get_World" => clientWorld,
                "get_Event" => clientEvents,
                "get_ClassRegistry" => classRegistry,
                "get_ModLoader" => modLoader,
                _ => DefaultValue(m.ReturnType),
            });
        }

        public ICoreServerAPI CreateServerApi()
        {
            var blockAccessor = CreateProxy<IBlockAccessor>((_, _) => null);
            var invUtil = CreateProxy<IInventoryNetworkUtil>((m, _) => DefaultValue(m.ReturnType));
            var classRegistry = CreateProxy<IClassRegistryAPI>((m, _) => m.Name switch
            {
                "CreateInvNetworkUtil" => invUtil,
                _ => DefaultValue(m.ReturnType),
            });
            var modLoader = CreateProxy<IModLoader>((m, _) => DefaultValue(m.ReturnType));
            var serverWorld = CreateProxy<IServerWorldAccessor>((m, _) => m.Name switch
            {
                "get_BlockAccessor" => blockAccessor,
                _ => DefaultValue(m.ReturnType),
            });

            var serverEvents = CreateProxy<IServerEventAPI>((m, args) => HandleEventCall(m, args));
            return CreateProxy<ICoreServerAPI>((m, _) => m.Name switch
            {
                "get_Side" => side,
                "get_World" => serverWorld,
                "get_Event" => serverEvents,
                "get_ClassRegistry" => classRegistry,
                "get_ModLoader" => modLoader,
                _ => DefaultValue(m.ReturnType),
            });
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

        private object? HandleEventCall(MethodInfo m, object?[]? args)
        {
            switch (m.Name)
            {
                case "RegisterRenderer" when args?.Length >= 1 && args[0] is IRenderer r:
                    registeredRenderers.Add(r);
                    return null;
                case "UnregisterRenderer" when args?.Length >= 1 && args[0] is IRenderer r:
                    registeredRenderers.Remove(r);
                    return null;
                case "RegisterGameTickListener":
                    int interval = 0;
                    if (args != null)
                    {
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (args[i] is int ms)
                            {
                                interval = ms;
                                break;
                            }
                        }
                    }
                    long id = nextListenerId++;
                    activeListeners[id] = interval;
                    return id;
                case "UnregisterGameTickListener" when args?.Length >= 1 && args[0] is long lid:
                    activeListeners.Remove(lid);
                    return null;
                default:
                    return DefaultValue(m.ReturnType);
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

    private static class DynamicPotTypeBuilder
    {
        public static Type GetOrCreate(Assembly modAsm, Type supplierIface)
        {
            Type childIface = modAsm.GetType("Vintagestory.GameContent.IInFirepitRenderer", throwOnError: true)!;
            Type enumModel = modAsm.GetType("Vintagestory.GameContent.EnumFirepitModel", throwOnError: true)!;
            Type firepitType = modAsm.GetType("Vintagestory.GameContent.BlockEntityFirepit", throwOnError: true)!;

            var asmName = new System.Reflection.AssemblyName("FirepitDynamicPot_" + Guid.NewGuid().ToString("N"));
            var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(asmName, System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
            var mb = ab.DefineDynamicModule("Main");

            var rendererTb = mb.DefineType(
                "StubInFirepitRenderer",
                System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed,
                typeof(object),
                new[] { childIface });
            rendererTb.DefineDefaultConstructor(System.Reflection.MethodAttributes.Public);
            foreach (MethodInfo m in childIface.GetInterfaces().Append(childIface).SelectMany(i => i.GetMethods()).Distinct())
            {
                var parms = m.GetParameters().Select(p => p.ParameterType).ToArray();
                var mbMethod = rendererTb.DefineMethod(
                    m.Name,
                    System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual,
                    m.ReturnType,
                    parms);
                var il = mbMethod.GetILGenerator();
                if (m.ReturnType == typeof(double)) il.Emit(System.Reflection.Emit.OpCodes.Ldc_R8, 0.5);
                else if (m.ReturnType == typeof(float)) il.Emit(System.Reflection.Emit.OpCodes.Ldc_R4, 0.5f);
                else if (m.ReturnType == typeof(int)) il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                rendererTb.DefineMethodOverride(mbMethod, m);
            }
            Type stubRendererType = rendererTb.CreateType()!;

            var blockTb = mb.DefineType(
                "StubPotBlock",
                System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed,
                typeof(Block),
                new[] { supplierIface });
            var getRenderer = blockTb.DefineMethod(
                "GetRendererWhenInFirepit",
                System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual,
                childIface,
                new[] { typeof(ItemStack), firepitType, typeof(bool) });
            var ilR = getRenderer.GetILGenerator();
            ilR.Emit(System.Reflection.Emit.OpCodes.Newobj, stubRendererType.GetConstructor(Type.EmptyTypes)!);
            ilR.Emit(System.Reflection.Emit.OpCodes.Ret);
            blockTb.DefineMethodOverride(getRenderer, supplierIface.GetMethod("GetRendererWhenInFirepit")!);

            var getModel = blockTb.DefineMethod(
                "GetDesiredFirepitModel",
                System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual,
                enumModel,
                new[] { typeof(ItemStack), firepitType, typeof(bool) });
            var ilM = getModel.GetILGenerator();
            ilM.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
            ilM.Emit(System.Reflection.Emit.OpCodes.Ret);
            blockTb.DefineMethodOverride(getModel, supplierIface.GetMethod("GetDesiredFirepitModel")!);

            return blockTb.CreateType()!;
        }
    }
}
