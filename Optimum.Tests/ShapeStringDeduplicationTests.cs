#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class ShapeStringDeduplicationTests
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
    public void ShapeStringDeduplication_PatchesAndRegistrationsExist()
    {
        string shapePatch = PatchReader.ReadPatch("patches/VintagestoryApi/Common/Model/Shape/Shape.cs.patch");
        string elemPatch = PatchReader.ReadPatch("patches/VintagestoryApi/Common/Model/Shape/ShapeElement.cs.patch");
        string apPatch = PatchReader.ReadPatch("patches/VintagestoryApi/Common/Model/Shape/AttachmentPoint.cs.patch");
        string stmPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShapeTesselatorManager.cs.patch");
        string apiPatcherSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
        string programSource = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("OptimumApiBridge.DeduplicateShapeAnimations(this);", shapePatch);
        Assert.Contains("OptimumApiBridge.DeduplicateShapeStrings(this);", shapePatch);
        Assert.Contains("OptimumApiBridge.ConcatAndDeduplicateShapeString(texturePrefixCode,", shapePatch);
        Assert.Contains("OptimumApiBridge.DeduplicateShapeString(", elemPatch);
        Assert.Contains("OptimumApiBridge.DeduplicateShapeString(Code);", apPatch);
        Assert.Contains("OptimumApiBridge.ClearShapeStringPool();", stmPatch);

        Assert.Contains("PatchShapeStringDeduplication(", apiPatcherSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"PrepareToLoadShapes\", 0)", programSource);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ShapeTesselatorManager\", \"Dispose\", 0)", programSource);
    }

    [Fact]
    public void OptimumShapeStringPool_DeduplicatesConcatenatesConcurrentlyAndClears()
    {
        var pool = new OptimumShapeStringPool();
        string suffix = Guid.NewGuid().ToString("N");
        string a = string.Concat("Bone_", suffix);
        string b = string.Concat("Bone_", suffix);
        Assert.False(object.ReferenceEquals(a, b));
        Assert.Null(string.IsInterned(a));

        string? canonA = pool.Deduplicate(a);
        string? canonB = pool.Deduplicate(b);
        Assert.Same(canonA, canonB);
        Assert.Null(string.IsInterned(canonA!));

        // Step-parenting concatenation cache hit allocates 0 managed bytes.
        string prefix = "outfit-";
        string prefixed1 = pool.ConcatAndDeduplicate(prefix, b);
        Assert.Equal(string.Concat(prefix, b), prefixed1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string prefixed2 = pool.ConcatAndDeduplicate(prefix, a);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(prefixed1, prefixed2);
        Assert.Equal(0, allocated);

        // Handles null, empty, and >256-char concatenations safely matching string.Concat semantics.
        Assert.Null(pool.Deduplicate(null));
        Assert.Same(string.Empty, pool.Deduplicate(new string(Array.Empty<char>())));
        Assert.Same(string.Empty, pool.ConcatAndDeduplicate(null, null));
        Assert.Same(canonA, pool.ConcatAndDeduplicate(null, b));
        string longTail = new string('x', 300) + suffix;
        Assert.Same(
            pool.ConcatAndDeduplicate("long-", longTail),
            pool.ConcatAndDeduplicate("long-", new string(longTail.ToCharArray())));

        // Concurrent worker deduplication stays consistent across threads.
        Parallel.For(0, 8, worker =>
        {
            for (int i = 0; i < 200; i++)
            {
                string dyn = $"WorkerBone_{i % 25}_{suffix}";
                string? res = pool.Deduplicate(dyn);
                Assert.Equal(dyn, res);
            }
        });

        Assert.True(pool.GetStats().EntryCount > 0);
        pool.Clear();
        Assert.Equal((0, 0), pool.GetStats());

        string afterClear = string.Concat("Bone_", suffix);
        string? canonAfterClear = pool.Deduplicate(afterClear);
        Assert.False(object.ReferenceEquals(canonA, canonAfterClear));
    }

    private sealed class IsolatedPatchedAlc : AssemblyLoadContext
    {
        private readonly string[] searchDirs;
        private readonly Dictionary<string, Assembly> preloaded = new(StringComparer.OrdinalIgnoreCase);

        public IsolatedPatchedAlc(string name, params string[] searchDirs) : base(name, isCollectible: true)
        {
            this.searchDirs = searchDirs;
        }

        public Assembly LoadEntryAssembly(string path, string logicalName)
        {
            Assembly asm = LoadFromAssemblyPath(path);
            preloaded[logicalName] = asm;
            return asm;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name != null && preloaded.TryGetValue(name.Name, out Assembly? pinned))
            {
                return pinned;
            }

            if (name.Name != null && !name.Name.StartsWith("System.", StringComparison.Ordinal) &&
                name.Name != "netstandard" && name.Name != "mscorlib")
            {
                foreach (string dir in searchDirs)
                {
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        return LoadFromAssemblyPath(candidate);
                    }
                }
            }

            return null;
        }
    }

    [Fact]
    public void PatchedApi_ShapeLoadResolveAndStepParenting_DeduplicateWithoutClrInterning()
    {
        string repoRoot = RepoRoot();
        string cfgDir = File.Exists(Path.Combine(repoRoot, "bin", "Release", "net10.0", "VintagestoryAPI-patched.dll"))
            ? "Release"
            : "Debug";
        string patchedApiPath = Path.Combine(repoRoot, "bin", cfgDir, "net10.0", "VintagestoryAPI-patched.dll");
        if (!File.Exists(patchedApiPath))
        {
            return;
        }

        string outDir = Path.GetDirectoryName(patchedApiPath)!;
        string vanillaDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory");
        string vanillaLibDir = Path.Combine(vanillaDir, "Lib");
        var alc = new IsolatedPatchedAlc("ShapeDedupApiTest", outDir, vanillaLibDir, vanillaDir, AppContext.BaseDirectory);

        try
        {
            Assembly apiAsm = alc.LoadEntryAssembly(patchedApiPath, "VintagestoryAPI");
            Assembly contractsAsm = alc.LoadFromAssemblyName(new AssemblyName("Optimum.Api.Contracts"));

            Type bridgeType = contractsAsm.GetType("Vintagestory.API.Config.OptimumApiBridge", throwOnError: true)!;
            MethodInfo clearPool = bridgeType.GetMethod("ClearShapeStringPool", BindingFlags.Public | BindingFlags.Static)!;
            clearPool.Invoke(null, null);

            Type shapeType = apiAsm.GetType("Vintagestory.API.Common.Shape", throwOnError: true)!;
            Type shapeElementType = apiAsm.GetType("Vintagestory.API.Common.ShapeElement", throwOnError: true)!;
            Type animType = apiAsm.GetType("Vintagestory.API.Common.Animation", throwOnError: true)!;
            Type kfType = apiAsm.GetType("Vintagestory.API.Common.AnimationKeyFrame", throwOnError: true)!;
            Type apType = apiAsm.GetType("Vintagestory.API.Common.AttachmentPoint", throwOnError: true)!;

            string token = Guid.NewGuid().ToString("N")[..12];
            string torsoName = "Torso_" + token;
            string headName = "Head_" + token;
            string parentBone = "ParentNeck_" + token;
            string apCode = "NeckAP_" + token;
            string animCode = "walk_" + token;
            string animName = "WalkCycle_" + token;
            string texName = "bodytex_" + token;

            string json = $$"""
                {
                  "textures": { "{{texName}}": "entity/humanoid/seraph" },
                  "elements": [
                    {
                      "name": "{{torsoName}}",
                      "from": [0, 0, 0],
                      "to": [4, 6, 4],
                      "faces": {
                        "north": { "texture": "#{{texName}}", "uv": [0, 0, 4, 6] }
                      },
                      "attachmentpoints": [
                        { "code": "{{apCode}}", "posx": 0, "posy": 6, "posz": 0 }
                      ],
                      "children": [
                        {
                          "name": "{{headName}}",
                          "stepParentName": "{{parentBone}}",
                          "from": [0, 6, 0],
                          "to": [4, 10, 4]
                        }
                      ]
                    }
                  ],
                  "animations": [
                    {
                      "name": "{{animName}}",
                      "code": "{{animCode}}",
                      "quantityframes": 10,
                      "keyframes": [
                        {
                          "frame": 0,
                          "elements": {
                            "{{torsoName}}": { "rotationX": 5 },
                            "{{headName}}": { "rotationY": 10 }
                          }
                        },
                        {
                          "frame": 5,
                          "elements": {
                            "{{torsoName}}": { "rotationX": 15 },
                            "{{headName}}": { "rotationY": 20 }
                          }
                        }
                      ]
                    }
                  ]
                }
                """;

            Type jsonUtilType = apiAsm.GetType("Vintagestory.API.Common.JsonUtil", throwOnError: true)!;
            MethodInfo populateMethod = jsonUtilType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "PopulateObject" && m.GetParameters().Length == 4);

            object DeserializeShape()
            {
                object shape = Activator.CreateInstance(shapeType)!;
                populateMethod.Invoke(null, [shape, json, "survival", null]);
                return shape;
            }

            object shape1 = DeserializeShape();
            object shape2 = DeserializeShape();

            object[] elems1 = (object[])shapeType.GetField("Elements")!.GetValue(shape1)!;
            object[] elems2 = (object[])shapeType.GetField("Elements")!.GetValue(shape2)!;
            object root1 = elems1[0];
            object root2 = elems2[0];
            object child1 = ((object[])shapeElementType.GetField("Children")!.GetValue(root1)!)[0];
            object child2 = ((object[])shapeElementType.GetField("Children")!.GetValue(root2)!)[0];

            FieldInfo nameField = shapeElementType.GetField("Name")!;
            FieldInfo stepParentField = shapeElementType.GetField("StepParentName")!;
            string loadedTorso1 = (string)nameField.GetValue(root1)!;
            string loadedTorso2 = (string)nameField.GetValue(root2)!;
            Assert.Same(loadedTorso1, loadedTorso2);
            Assert.Same(nameField.GetValue(child1), nameField.GetValue(child2));
            Assert.Same(stepParentField.GetValue(child1), stepParentField.GetValue(child2));
            Assert.Null(string.IsInterned(loadedTorso1));

            object ap1 = ((object[])shapeElementType.GetField("AttachmentPoints")!.GetValue(root1)!)[0];
            object ap2 = ((object[])shapeElementType.GetField("AttachmentPoints")!.GetValue(root2)!)[0];
            FieldInfo apCodeField = apType.GetField("Code")!;
            Assert.Same(apCodeField.GetValue(ap1), apCodeField.GetValue(ap2));

            object[] anims1 = (object[])shapeType.GetField("Animations")!.GetValue(shape1)!;
            object[] anims2 = (object[])shapeType.GetField("Animations")!.GetValue(shape2)!;
            Assert.Same(animType.GetField("Code")!.GetValue(anims1[0]), animType.GetField("Code")!.GetValue(anims2[0]));
            Assert.Same(animType.GetField("Name")!.GetValue(anims1[0]), animType.GetField("Name")!.GetValue(anims2[0]));

            object[] kfs1 = (object[])animType.GetField("KeyFrames")!.GetValue(anims1[0])!;
            object[] kfs2 = (object[])animType.GetField("KeyFrames")!.GetValue(anims2[0])!;
            IDictionary kf0Elems1 = (IDictionary)kfType.GetField("Elements")!.GetValue(kfs1[0])!;
            IDictionary kf1Elems1 = (IDictionary)kfType.GetField("Elements")!.GetValue(kfs1[1])!;
            IDictionary kf0Elems2 = (IDictionary)kfType.GetField("Elements")!.GetValue(kfs2[0])!;

            string kfKeyTorso1 = kf0Elems1.Keys.Cast<string>().Single(k => k == torsoName);
            string kfKeyTorso1Frame5 = kf1Elems1.Keys.Cast<string>().Single(k => k == torsoName);
            string kfKeyTorso2 = kf0Elems2.Keys.Cast<string>().Single(k => k == torsoName);
            Assert.Same(loadedTorso1, kfKeyTorso1);
            Assert.Same(loadedTorso1, kfKeyTorso1Frame5);
            Assert.Same(loadedTorso1, kfKeyTorso2);

            // Programmatic CollectAndResolveReferences preserves lookup semantics and deduplicates keys.
            IDictionary manualKf = (IDictionary)kfType.GetField("Elements")!.GetValue(kfs1[0])!;
            string freshTorsoKey = new string(torsoName.ToCharArray());
            object torsoAnimVal = manualKf[torsoName]!;
            manualKf.Clear();
            manualKf[freshTorsoKey] = torsoAnimVal;
            nameField.SetValue(root1, new string(torsoName.ToCharArray()));

            MethodInfo collectMethod = shapeType.GetMethods()
                .Single(m => m.Name == "CollectAndResolveReferences" && m.GetParameters().Length == 2);
            IDictionary resolvedByName = (IDictionary)collectMethod.Invoke(shape1, [null, "test-shape"])!;
            Assert.Same(root1, resolvedByName[torsoName]);
            Assert.Same(loadedTorso2, nameField.GetValue(root1));
            IDictionary postResolveKf = (IDictionary)kfType.GetField("Elements")!.GetValue(kfs1[0])!;
            Assert.Same(loadedTorso2, postResolveKf.Keys.Cast<string>().Single(k => k == torsoName));

            MethodInfo getByName = shapeType.GetMethod("GetElementByName", [typeof(string), typeof(StringComparison)])!;
            Assert.Same(root1, getByName.Invoke(shape1, [torsoName.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase]));

            // SubclassForStepParenting deduplicates prefixed names/textures/keyframes across clones.
            MethodInfo cloneMethod = shapeType.GetMethod("Clone", Type.EmptyTypes)!;
            MethodInfo subclassMethod = shapeType.GetMethod("SubclassForStepParenting", [typeof(string), typeof(float)])!;
            object garment1 = cloneMethod.Invoke(shape1, null)!;
            object garment2 = cloneMethod.Invoke(shape1, null)!;
            string prefix = "coat-" + token + "-";
            Assert.True((bool)subclassMethod.Invoke(garment1, [prefix, 0f])!);
            Assert.True((bool)subclassMethod.Invoke(garment2, [prefix, 0f])!);

            object garmentRoot1 = ((object[])shapeType.GetField("Elements")!.GetValue(garment1)!)[0];
            object garmentRoot2 = ((object[])shapeType.GetField("Elements")!.GetValue(garment2)!)[0];
            Assert.Equal(prefix + torsoName, (string)nameField.GetValue(garmentRoot1)!);
            Assert.Same(nameField.GetValue(garmentRoot1), nameField.GetValue(garmentRoot2));

            object gKf1 = ((object[])animType.GetField("KeyFrames")!.GetValue(((object[])shapeType.GetField("Animations")!.GetValue(garment1)!)[0])!)[0];
            object gKf2 = ((object[])animType.GetField("KeyFrames")!.GetValue(((object[])shapeType.GetField("Animations")!.GetValue(garment2)!)[0])!)[0];
            string gKey1 = ((IDictionary)kfType.GetField("Elements")!.GetValue(gKf1)!).Keys.Cast<string>().Single(k => k == prefix + torsoName);
            string gKey2 = ((IDictionary)kfType.GetField("Elements")!.GetValue(gKf2)!).Keys.Cast<string>().Single(k => k == prefix + torsoName);
            Assert.Same(nameField.GetValue(garmentRoot1), gKey1);
            Assert.Same(gKey1, gKey2);

            // Clearing the session pool allows fresh instances on next world/asset load.
            clearPool.Invoke(null, null);
            object shape3 = DeserializeShape();
            object root3 = ((object[])shapeType.GetField("Elements")!.GetValue(shape3)!)[0];
            string loadedTorso3 = (string)nameField.GetValue(root3)!;
            Assert.Equal(loadedTorso1, loadedTorso3);
            Assert.False(object.ReferenceEquals(loadedTorso1, loadedTorso3));
        }
        finally
        {
            alc.Unload();
        }
    }

    [Fact]
    public void PatchedLib_ShapeTesselatorManagerDispose_ClearsShapeStringPool()
    {
        string repoRoot = RepoRoot();
        string cfgDir = File.Exists(Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", "Release", "net10.0", "VintagestoryLib-patched.dll"))
            ? "Release"
            : "Debug";
        string patchedLibPath = Path.Combine(repoRoot, "build", "VintagestoryLib", "bin", cfgDir, "net10.0", "VintagestoryLib-patched.dll");
        if (!File.Exists(patchedLibPath))
        {
            return;
        }

        string libDir = Path.GetDirectoryName(patchedLibPath)!;
        string outDir = Path.Combine(repoRoot, "bin", cfgDir, "net10.0");
        string vanillaDir = Path.Combine(repoRoot, ".vanilla", "win-x64", "vintagestory");
        string vanillaLibDir = Path.Combine(vanillaDir, "Lib");
        var alc = new AssemblyLoadContext("ShapeDedupLibTest", isCollectible: true);
        alc.Resolving += (ctx, name) =>
        {
            foreach (string dir in new[] { libDir, outDir, vanillaDir, vanillaLibDir, AppContext.BaseDirectory })
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return ctx.LoadFromAssemblyPath(candidate);
                }
            }
            return null;
        };

        try
        {
            Assembly libAsm = alc.LoadFromAssemblyPath(patchedLibPath);
            Assembly contractsAsm = alc.Assemblies.FirstOrDefault(a => a.GetName().Name == "Optimum.Api.Contracts")
                ?? alc.LoadFromAssemblyName(new AssemblyName("Optimum.Api.Contracts"));

            Type bridgeType = contractsAsm.GetType("Vintagestory.API.Config.OptimumApiBridge", throwOnError: true)!;
            Type poolType = contractsAsm.GetType("Vintagestory.API.Config.OptimumShapeStringPool", throwOnError: true)!;
            object sharedPool = poolType.GetField("Shared", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            MethodInfo dedup = bridgeType.GetMethod("DeduplicateShapeString", BindingFlags.Public | BindingFlags.Static)!;
            MethodInfo getStats = poolType.GetMethod("GetStats", BindingFlags.Instance | BindingFlags.NonPublic)!;

            dedup.Invoke(null, ["TempBone_" + Guid.NewGuid().ToString("N")]);
            var beforeDispose = ((int EntryCount, int AllocatedSlots))getStats.Invoke(sharedPool, null)!;
            Assert.True(beforeDispose.EntryCount > 0);

            Type stmType = libAsm.GetType("Vintagestory.Client.NoObf.ShapeTesselatorManager", throwOnError: true)!;
            object stm = RuntimeHelpers.GetUninitializedObject(stmType);
            stmType.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(stm, null);

            var afterDispose = ((int EntryCount, int AllocatedSlots))getStats.Invoke(sharedPool, null)!;
            Assert.Equal(0, afterDispose.EntryCount);
        }
        finally
        {
            alc.Unload();
        }
    }
}
