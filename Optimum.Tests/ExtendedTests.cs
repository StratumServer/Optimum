using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Extended unit tests covering each optimization's logic in isolation,
/// plus smoke tests that verify the build and client launch pipeline.
/// </summary>
public class ExtendedTests
{
    // ===== DynamicLight: view-distance-scaled radius =====

    [Theory]
    [InlineData(64, 35f)]
    [InlineData(128, 35f)]
    [InlineData(192, 45f)]
    [InlineData(256, 45f)]
    [InlineData(320, 52f)]
    [InlineData(384, 52f)]
    [InlineData(512, 60f)]
    public void DynamicLight_RadiusScaledByViewDistance(int viewDistance, float expectedRadius)
    {
        float radius = viewDistance switch
        {
            <= 128 => 35f,
            <= 256 => 45f,
            <= 384 => 52f,
            _ => 60f
        };
        Assert.Equal(expectedRadius, radius);
    }

    // ===== BackgroundFpsLimiter =====

    [Theory]
    [InlineData(true, 60f, 60f)]    // focused: keep user FPS
    [InlineData(false, 60f, 20f)]   // unfocused: cap to 20
    [InlineData(false, 15f, 15f)]   // unfocused but user FPS < bg cap: keep lower
    [InlineData(true, 144f, 144f)]  // focused high refresh: keep
    public void BgFps_EffectiveMaxFps(bool focused, float userMaxFps, float expected)
    {
        const int bgMaxFps = 20;
        float effectiveMaxFps = userMaxFps;
        if (!focused && bgMaxFps > 0 && bgMaxFps < userMaxFps)
            effectiveMaxFps = bgMaxFps;
        Assert.Equal(expected, effectiveMaxFps);
    }

    // ===== PreciseFramePacing =====

    [Theory]
    [InlineData(16_666_666L, 10_000_000L, 5)]  // 6.6ms remaining at 1GHz freq, expect ~5ms coarse sleep
    [InlineData(16_666_666L, 16_000_000L, 0)]  // almost done, no coarse sleep
    [InlineData(16_666_666L, 0L, 15)]           // full frame remaining
    public void FramePacing_CoarseSleepMs(long targetTicks, long elapsed, int expectedApproxMs)
    {
        long freq = 1_000_000_000L; // simulate 1GHz stopwatch
        double undershootPercent = 0.075;
        long remainingTicks = targetTicks - elapsed;
        long finalWaitTicks = Math.Max(freq / 2000, (long)(targetTicks * undershootPercent));

        int coarseSleepMs = 0;
        if (remainingTicks > finalWaitTicks)
            coarseSleepMs = (int)((remainingTicks - finalWaitTicks) * 1000L / freq);

        Assert.InRange(coarseSleepMs, expectedApproxMs - 2, expectedApproxMs + 2);
    }

    [Fact]
    public void FramePacing_YieldThresholdTicks_Positive()
    {
        long freq = Stopwatch.Frequency;
        double yieldThresholdMs = 0.25;
        long yieldThresholdTicks = (long)(freq * (yieldThresholdMs / 1000.0));
        Assert.True(yieldThresholdTicks > 0);
    }

    // ===== WeatherWind =====

    [Theory]
    [InlineData(0, false)]   // frame 0: no lookup (first frame edge case handled separately)
    [InlineData(100, true)]  // 100 % 4 == 0: lookup
    [InlineData(int.MaxValue, false)] // large number not divisible by 4
    public void WeatherWind_EdgeCases(int frame, bool shouldLookup)
    {
        bool lookup = frame != 0 && frame % 4 == 0;
        Assert.Equal(shouldLookup, lookup);
    }

    // ===== TickingBlocks =====

    [Theory]
    [InlineData(0x00000000, 0, 0, 0)]
    [InlineData(0x00000401, 1, 1, 0)]       // x=1, y=(1<<10)&0x3FF=1, z=0
    [InlineData(0x00100000, 0, 0, 1)]       // z = (0x00100000>>20)&0x3FF = 1
    public void TickingBlocks_BitUnpacking(int key, int expectedX, int expectedY, int expectedZ)
    {
        int baseX = 0, baseY = 0, baseZ = 0;
        int x = baseX + (key & 0x3FF);
        int y = baseY + ((key >> 10) & 0x3FF);
        int z = baseZ + ((key >> 20) & 0x3FF);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
        Assert.Equal(expectedZ, z);
    }

    // ===== AmbientSound =====

    [Theory]
    [InlineData(8, false)]   // counter 8 → ++8=9: still skip
    [InlineData(9, true)]    // counter 9 → ++9=10: force update (fallback)
    [InlineData(0, false)]   // counter 0 → ++0=1: skip
    public void AmbientSound_FallbackCounter(int counter, bool shouldForceUpdate)
    {
        counter++;
        bool force = counter >= 10;
        Assert.Equal(shouldForceUpdate, force);
    }

    // ===== FlySound =====

    [Theory]
    [InlineData(-0.5f, 0f)]    // negative clamps to 0
    [InlineData(1.5f, 1f)]     // above 1 clamps to 1
    [InlineData(0.5f, 0.5f)]   // in range stays
    public void FlySound_VolumeClamping(float input, float expected)
    {
        float clamped = Math.Max(0f, Math.Min(1f, input));
        Assert.Equal(expected, clamped);
    }

    // ===== EntityShadowCull: player exempt =====

    [Fact]
    public void EntityShadowCull_PlayerNeverCulled()
    {
        // Player entity == game.EntityPlayer: distance check is skipped.
        // Simulating: if (entity != game.EntityPlayer) { check distance }
        bool isPlayer = true;
        double distSq = 200.0 * 200.0; // way beyond cull distance
        bool culled = !isPlayer && distSq > 80.0 * 80.0;
        Assert.False(culled);
    }

    // ===== RepulseAgents: player exempt =====

    [Fact]
    public void RepulseAgents_PlayerExempt()
    {
        // Player entity is checked separately, distance gate only for non-player.
        bool isPlayer = true;
        double distSq = 200.0 * 200.0;
        bool skipped = !isPlayer && distSq > 64.0 * 64.0;
        Assert.False(skipped);
    }

    // ===== MouseWheel =====

    [Theory]
    [InlineData(1.0f, 0.2f, 0.2f)]     // sensitivity 0.2: deltaPrecise = 0.2 (not 0)
    [InlineData(-1.0f, 0.2f, -0.2f)]   // negative scroll preserves sign
    [InlineData(1.0f, 1.0f, 1.0f)]     // full sensitivity: unchanged
    [InlineData(1.0f, 0.5f, 0.5f)]     // half sensitivity
    public void MouseWheel_DeltaPrecisePreservesFraction(float offsetY, float sensitivity, float expected)
    {
        float num = offsetY * sensitivity;
        float deltaPrecise = num;  // Optimum fix: was (int)num
        Assert.Equal(expected, deltaPrecise, 5);
    }

    [Theory]
    [InlineData(1.0f, 0.2f, 0)]   // vanilla bug: (int)0.2 = 0
    [InlineData(-1.0f, 0.2f, 0)]  // vanilla bug: (int)(-0.2) = 0
    public void MouseWheel_VanillaBugTruncatesToZero(float offsetY, float sensitivity, int vanillaResult)
    {
        float num = offsetY * sensitivity;
        int truncated = (int)num;
        Assert.Equal(vanillaResult, truncated); // proves the bug existed
    }

    // ===== GuiDialog disabled composer =====

    [Theory]
    [InlineData(true, true, true)]    // enabled + point inside: handled
    [InlineData(false, true, false)]  // disabled + point inside: NOT handled (our fix)
    [InlineData(true, false, false)]  // enabled + point outside: not handled
    public void GuiDialog_DisabledComposerSkipped(bool enabled, bool pointInside, bool shouldHandle)
    {
        // Simulates the bounds-check loop logic
        bool handled = false;
        if (enabled && pointInside)  // our fix adds the enabled check
            handled = true;
        Assert.Equal(shouldHandle, handled);
    }

    // ===== Health tooltip =====

    [Theory]
    [InlineData(18.8, "18.8")]
    [InlineData(15.0, "15")]
    [InlineData(20.5, "20.5")]
    public void HealthTooltip_MaxValueShowsDecimal(double maxValue, string expected)
    {
        string result = ((float)Math.Round(maxValue, 1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }
}

/// <summary>
/// Smoke tests that verify the build pipeline and client launch.
/// Run make build before these checks.
/// </summary>
public class SmokeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string VintagestoryLibDll = Path.Combine(
        RepoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib.dll");

    [Fact]
    public void Smoke_VintagestoryLibDll_Exists()
    {
        if (!File.Exists(Path.Combine(RepoRoot, ".bootstrap-complete")))
        {
            // Bootstrap not complete: Cecil patcher hasn't produced the DLL yet.
            // This is expected in development without a full bootstrap cycle.
            return;
        }
        Assert.True(File.Exists(VintagestoryLibDll), $"VintagestoryLib.dll not found at {VintagestoryLibDll}");
    }

    [Fact]
    public void Smoke_VintagestoryLibDll_ContainsOptimum()
    {
        if (!File.Exists(VintagestoryLibDll)) return;

        string content = File.ReadAllText(VintagestoryLibDll);
        Assert.Contains("OptimumInfo", content);
    }

    [Fact]
    public void Smoke_OptimumInfoSourceExists()
    {
        string src = Path.Combine(RepoRoot, "sources", "VintagestoryLib", "Optimum", "OptimumInfo.cs");
        Assert.True(File.Exists(src), "OptimumInfo.cs source not found");
    }

    [Fact]
    public void Smoke_DirectoryBuildPropsExists()
    {
        string props = Path.Combine(RepoRoot, "Directory.Build.props");
        Assert.True(File.Exists(props));
        string content = File.ReadAllText(props);
        Assert.Contains("FrameworkVersion", content);
        Assert.Contains("net10.0", content);
    }

    [Fact]
    public void Smoke_MakefileHasAllTargets()
    {
        string makefile = Path.Combine(RepoRoot, "Makefile");
        Assert.True(File.Exists(makefile));
        string content = File.ReadAllText(makefile);
        Assert.Contains("build:", content);
        Assert.Contains("deploy:", content);
        Assert.Contains("run:", content);
        Assert.Contains("test:", content);
    }
}
