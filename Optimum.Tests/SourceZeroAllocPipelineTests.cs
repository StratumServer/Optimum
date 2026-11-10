using System;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Tests;

public sealed class SourceZeroAllocPipelineTests
{
    private readonly ITestOutputHelper output;

    public SourceZeroAllocPipelineTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void Phase1_ColorUtil_GetIncandescenceColorFloats_MatchesBaselineBitExact()
    {
        for (int temp = -100; temp <= 2500; temp++)
        {
            float[] expected = ColorUtil.GetIncandescenceColorAsColor4f(temp);

            ColorUtil.GetIncandescenceColorFloats(temp, out float r, out float g, out float b, out float a);
            Assert.Equal(expected[0], r);
            Assert.Equal(expected[1], g);
            Assert.Equal(expected[2], b);
            Assert.Equal(expected[3], a);

            ColorUtil.GetIncandescenceColorFloats(temp, out float r3, out float g3, out float b3);
            Assert.Equal(expected[0], r3);
            Assert.Equal(expected[1], g3);
            Assert.Equal(expected[2], b3);
        }
    }

    [Fact]
    public void Phase1_ColorUtil_ToRGBAFloats_MatchesBaselineBitExact()
    {
        int[] testColors = new int[]
        {
            0,
            -1,
            unchecked((int)0xFF000000),
            unchecked((int)0x00FF0000),
            unchecked((int)0x0000FF00),
            unchecked((int)0x000000FF),
            unchecked((int)0x80123456),
            unchecked((int)0xAA55AA55),
            12345678,
            -987654321
        };

        foreach (int color in testColors)
        {
            Vec4f expected = ColorUtil.ToRGBAVec4f(color);

            ColorUtil.ToRGBAFloats(color, out float r, out float g, out float b, out float a);
            Assert.Equal(expected.X, r);
            Assert.Equal(expected.Y, g);
            Assert.Equal(expected.Z, b);
            Assert.Equal(expected.W, a);
        }
    }

    [Fact]
    public void Phase1_AllocationProfiling_IncandescenceAndColorUnpack_ZeroAlloc()
    {
        const int iterations = 100_000;

        // Warmup JIT
        ColorUtil.GetIncandescenceColorFloats(800, out float wr, out float wg, out float wb);
        ColorUtil.ToRGBAFloats(0x123456, out float war, out float wag, out float wab, out float waa);

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        // --- Baseline measurement (allocates float[] and Vec4f) ---
        long baselineAllocStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            float[] c = ColorUtil.GetIncandescenceColorAsColor4f(500 + (i % 800));
            Vec4f v = ColorUtil.ToRGBAVec4f(i);
            if (c[0] < 0 || v.X < 0) throw new InvalidOperationException();
        }
        long baselineAllocEnd = GC.GetAllocatedBytesForCurrentThread();
        long baselineBytes = baselineAllocEnd - baselineAllocStart;

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        // --- Optimized measurement (source zero-alloc scalar methods) ---
        long optAllocStart = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0f;
        for (int i = 0; i < iterations; i++)
        {
            ColorUtil.GetIncandescenceColorFloats(500 + (i % 800), out float r, out float g, out float b);
            ColorUtil.ToRGBAFloats(i, out float ar, out float ag, out float ab, out float aa);
            sum += r + ar;
        }
        long optAllocEnd = GC.GetAllocatedBytesForCurrentThread();
        long optBytes = optAllocEnd - optAllocStart;

        output.WriteLine($"Incandescence + Color Unpack (100k calls): Baseline = {baselineBytes:N0} bytes, Optimized = {optBytes:N0} bytes (Sum={sum})");

        Assert.True(baselineBytes >= 4_000_000, $"Baseline should have allocated at least 4 MB but was {baselineBytes} bytes.");
        Assert.Equal(0, optBytes);
    }

    [Fact]
    public void Phase3_WeatherParticles_VelocityInPlaceMutation_ZeroAlloc()
    {
        const int iterations = 100_000;

        var velocity = new Vec3f();

        // Warmup
        velocity.Set(1f, 2f, 3f);

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        // --- Baseline measurement (new Vec3f on every call) ---
        long baselineAllocStart = GC.GetAllocatedBytesForCurrentThread();
        Vec3f lastVec = null;
        for (int i = 0; i < iterations; i++)
        {
            lastVec = new Vec3f(-1f, 3f, -1f);
        }
        long baselineAllocEnd = GC.GetAllocatedBytesForCurrentThread();
        long baselineBytes = baselineAllocEnd - baselineAllocStart;

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        // --- Optimized measurement (in-place .Set() mutation) ---
        long optAllocStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            velocity.Set(-1f, 3f, -1f);
        }
        long optAllocEnd = GC.GetAllocatedBytesForCurrentThread();
        long optBytes = optAllocEnd - optAllocStart;

        output.WriteLine($"Weather Particle Velocity Mutation (100k calls): Baseline = {baselineBytes:N0} bytes, Optimized = {optBytes:N0} bytes (lastVec={lastVec.X})");

        Assert.True(baselineBytes >= 2_400_000, $"Baseline should have allocated at least 2.4 MB but was {baselineBytes} bytes.");
        Assert.Equal(0, optBytes);
    }

    [Fact]
    public void Patches_VerifySourceZeroAllocImplementations()
    {
        string colorUtilPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Math/ColorUtil.cs.patch"));
        Assert.Contains("GetIncandescenceColorFloats", colorUtilPatch);
        Assert.Contains("ToRGBAFloats", colorUtilPatch);

        string weatherPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSEssentials/Systems/Weather/WeatherSimulationParticles.cs.patch"));
        Assert.Contains("splashParticles.MinVelocity.Set(-1f, 3f, -1f);", weatherPatch);
        Assert.Contains("splashParticles.AddVelocity.Set(2f, 0f, 2f);", weatherPatch);

        string lanternPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSSurvivalMod/BlockEntity/BELantern.cs.patch"));
        Assert.Contains("rotatedMesh", lanternPatch);

        string bucketPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSSurvivalMod/BlockEntity/BEBucket.cs.patch"));
        Assert.Contains("rotatedMesh", bucketPatch);

        string anvilPatch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VSSurvivalMod/BlockEntity/BEAnvil.cs.patch"));
        Assert.Contains("rotatedMesh", anvilPatch);
    }
}
