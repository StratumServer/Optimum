#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class AnimationFrameLerpTests
{
    [Fact]
    public void ComputeClientAnimatorFrameLerps_MatchesVanillaAcrossWrapHoldAndZeroLengthGuard()
    {
        var wrapAnim = new Animation { Code = "walk", Name = "walk", QuantityFrames = 10 };
        var holdAnim = new Animation { Code = "hold", Name = "hold", QuantityFrames = 10, OnAnimationEnd = EnumEntityAnimationEndHandling.Hold };
        var emptyAnim = new Animation { Code = "zero", Name = "zero", QuantityFrames = 0 };

        var curAnims = new RunningAnimation[]
        {
            new() { Animation = wrapAnim, CurrentFrame = 9.25f }, // wraps 8 -> 2 across 10 frames (dist = 4, cur = 1.25 -> 0.3125)
            new() { Animation = holdAnim, CurrentFrame = 5.5f },  // 2 -> 6 (dist = 4, cur = 3.5 -> 0.875)
            new() { Animation = emptyAnim, CurrentFrame = 0f },   // keyFrameDist == 0 -> guarded 0f, no NaN
        };
        int[] prev = [8, 2, 0];
        int[] next = [2, 6, 0];
        float[]? lerps = null;

        OptimumApiBridge.ComputeClientAnimatorFrameLerps(curAnims, curAnims.Length, prev, next, ref lerps);

        Assert.NotNull(lerps);
        Assert.True(lerps!.Length >= ClientAnimator.MaxConcurrentAnimations);
        Assert.Equal(1.25f / 4f, lerps[0], 6);
        Assert.Equal(3.5f / 4f, lerps[1], 6);
        Assert.Equal(0f, lerps[2]);
        Assert.False(float.IsNaN(lerps[2]));

        // Zero active animations initializes buffer cleanly with no work.
        float[]? emptyLerps = null;
        OptimumApiBridge.ComputeClientAnimatorFrameLerps(curAnims, 0, prev, next, ref emptyLerps);
        Assert.NotNull(emptyLerps);
    }

    [Fact]
    public void ApiPatcher_RewritesCalculateMatricesToPrecomputeFrameLerpsAndRemovesInnerDivision()
    {
        string vanillaApi = PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/VintagestoryAPI.dll");
        using var module = AssemblyDefinition.ReadAssembly(vanillaApi).MainModule;

        MethodReference computeLerpsBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(
                nameof(OptimumApiBridge.ComputeClientAnimatorFrameLerps),
                [typeof(RunningAnimation[]), typeof(int), typeof(int[]), typeof(int[]), typeof(float[]).MakeByRefType()])!);

        Assert.Equal(1, ApiPatcher.PatchClientAnimatorFrameLerp(module, computeLerpsBridge));
        // Second call must be idempotent.
        Assert.Equal(1, ApiPatcher.PatchClientAnimatorFrameLerp(module, computeLerpsBridge));

        TypeDefinition clientAnimator = module.GetType("Vintagestory.API.Common.ClientAnimator");
        Assert.Single(clientAnimator.Fields, f => f.Name == "frameLerps");

        MethodDefinition outerCalc = clientAnimator.Methods.Single(m =>
            !m.IsStatic && m.Name == "calculateMatrices" && m.Parameters.Count == 1);
        Assert.Equal(1, outerCalc.Body.Instructions.Count(ins =>
            ins.OpCode == OpCodes.Call &&
            ins.Operand is MethodReference mr &&
            mr.FullName == computeLerpsBridge.FullName));

        MethodDefinition innerCalc = clientAnimator.Methods.Single(m =>
            !m.IsStatic && m.Name == "calculateMatrices" && m.Parameters.Count == 8);
        Assert.Contains(innerCalc.Body.Instructions, ins =>
            ins.OpCode == OpCodes.Ldfld &&
            ins.Operand is FieldReference fr &&
            fr.Name == "frameLerps");
        Assert.DoesNotContain(innerCalc.Body.Instructions, ins =>
            ins.OpCode == OpCodes.Ldfld &&
            ins.Operand is FieldReference fr &&
            (fr.Name == "prevFrame" || fr.Name == "nextFrame"));
        // Only one division remains (weightSum / sew.Weight); the per-bone lerp division is eliminated.
        Assert.Equal(1, innerCalc.Body.Instructions.Count(ins => ins.OpCode == OpCodes.Div));
    }

    [Fact]
    public void PatchedShippedClientAnimator_MatchesVanillaMatricesBitExactAcrossWrapHoldAndMultiBlend()
    {
        string worktreeRoot = Path.GetDirectoryName(Path.GetDirectoryName(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"))!)!;
        string vanillaApiPath = Path.Combine(worktreeRoot, ".vanilla/win-x64/runtime-donors/VintagestoryAPI.dll");
        string patchedApiPath = Path.Combine(worktreeRoot, ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll");
        string vsLibDir = Path.Combine(worktreeRoot, ".vanilla/win-x64/vintagestory/Lib");

        Assert.True(File.Exists(vanillaApiPath), $"Missing vanilla donor API: {vanillaApiPath}");
        Assert.True(File.Exists(patchedApiPath), $"Missing deployed patched API: {patchedApiPath}");

        float[] RunSequence(string apiPath)
        {
            var alc = new IsolatedApiLoadContext(
                Path.GetDirectoryName(apiPath)!,
                Path.GetDirectoryName(patchedApiPath)!,
                vsLibDir);
            try
            {
                Assembly asm = alc.LoadFromAssemblyPath(apiPath);
                Type shapeElemType = asm.GetType("Vintagestory.API.Common.ShapeElement")!;
                Type jointType = asm.GetType("Vintagestory.API.Common.AnimationJoint")!;
                Type kfElemType = asm.GetType("Vintagestory.API.Common.AnimationKeyFrameElement")!;
                Type kfType = asm.GetType("Vintagestory.API.Common.AnimationKeyFrame")!;
                Type animType = asm.GetType("Vintagestory.API.Common.Animation")!;
                Type metaType = asm.GetType("Vintagestory.API.Common.AnimationMetaData")!;
                Type endEnum = asm.GetType("Vintagestory.API.Common.EnumEntityAnimationEndHandling")!;
                Type blendEnum = asm.GetType("Vintagestory.API.Common.EnumAnimationBlendMode")!;
                Type animatorType = asm.GetType("Vintagestory.API.Common.ClientAnimator")!;
                Type walkDelType = asm.GetType("Vintagestory.API.Common.WalkSpeedSupplierDelegate")!;

                object MakeElem(string name, object? parent, int jid, Dictionary<string, object> byName, object jointsDict)
                {
                    object e = Activator.CreateInstance(shapeElemType)!;
                    shapeElemType.GetField("Name")!.SetValue(e, name);
                    shapeElemType.GetField("ParentElement")!.SetValue(e, parent);
                    shapeElemType.GetField("JointId")!.SetValue(e, jid);
                    shapeElemType.GetField("From")!.SetValue(e, new[] { 0.0, 0.0, 0.0 });
                    shapeElemType.GetField("To")!.SetValue(e, new[] { 2.0, 4.0, 2.0 });
                    shapeElemType.GetField("RotationOrigin")!.SetValue(e, new[] { 1.0, 1.0, 1.0 });
                    byName[name] = e;
                    object j = Activator.CreateInstance(jointType)!;
                    jointType.GetField("JointId")!.SetValue(j, jid);
                    jointType.GetField("Element")!.SetValue(j, e);
                    jointsDict.GetType().GetMethod("set_Item")!.Invoke(jointsDict, [jid, j]);
                    return e;
                }

                Type byNameDictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), shapeElemType);
                object byNameObj = Activator.CreateInstance(byNameDictType)!;
                var tmpByName = new Dictionary<string, object>();
                Type jointsDictType = typeof(Dictionary<,>).MakeGenericType(typeof(int), jointType);
                object jointsObj = Activator.CreateInstance(jointsDictType)!;

                object torso = MakeElem("torso", null, 1, tmpByName, jointsObj);
                object head = MakeElem("head", torso, 2, tmpByName, jointsObj);
                object armL = MakeElem("armL", torso, 3, tmpByName, jointsObj);
                object handL = MakeElem("handL", armL, 4, tmpByName, jointsObj);

                Array oneHand = Array.CreateInstance(shapeElemType, 1);
                oneHand.SetValue(handL, 0);
                shapeElemType.GetField("Children")!.SetValue(armL, oneHand);

                Array twoChildren = Array.CreateInstance(shapeElemType, 2);
                twoChildren.SetValue(head, 0);
                twoChildren.SetValue(armL, 1);
                shapeElemType.GetField("Children")!.SetValue(torso, twoChildren);

                foreach (var kv in tmpByName)
                {
                    byNameDictType.GetMethod("set_Item")!.Invoke(byNameObj, [kv.Key, kv.Value]);
                }

                Array rootArr = Array.CreateInstance(shapeElemType, 1);
                rootArr.SetValue(torso, 0);

                object MakeKf(int frame, double rx, double ry, double ox)
                {
                    object kf = Activator.CreateInstance(kfType)!;
                    kfType.GetField("Frame")!.SetValue(kf, frame);
                    Type dType = typeof(Dictionary<,>).MakeGenericType(typeof(string), kfElemType);
                    object d = Activator.CreateInstance(dType)!;
                    foreach (string name in new[] { "torso", "head", "armL", "handL" })
                    {
                        object ke = Activator.CreateInstance(kfElemType)!;
                        kfElemType.GetField("RotationX")!.SetValue(ke, rx);
                        kfElemType.GetField("RotationY")!.SetValue(ke, ry);
                        kfElemType.GetField("RotationZ")!.SetValue(ke, 0.0);
                        kfElemType.GetField("OffsetX")!.SetValue(ke, ox);
                        kfElemType.GetField("OffsetY")!.SetValue(ke, 0.0);
                        kfElemType.GetField("OffsetZ")!.SetValue(ke, 0.0);
                        dType.GetMethod("set_Item")!.Invoke(d, [name, ke]);
                    }
                    kfType.GetField("Elements")!.SetValue(kf, d);
                    kfType.GetMethod("Resolve", [byNameDictType])!.Invoke(kf, [byNameObj]);
                    return kf;
                }

                object MakeAnim(string code, int qty, int endMode, (int f, double rx, double ry, double ox)[] kfs)
                {
                    object a = Activator.CreateInstance(animType)!;
                    animType.GetField("Code")!.SetValue(a, code);
                    animType.GetField("Name")!.SetValue(a, code);
                    animType.GetField("QuantityFrames")!.SetValue(a, qty);
                    animType.GetField("OnAnimationEnd")!.SetValue(a, Enum.ToObject(endEnum, endMode));
                    Array kfArr = Array.CreateInstance(kfType, kfs.Length);
                    for (int i = 0; i < kfs.Length; i++)
                        kfArr.SetValue(MakeKf(kfs[i].f, kfs[i].rx, kfs[i].ry, kfs[i].ox), i);
                    animType.GetField("KeyFrames")!.SetValue(a, kfArr);
                    animType.GetMethod("GenerateAllFrames")!.Invoke(a, [rootArr, jointsObj, true]);
                    return a;
                }

                // Walk repeats cleanly (0), Hold freezes on last frame (2), Add layers additively.
                object aWalk = MakeAnim("walk", 10, 0, [(0, 5, 0, 0), (4, 35, 10, 1), (8, -20, 5, 0.5)]);
                object aHold = MakeAnim("hold", 6, 2, [(0, 0, 15, 0), (5, 45, 30, 2)]);
                object aAdd = MakeAnim("add", 8, 0, [(0, 2, 2, 0), (6, 12, 18, 0)]);

                Array animsArr = Array.CreateInstance(animType, 3);
                animsArr.SetValue(aWalk, 0);
                animsArr.SetValue(aHold, 1);
                animsArr.SetValue(aAdd, 2);

                ConstructorInfo ctor = animatorType.GetConstructors()
                    .Single(c => c.GetParameters().Length == 6 && c.GetParameters()[2].ParameterType.IsArray);
                object walkDel = Delegate.CreateDelegate(walkDelType, typeof(AnimParityHelpers).GetMethod(nameof(AnimParityHelpers.One))!);
                object animator = ctor.Invoke([walkDel, animsArr, rootArr, jointsObj, null, null]);

                Type activeDictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), metaType);
                object active = Activator.CreateInstance(activeDictType)!;

                object MakeMeta(string code, float speed, float weight, int blendMode)
                {
                    object m = Activator.CreateInstance(metaType)!;
                    metaType.GetField("Code")!.SetValue(m, code);
                    metaType.GetField("AnimationSpeed")!.SetValue(m, speed);
                    metaType.GetField("Weight")!.SetValue(m, weight);
                    metaType.GetField("EaseInSpeed")!.SetValue(m, 50f);
                    metaType.GetField("EaseOutSpeed")!.SetValue(m, 50f);
                    metaType.GetField("BlendMode")!.SetValue(m, Enum.ToObject(blendEnum, blendMode));
                    return m;
                }

                activeDictType.GetMethod("set_Item")!.Invoke(active, ["walk", MakeMeta("walk", 1.25f, 1.0f, 1)]);
                activeDictType.GetMethod("set_Item")!.Invoke(active, ["hold", MakeMeta("hold", 1.0f, 0.6f, 2)]);
                activeDictType.GetMethod("set_Item")!.Invoke(active, ["add", MakeMeta("add", 0.9f, 0.4f, 0)]);

                MethodInfo onFrame = animatorType.GetMethod("OnFrame")!;
                var recorded = new List<float>();
                FieldInfo matricesField = animatorType.GetField("TransformationMatrices")!;
                for (int step = 0; step < 30; step++)
                {
                    onFrame.Invoke(animator, [active, 0.1f]);
                    float[] mats = (float[])matricesField.GetValue(animator)!;
                    recorded.AddRange(mats);
                }
                return recorded.ToArray();
            }
            finally
            {
                alc.Unload();
            }
        }

        float[] vanillaMatrices = RunSequence(vanillaApiPath);
        float[] patchedMatrices = RunSequence(patchedApiPath);

        Assert.Equal(vanillaMatrices.Length, patchedMatrices.Length);
        Assert.NotEmpty(patchedMatrices);
        for (int i = 0; i < vanillaMatrices.Length; i++)
        {
            Assert.Equal(vanillaMatrices[i], patchedMatrices[i]);
        }
    }

    private sealed class IsolatedApiLoadContext(string primaryDir, string fallbackDir, string libDir) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            string dll = name.Name + ".dll";
            string p1 = Path.Combine(primaryDir, dll);
            if (File.Exists(p1)) return LoadFromAssemblyPath(p1);
            string p2 = Path.Combine(fallbackDir, dll);
            if (File.Exists(p2)) return LoadFromAssemblyPath(p2);
            string p3 = Path.Combine(libDir, dll);
            if (File.Exists(p3)) return LoadFromAssemblyPath(p3);
            return null;
        }
    }

    private static class AnimParityHelpers
    {
        public static double One() => 1.0;
    }
}
