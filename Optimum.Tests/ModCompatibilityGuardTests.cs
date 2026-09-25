using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class ModCompatibilityGuardTests : IDisposable
{
    public ModCompatibilityGuardTests()
    {
        OptimumCompatibilityGuard.ResetForTests();
    }

    public void Dispose()
    {
        OptimumCompatibilityGuard.ResetForTests();
    }

    [Fact]
    public void DefaultState_NoPerformanceModsDetected()
    {
        Assert.False(OptimumConfig.KometDetected);
        Assert.False(OptimumConfig.OptiTimeDetected);
        Assert.False(OptimumConfig.TungstenDetected);
        Assert.False(OptimumConfig.SynergyDetected);
        Assert.True(OptimumConfig.KometGuardEnabled);

        Assert.Equal("none detected", OptimumCompatibilityGuard.GetKometStatusLine());
        Assert.Equal("none detected", OptimumCompatibilityGuard.GetOptiTimeStatusLine());
        Assert.Equal("none detected", OptimumCompatibilityGuard.GetTungstenStatusLine());
        Assert.Equal("none detected", OptimumCompatibilityGuard.GetSynergyStatusLine());

        string summary = OptimumCompatibilityGuard.GetPerformanceModsSummary();
        Assert.Equal("Optimum performance mods: none detected", summary);
    }

    [Fact]
    public void DetectKomet_UsesProtocolProviderWithoutAdvisory()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("komet", "Komet", "2.0.0"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));
        OptimumCompatibilityGuard.LogKometAdvisoryIfUncoordinated(logs.Add);

        Assert.True(OptimumConfig.KometDetected);
        Assert.Equal("2.0.0", OptimumCompatibilityGuard.KometDetectedVersion);
        Assert.Empty(logs);

        string status = OptimumCompatibilityGuard.GetKometStatusLine();
        Assert.Contains("Komet v2.0.0", status);
        Assert.Contains("interop protocol available", status);
    }

    [Fact]
    public void DetectOptiTime_SetsDetectedAndLogsRedundancyAdvisory()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("optitime", "OptiTime", "1.4.2"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.OptiTimeDetected);
        Assert.Equal("1.4.2", OptimumCompatibilityGuard.OptiTimeDetectedVersion);
        Assert.Single(logs);
        Assert.Contains("OptiTime mod detected", logs[0]);
        Assert.Contains("redundant", logs[0]);

        string status = OptimumCompatibilityGuard.GetOptiTimeStatusLine();
        Assert.Contains("OptiTime v1.4.2", status);
        Assert.Contains("REDUNDANT", status);
    }

    [Fact]
    public void DetectTungsten_SetsDetectedAndMarksCompatible()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("tungsten", "Tungsten", "1.3.7"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.TungstenDetected);
        Assert.Equal("1.3.7", OptimumCompatibilityGuard.TungstenDetectedVersion);
        Assert.Empty(logs);

        string status = OptimumCompatibilityGuard.GetTungstenStatusLine();
        Assert.Contains("Tungsten v1.3.7", status);
        Assert.Contains("COMPATIBLE", status);
    }

    [Fact]
    public void DetectSynergy_SetsDetectedAndMarksCompatible()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(("synergy", "Synergy", "1.1.25"));

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));

        Assert.True(OptimumConfig.SynergyDetected);
        Assert.Equal("1.1.25", OptimumCompatibilityGuard.SynergyDetectedVersion);
        Assert.Empty(logs);

        string status = OptimumCompatibilityGuard.GetSynergyStatusLine();
        Assert.Contains("Synergy v1.1.25", status);
        Assert.Contains("COMPATIBLE", status);
    }

    [Fact]
    public void DetectAllFourMods_SimultaneouslyPopulatesAllStates()
    {
        var logs = new List<string>();
        var loader = new MultiModLoader(
            ("komet", "Komet", "2.0.0"),
            ("optitime", "OptiTime", "1.4.0"),
            ("tungsten", "Tungsten", "1.3.7"),
            ("synergy", "Synergy", "1.1.25")
        );

        OptimumCompatibilityGuard.DetectFromModLoader(loader, msg => logs.Add(msg));
        OptimumCompatibilityGuard.LogKometAdvisoryIfUncoordinated(logs.Add);

        Assert.True(OptimumConfig.KometDetected);
        Assert.True(OptimumConfig.OptiTimeDetected);
        Assert.True(OptimumConfig.TungstenDetected);
        Assert.True(OptimumConfig.SynergyDetected);

        // Komet speaks the interop protocol; only the independent OptiTime advisory remains.
        Assert.Single(logs);

        string report = OptimumCompatibilityGuard.GetPerformanceModsReport();
        Assert.Contains("Komet:    Komet v2.0.0 (interop protocol available)", report);
        Assert.Contains("OptiTime: OptiTime v1.4.0 (REDUNDANT - natively integrated in Optimum)", report);
        Assert.Contains("Tungsten: Tungsten v1.3.7 (COMPATIBLE - server-side optimizations active)", report);
        Assert.Contains("Synergy:  Synergy v1.1.25 (COMPATIBLE - client-server synchronization active)", report);

        string summary = OptimumCompatibilityGuard.GetPerformanceModsSummary();
        Assert.Contains("Komet v2.0.0", summary);
        Assert.Contains("OptiTime v1.4.0", summary);
        Assert.Contains("Tungsten v1.3.7", summary);
        Assert.Contains("Synergy v1.1.25", summary);
    }

    [Fact]
    public void KometGuard_DoesNotDisableUnverifiedFeaturesForProtocolAwarePeer()
    {
        bool origSupported = OptimumConfig.IndirectDrawSupported;
        bool origEnabled = OptimumConfig.IndirectDrawEnabled;
        bool origSimd = OptimumConfig.SimdCullingEnabled;

        try
        {
            OptimumConfig.IndirectDrawSupported = true;
            OptimumConfig.IndirectDrawEnabled = true;
            OptimumConfig.SimdCullingEnabled = true;

            // Without Komet: MDI and SIMD are active
            OptimumConfig.KometDetected = false;
            OptimumConfig.KometGuardEnabled = true;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);

            // Protocol-aware Komet nightly has no evidenced replacement path for these features.
            OptimumConfig.KometDetected = true;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);

            // With KometGuard disabled by user: forces active
            OptimumConfig.KometGuardEnabled = false;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
        }
        finally
        {
            OptimumConfig.IndirectDrawSupported = origSupported;
            OptimumConfig.IndirectDrawEnabled = origEnabled;
            OptimumConfig.SimdCullingEnabled = origSimd;
        }
    }

    private sealed class MultiModLoader : IModLoader
    {
        private readonly List<Mod> _mods = new();

        public MultiModLoader(params (string id, string name, string version)[] entries)
        {
            foreach (var (id, name, version) in entries)
            {
                var mod = new TestMod(id, name, version);
                _mods.Add(mod);
            }
        }

        public IEnumerable<Mod> Mods => _mods;
        public IEnumerable<ModSystem> Systems => Enumerable.Empty<ModSystem>();

        public Mod GetMod(string modID)
        {
            return _mods.FirstOrDefault(m => string.Equals(m.Info?.ModID, modID, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsModEnabled(string modID)
        {
            return _mods.Any(m => string.Equals(m.Info?.ModID, modID, StringComparison.OrdinalIgnoreCase));
        }

        public ModSystem GetModSystem(string fullName) => null;
        public T GetModSystem<T>(bool withInheritance = true) where T : ModSystem => null;
        public bool IsModSystemEnabled(string fullName) => false;
    }

    private sealed class TestMod : Mod
    {
        public TestMod(string modId, string name, string version)
        {
            var info = new ModInfo
            {
                ModID = modId,
                Name = name,
                Version = version,
            };
            typeof(Mod).GetProperty(nameof(Info), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(this, info);
        }
    }
}
