#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class KometChunkTesselatorCompatTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void ChunkTesselatorPatchAndPatcher_KeepVanillaTwoArgPopulateUniqueAndDistinctChiselHelper()
    {
        string root = RepoRoot();
        string patch = File.ReadAllText(Path.Combine(
            root, "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch"));
        string program = File.ReadAllText(Path.Combine(
            root, "Optimum.Patcher/Program.cs"));

        Assert.Contains("private int populateOptimumChiselTesselatedChunkPart(MeshData[][][] modeldataByRenderPassByLodLevel, out TesselatedChunkPart[] tessChunkParts)", patch);
        Assert.Contains("populateOptimumChiselTesselatedChunkPart(centerOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] centerOptimumChiselParts);", patch);
        Assert.Contains("populateOptimumChiselTesselatedChunkPart(edgeOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] edgeOptimumChiselParts);", patch);
        Assert.DoesNotContain("bool optimumUseChiselLodDistance = false", patch);

        Assert.Contains("new(\"Vintagestory.Client.NoObf.ChunkTesselator\", \"populateTesselatedChunkPart\", 2)", program);
        Assert.Contains("\"populateOptimumChiselTesselatedChunkPart\"", program);

        // Must not inject a second populateTesselatedChunkPart overload via membersToInject.
        int chunkMembersStart = program.IndexOf("[\"Vintagestory.Client.NoObf.ChunkTesselator\"]", StringComparison.Ordinal);
        Assert.True(chunkMembersStart >= 0);
        int chunkMembersEnd = program.IndexOf("},", chunkMembersStart, StringComparison.Ordinal);
        Assert.True(chunkMembersEnd > chunkMembersStart);
        string chunkMembersBlock = program.Substring(chunkMembersStart, chunkMembersEnd - chunkMembersStart);
        Assert.DoesNotContain("\"populateTesselatedChunkPart\"", chunkMembersBlock);
        Assert.Contains("\"populateOptimumChiselTesselatedChunkPart\"", chunkMembersBlock);
    }

#if !NO_DONOR
    private static int s_harmonyPostfixCalls;
    private static int s_harmonyPartsReceived;
    private static readonly List<object> s_harmonyReceivedParts = new();

    private static void RecordingPostfix(Array? tessChunkParts)
    {
        s_harmonyPostfixCalls++;
        if (tessChunkParts == null) return;
        foreach (object? part in tessChunkParts)
        {
            if (part != null)
            {
                s_harmonyPartsReceived++;
                s_harmonyReceivedParts.Add(part);
            }
        }
    }

    [Fact]
    public void ShippedPatchedDll_HasSingleVanillaPopulateMethodAndRoutesRegularChunksThroughIt()
    {
        string root = RepoRoot();
        string? compiledLibPath = new[] { "Release", "Debug" }
            .Select(cfg => Path.Combine(root, "build/VintagestoryLib/bin", cfg, "net10.0/VintagestoryLib.dll"))
            .FirstOrDefault(File.Exists);
        Assert.True(compiledLibPath != null && File.Exists(compiledLibPath), "Missing compiled VintagestoryLib.dll");

        string? vanillaLibPath = new[]
        {
            Path.Combine(root, ".vanilla/linux-x64/vintagestory/VintagestoryLib.vanilla.dll"),
            Path.Combine(root, ".vanilla/win-x64/vintagestory/VintagestoryLib.vanilla.dll"),
            Path.Combine(root, ".vanilla/osx-x64/vintagestory/VintagestoryLib.vanilla.dll"),
            Path.Combine(root, ".vanilla/linux-x64/vintagestory/VintagestoryLib.dll"),
            Path.Combine(root, ".vanilla/win-x64/vintagestory/VintagestoryLib.dll"),
        }.FirstOrDefault(File.Exists);
        if (vanillaLibPath == null)
        {
            return;
        }

        string tempPatchedPath = Path.Combine(Path.GetTempPath(), $"optimum-komet-673-{Guid.NewGuid():N}.dll");
        try
        {
            int exitCode = Optimum.Patcher.Program.Run(new[] { vanillaLibPath, compiledLibPath!, tempPatchedPath });
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(tempPatchedPath));

            // 1. Static Cecil verification on the exact shipped VintagestoryLib-patched.dll
            using (var module = ModuleDefinition.ReadModule(tempPatchedPath))
            {
                TypeDefinition tct = module.Types.First(t => t.FullName == "Vintagestory.Client.NoObf.ChunkTesselator");

                List<MethodDefinition> populateMethods = tct.Methods
                    .Where(m => m.Name == "populateTesselatedChunkPart")
                    .ToList();
                MethodDefinition populate = Assert.Single(populateMethods);
                Assert.True(populate.HasBody);
                Assert.Equal(2, populate.Parameters.Count);
                Assert.Equal("tessChunkParts", populate.Parameters[1].Name);
                Assert.True(populate.Parameters[1].IsOut);

                List<MethodDefinition> chiselHelpers = tct.Methods
                    .Where(m => m.Name == "populateOptimumChiselTesselatedChunkPart")
                    .ToList();
                MethodDefinition chiselHelper = Assert.Single(chiselHelpers);
                Assert.True(chiselHelper.HasBody);
                Assert.Equal(2, chiselHelper.Parameters.Count);

                // Verify NowProcessChunk routes regular pools through 2-arg populateTesselatedChunkPart
                // and chisel pools through populateOptimumChiselTesselatedChunkPart.
                MethodDefinition nowProcess = tct.Methods.First(m => m.Name == "NowProcessChunk" && m.Parameters.Count == 5);
                var regularCalls = nowProcess.Body.Instructions
                    .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference mr
                        && mr.Name == "populateTesselatedChunkPart")
                    .Select(i => (MethodReference)i.Operand)
                    .ToList();
                Assert.Equal(2, regularCalls.Count);
                Assert.All(regularCalls, mr => Assert.Equal(2, mr.Parameters.Count));

                var chiselCalls = nowProcess.Body.Instructions
                    .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference mr
                        && mr.Name == "populateOptimumChiselTesselatedChunkPart")
                    .Select(i => (MethodReference)i.Operand)
                    .ToList();
                Assert.Equal(2, chiselCalls.Count);
                Assert.All(chiselCalls, mr => Assert.Equal(2, mr.Parameters.Count));

                // Verify populateTesselatedChunkPart itself preserves pooling + face bucketing hooks.
                Assert.Contains(populate.Body.Instructions, i =>
                    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                    && i.Operand is MethodReference mr
                    && mr.Name == "OptimumCloneChunkMesh");
                Assert.Contains(populate.Body.Instructions, i =>
                    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                    && i.Operand is MethodReference mr
                    && mr.Name == nameof(OptimumChunkFaceBuckets.BucketAndAttach));
            }

            // 2. Runtime name-only reflection lookup + live execution + Harmony postfix delivery
            string? vanillaLibDir = Path.GetDirectoryName(vanillaLibPath);
            string? harmonyDllPath = vanillaLibDir != null
                ? Path.Combine(vanillaLibDir, "Lib/0Harmony.dll")
                : null;
            if (harmonyDllPath == null || !File.Exists(harmonyDllPath))
            {
                harmonyDllPath = new[]
                {
                    Path.Combine(root, ".vanilla/linux-x64/vintagestory/Lib/0Harmony.dll"),
                    Path.Combine(root, ".vanilla/win-x64/vintagestory/Lib/0Harmony.dll"),
                }.FirstOrDefault(File.Exists);
            }

            string binDir = Path.Combine(root, "bin/Release/net10.0");
            var alc = new AssemblyLoadContext("KometCompat673Test", isCollectible: true);
            alc.Resolving += (_, name) =>
            {
                if (name.Name == "VintagestoryAPI") return typeof(MeshData).Assembly;
                if (name.Name == "Optimum.Api.Contracts") return typeof(OptimumConfig).Assembly;
                if (name.Name == "0Harmony" && harmonyDllPath != null && File.Exists(harmonyDllPath))
                    return alc.LoadFromAssemblyPath(harmonyDllPath);
                string inBin = Path.Combine(binDir, name.Name + ".dll");
                if (File.Exists(inBin)) return alc.LoadFromAssemblyPath(inBin);
                if (vanillaLibDir != null)
                {
                    string inVanilla = Path.Combine(vanillaLibDir, name.Name + ".dll");
                    if (File.Exists(inVanilla)) return alc.LoadFromAssemblyPath(inVanilla);
                    string inVanillaLib = Path.Combine(vanillaLibDir, "Lib", name.Name + ".dll");
                    if (File.Exists(inVanillaLib)) return alc.LoadFromAssemblyPath(inVanillaLib);
                }
                string inBase = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                if (File.Exists(inBase)) return alc.LoadFromAssemblyPath(inBase);
                return null;
            };

            try
            {
                Assembly patchedAsm = alc.LoadFromAssemblyPath(tempPatchedPath);
                Type tctType = patchedAsm.GetType("Vintagestory.Client.NoObf.ChunkTesselator", throwOnError: true)!;
                Type partType = patchedAsm.GetType("Vintagestory.Client.NoObf.TesselatedChunkPart", throwOnError: true)!;
                FieldInfo chiselFlagField = partType.GetField("optimumUseChiselLodDistance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

                const BindingFlags declaredAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                // Exact name-only lookup that Komet's FaceSorting.Install executes via AccessTools.DeclaredMethod:
                MethodInfo? populateMethod = tctType.GetMethod("populateTesselatedChunkPart", declaredAll);
                Assert.NotNull(populateMethod);
                ParameterInfo[] parameters = populateMethod!.GetParameters();
                Assert.Equal(2, parameters.Length);
                Assert.Equal("tessChunkParts", parameters[1].Name);

                MethodInfo? chiselHelperMethod = tctType.GetMethod("populateOptimumChiselTesselatedChunkPart", declaredAll);
                Assert.NotNull(chiselHelperMethod);

                // Attach Harmony postfix matching Komet's FaceSorting.Populated when 0Harmony.dll is available
                bool harmonyPatched = false;
                object? harmonyInstance = null;
                MethodInfo? unpatchAll = null;
                s_harmonyPostfixCalls = 0;
                s_harmonyPartsReceived = 0;
                s_harmonyReceivedParts.Clear();

                if (harmonyDllPath != null && File.Exists(harmonyDllPath))
                {
                    Assembly harmonyAsm = alc.LoadFromAssemblyPath(harmonyDllPath);
                    Type harmonyType = harmonyAsm.GetType("HarmonyLib.Harmony", throwOnError: true)!;
                    Type harmonyMethodType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod", throwOnError: true)!;
                    Type accessToolsType = harmonyAsm.GetType("HarmonyLib.AccessTools", throwOnError: true)!;

                    // Verify Harmony's AccessTools.DeclaredMethod(typeof(ChunkTesselator), "populateTesselatedChunkPart") directly
                    MethodInfo declaredMethodLookup = accessToolsType.GetMethod(
                        "DeclaredMethod",
                        new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) })!;
                    object? resolved = declaredMethodLookup.Invoke(null, new object?[] { tctType, "populateTesselatedChunkPart", null, null });
                    Assert.Equal(populateMethod, resolved);

                    harmonyInstance = Activator.CreateInstance(harmonyType, "optimum.test.komet673")!;
                    MethodInfo patchMethod = harmonyType.GetMethod(
                        "Patch",
                        new[] { typeof(MethodBase), harmonyMethodType, harmonyMethodType, harmonyMethodType, harmonyMethodType })!;
                    object postfixWrapper = Activator.CreateInstance(
                        harmonyMethodType,
                        typeof(KometChunkTesselatorCompatTests).GetMethod(nameof(RecordingPostfix), BindingFlags.Static | BindingFlags.NonPublic)!)!;
                    patchMethod.Invoke(harmonyInstance, new object?[] { populateMethod, null, postfixWrapper, null, null });
                    unpatchAll = harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) });
                    harmonyPatched = true;
                }

                try
                {
                    object tct = RuntimeHelpers.GetUninitializedObject(tctType);
                    EnumChunkRenderPass[] passes = (EnumChunkRenderPass[])Enum.GetValues(typeof(EnumChunkRenderPass));
                    tctType.GetField("passes", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(tct, passes);
                    tctType.GetField("quantityAtlasses", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(tct, 1);
                    tctType.GetField("ret", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(tct, Array.CreateInstance(partType, passes.Length));
                    tctType.GetField("emptyParts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(tct, Array.CreateInstance(partType, 0));

                    MeshData[][][] regularPool = CreatePool(passes.Length, withQuadOnOpaqueLod1: true);
                    MeshData[][][] emptyChiselPool = CreatePool(passes.Length, withQuadOnOpaqueLod1: false);
                    MeshData[][][] populatedChiselPool = CreatePool(passes.Length, withQuadOnOpaqueLod1: true);

                    // A. Regular chunk pool through vanilla 2-arg populateTesselatedChunkPart
                    object?[] regArgs = { regularPool, null };
                    int regVerts = (int)populateMethod.Invoke(tct, regArgs)!;
                    Array regParts = Assert.IsAssignableFrom<Array>(regArgs[1]);
                    Assert.Equal(4, regVerts);
                    Assert.Equal(1, regParts.Length);
                    Assert.False((bool)chiselFlagField.GetValue(regParts.GetValue(0)!)!);
                    if (harmonyPatched)
                    {
                        Assert.Equal(1, s_harmonyPostfixCalls);
                        Assert.Equal(1, s_harmonyPartsReceived);
                    }

                    // B. Empty chisel pool skips populateTesselatedChunkPart completely
                    object?[] emptyArgs = { emptyChiselPool, null };
                    int emptyVerts = (int)chiselHelperMethod!.Invoke(tct, emptyArgs)!;
                    Array emptyPartsOut = Assert.IsAssignableFrom<Array>(emptyArgs[1]);
                    Assert.Equal(0, emptyVerts);
                    Assert.Empty(emptyPartsOut);
                    if (harmonyPatched)
                    {
                        Assert.Equal(1, s_harmonyPostfixCalls);
                    }

                    // C. Populated chisel pool sets optimumUseChiselLodDistance = true and emits valid geometry
                    object?[] chiselArgs = { populatedChiselPool, null };
                    int chiselVerts = (int)chiselHelperMethod.Invoke(tct, chiselArgs)!;
                    Array chiselPartsOut = Assert.IsAssignableFrom<Array>(chiselArgs[1]);
                    Assert.Equal(4, chiselVerts);
                    Assert.Equal(1, chiselPartsOut.Length);
                    Assert.True((bool)chiselFlagField.GetValue(chiselPartsOut.GetValue(0)!)!);
                    if (harmonyPatched)
                    {
                        Assert.Equal(2, s_harmonyPostfixCalls);
                        Assert.Equal(2, s_harmonyPartsReceived);
                    }
                }
                finally
                {
                    if (harmonyPatched && harmonyInstance != null && unpatchAll != null)
                    {
                        unpatchAll.Invoke(harmonyInstance, new object?[] { "optimum.test.komet673" });
                    }
                }
            }
            finally
            {
                alc.Unload();
            }
        }
        finally
        {
            if (File.Exists(tempPatchedPath))
            {
                try { File.Delete(tempPatchedPath); } catch { }
            }
        }
    }

    private static MeshData[][][] CreatePool(int passCount, bool withQuadOnOpaqueLod1)
    {
        var pool = new MeshData[4][][];
        for (int lod = 0; lod < 4; lod++)
        {
            pool[lod] = new MeshData[passCount][];
            for (int p = 0; p < passCount; p++)
            {
                var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);
                if (withQuadOnOpaqueLod1 && lod == 1 && p == (int)EnumChunkRenderPass.Opaque)
                {
                    mesh.AddVertex(0f, 0f, 0f, 0f, 0f, -1);
                    mesh.AddVertex(1f, 0f, 0f, 1f, 0f, -1);
                    mesh.AddVertex(1f, 1f, 0f, 1f, 1f, -1);
                    mesh.AddVertex(0f, 1f, 0f, 0f, 1f, -1);
                    mesh.AddIndex(0);
                    mesh.AddIndex(1);
                    mesh.AddIndex(2);
                    mesh.AddIndex(0);
                    mesh.AddIndex(2);
                    mesh.AddIndex(3);
                }
                pool[lod][p] = new[] { mesh };
            }
        }
        return pool;
    }
#endif
}
