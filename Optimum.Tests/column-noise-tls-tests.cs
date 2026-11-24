using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Optimum.Patcher;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public sealed class ColumnNoiseTlsTests
{
    private delegate (double UncurvedBound, double BoundMin, double BoundMax, int UsedOctaves, double[] NoiseSigns, double[] NoiseValues) ColumnEvaluator(
        object fractalNoise,
        double relativeYFrequency,
        double[] amplitudes,
        double[] thresholds,
        double noiseX,
        double noiseZ,
        double[] yValues,
        double[] inverseThresholds);

    private delegate (double UncurvedBound, double BoundMin, double BoundMax) BoundExtractor(
        object fractalNoise,
        double relativeYFrequency,
        double[] amplitudes,
        double[] thresholds,
        double noiseX,
        double noiseZ);

    private delegate (long AllocatedBytes, double Checksum) AllocationProbe(
        object fractalNoise,
        double[] amplitudes,
        double[] thresholds,
        int iterations);

    private sealed class IsolatedApiLoadContext(params string[] dirs) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            string dll = name.Name + ".dll";
            foreach (string dir in dirs)
            {
                string candidate = Path.Combine(dir, dll);
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(candidate);
                }
            }
            return null;
        }
    }

    private sealed class RuntimeApiContext : IDisposable
    {
        public readonly string TempDir;
        public readonly AssemblyLoadContext VanillaAlc;
        public readonly AssemblyLoadContext PatchedAlc;
        public readonly Assembly VanillaAsm;
        public readonly Assembly PatchedAsm;
        public readonly Type VanillaFractalType;
        public readonly Type PatchedFractalType;
        public readonly Type PatchedColumnNoiseType;

        public RuntimeApiContext()
        {
            string vanillaApiPath = FindFirstExisting(
                ".vanilla/linux-x64/vintagestory/VintagestoryAPI.vanilla.dll",
                ".vanilla/win-x64/vintagestory/VintagestoryAPI.vanilla.dll",
                ".vanilla/linux-x64/vintagestory/VintagestoryAPI.dll",
                ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll")
                ?? throw new FileNotFoundException("Vanilla VintagestoryAPI.dll was not found.");

            string contractsPath = FindFirstExisting(
                "bin/Release/net10.0/Optimum.Api.Contracts.dll",
                "bin/Debug/net10.0/Optimum.Api.Contracts.dll")
                ?? typeof(OptimumApiBridge).Assembly.Location;

            TempDir = Path.Combine(Path.GetTempPath(), $"optimum-column-noise-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);
            string patchedApiPath = Path.Combine(TempDir, "VintagestoryAPI.dll");

            Assert.True(ApiPatcher.Patch(vanillaApiPath, contractsPath, patchedApiPath));

            string vanillaDir = Path.GetDirectoryName(vanillaApiPath)!;
            string vanillaLibDir = Path.Combine(vanillaDir, "Lib");
            string contractsDir = Path.GetDirectoryName(contractsPath)!;

            VanillaAlc = new IsolatedApiLoadContext(vanillaDir, vanillaLibDir);
            VanillaAsm = VanillaAlc.LoadFromAssemblyPath(vanillaApiPath);

            PatchedAlc = new IsolatedApiLoadContext(TempDir, contractsDir, vanillaLibDir);
            PatchedAsm = PatchedAlc.LoadFromAssemblyPath(patchedApiPath);

            VanillaFractalType = VanillaAsm.GetType("Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise", throwOnError: true)!;
            PatchedFractalType = PatchedAsm.GetType("Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise", throwOnError: true)!;
            PatchedColumnNoiseType = PatchedFractalType.GetNestedType("ColumnNoise", BindingFlags.Public)!;
        }

        public object CreateFractal(Type fractalType, int octaves, double baseFreq, double persistence, long seed)
        {
            var fromDefault = fractalType.GetMethod("FromDefaultOctaves", BindingFlags.Public | BindingFlags.Static)!;
            return fromDefault.Invoke(null, [octaves, baseFreq, persistence, seed])!;
        }

        public void Dispose()
        {
            VanillaAlc.Unload();
            PatchedAlc.Unload();
            if (Directory.Exists(TempDir))
            {
                try { Directory.Delete(TempDir, recursive: true); } catch { }
            }
        }

        private static string? FindFirstExisting(params string[] relativePaths)
        {
            foreach (string rel in relativePaths)
            {
                try
                {
                    string full = PatchReader.FindRepositoryFile(rel);
                    if (File.Exists(full)) return full;
                }
                catch (FileNotFoundException)
                {
                }
            }
            return null;
        }
    }

    [Fact]
    public void ColumnNoise_UsesThreadLocalStorageAndCalculatesDeterministicNoise()
    {
        var noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(4, 0.5, 2.0, 12345);
        double[] amps = [1.0, 0.5, 0.25, 0.125];
        double[] th = [0.0, 0.0, 0.0, 0.0];

        var col1 = noise.ForColumn(1.0, amps, th, 100.5, 200.5);
        Assert.True(col1.UsedOctaves > 0);
        Assert.True(col1.BoundMax > col1.BoundMin);

        double val1 = col1.Noise(64.0);
        double sign1 = col1.NoiseSign(64.0, 0.0);

        var col2 = noise.ForColumn(1.0, amps, th, 100.5, 200.5);
        Assert.Equal(col1.UsedOctaves, col2.UsedOctaves);
        Assert.Equal(col1.BoundMin, col2.BoundMin);
        Assert.Equal(col1.BoundMax, col2.BoundMax);

        double val2 = col2.Noise(64.0);
        double sign2 = col2.NoiseSign(64.0, 0.0);

        Assert.Equal(val1, val2);
        Assert.Equal(sign1, sign2);
    }

    [Fact]
    public void ColumnNoise_SameThreadConcurrentForColumn_ThrowsInvalidOperationException()
    {
        var noiseA = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(4, 0.05, 0.85, 111);
        var noiseB = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(4, 0.05, 0.85, 222);
        double[] amps = [1.0, 0.5, 0.25, 0.125];
        double[] th = [0.0, 0.0, 0.0, 0.0];

        var colA = noiseA.ForColumn(0.5, amps, th, 10.0, 20.0);
        var colB = noiseB.ForColumn(0.5, amps, th, 30.0, 40.0);

        Assert.Throws<InvalidOperationException>(() => colA.NoiseSign(64.0, 0.0));
        Assert.Throws<InvalidOperationException>(() => colA.Noise(64.0));

        _ = colB.NoiseSign(64.0, 0.0);
        _ = colB.Noise(64.0);
    }

    [Fact]
    public void ColumnNoise_CrossThreadUsage_ThrowsInvalidOperationExceptionEvenWithMatchingGeneration()
    {
        var noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(4, 0.05, 0.85, 111);
        double[] amps = [1.0, 0.5, 0.25, 0.125];
        double[] th = [0.0, 0.0, 0.0, 0.0];

        NewNormalizedSimplexFractalNoise.ColumnNoise foreignCol = default;
        RunOnDedicatedThread(() =>
        {
            foreignCol = noise.ForColumn(0.5, amps, th, 10.0, 20.0);
        });

        RunOnDedicatedThread(() =>
        {
            // Initialize threadBuffer on this second thread so generation counters may align,
            // verifying ReferenceEquals(buffer, threadBuffer) still rejects cross-thread structs.
            var localCol = noise.ForColumn(0.5, amps, th, 10.0, 20.0);
            _ = localCol.Noise(64.0);

            Assert.Throws<InvalidOperationException>(() => foreignCol.NoiseSign(64.0, 0.0));
            Assert.Throws<InvalidOperationException>(() => foreignCol.Noise(64.0));
        });
    }

    [Fact]
    public void ColumnNoise_DefaultAndExceptionSafety_PreserveActiveInstance()
    {
        NewNormalizedSimplexFractalNoise.ColumnNoise uninitialized = default;
        Assert.Throws<NullReferenceException>(() => uninitialized.NoiseSign(64.0, 0.0));
        Assert.Throws<NullReferenceException>(() => uninitialized.Noise(64.0));

        var noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(4, 0.05, 0.85, 555);
        double[] amps = [1.0, 0.5, 0.25, 0.125];
        double[] th = [0.0, 0.0, 0.0, 0.0];
        double[] shortAmps = [1.0];

        var active = noise.ForColumn(0.5, amps, th, 10.0, 20.0);
        double expected = active.Noise(64.0);

        Assert.Throws<IndexOutOfRangeException>(() => noise.ForColumn(0.5, shortAmps, th, 10.0, 20.0));
        Assert.Equal(expected, active.Noise(64.0));
        _ = active.NoiseSign(64.0, 0.0);
    }

    [Fact]
    public void PatchedApi_HasBitExactParityWithVanillaAcrossSeedsZeroAndLargeOctaveCounts()
    {
        using var ctx = new RuntimeApiContext();
        var vanillaEval = CompileColumnEvaluator(ctx.VanillaFractalType, hasUsedOctavesProperty: false);
        var patchedEval = CompileColumnEvaluator(ctx.PatchedFractalType, hasUsedOctavesProperty: true);

        double[] yValues = new double[64];
        double[] invThresholds = new double[64];
        for (int i = 0; i < yValues.Length; i++)
        {
            yValues[i] = i * 4.0;
            invThresholds[i] = (i % 5 - 2) * 0.65;
        }

        long[] seeds = [12345L, 42424242L, -987654321L];
        (double X, double Z)[] coords = [(0.0, 0.0), (100.5, 200.5), (-512.25, 1024.75)];

        foreach (long seed in seeds)
        {
            object vanilla9 = ctx.CreateFractal(ctx.VanillaFractalType, 9, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency, 0.9, seed);
            object patched9 = ctx.CreateFractal(ctx.PatchedFractalType, 9, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency, 0.9, seed);
            var source9 = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(9, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency, 0.9, seed);

            double[] amps9 = [1.0, 0.8, 0.6, 0.4, 0.3, 0.2, 0.15, 0.1, 0.05];
            double[] th9 = [0.1, 0.05, 0.0, 0.05, 0.0, 0.0, 0.02, 0.0, 0.0];

            foreach (var (x, z) in coords)
            {
                var vRes = vanillaEval(vanilla9, 0.5, amps9, th9, x, z, yValues, invThresholds);
                var pRes = patchedEval(patched9, 0.5, amps9, th9, x, z, yValues, invThresholds);

                AssertDoubleBitsEqual(vRes.UncurvedBound, pRes.UncurvedBound);
                AssertDoubleBitsEqual(vRes.BoundMin, pRes.BoundMin);
                AssertDoubleBitsEqual(vRes.BoundMax, pRes.BoundMax);
                Assert.Equal(9, pRes.UsedOctaves);

                var sCol = source9.ForColumn(0.5, amps9, th9, x, z);
                AssertDoubleBitsEqual(vRes.UncurvedBound, sCol.UncurvedBound);
                AssertDoubleBitsEqual(vRes.BoundMin, sCol.BoundMin);
                AssertDoubleBitsEqual(vRes.BoundMax, sCol.BoundMax);
                Assert.Equal(9, sCol.UsedOctaves);

                for (int i = 0; i < yValues.Length; i++)
                {
                    AssertDoubleBitsEqual(vRes.NoiseSigns[i], pRes.NoiseSigns[i]);
                    AssertDoubleBitsEqual(vRes.NoiseValues[i], pRes.NoiseValues[i]);
                    AssertDoubleBitsEqual(vRes.NoiseSigns[i], sCol.NoiseSign(yValues[i], invThresholds[i]));
                    AssertDoubleBitsEqual(vRes.NoiseValues[i], sCol.Noise(yValues[i]));
                }
            }

            // 0 used octaves (thresholds >= amplitudes)
            double[] zeroAmps = [0.2, 0.1, 0.0, 0.5];
            double[] zeroTh = [0.2, 0.2, 0.0, 0.6];
            object vanilla4 = ctx.CreateFractal(ctx.VanillaFractalType, 4, 0.01, 0.8, seed);
            object patched4 = ctx.CreateFractal(ctx.PatchedFractalType, 4, 0.01, 0.8, seed);
            var vZero = vanillaEval(vanilla4, 0.5, zeroAmps, zeroTh, 12.0, -34.0, yValues, invThresholds);
            var pZero = patchedEval(patched4, 0.5, zeroAmps, zeroTh, 12.0, -34.0, yValues, invThresholds);
            AssertDoubleBitsEqual(vZero.UncurvedBound, pZero.UncurvedBound);
            AssertDoubleBitsEqual(vZero.BoundMin, pZero.BoundMin);
            AssertDoubleBitsEqual(vZero.BoundMax, pZero.BoundMax);
            Assert.Equal(0, pZero.UsedOctaves);
            for (int i = 0; i < yValues.Length; i++)
            {
                AssertDoubleBitsEqual(vZero.NoiseSigns[i], pZero.NoiseSigns[i]);
                AssertDoubleBitsEqual(vZero.NoiseValues[i], pZero.NoiseValues[i]);
            }

            // > 48 octaves (triggers resize of InitialCapacity = 48)
            const int largeOctaves = 64;
            double[] largeAmps = new double[largeOctaves];
            double[] largeTh = new double[largeOctaves];
            for (int i = 0; i < largeOctaves; i++)
            {
                largeAmps[i] = 1.0 / (i + 1);
                largeTh[i] = (i % 3) * 0.001;
            }
            object vanilla64 = ctx.CreateFractal(ctx.VanillaFractalType, largeOctaves, 0.002, 0.9, seed);
            object patched64 = ctx.CreateFractal(ctx.PatchedFractalType, largeOctaves, 0.002, 0.9, seed);
            var vLarge = vanillaEval(vanilla64, 0.5, largeAmps, largeTh, 77.25, -88.5, yValues, invThresholds);
            var pLarge = patchedEval(patched64, 0.5, largeAmps, largeTh, 77.25, -88.5, yValues, invThresholds);
            AssertDoubleBitsEqual(vLarge.UncurvedBound, pLarge.UncurvedBound);
            AssertDoubleBitsEqual(vLarge.BoundMin, pLarge.BoundMin);
            AssertDoubleBitsEqual(vLarge.BoundMax, pLarge.BoundMax);
            Assert.Equal(largeOctaves, pLarge.UsedOctaves);
            for (int i = 0; i < yValues.Length; i++)
            {
                AssertDoubleBitsEqual(vLarge.NoiseSigns[i], pLarge.NoiseSigns[i]);
                AssertDoubleBitsEqual(vLarge.NoiseValues[i], pLarge.NoiseValues[i]);
            }
        }
    }

    [Fact]
    public void PatchedApi_AllocatesZeroBytesPerColumnAfterWarmup_WhileVanillaAllocates()
    {
        using var ctx = new RuntimeApiContext();
        var vanillaProbe = CompileAllocationProbe(ctx.VanillaFractalType);
        var patchedProbe = CompileAllocationProbe(ctx.PatchedFractalType);

        object vanillaFractal = ctx.CreateFractal(ctx.VanillaFractalType, 9, 0.001, 0.9, 42424242L);
        object patchedFractal = ctx.CreateFractal(ctx.PatchedFractalType, 9, 0.001, 0.9, 42424242L);
        double[] amps = [1.0, 0.8, 0.6, 0.4, 0.3, 0.2, 0.15, 0.1, 0.05];
        double[] th = [0.1, 0.05, 0.0, 0.05, 0.0, 0.0, 0.02, 0.0, 0.0];

        long vanillaBytes = 0;
        double vanillaChecksum = 0;
        RunOnDedicatedThread(() =>
        {
            _ = vanillaProbe(vanillaFractal, amps, th, 1);
            (vanillaBytes, vanillaChecksum) = vanillaProbe(vanillaFractal, amps, th, 1000);
        });

        long patchedBytes = -1;
        double patchedChecksum = 0;
        RunOnDedicatedThread(() =>
        {
            _ = patchedProbe(patchedFractal, amps, th, 1);
            (patchedBytes, patchedChecksum) = patchedProbe(patchedFractal, amps, th, 1000);
        });

        Assert.True(vanillaBytes > 0, $"Expected vanilla ColumnNoise to allocate on heap, observed {vanillaBytes} bytes.");
        Assert.Equal(0, patchedBytes);
        AssertDoubleBitsEqual(vanillaChecksum, patchedChecksum);
    }

    [Fact]
    public void PatchedApi_GuardsAgainstSameThreadAndCrossThreadAliasingAndExceptions()
    {
        using var ctx = new RuntimeApiContext();
        object patchedFractal = ctx.CreateFractal(ctx.PatchedFractalType, 4, 0.05, 0.85, 777L);
        double[] amps = [1.0, 0.5, 0.25, 0.125];
        double[] th = [0.0, 0.0, 0.0, 0.0];
        double[] shortAmps = [1.0];

        var forColumn = ctx.PatchedFractalType.GetMethod("ForColumn", BindingFlags.Public | BindingFlags.Instance)!;
        var noiseSign = ctx.PatchedColumnNoiseType.GetMethod("NoiseSign", BindingFlags.Public | BindingFlags.Instance)!;
        var noise = ctx.PatchedColumnNoiseType.GetMethod("Noise", BindingFlags.Public | BindingFlags.Instance)!;

        // Same-thread aliasing
        object col1 = forColumn.Invoke(patchedFractal, [0.5, amps, th, 10.0, 20.0])!;
        object col2 = forColumn.Invoke(patchedFractal, [0.5, amps, th, 30.0, 40.0])!;

        var signTie = Assert.Throws<TargetInvocationException>(() => noiseSign.Invoke(col1, [64.0, 0.0]));
        Assert.IsType<InvalidOperationException>(signTie.InnerException);
        var noiseTie = Assert.Throws<TargetInvocationException>(() => noise.Invoke(col1, [64.0]));
        Assert.IsType<InvalidOperationException>(noiseTie.InnerException);

        _ = noiseSign.Invoke(col2, [64.0, 0.0]);
        double col2Value = (double)noise.Invoke(col2, [64.0])!;

        // Exception safety: failed ForColumn does not invalidate col2
        var argTie = Assert.Throws<TargetInvocationException>(() => forColumn.Invoke(patchedFractal, [0.5, shortAmps, th, 10.0, 20.0]));
        Assert.IsType<IndexOutOfRangeException>(argTie.InnerException);
        AssertDoubleBitsEqual(col2Value, (double)noise.Invoke(col2, [64.0])!);

        // Default struct throws NullReferenceException
        object defaultCol = Activator.CreateInstance(ctx.PatchedColumnNoiseType)!;
        var defaultSignTie = Assert.Throws<TargetInvocationException>(() => noiseSign.Invoke(defaultCol, [64.0, 0.0]));
        Assert.IsType<NullReferenceException>(defaultSignTie.InnerException);
        var defaultNoiseTie = Assert.Throws<TargetInvocationException>(() => noise.Invoke(defaultCol, [64.0]));
        Assert.IsType<NullReferenceException>(defaultNoiseTie.InnerException);

        // Cross-thread aliasing even after thread B creates its own ColumnNoise
        object? threadACol = null;
        RunOnDedicatedThread(() =>
        {
            threadACol = forColumn.Invoke(patchedFractal, [0.5, amps, th, 10.0, 20.0]);
        });
        RunOnDedicatedThread(() =>
        {
            object local = forColumn.Invoke(patchedFractal, [0.5, amps, th, 10.0, 20.0])!;
            _ = noise.Invoke(local, [64.0]);

            var crossSignTie = Assert.Throws<TargetInvocationException>(() => noiseSign.Invoke(threadACol, [64.0, 0.0]));
            Assert.IsType<InvalidOperationException>(crossSignTie.InnerException);
            var crossNoiseTie = Assert.Throws<TargetInvocationException>(() => noise.Invoke(threadACol, [64.0]));
            Assert.IsType<InvalidOperationException>(crossNoiseTie.InnerException);
        });
    }

    [Fact]
    public void PatchedApi_MultiThreadedEvaluationMatchesSingleThreadedVanillaBitExact()
    {
        using var ctx = new RuntimeApiContext();
        var vanillaBounds = CompileBoundExtractor(ctx.VanillaFractalType);
        var patchedEval = CompileColumnEvaluator(ctx.PatchedFractalType, hasUsedOctavesProperty: true);
        var vanillaEval = CompileColumnEvaluator(ctx.VanillaFractalType, hasUsedOctavesProperty: false);

        object vanillaFractal = ctx.CreateFractal(ctx.VanillaFractalType, 9, 0.0005, 0.9, 998877L);
        object patchedFractal = ctx.CreateFractal(ctx.PatchedFractalType, 9, 0.0005, 0.9, 998877L);
        double[] amps = [1.0, 0.8, 0.6, 0.4, 0.3, 0.2, 0.15, 0.1, 0.05];
        double[] th = [0.1, 0.05, 0.0, 0.05, 0.0, 0.0, 0.02, 0.0, 0.0];
        double[] yValues = [10.0, 42.0, 96.0, 128.0, 190.0];
        double[] invTh = [-0.5, 0.0, 0.25, 0.8, -1.2];

        const int columns = 128;
        var expected = new (double UncurvedBound, double BoundMin, double BoundMax, int UsedOctaves, double[] NoiseSigns, double[] NoiseValues)[columns];
        for (int c = 0; c < columns; c++)
        {
            expected[c] = vanillaEval(vanillaFractal, 0.5, amps, th, c * 3.5, -c * 2.25, yValues, invTh);
            _ = vanillaBounds(vanillaFractal, 0.5, amps, th, c * 3.5, -c * 2.25);
        }

        var actual = new (double UncurvedBound, double BoundMin, double BoundMax, int UsedOctaves, double[] NoiseSigns, double[] NoiseValues)[columns];
        Parallel.For(0, columns, new ParallelOptions { MaxDegreeOfParallelism = 8 }, c =>
        {
            actual[c] = patchedEval(patchedFractal, 0.5, amps, th, c * 3.5, -c * 2.25, yValues, invTh);
        });

        for (int c = 0; c < columns; c++)
        {
            AssertDoubleBitsEqual(expected[c].UncurvedBound, actual[c].UncurvedBound);
            AssertDoubleBitsEqual(expected[c].BoundMin, actual[c].BoundMin);
            AssertDoubleBitsEqual(expected[c].BoundMax, actual[c].BoundMax);
            Assert.Equal(9, actual[c].UsedOctaves);
            for (int i = 0; i < yValues.Length; i++)
            {
                AssertDoubleBitsEqual(expected[c].NoiseSigns[i], actual[c].NoiseSigns[i]);
                AssertDoubleBitsEqual(expected[c].NoiseValues[i], actual[c].NoiseValues[i]);
            }
        }
    }

    [Fact]
    public void PatchColumnNoiseTls_IsIdempotentAndPassesIlVerification()
    {
        string? vanillaApiPath = new[]
        {
            ".vanilla/linux-x64/vintagestory/VintagestoryAPI.vanilla.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.vanilla.dll",
            ".vanilla/linux-x64/vintagestory/VintagestoryAPI.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll",
        }.Select(rel =>
        {
            try { return PatchReader.FindRepositoryFile(rel); }
            catch (FileNotFoundException) { return null; }
        }).FirstOrDefault(p => p != null && File.Exists(p));

        if (vanillaApiPath == null)
        {
            return;
        }

        using var resolver = ApiPatcher.CreateResolver(vanillaApiPath, typeof(OptimumApiBridge).Assembly.Location);
        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath, new ReaderParameters { AssemblyResolver = resolver });
        using var contracts = AssemblyDefinition.ReadAssembly(typeof(OptimumApiBridge).Assembly.Location, new ReaderParameters { AssemblyResolver = resolver });

        Assert.Equal(3, ApiPatcher.PatchColumnNoiseTls(vanilla.MainModule, contracts.MainModule));
        Assert.Equal(3, ApiPatcher.PatchColumnNoiseTls(vanilla.MainModule, contracts.MainModule));
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));
    }

    private static ColumnEvaluator CompileColumnEvaluator(Type fractalType, bool hasUsedOctavesProperty)
    {
        Type columnType = fractalType.GetNestedType("ColumnNoise", BindingFlags.Public)!;
        var forColumn = fractalType.GetMethod("ForColumn", BindingFlags.Public | BindingFlags.Instance)!;
        var noiseSign = columnType.GetMethod("NoiseSign", BindingFlags.Public | BindingFlags.Instance)!;
        var noise = columnType.GetMethod("Noise", BindingFlags.Public | BindingFlags.Instance)!;
        var getUncurved = columnType.GetProperty("UncurvedBound", BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;
        var getMin = columnType.GetProperty("BoundMin", BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;
        var getMax = columnType.GetProperty("BoundMax", BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;
        var getUsed = hasUsedOctavesProperty
            ? columnType.GetProperty("UsedOctaves", BindingFlags.Public | BindingFlags.Instance)!.GetMethod!
            : null;

        var pFractalObj = Expression.Parameter(typeof(object), "fractalObj");
        var pRelY = Expression.Parameter(typeof(double), "relY");
        var pAmps = Expression.Parameter(typeof(double[]), "amps");
        var pTh = Expression.Parameter(typeof(double[]), "th");
        var pX = Expression.Parameter(typeof(double), "x");
        var pZ = Expression.Parameter(typeof(double), "z");
        var pYs = Expression.Parameter(typeof(double[]), "ys");
        var pInvTh = Expression.Parameter(typeof(double[]), "invTh");

        var colVar = Expression.Variable(columnType, "col");
        var signsVar = Expression.Variable(typeof(double[]), "signs");
        var valuesVar = Expression.Variable(typeof(double[]), "values");
        var iVar = Expression.Variable(typeof(int), "i");
        var breakLabel = Expression.Label("loopEnd");

        var tupleCtor = typeof(ValueTuple<double, double, double, int, double[], double[]>)
            .GetConstructor([typeof(double), typeof(double), typeof(double), typeof(int), typeof(double[]), typeof(double[])])!;

        var body = Expression.Block(
            [colVar, signsVar, valuesVar, iVar],
            Expression.Assign(
                colVar,
                Expression.Call(Expression.Convert(pFractalObj, fractalType), forColumn, pRelY, pAmps, pTh, pX, pZ)),
            Expression.Assign(signsVar, Expression.NewArrayBounds(typeof(double), Expression.ArrayLength(pYs))),
            Expression.Assign(valuesVar, Expression.NewArrayBounds(typeof(double), Expression.ArrayLength(pYs))),
            Expression.Assign(iVar, Expression.Constant(0)),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.LessThan(iVar, Expression.ArrayLength(pYs)),
                    Expression.Block(
                        Expression.Assign(
                            Expression.ArrayAccess(signsVar, iVar),
                            Expression.Call(
                                colVar,
                                noiseSign,
                                Expression.ArrayIndex(pYs, iVar),
                                Expression.ArrayIndex(pInvTh, iVar))),
                        Expression.Assign(
                            Expression.ArrayAccess(valuesVar, iVar),
                            Expression.Call(colVar, noise, Expression.ArrayIndex(pYs, iVar))),
                        Expression.PostIncrementAssign(iVar)),
                    Expression.Break(breakLabel)),
                breakLabel),
            Expression.New(
                tupleCtor,
                Expression.Call(colVar, getUncurved),
                Expression.Call(colVar, getMin),
                Expression.Call(colVar, getMax),
                getUsed != null ? (Expression)Expression.Call(colVar, getUsed) : Expression.Constant(-1),
                signsVar,
                valuesVar));

        return Expression.Lambda<ColumnEvaluator>(body, pFractalObj, pRelY, pAmps, pTh, pX, pZ, pYs, pInvTh).Compile();
    }

    private static BoundExtractor CompileBoundExtractor(Type fractalType)
    {
        Type columnType = fractalType.GetNestedType("ColumnNoise", BindingFlags.Public)!;
        var forColumn = fractalType.GetMethod("ForColumn", BindingFlags.Public | BindingFlags.Instance)!;
        var getUncurved = columnType.GetProperty("UncurvedBound", BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;
        var getMin = columnType.GetProperty("BoundMin", BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;
        var getMax = columnType.GetProperty("BoundMax", BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;

        var pFractalObj = Expression.Parameter(typeof(object), "fractalObj");
        var pRelY = Expression.Parameter(typeof(double), "relY");
        var pAmps = Expression.Parameter(typeof(double[]), "amps");
        var pTh = Expression.Parameter(typeof(double[]), "th");
        var pX = Expression.Parameter(typeof(double), "x");
        var pZ = Expression.Parameter(typeof(double), "z");
        var colVar = Expression.Variable(columnType, "col");
        var tupleCtor = typeof(ValueTuple<double, double, double>).GetConstructor([typeof(double), typeof(double), typeof(double)])!;

        var body = Expression.Block(
            [colVar],
            Expression.Assign(
                colVar,
                Expression.Call(Expression.Convert(pFractalObj, fractalType), forColumn, pRelY, pAmps, pTh, pX, pZ)),
            Expression.New(
                tupleCtor,
                Expression.Call(colVar, getUncurved),
                Expression.Call(colVar, getMin),
                Expression.Call(colVar, getMax)));

        return Expression.Lambda<BoundExtractor>(body, pFractalObj, pRelY, pAmps, pTh, pX, pZ).Compile();
    }

    private static AllocationProbe CompileAllocationProbe(Type fractalType)
    {
        Type columnType = fractalType.GetNestedType("ColumnNoise", BindingFlags.Public)!;
        var forColumn = fractalType.GetMethod("ForColumn", BindingFlags.Public | BindingFlags.Instance)!;
        var noiseSign = columnType.GetMethod("NoiseSign", BindingFlags.Public | BindingFlags.Instance)!;
        var noise = columnType.GetMethod("Noise", BindingFlags.Public | BindingFlags.Instance)!;
        var getBytes = typeof(GC).GetMethod(nameof(GC.GetAllocatedBytesForCurrentThread), BindingFlags.Public | BindingFlags.Static)!;

        var pFractalObj = Expression.Parameter(typeof(object), "fractalObj");
        var pAmps = Expression.Parameter(typeof(double[]), "amps");
        var pTh = Expression.Parameter(typeof(double[]), "th");
        var pIter = Expression.Parameter(typeof(int), "iterations");

        var fractalVar = Expression.Variable(fractalType, "fractal");
        var colVar = Expression.Variable(columnType, "col");
        var iVar = Expression.Variable(typeof(int), "i");
        var startVar = Expression.Variable(typeof(long), "startBytes");
        var sumVar = Expression.Variable(typeof(double), "sum");
        var breakLabel = Expression.Label("end");
        var tupleCtor = typeof(ValueTuple<long, double>).GetConstructor([typeof(long), typeof(double)])!;

        var body = Expression.Block(
            [fractalVar, colVar, iVar, startVar, sumVar],
            Expression.Assign(fractalVar, Expression.Convert(pFractalObj, fractalType)),
            Expression.Assign(sumVar, Expression.Constant(0.0)),
            Expression.Assign(iVar, Expression.Constant(0)),
            Expression.Assign(startVar, Expression.Call(getBytes)),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.LessThan(iVar, pIter),
                    Expression.Block(
                        Expression.Assign(
                            colVar,
                            Expression.Call(
                                fractalVar,
                                forColumn,
                                Expression.Constant(0.5),
                                pAmps,
                                pTh,
                                Expression.Convert(iVar, typeof(double)),
                                Expression.Convert(iVar, typeof(double)))),
                        Expression.AddAssign(
                            sumVar,
                            Expression.Call(colVar, noiseSign, Expression.Constant(64.0), Expression.Constant(0.1))),
                        Expression.AddAssign(
                            sumVar,
                            Expression.Call(colVar, noise, Expression.Constant(64.0))),
                        Expression.PostIncrementAssign(iVar)),
                    Expression.Break(breakLabel)),
                breakLabel),
            Expression.New(
                tupleCtor,
                Expression.Subtract(Expression.Call(getBytes), startVar),
                sumVar));

        return Expression.Lambda<AllocationProbe>(body, pFractalObj, pAmps, pTh, pIter).Compile();
    }

    private static void RunOnDedicatedThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void AssertDoubleBitsEqual(double expected, double actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
    }
}
