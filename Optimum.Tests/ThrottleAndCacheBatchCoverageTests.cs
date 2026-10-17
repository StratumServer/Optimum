using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class ThrottleAndCacheBatchCoverageTests
{
    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderPlayerEffects.cs.patch")]
    public void OnBeforeRenderReusesTheCachedLightScanWhileStationary(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

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

        string clientSettingsSource = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSettings.cs.patch");
        Assert.Contains("public static bool OptimumDynamicLightCache { get; set; } = true;", clientSettingsSource);

        string platformSource = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch");
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
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs.patch");
        Assert.DoesNotContain("vec3d.X != _lastListenerX", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs.patch")]
    public void AudioListenerUsesAMovementThresholdAndPeriodicRefresh(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

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
        string source = File.ReadAllText(FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationParticles.cs.patch"));
        Assert.Contains("optimumLastHeightmapCenterX", source);
        Assert.Contains("optimumLastHeightmapCenterZ", source);
    }

    [Fact]
    public void WindSpeedAndFogLightThrottlesAlreadyShipped()
    {
        // The implementation has one wind lookup. Verify that it remains
        // inside the four-frame throttle rather than running every frame.
        string source = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/WeatherSystemClient.cs.patch");
        Assert.Contains("doWindLookup", source);

        const string windLookup = "WeatherDataAtPlayer.GetWindSpeed(plrPosd.Y);";
        Assert.Equal(1, source.Split(windLookup, StringSplitOptions.None).Length - 1);

        Assert.Contains("private const int WindUpdateInterval = 4;", source);
        Assert.Contains("_windFrameCounter % WindUpdateInterval == 0", source);
    }

    [Fact]
    public void WeatherWindThrottlePreservesPerFrameSmoothing()
    {
        string source = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/WeatherSystemClient.cs.patch");
        Assert.Contains("windSpeedSmoothed.X += (_cachedWindX - windSpeedSmoothed.X) * dt;", source);
        Assert.Contains("surfaceWindSpeedSmoothed.X += (_cachedSurfaceWindX - surfaceWindSpeedSmoothed.X) * dt;", source);

        string runtimeSource = PatchReader.ReadPatch("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSystemClient.cs.patch");
        Assert.Contains("windSpeedSmoothed.X += (optimumTargetWindX - windSpeedSmoothed.X) * dt;", runtimeSource);
        Assert.Contains("surfaceWindSpeedSmoothed.X += (optimumTargetSurfaceWindX - surfaceWindSpeedSmoothed.X) * dt;", runtimeSource);
    }

    [Fact]
    public void WeatherSimulationSoundResetsDeadzoneOnStopAndSetsVolumeOnStart()
    {
        string source = PatchReader.ReadPatch("patches/VSEssentials/Systems/Weather/WeatherSimulationSound.cs.patch");
        Assert.Contains("lastSetWindVolumeLeafy = -99f;", source);
        Assert.Contains("lastSetWindVolumeLeafless = -99f;", source);
        Assert.Contains("windSoundLeafy?.SetVolume(initLeafyVol);", source);
        Assert.Contains("windSoundLeafy?.SetVolume(0f);", source);

        string runtimeSource = PatchReader.ReadPatch("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationSound.cs.patch");
        Assert.Contains("lastSetWindVolumeLeafy = -99f;", runtimeSource);
        Assert.Contains("lastSetWindVolumeLeafless = -99f;", runtimeSource);
        Assert.Contains("windSoundLeafy?.SetVolume(initLeafyVol);", runtimeSource);
        Assert.Contains("windSoundLeafy?.SetVolume(0f);", runtimeSource);
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
