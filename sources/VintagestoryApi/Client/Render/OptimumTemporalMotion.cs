using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;

#nullable disable

namespace Vintagestory.API.Client
{
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
    /// Per-instance previous transforms for the instanced motion writer (TAA P3).
    ///
    /// The mechanical-power renderers issue one draw per block shape and hand the
    /// GPU a per-instance <c>mat4 transform</c>, rebuilt from scratch every frame
    /// for every device in view. Slot order is the enumeration order of a
    /// dictionary that gains and loses entries as blocks are placed, broken and
    /// streamed in, so "the matrix that was in slot 3 last frame" is not this
    /// instance's previous transform - it is some other gear's. History is
    /// therefore keyed on the device object itself, per instance buffer, and a
    /// device that was not drawn into that same buffer in the previous frame gets
    /// no history at all (camera-only motion plus reactive 1) instead of another
    /// block's matrix.
    ///
    /// The previous transform travels to the shader the same way the current one
    /// does: as instance attributes in the very same interleaved buffer, four
    /// vec4s for the matrix and one vec4 of metadata. That is why the layout lives
    /// here rather than in each renderer - it is a contract with instanced.vsh,
    /// not a renderer detail. A mesh built without it (the cloth renderer shares
    /// the instanced program) reads the default attribute (0,0,0,1), whose x = 0
    /// is exactly "no history", on both backends.
    ///
    /// Render thread only.
    /// </summary>
    public static class OptimumInstanceMotion
    {
        /// <summary>Floats per instance: light rgba, transform, previous transform, TAA metadata.</summary>
        public const int InstanceFloats = 4 + 16 + 16 + 4;

        /// <summary>Where the light rgba starts within an instance, in floats.</summary>
        public const int LightOffset = 0;

        /// <summary>Where the current transform starts within an instance, in floats.</summary>
        public const int TransformOffset = 4;

        /// <summary>Where the previous transform starts within an instance, in floats.</summary>
        public const int PrevTransformOffset = 20;

        /// <summary>Where the TAA metadata vec4 starts within an instance, in floats.</summary>
        public const int MetaOffset = 36;

        private sealed class Entry
        {
            public float[] Prev = new float[16];
            public float[] Cur = new float[16];
            public EnumTemporalView PrevView;
            public EnumTemporalView CurView;
            public long CapturedFrame = -1;
            public long PreviousFrame = -1;
        }

        // Keyed on the instance buffer first, so a renderer that fills several
        // buffers for one device (the creative rotor's five sub-meshes, the
        // pulverizer's axle and pounders) keeps one history per sub-mesh; and on
        // the device object second, so it dies with the block entity behaviour.
        private static readonly ConditionalWeakTable<float[], ConditionalWeakTable<object, Entry>> buffers =
            new ConditionalWeakTable<float[], ConditionalWeakTable<object, Entry>>();

        private static object currentDevice;

        /// <summary>
        /// The instance-buffer layout instanced.vsh expects: rgbaLightIn at
        /// location 4, transform at 5..8, prevTransform at 9..12 and the TAA
        /// metadata at 13. Allocated whether or not TAA is on, because the shaders
        /// are recompiled when TAA is toggled but the instance buffers are not.
        /// </summary>
        /// <param name="instanceCapacity">How many instances the buffer must hold.</param>
        public static CustomMeshDataPartFloat CreateInstanceFloats(int instanceCapacity)
        {
            int floats = InstanceFloats * instanceCapacity;
            CustomMeshDataPartFloat part = new CustomMeshDataPartFloat(floats)
            {
                Instanced = true,
                InterleaveOffsets = new int[] { 0, 16, 32, 48, 64, 80, 96, 112, 128, 144 },
                InterleaveSizes = new int[] { 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 },
                InterleaveStride = InstanceFloats * 4,
                StaticDraw = false
            };
            part.SetAllocationSize(floats);
            return part;
        }

        /// <summary>
        /// Names the device whose instances are written next. Called once per
        /// device per frame by the renderer's buffer fill, before the transforms
        /// for that device reach any buffer.
        /// </summary>
        public static void NoteDevice(object device)
        {
            currentDevice = device;
        }

        /// <summary>
        /// Writes one instance: the light colour and this frame's transform as
        /// vanilla did, plus the same device's transform from the previous frame
        /// and the metadata that tells the shader whether to believe it.
        /// </summary>
        /// <param name="values">The instance buffer being filled.</param>
        /// <param name="index">The instance slot in that buffer.</param>
        /// <param name="lightRgba">The instance's light colour.</param>
        /// <param name="transform">This frame's 16-float transform.</param>
        public static void WriteInstance(float[] values, int index, Vec4f lightRgba, float[] transform)
        {
            if (values == null || transform == null || index < 0) return;

            int j = index * InstanceFloats;
            if (j + InstanceFloats > values.Length) return;

            values[j + LightOffset] = lightRgba.R;
            values[j + LightOffset + 1] = lightRgba.G;
            values[j + LightOffset + 2] = lightRgba.B;
            values[j + LightOffset + 3] = lightRgba.A;

            for (int i = 0; i < 16; i++)
            {
                values[j + TransformOffset + i] = transform[i];
            }

            bool valid = false;
            object device = currentDevice;

            if (OptimumEntityMotion.Enabled && device != null)
            {
                OptimumTemporalFrame frame = OptimumTemporal.Frame;
                EnumTemporalView view = frame.ActiveView;
                ConditionalWeakTable<object, Entry> entries =
                    buffers.GetValue(values, _ => new ConditionalWeakTable<object, Entry>());
                Entry entry = entries.GetValue(device, _ => new Entry());

                // One roll per frame, not per written instance: a device that
                // writes twice into the same buffer in one frame must both times
                // compare against the frame before, not against its own first write.
                if (entry.CapturedFrame != frame.FrameIndex)
                {
                    float[] swap = entry.Prev;
                    entry.Prev = entry.Cur;
                    entry.Cur = swap;
                    entry.PrevView = entry.CurView;
                    entry.PreviousFrame = entry.CapturedFrame;
                    entry.CapturedFrame = frame.FrameIndex;
                }

                for (int i = 0; i < 16; i++)
                {
                    entry.Cur[i] = transform[i];
                }
                entry.CurView = view;

                // The transform is camera-relative, so the previous one only means
                // anything together with the previous camera: the same device, drawn
                // into the same buffer, in the frame immediately before, under the
                // same view, in a frame that did not reset.
                valid =
                    !frame.Reset &&
                    entry.PreviousFrame == frame.FrameIndex - 1 &&
                    entry.PrevView == view &&
                    frame.WasViewCaptured(view);

                float[] previous = valid ? entry.Prev : entry.Cur;
                for (int i = 0; i < 16; i++)
                {
                    values[j + PrevTransformOffset + i] = previous[i];
                }
            }
            else
            {
                for (int i = 0; i < 16; i++)
                {
                    values[j + PrevTransformOffset + i] = transform[i];
                }
            }

            // x selects the branch in the vertex shader, y is the reactive value the
            // fragment writer stamps: a new or reordered instance has no history, so
            // the resolve is told to lean on this frame.
            values[j + MetaOffset] = valid ? 1f : 0f;
            values[j + MetaOffset + 1] = valid ? 0f : 1f;
            values[j + MetaOffset + 2] = 0f;
            values[j + MetaOffset + 3] = 0f;
        }

        /// <summary>
        /// Sets the per-pass half of the writer's uniforms: the previous camera and
        /// the shared warp/jitter/render-size block. Called once per frame by the
        /// renderer that owns the instanced program, after it has set this frame's
        /// projection and model-view matrices.
        /// </summary>
        public static void ApplyPassUniforms(IShaderProgram program)
        {
            if (!OptimumEntityMotion.Enabled || program == null) return;
            if (!program.HasUniform("prevProjectionMatrix")) return;

            OptimumTemporalFrame frame = OptimumTemporal.Frame;
            EnumTemporalView view = frame.ActiveView;

            program.UniformMatrix("prevProjectionMatrix", frame.GetPrevProjection(view));
            if (program.HasUniform("prevModelViewMatrix"))
            {
                program.UniformMatrix("prevModelViewMatrix", frame.PrevCameraMatrixOrigin);
            }
            frame.ApplyMotionUniforms(program);
        }
    }
}
