using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan.md), Phase 3b decision 5
// stage 2: the entity system on the native device API.
//
// What it draws: every entity's animated shape - the body through entityanimated in the Opaque
// stage (SystemRenderEntities.OnRenderOpaque3D's batched loop) and the same body through
// shadowmapentityanimated in the two shadow stages (SystemRenderEntities.OnRenderFrameShadows).
// Both reach here one sub-mesh at a time through ClientPlatformAbstract.RenderEntityMesh, the
// seam RenderAPIBase.RenderMultiTextureMesh draws through.
// Where the other side is: the seam's neutral body, which is the RenderMesh(MeshRef) call it
// replaced and which the OpenGL path still runs (ClientPlatformWindows.RenderMesh ->
// GL.DrawElements). NativeEntitiesEnabled false takes that route on the Vulkan device too,
// which is what the differential tests compare against.
// Target and slots: whatever the stage bound. Opaque -> Primary, with every bound colour slot
// in scope; shadow -> FrameBuffers[11]/[12], which have no colour attachment at all.
// State that is not obvious:
//   - the motion attachment is a colour WRITE MASK here, never a draw-buffer toggle (decision 4).
//     OptimumMotionWriteActive is the platform's own window state, so the pipeline masks the
//     motion slot off when the window is shut and gives it replace-blend (ONE, ZERO, FUNC_ADD)
//     when it is open - exactly what ClientPlatformWindows.ApplyOptimumMotionBlendState does.
//     A slot past the motion attachment is masked off as well, so nothing Vulkan leaves
//     undefined reaches an attachment GL would have kept (rule 9).
//   - the SSAO G-buffer slots (2 and 3, present only when Primary carries them) take replace-blend
//     too, which is the branch GlToggleBlend takes under RenderSSAO;
//   - colour 0 and the glow slot 1 take the standard alpha blend GlToggleBlend(true) sets, because
//     the caller turned blending on before the loop;
//   - cull off, depth test on, depth write on, compare LESS: what SystemRenderEntities sets
//     immediately before both loops (GlDisableCullFace / GlEnableDepthTest / GlToggleBlend(true))
//     and what ChunkRenderer left for the shadow stage, stated outright rather than read back
//     out of the tracker (decision 3);
//   - the draw is recorded INSIDE the stage's own declared pass, not a pass of its own: it names
//     BoundPassName() with every slot, so RenderTargetManager.DeclarePass coalesces and
//     EndNativePass(keepScope: true) leaves the scope open. One entity per rendering scope would
//     otherwise cost an end/begin pair per entity per frame.
//   - the bone matrices are NOT re-uploaded here. EntityShapeRenderer keeps calling
//     UBO.Update("Animation", ...), which lands in the device's animation storage ring with its
//     per-(frame, version) snapshot dedup; the native draw passes the real mesh id into
//     BindProgramSets, which is what makes the ring's snapshot resolve for this draw.
// What it deliberately does NOT take native, and why:
//   - held items through the `standard` program (EntityShapeRenderer.RenderItem). Their cull mode
//     is decided per draw by renderInfo.CullFaces inside the VSEssentials fork, through
//     GlDisableCullFace/GlEnableCullFace, and no seam carries it; a native pipeline would have to
//     read it back off the tracker, which decision 3 forbids. It needs a seam in the fork, which
//     is that fork's own change.
//   - the `instanced` program: ShaderPrograms.Instanced has no vanilla call site in this tree
//     (registration and manifest only), so there is nothing to port. The entity renderers'
//     instanced draws go through RenderMeshInstanced with mod-registered shaders.
// What pins it: NativeEntityDrawTests (old route against native route, colour and the motion
// attachment) and Optimum.Tests/native-world-systems-coverage-tests.cs (the lib seam).
public partial class VulkanClientPlatform
{
    /// <summary>
    /// False runs the seam's neutral body - the OpenGL body's RenderMesh - on the Vulkan device
    /// instead of the native pass: the old route the differential tests compare against, in the
    /// pattern of <see cref="NativeBlitEnabled" /> and <see cref="NativeSkyEnabled" />.
    /// </summary>
    // On by default; OPTIMUM_VK_NATIVE_ENTITIES=0 sends every entity draw to the neutral body.
    internal bool NativeEntitiesEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_ENTITIES") != "0";

    /// <summary>The programs this file owns. Anything else takes the seam's neutral body.</summary>
    private const string EntityAnimatedPass = "entityanimated";

    private const string EntityShadowPass = "shadowmapentityanimated";

    /// <summary>
    /// The texture the client declared for a sampler, or 0 - which resolves to the placeholder.
    /// The table itself is <see cref="nativeProgramTextures" /> in
    /// VulkanClientPlatform.NativeChunks.cs, filled by NoteNativeProgramTexture from
    /// BindProgramTexture2D/Cube - the client saying "this program's sampler is this texture".
    /// A program whose draws are all native never runs the emulated resolve that fills the push
    /// block's slots from the texture units, so the native draw has to resolve every sampler the
    /// program declares, not only the one the seam names.
    /// </summary>
    internal int DeclaredProgramTexture(int programId, string samplerName) =>
        nativeProgramTextures.TryGetValue(programId, out Dictionary<string, int>? samplers) &&
        samplers.TryGetValue(samplerName, out int textureId)
            ? textureId
            : 0;

    /// <summary>
    /// The entity pipeline last handed out, with the state it was built for. The batched loop
    /// draws every entity with the same program, target and mesh shape, so this hits on all but
    /// the first draw of a stage and no draw allocates a description or a blend array.
    /// </summary>
    private readonly record struct NativeEntityKey(
        int ProgramId, int FramebufferId, int LayoutId, int ColorCount, int MotionIndex, bool MotionOpen);

    private NativeEntityKey nativeEntityKey;
    private NativePipeline? nativeEntityPipeline;
    private NativeTexture[] nativeEntityTextures = Array.Empty<NativeTexture>();
    private int[] nativeEntityReads = Array.Empty<int>();
    private bool nativeEntityReported;

    /// <summary>An entity's shape: the native draw inside the stage's pass, or the neutral body.</summary>
    public override void RenderEntityMesh(MeshRef mesh, string samplerName, int textureId)
    {
        if (!NativeEntitiesEnabled || device == null || mesh == null)
        {
            base.RenderEntityMesh(mesh!, samplerName, textureId);
            return;
        }

        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        FrameBufferRef target = CurrentFrameBuffer;
        var vao = mesh as VAO;
        if (program == null || target == null || vao == null || vao.VaoId == 0 || vao.Disposed ||
            !IsNativeEntityProgram(program))
        {
            base.RenderEntityMesh(mesh, samplerName, textureId);
            return;
        }

        int layoutId = device.NativeMeshLayoutId(vao.VaoId);
        if (layoutId < 0)
        {
            base.RenderEntityMesh(mesh, samplerName, textureId);
            return;
        }

        NativePipeline? pipeline = NativeEntityPipelineFor(program, target, layoutId);
        if (pipeline == null)
        {
            base.RenderEntityMesh(mesh, samplerName, textureId);
            return;
        }

        // Every sampler the program declares, resolved from what the client declared for it, with
        // this draw's own texture for the sampler the seam names.
        ResolveNativeEntityTextures(pipeline, program.ProgramId, samplerName, textureId);

        RuntimeStats.drawCallsCount++;
        if (device.BeginNativePass(new NativePassDescription
        {
            // The stage's own pass, so the declaration coalesces and the scope stays open across
            // the whole loop. Closed with keepScope below for the same reason.
            Name = BoundPassName(),
            FramebufferId = target.FboId,
            ColorSlots = uint.MaxValue,
            Reads = nativeEntityReads,
            Flags = passContextFlags,
        }))
        {
            device.DrawNativeMesh(pipeline, vao.VaoId, nativeEntityTextures);
        }
        device.EndNativePass(keepScope: true);
    }

    /// <summary>Whether the program in use is one this file draws natively.</summary>
    private static bool IsNativeEntityProgram(ShaderProgramBase program)
    {
        if (!string.Equals(program.PassName, EntityAnimatedPass, StringComparison.Ordinal) &&
            !string.Equals(program.PassName, EntityShadowPass, StringComparison.Ordinal))
        {
            return false;
        }

        // A sampler the client gave its own filtering or wrap mode to is bound through the unit's
        // sampler override, which a native draw does not read. Neither vanilla entity program does
        // that; if one ever did, the neutral body keeps it correct instead of silently losing it.
        if (program.customSamplers.Count != 0 || program.clampTToEdge) return false;

        // The vanilla programs, and the program the shader registry holds under the same pass
        // name: VSEssentials' first-person hands (ModSystemFpHands.fpModeHandShader) registers its
        // own entityanimated (ALLOWDEPTHOFFSET, its own Animation and, under TAA, AnimationPrev
        // blocks), which replaces the registry entry. On 2026-09-16 that program drew the arm
        // several times too large through this route with TAA on, with every bound input traced
        // equal; on 2026-09-17 the same repro (OPTIMUM_VK_NATIVE_ENTITIES=all,
        // OPTIMUM_VK_NATIVE_SHADERS=force, TAA on, headless) no longer showed it - the parity dump
        // of the hand region matched the neutral body (depth identical, motion within 0.0023) -
        // after the native routes that ran around it had changed. The cause was never named.
        // Anything else registered under these names stays on the neutral body (decision 1);
        // OPTIMUM_VK_NATIVE_ENTITIES=all admits every program with the pass name.
        if (!AllEntityPrograms &&
            !ReferenceEquals(program, ShaderPrograms.Entityanimated) &&
            !ReferenceEquals(program, ShaderPrograms.Shadowmapentityanimated) &&
            !IsRegistryProgram(program))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the shader registry holds this program under its pass name. Only a program the
    /// registry registered (PassId set, from 1) is looked up: ShaderRegistry's type initializer
    /// publishes uncompiled programs into ShaderPrograms.*, so a program the registry never saw
    /// must not be the first thing to touch it - that is what crashed the OpenGL loading screen
    /// when the upscaler stand-down called into it during startup.
    /// </summary>
    internal static bool IsRegistryProgram(ShaderProgramBase program) =>
        program.PassId > 0 && program.PassName != null &&
        ReferenceEquals(program, ShaderRegistry.getProgramByName(program.PassName));

    private static readonly bool AllEntityPrograms =
        Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_ENTITIES") == "all";

    /// <summary>
    /// The pipeline for this program, target, mesh shape and motion-window state, rebuilt only
    /// when one of those changes. Every piece of fixed state is stated from client state: the
    /// stage's own toggles, the platform's motion window, and the target's attachment count.
    /// </summary>
    private NativePipeline? NativeEntityPipelineFor(ShaderProgramBase program, FrameBufferRef target, int layoutId)
    {
        int colorCount = target.ColorTextureIds?.Length ?? 0;
        int motionIndex = MotionAttachmentIndex;
        if (motionIndex < 0 || motionIndex >= colorCount) motionIndex = -1;
        bool motionOpen = motionIndex >= 0 && OptimumMotionWriteActive;

        var key = new NativeEntityKey(program.ProgramId, target.FboId, layoutId, colorCount, motionIndex, motionOpen);
        if (nativeEntityPipeline != null && key.Equals(nativeEntityKey) &&
            device!.IsNativePipelineLive(nativeEntityPipeline))
        {
            return nativeEntityPipeline;
        }

        RenderTargetFormats? formats = device!.NativeTargetFormats(target.FboId, uint.MaxValue);
        if (formats == null) return null;

        var description = new NativePipelineDescription
        {
            ProgramId = program.ProgramId,
            PassName = program.PassName,
            VertexLayoutId = layoutId,
            Blend = NativeEntityBlend(formats.ColorFormats.Length, motionIndex, motionOpen),
            // SystemRenderEntities sets these immediately before both loops; ClientMain set the
            // depth function to LESS for the whole 3D render.
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = CompareOp.Less,
            Cull = CullModeFlags.None,
            Topology = PrimitiveTopology.TriangleList,
            Targets = formats,
        };

        NativePipeline? pipeline = device.RequestNativePipeline(description, out string error);
        if (pipeline == null)
        {
            if (!nativeEntityReported)
            {
                nativeEntityReported = true;
                Logger.Warning("Optimum: no native pipeline for '{0}': {1}", program.PassName, error);
            }
            nativeEntityPipeline = null;
            return null;
        }

        nativeEntityReported = false;
        nativeEntityKey = key;
        nativeEntityPipeline = pipeline;
        return pipeline;
    }

    /// <summary>
    /// The per-attachment blend the OpenGL body would be drawing with: standard alpha blending on
    /// the shaded slots, replace on the SSAO G-buffer slots, replace on the motion attachment
    /// while its window is open and no write at all when it is shut or the slot is past it.
    /// </summary>
    private static AttachmentBlend[] NativeEntityBlend(int colorCount, int motionIndex, bool motionOpen)
    {
        var blend = new AttachmentBlend[Math.Max(colorCount, 1)];
        // The attachments the shading pass writes: everything before the motion attachment, or
        // every bound slot when there is none. 2 without the SSAO G-buffer, 4 with it.
        int shaded = motionIndex >= 0 ? motionIndex : colorCount;
        for (int i = 0; i < blend.Length; i++)
        {
            AttachmentBlend attachment = AttachmentBlend.Default;
            if (i == motionIndex)
            {
                if (!motionOpen)
                {
                    attachment.WriteMask = 0;
                }
                else
                {
                    Replace(ref attachment);
                }
            }
            else if (i >= shaded)
            {
                // Past the motion attachment: nothing the entity programs declare an output for.
                attachment.WriteMask = 0;
            }
            else if (i >= 2)
            {
                // The SSAO G-buffer's normal and position slots: GlToggleBlend's RenderSSAO branch.
                Replace(ref attachment);
            }
            else
            {
                // Colour and glow: GlToggleBlend(true), EnumBlendMode.Standard.
                attachment.Enabled = true;
                attachment.SrcColor = BlendFactor.SrcAlpha;
                attachment.DstColor = BlendFactor.OneMinusSrcAlpha;
                attachment.ColorOp = BlendOp.Add;
                attachment.SrcAlpha = BlendFactor.SrcAlpha;
                attachment.DstAlpha = BlendFactor.OneMinusSrcAlpha;
                attachment.AlphaOp = BlendOp.Add;
            }
            blend[i] = attachment;
        }
        return blend;
    }

    /// <summary>(ONE, ZERO) with FUNC_ADD on every channel: the source wins, blending or not.</summary>
    private static void Replace(ref AttachmentBlend attachment)
    {
        attachment.Enabled = true;
        attachment.SrcColor = BlendFactor.One;
        attachment.DstColor = BlendFactor.Zero;
        attachment.ColorOp = BlendOp.Add;
        attachment.SrcAlpha = BlendFactor.One;
        attachment.DstAlpha = BlendFactor.Zero;
        attachment.AlphaOp = BlendOp.Add;
    }

    /// <summary>
    /// This draw's sampled textures: the seam's texture for the sampler it names, and what the
    /// client declared for every other sampler the program has. Reused buffers, because the
    /// batched loop calls this once per entity.
    /// </summary>
    private void ResolveNativeEntityTextures(NativePipeline pipeline, int programId, string samplerName, int textureId)
    {
        string[] names = pipeline.SamplerNames;
        if (nativeEntityTextures.Length != names.Length)
        {
            nativeEntityTextures = new NativeTexture[names.Length];
            nativeEntityReads = new int[names.Length];
        }
        for (int i = 0; i < names.Length; i++)
        {
            int id = string.Equals(names[i], samplerName, StringComparison.Ordinal)
                ? textureId
                : DeclaredProgramTexture(programId, names[i]);
            nativeEntityTextures[i] = new NativeTexture(pipeline.Sampler(names[i]), id);
            nativeEntityReads[i] = id;
        }
    }
}
