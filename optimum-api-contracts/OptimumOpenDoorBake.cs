using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

/// <summary>
/// Helpers for Issue #634: detects when a door, gate, trapdoor, or cabinet
/// opening animation has settled cleanly at its resting open pose, evaluates or
/// captures joint matrices, bakes static chunk meshes, and primes smooth rewind
/// or ease-out when closing from a baked open state.
/// </summary>
public static class OptimumOpenDoorBake
{
    public const string OpenAnimCode = "opened";
    public const float SettledEasingThreshold = 0.999f;
    private const float FrameEndEpsilon = 0.001f;

    public static bool CanBakeOpenAnimation(Animation? anim)
    {
        if (anim == null) return false;

        if (anim.OnAnimationEnd == EnumEntityAnimationEndHandling.Hold)
        {
            return true;
        }

        // Many trapdoors and crude doors declare onAnimationEnd="Repeat" with a single static pose keyframe.
        return anim.OnAnimationEnd == EnumEntityAnimationEndHandling.Repeat
            && (anim.KeyFrames == null || anim.KeyFrames.Length <= 1);
    }

    public static bool IsSingleSettledOpenAnimation(
        IAnimator? animator,
        IReadOnlyDictionary<string, AnimationMetaData>? activeAnimations)
    {
        if (!OptimumConfig.BakeOpenDoorsEnabled || animator == null || activeAnimations == null)
        {
            return false;
        }

        if (activeAnimations.Count != 1 || !activeAnimations.ContainsKey(OpenAnimCode))
        {
            return false;
        }

        if (animator.ActiveAnimationCount != 1)
        {
            return false;
        }

        RunningAnimation? anim = animator.GetAnimationState(OpenAnimCode);
        if (anim == null || !anim.Active || !CanBakeOpenAnimation(anim.Animation))
        {
            return false;
        }

        if (anim.EasingFactor < SettledEasingThreshold)
        {
            return false;
        }

        if (anim.Animation.OnAnimationEnd == EnumEntityAnimationEndHandling.Hold && anim.Animation.QuantityFrames > 1)
        {
            return anim.CurrentFrame >= anim.Animation.QuantityFrames - 1f - FrameEndEpsilon;
        }

        return true;
    }

    public static float[]? CaptureCurrentMatrices(IAnimator? animator)
    {
        float[]? src = animator?.Matrices;
        return src != null && src.Length >= 16 ? (float[])src.Clone() : null;
    }

    public static float[]? EvaluateSettledOpenPoseMatrices(IAnimator? animator, float easingSpeed)
    {
        if (animator == null) return null;

        var meta = new AnimationMetaData
        {
            Animation = OpenAnimCode,
            Code = OpenAnimCode,
            EaseInSpeed = easingSpeed,
            EaseOutSpeed = easingSpeed,
        }.Init();

        var tempActive = new Dictionary<string, AnimationMetaData>(1, StringComparer.OrdinalIgnoreCase)
        {
            [OpenAnimCode] = meta,
        };

        animator.OnFrame(tempActive, 0f);
        RunningAnimation? anim = animator.GetAnimationState(OpenAnimCode);
        if (anim == null || !CanBakeOpenAnimation(anim.Animation))
        {
            tempActive.Clear();
            anim?.Stop();
            animator.OnFrame(tempActive, 0f);
            return null;
        }

        ConfigureSettledOpenPose(anim);
        animator.OnFrame(tempActive, 0f);
        float[]? result = CaptureCurrentMatrices(animator);

        tempActive.Clear();
        anim.Stop();
        animator.OnFrame(tempActive, 0f);
        return result;
    }

    public static MeshData BakeJointPoseMesh(MeshData origMesh, float[]? jointMatrices)
    {
        ArgumentNullException.ThrowIfNull(origMesh);

        MeshData baked = origMesh.Clone();
        if (baked.Normals != null && baked.NormalsCount == 0 && origMesh.NormalsCount > 0)
        {
            baked.NormalsCount = Math.Min(origMesh.NormalsCount, baked.Normals.Length);
        }
        int[]? jointIds = origMesh.CustomInts?.Values;
        int jointCountInts = origMesh.CustomInts?.Count ?? 0;

        if (jointIds == null || jointMatrices == null || jointMatrices.Length < 16 || baked.VerticesCount <= 0)
        {
            baked.CustomInts = null;
            return baked;
        }

        int jointLimit = jointMatrices.Length / 16;
        float[]? xyz = baked.xyz;
        int[]? normals = baked.Normals;
        int[]? flags = baked.Flags;
        float[] vec4 = new float[4];
        float[] vec3 = new float[3];
        Vec4f packedNormalVec = new Vec4f();

        int vertexLimit = Math.Min(baked.VerticesCount, Math.Min(jointCountInts, xyz != null ? xyz.Length / 3 : 0));
        for (int v = 0; v < vertexLimit; v++)
        {
            int jointId = jointIds[v];
            if ((uint)jointId >= (uint)jointLimit) continue;

            Span<float> jointMat = jointMatrices.AsSpan(jointId * 16, 16);
            Mat4f.MulWithVec3_Position(jointMat, xyz!, xyz!, v * 3);

            if (normals != null && v < normals.Length)
            {
                NormalUtil.FromPackedNormal(normals[v], ref vec4);
                float origW = vec4[3];
                vec4[3] = 0f;
                Mat4f.MulWithVec4(jointMat, vec4, vec4);
                float nLen = GameMath.RootSumOfSquares(vec4[0], vec4[1], vec4[2]);
                if (nLen > 1e-6f)
                {
                    float inv = 1f / nLen;
                    packedNormalVec.Set(vec4[0] * inv, vec4[1] * inv, vec4[2] * inv, origW);
                }
                else
                {
                    packedNormalVec.Set(vec4[0], vec4[1], vec4[2], origW);
                }
                normals[v] = NormalUtil.PackNormal(packedNormalVec);
            }

            if (flags != null && v < flags.Length)
            {
                VertexFlags.UnpackNormal(flags[v], vec3);
                Mat4f.MulWithVec3(jointMat, vec3, vec3);
                float fLen = GameMath.RootSumOfSquares(vec3[0], vec3[1], vec3[2]);
                if (fLen > 1e-6f)
                {
                    float inv = 1f / fLen;
                    flags[v] = (flags[v] & ~VertexFlags.NormalBitMask)
                        | VertexFlags.PackNormal(vec3[0] * inv, vec3[1] * inv, vec3[2] * inv);
                }
            }
        }

        byte[]? xyzFaces = baked.XyzFaces;
        if (xyzFaces != null && baked.VerticesPerFace > 0)
        {
            int faceCount = Math.Min(baked.XyzFacesCount, xyzFaces.Length);
            for (int f = 0; f < faceCount; f++)
            {
                byte meshFaceIndex = xyzFaces[f];
                if (meshFaceIndex == 0 || meshFaceIndex > BlockFacing.ALLFACES.Length) continue;

                int vIdx = f * baked.VerticesPerFace;
                if ((uint)vIdx >= (uint)vertexLimit) continue;

                int jointId = jointIds[vIdx];
                if ((uint)jointId >= (uint)jointLimit) continue;

                Span<float> jointMat = jointMatrices.AsSpan(jointId * 16, 16);
                Vec3f normalfv = BlockFacing.ALLFACES[meshFaceIndex - 1].Normalf;
                xyzFaces[f] = Mat4f.MulWithVec3_BlockFacing(jointMat, normalfv).MeshDataIndex;
            }
        }

        baked.CustomInts = null;
        return baked;
    }

    public static void PrimeClosingFromSettledOpen(
        IAnimator? animator,
        Dictionary<string, AnimationMetaData>? activeAnimations,
        Action<AnimationMetaData>? startAnimation,
        Action<string>? stopAnimation,
        AnimationMetaData openedMeta)
    {
        if (openedMeta == null) return;

        startAnimation?.Invoke(openedMeta);

        if (animator != null && activeAnimations != null)
        {
            animator.OnFrame(activeAnimations, 0f);
            RunningAnimation? anim = animator.GetAnimationState(OpenAnimCode);
            if (anim != null && anim.Animation != null)
            {
                ConfigureSettledOpenPose(anim);
                animator.OnFrame(activeAnimations, 0f);
            }
        }

        stopAnimation?.Invoke(OpenAnimCode);
    }

    public static void StopSettledOpenAnimation(
        IAnimator? animator,
        Dictionary<string, AnimationMetaData>? activeAnimations,
        AnimatableRenderer? renderer)
    {
        activeAnimations?.Remove(OpenAnimCode);
        RunningAnimation? anim = animator?.GetAnimationState(OpenAnimCode);
        anim?.Stop();
        if (animator != null && activeAnimations != null)
        {
            animator.OnFrame(activeAnimations, 0f);
        }
        if (renderer != null)
        {
            renderer.ShouldRender = false;
        }
    }

    public static AnimationMetaData CreateOpenedMeta(float easingSpeed, float animationSpeed = 1f, float? easeOutSpeed = null)
    {
        return new AnimationMetaData
        {
            Animation = OpenAnimCode,
            Code = OpenAnimCode,
            AnimationSpeed = animationSpeed,
            EaseInSpeed = easingSpeed,
            EaseOutSpeed = easeOutSpeed ?? easingSpeed,
        }.Init();
    }

    public static MeshData? EnsureOpenedOrigMesh(
        object cacheLock,
        ref MeshData? cachedOpenedOrigMesh,
        MeshData? closedOrigMesh,
        IAnimator? animator,
        float easingSpeed)
    {
        MeshData? existing = Volatile.Read(ref cachedOpenedOrigMesh);
        if (existing != null)
        {
            return existing;
        }

        if (closedOrigMesh == null || animator == null || cacheLock == null)
        {
            return null;
        }

        lock (cacheLock)
        {
            existing = cachedOpenedOrigMesh;
            if (existing != null)
            {
                return existing;
            }

            float[]? jointMatrices = EvaluateSettledOpenPoseMatrices(animator, easingSpeed);
            if (jointMatrices == null)
            {
                return null;
            }

            MeshData baked = BakeJointPoseMesh(closedOrigMesh, jointMatrices);
            Volatile.Write(ref cachedOpenedOrigMesh, baked);
            return baked;
        }
    }

    public static MeshData TransformDoorInstanceMesh(MeshData origMesh, float rotateYRad, bool invertHandles)
    {
        ArgumentNullException.ThrowIfNull(origMesh);

        MeshData result = origMesh.Clone();
        if (result.Normals != null && result.NormalsCount == 0 && origMesh.NormalsCount > 0)
        {
            result.NormalsCount = Math.Min(origMesh.NormalsCount, result.Normals.Length);
        }
        if (rotateYRad != 0f)
        {
            float rot = invertHandles ? -rotateYRad : rotateYRad;
            result = result.Rotate(0f, rot, 0f);
        }

        if (invertHandles)
        {
            Matrixf matf = new Matrixf();
            matf.Translate(0.5f, 0.5f, 0.5f).Scale(-1f, 1f, 1f).Translate(-0.5f, -0.5f, -0.5f);
            result.MatrixTransform(matf.Values);
        }

        return result;
    }

    public static MeshData TransformTrapDoorInstanceMesh(MeshData origMesh, float[] tfMatrixValues)
    {
        ArgumentNullException.ThrowIfNull(origMesh);
        ArgumentNullException.ThrowIfNull(tfMatrixValues);

        MeshData result = origMesh.Clone();
        if (result.Normals != null && result.NormalsCount == 0 && origMesh.NormalsCount > 0)
        {
            result.NormalsCount = Math.Min(origMesh.NormalsCount, result.Normals.Length);
        }
        result.MatrixTransform(tfMatrixValues);
        return result;
    }

    private static void ConfigureSettledOpenPose(RunningAnimation anim)
    {
        anim.Active = true;
        anim.Running = true;
        anim.ShouldRewind = false;
        anim.ShouldPlayTillEnd = false;
        anim.NowPlaying = false;
        anim.Iterations = 1;
        anim.CurrentFrame = anim.Animation.QuantityFrames > 1 ? anim.Animation.QuantityFrames - 1 : 0;
        anim.EasingFactor = 1f;
        anim.BlendedWeight = 1f;
    }
}

/// <summary>
/// Per-instance controller shared across BEBehaviorDoor, BEBehaviorTrapDoor, and
/// BEBehaviorCabinetDoors so transition polling, chunk-upload handoff, closing
/// rewind priming, and runtime-toggle fallback share one implementation.
/// </summary>
public sealed class OptimumOpenDoorState
{
    private MeshData? _openedMesh;
    private bool _bakedOpen;
    private long _bakeListenerId;
    private int _fallbackQueued;

    public bool IsBakedOpen => Volatile.Read(ref _bakedOpen);
    public MeshData? BakedOpenedMesh => Volatile.Read(ref _openedMesh);

    public bool TryBakeOnLoad(
        ICoreAPI? api,
        bool opened,
        IAnimator? animator,
        Func<MeshData?> buildOpenedMesh)
    {
        if (!OptimumConfig.BakeOpenDoorsEnabled || !opened || api?.Side != EnumAppSide.Client || animator == null)
        {
            return false;
        }

        MeshData? baked = buildOpenedMesh();
        if (baked == null)
        {
            OptimumDiagnostics.RecordOpenDoorDynamicOrSkipped();
            return false;
        }

        Volatile.Write(ref _openedMesh, baked);
        Volatile.Write(ref _bakedOpen, true);
        OptimumDiagnostics.RecordOpenDoorBakedFromLoad();
        return true;
    }

    public void RefreshOpenedMeshIfBaked(Func<MeshData?> buildOpenedMesh)
    {
        if (Volatile.Read(ref _bakedOpen) && OptimumConfig.BakeOpenDoorsEnabled)
        {
            MeshData? refreshed = buildOpenedMesh();
            if (refreshed != null)
            {
                Volatile.Write(ref _openedMesh, refreshed);
            }
        }
    }

    public void CancelBakeCheck(BlockEntity? be)
    {
        if (_bakeListenerId != 0)
        {
            be?.UnregisterGameTickListener(_bakeListenerId);
            _bakeListenerId = 0;
        }
    }

    public void Reset(BlockEntity? be)
    {
        CancelBakeCheck(be);
        Volatile.Write(ref _bakedOpen, false);
        Volatile.Write(ref _openedMesh, null);
        Volatile.Write(ref _fallbackQueued, 0);
    }

    public void HandleToggle(
        BlockEntity? be,
        AnimationUtil? animUtil,
        bool opened,
        AnimationMetaData openedMeta,
        Func<bool> isStillOpened,
        Func<MeshData?> buildOpenedMesh)
    {
        if (animUtil == null) return;

        if (!opened)
        {
            CancelBakeCheck(be);
            if (Volatile.Read(ref _bakedOpen))
            {
                Volatile.Write(ref _bakedOpen, false);
                Volatile.Write(ref _openedMesh, null);
                if (be?.Api?.Side == EnumAppSide.Client && animUtil.animator != null && animUtil.renderer != null)
                {
                    OptimumOpenDoorBake.PrimeClosingFromSettledOpen(
                        animUtil.animator,
                        animUtil.activeAnimationsByAnimCode,
                        meta => animUtil.StartAnimation(meta),
                        code => animUtil.StopAnimation(code),
                        openedMeta);
                    OptimumDiagnostics.RecordOpenDoorReopened();
                    return;
                }
            }

            animUtil.StopAnimation(OptimumOpenDoorBake.OpenAnimCode);
            return;
        }

        if (Volatile.Read(ref _bakedOpen) && OptimumConfig.BakeOpenDoorsEnabled)
        {
            return;
        }

        Volatile.Write(ref _bakedOpen, false);
        Volatile.Write(ref _openedMesh, null);
        animUtil.StartAnimation(openedMeta);

        if (be?.Api?.Side == EnumAppSide.Client && OptimumConfig.BakeOpenDoorsEnabled)
        {
            ScheduleBakeCheck(be, animUtil, isStillOpened, buildOpenedMesh);
        }
        else if (be?.Api?.Side == EnumAppSide.Client)
        {
            OptimumDiagnostics.RecordOpenDoorDynamicOrSkipped();
        }
    }

    public bool TryGetStaticMeshForTesselation(
        ICoreAPI? api,
        out MeshData? bakedMesh,
        Action fallbackAction)
    {
        if (!Volatile.Read(ref _bakedOpen))
        {
            bakedMesh = null;
            return false;
        }

        if (OptimumConfig.BakeOpenDoorsEnabled)
        {
            bakedMesh = Volatile.Read(ref _openedMesh);
            return bakedMesh != null;
        }

        if (Interlocked.Exchange(ref _fallbackQueued, 1) == 0 && api is ICoreClientAPI capi && fallbackAction != null)
        {
            capi.Event.EnqueueMainThreadTask(fallbackAction, "optimum-open-door-fallback");
        }

        bakedMesh = null;
        return false;
    }

    public void ExecuteFallbackRestore(
        BlockEntity? be,
        AnimationUtil? animUtil,
        bool opened,
        AnimationMetaData openedMeta)
    {
        Volatile.Write(ref _fallbackQueued, 0);
        if (be?.Api == null || !Volatile.Read(ref _bakedOpen) || OptimumConfig.BakeOpenDoorsEnabled)
        {
            return;
        }

        Volatile.Write(ref _bakedOpen, false);
        Volatile.Write(ref _openedMesh, null);
        if (opened && animUtil != null)
        {
            animUtil.StartAnimation(openedMeta);
        }
        be.MarkDirty();
    }

    private void ScheduleBakeCheck(
        BlockEntity be,
        AnimationUtil animUtil,
        Func<bool> isStillOpened,
        Func<MeshData?> buildOpenedMesh)
    {
        if (_bakeListenerId != 0) return;

        _bakeListenerId = be.RegisterGameTickListener(_ =>
        {
            if (be.Api?.Side != EnumAppSide.Client || !isStillOpened() || !OptimumConfig.BakeOpenDoorsEnabled)
            {
                CancelBakeCheck(be);
                return;
            }

            if (!OptimumOpenDoorBake.IsSingleSettledOpenAnimation(animUtil.animator, animUtil.activeAnimationsByAnimCode))
            {
                return;
            }

            MeshData? baked = buildOpenedMesh();
            if (baked == null)
            {
                OptimumDiagnostics.RecordOpenDoorDynamicOrSkipped();
                CancelBakeCheck(be);
                return;
            }

            CancelBakeCheck(be);
            Volatile.Write(ref _openedMesh, baked);
            Volatile.Write(ref _bakedOpen, true);
            OptimumDiagnostics.RecordOpenDoorBakedFromTransition();

            be.Api.World.BlockAccessor.MarkBlockDirty(be.Pos, () =>
            {
                if (be.Api == null || !isStillOpened() || !Volatile.Read(ref _bakedOpen) || !OptimumConfig.BakeOpenDoorsEnabled)
                {
                    return;
                }

                OptimumOpenDoorBake.StopSettledOpenAnimation(
                    animUtil.animator,
                    animUtil.activeAnimationsByAnimCode,
                    animUtil.renderer);
            });
        }, 100);
    }
}
