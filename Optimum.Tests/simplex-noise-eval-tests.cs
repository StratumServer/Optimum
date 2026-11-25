using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Optimum.Patcher;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public sealed class SimplexNoiseEvalTests
{
    private delegate float NoiseLayerEvaluator(long seed, double x, double y, double z);
    private delegate double DoubleSmoothEvaluator(double a, double b, double smoothingFactor);
    private delegate float FloatSmoothEvaluator(float a, float b, float smoothingFactor);
    private delegate double FractalNoiseEvaluator(object fractal, double x, double y, double z, double[] amplitudes, double[] thresholds);

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
        public readonly Type VanillaLayerType;
        public readonly Type PatchedLayerType;
        public readonly Type VanillaGameMathType;
        public readonly Type PatchedGameMathType;
        public readonly Type VanillaFractalType;
        public readonly Type PatchedFractalType;

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

            TempDir = Path.Combine(Path.GetTempPath(), $"optimum-simplex-eval-{Guid.NewGuid():N}");
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

            VanillaLayerType = VanillaAsm.GetType("Vintagestory.API.MathTools.NewSimplexNoiseLayer", throwOnError: true)!;
            PatchedLayerType = PatchedAsm.GetType("Vintagestory.API.MathTools.NewSimplexNoiseLayer", throwOnError: true)!;
            VanillaGameMathType = VanillaAsm.GetType("Vintagestory.API.MathTools.GameMath", throwOnError: true)!;
            PatchedGameMathType = PatchedAsm.GetType("Vintagestory.API.MathTools.GameMath", throwOnError: true)!;
            VanillaFractalType = VanillaAsm.GetType("Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise", throwOnError: true)!;
            PatchedFractalType = PatchedAsm.GetType("Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise", throwOnError: true)!;
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
    public void OptimumSimplexNoise_Gradients3D_MatchesVanillaBitExact()
    {
        using var ctx = new RuntimeApiContext();

        var vanillaField = ctx.VanillaLayerType.GetField("Gradients3D", BindingFlags.NonPublic | BindingFlags.Static)!;
        var optimumField = typeof(OptimumSimplexNoise).GetField("Gradients3D", BindingFlags.NonPublic | BindingFlags.Static)!;

        float[] vanillaGradients = (float[])vanillaField.GetValue(null)!;
        float[] optimumGradients = (float[])optimumField.GetValue(null)!;

        Assert.Equal(vanillaGradients.Length, optimumGradients.Length);
        for (int i = 0; i < vanillaGradients.Length; i++)
        {
            AssertFloatBitsEqual(vanillaGradients[i], optimumGradients[i]);
        }
    }

    [Fact]
    public void NewSimplexNoiseLayer_PatchedAndSource_MatchVanillaBitExact()
    {
        using var ctx = new RuntimeApiContext();

        var vanillaImproved = CompileLayerDelegate(ctx.VanillaLayerType, "Evaluate_ImprovedXZ");
        var patchedImproved = CompileLayerDelegate(ctx.PatchedLayerType, "Evaluate_ImprovedXZ");
        var vanillaFallback = CompileLayerDelegate(ctx.VanillaLayerType, "Evaluate_FallbackOrientation");
        var patchedFallback = CompileLayerDelegate(ctx.PatchedLayerType, "Evaluate_FallbackOrientation");

        long[] seeds = [0L, 1L, -1L, 42424242L, long.MinValue + 12345L, long.MaxValue - 999L];
        double[] coords =
        [
            0.0, -0.0, 0.5, -0.5, 1.0, -1.0, 1.234567, -1.234567,
            31.9999, -31.9999, 128.0, -256.25, 4096.125, -4096.875, 512000.5, -512000.5
        ];

        foreach (long seed in seeds)
        {
            foreach (double x in coords)
            {
                foreach (double y in new[] { 0.0, -0.0, 0.333, -0.75, 64.5, 255.9 })
                {
                    foreach (double z in new[] { 0.0, -1.0, 17.25, -512.125, 12345.678 })
                    {
                        float vImp = vanillaImproved(seed, x, y, z);
                        float pImp = patchedImproved(seed, x, y, z);
                        float sImp = NewSimplexNoiseLayer.Evaluate_ImprovedXZ(seed, x, y, z);
                        AssertFloatBitsEqual(vImp, pImp);
                        AssertFloatBitsEqual(vImp, sImp);

                        float vFb = vanillaFallback(seed, x, y, z);
                        float pFb = patchedFallback(seed, x, y, z);
                        float sFb = NewSimplexNoiseLayer.Evaluate_FallbackOrientation(seed, x, y, z);
                        AssertFloatBitsEqual(vFb, pFb);
                        AssertFloatBitsEqual(vFb, sFb);
                    }
                }
            }
        }
    }

    [Fact]
    public void GameMath_SmoothMinAndSmoothMax_MatchVanillaBitExactAcrossEdgeCases()
    {
        using var ctx = new RuntimeApiContext();

        var vMinD = CompileDoubleSmoothDelegate(ctx.VanillaGameMathType, "SmoothMin");
        var pMinD = CompileDoubleSmoothDelegate(ctx.PatchedGameMathType, "SmoothMin");
        var vMaxD = CompileDoubleSmoothDelegate(ctx.VanillaGameMathType, "SmoothMax");
        var pMaxD = CompileDoubleSmoothDelegate(ctx.PatchedGameMathType, "SmoothMax");

        var vMinF = CompileFloatSmoothDelegate(ctx.VanillaGameMathType, "SmoothMin");
        var pMinF = CompileFloatSmoothDelegate(ctx.PatchedGameMathType, "SmoothMin");
        var vMaxF = CompileFloatSmoothDelegate(ctx.VanillaGameMathType, "SmoothMax");
        var pMaxF = CompileFloatSmoothDelegate(ctx.PatchedGameMathType, "SmoothMax");

        double[] dValues =
        [
            0.0, -0.0, 1.0, -1.0, 0.25, -0.25, 0.5, -0.5, 1.25, -1.25, 100.0, -100.0,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity
        ];
        double[] dFactors = [0.0, -0.0, -0.5, 0.25, 0.5, 1.0, 2.5, double.PositiveInfinity, double.NaN];

        foreach (double a in dValues)
        {
            foreach (double b in dValues)
            {
                foreach (double sf in dFactors)
                {
                    double expMin = vMinD(a, b, sf);
                    AssertDoubleBitsEqual(expMin, pMinD(a, b, sf));
                    AssertDoubleBitsEqual(expMin, GameMath.SmoothMin(a, b, sf));

                    double expMax = vMaxD(a, b, sf);
                    AssertDoubleBitsEqual(expMax, pMaxD(a, b, sf));
                    AssertDoubleBitsEqual(expMax, GameMath.SmoothMax(a, b, sf));
                }
            }
        }

        float[] fValues =
        [
            0.0f, -0.0f, 1.0f, -1.0f, 0.25f, -0.25f, 0.5f, -0.5f, 1.25f, -1.25f, 100.0f, -100.0f,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity
        ];
        float[] fFactors = [0.0f, -0.0f, -0.5f, 0.25f, 0.5f, 1.0f, 2.5f, float.PositiveInfinity, float.NaN];

        foreach (float a in fValues)
        {
            foreach (float b in fValues)
            {
                foreach (float sf in fFactors)
                {
                    float expMin = vMinF(a, b, sf);
                    AssertFloatBitsEqual(expMin, pMinF(a, b, sf));
                    AssertFloatBitsEqual(expMin, GameMath.SmoothMin(a, b, sf));

                    float expMax = vMaxF(a, b, sf);
                    AssertFloatBitsEqual(expMax, pMaxF(a, b, sf));
                    AssertFloatBitsEqual(expMax, GameMath.SmoothMax(a, b, sf));
                }
            }
        }
    }

    [Fact]
    public void ColumnNoise_EarlyNanBreakAndPreRotation_MatchVanillaBitExact()
    {
        using var ctx = new RuntimeApiContext();

        var vFractalNoise = CompileFractalNoiseDelegate(ctx.VanillaFractalType);
        var pFractalNoise = CompileFractalNoiseDelegate(ctx.PatchedFractalType);

        double[] amps = [0.9, 0.7, 0.5, 0.35, 0.25, 0.15, 0.1, 0.05, 0.02];
        double[] ths = [0.15, 0.1, 0.05, 0.03, 0.02, 0.01, 0.0, 0.0, 0.0];

        object vFractal = ctx.CreateFractal(ctx.VanillaFractalType, 9, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency, 0.9, 42424242L);
        object pFractal = ctx.CreateFractal(ctx.PatchedFractalType, 9, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency, 0.9, 42424242L);
        var sFractal = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(9, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency, 0.9, 42424242L);

        // Verify direct 3D fractal Noise as well (exercises SmoothMin/Max + Evaluate_ImprovedXZ).
        for (int i = 0; i < 32; i++)
        {
            double x = i * 3.25 - 40.0;
            double y = (i % 11) * 21.5;
            double z = i * 2.75 + 10.0;
            double exp = vFractalNoise(vFractal, x, y, z, amps, ths);
            AssertDoubleBitsEqual(exp, pFractalNoise(pFractal, x, y, z, amps, ths));
            AssertDoubleBitsEqual(exp, sFractal.Noise(x, y, z, amps, ths));
        }

        // Exercise columns where early y steps stop after fewer octaves (leaving deeper pastEvaluations[j].Y == NaN)
        // and later y steps fall inside the sign uncertainty window and hit those NaN slots.
        var vNoiseSign = CompileColumnNoiseSign(ctx.VanillaFractalType);
        var pNoiseSign = CompileColumnNoiseSign(ctx.PatchedFractalType);

        for (int colIdx = 0; colIdx < 8; colIdx++)
        {
            double nx = (colIdx - 3) * 19.75;
            double nz = (colIdx + 1) * 13.5;

            var sCol = sFractal.ForColumn(0.5, amps, ths, nx, nz);
            object? vCol = CreateColumn(ctx.VanillaFractalType, vFractal, 0.5, amps, ths, nx, nz);
            object? pCol = CreateColumn(ctx.PatchedFractalType, pFractal, 0.5, amps, ths, nx, nz);

            // Start at strong negative/positive inverse thresholds so first y exits early on octave 0..2,
            // then sweep across y=0..255 with sign-flipping thresholds.
            double[] testThresholds = [-4.0, -3.5, -0.5, 0.0, 0.2, -0.2, 3.5, 4.0];
            for (int step = 0; step < 256; step++)
            {
                double invTh = testThresholds[(step + colIdx) % testThresholds.Length];
                double y = step;
                double expSign = vNoiseSign(vCol, y, invTh);
                AssertDoubleBitsEqual(expSign, pNoiseSign(pCol, y, invTh));
                AssertDoubleBitsEqual(expSign, sCol.NoiseSign(y, invTh));
            }
        }
    }

    [Fact]
    public void PatchedApi_HasAggressiveInlining_IsIdempotent_AndPassesIlVerification()
    {
        string vanillaApiPath = new[]
        {
            ".vanilla/linux-x64/vintagestory/VintagestoryAPI.vanilla.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.vanilla.dll",
            ".vanilla/linux-x64/vintagestory/VintagestoryAPI.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll",
        }.Select(p =>
        {
            try { return PatchReader.FindRepositoryFile(p); }
            catch (FileNotFoundException) { return null; }
        }).First(p => p != null && File.Exists(p))!;

        string contractsPath = PatchReader.FindRepositoryFile("bin/Release/net10.0/Optimum.Api.Contracts.dll");
        using var resolver = ApiPatcher.CreateResolver(vanillaApiPath, contractsPath);
        using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath, new ReaderParameters { AssemblyResolver = resolver });
        using var contracts = AssemblyDefinition.ReadAssembly(contractsPath, new ReaderParameters { AssemblyResolver = resolver });

        Assert.Equal(4, ApiPatcher.PatchGameMathSmoothMinMax(vanilla.MainModule, contracts.MainModule));
        Assert.Equal(4, ApiPatcher.PatchGameMathSmoothMinMax(vanilla.MainModule, contracts.MainModule));
        Assert.Equal(2, ApiPatcher.PatchNewSimplexNoiseLayer(vanilla.MainModule, contracts.MainModule));
        Assert.Equal(2, ApiPatcher.PatchNewSimplexNoiseLayer(vanilla.MainModule, contracts.MainModule));

        Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(vanilla.MainModule));
        Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));

        using var ctx = new RuntimeApiContext();
        foreach (string name in new[] { "SmoothMin", "SmoothMax" })
        {
            foreach (Type scalar in new[] { typeof(float), typeof(double) })
            {
                var method = ctx.PatchedGameMathType.GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Static,
                    [scalar, scalar, scalar])!;
                Assert.True((method.GetMethodImplementationFlags() & System.Reflection.MethodImplAttributes.AggressiveInlining) != 0,
                    $"Patched GameMath.{name}({scalar.Name}) missing AggressiveInlining");
            }
        }

        foreach (string name in new[] { "Evaluate_ImprovedXZ", "Evaluate_FallbackOrientation" })
        {
            var method = ctx.PatchedLayerType.GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Static,
                [typeof(long), typeof(double), typeof(double), typeof(double)])!;
            Assert.True((method.GetMethodImplementationFlags() & System.Reflection.MethodImplAttributes.AggressiveInlining) != 0,
                $"Patched NewSimplexNoiseLayer.{name} missing AggressiveInlining");
        }
    }

    private static NoiseLayerEvaluator CompileLayerDelegate(Type layerType, string methodName)
    {
        var method = layerType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            [typeof(long), typeof(double), typeof(double), typeof(double)])!;
        var seed = Expression.Parameter(typeof(long), "seed");
        var x = Expression.Parameter(typeof(double), "x");
        var y = Expression.Parameter(typeof(double), "y");
        var z = Expression.Parameter(typeof(double), "z");
        return Expression.Lambda<NoiseLayerEvaluator>(Expression.Call(method, seed, x, y, z), seed, x, y, z).Compile();
    }

    private static DoubleSmoothEvaluator CompileDoubleSmoothDelegate(Type gameMathType, string methodName)
    {
        var method = gameMathType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            [typeof(double), typeof(double), typeof(double)])!;
        var a = Expression.Parameter(typeof(double), "a");
        var b = Expression.Parameter(typeof(double), "b");
        var sf = Expression.Parameter(typeof(double), "sf");
        return Expression.Lambda<DoubleSmoothEvaluator>(Expression.Call(method, a, b, sf), a, b, sf).Compile();
    }

    private static FloatSmoothEvaluator CompileFloatSmoothDelegate(Type gameMathType, string methodName)
    {
        var method = gameMathType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            [typeof(float), typeof(float), typeof(float)])!;
        var a = Expression.Parameter(typeof(float), "a");
        var b = Expression.Parameter(typeof(float), "b");
        var sf = Expression.Parameter(typeof(float), "sf");
        return Expression.Lambda<FloatSmoothEvaluator>(Expression.Call(method, a, b, sf), a, b, sf).Compile();
    }

    private static FractalNoiseEvaluator CompileFractalNoiseDelegate(Type fractalType)
    {
        var method = fractalType.GetMethod("Noise", BindingFlags.Public | BindingFlags.Instance)!;
        var fObj = Expression.Parameter(typeof(object), "f");
        var x = Expression.Parameter(typeof(double), "x");
        var y = Expression.Parameter(typeof(double), "y");
        var z = Expression.Parameter(typeof(double), "z");
        var amps = Expression.Parameter(typeof(double[]), "amps");
        var ths = Expression.Parameter(typeof(double[]), "ths");
        return Expression.Lambda<FractalNoiseEvaluator>(
            Expression.Call(Expression.Convert(fObj, fractalType), method, x, y, z, amps, ths),
            fObj, x, y, z, amps, ths).Compile();
    }

    private static object CreateColumn(
        Type fractalType,
        object fractal,
        double relY,
        double[] amps,
        double[] ths,
        double nx,
        double nz)
    {
        var forColumn = fractalType.GetMethod("ForColumn", BindingFlags.Public | BindingFlags.Instance)!;
        return forColumn.Invoke(fractal, [relY, amps, ths, nx, nz])!;
    }

    private static Func<object, double, double, double> CompileColumnNoiseSign(Type fractalType)
    {
        Type colType = fractalType.GetNestedType("ColumnNoise", BindingFlags.Public)!;
        var noiseSign = colType.GetMethod("NoiseSign", BindingFlags.Public | BindingFlags.Instance)!;
        var cObj = Expression.Parameter(typeof(object), "col");
        var y = Expression.Parameter(typeof(double), "y");
        var inv = Expression.Parameter(typeof(double), "inv");
        return Expression.Lambda<Func<object, double, double, double>>(
            Expression.Call(Expression.Convert(cObj, colType), noiseSign, y, inv),
            cObj, y, inv).Compile();
    }

    private static void AssertDoubleBitsEqual(double expected, double actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
    }

    private static void AssertFloatBitsEqual(float expected, float actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
    }
}
