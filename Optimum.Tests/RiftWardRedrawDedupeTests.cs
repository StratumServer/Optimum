#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public sealed class RiftWardRedrawDedupeTests
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
    public void RiftWardPatchesAndManifest_GateActivationOnStateTransition()
    {
        string root = RepoRoot();
        string sourcePatch = File.ReadAllText(Path.Combine(
            root, "patches/VSSurvivalMod/BlockEntity/BERiftWard.cs.patch"));
        string runtimePatch = File.ReadAllText(Path.Combine(
            root, "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityRiftWard.cs.patch"));
        string modPatcher = File.ReadAllText(Path.Combine(
            root, "Optimum.Patcher/mod-patcher.cs"));

        Assert.Contains("bool prevOn = On;", sourcePatch);
        Assert.Contains("if (On != prevOn)", sourcePatch);
        Assert.Contains("bool prevOn = On;", runtimePatch);
        Assert.Contains("if (On != prevOn)", runtimePatch);
        Assert.Contains("new(\"Vintagestory.GameContent.BlockEntityRiftWard\", \"FromTreeAttributes\", 2)", modPatcher);
    }

    [Fact]
    public void SourceBuild_RiftWardRedrawDedupe_PreservesLifecycleAndSkipsSteadyRedraws()
    {
        string root = RepoRoot();
        string sourceDll = Path.Combine(root, "bin/Release/net10.0/VSSurvivalMod.dll");
        if (!File.Exists(sourceDll))
        {
            sourceDll = Path.Combine(AppContext.BaseDirectory, "VSSurvivalMod.dll");
        }
        Assert.True(File.Exists(sourceDll), $"Missing VSSurvivalMod.dll at {sourceDll}");

        RunRiftWardSuite(sourceDll, Path.GetDirectoryName(sourceDll)!);
    }

    [Fact]
    public void AssembledRuntimeDonor_RiftWardRedrawDedupe_PreservesLifecycleAndSkipsSteadyRedraws()
    {
        string root = RepoRoot();
        string vanillaPath = Path.Combine(root, ".vanilla/win-x64/runtime-donors/Mods/VSSurvivalMod.dll");
        string compiledPath = Path.Combine(root, ".build/runtime-donors/VSSurvivalMod/bin/Release/net10.0/VSSurvivalMod.dll");
        if (!File.Exists(vanillaPath) || !File.Exists(compiledPath))
        {
            return;
        }

        string tempOut = Path.Combine(Path.GetTempPath(), $"optimum_riftward_670_{Guid.NewGuid():N}.dll");
        try
        {
            Assert.True(Optimum.Patcher.ModPatcher.Patch("vssurvivalmod", vanillaPath, compiledPath, tempOut));
            Assert.True(File.Exists(tempOut));

            RunRiftWardSuite(tempOut, Path.Combine(root, "bin/Release/net10.0"));
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

    private static void RunRiftWardSuite(string targetSurvivalDll, string survivalDir)
    {
        var alc = new AssemblyLoadContext("RiftWardRedrawTest", isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            if (name.Name == "VintagestoryAPI") return typeof(ICoreAPI).Assembly;
            if (name.Name == "Optimum.Api.Contracts") return typeof(OptimumConfig).Assembly;
            if (name.Name == "VSEssentials") return typeof(BEBehaviorAnimatable).Assembly;
            string candidate = Path.Combine(survivalDir, name.Name + ".dll");
            if (File.Exists(candidate)) return alc.LoadFromAssemblyPath(candidate);
            string baseCandidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (File.Exists(baseCandidate)) return alc.LoadFromAssemblyPath(baseCandidate);
            return null;
        };

        try
        {
            Assembly asm = alc.LoadFromAssemblyPath(targetSurvivalDll);
            Type wardType = asm.GetType("Vintagestory.GameContent.BlockEntityRiftWard")!;
            Assert.NotNull(wardType);
            PropertyInfo onProp = wardType.GetProperty("On")!;
            FieldInfo fuelDaysField = wardType.GetField("fuelDays", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo lastUpdateField = wardType.GetField("lastUpdateTotalDays", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo riftsBlockedField = wardType.GetField("riftsBlocked", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // 1) Fueling (false -> true), 1 minute (12 x 5s) steady active syncs, and running out of fuel (true -> false).
            {
                var harness = new RiftWardHarness(wardType);
                var (ward, animBehavior) = harness.CreateWard(new BlockPos(10, 64, 10), initializeNow: true);
                Assert.False((bool)onProp.GetValue(ward)!);
                Assert.Equal(0, harness.MarkBlockDirtyCount);

                // Unchanged OFF sync -> 0 redraws.
                harness.SyncWard(ward, on: false, fuelDays: 0.0, lastUpdateTotalDays: 10.0, riftsBlocked: 0);
                Assert.Equal(0, harness.MarkBlockDirtyCount);
                Assert.Equal(0, harness.SoundStartCount);

                // Fueled and activated (false -> true) -> starts animation, sound, and redraws once.
                harness.CalendarTotalDays = 10.1;
                harness.SyncWard(ward, on: true, fuelDays: 21.0, lastUpdateTotalDays: 10.1, riftsBlocked: 0);
                Assert.True((bool)onProp.GetValue(ward)!);
                Assert.Equal(1, harness.MarkBlockDirtyCount);
                Assert.Equal(1, harness.SoundStartCount);
                Assert.Equal(1, harness.SoundFadeToCount);
                Assert.True(animBehavior.animUtil.activeAnimationsByAnimCode.ContainsKey("on-spin"));

                // 12 consecutive 5s server syncs (60s total) while ward stays ON.
                harness.MarkBlockDirtyCount = 0;
                harness.SoundFadeToCount = 0;
                for (int i = 1; i <= 12; i++)
                {
                    double serverDays = 10.1 + i * 0.005;
                    harness.CalendarTotalDays = serverDays + 0.5;
                    harness.SyncWard(ward, on: true, fuelDays: 21.0 - i * 0.005, lastUpdateTotalDays: serverDays, riftsBlocked: i);
                }

                // Zero chunk redraws, zero sound restarts, synced fields updated, lastUpdateTotalDays preserved from packet.
                Assert.Equal(0, harness.MarkBlockDirtyCount);
                Assert.Equal(0, harness.SoundFadeToCount);
                Assert.True(animBehavior.animUtil.activeAnimationsByAnimCode.ContainsKey("on-spin"));
                Assert.Equal(21.0 - 12 * 0.005, (double)fuelDaysField.GetValue(ward)!, precision: 5);
                Assert.Equal(10.1 + 12 * 0.005, (double)lastUpdateField.GetValue(ward)!, precision: 5);
                Assert.Equal(12, (int)riftsBlockedField.GetValue(ward)!);

                // Runs out of fuel (true -> false) -> stops animation, fades out sound, and redraws once.
                harness.SyncWard(ward, on: false, fuelDays: 0.0, lastUpdateTotalDays: 31.1, riftsBlocked: 12);
                Assert.False((bool)onProp.GetValue(ward)!);
                Assert.Equal(1, harness.MarkBlockDirtyCount);
                Assert.Equal(1, harness.SoundFadeOutCount);
                Assert.False(animBehavior.animUtil.activeAnimationsByAnimCode.ContainsKey("on-spin"));

                // Repeated OFF syncs after burnout -> 0 redraws.
                harness.SyncWard(ward, on: false, fuelDays: 0.0, lastUpdateTotalDays: 31.2, riftsBlocked: 12);
                Assert.Equal(1, harness.MarkBlockDirtyCount);
                Assert.Equal(1, harness.SoundFadeOutCount);
            }

            // 2) Chunk reload while ward is saved ON: FromTreeAttributes (Api == null) then Initialize(capi), then steady 5s syncs.
            {
                var harness = new RiftWardHarness(wardType);
                var (ward, animBehavior) = harness.CreateWard(new BlockPos(20, 64, 20), initializeNow: false);

                // Chunk deserialization on client runs FromTreeAttributes before Initialize (Api == null).
                harness.SyncWard(ward, on: true, fuelDays: 18.5, lastUpdateTotalDays: 15.0, riftsBlocked: 3);
                Assert.True((bool)onProp.GetValue(ward)!);
                Assert.Equal(0, harness.MarkBlockDirtyCount);
                Assert.Equal(0, harness.SoundStartCount);

                // Initialize on client activates the already-on ward once (animation + sound + initial redraw).
                harness.InitializeWard(ward);
                Assert.True((bool)onProp.GetValue(ward)!);
                Assert.Equal(1, harness.MarkBlockDirtyCount);
                Assert.Equal(1, harness.SoundStartCount);
                Assert.True(animBehavior.animUtil.activeAnimationsByAnimCode.ContainsKey("on-spin"));

                // Next 12 five-second syncs (1 minute) trigger 0 redraws while spin & sound stay running.
                harness.MarkBlockDirtyCount = 0;
                harness.SoundFadeToCount = 0;
                for (int i = 1; i <= 12; i++)
                {
                    harness.SyncWard(ward, on: true, fuelDays: 18.5 - i * 0.005, lastUpdateTotalDays: 15.0 + i * 0.005, riftsBlocked: 3);
                }
                Assert.Equal(0, harness.MarkBlockDirtyCount);
                Assert.Equal(0, harness.SoundFadeToCount);
                Assert.True(animBehavior.animUtil.activeAnimationsByAnimCode.ContainsKey("on-spin"));
            }
        }
        finally
        {
            alc.Unload();
        }
    }

    private sealed class RiftWardHarness
    {
        private readonly Type _wardType;
        private readonly ICoreClientAPI _capi;
        private readonly IClientWorldAccessor _world;
        private bool _soundPlaying;

        public double CalendarTotalDays { get; set; } = 10.0;
        public int MarkBlockDirtyCount { get; set; }
        public int SoundStartCount { get; set; }
        public int SoundFadeToCount { get; set; }
        public int SoundFadeOutCount { get; set; }

        public RiftWardHarness(Type wardType)
        {
            _wardType = wardType;
            var rand = new Random(42);

            var sound = CreateProxy<ILoadedSound>((m, args) =>
            {
                switch (m.Name)
                {
                    case "get_IsPlaying":
                        return _soundPlaying;
                    case "get_SoundLengthSeconds":
                        return 10f;
                    case "Start":
                        _soundPlaying = true;
                        SoundStartCount++;
                        return null;
                    case "FadeTo":
                        SoundFadeToCount++;
                        return null;
                    case "FadeOut":
                        SoundFadeOutCount++;
                        _soundPlaying = false;
                        if (args is { Length: >= 2 } && args[1] is Action<ILoadedSound> onFaded)
                        {
                            onFaded((ILoadedSound)CreateProxy<ILoadedSound>((_, _) => null));
                        }
                        return null;
                    case "Dispose":
                        _soundPlaying = false;
                        return null;
                    default:
                        return DefaultValue(m.ReturnType);
                }
            });

            var calendar = CreateProxy<IGameCalendar>((m, _) => m.Name switch
            {
                "get_TotalDays" => CalendarTotalDays,
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

            ICoreClientAPI capi = null!;
            IClientWorldAccessor world = CreateProxy<IClientWorldAccessor>((m, _) => m.Name switch
            {
                "get_Api" => capi,
                "get_Side" => EnumAppSide.Client,
                "get_BlockAccessor" => blockAccessor,
                "get_Calendar" => calendar,
                "get_Rand" => rand,
                "LoadSound" => sound,
                _ => DefaultValue(m.ReturnType)
            });

            capi = CreateProxy<ICoreClientAPI>((m, _) => m.Name switch
            {
                "get_Side" => EnumAppSide.Client,
                "get_World" => world,
                "get_Event" => eventApi,
                _ => DefaultValue(m.ReturnType)
            });

            _capi = capi;
            _world = world;
        }

        public (BlockEntity Ward, BEBehaviorAnimatable AnimBehavior) CreateWard(BlockPos pos, bool initializeNow)
        {
            var ward = (BlockEntity)Activator.CreateInstance(_wardType)!;
            ward.Pos = pos;
            ward.Block = new Block { Code = new AssetLocation("game", "riftward") };
            var animBehavior = new BEBehaviorAnimatable(ward);
            ward.Behaviors.Add(animBehavior);

            if (initializeNow)
            {
                InitializeWard(ward);
            }

            return (ward, animBehavior);
        }

        public void InitializeWard(BlockEntity ward)
        {
            ward.Initialize(_capi);
        }

        public void SyncWard(BlockEntity ward, bool on, double fuelDays, double lastUpdateTotalDays, int riftsBlocked)
        {
            var tree = new TreeAttribute();
            tree.SetInt("posx", ward.Pos.X);
            tree.SetInt("posy", ward.Pos.Y);
            tree.SetInt("posz", ward.Pos.Z);
            tree.SetBool("on", on);
            tree.SetDouble("fuelUntilTotalDays", fuelDays);
            tree.SetDouble("lastUpdateTotalDays", lastUpdateTotalDays);
            tree.SetInt("riftsBlocked", riftsBlocked);
            ward.FromTreeAttributes(tree, _world);
        }
    }

    private static T CreateProxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, WardProxy>();
        ((WardProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultValue(Type returnType)
    {
        if (returnType == typeof(void)) return null;
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    public class WardProxy : DispatchProxy
    {
        public System.Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null || Handler == null) return null;
            return Handler(targetMethod, args);
        }
    }
}
