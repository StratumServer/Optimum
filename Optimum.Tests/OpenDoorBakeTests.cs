#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Optimum.Patcher;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class OpenDoorBakeTests
{
    private static string FindDll(string relativePath)
    {
        string nearTests = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(relativePath));
        if (File.Exists(nearTests))
        {
            return nearTests;
        }
        return PatchReader.FindRepositoryFile(relativePath);
    }

    private static void BuildTestDoorRig(
        EnumEntityAnimationEndHandling endHandling,
        int quantityFrames,
        float hingeDegY,
        Action<string>? onSound,
        out MeshData origMesh,
        out ClientAnimator animator)
    {
        var leaf = new ShapeElement
        {
            Name = "doorLeaf",
            From = [0, 0, 0],
            To = [1, 1, 1],
            RotationOrigin = [0, 0, 0],
            JointId = 1,
        };
        var byName = new Dictionary<string, ShapeElement> { [leaf.Name] = leaf };
        var jointsById = new Dictionary<int, AnimationJoint>
        {
            [1] = new AnimationJoint { JointId = 1, Element = leaf },
        };

        var kf0 = new AnimationKeyFrame
        {
            Frame = 0,
            Elements = new Dictionary<string, AnimationKeyFrameElement>
            {
                [leaf.Name] = new AnimationKeyFrameElement
                {
                    RotationX = 0,
                    RotationY = quantityFrames <= 1 ? hingeDegY : 0f,
                    RotationZ = 0,
                },
            },
        };
        kf0.Resolve(byName);

        AnimationKeyFrame[] keyFrames;
        if (quantityFrames <= 1)
        {
            keyFrames = [kf0];
        }
        else
        {
            var kfEnd = new AnimationKeyFrame
            {
                Frame = quantityFrames - 1,
                Elements = new Dictionary<string, AnimationKeyFrameElement>
                {
                    [leaf.Name] = new AnimationKeyFrameElement { RotationX = 0, RotationY = hingeDegY, RotationZ = 0 },
                },
            };
            kfEnd.Resolve(byName);
            keyFrames = [kf0, kfEnd];
        }

        var anim = new Animation
        {
            Code = OptimumOpenDoorBake.OpenAnimCode,
            Name = OptimumOpenDoorBake.OpenAnimCode,
            QuantityFrames = quantityFrames,
            OnAnimationEnd = endHandling,
            OnActivityStopped = EnumEntityActivityStoppedHandling.Rewind,
            KeyFrames = keyFrames,
        };
        ShapeElement[] roots = [leaf];
        anim.GenerateAllFrames(roots, jointsById);

        animator = new ClientAnimator(
            () => 1.0,
            [anim],
            roots,
            jointsById,
            onShouldPlaySoundListener: onSound != null ? (code, _) => onSound(code) : null);

        origMesh = new MeshData(8, 12);
        origMesh.WithNormals();
        origMesh.CustomInts = new CustomMeshDataPartInt(8)
        {
            InterleaveSizes = [1],
            InterleaveOffsets = [0],
            InterleaveStride = 0,
        };

        int packedNorm = NormalUtil.PackNormal(new Vec4f(0f, 0f, 1f, 1f));
        int vertFlags = VertexFlags.PackNormal(0f, 0f, 1f) | 0x2A;

        // Vertex 0: unjointed frame (jointId = 0)
        origMesh.AddVertexWithFlags(0.1f, 0.2f, 0.3f, 0f, 0f, -1, vertFlags);
        origMesh.Normals[0] = packedNorm;
        origMesh.NormalsCount = 1;
        origMesh.CustomInts.Add(0);

        // Vertex 1: moving door leaf (jointId = 1)
        origMesh.AddVertexWithFlags(0.8f, 0.5f, 0.2f, 0f, 0f, -1, vertFlags);
        origMesh.Normals[1] = packedNorm;
        origMesh.NormalsCount = 2;
        origMesh.CustomInts.Add(1);

        origMesh.AddXyzFace((byte)BlockFacing.SOUTH.MeshDataIndex);
        origMesh.VerticesPerFace = 2;
    }

    [Fact]
    public void BakeOpenDoors_ConfigRoundTrips_DefaultsTrue_AndDiagnosticsReset()
    {
        bool origConfig = OptimumConfig.BakeOpenDoorsEnabled;
        string dir = Path.Combine(Path.GetTempPath(), "optimum-bake-doors-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(OptimumConfig.BakeOpenDoorsEnabled);
            Assert.Contains(OptimumConfig.DescribeToggles(), t => t.Name == nameof(OptimumConfigData.BakeOpenDoors));

            OptimumConfig.SetDataPath(dir);
            OptimumConfig.BakeOpenDoorsEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.BakeOpenDoorsEnabled = true;
            OptimumConfig.Load();
            Assert.False(OptimumConfig.BakeOpenDoorsEnabled);

            OptimumConfig.BakeOpenDoorsEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.BakeOpenDoorsEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.BakeOpenDoorsEnabled);

            OptimumDiagnostics.ResetAllCounters();
            OptimumDiagnostics.RecordOpenDoorBakedFromTransition();
            OptimumDiagnostics.RecordOpenDoorBakedFromLoad();
            OptimumDiagnostics.RecordOpenDoorBakedFromLoad();
            OptimumDiagnostics.RecordOpenDoorReopened();
            OptimumDiagnostics.RecordOpenDoorDynamicOrSkipped();

            Assert.Equal(1, OptimumDiagnostics.OpenDoorsBakedFromTransition);
            Assert.Equal(2, OptimumDiagnostics.OpenDoorsBakedFromLoad);
            Assert.Equal(1, OptimumDiagnostics.OpenDoorsReopenedAnimation);
            Assert.Equal(1, OptimumDiagnostics.OpenDoorsDynamicOrSkipped);

            string summary = OptimumDiagnostics.GetOpenDoorBakeSummary();
            Assert.Contains("enabled=True", summary);
            Assert.Contains("bakedTotal=3", summary);
            Assert.Contains("transition=1", summary);
            Assert.Contains("load=2", summary);
            Assert.Contains("reopened=1", summary);
            Assert.Contains("dynamicOrSkipped=1", summary);

            OptimumDiagnostics.ResetAllCounters();
            Assert.Equal(0, OptimumDiagnostics.OpenDoorsBakedFromTransition);
            Assert.Equal(0, OptimumDiagnostics.OpenDoorsBakedFromLoad);
            Assert.Equal(0, OptimumDiagnostics.OpenDoorsReopenedAnimation);
            Assert.Equal(0, OptimumDiagnostics.OpenDoorsDynamicOrSkipped);

            string statusSource = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));
            Assert.Contains("OptimumDiagnostics.GetOpenDoorBakeSummary()", statusSource);
        }
        finally
        {
            OptimumConfig.SetDataPath(null!);
            OptimumConfig.BakeOpenDoorsEnabled = origConfig;
            OptimumDiagnostics.ResetAllCounters();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CanBakeOpenAnimation_MatchesVanillaDoorAndTrapdoorShapes_RejectsLoopingDoors()
    {
        Assert.False(OptimumOpenDoorBake.CanBakeOpenAnimation(null));
        Assert.True(OptimumOpenDoorBake.CanBakeOpenAnimation(new Animation { OnAnimationEnd = EnumEntityAnimationEndHandling.Hold, QuantityFrames = 30 }));
        Assert.True(OptimumOpenDoorBake.CanBakeOpenAnimation(new Animation
        {
            OnAnimationEnd = EnumEntityAnimationEndHandling.Repeat,
            QuantityFrames = 2,
            KeyFrames = [new AnimationKeyFrame { Frame = 0 }],
        }));
        Assert.False(OptimumOpenDoorBake.CanBakeOpenAnimation(new Animation
        {
            OnAnimationEnd = EnumEntityAnimationEndHandling.Repeat,
            QuantityFrames = 10,
            KeyFrames = [new AnimationKeyFrame { Frame = 0 }, new AnimationKeyFrame { Frame = 5 }],
        }));
        Assert.False(OptimumOpenDoorBake.CanBakeOpenAnimation(new Animation { OnAnimationEnd = EnumEntityAnimationEndHandling.EaseOut, QuantityFrames = 5 }));
        Assert.False(OptimumOpenDoorBake.CanBakeOpenAnimation(new Animation { OnAnimationEnd = EnumEntityAnimationEndHandling.Stop, QuantityFrames = 5 }));
    }

    [Fact]
    public void IsSingleSettledOpenAnimation_RespectsSettlingLockAndConfigToggle()
    {
        bool origConfig = OptimumConfig.BakeOpenDoorsEnabled;
        try
        {
            BuildTestDoorRig(EnumEntityAnimationEndHandling.Hold, 10, 90f, null, out _, out ClientAnimator animator);

            var metaOpen = OptimumOpenDoorBake.CreateOpenedMeta(10f);
            var active = new Dictionary<string, AnimationMetaData>(StringComparer.OrdinalIgnoreCase)
            {
                [OptimumOpenDoorBake.OpenAnimCode] = metaOpen,
            };

            animator.OnFrame(active, 0f);
            RunningAnimation? anim = animator.GetAnimationState(OptimumOpenDoorBake.OpenAnimCode);
            Assert.NotNull(anim);

            anim!.Active = true;
            anim.Running = true;
            anim.EasingFactor = 0.9995f;
            anim.CurrentFrame = 9f;

            OptimumConfig.BakeOpenDoorsEnabled = false;
            Assert.False(OptimumOpenDoorBake.IsSingleSettledOpenAnimation(animator, active));

            OptimumConfig.BakeOpenDoorsEnabled = true;
            Assert.True(OptimumOpenDoorBake.IsSingleSettledOpenAnimation(animator, active));

            // Multi-animation active (e.g. door lock) must stay dynamic
            active["lock"] = new AnimationMetaData { Animation = "lock", Code = "lock" }.Init();
            Assert.False(OptimumOpenDoorBake.IsSingleSettledOpenAnimation(animator, active));
            active.Remove("lock");

            // Not yet eased in
            anim.EasingFactor = 0.90f;
            Assert.False(OptimumOpenDoorBake.IsSingleSettledOpenAnimation(animator, active));

            // Eased in but Hold frame not at final frame
            anim.EasingFactor = 1f;
            anim.CurrentFrame = 4f;
            Assert.False(OptimumOpenDoorBake.IsSingleSettledOpenAnimation(animator, active));

            // Single-keyframe Repeat (trapdoor / crude door) settles on EasingFactor >= 0.999f even at frame 0
            BuildTestDoorRig(EnumEntityAnimationEndHandling.Repeat, 1, 90f, null, out _, out ClientAnimator trapAnimator);
            trapAnimator.OnFrame(active, 0f);
            RunningAnimation? trapAnim = trapAnimator.GetAnimationState(OptimumOpenDoorBake.OpenAnimCode);
            Assert.NotNull(trapAnim);
            trapAnim!.EasingFactor = 0.9992f;
            trapAnim.CurrentFrame = 0f;
            Assert.True(OptimumOpenDoorBake.IsSingleSettledOpenAnimation(trapAnimator, active));
        }
        finally
        {
            OptimumConfig.BakeOpenDoorsEnabled = origConfig;
        }
    }

    [Fact]
    public void BakeJointPoseMesh_And_TransformDoorInstanceMesh_MatchGpuMatrixPipeline()
    {
        BuildTestDoorRig(EnumEntityAnimationEndHandling.Hold, 10, 90f, null, out MeshData origMesh, out ClientAnimator animator);

        float[]? jointMatrices = OptimumOpenDoorBake.EvaluateSettledOpenPoseMatrices(animator, 10f);
        Assert.NotNull(jointMatrices);
        Assert.Equal(0, animator.ActiveAnimationCount);

        MeshData bakedOrig = OptimumOpenDoorBake.BakeJointPoseMesh(origMesh, jointMatrices);
        Assert.Null(bakedOrig.CustomInts);
        Assert.NotNull(origMesh.CustomInts);

        // Static joint 0 vertex unchanged; moving joint 1 vertex matches exact joint matrix transform.
        Assert.Equal(origMesh.xyz[0], bakedOrig.xyz[0], 5);
        Assert.Equal(origMesh.xyz[1], bakedOrig.xyz[1], 5);
        Assert.Equal(origMesh.xyz[2], bakedOrig.xyz[2], 5);

        float[] expectedLeaf = new float[3];
        Mat4f.MulWithVec3_Position(jointMatrices!.AsSpan(16, 16), [origMesh.xyz[3], origMesh.xyz[4], origMesh.xyz[5]], expectedLeaf, 0);
        Assert.Equal(expectedLeaf[0], bakedOrig.xyz[3], 5);
        Assert.Equal(expectedLeaf[1], bakedOrig.xyz[4], 5);
        Assert.Equal(expectedLeaf[2], bakedOrig.xyz[5], 5);

        // Top 2 W bits of packed normal on unjointed vertex survive and leaf normal rotated cleanly.
        Assert.Equal(origMesh.Normals[0] >> 30, bakedOrig.Normals[0] >> 30);
        Assert.Equal(origMesh.Normals[1] >> 30, bakedOrig.Normals[1] >> 30);

        // Verify double door invertHandles & all cardinal rotations match GPU AnimatableRenderer ModelMat * JointMat
        foreach (bool invertHandles in new[] { false, true })
        {
            for (int step = 0; step < 4; step++)
            {
                float rotYRad = step * GameMath.PIHALF;
                MeshData instanceBaked = OptimumOpenDoorBake.TransformDoorInstanceMesh(bakedOrig, rotYRad, invertHandles);

                float rot = invertHandles ? -rotYRad : rotYRad;
                var gpuModelMat = new Matrixf()
                    .Translate(0.5f, 0f, 0.5f)
                    .Scale(invertHandles ? -1f : 1f, 1f, 1f)
                    .RotateY(rot)
                    .Translate(-0.5f, 0f, -0.5f)
                    .Values;

                var combinedLeafMat = new float[16];
                Mat4f.Mul(combinedLeafMat, gpuModelMat, jointMatrices!.AsSpan(16, 16).ToArray());
                float[] gpuPos = new float[3];
                Mat4f.MulWithVec3_Position(combinedLeafMat, [origMesh.xyz[3], origMesh.xyz[4], origMesh.xyz[5]], gpuPos, 0);

                Assert.Equal(gpuPos[0], instanceBaked.xyz[3], 4);
                Assert.Equal(gpuPos[1], instanceBaked.xyz[4], 4);
                Assert.Equal(gpuPos[2], instanceBaked.xyz[5], 4);
            }
        }
    }

    [Fact]
    public void EvaluateAndPrimeClosingRewind_SuppressesBakeSoundAndRewindsSmoothlyFromFullOpen()
    {
        int soundTriggers = 0;
        BuildTestDoorRig(EnumEntityAnimationEndHandling.Hold, 10, 90f, _ => soundTriggers++, out _, out ClientAnimator animator);

        var openMeta = OptimumOpenDoorBake.CreateOpenedMeta(10f);
        openMeta.AnimationSounds =
        [
            new AnimationSound
            {
                Frame = 0,
                Attributes = new SoundAttributes { Location = new AssetLocation("game", "sounds/block/door") },
            },
        ];

        float[]? matrices = OptimumOpenDoorBake.EvaluateSettledOpenPoseMatrices(animator, 10f);
        Assert.NotNull(matrices);
        Assert.Equal(0, soundTriggers);
        Assert.Equal(0, animator.ActiveAnimationCount);

        var active = new Dictionary<string, AnimationMetaData>(StringComparer.OrdinalIgnoreCase);
        bool started = false;
        bool stopped = false;

        OptimumOpenDoorBake.PrimeClosingFromSettledOpen(
            animator,
            active,
            meta =>
            {
                started = true;
                active[meta.Code] = meta;
            },
            code =>
            {
                stopped = true;
                active.Remove(code);
            },
            OptimumOpenDoorBake.CreateOpenedMeta(10f));

        Assert.True(started);
        Assert.True(stopped);

        // Next OnFrame steps closing rewind starting from EasingFactor ~ (1 - 10/60)^2 = 0.694 and eases smoothly down.
        animator.OnFrame(active, 1f / 60f);
        RunningAnimation? anim = animator.GetAnimationState(OptimumOpenDoorBake.OpenAnimCode);
        Assert.NotNull(anim);
        Assert.False(anim!.Active);
        Assert.True(anim.Running);
        Assert.True(anim.ShouldRewind);
        float easingStep1 = anim.EasingFactor;
        Assert.InRange(easingStep1, 0.65f, 0.99f);

        animator.OnFrame(active, 1f / 60f);
        Assert.True(anim.Running);
        Assert.InRange(anim.EasingFactor, 0.40f, easingStep1);
    }

    [Fact]
    public async Task EnsureOpenedOrigMesh_IsThreadSafeAcrossConcurrentWorkers()
    {
        BuildTestDoorRig(EnumEntityAnimationEndHandling.Repeat, 1, 90f, null, out MeshData origMesh, out ClientAnimator animator);

        object cacheLock = new object();
        MeshData? cached = null;
        var barrier = new Barrier(8);

        Task<MeshData?>[] workers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return OptimumOpenDoorBake.EnsureOpenedOrigMesh(cacheLock, ref cached, origMesh, animator, 10f);
            }))
            .ToArray();

        MeshData?[] results = await Task.WhenAll(workers);
        Assert.All(results, m => Assert.NotNull(m));
        Assert.All(results, m => Assert.Same(results[0], m));
        Assert.Null(results[0]!.CustomInts);
    }

    [Fact]
    public void RuntimeAndForkPatches_And_SurvivalManifest_AreCompleteAndLambdaFree()
    {
        var forkPatches = new[]
        {
            "patches/VSSurvivalMod/BlockBehavior/BehaviorDoor.cs.patch",
            "patches/VSSurvivalMod/BlockBehavior/BlockBehaviorTrapDoor.cs.patch",
            "patches/VSSurvivalMod/BlockEntityBehavior/BEBehaviorDoor.cs.patch",
            "patches/VSSurvivalMod/BlockEntityBehavior/BEBehaviorTrapDoor.cs.patch",
            "patches/VSSurvivalMod/BlockEntityBehavior/BEBehaviorCabinetDoors.cs.patch",
        };
        var runtimePatches = new[]
        {
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockBehaviorDoor.cs.patch",
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockBehaviorTrapDoor.cs.patch",
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BEBehaviorDoor.cs.patch",
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BEBehaviorTrapDoor.cs.patch",
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BEBehaviorCabinetDoors.cs.patch",
        };

        var lambdaPattern = new Regex(@"^\+.*=>", RegexOptions.Multiline);
        foreach (string rel in forkPatches.Concat(runtimePatches))
        {
            string full = PatchReader.FindRepositoryFile(rel);
            Assert.True(File.Exists(full), $"Missing patch: {rel}");
            string text = File.ReadAllText(full);
            Assert.NotEmpty(text);
            if (rel.StartsWith("patches/runtime/", StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(lambdaPattern, text);
            }
        }

        var manifestMethod = typeof(ModPatcher).GetMethod("SurvivalManifest", BindingFlags.NonPublic | BindingFlags.Static)!;
        object manifest = manifestMethod.Invoke(null, null)!;
        var members = (Dictionary<string, List<string>>)manifest.GetType().GetProperty("Members")!.GetValue(manifest)!;
        var methods = (IEnumerable<MethodTarget>)manifest.GetType().GetProperty("Methods")!.GetValue(manifest)!;

        Assert.Contains("animatableOpenedOrigMesh", members["Vintagestory.GameContent.BlockBehaviorDoor"]);
        Assert.Contains("animatableOpenedOrigMesh", members["Vintagestory.GameContent.BlockBehaviorTrapDoor"]);
        Assert.Contains("optimumBakeState", members["Vintagestory.GameContent.BEBehaviorDoor"]);
        Assert.Contains("optimumBakeState", members["Vintagestory.GameContent.BEBehaviorTrapDoor"]);
        Assert.Contains("optimumBakeState", members["Vintagestory.GameContent.BEBehaviorCabinetDoors"]);

        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BEBehaviorDoor" && m.MethodName == "OnTesselation");
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BEBehaviorTrapDoor" && m.MethodName == "OnTesselation");
        Assert.Contains(methods, m => m.TypeFullName == "Vintagestory.GameContent.BEBehaviorCabinetDoors" && m.MethodName == "OnTesselation");

        string survivalDll = FindDll("bin/Release/net10.0/VSSurvivalMod.dll");
        Assert.True(File.Exists(survivalDll));
        using var asm = AssemblyDefinition.ReadAssembly(survivalDll);
        foreach (string typeName in new[]
        {
            "Vintagestory.GameContent.BEBehaviorDoor",
            "Vintagestory.GameContent.BEBehaviorTrapDoor",
            "Vintagestory.GameContent.BEBehaviorCabinetDoors",
        })
        {
            TypeDefinition typeDef = asm.MainModule.GetType(typeName);
            Assert.NotNull(typeDef);
            Assert.Contains(typeDef.Fields, f => f.Name == "optimumBakeState");
            Assert.Contains(typeDef.Methods, m => m.Name == "BuildOptimumOpenedMesh");
        }
    }
}
