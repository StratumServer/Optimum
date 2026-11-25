#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Intrinsics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Optimum.Patcher;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;
using Xunit;

namespace Optimum.Tests;

public sealed class TerrainWorldgenRuntimeDeliveryTests
{
    private static readonly string[] ExpectedRuntimePatchedTypes =
    {
        "GenTerra",
        "GenRockStrataNew",
        "GenPartial",
        "GenCaves",
        "GenBlockLayers",
    };

    [Fact]
    public void EssentialsManifest_CoversEveryDirectlySafeTerrainHandler()
    {
        string supplyChunks = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch");

        Match allowListMatch = Regex.Match(
            supplyChunks,
            @"IsWorldgenHandlerAllowedForParallelPass\s*\([^)]*\)\s*\{(?<body>.*?)(?=private\s+bool\s+IsWorldgenHandlerDirectlySafe)",
            RegexOptions.Singleline);
        Assert.True(allowListMatch.Success, "Could not locate IsWorldgenHandlerAllowedForParallelPass in ServerSystemSupplyChunks patch.");

        var allowedServerMods = Regex.Matches(allowListMatch.Groups["body"].Value, @"Vintagestory\.ServerMods\.([A-Za-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .Where(name => name != "GenDevastationLayer")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("GenTerra", allowedServerMods);
        Assert.Contains("GenRockStrataNew", allowedServerMods);
        Assert.Contains("GenCaves", allowedServerMods);

        foreach (string typeShortName in ExpectedRuntimePatchedTypes)
        {
            string patchPath = PatchReader.FindRepositoryFile(
                $"patches/runtime/VSEssentials/Vintagestory/ServerMods/{typeShortName}.cs.patch");
            Assert.True(File.Exists(patchPath), $"Missing runtime patch for {typeShortName}");
        }

        MethodInfo manifestMethod = typeof(ModPatcher).GetMethod(
            "EssentialsManifest",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;
        Type manifestType = manifest.GetType();

        var injectedTypes = (IReadOnlyList<string>)manifestType.GetProperty("Types")!.GetValue(manifest)!;
        var injectedMembers = (IReadOnlyDictionary<string, List<string>>)manifestType.GetProperty("Members")!.GetValue(manifest)!;
        var methodsObj = (System.Collections.IEnumerable)manifestType.GetProperty("Methods")!.GetValue(manifest)!;

        Assert.Contains("Vintagestory.ServerMods.OptimumGenTerraTempData", injectedTypes);
        Assert.Contains("Vintagestory.ServerMods.OptimumGenTerraWorkspace", injectedTypes);
        Assert.Contains("Vintagestory.ServerMods.OptimumRockStrataWorkspace", injectedTypes);
        Assert.Contains("Vintagestory.ServerMods.OptimumBlockLayerWorkspace", injectedTypes);

        foreach (string typeShortName in allowedServerMods.Concat(new[] { "GenPartial", "GenBlockLayers" }).Distinct())
        {
            string fullName = $"Vintagestory.ServerMods.{typeShortName}";
            Assert.True(injectedMembers.ContainsKey(fullName), $"EssentialsManifest.Members missing {fullName}");
        }

        var methodsByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (object target in methodsObj)
        {
            Type t = target.GetType();
            string typeName = (string)t.GetProperty("TypeFullName")!.GetValue(target)!;
            string methodName = (string)t.GetProperty("MethodName")!.GetValue(target)!;
            if (!methodsByType.TryGetValue(typeName, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                methodsByType[typeName] = set;
            }
            set.Add(methodName);
        }

        Assert.Contains("initWorldGen", methodsByType["Vintagestory.ServerMods.GenTerra"]);
        Assert.Contains("OnChunkColumnGen", methodsByType["Vintagestory.ServerMods.GenTerra"]);
        Assert.Contains("generate", methodsByType["Vintagestory.ServerMods.GenTerra"]);
        Assert.Contains("GetOrLoadLerpedLandformMap", methodsByType["Vintagestory.ServerMods.GenTerra"]);
        Assert.Contains("GetInterpolatedOctaves", methodsByType["Vintagestory.ServerMods.GenTerra"]);

        Assert.Contains("initWorldGen", methodsByType["Vintagestory.ServerMods.GenRockStrataNew"]);
        Assert.Contains("preLoad", methodsByType["Vintagestory.ServerMods.GenRockStrataNew"]);
        Assert.Contains("genBlockColumn", methodsByType["Vintagestory.ServerMods.GenRockStrataNew"]);
        Assert.Contains("GetOrLoadLerpedProvinceMap", methodsByType["Vintagestory.ServerMods.GenRockStrataNew"]);

        Assert.Contains("initWorldGen", methodsByType["Vintagestory.ServerMods.GenPartial"]);
        Assert.Contains("GenChunkColumn", methodsByType["Vintagestory.ServerMods.GenPartial"]);

        Assert.Contains("initWorldGen", methodsByType["Vintagestory.ServerMods.GenCaves"]);
        Assert.Contains("CmdCaveGenTest", methodsByType["Vintagestory.ServerMods.GenCaves"]);
        Assert.Contains("GeneratePartial", methodsByType["Vintagestory.ServerMods.GenCaves"]);
        Assert.Contains("CarveTunnel", methodsByType["Vintagestory.ServerMods.GenCaves"]);
        Assert.Contains("CarveShaft", methodsByType["Vintagestory.ServerMods.GenCaves"]);
        Assert.Contains("SetBlocks", methodsByType["Vintagestory.ServerMods.GenCaves"]);

        Assert.Contains("InitWorldGen", methodsByType["Vintagestory.ServerMods.GenBlockLayers"]);
        Assert.Contains("OnChunkColumnGeneration", methodsByType["Vintagestory.ServerMods.GenBlockLayers"]);
        Assert.Contains("PutLayers", methodsByType["Vintagestory.ServerMods.GenBlockLayers"]);
        Assert.Contains("PlaceTallGrass", methodsByType["Vintagestory.ServerMods.GenBlockLayers"]);
        Assert.Contains("LoadBlockLayers", methodsByType["Vintagestory.ServerMods.GenBlockLayers"]);
    }

    [Fact]
    public void ModPatcher_VSEssentials_TransplantsAndJitsTerrainGenerators()
    {
        if (!TryLocateRuntimeDonor(out string root, out string vanillaPath, out string compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_terrain_delivery_{Guid.NewGuid():N}.dll");
        IsolatedEssentialsContext? alc = null;
        try
        {
            Assert.True(ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            Assert.True(File.Exists(tempOutput));

            using (AssemblyDefinition asmDef = AssemblyDefinition.ReadAssembly(tempOutput))
            {
                ModuleDefinition mod = asmDef.MainModule;
                Assert.NotNull(mod.GetType("Vintagestory.ServerMods.OptimumGenTerraTempData"));
                Assert.NotNull(mod.GetType("Vintagestory.ServerMods.OptimumGenTerraWorkspace"));
                Assert.NotNull(mod.GetType("Vintagestory.ServerMods.OptimumRockStrataWorkspace"));
                Assert.NotNull(mod.GetType("Vintagestory.ServerMods.OptimumBlockLayerWorkspace"));

                TypeDefinition genTerra = mod.GetType("Vintagestory.ServerMods.GenTerra");
                var generateMethods = genTerra.Methods.Where(m => m.Name == "generate").ToList();
                Assert.Single(generateMethods);
                Assert.Equal(4, generateMethods[0].Parameters.Count);

                Assert.NotNull(genTerra.Fields.FirstOrDefault(f => f.Name == "optimumWorkspaces"));
                Assert.NotNull(genTerra.Fields.FirstOrDefault(f => f.Name == "optimumTempDataThreadLocal"));
                Assert.NotNull(genTerra.Fields.FirstOrDefault(f => f.Name == "columnResults"));
                Assert.NotNull(genTerra.Fields.FirstOrDefault(f => f.Name == "taperMap"));

                TypeDefinition genRock = mod.GetType("Vintagestory.ServerMods.GenRockStrataNew");
                Assert.NotNull(genRock.Fields.FirstOrDefault(f => f.Name == "optimumWorkspaces"));
                Assert.NotNull(genRock.Fields.FirstOrDefault(f => f.Name == "rockGroupMaxThickness"));
                Assert.NotNull(genRock.Fields.FirstOrDefault(f => f.Name == "mapChunk"));

                TypeDefinition genPartial = mod.GetType("Vintagestory.ServerMods.GenPartial");
                Assert.NotNull(genPartial.Fields.FirstOrDefault(f => f.Name == "chunkRandThreadLocal"));
                Assert.NotNull(genPartial.Fields.FirstOrDefault(f => f.Name == "chunkRand"));

                TypeDefinition genCaves = mod.GetType("Vintagestory.ServerMods.GenCaves");
                Assert.NotNull(genCaves.Fields.FirstOrDefault(f => f.Name == "caveRandThreadLocal"));
                Assert.NotNull(genCaves.Fields.FirstOrDefault(f => f.Name == "caveRand"));

                TypeDefinition genLayers = mod.GetType("Vintagestory.ServerMods.GenBlockLayers");
                Assert.NotNull(genLayers.Fields.FirstOrDefault(f => f.Name == "optimumWorkspaces"));
                Assert.NotNull(genLayers.Fields.FirstOrDefault(f => f.Name == "layersUnderWater"));
                Assert.NotNull(genLayers.Fields.FirstOrDefault(f => f.Name == "layersUnderWaterTmp"));
            }

            alc = new IsolatedEssentialsContext(root, tempOutput, harmonyDllPath: null, isCollectible: true);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);

            const BindingFlags allDeclared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (string shortName in ExpectedRuntimePatchedTypes)
            {
                Type type = patchedAsm.GetType($"Vintagestory.ServerMods.{shortName}", throwOnError: true)!;
                foreach (MethodInfo method in type.GetMethods(allDeclared))
                {
                    if (!method.IsAbstract)
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    }
                }
                foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    foreach (MethodInfo method in nested.GetMethods(allDeclared))
                    {
                        if (!method.IsAbstract && !method.ContainsGenericParameters)
                        {
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        }
                    }
                }
            }

            // Verify ThreadLocal workspace isolation via runtime-patched GenRockStrataNew helper without initWorldGen.
            Type patchedRockType = patchedAsm.GetType("Vintagestory.ServerMods.GenRockStrataNew", throwOnError: true)!;
            object rockInstance = RuntimeHelpers.GetUninitializedObject(patchedRockType);
            MethodInfo getWorkspace = patchedRockType.GetMethod("GetOptimumWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var threadWorkspaces = new object?[2];
            using var bothReady = new Barrier(2);
            var threads = new Thread[2];
            for (int t = 0; t < 2; t++)
            {
                int slot = t;
                threads[t] = new Thread(() =>
                {
                    bothReady.SignalAndWait();
                    threadWorkspaces[slot] = getWorkspace.Invoke(rockInstance, null);
                });
                threads[t].Start();
            }
            foreach (Thread thread in threads)
            {
                thread.Join();
            }
            Assert.All(threadWorkspaces, Assert.NotNull);
            Assert.NotSame(threadWorkspaces[0], threadWorkspaces[1]);

            // Verify OptimumGenTerraWorkspace bit-packing across columns.
            Type terraWorkspaceType = patchedAsm.GetType("Vintagestory.ServerMods.OptimumGenTerraWorkspace", throwOnError: true)!;
            object workspace = Activator.CreateInstance(terraWorkspaceType, 384)!;
            MethodInfo setSolid = terraWorkspaceType.GetMethod("SetSolid")!;
            MethodInfo isSolid = terraWorkspaceType.GetMethod("IsSolid")!;
            MethodInfo clearCol = terraWorkspaceType.GetMethod("ClearColumn")!;

            setSolid.Invoke(workspace, new object[] { 17, 63 });
            setSolid.Invoke(workspace, new object[] { 17, 64 });
            setSolid.Invoke(workspace, new object[] { 17, 382 });
            Assert.True((bool)isSolid.Invoke(workspace, new object[] { 17, 63 })!);
            Assert.True((bool)isSolid.Invoke(workspace, new object[] { 17, 64 })!);
            Assert.True((bool)isSolid.Invoke(workspace, new object[] { 17, 382 })!);
            Assert.False((bool)isSolid.Invoke(workspace, new object[] { 17, 65 })!);
            Assert.False((bool)isSolid.Invoke(workspace, new object[] { 18, 63 })!);
            clearCol.Invoke(workspace, new object[] { 17 });
            Assert.False((bool)isSolid.Invoke(workspace, new object[] { 17, 64 })!);
        }
        finally
        {
            alc?.Unload();
            TryDeleteFile(tempOutput);
            TryDeleteFile(Path.ChangeExtension(tempOutput, ".pdb"));
        }
    }

    [Fact]
    public void HarmonyTranspiler_CanTargetPatchedGenTerraGenerate()
    {
        if (!TryLocateRuntimeDonor(out string root, out string vanillaPath, out string compiledPath))
        {
            return;
        }

        string? harmonyDllPath = FindHarmonyDll(root);
        if (harmonyDllPath == null)
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_genterra_harmony_{Guid.NewGuid():N}.dll");
        AssemblyLoadContext? alc = null;
        System.Func<AssemblyLoadContext, AssemblyName, Assembly?>? defaultResolver = null;
        object? harmonyInstance = null;
        MethodInfo? unpatchAll = null;

        try
        {
            Assert.True(ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            string binDir = Path.Combine(root, "bin/Release/net10.0");
            string harmonyLibDir = Path.GetDirectoryName(harmonyDllPath)!;

            defaultResolver = (ctx, name) =>
            {
                if (name.Name == null)
                {
                    return null;
                }
                string candidate = Path.Combine(harmonyLibDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            AssemblyLoadContext.Default.Resolving += defaultResolver;

            Assembly harmonyAsm = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(harmonyDllPath);

            alc = new AssemblyLoadContext($"GenTerraHarmony741-{Guid.NewGuid():N}", isCollectible: false);
            alc.Resolving += (ctx, name) =>
            {
                if (string.Equals(name.Name, "VSEssentials", StringComparison.OrdinalIgnoreCase))
                {
                    return ctx.LoadFromAssemblyPath(tempOutput);
                }
                if (string.Equals(name.Name, "VintagestoryAPI", StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(ICoreServerAPI).Assembly;
                }
                if (string.Equals(name.Name, "Optimum.Api.Contracts", StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(OptimumConfig).Assembly;
                }
                if (string.Equals(name.Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                {
                    return harmonyAsm;
                }
                if (name.Name != null &&
                    (name.Name.StartsWith("MonoMod.", StringComparison.OrdinalIgnoreCase) ||
                     name.Name.StartsWith("Mono.Cecil", StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                string[] searchDirs =
                {
                    binDir,
                    Path.Combine(root, ".vanilla/win-x64/runtime-donors"),
                    Path.Combine(root, ".vanilla/win-x64/runtime-donors/Lib"),
                    Path.Combine(root, ".vanilla/linux-x64/vintagestory"),
                    Path.Combine(root, ".vanilla/linux-x64/vintagestory/Lib"),
                    AppContext.BaseDirectory,
                };
                foreach (string dir in searchDirs)
                {
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        return ctx.LoadFromAssemblyPath(candidate);
                    }
                }
                return null;
            };
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);

            Type genTerraType = patchedAsm.GetType("Vintagestory.ServerMods.GenTerra", throwOnError: true)!;
            Type harmonyType = harmonyAsm.GetType("HarmonyLib.Harmony", throwOnError: true)!;
            Type harmonyMethodType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod", throwOnError: true)!;
            Type accessToolsType = harmonyAsm.GetType("HarmonyLib.AccessTools", throwOnError: true)!;

            MethodInfo declaredLookup = accessToolsType.GetMethod(
                "DeclaredMethod",
                new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) })!;

            var resolved = (MethodInfo?)declaredLookup.Invoke(null, new object?[] { genTerraType, "generate", null, null });
            Assert.NotNull(resolved);
            Assert.Equal(4, resolved!.GetParameters().Length);

            harmonyInstance = Activator.CreateInstance(harmonyType, "optimum.test.genterra741")!;
            MethodInfo patchMethod = harmonyType.GetMethod(
                "Patch",
                new[] { typeof(MethodBase), harmonyMethodType, harmonyMethodType, harmonyMethodType, harmonyMethodType })!;
            Type codeInstructionType = harmonyAsm.GetType("HarmonyLib.CodeInstruction", throwOnError: true)!;
            MethodInfo identityTranspiler = typeof(TerrainWorldgenRuntimeDeliveryTests)
                .GetMethod(nameof(IdentityTranspiler), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(codeInstructionType);
            object transpilerWrapper = Activator.CreateInstance(harmonyMethodType, identityTranspiler)!;

            patchMethod.Invoke(harmonyInstance, new object?[] { resolved, null, null, transpilerWrapper, null });
            unpatchAll = harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) });

            // Confirm the declaring-type Harmony scan used by ServerSystemSupplyChunks detects the patch on generate().
            MethodInfo getPatchInfo = harmonyType.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null)!;
            object? patchInfo = getPatchInfo.Invoke(null, new object[] { resolved });
            Assert.NotNull(patchInfo);
            var owners = (System.Collections.ICollection)patchInfo!.GetType().GetProperty("Owners")!.GetValue(patchInfo)!;
            Assert.True(owners.Count > 0);
        }
        finally
        {
            if (harmonyInstance != null && unpatchAll != null)
            {
                try { unpatchAll.Invoke(harmonyInstance, new object?[] { "optimum.test.genterra741" }); } catch { }
            }
            if (defaultResolver != null)
            {
                AssemblyLoadContext.Default.Resolving -= defaultResolver;
            }
            if (alc != null && alc.IsCollectible)
            {
                alc.Unload();
            }
            TryDeleteFile(tempOutput);
            TryDeleteFile(Path.ChangeExtension(tempOutput, ".pdb"));
        }
    }

    [Theory]
    [InlineData(128, 8)]
    [InlineData(256, 9)]
    [InlineData(384, 10)]
    [InlineData(512, 11)]
    [InlineData(1536, 19)]
    public void GenTerraVector256BiLerp_MatchesScalarAcrossWorldHeights(int mapSizeY, int expectedOctaves)
    {
        int octaves = TerraGenConfig.GetTerrainOctaveCount(mapSizeY);
        Assert.Equal(expectedOctaves, octaves);

        var rng = new Random(424242 + mapSizeY);
        double[] x0 = new double[octaves];
        double[] x1 = new double[octaves];
        double[] x2 = new double[octaves];
        double[] x3 = new double[octaves];
        for (int i = 0; i < octaves; i++)
        {
            x0[i] = rng.NextDouble() * 200.0 - 100.0;
            x1[i] = rng.NextDouble() * 200.0 - 100.0;
            x2[i] = rng.NextDouble() * 200.0 - 100.0;
            x3[i] = rng.NextDouble() * 200.0 - 100.0;
        }

        double[] expected = new double[octaves];
        double[] actual = new double[octaves];
        const float chunkBlockDelta = 1f / 32f;

        for (int lZ = 0; lZ < 32; lZ += 7)
        {
            for (int lX = 0; lX < 32; lX += 5)
            {
                float lxFrac = lX * chunkBlockDelta;
                float lzFrac = lZ * chunkBlockDelta;

                for (int i = 0; i < octaves; i++)
                {
                    expected[i] = GameMath.BiLerp(x0[i], x1[i], x2[i], x3[i], lxFrac, lzFrac);
                }

                int k = 0;
                if (Vector256.IsHardwareAccelerated)
                {
                    Vector256<double> vx = Vector256.Create((double)lxFrac);
                    Vector256<double> vz = Vector256.Create((double)lzFrac);
                    for (; k <= octaves - 4; k += 4)
                    {
                        Vector256<double> tl = Vector256.LoadUnsafe(ref x0[k]);
                        Vector256<double> tr = Vector256.LoadUnsafe(ref x1[k]);
                        Vector256<double> bl = Vector256.LoadUnsafe(ref x2[k]);
                        Vector256<double> br = Vector256.LoadUnsafe(ref x3[k]);
                        Vector256<double> top = tl + (tr - tl) * vx;
                        Vector256<double> bot = bl + (br - bl) * vx;
                        (top + (bot - top) * vz).StoreUnsafe(ref actual[k]);
                    }
                }
                for (; k < octaves; k++)
                {
                    actual[k] = GameMath.BiLerp(x0[k], x1[k], x2[k], x3[k], lxFrac, lzFrac);
                }

                for (int i = 0; i < octaves; i++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i]), BitConverter.DoubleToInt64Bits(actual[i]));
                }
            }
        }
    }

    [Fact]
    public void GenPartial_ConcurrentColumnsIsolateThreadLocalChunkRand()
    {
        var objectCache = new Dictionary<string, object>
        {
            [GlobalConfig.cacheKey] = new GlobalConfig(),
        };
        var modLoader = HighImpactBugFixRegressionTests.StubProxy<IModLoader>.Create((_, _) => null!);
        var worldManager = HighImpactBugFixRegressionTests.StubProxy<IWorldManagerAPI>.Create((wmMethod, _) => wmMethod.Name switch
        {
            "get_Seed" => 123456,
            "get_MapSizeY" => 256,
            _ => null!,
        });
        var world = HighImpactBugFixRegressionTests.StubProxy<IServerWorldAccessor>.Create((wMethod, _) => wMethod.Name switch
        {
            "get_Config" => new Vintagestory.API.Datastructures.TreeAttribute(),
            _ => null!,
        });
        var serverApi = HighImpactBugFixRegressionTests.StubProxy<ICoreServerAPI>.Create((method, _) => method.Name switch
        {
            "get_WorldManager" => worldManager,
            "get_World" => world,
            "get_ObjectCache" => objectCache,
            "get_ModLoader" => modLoader,
            _ => null!,
        });

        var probe = new ConcurrentGenPartialProbe(serverApi);
        probe.initWorldGen();

        var expectedByChunk = new Dictionary<(int, int), int>();
        for (int cx = 0; cx < 8; cx++)
        {
            for (int cz = 0; cz < 8; cz++)
            {
                var req = CreateColumnRequest(cx, cz);
                probe.InvokeColumn(req);
                expectedByChunk[(cx, cz)] = probe.LastObservedRoll.Value;
            }
        }

        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 4 }, iter =>
        {
            int cx = iter % 8;
            int cz = (iter / 8) % 8;
            var req = CreateColumnRequest(cx, cz);
            probe.InvokeColumn(req);
            int actual = probe.LastObservedRoll.Value;
            int expected = expectedByChunk[(cx, cz)];
            if (actual != expected)
            {
                mismatches.Add($"({cx},{cz}): expected {expected}, got {actual}");
            }
        });

        Assert.Empty(mismatches);
    }

    [Fact]
    public void GenPartial_PreservesLegacyChunkRandSeedingUnlessOptedOut()
    {
        var objectCache = new Dictionary<string, object>
        {
            [GlobalConfig.cacheKey] = new GlobalConfig(),
        };
        var modLoader = HighImpactBugFixRegressionTests.StubProxy<IModLoader>.Create((_, _) => null!);
        var worldManager = HighImpactBugFixRegressionTests.StubProxy<IWorldManagerAPI>.Create((wmMethod, _) => wmMethod.Name switch
        {
            "get_Seed" => 777123,
            "get_MapSizeY" => 256,
            _ => null!,
        });
        var world = HighImpactBugFixRegressionTests.StubProxy<IServerWorldAccessor>.Create((wMethod, _) => wMethod.Name switch
        {
            "get_Config" => new Vintagestory.API.Datastructures.TreeAttribute(),
            _ => null!,
        });
        var serverApi = HighImpactBugFixRegressionTests.StubProxy<ICoreServerAPI>.Create((method, _) => method.Name switch
        {
            "get_WorldManager" => worldManager,
            "get_World" => world,
            "get_ObjectCache" => objectCache,
            "get_ModLoader" => modLoader,
            _ => null!,
        });

        var legacyProbe = new LegacyAndThreadLocalGenPartialProbe(serverApi, threadLocalOnly: false);
        legacyProbe.initWorldGen();
        legacyProbe.InvokeColumn(CreateColumnRequest(11, -7));

        Assert.Equal(legacyProbe.LastThreadLocalRoll, legacyProbe.LastLegacyRoll);

        var threadLocalOnlyProbe = new LegacyAndThreadLocalGenPartialProbe(serverApi, threadLocalOnly: true);
        threadLocalOnlyProbe.initWorldGen();
        int initialLegacyFirstRoll = threadLocalOnlyProbe.PeekLegacyNextInt();
        threadLocalOnlyProbe.InvokeColumn(CreateColumnRequest(11, -7));

        Assert.Equal(initialLegacyFirstRoll, threadLocalOnlyProbe.LastLegacyRoll);
        Assert.Equal(legacyProbe.LastThreadLocalRoll, threadLocalOnlyProbe.LastThreadLocalRoll);
    }

    [Fact]
    public void GenTerra_RuntimePatchedInitWorldGen_DoesNotDereferenceNullNoiseLandforms()
    {
        if (!TryLocateRuntimeDonor(out string root, out string vanillaPath, out string compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_genterra_init_{Guid.NewGuid():N}.dll");
        IsolatedEssentialsContext? alc = null;
        try
        {
            Assert.True(ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            alc = new IsolatedEssentialsContext(root, tempOutput, harmonyDllPath: null, isCollectible: true);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);
            Type noiseLandformsType = patchedAsm.GetType("Vintagestory.ServerMods.NoiseLandforms", throwOnError: true)!;
            FieldInfo landformsField = noiseLandformsType.GetField("landforms", BindingFlags.Public | BindingFlags.Static)!;
            object? previous = landformsField.GetValue(null);
            landformsField.SetValue(null, null);

            try
            {
                Type tempDataType = patchedAsm.GetType("Vintagestory.ServerMods.OptimumGenTerraTempData", throwOnError: true)!;
                MethodInfo createThreadLocal = tempDataType.GetMethod("CreateThreadLocal", BindingFlags.Public | BindingFlags.Static)!;
                ParameterInfo[] parameters = createThreadLocal.GetParameters();
                Assert.Equal(2, parameters.Length);

                using var tls = (IDisposable)createThreadLocal.Invoke(null, new object[] { 9, 256 })!;
                Assert.NotNull(tls);
            }
            finally
            {
                landformsField.SetValue(null, previous);
            }
        }
        finally
        {
            alc?.Unload();
            TryDeleteFile(tempOutput);
            TryDeleteFile(Path.ChangeExtension(tempOutput, ".pdb"));
        }
    }

    [Fact]
    public void GenTerra_SourceAndRuntimePatches_EliminateRedundantTlsAndCornerAllocations()
    {
        string sourcePatch = PatchReader.ReadPatch(
            "patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/1.GenTerra/GenTerra.cs.patch");

        // Issues #743 and #744: no per-column tempDataThreadLocal.Value reads remain in Parallel.For,
        // corners reuse workspace buffers via InterpolateOctaves, layer merges synchronize via lock (workspace)
        // + SIMD MergeLayerFlags, and the block-fill loop uses shift-OR indexing.
        Assert.DoesNotContain("tempDataThreadLocal.Value.LerpedAmplitudes", sourcePatch);
        Assert.DoesNotContain("tempDataThreadLocal.Value.LerpedThresholds", sourcePatch);
        Assert.DoesNotContain("float[] columnLandformIndexedWeights = tempDataThreadLocal.Value.landformWeights", sourcePatch);
        Assert.Contains("workspace.InterpolateOctaves(", sourcePatch);
        Assert.Contains("lock (workspace)", sourcePatch);
        Assert.Contains("GenerationWorkspace.MergeLayerFlags(", sourcePatch);
        Assert.Contains("(lY << 10) | mapIndex", sourcePatch);

        if (!TryLocateRuntimeDonor(out string root, out string vanillaPath, out string compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_genterra_oct_{Guid.NewGuid():N}.dll");
        IsolatedEssentialsContext? alc = null;
        try
        {
            Assert.True(ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            alc = new IsolatedEssentialsContext(root, tempOutput, harmonyDllPath: null, isCollectible: true);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);

            Type wsType = patchedAsm.GetType("Vintagestory.ServerMods.OptimumGenTerraWorkspace", throwOnError: true)!;
            Type variantType = patchedAsm.GetType("Vintagestory.ServerMods.NoObf.LandformVariant", throwOnError: true)!;
            Type variantArrayType = variantType.MakeArrayType();
            FieldInfo octavesField = variantType.GetField("TerrainOctaves")!;
            FieldInfo thresholdsField = variantType.GetField("TerrainOctaveThresholds")!;
            MethodInfo ensureCap = wsType.GetMethod("EnsureCapacity", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo interp = wsType.GetMethod("InterpolateOctaves", BindingFlags.Public | BindingFlags.Instance)!;

            var wsParam = Expression.Parameter(typeof(object), "ws");
            var wParam = Expression.Parameter(typeof(float[]), "w");
            var vParam = Expression.Parameter(typeof(Array), "v");
            var oParam = Expression.Parameter(typeof(int), "o");
            var aParam = Expression.Parameter(typeof(double[]), "a");
            var tParam = Expression.Parameter(typeof(double[]), "t");
            var typedInterp = Expression.Lambda<Action<object, float[], Array, int, double[], double[]>>(
                Expression.Call(
                    Expression.Convert(wsParam, wsType),
                    interp,
                    wParam,
                    Expression.Convert(vParam, variantArrayType),
                    oParam,
                    aParam,
                    tParam),
                wsParam, wParam, vParam, oParam, aParam, tParam).Compile();

            var rng = new Random(744743);
            const int landformCount = 24;
            foreach (int octaves in new[] { 8, 9, 10, 11, 19 })
            {
                Array variants = Array.CreateInstance(variantType, landformCount);
                var expectedVariants = new (double[] amp, double[] th)[landformCount];
                for (int v = 0; v < landformCount; v++)
                {
                    var amp = new double[octaves];
                    var th = new double[octaves];
                    for (int o = 0; o < octaves; o++)
                    {
                        amp[o] = rng.NextDouble() * 2.0 - 1.0;
                        th[o] = rng.NextDouble() * 2.0 - 1.0;
                    }
                    object variant = Activator.CreateInstance(variantType)!;
                    octavesField.SetValue(variant, amp);
                    thresholdsField.SetValue(variant, th);
                    variants.SetValue(variant, v);
                    expectedVariants[v] = (amp, th);
                }

                float[] weights = new float[landformCount];
                weights[1] = 0.45f;
                weights[7] = 0.35f;
                weights[19] = 0.20f;

                double[] expectedAmps = new double[octaves];
                double[] expectedTh = new double[octaves];
                for (int o = 0; o < octaves; o++)
                {
                    double a = 0.0;
                    double t = 0.0;
                    for (int i = 0; i < weights.Length; i++)
                    {
                        float w = weights[i];
                        if (w == 0f) continue;
                        a += expectedVariants[i].amp[o] * w;
                        t += expectedVariants[i].th[o] * w;
                    }
                    expectedAmps[o] = a;
                    expectedTh[o] = t;
                }

                object workspace = Activator.CreateInstance(wsType, 256)!;
                ensureCap.Invoke(workspace, new object[] { octaves, landformCount, 256 });
                double[] actualAmps = (double[])wsType.GetField("OctNoiseX0")!.GetValue(workspace)!;
                double[] actualTh = (double[])wsType.GetField("OctThX0")!.GetValue(workspace)!;

                typedInterp(workspace, weights, variants, octaves, actualAmps, actualTh);
                for (int o = 0; o < octaves; o++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expectedAmps[o]), BitConverter.DoubleToInt64Bits(actualAmps[o]));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expectedTh[o]), BitConverter.DoubleToInt64Bits(actualTh[o]));
                }

                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int iter = 0; iter < 128; iter++)
                {
                    typedInterp(workspace, weights, variants, octaves, actualAmps, actualTh);
                }
                Assert.Equal(allocBefore, GC.GetAllocatedBytesForCurrentThread());
            }
        }
        finally
        {
            alc?.Unload();
            TryDeleteFile(tempOutput);
            TryDeleteFile(Path.ChangeExtension(tempOutput, ".pdb"));
        }
    }

    [Fact]
    public void GenTerra_EnsureCapacity_HandlesDynamicWorldHeightAndLandformExpansion()
    {
        if (!TryLocateRuntimeDonor(out string root, out string vanillaPath, out string compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_genterra_cap_{Guid.NewGuid():N}.dll");
        IsolatedEssentialsContext? alc = null;
        try
        {
            Assert.True(ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            alc = new IsolatedEssentialsContext(root, tempOutput, harmonyDllPath: null, isCollectible: true);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);

            Type tdType = patchedAsm.GetType("Vintagestory.ServerMods.OptimumGenTerraTempData", throwOnError: true)!;
            Type wsType = patchedAsm.GetType("Vintagestory.ServerMods.OptimumGenTerraWorkspace", throwOnError: true)!;

            object td = Activator.CreateInstance(tdType, 8, 10, 128)!;
            MethodInfo tdEnsure = tdType.GetMethod("EnsureCapacity", BindingFlags.Public | BindingFlags.Instance)!;
            tdEnsure.Invoke(td, new object[] { 19, 32, 1536 });
            Assert.Equal(19, ((double[])tdType.GetField("LerpedAmplitudes")!.GetValue(td)!).Length);
            Assert.Equal(19, ((double[])tdType.GetField("LerpedThresholds")!.GetValue(td)!).Length);
            Assert.Equal(32, ((float[])tdType.GetField("LandformWeights")!.GetValue(td)!).Length);
            Assert.Equal(32, ((float[])tdType.GetField("CompactWeights")!.GetValue(td)!).Length);
            Assert.Equal(1536, ((bool[])tdType.GetField("LocalLayerFullySolid")!.GetValue(td)!).Length);
            Assert.Equal(1536, ((bool[])tdType.GetField("LocalLayerFullyEmpty")!.GetValue(td)!).Length);

            tdEnsure.Invoke(td, new object[] { 9, 24, 256 });
            Assert.Equal(9, ((double[])tdType.GetField("LerpedAmplitudes")!.GetValue(td)!).Length);
            Assert.Equal(256, ((bool[])tdType.GetField("LocalLayerFullySolid")!.GetValue(td)!).Length);

            object ws = Activator.CreateInstance(wsType, 128)!;
            MethodInfo wsEnsure = wsType.GetMethod("EnsureCapacity", BindingFlags.Public | BindingFlags.Instance)!;
            wsEnsure.Invoke(ws, new object[] { 19, 32, 1536 });
            Assert.Equal(19, ((double[])wsType.GetField("OctNoiseX0")!.GetValue(ws)!).Length);
            Assert.Equal(19, ((double[])wsType.GetField("OctThX3")!.GetValue(ws)!).Length);
            Assert.Equal(32, ((float[])wsType.GetField("CornerLandformWeights")!.GetValue(ws)!).Length);
            Assert.Equal(32, ((int[])wsType.GetField("CornerNonZeroIndices")!.GetValue(ws)!).Length);
            Assert.Equal(1536, ((bool[])wsType.GetField("LayerFullySolid")!.GetValue(ws)!).Length);
            Assert.Equal(24, ((ulong[][])wsType.GetField("ColumnBlockSolidities")!.GetValue(ws)!)[0].Length);
        }
        finally
        {
            alc?.Unload();
            TryDeleteFile(tempOutput);
            TryDeleteFile(Path.ChangeExtension(tempOutput, ".pdb"));
        }
    }

    [Fact]
    public void GenTerra_SimdMergeLayerFlags_MatchesScalarAndIsThreadSafeAcrossHeights()
    {
        if (!TryLocateRuntimeDonor(out string root, out string vanillaPath, out string compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_genterra_merge_{Guid.NewGuid():N}.dll");
        IsolatedEssentialsContext? alc = null;
        try
        {
            Assert.True(ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            alc = new IsolatedEssentialsContext(root, tempOutput, harmonyDllPath: null, isCollectible: true);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);

            Type wsType = patchedAsm.GetType("Vintagestory.ServerMods.OptimumGenTerraWorkspace", throwOnError: true)!;
            MethodInfo mergeMethod = wsType.GetMethod("MergeLayerFlags", BindingFlags.Public | BindingFlags.Static)!;
            var merge = (Action<bool[], bool[], bool[], bool[]>)Delegate.CreateDelegate(typeof(Action<bool[], bool[], bool[], bool[]>), mergeMethod);

            var rng = new Random(9911);
            foreach (int len in new[] { 0, 15, 16, 31, 32, 128, 256, 260, 384, 512, 1536 })
            {
                bool[] solid = new bool[len];
                bool[] empty = new bool[len];
                bool[] refSolid = new bool[len];
                bool[] refEmpty = new bool[len];
                bool[] localSolid = new bool[len];
                bool[] localEmpty = new bool[len];

                for (int i = 0; i < len; i++)
                {
                    solid[i] = refSolid[i] = rng.Next(2) == 1;
                    empty[i] = refEmpty[i] = rng.Next(2) == 1;
                    localSolid[i] = rng.Next(2) == 1;
                    localEmpty[i] = rng.Next(2) == 1;
                    if (!localSolid[i]) refSolid[i] = false;
                    if (!localEmpty[i]) refEmpty[i] = false;
                }

                merge(solid, empty, localSolid, localEmpty);
                Assert.Equal(refSolid, solid);
                Assert.Equal(refEmpty, empty);
            }

            // Concurrent thread-safety check with lock (workspace).
            object gate = new object();
            bool[] sharedSolid = new bool[384];
            bool[] sharedEmpty = new bool[384];
            Array.Fill(sharedSolid, true);
            Array.Fill(sharedEmpty, true);
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 4 }, idx =>
            {
                bool[] ls = new bool[384];
                bool[] le = new bool[384];
                Array.Fill(ls, true);
                Array.Fill(le, true);
                ls[(idx * 5) % 384] = false;
                le[(idx * 7) % 384] = false;
                lock (gate)
                {
                    merge(sharedSolid, sharedEmpty, ls, le);
                }
            });
            for (int idx = 0; idx < 64; idx++)
            {
                Assert.False(sharedSolid[(idx * 5) % 384]);
                Assert.False(sharedEmpty[(idx * 7) % 384]);
            }
        }
        finally
        {
            alc?.Unload();
            TryDeleteFile(tempOutput);
            TryDeleteFile(Path.ChangeExtension(tempOutput, ".pdb"));
        }
    }

    [Fact]
    public void GenTerra_BitShiftChunkIndex3d_MatchesVanillaForAll32x32x32Coords()
    {
        for (int posY = 0; posY < 1536; posY++)
        {
            int yBaseChunk = posY / 32;
            int lY = posY & 31;
            Assert.Equal(yBaseChunk, posY >> 5);
            Assert.Equal(posY % 32, lY);
            if (posY == 0 || posY == 31 || posY == 255 || posY == 1023 || posY == 1535)
            {
                for (int lZ = 0; lZ < 32; lZ++)
                {
                    int mapIndex = lZ * 32;
                    for (int lX = 0; lX < 32; lX++, mapIndex++)
                    {
                        int expected = (lY * 32 + lZ) * 32 + lX;
                        int actual = (lY << 10) | mapIndex;
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }
    }

    private sealed class LegacyAndThreadLocalGenPartialProbe : GenPartial
    {
        private readonly bool threadLocalOnly;

        public int LastLegacyRoll { get; private set; }
        public int LastThreadLocalRoll { get; private set; }

        public LegacyAndThreadLocalGenPartialProbe(ICoreServerAPI serverApi, bool threadLocalOnly)
        {
            api = serverApi;
            gcfg = new GlobalConfig();
            this.threadLocalOnly = threadLocalOnly;
        }

        protected override int chunkRange => 0;

        public override void initWorldGen()
        {
            base.initWorldGen();
            optimumUseThreadLocalChunkRandOnly = threadLocalOnly;
        }

        public int PeekLegacyNextInt()
        {
            var copy = new LCGRandom(api.WorldManager.Seed);
            return copy.NextInt(10_000_000);
        }

        public void InvokeColumn(IChunkColumnGenerateRequest request) => GenChunkColumn(request);

        public override void GeneratePartial(IServerChunk[] chunks, int chunkX, int chunkZ, int basePosX, int basePosZ)
        {
            LastLegacyRoll = chunkRand.NextInt(10_000_000);
            LastThreadLocalRoll = chunkRandThreadLocal.Value!.NextInt(10_000_000);
        }
    }

    private static IChunkColumnGenerateRequest CreateColumnRequest(int cx, int cz)
    {
        IServerChunk[] chunks = Array.Empty<IServerChunk>();
        return HighImpactBugFixRegressionTests.StubProxy<IChunkColumnGenerateRequest>.Create((m, _) => m.Name switch
        {
            "get_Chunks" => chunks,
            "get_ChunkX" => cx,
            "get_ChunkZ" => cz,
            _ => null!,
        });
    }

    private sealed class ConcurrentGenPartialProbe : GenPartial
    {
        public readonly ThreadLocal<int> LastObservedRoll = new();

        public ConcurrentGenPartialProbe(ICoreServerAPI serverApi)
        {
            api = serverApi;
            gcfg = new GlobalConfig();
        }

        protected override int chunkRange => 1;

        public void InvokeColumn(IChunkColumnGenerateRequest request) => GenChunkColumn(request);

        public override void GeneratePartial(IServerChunk[] chunks, int chunkX, int chunkZ, int basePosX, int basePosZ)
        {
            LCGRandom rng = chunkRandThreadLocal.Value!;
            int roll = rng.NextInt(10_000_000) ^ (rng.NextInt(10_000_000) << 1);
            if (basePosX == 1 && basePosZ == 1)
            {
                LastObservedRoll.Value = roll;
            }
        }
    }

    private static IEnumerable<T> IdentityTranspiler<T>(IEnumerable<T> instructions) => instructions;

    private static bool TryLocateRuntimeDonor(out string root, out string vanillaPath, out string compiledPath)
    {
        root = Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!;
        vanillaPath = Path.Combine(root, ".vanilla/win-x64/runtime-donors/Mods/VSEssentials.dll");
        compiledPath = Path.Combine(root, ".build/runtime-donors/VSEssentials/bin/Release/net10.0/VSEssentials.dll");
        return File.Exists(vanillaPath) && File.Exists(compiledPath);
    }

    private static string? FindHarmonyDll(string root)
    {
        return new[]
        {
            Path.Combine(root, ".vanilla/linux-x64/vintagestory/Lib/0Harmony.dll"),
            Path.Combine(root, ".vanilla/win-x64/vintagestory/Lib/0Harmony.dll"),
        }.FirstOrDefault(File.Exists);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class IsolatedEssentialsContext : AssemblyLoadContext
    {
        private readonly string root;
        private readonly string patchedEssentialsPath;
        private readonly string? harmonyDllPath;

        public IsolatedEssentialsContext(string root, string patchedEssentialsPath, string? harmonyDllPath, bool isCollectible)
            : base(isCollectible: isCollectible)
        {
            this.root = root;
            this.patchedEssentialsPath = patchedEssentialsPath;
            this.harmonyDllPath = harmonyDllPath;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "VSEssentials", StringComparison.OrdinalIgnoreCase))
            {
                return LoadFromAssemblyPath(patchedEssentialsPath);
            }
            if (string.Equals(assemblyName.Name, "0Harmony", StringComparison.OrdinalIgnoreCase) &&
                harmonyDllPath != null &&
                File.Exists(harmonyDllPath))
            {
                return LoadFromAssemblyPath(harmonyDllPath);
            }

            // Let assemblies already available in the default context (VintagestoryAPI,
            // Optimum.Api.Contracts, Newtonsoft.Json, etc.) resolve from Default so
            // type identities match VintagestoryAPI signatures cleanly.
            foreach (Assembly loaded in Default.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            string inAppBase = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
            if (File.Exists(inAppBase))
            {
                return null;
            }

            string binCandidate = Path.Combine(root, "bin/Release/net10.0", assemblyName.Name + ".dll");
            if (File.Exists(binCandidate))
            {
                return LoadFromAssemblyPath(binCandidate);
            }

            string[] probeDirs =
            {
                Path.Combine(root, ".vanilla/win-x64/runtime-donors"),
                Path.Combine(root, ".vanilla/win-x64/runtime-donors/Lib"),
                Path.Combine(root, ".vanilla/linux-x64/vintagestory"),
                Path.Combine(root, ".vanilla/linux-x64/vintagestory/Lib"),
            };
            foreach (string dir in probeDirs)
            {
                string candidate = Path.Combine(dir, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(candidate);
                }
            }

            return null;
        }
    }
}
