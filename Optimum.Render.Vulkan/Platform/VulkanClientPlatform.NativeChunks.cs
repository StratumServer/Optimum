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
// stage 2: the terrain, the heaviest draw path in the game, on the native device API.
//
// What it draws: every ChunkRenderer draw group - the two shadow cascades, the opaque,
// topsoil, vegetation, blend-no-cull and decorative groups of the Opaque stage, the OIT
// liquid and transparent groups, the liquid velocity redraw and the AfterOIT terrain
// overlay. Each group is one indirect multi-draw per visible chunk pool over per-chunk
// meshes, with the FaceData storage buffer feeding the vertex fetch when the pool is an
// SSBO pool, and it stays exactly that: MeshDataPool's ranges reach
// VulkanDevice.DrawNativeMeshMulti, which allocates from the same per-slot indirect ring
// the emulated DrawMeshMulti allocates from, and the real mesh id reaches BindProgramSets
// so the storage-buffer vertex fetch resolves per draw.
// Where the other side is: ClientPlatformAbstract.BeginChunkPass / EndChunkPass have
// neutral bodies (false and nothing), so the OpenGL path still draws through
// ClientPlatformWindows.RenderMesh(MeshRef, int[], int[], int, bool) -> GL.MultiDrawElements
// under the GlToggleBlend / GlEnableDepthTest / GlDepthMask / cull calls ChunkRenderer
// already makes. NativeChunksEnabled false takes that same route on the Vulkan device,
// which is what the differential test compares against.
// Target and slots: whatever the stage bound. Primary for the opaque, overlay and liquid
// velocity groups; the Transparent target for the OIT groups; a shadow map for the
// cascades. The motion window is the pass's colour-write mask and never a draw-buffer
// toggle: the motion attachment joins the pass's slots while the window is open, with
// replace blending, and the liquid velocity group masks every other slot to zero, which is
// what BeginMotionOnlyWrite means here.
// State that is not obvious:
//   - the per-attachment blend of a Primary group is the client's own contract, rebuilt
//     from the same values GlToggleBlend applies: the standard mode on the shaded slots,
//     replace blending on the SSAO G-buffer slots 2 and 3 and on the motion attachment,
//     because a blended G-buffer or motion value is an average of two surfaces and belongs
//     to neither;
//   - the Transparent target's contract is whichever set the client last applied to it -
//     the vanilla three-attachment set or the six-attachment OIT accumulation set -
//     recorded where the client states it (ApplyTransparentPassBlendState and
//     BeginOitAccumulation), not read back out of the state tracker;
//   - chunkliquid samples the depth attachment it draws against with depth writes off, so
//     its pipeline declares SamplesBoundDepth and the scope holds that depth read-only;
//   - the slots the group's program does not write keep their contents, because the
//     pipeline masks every output the program never writes (rule 9).
// What is still emulated inside a chunk group, and why: the uniform values the group sets
// by name - the per-pool "origin" from MeshDataPoolManager and the generated program
// setters - still travel through the GL-shaped uniform dispatch. They land in the same
// per-program record and push shadows the native draw snapshots, so the image is the same;
// removing that dispatch means moving MeshDataPoolManager itself, which is API-fork code
// and a later stage. The draws, the pass, the pipelines, the fixed state and the texture
// resolution are all native.
// What pins it: NativeChunkTests (old route against native route, per pass, including the
// motion attachment) and Optimum.Tests/native-world-systems-coverage-tests.cs.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// False runs the chunk groups through the generic stated multi-draw instead of the native
    /// pass: the route the differential test compares against, in the pattern of
    /// <see cref="NativeSkyEnabled" />.
    /// </summary>
    internal bool NativeChunksEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_CHUNKS") != "0";

    /// <summary>
    /// The texture each program sampler was last pointed at, recorded where the client points
    /// it (<see cref="BindProgramTexture2D" />). A native draw resolves what it samples from
    /// handles, so it needs the handle the client chose rather than the texture unit the
    /// GL-shaped path bound it to. Keyed by program id, then by the sampler's name.
    /// </summary>
    private readonly Dictionary<int, Dictionary<string, int>> nativeProgramTextures = new();

    /// <summary>The sampler names of a program, resolved once from its interface rather than per draw.</summary>
    private readonly Dictionary<int, string[]> nativeProgramSamplers = new();

    /// <summary>
    /// The per-attachment blend contract the Transparent target is drawn under, recorded at
    /// the two seams the client states it through. Null until the client states one.
    /// </summary>
    private AttachmentBlend[]? nativeTransparentBlend;

    /// <summary>
    /// The colour slots the client last selected on the Transparent target, recorded with the
    /// blend contract: 0x7 for the vanilla set (ApplyTransparentPassBlendState), 0x3F for the OIT
    /// accumulation set (BeginOitAccumulation), whose colour accumulation lives on slots 3-5 -
    /// attachments the target's FrameBufferRef does not list, so its texture count is not the
    /// slot set. 0 until the client has stated one.
    /// </summary>
    private uint nativeTransparentSlots;

    /// <summary>Bumped when that contract changes, so its pipelines are rebuilt rather than reused.</summary>
    private int nativeBlendEpoch;

    // ------------------------------------------------------------------ the open scope

    private bool chunkScopeActive;
    private bool chunkScopeBlend;
    private bool chunkScopeDepthTest;
    private bool chunkScopeDepthWrite;
    private bool chunkScopeCull;
    private bool chunkScopeMotionOnly;
    private string chunkScopeName = "";
    private FrameBufferRef? chunkScopeTarget;
    private uint chunkScopeSlots;
    private bool chunkScopePassOpen;
    private string chunkScopeOuterContext = "";
    private PassFlags chunkScopeOuterFlags;
    private NativeTexture[] chunkScopeTextures = Array.Empty<NativeTexture>();
    private int[] chunkScopeReads = Array.Empty<int>();

    /// <summary>The native pipelines the chunk groups draw through, one per distinct shape.</summary>
    private readonly Dictionary<ChunkPipelineKey, NativePipeline> chunkPipelines = new();

    /// <summary>
    /// Every dimension of a chunk group's pipeline the platform decides: the program, the
    /// target and the slots it writes, the mesh's vertex layout, and the fixed state the seam
    /// stated. The device keys its own cache on the full description; this one only keeps the
    /// description and its blend array from being rebuilt per draw.
    /// </summary>
    private readonly record struct ChunkPipelineKey(int ProgramId, int FramebufferId, uint Slots, int LayoutId,
        bool Blend, bool DepthTest, bool DepthWrite, bool Cull, bool MotionOnly, bool SamplesBoundDepth,
        int BlendEpoch);

    // ------------------------------------------------------------------- captured state

    /// <summary>
    /// Records the texture a program sampler points at. Called from
    /// <see cref="BindProgramTexture2D" /> and <see cref="BindProgramTextureCube" />, which is
    /// where the client states it; the GL-shaped unit binding still happens there too, so the
    /// old route keeps working unchanged.
    /// </summary>
    internal void NoteNativeProgramTexture(int programId, string samplerName, int textureId)
    {
        if (!nativeProgramTextures.TryGetValue(programId, out Dictionary<string, int>? textures))
        {
            textures = new Dictionary<string, int>(StringComparer.Ordinal);
            nativeProgramTextures[programId] = textures;
        }
        textures[samplerName] = textureId;
    }

    /// <summary>
    /// Records one attachment of the Transparent target's blend contract, at the seam the
    /// client states it through. GL's blend enable is global, so the recorded entry carries
    /// the functions and the group's own blend flag decides whether they apply.
    /// </summary>
    internal void NoteNativeTransparentBlend(int slot, int glEquation, int srcColor, int dstColor,
        int srcAlpha, int dstAlpha)
    {
        if ((uint)slot >= RenderLimits.MaxColorAttachments) return;
        if (nativeTransparentBlend == null)
        {
            nativeTransparentBlend = new AttachmentBlend[RenderLimits.MaxColorAttachments];
            for (int i = 0; i < nativeTransparentBlend.Length; i++)
            {
                nativeTransparentBlend[i] = AttachmentBlend.Default;
            }
        }

        AttachmentBlend blend = AttachmentBlend.Default;
        blend.Enabled = true;
        blend.ColorOp = GlEnums.BlendOpFrom(glEquation);
        blend.AlphaOp = blend.ColorOp;
        blend.SrcColor = GlEnums.BlendFactorFrom(srcColor);
        blend.DstColor = GlEnums.BlendFactorFrom(dstColor);
        blend.SrcAlpha = GlEnums.BlendFactorFrom(srcAlpha);
        blend.DstAlpha = GlEnums.BlendFactorFrom(dstAlpha);
        if (!nativeTransparentBlend[slot].Equals(blend)) nativeBlendEpoch++;
        nativeTransparentBlend[slot] = blend;
    }

    // ------------------------------------------------------------------------ the seam

    /// <summary>
    /// Opens the scope one ChunkRenderer draw group draws under. The group's multi-draws then
    /// reach <see cref="TryDrawChunkPoolNative" /> from the mesh seam; the native pass itself
    /// opens with the first draw, because that is when the textures it reads are known.
    /// </summary>
    public override bool BeginChunkPass(string chunkPass, bool blend, bool depthTest, bool depthWrite, bool cullFace)
    {
        EndChunkPass();
        if (!NativeChunksEnabled || device == null) return false;

        FrameBufferRef target = CurrentFrameBuffer;
        if (target == null || target.FboId <= 0) return false;

        chunkScopeActive = true;
        chunkScopeName = chunkPass ?? "chunk";
        chunkScopeBlend = blend;
        chunkScopeDepthTest = depthTest;
        chunkScopeDepthWrite = depthWrite;
        chunkScopeCull = cullFace;
        chunkScopeTarget = target;
        // The liquid velocity redraw is the one chunk group that opens its window with
        // BeginMotionOnlyWrite, so the seam's own name is what says the other slots are masked
        // off - no second copy of the window's state, and no reading a draw-buffer mask back.
        chunkScopeMotionOnly = string.Equals(chunkScopeName, "chunk-liquid-motion", StringComparison.Ordinal);
        chunkScopeSlots = ChunkColorSlots(target);
        chunkScopePassOpen = false;
        return true;
    }

    /// <summary>Closes the scope and, if a draw opened one, the native pass with it.</summary>
    public override void EndChunkPass()
    {
        if (!chunkScopeActive) return;
        chunkScopeActive = false;

        if (chunkScopePassOpen)
        {
            chunkScopePassOpen = false;
            device.EndNativePass();
            SetPassContext(chunkScopeOuterContext, chunkScopeOuterFlags);
        }
        chunkScopeTarget = null;
    }

    /// <summary>
    /// One chunk pool's multi-draw, recorded natively. False means the group is not in a native
    /// scope, or the first draw of one could not be recorded, and the caller takes the generic
    /// stated route. Once the scope's pass is open the native route owns the group: a draw the
    /// device skips (a pipeline still compiling) is skipped, not moved to another route.
    /// </summary>
    internal bool TryDrawChunkPoolNative(VAO vao, int[] indicesStarts, int[] indicesSizes, int groupCount)
    {
        if (!chunkScopeActive || device == null) return false;
        if (vao == null || vao.VaoId == 0 || vao.Disposed) return false;
        if (groupCount <= 0) return chunkScopePassOpen;

        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program == null || program.ProgramId <= 0) return chunkScopePassOpen;

        int layoutId = device.NativeMeshLayoutId(vao.VaoId);
        if (layoutId < 0) return chunkScopePassOpen;

        FrameBufferRef target = chunkScopeTarget!;
        string[] names = ChunkSamplerNames(program.ProgramId);
        int textureCount = CollectChunkTextures(program, names, target, out bool samplesBoundDepth);
        NativePipeline? pipeline = ChunkPipeline(program, target, layoutId, samplesBoundDepth);
        if (pipeline == null) return chunkScopePassOpen;

        for (int i = 0; i < textureCount; i++)
        {
            chunkScopeTextures[i] = chunkScopeTextures[i] with { Sampler = pipeline.Sampler(names[i]) };
        }

        if (!chunkScopePassOpen && !OpenChunkPass(target, textureCount)) return false;

        device.DrawNativeMeshMulti(pipeline, vao.VaoId, indicesStarts, indicesSizes, groupCount,
            new ReadOnlySpan<NativeTexture>(chunkScopeTextures, 0, textureCount));
        return true;
    }

    /// <summary>
    /// Declares the group's pass: the bound target, the slots the group writes and the textures
    /// its first draw reads, in the viewport the stage left - the way the OpenGL body's
    /// bind-only setter leaves it.
    /// </summary>
    private bool OpenChunkPass(FrameBufferRef target, int textureCount)
    {
        Rect2D viewport = StatedViewport();
        chunkScopeOuterContext = passContext;
        chunkScopeOuterFlags = passContextFlags;

        var reads = new int[textureCount];
        Array.Copy(chunkScopeReads, reads, textureCount);
        if (!device.BeginNativePass(new NativePassDescription
            {
                Name = chunkScopeName + "/" + target.FboId,
                FramebufferId = target.FboId,
                ColorSlots = chunkScopeSlots,
                Reads = reads,
                Flags = PassFlags.AllowSplit,
                ViewportX = viewport.Offset.X,
                ViewportY = viewport.Offset.Y,
                ViewportWidth = (int)viewport.Extent.Width,
                ViewportHeight = (int)viewport.Extent.Height,
            }))
        {
            return false;
        }
        chunkScopePassOpen = true;
        return true;
    }

    // ------------------------------------------------------------------- the pipeline

    /// <summary>
    /// The pipeline for one group's program, target, mesh shape and fixed state. The device
    /// caches the pipeline itself; this table only keeps the description and its blend array
    /// from being rebuilt per draw.
    /// </summary>
    private NativePipeline? ChunkPipeline(ShaderProgramBase program, FrameBufferRef target, int layoutId,
        bool samplesBoundDepth)
    {
        RenderTargetFormats? formats = device.NativeTargetFormats(target.FboId, chunkScopeSlots);
        if (formats == null) return null;

        var key = new ChunkPipelineKey(program.ProgramId, target.FboId, chunkScopeSlots, layoutId,
            chunkScopeBlend, chunkScopeDepthTest, chunkScopeDepthWrite, chunkScopeCull,
            chunkScopeMotionOnly, samplesBoundDepth, nativeBlendEpoch);
        if (chunkPipelines.TryGetValue(key, out NativePipeline? cached) &&
            device.IsNativePipelineLive(cached) && formats.Equals(cached.Description.Targets))
        {
            return cached;
        }

        NativePipeline? pipeline = device.RequestNativePipeline(new NativePipelineDescription
        {
            ProgramId = program.ProgramId,
            PassName = program.PassName,
            Blend = ChunkBlend(target, formats.ColorFormats.Length),
            DepthTest = chunkScopeDepthTest,
            DepthWrite = chunkScopeDepthWrite,
            DepthCompare = CompareOp.Less,
            Cull = chunkScopeCull ? CullModeFlags.BackBit : CullModeFlags.None,
            Topology = PrimitiveTopology.TriangleList,
            VertexLayoutId = layoutId,
            SamplesBoundDepth = samplesBoundDepth,
            Targets = formats,
        }, out string error);

        if (pipeline == null)
        {
            chunkPipelines.Remove(key);
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("no native chunk pipeline for '" + chunkScopeName + "': " + error);
            }
            return null;
        }
        chunkPipelines[key] = pipeline;
        return pipeline;
    }

    /// <summary>
    /// The colour slots a chunk group's pass writes. On Primary that is the client's default
    /// set, plus the motion attachment while a motion window is open; on any other target it is
    /// every bound slot, which is the set the emulated draw would have written.
    /// </summary>
    private uint ChunkColorSlots(FrameBufferRef target)
    {
        if (IsTransparentTarget(target) && nativeTransparentSlots != 0) return nativeTransparentSlots;
        if (!IsPrimaryTarget(target)) return NativeAllColorSlots(target);

        int motion = MotionAttachmentIndex;
        if (motion >= 0 && OptimumMotionWriteActive) return (1u << (motion + 1)) - 1u;
        return motion > 0 ? (1u << motion) - 1u : NativeWorldColorSlots();
    }

    /// <summary>
    /// The per-attachment blend of the group, rebuilt from the values the client applied
    /// rather than read back off the state tracker.
    /// </summary>
    private AttachmentBlend[] ChunkBlend(FrameBufferRef target, int count)
    {
        var blend = new AttachmentBlend[Math.Max(count, 1)];
        bool primary = IsPrimaryTarget(target);
        bool transparent = !primary && nativeTransparentBlend != null && IsTransparentTarget(target);
        int motion = primary && OptimumMotionWriteActive ? MotionAttachmentIndex : -1;

        for (int slot = 0; slot < blend.Length; slot++)
        {
            AttachmentBlend entry;
            if (transparent)
            {
                // The contract the client applied to the Transparent target, whichever of the
                // two it was; its enable is the group's, because GL's is global.
                entry = nativeTransparentBlend![slot];
                entry.Enabled = chunkScopeBlend;
            }
            else if (chunkScopeBlend && primary && OptimumRenderSsao && (slot == 2 || slot == 3))
            {
                // GlToggleBlend's own exception: the SSAO G-buffer is replace-blended, because
                // a blended normal or position belongs to neither surface.
                entry = ReplaceBlend(chunkScopeBlend);
            }
            else
            {
                entry = AttachmentBlend.Default;
                entry.Enabled = chunkScopeBlend;
            }

            // The motion attachment never blends (ApplyOptimumMotionBlendState).
            if (slot == motion) entry = ReplaceBlend(chunkScopeBlend);

            // The liquid velocity redraw writes the motion attachment and nothing else: the
            // window is this mask, not a draw-buffer toggle.
            if (chunkScopeMotionOnly && slot != motion) entry.WriteMask = 0;
            blend[slot] = entry;
        }
        return blend;
    }

    private static AttachmentBlend ReplaceBlend(bool enabled)
    {
        AttachmentBlend blend = AttachmentBlend.Default;
        blend.Enabled = enabled;
        blend.ColorOp = BlendOp.Add;
        blend.AlphaOp = BlendOp.Add;
        blend.SrcColor = BlendFactor.One;
        blend.DstColor = BlendFactor.Zero;
        blend.SrcAlpha = BlendFactor.One;
        blend.DstAlpha = BlendFactor.Zero;
        return blend;
    }

    private bool IsPrimaryTarget(FrameBufferRef target) =>
        FrameBuffers != null && FrameBuffers.Count > 0 && ReferenceEquals(target, FrameBuffers[0]);

    private bool IsTransparentTarget(FrameBufferRef target) =>
        FrameBuffers != null && FrameBuffers.Count > 1 && ReferenceEquals(target, FrameBuffers[1]);

    // -------------------------------------------------------------------- the textures

    /// <summary>
    /// The textures this draw samples: every sampler the program declares, resolved to the
    /// handle the client last pointed it at, with the program's own sampler override where it
    /// has one (the terrain atlas read twice, nearest and linear, is exactly that case).
    /// Returns how many entries of the scratch arrays are in use, and whether any of them is
    /// the depth attachment of the target being drawn into - chunkliquid's fade against the
    /// depth it draws with writes off.
    /// </summary>
    private int CollectChunkTextures(ShaderProgramBase program, string[] names, FrameBufferRef target,
        out bool samplesBoundDepth)
    {
        samplesBoundDepth = false;
        if (chunkScopeTextures.Length < names.Length)
        {
            chunkScopeTextures = new NativeTexture[names.Length];
            chunkScopeReads = new int[names.Length];
        }

        nativeProgramTextures.TryGetValue(program.ProgramId, out Dictionary<string, int>? bound);
        for (int i = 0; i < names.Length; i++)
        {
            int textureId = 0;
            if (bound != null) bound.TryGetValue(names[i], out textureId);

            SamplerState? sampling = null;
            if (program.customSamplers.TryGetValue(names[i], out int samplerId) &&
                device.TryNativeSamplerState(samplerId, out SamplerState custom))
            {
                sampling = custom;
            }

            if (textureId > 0 && textureId == target.DepthTextureId && !chunkScopeDepthWrite)
            {
                samplesBoundDepth = true;
            }

            chunkScopeTextures[i] = new NativeTexture(NativeSamplerSlot.None, textureId, sampling);
            chunkScopeReads[i] = textureId;
        }
        return names.Length;
    }

    private string[] ChunkSamplerNames(int programId)
    {
        if (nativeProgramSamplers.TryGetValue(programId, out string[]? names)) return names;
        names = device.SamplerNamesOf(programId);
        nativeProgramSamplers[programId] = names;
        return names;
    }

    /// <summary>
    /// A relinked or deleted program's cached sampler names, texture bindings and pipelines are
    /// no longer its own: a shader reload hands the same id a different interface.
    /// </summary>
    internal void ForgetNativeChunkProgram(int programId)
    {
        nativeProgramSamplers.Remove(programId);
        nativeProgramTextures.Remove(programId);
        if (chunkPipelines.Count == 0) return;

        var stale = new List<ChunkPipelineKey>();
        foreach (KeyValuePair<ChunkPipelineKey, NativePipeline> entry in chunkPipelines)
        {
            if (entry.Key.ProgramId == programId) stale.Add(entry.Key);
        }
        for (int i = 0; i < stale.Count; i++) chunkPipelines.Remove(stale[i]);
    }
}
