using System;
using System.Runtime.CompilerServices;
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

        /// <summary>
        /// The view the projection matrix currently loaded belongs to: whatever
        /// <c>Set3DProjection</c> last recorded. A draw issued now is under this
        /// FOV, so its previous position has to go through the same view's
        /// previous projection - which is how the first-person hands, drawn under
        /// their own FOV between two <c>Set3DProjection</c> calls, are told apart
        /// from the world without every call site having to opt in.
        /// </summary>
        EnumTemporalView ActiveView { get; }

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

        /// <summary>See <see cref="IOptimumTemporalContext.ActiveView" />.</summary>
        public EnumTemporalView ActiveView { get; private set; }

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
            ActiveView = EnumTemporalView.World;
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
            ActiveView = view;
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

        /// <summary>
        /// Sets the uniforms every motion-vector writer shares (TAA P3): the render
        /// size and this frame's jitter, which turn a clip position into the pixel
        /// grid the resolve works in; the camera's own movement in double-differenced
        /// form; and the previous frame's complete warp state, so the writer can
        /// evaluate the vertex warp twice through the same code.
        ///
        /// The previous view and projection matrices are deliberately not set here:
        /// their uniform names differ per program (terrain has one modelViewMatrix,
        /// entities a separate view and model matrix) and so does the view they
        /// belong to (world FOV vs hand FOV). Each writer sets those two itself and
        /// calls this for the rest.
        ///
        /// Every uniform is guarded by <see cref="IShaderProgram.HasUniform" />:
        /// with TAA off the writers preprocess away, the names are not active, and
        /// setting one by name would throw.
        /// </summary>
        public void ApplyMotionUniforms(IShaderProgram program)
        {
            if (program == null) return;

            if (program.HasUniform("taaRenderSize")) program.Uniform("taaRenderSize", RenderWidth, RenderHeight);
            if (program.HasUniform("taaJitterPx")) program.Uniform("taaJitterPx", JitterPx.X, JitterPx.Y);
            if (program.HasUniform("cameraPosDelta")) program.Uniform("cameraPosDelta", CameraPosDelta.X, CameraPosDelta.Y, CameraPosDelta.Z);

            OptimumWarpState previous = PrevWarp;
            if (program.HasUniform("prevTimeCounter")) program.Uniform("prevTimeCounter", previous.TimeCounter);
            if (program.HasUniform("prevWindWaveCounter")) program.Uniform("prevWindWaveCounter", previous.WindWaveCounter);
            if (program.HasUniform("prevWindWaveCounterHighFreq")) program.Uniform("prevWindWaveCounterHighFreq", previous.WindWaveCounterHighFreq);
            if (program.HasUniform("prevWaterWaveCounter")) program.Uniform("prevWaterWaveCounter", previous.WaterWaveCounter);
            if (program.HasUniform("prevWindSpeed")) program.Uniform("prevWindSpeed", previous.WindSpeed);
            if (program.HasUniform("prevGlobalWarpIntensity")) program.Uniform("prevGlobalWarpIntensity", previous.GlobalWarpIntensity);
            if (program.HasUniform("prevGlitchWaviness")) program.Uniform("prevGlitchWaviness", previous.GlitchWaviness);
            if (program.HasUniform("prevWindWaveIntensity")) program.Uniform("prevWindWaveIntensity", previous.WindWaveIntensity);
            if (program.HasUniform("prevWaterWaveIntensity")) program.Uniform("prevWaterWaveIntensity", previous.WaterWaveIntensity);
            if (program.HasUniform("prevPerceptionEffectId")) program.Uniform("prevPerceptionEffectId", previous.PerceptionEffectId);
            if (program.HasUniform("prevPerceptionEffectIntensity")) program.Uniform("prevPerceptionEffectIntensity", previous.PerceptionEffectIntensity);
            if (program.HasUniform("prevPlayerpos")) program.Uniform("prevPlayerpos", PrevPlayerpos.X, PrevPlayerpos.Y, PrevPlayerpos.Z);
        }
    }

    /// <summary>
    /// The switch that lets a motion-writing pass into Primary's motion
    /// attachment, reachable from code that cannot see ClientPlatformWindows.
    ///
    /// The draw-buffer window itself lives in the platform layer (BeginMotionWrite /
    /// EndMotionWrite); the mod-side renderers that draw entity geometry of their
    /// own - the first-person hands and the echo chamber - are in assemblies that
    /// only see the API, so the platform installs its two delegates here once and
    /// they call through. A null hook (no platform, TAA off, headless tests) makes
    /// Begin report false, which is exactly what a caller does when the window
    /// could not be opened.
    /// </summary>
    public static class OptimumMotionWrite
    {
        /// <summary>Installed by ClientPlatformWindows. Render thread only.</summary>
        public static Func<bool> BeginHook;

        /// <summary>Installed by ClientPlatformWindows. Render thread only.</summary>
        public static Action EndHook;

        public static bool Begin()
        {
            Func<bool> hook = BeginHook;
            return hook != null && hook();
        }

        public static void End()
        {
            Action hook = EndHook;
            if (hook != null) hook();
        }
    }

    /// <summary>
    /// Per-entity previous transforms for the skinned-entity motion writer (TAA P3).
    ///
    /// Every draw that feeds entityanimated goes through one narrow gate: it sets
    /// <c>modelMatrix</c> and then uploads the animator's bone matrices into the
    /// "Animation" uniform block, immediately before the draw. The lib routes that
    /// upload here, which is why the first-person hands (their own program and FOV),
    /// the echo chamber (the shared program, three meshes, one pose) and any mod
    /// entity renderer all get motion vectors without each one growing its own
    /// history bookkeeping.
    ///
    /// History is keyed on the animator's own <c>Matrices</c> array, so identity is
    /// the thing that actually decides whether last frame's pose belongs to this
    /// entity: a respawned entity, a re-tesselated shape or a changed animator hands
    /// over a different array and gets no history, which is precisely when it must
    /// not have one. Entries die with the animator - the table holds no strong
    /// reference to it.
    ///
    /// Render thread only.
    /// </summary>
    public static class OptimumEntityMotion
    {
        /// <summary>
        /// Whether the motion writers are compiled into the shaders at all. Set by
        /// ShaderRegistry from the same value it stamps TAAMOTION with, so the
        /// per-uniform hooks below cost one static bool read when TAA is off.
        /// </summary>
        public static bool Enabled;

        private sealed class History
        {
            public float[] PrevBones = new float[0];
            public float[] CurBones = new float[0];
            public int PrevFloatCount = -1;
            public int CurFloatCount = -1;

            public readonly float[] PrevModelMatrix = new float[16];
            public readonly float[] CurModelMatrix = new float[16];

            public float PrevWindWaveIntensity = 1f;
            public float CurWindWaveIntensity = 1f;
            public float PrevWaterWaveCounter;
            public float CurWaterWaveCounter;

            public EnumTemporalView PrevView;
            public EnumTemporalView CurView;

            /// <summary>The frame CurBones et al. were captured in; -1 = never.</summary>
            public long CapturedFrame = -1;
            /// <summary>The frame PrevBones et al. were captured in; -1 = never.</summary>
            public long PreviousFrame = -1;
        }

        private static readonly ConditionalWeakTable<object, History> histories = new ConditionalWeakTable<object, History>();

        private static readonly float[] modelMatrixScratch = new float[16];
        private static float windWaveIntensityScratch = 1f;
        private static float waterWaveCounterScratch;

        /// <summary>
        /// The two per-draw warp uniforms as the draw in progress last set them.
        /// Shared with <see cref="OptimumStandardMotion" />: the standard shader's
        /// users override the very same two names (a swimming dropped item sets
        /// waterWaveCounter, an entity sets windWaveIntensity), and both writers
        /// read them through the one recorder in ShaderProgramBase rather than
        /// growing a second hook.
        /// </summary>
        internal static float ScratchWindWaveIntensity => windWaveIntensityScratch;

        /// <summary>See <see cref="ScratchWindWaveIntensity" />.</summary>
        internal static float ScratchWaterWaveCounter => waterWaveCounterScratch;

        private static IShaderProgram sharedUniformProgram;
        private static long sharedUniformFrame = -1;
        private static EnumTemporalView sharedUniformView;

        /// <summary>
        /// Remembers the model matrix a draw just set, so the upload that follows can
        /// store it as next frame's previous one. Called from ShaderProgramBase for
        /// the uniform named "modelMatrix" only.
        /// </summary>
        public static void NoteModelMatrix(float[] matrix)
        {
            if (matrix == null || matrix.Length < 16) return;
            Array.Copy(matrix, modelMatrixScratch, 16);
        }

        /// <summary>
        /// Remembers a warp uniform that varies per draw rather than per frame.
        /// EntityShapeRenderer overrides windWaveIntensity per entity and the echo
        /// chamber pins both to zero, so the previous frame's values for these two
        /// have to be stored per entity - the global PrevWarp would replay a warp the
        /// entity never had.
        /// </summary>
        public static void NoteWarpUniform(string uniformName, float value)
        {
            if (uniformName == "windWaveIntensity") windWaveIntensityScratch = value;
            else if (uniformName == "waterWaveCounter") waterWaveCounterScratch = value;
        }

        /// <summary>
        /// Called by the lib just before an entity's bone matrices reach the GPU.
        /// Uploads the same entity's previous pose into <paramref name="previousBones" />
        /// and sets the writer's per-draw uniforms, then records this draw's state
        /// as next frame's previous one.
        /// </summary>
        /// <param name="program">The program in use; must be an entityanimated motion writer.</param>
        /// <param name="previousBones">Its "AnimationPrev" uniform block.</param>
        /// <param name="boneMatrices">The array being uploaded into "Animation".</param>
        /// <param name="byteCount">How many bytes of it the draw uses.</param>
        public static void OnAnimationUpload(IShaderProgram program, UBORef previousBones, object boneMatrices, int byteCount)
        {
            if (!Enabled || program == null || previousBones == null || previousBones.Disposed) return;
            if (!program.HasUniform("taaHistoryValid")) return;

            float[] bones = boneMatrices as float[];
            if (bones == null || byteCount <= 0) return;

            int floats = byteCount / 4;
            if (floats <= 0 || floats > bones.Length) return;

            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            EnumTemporalView view = frame.ActiveView;
            History history = histories.GetValue(bones, _ => new History());

            // One roll per frame, not per draw: an entity drawn twice in a frame
            // (opaque then after-OIT) must both times compare against the frame
            // before, not against its own first draw.
            if (history.CapturedFrame != frame.FrameIndex)
            {
                float[] swap = history.PrevBones;
                history.PrevBones = history.CurBones;
                history.CurBones = swap;
                history.PrevFloatCount = history.CurFloatCount;
                Array.Copy(history.CurModelMatrix, history.PrevModelMatrix, 16);
                history.PrevWindWaveIntensity = history.CurWindWaveIntensity;
                history.PrevWaterWaveCounter = history.CurWaterWaveCounter;
                history.PrevView = history.CurView;
                history.PreviousFrame = history.CapturedFrame;
                history.CapturedFrame = frame.FrameIndex;
            }

            if (history.CurBones.Length < floats) history.CurBones = new float[floats];
            Array.Copy(bones, history.CurBones, floats);
            history.CurFloatCount = floats;
            Array.Copy(modelMatrixScratch, history.CurModelMatrix, 16);
            history.CurWindWaveIntensity = windWaveIntensityScratch;
            history.CurWaterWaveCounter = waterWaveCounterScratch;
            history.CurView = view;

            // Valid only if the very same entity was drawn last frame, under the same
            // view (a first/third person switch changes both the FOV and the mesh),
            // with the same joint count, and the frame itself did not reset.
            bool valid =
                !frame.Reset &&
                history.PreviousFrame == frame.FrameIndex - 1 &&
                history.PrevFloatCount == floats &&
                history.PrevView == view &&
                frame.WasViewCaptured(view);

            previousBones.Update(valid ? history.PrevBones : history.CurBones, 0, byteCount);

            // Per-frame, per-program half: the previous camera and the previous warp
            // state are the same for every entity in the pass.
            if (!ReferenceEquals(sharedUniformProgram, program) ||
                sharedUniformFrame != frame.FrameIndex ||
                sharedUniformView != view)
            {
                sharedUniformProgram = program;
                sharedUniformFrame = frame.FrameIndex;
                sharedUniformView = view;
                if (program.HasUniform("prevProjectionMatrix"))
                {
                    program.UniformMatrix("prevProjectionMatrix", frame.GetPrevProjection(view));
                }
                if (program.HasUniform("prevViewMatrix"))
                {
                    program.UniformMatrix("prevViewMatrix", frame.PrevCameraMatrixOrigin);
                }
                frame.ApplyMotionUniforms(program);
            }

            // Per-draw half.
            if (program.HasUniform("prevModelMatrix"))
            {
                program.UniformMatrix("prevModelMatrix", valid ? history.PrevModelMatrix : history.CurModelMatrix);
            }
            program.Uniform("taaHistoryValid", valid ? 1 : 0);
            if (program.HasUniform("taaReactive")) program.Uniform("taaReactive", valid ? 0f : 1f);
            if (program.HasUniform("prevWindWaveIntensity"))
            {
                program.Uniform("prevWindWaveIntensity", valid ? history.PrevWindWaveIntensity : history.CurWindWaveIntensity);
            }
            if (program.HasUniform("prevWaterWaveCounter"))
            {
                program.Uniform("prevWaterWaveCounter", valid ? history.PrevWaterWaveCounter : history.CurWaterWaveCounter);
            }
        }
    }

    /// <summary>
    /// Per-object previous transforms for the standard-shader motion writer (TAA P3).
    ///
    /// The standard shader has no bone upload to hang the history off, so its users
    /// name themselves: a renderer calls <see cref="Apply" /> after it has set this
    /// draw's <c>modelMatrix</c> and before it draws, passing a stable identity
    /// object and the mesh it is about to render. Held items key on the attachment
    /// point pose (one per hand, replaced when the animator changes), dropped items
    /// and block-entity renderers on the renderer instance itself, which is exactly
    /// as long-lived as the thing it draws. Nothing is stored on the mod-fork types,
    /// so no new fields have to be transplanted into the installed runtime.
    ///
    /// A draw that never calls this is not instrumented at all - and, because the
    /// motion attachment is only in the draw-buffer mask while a writer holds the
    /// window open, it also writes nothing, so the resolve falls back to camera
    /// reprojection for it rather than reprojecting it by a stale vector.
    ///
    /// Render thread only.
    /// </summary>
    public static class OptimumStandardMotion
    {
        private sealed class History
        {
            public readonly float[] PrevModelMatrix = new float[16];
            public readonly float[] CurModelMatrix = new float[16];

            /// <summary>The mesh drawn last frame; a different one means a different shape.</summary>
            public object PrevShape;
            public object CurShape;

            public float PrevWindWaveIntensity = 1f;
            public float CurWindWaveIntensity = 1f;
            public float PrevWaterWaveCounter;
            public float CurWaterWaveCounter;

            public EnumTemporalView PrevView;
            public EnumTemporalView CurView;

            public long CapturedFrame = -1;
            public long PreviousFrame = -1;
        }

        private static readonly ConditionalWeakTable<object, History> histories = new ConditionalWeakTable<object, History>();

        private static IShaderProgram sharedUniformProgram;
        private static long sharedUniformFrame = -1;
        private static EnumTemporalView sharedUniformView;

        /// <summary>
        /// Feeds one standard-shader draw's previous transform to the writer.
        /// </summary>
        /// <param name="program">The standard-shader program in use, already active.</param>
        /// <param name="identity">A stable object that means "this drawn thing".</param>
        /// <param name="shape">The mesh about to be drawn; history is void when it changed.</param>
        /// <param name="modelMatrix">The model matrix this draw set, 16 floats.</param>
        /// <returns>Whether the writer got a usable previous transform.</returns>
        public static bool Apply(IShaderProgram program, object identity, object shape, float[] modelMatrix)
        {
            if (!OptimumEntityMotion.Enabled || program == null || identity == null) return false;
            if (modelMatrix == null || modelMatrix.Length < 16) return false;
            if (!program.HasUniform("taaHistoryValid")) return false;

            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            EnumTemporalView view = frame.ActiveView;
            History history = histories.GetValue(identity, _ => new History());

            // One roll per frame, not per draw: something drawn twice in a frame must
            // both times compare against the frame before, not against its own first draw.
            if (history.CapturedFrame != frame.FrameIndex)
            {
                Array.Copy(history.CurModelMatrix, history.PrevModelMatrix, 16);
                history.PrevShape = history.CurShape;
                history.PrevWindWaveIntensity = history.CurWindWaveIntensity;
                history.PrevWaterWaveCounter = history.CurWaterWaveCounter;
                history.PrevView = history.CurView;
                history.PreviousFrame = history.CapturedFrame;
                history.CapturedFrame = frame.FrameIndex;
            }

            Array.Copy(modelMatrix, history.CurModelMatrix, 16);
            history.CurShape = shape;
            history.CurWindWaveIntensity = OptimumEntityMotion.ScratchWindWaveIntensity;
            history.CurWaterWaveCounter = OptimumEntityMotion.ScratchWaterWaveCounter;
            history.CurView = view;

            bool valid =
                !frame.Reset &&
                history.PreviousFrame == frame.FrameIndex - 1 &&
                ReferenceEquals(history.PrevShape, shape) &&
                history.PrevView == view &&
                frame.WasViewCaptured(view);

            // Per-frame, per-program half: the previous camera and the previous global
            // warp state are the same for every draw the pass makes.
            if (!ReferenceEquals(sharedUniformProgram, program) ||
                sharedUniformFrame != frame.FrameIndex ||
                sharedUniformView != view)
            {
                sharedUniformProgram = program;
                sharedUniformFrame = frame.FrameIndex;
                sharedUniformView = view;
                if (program.HasUniform("prevProjectionMatrix"))
                {
                    program.UniformMatrix("prevProjectionMatrix", frame.GetPrevProjection(view));
                }
                if (program.HasUniform("prevViewMatrix"))
                {
                    program.UniformMatrix("prevViewMatrix", frame.PrevCameraMatrixOrigin);
                }
                frame.ApplyMotionUniforms(program);
            }

            if (program.HasUniform("prevModelMatrix"))
            {
                program.UniformMatrix("prevModelMatrix", valid ? history.PrevModelMatrix : history.CurModelMatrix);
            }
            program.Uniform("taaHistoryValid", valid ? 1 : 0);
            if (program.HasUniform("taaReactive")) program.Uniform("taaReactive", valid ? 0f : 1f);
            if (program.HasUniform("prevWindWaveIntensity"))
            {
                program.Uniform("prevWindWaveIntensity", valid ? history.PrevWindWaveIntensity : history.CurWindWaveIntensity);
            }
            if (program.HasUniform("prevWaterWaveCounter"))
            {
                program.Uniform("prevWaterWaveCounter", valid ? history.PrevWaterWaveCounter : history.CurWaterWaveCounter);
            }

            return valid;
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
