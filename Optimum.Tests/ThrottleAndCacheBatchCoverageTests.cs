using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class ThrottleAndCacheBatchCoverageTests
{
    [Theory]
    [InlineData("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderPlayerEffects.cs")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderPlayerEffects.cs.patch")]
    public void OnBeforeRenderReusesTheCachedLightScanWhileStationary(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("ClientSettings.OptimumDynamicLightCache", source);
        Assert.Contains("OptimumDiagnostics.DynamicLightCache.Hit()", source);
        Assert.Contains("OptimumDiagnostics.DynamicLightCache.Skip()", source);
        Assert.Contains("LightScanRefreshFrames = 15", source);
    }

    [Fact]
    public void DynamicLightCacheTogglePlumbingIsComplete()
    {
        string configSource = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Config/OptimumConfig.cs"));
        Assert.Contains("public static bool DynamicLightCacheEnabled = true;", configSource);
        Assert.Contains("public bool DynamicLightCache { get; set; } = true;", configSource);
        Assert.Contains("public static readonly HitSkipCounter DynamicLightCache = new();", configSource);

        string clientSettingsSource = File.ReadAllText(FindRepositoryFile("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientSettings.cs"));
        Assert.Contains("public static bool OptimumDynamicLightCache { get; set; } = true;", clientSettingsSource);

        string platformSource = File.ReadAllText(FindRepositoryFile("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs"));
        Assert.Contains("ClientSettings.OptimumDynamicLightCache = Vintagestory.API.Config.OptimumConfig.DynamicLightCacheEnabled;", platformSource);
    }

    [Fact]
    public void SystemRenderPlayerEffectsOnBeforeRenderIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderPlayerEffects\", \"onBeforeRender\", 1", programSource);
    }

    [Fact]
    public void AudioListenerNoLongerUsesExactEqualityInSource()
    {
        string source = File.ReadAllText(FindRepositoryFile("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs"));
        Assert.DoesNotContain("vec3d.X != _lastListenerX", source);
    }

    [Theory]
    [InlineData("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs.patch")]
    public void AudioListenerUsesAMovementThresholdAndPeriodicRefresh(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("ListenerMoveThresholdSq = 0.0025", source);
        Assert.Contains("ListenerDirThresholdSq = 0.000001f", source);
        Assert.Contains("ListenerRefreshFrames = 10", source);
        Assert.Contains("_listenerFramesSinceUpdate >= ListenerRefreshFrames", source);
    }

    [Fact]
    public void SystemSoundEngineOnRenderFrameIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemSoundEngine\", \"OnRenderFrame\", 2", programSource);
    }

    [Fact]
    public void RainHeightmapSkipAlreadyShipped()
    {
        // docs/todo.md's "guard the 256-lookup rebuild on player move" item was
        // already shipped before Batch 6.2 started; assert it stays that way.
        string source = File.ReadAllText(FindRepositoryFile("VSEssentials/Systems/Weather/WeatherSimulationParticles.cs"));
        Assert.Contains("_lastHeightmapCenterX", source);
        Assert.Contains("_lastHeightmapCenterZ", source);
    }

    [Fact]
    public void WindSpeedAndFogLightThrottlesAlreadyShipped()
    {
        // Both docs/todo.md items ("wind speed calls" and "weather fog light")
        // were already shipped before Batch 6.2 started, including the second
        // GetWindSpeedAt call site the plan worried the shipped cache might
        // miss. Assert it stays that way.
        string source = File.ReadAllText(FindRepositoryFile("VSEssentials/Systems/Weather/WeatherSystemClient.cs"));
        Assert.Contains("doWindLookup", source);

        int firstWindCall = source.IndexOf("GetWindSpeedAt(plrPosd);", StringComparison.Ordinal);
        int secondWindCall = source.IndexOf("GetWindSpeedAt(plrPosd);", firstWindCall + 1, StringComparison.Ordinal);
        Assert.True(firstWindCall > 0 && secondWindCall > 0, "Expected two GetWindSpeedAt call sites.");

        Assert.Contains("_windFrameCounter % 4 == 0", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
