#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Xunit;

namespace Optimum.Tests;

public sealed class AnimationDepthAndKeyFrameTests
{
    private static ShapeElement CreateTreeShape(int siblingCount, int chainDepth, out Dictionary<string, ShapeElement> byName)
    {
        byName = new Dictionary<string, ShapeElement>();
        var roots = new List<ShapeElement>(siblingCount);
        for (int s = 0; s < siblingCount; s++)
        {
            ShapeElement parent = new() { Name = $"root_{s}", From = [0, 0, 0], To = [1, 1, 1], RotationOrigin = [0, 0, 0] };
            byName[parent.Name] = parent;
            roots.Add(parent);

            ShapeElement cursor = parent;
            for (int d = 1; d < chainDepth; d++)
            {
                ShapeElement child = new()
                {
                    Name = $"node_{s}_{d}",
                    ParentElement = cursor,
                    From = [0, 0, 0],
                    To = [1, 1, 1],
                    RotationOrigin = [0, 0, 0],
                };
                byName[child.Name] = child;
                cursor.Children = [child];
                cursor = child;
            }
        }

        var syntheticRoot = new ShapeElement { Name = "synthetic", Children = roots.ToArray(), RotationOrigin = [0, 0, 0] };
        return syntheticRoot;
    }

    [Fact]
    public void ClientAnimatorGetMaxDepth_TracksMaxBranchInsteadOfAccumulatingSiblings()
    {
        Assert.Equal(1, OptimumApiBridge.ClientAnimatorGetMaxDepth(null, 1));
        Assert.Equal(1, OptimumApiBridge.ClientAnimatorGetMaxDepth([], 1));

        var poses = new List<ElementPose>();
        for (int i = 0; i < 40; i++)
        {
            poses.Add(new ElementPose
            {
                ChildElementPoses =
                [
                    new ElementPose
                    {
                        ChildElementPoses = i == 7 ? [new ElementPose { ChildElementPoses = [] }] : null,
                    },
                ],
            });
        }

        Assert.Equal(4, OptimumApiBridge.ClientAnimatorGetMaxDepth(poses, 1));
    }

    [Fact]
    public void ClientAnimator_WideHierarchyAllocatesTrueMaxDepthAndRunsCalculateMatricesSafely()
    {
        ShapeElement holder = CreateTreeShape(siblingCount: 50, chainDepth: 4, out var byName);
        ShapeElement[] rootElements = holder.Children!;
        var jointsById = new Dictionary<int, AnimationJoint>();
        int jointId = 1;
        foreach (ShapeElement elem in byName.Values)
        {
            elem.JointId = jointId;
            jointsById[jointId] = new AnimationJoint { JointId = jointId, Element = elem };
            jointId++;
        }

        // Attach an empty non-null ChildElementPoses leaf to verify calculateMatrices bounds on depth++.
        var deepestLeaf = byName["node_0_3"];
        deepestLeaf.Children = [];

        var kf0 = new AnimationKeyFrame
        {
            Frame = 0,
            Elements = byName.Keys.ToDictionary(name => name, _ => new AnimationKeyFrameElement { RotationX = 0, RotationY = 0, RotationZ = 0 }),
        };
        var kf1 = new AnimationKeyFrame
        {
            Frame = 5,
            Elements = byName.Keys.ToDictionary(name => name, _ => new AnimationKeyFrameElement { RotationX = 15, RotationY = 0, RotationZ = 0 }),
        };
        foreach (var animKf in new[] { kf0, kf1 })
        {
            animKf.Resolve(byName);
        }

        var anim = new Animation
        {
            Code = "idle",
            Name = "idle",
            QuantityFrames = 10,
            KeyFrames = [kf0, kf1],
        };
        anim.GenerateAllFrames(rootElements, jointsById);

        var animator = new ClientAnimator(
            () => 1.0,
            [anim],
            rootElements,
            jointsById);

        FieldInfo maxDepthField = typeof(ClientAnimator).GetField("maxDepth", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo frameByDepthField = typeof(ClientAnimator).GetField("frameByDepthByAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!;

        int maxDepth = (int)maxDepthField.GetValue(animator)!;
        var frameByDepth = (List<ElementPose>[][])frameByDepthField.GetValue(animator)!;

        // chainDepth = 4 (levels 1..4) plus empty child array at level 5 => getMaxDepth == 5, initFields adds +2 => 7.
        // Without the fix, 50 width-4 siblings would inflate maxDepth above 150.
        Assert.Equal(7, maxDepth);
        Assert.Equal(7, frameByDepth.Length);

        var active = new Dictionary<string, AnimationMetaData>
        {
            ["idle"] = new AnimationMetaData { Code = "idle", CodeCrc32 = anim.CodeCrc32, AnimationSpeed = 1f },
        };
        animator.OnFrame(active, 0.05f);
        animator.OnFrame(active, 0.05f);
    }

    [Theory]
    [InlineData(0, typeof(FastSmallDictionary<ShapeElement, AnimationKeyFrameElement>))]
    [InlineData(1, typeof(FastSmallDictionary<ShapeElement, AnimationKeyFrameElement>))]
    [InlineData(16, typeof(FastSmallDictionary<ShapeElement, AnimationKeyFrameElement>))]
    [InlineData(17, typeof(Dictionary<ShapeElement, AnimationKeyFrameElement>))]
    [InlineData(96, typeof(Dictionary<ShapeElement, AnimationKeyFrameElement>))]
    public void AnimationKeyFrame_ResolveSwitchesStorageAboveThresholdAndPreservesLookupParity(int elementCount, Type expectedMapType)
    {
        var allElements = new Dictionary<string, ShapeElement>();
        var elements = new Dictionary<string, AnimationKeyFrameElement>();
        var created = new ShapeElement[elementCount];

        for (int i = 0; i < elementCount; i++)
        {
            string name = $"bone_{i}";
            var shapeElem = new ShapeElement { Name = name };
            created[i] = shapeElem;
            allElements[name] = shapeElem;
            elements[name] = new AnimationKeyFrameElement { OffsetX = i + 1.0 };
        }

        // Replace the final key with an unmapped bone when elementCount >= 2 so Elements.Count stays exact.
        bool checkMissing = elementCount >= 2;
        int resolvedCount = checkMissing ? elementCount - 1 : elementCount;
        if (checkMissing)
        {
            string replacedName = $"bone_{elementCount - 1}";
            elements.Remove(replacedName);
            elements["missing_bone"] = new AnimationKeyFrameElement { OffsetX = 999 };
        }

        var keyFrame = new AnimationKeyFrame
        {
            Frame = 12,
            Elements = elements,
        };

        keyFrame.Resolve(allElements);

        FieldInfo mapField = typeof(AnimationKeyFrame).GetField("ElementsByShapeElement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo getElemMethod = typeof(AnimationKeyFrame).GetMethod("GetKeyFrameElement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo frameField = typeof(AnimationKeyFrameElement).GetField("Frame", BindingFlags.Instance | BindingFlags.NonPublic)!;

        object? backingMap = mapField.GetValue(keyFrame);
        Assert.NotNull(backingMap);
        Assert.IsType(expectedMapType, backingMap);

        for (int i = 0; i < resolvedCount; i++)
        {
            var resolved = (AnimationKeyFrameElement?)getElemMethod.Invoke(keyFrame, [created[i]]);
            Assert.NotNull(resolved);
            Assert.Equal(12, (int)frameField.GetValue(resolved)!);
            Assert.Equal(i + 1.0, resolved.OffsetX);
        }

        Assert.Null(getElemMethod.Invoke(keyFrame, [new ShapeElement { Name = "unreferenced" }]));
        Assert.Null(getElemMethod.Invoke(keyFrame, [null!]));
        if (checkMissing)
        {
            Assert.Null(getElemMethod.Invoke(keyFrame, [created[elementCount - 1]]));
            Assert.Equal(12, (int)frameField.GetValue(elements["missing_bone"])!);
        }
    }

    [Fact]
    public void ApiPatcher_RewritesClientAnimatorGetMaxDepthAndAnimationKeyFrameResolveCleanly()
    {
        string vanillaApi = PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/VintagestoryAPI.dll");
        using var module = AssemblyDefinition.ReadAssembly(vanillaApi).MainModule;

        MethodReference getMaxDepthBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(
                nameof(OptimumApiBridge.ClientAnimatorGetMaxDepth),
                [typeof(List<ElementPose>), typeof(int)])!);
        MethodReference createDictBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(
                nameof(OptimumApiBridge.CreateKeyFrameElementDictionary),
                [typeof(int)])!);

        Assert.Equal(1, ApiPatcher.PatchClientAnimatorGetMaxDepth(module, getMaxDepthBridge));
        Assert.Equal(1, ApiPatcher.PatchAnimationKeyFrame(module, createDictBridge));
        // Second call verifies idempotency.
        Assert.Equal(1, ApiPatcher.PatchClientAnimatorGetMaxDepth(module, getMaxDepthBridge));
        Assert.Equal(1, ApiPatcher.PatchAnimationKeyFrame(module, createDictBridge));

        TypeDefinition clientAnimator = module.GetType("Vintagestory.API.Common.ClientAnimator");
        MethodDefinition getMaxDepth = clientAnimator.Methods.Single(m =>
            !m.IsStatic && m.Name == "getMaxDepth" && m.Parameters.Count == 2);
        Assert.Collection(
            getMaxDepth.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_2, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(getMaxDepthBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        TypeDefinition keyFrame = module.GetType("Vintagestory.API.Common.AnimationKeyFrame");
        MethodDefinition resolveDict = keyFrame.Methods.Single(m =>
            !m.IsStatic && m.Name == "Resolve" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name.StartsWith("Dictionary", StringComparison.Ordinal));
        Assert.Contains(resolveDict.Body.Instructions, ins =>
            ins.OpCode == OpCodes.Call &&
            ins.Operand is MethodReference mr &&
            mr.FullName == createDictBridge.FullName);
        Assert.DoesNotContain(resolveDict.Body.Instructions, ins =>
            ins.OpCode == OpCodes.Newobj &&
            ins.Operand is MethodReference ctor &&
            ctor.DeclaringType.FullName.StartsWith("Vintagestory.API.Datastructures.FastSmallDictionary", StringComparison.Ordinal));
    }
}
