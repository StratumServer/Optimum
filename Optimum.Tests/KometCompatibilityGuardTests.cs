using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class KometCompatibilityGuardTests : IDisposable
{
    public KometCompatibilityGuardTests()
    {
        OptimumKometGuard.ResetForTests();
    }

    public void Dispose()
    {
        OptimumKometGuard.ResetForTests();
    }

    [Fact]
    public void DefaultState_HasNoKometDetected_AndGuardEnabled()
    {
        Assert.False(OptimumConfig.KometDetected);
        Assert.True(OptimumConfig.KometGuardEnabled);
        Assert.False(OptimumKometGuard.IsDetected);
        Assert.Equal("none detected", OptimumKometGuard.GetStatusLine());
        Assert.Contains("none detected", OptimumDiagnostics.GetConflictingModsSummary());
    }

    [Fact]
    public void DetectViaModLoader_SetsDetected_AndRecognizesProtocolProvider()
    {
        var logs = new List<string>();
        var loader = new FakeModLoader(hasKomet: true, kometVersion: "2.0.0");

        bool detected = OptimumKometGuard.Detect(loader, msg => logs.Add(msg));

        Assert.True(detected);
        Assert.True(OptimumConfig.KometDetected);
        Assert.True(OptimumKometGuard.IsDetected);
        Assert.Equal("ModLoader", OptimumKometGuard.DetectionSource);
        Assert.Equal("2.0.0", OptimumKometGuard.DetectedVersion);
        Assert.Empty(logs);
        OptimumCompatibilityGuard.LogKometAdvisoryIfUncoordinated(logs.Add);
        Assert.Empty(logs);

        string status = OptimumKometGuard.GetStatusLine();
        Assert.Contains("Komet v2.0.0", status);
        Assert.Contains("interop protocol available", status);
    }

    [Fact]
    public void DetectViaModLoader_IgnoresWhenKometNotPresent()
    {
        var logs = new List<string>();
        var loader = new FakeModLoader(hasKomet: false);

        bool detected = OptimumKometGuard.Detect(loader, msg => logs.Add(msg));

        Assert.False(detected);
        Assert.False(OptimumConfig.KometDetected);
        Assert.Empty(logs);
        Assert.Equal("none detected", OptimumKometGuard.GetStatusLine());
    }

    [Fact]
    public void EffectiveIndirectDraw_RemainsEnabledForProtocolAwareKomet()
    {
        bool origSupported = OptimumConfig.IndirectDrawSupported;
        bool origEnabled = OptimumConfig.IndirectDrawEnabled;

        try
        {
            OptimumConfig.IndirectDrawSupported = true;
            OptimumConfig.IndirectDrawEnabled = true;

            // Without Komet: MDI is active
            OptimumConfig.KometDetected = false;
            OptimumConfig.KometGuardEnabled = true;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);

            // Protocol-aware Komet nightly has no evidenced MDI replacement path.
            OptimumConfig.KometDetected = true;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);

            // User explicitly disables guard: force MDI despite Komet
            OptimumConfig.KometGuardEnabled = false;
            Assert.True(OptimumConfig.EffectiveIndirectDraw);
        }
        finally
        {
            OptimumConfig.IndirectDrawSupported = origSupported;
            OptimumConfig.IndirectDrawEnabled = origEnabled;
        }
    }

    [Fact]
    public void EffectiveSimdCulling_RemainsEnabledForProtocolAwareKomet()
    {
        bool origEnabled = OptimumConfig.SimdCullingEnabled;

        try
        {
            OptimumConfig.SimdCullingEnabled = true;

            // Without Komet: SIMD active if CPU supports it
            OptimumConfig.KometDetected = false;
            OptimumConfig.KometGuardEnabled = true;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);

            // Protocol-aware Komet nightly has no evidenced SIMD culling replacement path.
            OptimumConfig.KometDetected = true;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);

            // User explicitly disables guard: force SIMD despite Komet
            OptimumConfig.KometGuardEnabled = false;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
        }
        finally
        {
            OptimumConfig.SimdCullingEnabled = origEnabled;
        }
    }

    [Fact]
    public void ChunkRenderSummary_ReflectsKometOverrideState()
    {
        OptimumConfig.KometDetected = false;
        string cleanSummary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.DoesNotContain("kometOverride", cleanSummary);

        OptimumConfig.KometDetected = true;
        string overriddenSummary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.Contains("kometOverride=true", overriddenSummary);
    }

    [Fact]
    public void DescribeToggles_IncludesKometGuard()
    {
        var toggles = OptimumConfig.DescribeToggles();
        var entry = toggles.FirstOrDefault(t => t.Name == nameof(OptimumConfigData.KometGuard));

        Assert.NotNull(entry.Name);
        Assert.Equal("True", entry.Value);
    }

    [Fact]
    public void ConfigSerialization_RoundtripsKometGuard()
    {
        var data = new OptimumConfigData { KometGuard = false };
        string json = JsonSerializer.Serialize(data);
        var parsed = JsonSerializer.Deserialize<OptimumConfigData>(json);

        Assert.NotNull(parsed);
        Assert.False(parsed.KometGuard);
    }

    [Fact]
    public void NotifyPlayerOnJoin_StaysQuietForProtocolAwarePeer()
    {
        var messages = new List<string>();
        OptimumConfig.KometDetected = true;
        OptimumConfig.KometGuardEnabled = true;

        // Protocol-aware Komet has no unresolved compatibility advisory.
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Empty(messages);

        // Repeated joins stay quiet as well.
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Empty(messages);

        // ResetSession simulates leaving and rejoining world
        OptimumKometGuard.ResetSession();
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Empty(messages);
    }

    private sealed class FakeModLoader : IModLoader
    {
        private readonly bool _hasKomet;
        private readonly string _kometVersion;

        public FakeModLoader(bool hasKomet, string kometVersion = "1.0.0")
        {
            _hasKomet = hasKomet;
            _kometVersion = kometVersion;
        }

        public IEnumerable<Mod> Mods => _hasKomet
            ? new List<Mod> { new FakeMod("komet", "Komet", _kometVersion) }
            : new List<Mod>();

        public IEnumerable<ModSystem> Systems => Enumerable.Empty<ModSystem>();

        public Mod GetMod(string modID)
        {
            if (_hasKomet && string.Equals(modID, "komet", StringComparison.OrdinalIgnoreCase))
            {
                return new FakeMod("komet", "Komet", _kometVersion);
            }
            return null;
        }

        public bool IsModEnabled(string modID)
        {
            return _hasKomet && string.Equals(modID, "komet", StringComparison.OrdinalIgnoreCase);
        }

        public ModSystem GetModSystem(string fullName) => null;
        public T GetModSystem<T>(bool withInheritance = true) where T : ModSystem => null;
        public bool IsModSystemEnabled(string fullName) => false;
    }

    private sealed class FakeMod : Mod
    {
        public FakeMod(string modId, string name, string version)
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
