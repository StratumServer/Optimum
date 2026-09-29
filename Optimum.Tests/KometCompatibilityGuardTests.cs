using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Text.Json;
using Komet.Interop;
using Vintagestory.API.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class KometCompatibilityGuardTests : IDisposable
{
    public KometCompatibilityGuardTests()
    {
        OptimumKometGuard.ResetForTests();
        KometInterop.AnswersFeature = true;
        KometInterop.OverrideState = null;
        ModInterop.ResetCacheForTests();
    }

    public void Dispose()
    {
        OptimumKometGuard.ResetForTests();
        ModInterop.ResetCacheForTests();
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
    public void DetectViaModLoader_SetsDetected_AndWarnsAboutUnresolvedRenderOverlap()
    {
        var logs = new List<string>();
        var loader = new FakeModLoader(hasKomet: true, kometVersion: "2.0.0");

        bool detected = OptimumKometGuard.Detect(loader, msg => logs.Add(msg));
        Assert.True(OptimumCompatibilityGuard.HasKometInteropProvider);
        OptimumCompatibilityGuard.LogKometAdvisoryIfUncoordinated(logs.Add);

        Assert.True(detected);
        Assert.True(OptimumConfig.KometDetected);
        Assert.True(OptimumKometGuard.IsDetected);
        Assert.Equal("ModLoader", OptimumKometGuard.DetectionSource);
        Assert.Equal("2.0.0", OptimumKometGuard.DetectedVersion);
        Assert.Single(logs);
        Assert.Contains("Adaptive-radius coordination does not resolve render-path overlap", logs[0]);
        OptimumCompatibilityGuard.LogKometAdvisoryIfUncoordinated(logs.Add);
        Assert.Single(logs);

        string status = OptimumKometGuard.GetStatusLine();
        Assert.Contains("Komet v2.0.0", status);
        Assert.Contains("adaptive-radius interop available", status);
        Assert.Contains("render-path overlap unresolved", status);
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
    public void EffectiveIndirectDraw_IsGuardedForKometUnlessOptedOut()
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

            // Provider availability does not negotiate render-path compatibility.
            OptimumConfig.KometDetected = true;
            Assert.False(OptimumConfig.EffectiveIndirectDraw);

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
    public void EffectiveSimdCulling_IsGuardedForKometUnlessOptedOut()
    {
        bool origEnabled = OptimumConfig.SimdCullingEnabled;

        try
        {
            OptimumConfig.SimdCullingEnabled = true;

            // Without Komet: SIMD active if CPU supports it
            OptimumConfig.KometDetected = false;
            OptimumConfig.KometGuardEnabled = true;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);

            // Provider availability does not negotiate render-path compatibility.
            OptimumConfig.KometDetected = true;
            Assert.False(OptimumConfig.EffectiveSimdCulling);

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
    public void NotifyPlayerOnJoin_ReportsProtocolDoesNotResolveRenderOverlap()
    {
        var messages = new List<string>();
        OptimumConfig.KometDetected = true;
        OptimumConfig.KometGuardEnabled = true;

        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Single(messages);
        Assert.Contains("Adaptive-radius coordination does not resolve Komet render-path overlap", messages[0]);

        // Repeated joins are deduplicated for the session.
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Single(messages);

        // ResetSession simulates leaving and rejoining world
        OptimumKometGuard.ResetSession();
        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void NotifyPlayerOnJoin_StillReportsOverlapWhenRenderGuardIsDisabled()
    {
        var messages = new List<string>();
        OptimumConfig.KometDetected = true;
        OptimumConfig.KometGuardEnabled = false;

        OptimumKometGuard.NotifyPlayerOnJoin(msg => messages.Add(msg));

        Assert.Single(messages);
        Assert.Contains("render-path overlap", messages[0]);
        Assert.Contains("render guard disabled", OptimumKometGuard.GetStatusLine());
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
