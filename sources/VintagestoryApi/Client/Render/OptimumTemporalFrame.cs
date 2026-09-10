using System;
using Vintagestory.API.MathTools;

#nullable disable

namespace Vintagestory.API.Client
{
    /// <summary>
    /// Why the temporal history is invalid for a frame. Every consumer of the
    /// temporal frame contract (the in-house TAA resolve first, vendor upscalers
    /// and frame generators later) needs the same "throw the history away" signal,
    /// and needs to know why, because the remedies differ: a resize reallocates
    /// targets, a teleport only clears colour.
    /// </summary>
    public enum EnumTemporalResetReason
    {
        None = 0,
        WorldLoad,
        Dimension,
        Teleport,
        Rebase,
        Resize,
        ShaderReload,
        FovChange,
        RenderScale,
        Toggle,
        Screenshot
    }

    /// <summary>
    /// The camera views the temporal contract captures. Each one has its own
    /// projection matrix and its own previous projection, because a motion vector
    /// written by a draw under the hand FOV must be reprojected through the hand
    /// FOV of the previous frame, not the world one.
    /// </summary>
    public enum EnumTemporalView
    {
        /// <summary>The world FOV set by <c>Reset3DProjection</c>.</summary>
        World = 0,
        /// <summary>The first-person hand FOV (EntityPlayerShapeRenderer's HandRenderFov).</summary>
        Hand = 1
    }

    /// <summary>
    /// Every uniform the vertex-warp functions read, snapshotted so a motion-vector
    /// writer can evaluate the warp twice: once with this frame's state and once
    /// with the previous frame's. The counters wrap (DefaultShaderUniforms.Update
    /// takes them modulo 6000), so the previous value must be stored rather than
    /// derived from the current value minus a delta.
    /// </summary>
    public struct OptimumWarpState
    {
        public float TimeCounter;
        public float WindWaveCounter;
        public float WindWaveCounterHighFreq;
        public float WaterWaveCounter;
        public float WindSpeed;
        /// <summary>DefaultShaderUniforms.GlobalWorldWarp.</summary>
        public float GlobalWarpIntensity;
        public float GlitchWaviness;
        public float WindWaveIntensity;
        public float WaterWaveIntensity;
        public int PerceptionEffectId;
        public float PerceptionEffectIntensity;

        public static OptimumWarpState FromUniforms(DefaultShaderUniforms u)
        {
            OptimumWarpState state = default(OptimumWarpState);
            if (u == null) return state;

            state.TimeCounter = u.TimeCounter;
            state.WindWaveCounter = u.WindWaveCounter;
            state.WindWaveCounterHighFreq = u.WindWaveCounterHighFreq;
            state.WaterWaveCounter = u.WaterWaveCounter;
            state.WindSpeed = u.WindSpeed;
            state.GlobalWarpIntensity = u.GlobalWorldWarp;
            state.GlitchWaviness = u.GlitchWaviness;
            state.WindWaveIntensity = u.WindWaveIntensity;
            state.WaterWaveIntensity = u.WaterWaveIntensity;
            state.PerceptionEffectId = u.PerceptionEffectId;
            state.PerceptionEffectIntensity = u.PerceptionEffectIntensity;
            return state;
        }
    }

    /// <summary>
    /// Read-only view of the current temporal frame.
    ///
    /// Deliberately a companion interface rather than new members on IRenderAPI:
    /// every mod that implements IRenderAPI would break if the interface grew, and
    /// the contract is expected to keep growing as upscalers and frame generation
    /// land. Consumers reach it through <see cref="OptimumTemporal.Context" />.
    ///
    /// The float[16] matrices are the live per-frame arrays, not copies; treat them
    /// as read-only and copy before keeping them past the frame.
    /// </summary>
    public interface IOptimumTemporalContext
    {
        /// <summary>Increments once per real rendered frame. Generated frames get their own id later.</summary>
        long FrameIndex { get; }

        /// <summary>True while the jitter window is open: from Advance() until the resolve has run.</summary>
        bool JitterActive { get; }

        /// <summary>The sub-pixel offset actually applied this frame, in render pixels. Zero while the window is closed.</summary>
        Vec2f JitterPx { get; }

        /// <summary>The previous frame's applied jitter, in render pixels.</summary>
        Vec2f PrevJitterPx { get; }

        /// <summary>The Halton offset for this frame index, whether or not it is applied.</summary>
        Vec2f JitterSequencePx { get; }

        int RenderWidth { get; }
        int RenderHeight { get; }

        /// <summary>The unjittered projection last loaded for the given view this frame.</summary>
        float[] GetProjection(EnumTemporalView view);
        /// <summary>The unjittered projection that view had in the previous frame.</summary>
        float[] GetPrevProjection(EnumTemporalView view);
        /// <summary>Whether the view was actually set up this frame (the hand view is absent in third person).</summary>
        bool IsViewCaptured(EnumTemporalView view);

        float[] CameraMatrix { get; }
        float[] PrevCameraMatrix { get; }
        float[] CameraMatrixOrigin { get; }
        float[] PrevCameraMatrixOrigin { get; }

        /// <summary>Current minus previous EntityPlayer.CameraPos, differenced in double precision.</summary>
        Vec3f CameraPosDelta { get; }

        /// <summary>DefaultShaderUniforms.PlayerPos: the camera relative to the slowly rebased reference position.</summary>
        Vec3f Playerpos { get; }
        Vec3f PrevPlayerpos { get; }

        OptimumWarpState Warp { get; }
        OptimumWarpState PrevWarp { get; }

        bool Reset { get; }
        EnumTemporalResetReason ResetReason { get; }

        float ZNear { get; }
        float ZFar { get; }
        float Fov { get; }
        float DeltaTimeMs { get; }
    }

    /// <summary>
    /// The per-frame temporal contract: one engine-owned superset of the data every
    /// temporal consumer needs (jitter, camera constants and their previous values,
    /// warp state, reset reason), rotated once per frame by <see cref="Advance" />.
    ///
    /// A single mutable instance rather than a fresh record per frame: it lives on
    /// the render thread only and is read by shader-uniform setters in the hot path,
    /// so it must not allocate per frame.
    /// </summary>
    public sealed class OptimumTemporalFrame : IOptimumTemporalContext
    {
        /// <summary>Camera movement above this many blocks in one frame is a teleport, not motion.</summary>
        public const double TeleportThresholdBlocks = 8.0;

        private const int ViewCount = 2;

        private readonly float[][] projection = new float[ViewCount][];
        private readonly float[][] projectionPrev = new float[ViewCount][];
        private readonly bool[] viewCaptured = new bool[ViewCount];
        private readonly bool[] viewCapturedPrev = new bool[ViewCount];

        private readonly float[] cameraMatrix = new float[16];
        private readonly float[] cameraMatrixPrev = new float[16];
        private readonly float[] cameraMatrixOrigin = new float[16];
        private readonly float[] cameraMatrixOriginPrev = new float[16];

        /// <summary>
        /// Scratch for the jittered projection handed out by
        /// ClientMain.CurrentProjectionMatrix. Owned here so ClientMain needs no
        /// injected field, and separate from ClientMain's shared tmpMatrix so a
        /// caller that reads the modelview matrix in between does not overwrite it.
        /// </summary>
        private readonly float[] jitteredScratch = new float[16];

        private readonly Vec3d cameraPos = new Vec3d();
        private readonly Vec3d cameraPosPrev = new Vec3d();
        private readonly Vec3d referencePos = new Vec3d();
        private bool hasCameraPos;
        private bool hasReferencePos;

        private EnumTemporalResetReason pendingReset;
        private bool jitterActive;

        public OptimumTemporalFrame()
        {
            for (int i = 0; i < ViewCount; i++)
            {
                projection[i] = new float[16];
                projectionPrev[i] = new float[16];
            }
        }

        public long FrameIndex { get; private set; }

        public Vec2f JitterPx { get; } = new Vec2f();
        public Vec2f PrevJitterPx { get; } = new Vec2f();
        public Vec2f JitterSequencePx { get; } = new Vec2f();

        /// <summary>
        /// Opens and closes the jitter window. Setting it also updates
        /// <see cref="JitterPx" />, so the contract always reports the offset that
        /// was really applied: closed window means (0,0), which is what a motion
        /// vector or a reprojection has to assume.
        /// </summary>
        public bool JitterActive
        {
            get { return jitterActive; }
            set
            {
                jitterActive = value;
                JitterPx.X = value ? JitterSequencePx.X : 0f;
                JitterPx.Y = value ? JitterSequencePx.Y : 0f;
            }
        }

        public int RenderWidth { get; private set; }
        public int RenderHeight { get; private set; }

        public float[] CameraMatrix => cameraMatrix;
        public float[] PrevCameraMatrix => cameraMatrixPrev;
        public float[] CameraMatrixOrigin => cameraMatrixOrigin;
        public float[] PrevCameraMatrixOrigin => cameraMatrixOriginPrev;

        public Vec3f CameraPosDelta { get; } = new Vec3f();

        public Vec3f Playerpos { get; } = new Vec3f();
        public Vec3f PrevPlayerpos { get; } = new Vec3f();

        public OptimumWarpState Warp { get; private set; }
        public OptimumWarpState PrevWarp { get; private set; }

        public bool Reset { get; private set; }
        public EnumTemporalResetReason ResetReason { get; private set; }

        public float ZNear { get; private set; }
        public float ZFar { get; private set; }
        public float Fov { get; private set; }
        public float DeltaTimeMs { get; private set; }

        public float[] GetProjection(EnumTemporalView view) => projection[(int)view];
        public float[] GetPrevProjection(EnumTemporalView view) => projectionPrev[(int)view];
        public bool IsViewCaptured(EnumTemporalView view) => viewCaptured[(int)view];
        public bool WasViewCaptured(EnumTemporalView view) => viewCapturedPrev[(int)view];

        /// <summary>
        /// Marks the history invalid for the next frame. Safe to call several times
        /// before the next <see cref="Advance" />; the first non-None reason wins,
        /// so the earliest cause is the one reported.
        /// </summary>
        public void RequestReset(EnumTemporalResetReason reason)
        {
            if (reason == EnumTemporalResetReason.None) return;
            if (pendingReset == EnumTemporalResetReason.None) pendingReset = reason;
        }

        /// <summary>
        /// Rotates current-to-previous, increments the frame index, computes this
        /// frame's Halton jitter and decides whether the history survives. Called
        /// once per frame, immediately after DefaultShaderUniforms.Update and before
        /// the Before render stage, so every pass in the frame sees one consistent
        /// snapshot.
        /// </summary>
        /// <param name="renderScale">Optimum's render scale (1 = native). The jitter
        /// sequence gets more phases the more the image is upscaled.</param>
        /// <param name="cameraPosIn">EntityPlayer.CameraPos, differenced in double
        /// precision. May be null before a world is loaded.</param>
        public void Advance(
            float deltaTimeMs,
            int renderWidth,
            int renderHeight,
            float renderScale,
            float zNear,
            float zFar,
            float fov,
            Vec3d cameraPosIn,
            DefaultShaderUniforms uniforms)
        {
            // --- rotate current -> previous -------------------------------------
            PrevJitterPx.X = JitterPx.X;
            PrevJitterPx.Y = JitterPx.Y;
            for (int i = 0; i < ViewCount; i++)
            {
                Array.Copy(projection[i], projectionPrev[i], 16);
                viewCapturedPrev[i] = viewCaptured[i];
                viewCaptured[i] = false;
            }
            Array.Copy(cameraMatrix, cameraMatrixPrev, 16);
            Array.Copy(cameraMatrixOrigin, cameraMatrixOriginPrev, 16);
            PrevPlayerpos.Set(Playerpos.X, Playerpos.Y, Playerpos.Z);
            PrevWarp = Warp;

            FrameIndex++;

            // --- this frame's constants -----------------------------------------
            DeltaTimeMs = deltaTimeMs;
            ZNear = zNear;
            ZFar = zFar;
            Fov = fov;
            Warp = OptimumWarpState.FromUniforms(uniforms);
            if (uniforms != null && uniforms.PlayerPos != null)
            {
                Playerpos.Set(uniforms.PlayerPos.X, uniforms.PlayerPos.Y, uniforms.PlayerPos.Z);
            }

            EnumTemporalResetReason reason = pendingReset;
            pendingReset = EnumTemporalResetReason.None;

            int prevWidth = RenderWidth;
            int prevHeight = RenderHeight;
            RenderWidth = Math.Max(1, renderWidth);
            RenderHeight = Math.Max(1, renderHeight);
            if (prevWidth != 0 && (prevWidth != RenderWidth || prevHeight != RenderHeight) &&
                reason == EnumTemporalResetReason.None)
            {
                reason = EnumTemporalResetReason.Resize;
            }

            // --- camera position delta, teleport detection -----------------------
            if (cameraPosIn != null)
            {
                if (hasCameraPos)
                {
                    cameraPosPrev.Set(cameraPos);
                    double dx = cameraPosIn.X - cameraPosPrev.X;
                    double dy = cameraPosIn.Y - cameraPosPrev.Y;
                    double dz = cameraPosIn.Z - cameraPosPrev.Z;
                    CameraPosDelta.Set((float)dx, (float)dy, (float)dz);
                    if (dx * dx + dy * dy + dz * dz > TeleportThresholdBlocks * TeleportThresholdBlocks)
                    {
                        reason = EnumTemporalResetReason.Teleport;
                    }
                }
                else
                {
                    cameraPosPrev.Set(cameraPosIn);
                    CameraPosDelta.Set(0f, 0f, 0f);
                    hasCameraPos = true;
                }
                cameraPos.Set(cameraPosIn);
            }
            else
            {
                CameraPosDelta.Set(0f, 0f, 0f);
                hasCameraPos = false;
            }

            // --- reference-position rebase ---------------------------------------
            Vec3d reference = uniforms?.playerReferencePos;
            if (reference != null)
            {
                if (hasReferencePos)
                {
                    if (reference.X != referencePos.X || reference.Y != referencePos.Y || reference.Z != referencePos.Z)
                    {
                        if (reason == EnumTemporalResetReason.None) reason = EnumTemporalResetReason.Rebase;
                    }
                }
                else hasReferencePos = true;
                referencePos.Set(reference);
            }
            else hasReferencePos = false;

            ResetReason = reason;
            Reset = reason != EnumTemporalResetReason.None;
            if (Reset) CameraPosDelta.Set(0f, 0f, 0f);

            // --- jitter ----------------------------------------------------------
            int phaseCount = Math.Max(1, OptimumTemporalMath.JitterPhaseCount(renderScale > 0f ? 1f / renderScale : 1f));
            int phase = (int)(FrameIndex % phaseCount);
            double jx = OptimumTemporalMath.Halton(phase + 1, 2) - 0.5;
            double jy = OptimumTemporalMath.Halton(phase + 1, 3) - 0.5;
            // Halton(2,3) never lands on (0.5, 0.5), but a zero offset would make a
            // frame contribute no new sub-pixel sample at all, so it is excluded by
            // construction rather than by luck.
            if (jx == 0.0 && jy == 0.0) jx = 0.25;
            JitterSequencePx.X = (float)jx;
            JitterSequencePx.Y = (float)jy;
            JitterPx.X = jitterActive ? JitterSequencePx.X : 0f;
            JitterPx.Y = jitterActive ? JitterSequencePx.Y : 0f;
        }

        /// <summary>
        /// Records the unjittered projection matrix a Set3DProjection call just
        /// loaded, for the view it belongs to.
        /// </summary>
        public void RecordProjection(EnumTemporalView view, double[] matrix)
        {
            if (matrix == null) return;
            float[] dest = projection[(int)view];
            for (int i = 0; i < 16; i++) dest[i] = (float)matrix[i];
            viewCaptured[(int)view] = true;
        }

        /// <summary>
        /// Freezes the camera matrices for the frame: the entity view (camera at the
        /// player) and the terrain view (camera at the chunk-relative origin).
        /// </summary>
        public void CaptureCamera(double[] cameraMatrixIn, double[] cameraMatrixOriginIn)
        {
            if (cameraMatrixIn != null)
            {
                for (int i = 0; i < 16; i++) cameraMatrix[i] = (float)cameraMatrixIn[i];
            }
            if (cameraMatrixOriginIn != null)
            {
                for (int i = 0; i < 16; i++) cameraMatrixOrigin[i] = (float)cameraMatrixOriginIn[i];
            }
        }

        /// <summary>
        /// Copies a projection matrix into this frame's scratch array and shears it
        /// by the current jitter. The shear matches Mat4d.Perspective's convention
        /// (clip.w = -z_view), so a static point moves by exactly JitterPx pixels.
        /// </summary>
        public float[] ApplyJitterCopy(double[] matrix)
        {
            for (int i = 0; i < 16; i++) jitteredScratch[i] = (float)matrix[i];
            jitteredScratch[8] -= (float)(2.0 * JitterPx.X / RenderWidth);
            jitteredScratch[9] -= (float)(2.0 * JitterPx.Y / RenderHeight);
            return jitteredScratch;
        }
    }

    /// <summary>
    /// The process-wide holder of the temporal frame contract. Static because the
    /// producers are scattered across the render loop, the platform layer and the
    /// mod forks, and none of them share an object that already reaches all of them.
    /// Render-thread only.
    /// </summary>
    public static class OptimumTemporal
    {
        public static readonly OptimumTemporalFrame Frame = new OptimumTemporalFrame();

        /// <summary>Read-only access for consumers that must not mutate the frame.</summary>
        public static IOptimumTemporalContext Context => Frame;

        public static void RequestReset(EnumTemporalResetReason reason) => Frame.RequestReset(reason);
    }
}
