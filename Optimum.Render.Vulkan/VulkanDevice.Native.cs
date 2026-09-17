using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

/// <summary>Which block of a program's interface a native uniform lives in.</summary>
internal enum NativeUniformBlock : byte
{
    None = 0,
    /// <summary>The program record at set 2, snapshotted into the uniform ring per draw.</summary>
    Record = 1,
    /// <summary>A DRAW uniform in the program's push block.</summary>
    Push = 2,
    /// <summary>A member of the shared frame block at set 0.</summary>
    Frame = 3,
}

/// <summary>
/// A uniform's placement in one native pipeline's program, resolved once when the pipeline
/// is created. A native renderer holds the value and writes through it, so no draw looks a
/// uniform up by name.
/// </summary>
internal readonly record struct NativeUniform(NativeUniformBlock Block, int Offset, int Size)
{
    public bool IsPresent => Block != NativeUniformBlock.None;
}

/// <summary>
/// A sampler's placement: the push-block offset its bindless slot index is written to (or
/// the set 0 binding, for a fixed frame texture) and the array kind it indexes. Resolved
/// once with the pipeline.
/// </summary>
internal readonly record struct NativeSamplerSlot(int Index, int PushOffset, int FrameBinding, TextureKind Kind)
{
    public static NativeSamplerSlot None => new(-1, -1, -1, TextureKind.Texture2D);

    public bool IsPresent => Index >= 0;
}

/// <summary>One sampled texture of a native draw: the slot, the texture handle and the sampler state to read it with (null: the texture's own).</summary>
internal readonly record struct NativeTexture(NativeSamplerSlot Sampler, int TextureId, SamplerState? Sampling = null);

/// <summary>
/// The fixed state a native pipeline is built for (docs/vulkan-native-render-systems.md,
/// decision 4). It is <see cref="PipelineKey" />'s shape stated outright: per-attachment blend and colour write mask,
/// depth test/write/compare, cull, topology and the target's formats.
/// </summary>
internal sealed class NativePipelineDescription
{
    /// <summary>The linked program: a manifest program when native shaders are on, its rewritten twin otherwise.</summary>
    public int ProgramId;

    /// <summary>The pass name the program was linked under, checked against the device's; null skips the check.</summary>
    public string? PassName;

    /// <summary>The variant the program must have been linked for, checked against the device's; null skips the check.</summary>
    public string? VariantKey;

    /// <summary>Per colour attachment; <see cref="AttachmentBlend.WriteMask" /> is the colour write mask. Attachments past the array are not written.</summary>
    public AttachmentBlend[] Blend = Array.Empty<AttachmentBlend>();

    public bool DepthTest;
    public bool DepthWrite;
    public CompareOp DepthCompare = CompareOp.Less;
    public CullModeFlags Cull = CullModeFlags.None;

    /// <summary>
    /// The winding a front face has. The game never calls glFrontFace, so every vanilla system
    /// states <see cref="RenderLimits.FrontFace" />; a native system that needs the other one
    /// says so here rather than through a tracked toggle.
    /// </summary>
    public FrontFace FrontFace = RenderLimits.FrontFace;

    public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;

    /// <summary>Fill for every vanilla system; Line is the wireframe debug render's.</summary>
    public PolygonMode PolygonMode = PolygonMode.Fill;

    /// <summary>The width a line-topology draw rasterizes with (autocamera's debug path sets 2).</summary>
    public float LineWidth = 1.0f;

    /// <summary>
    /// The vertex layout the pipeline's draws feed it with: <see cref="MeshManager.EmptyLayoutId" />
    /// for a pass that generates its vertices (the fullscreen triangle), otherwise the layout id of
    /// the mesh the system draws (<see cref="VulkanDevice.NativeMeshLayoutId" />). It is part of the
    /// pipeline key, so a mesh pipeline can never be handed a fullscreen one.
    /// </summary>
    public int VertexLayoutId = MeshManager.EmptyLayoutId;

    /// <summary>
    /// The draws sample the depth attachment of the target they draw into, with depth writes off -
    /// what the liquid pass does to fade water at its edges. The scope then holds depth read-only
    /// for the draw. Only legal with <see cref="DepthWrite" /> false; a fullscreen pass leaves it
    /// false and sampling its own attachment stays an error.
    /// </summary>
    public bool SamplesBoundDepth;

    /// <summary>The attachment formats of the target the pipeline renders into.</summary>
    public RenderTargetFormats Targets = null!;
}

/// <summary>
/// A pipeline a native render system owns: the program, its fixed state, the pipeline-cache
/// key built from them, and the placement tables the draws write through.
/// </summary>
internal sealed class NativePipeline
{
    private readonly Dictionary<string, NativeUniform> _uniforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeSamplerSlot> _samplers = new(StringComparer.Ordinal);

    internal NativePipeline(ShaderProgramResources program, NativePipelineDescription description,
        PipelineKey key, GraphicsPipelineCache.PipelineRequest request, int dynamicBlendId)
    {
        Program = program;
        Description = description;
        Key = key;
        Request = request;
        DynamicBlendId = dynamicBlendId;

        ProgramInterfaceLayout layout = program.Interface;
        foreach (UniformMember member in layout.Members)
        {
            _uniforms[member.Name] = new NativeUniform(NativeUniformBlock.Record, member.Offset, member.Size);
        }
        foreach (KeyValuePair<string, UniformMember> push in layout.PushMembersByName)
        {
            _uniforms[push.Key] = new NativeUniform(NativeUniformBlock.Push, push.Value.Offset, push.Value.Size);
        }
        foreach (string name in layout.FrameMemberDeclaredLengths.Keys)
        {
            if (FrameGlobals.TryGetMember(name, out UniformMember frame))
            {
                _uniforms[name] = new NativeUniform(NativeUniformBlock.Frame, frame.Offset, frame.Size);
            }
        }
        SamplerNames = new string[layout.Samplers.Count];
        for (int i = 0; i < layout.Samplers.Count; i++)
        {
            SamplerBinding sampler = layout.Samplers[i];
            TextureKind kind = sampler.Kind;
            if (sampler.IsFrameTexture) BindlessKinds.TryFromGlslType(sampler.TypeName, out kind);
            _samplers[sampler.Name] = new NativeSamplerSlot(i, sampler.PushOffset, sampler.FrameBinding, kind);
            SamplerNames[i] = sampler.Name;
        }
    }

    /// <summary>
    /// Every sampler the program declares, in binding order. A system whose draws are all
    /// native has to resolve all of them: the emulated resolve that would otherwise fill the
    /// push block's slots from the texture units never runs for such a program, so a sampler
    /// left out would read whatever slot index was last written there.
    /// </summary>
    internal string[] SamplerNames { get; }

    internal ShaderProgramResources Program { get; }

    public int ProgramId => Program.ProgramId;

    public NativePipelineDescription Description { get; }

    internal PipelineKey Key { get; }

    internal GraphicsPipelineCache.PipelineRequest Request { get; }

    /// <summary>The interned blend set the dynamic-state cache compares on, with the mask tier's dynamic blend.</summary>
    internal int DynamicBlendId { get; }

    /// <summary>The placement of a uniform, resolved once here rather than per draw.</summary>
    public NativeUniform Uniform(string name) =>
        _uniforms.TryGetValue(name, out NativeUniform member) ? member : default;

    /// <summary>The placement of a sampler, resolved once here rather than per draw.</summary>
    public NativeSamplerSlot Sampler(string name) =>
        _samplers.TryGetValue(name, out NativeSamplerSlot sampler) ? sampler : NativeSamplerSlot.None;
}

/// <summary>
/// A native pass: an explicit target, the colour slots it writes, the textures it samples
/// and the viewport its draws use. No draw-buffer mask and no bound-target guessing.
/// </summary>
internal sealed class NativePassDescription
{
    public string Name = "";

    /// <summary>A render target id, or <see cref="PassDeclaration.DefaultFramebuffer" /> for the default target.</summary>
    public int FramebufferId = PassDeclaration.DefaultFramebuffer;

    /// <summary>Bit i: colour slot i is an attachment of the pass.</summary>
    public uint ColorSlots = 1u;

    /// <summary>The textures the pass samples, made shader-readable at pass entry.</summary>
    public int[] Reads = Array.Empty<int>();

    public uint TransientSlots;
    public PassFlags Flags = PassFlags.None;

    /// <summary>
    /// Bit i: colour slot i starts the pass cleared to <see cref="ClearValue" />. The pass states
    /// its own clear instead of a glClearBuffer against a draw-buffer mask, so it lands as the
    /// scope's load op.
    /// </summary>
    public uint ClearSlots;

    /// <summary>The value <see cref="ClearSlots" /> clears to.</summary>
    public float[] ClearValue = { 0f, 0f, 0f, 0f };

    public int ViewportX;
    public int ViewportY;

    /// <summary>
    /// The scissor the client stated for this draw, in the target's own (GL-oriented) pixels;
    /// null: the full target. A GUI draw inside a clipped dialog needs it.
    /// </summary>
    public Rect2D? Scissor;

    /// <summary>
    /// A pass of the generic stated route (Platform/StatedDraw.cs) rather than of a dedicated
    /// native system. It records the same way; only the test counters keep it apart.
    /// </summary>
    public bool Generic;

    /// <summary>Negative: the full target.</summary>
    public int ViewportWidth = -1;
    public int ViewportHeight = -1;
}

/// <summary>
/// The device API native render systems draw through (docs/vulkan-native-render-systems.md,
/// section 2 decision 4 and section 3).
///
/// A native system asks for a pipeline by program and fixed state, declares a pass with its
/// target, its colour slots and the textures it reads, writes its uniforms by placement and
/// records draws. Nothing else reaches the GPU: the device has no GL state machine, no
/// texture-unit tables and no draw-buffer mask. Mod renderers and the vanilla systems without a
/// dedicated route draw through the platform's generic native draw (Platform/StatedDraw.cs).
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    private readonly Interner<BlendSignature> _nativeBlends = new();
    private readonly Interner<RenderTargetFormats> _nativeFormats = new();
    private readonly Dictionary<NativePipelineCacheKey, NativePipeline> _nativePipelines = new();

    /// <summary>The manifest variant each native program was linked for, keyed by program id.</summary>
    private readonly Dictionary<int, string> _programVariants = new();

    private NativePassDescription? _nativePass;
    private VulkanFramebuffer? _nativeTarget;
    private long _nativePasses;
    private long _nativeDraws;
    private long _genericPasses;
    private long _genericDraws;
    private long _nativeFullscreenDraws;
    private long _nativeMeshDraws;
    private long _nativeInstancedDraws;
    private long _nativeIndirectDraws;

    /// <summary>
    /// The identity of a native pipeline: every field of its description that changes what a draw
    /// through it does. The vertex layout is in it, so a mesh pipeline never collides with the
    /// fullscreen one of the same program, formats and blend; so are the dynamic pieces (front
    /// face, line width) that are not in <see cref="PipelineKey" />, because the cached
    /// <see cref="NativePipeline" /> carries the description its draws emit.
    /// </summary>
    private readonly record struct NativePipelineCacheKey(
        int ProgramId, int FormatsId, int BlendId, bool DepthTest, bool DepthWrite,
        CompareOp DepthCompare, CullModeFlags Cull, PrimitiveTopology Topology,
        int VertexLayoutId, PolygonMode PolygonMode, FrontFace FrontFace, float LineWidth,
        bool SamplesBoundDepth);

    /// <summary>Native passes declared and native draws recorded (every kind). Tests only.</summary>
    internal long NativePassesForTests => _nativePasses;
    internal long NativeDrawsForTests => _nativeDraws;

    /// <summary>Passes and draws of the generic stated route (Platform/StatedDraw.cs), counted apart from the above. Tests only.</summary>
    internal long GenericPassesForTests => _genericPasses;
    internal long GenericDrawsForTests => _genericDraws;

    /// <summary>Native draws by kind: the fullscreen triangle, a mesh, an instanced mesh, a multi-draw. Tests only.</summary>
    internal long NativeFullscreenDrawsForTests => _nativeFullscreenDraws;
    internal long NativeMeshDrawsForTests => _nativeMeshDraws;
    internal long NativeInstancedDrawsForTests => _nativeInstancedDraws;
    internal long NativeIndirectDrawsForTests => _nativeIndirectDraws;

    /// <summary>Distinct native pipelines this device holds. Tests only.</summary>
    internal int NativePipelinesForTests => _nativePipelines.Count;

    /// <summary>Set 1's placeholders are written shader-read-only and never used any other way: put them there once.</summary>
    private void EnsureBindlessPlaceholdersReadable(CommandBuffer commandBuffer)
    {
        if (_bindlessPlaceholdersReadable || _bindless == null) return;

        for (int kind = 0; kind < BindlessKinds.Count; kind++)
        {
            VulkanTexture? placeholder = _textures.Get(_bindless.PlaceholderTextureId((TextureKind)kind));
            if (placeholder == null || placeholder.Layout == ImageLayout.ShaderReadOnlyOptimal) continue;
            _targets.EndRendering(commandBuffer);
            _textures.Require(_barriers, commandBuffer, placeholder, ResourceUsage.SampleFragment);
        }
        _bindlessPlaceholdersReadable = true;
    }

    // ------------------------------------------------------------------ pipelines

    /// <summary>
    /// The attachment formats of a target's colour slots, so a native system can state the
    /// formats its pipeline is built for. Null for a target that does not exist.
    /// </summary>
    internal RenderTargetFormats? NativeTargetFormats(int framebufferId, uint colorSlots)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(framebufferId));
        if (target == null) return null;

        return _targets.DeclaredFormats(target, colorSlots);
    }

    /// <summary>
    /// The viewport the GL-shaped state last set. A native pass that keeps the viewport - the
    /// OIT merge and sky motion bind their target without touching it, as the OpenGL body's
    /// bind-only setter does - states this as its own.
    /// </summary>
    /// <summary>
    /// The unit a program's sampler reads: the client's SetSamplerUnit mapping, else the sampler's
    /// declaration order - the resolution the removed emulated draw made. -1 for an unknown program or name.
    /// </summary>
    internal int NativeSamplerUnit(int programId, string samplerName)
    {
        if (!_programs.TryGetValue(programId, out ShaderProgramResources? program)) return -1;
        if (program.SamplerUnits.TryGetValue(samplerName, out int mapped)) return mapped;
        foreach (SamplerBinding declared in program.Interface.Samplers)
        {
            if (string.Equals(declared.Name, samplerName, StringComparison.Ordinal)) return declared.Order;
        }
        return -1;
    }

    /// <summary>The sampling state of a standalone sampler object (GenSampler), or null.</summary>
    internal SamplerState? NativeStandaloneSampler(int samplerId) =>
        _standaloneSamplers.TryGetValue(samplerId, out SamplerState state) ? state : null;

    /// <summary>The texture attached at a framebuffer's colour slot, 0 without one.</summary>
    internal int NativeFramebufferColorTexture(int framebufferId, int slot)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(framebufferId));
        return target != null && (uint)slot < (uint)target.Color.Length ? target.Color[slot].TextureId : 0;
    }

    /// <summary>The depth texture attached to a framebuffer, 0 without one.</summary>
    internal int NativeFramebufferDepthTexture(int framebufferId) =>
        _targets.Get(ResolveNativeFramebuffer(framebufferId))?.DepthTextureId ?? 0;


    /// <summary>The manifest variant a program was linked for; "" for a program the rewriter linked.</summary>
    internal string NativeVariantOf(int programId) =>
        _programVariants.TryGetValue(programId, out string? key) ? key : "";

    /// <summary>
    /// The interned vertex layout of a mesh, which a native system states on the pipeline it
    /// draws that mesh through. -1 for a mesh that does not exist.
    /// </summary>
    internal int NativeMeshLayoutId(int meshId) => _meshes.LayoutIdOf(meshId);

    /// <summary>
    /// The state of a sampler object the client created (glGenSamplers), so a native system
    /// can read a program's own sampler override - the chunk terrain's linear sampler on the
    /// same atlas texture the nearest sampler reads - straight from the handle the client holds,
    /// instead of through the texture unit it was bound to. False for an id that is not one.
    /// </summary>
    internal bool TryNativeSamplerState(int samplerId, out SamplerState state) =>
        _standaloneSamplers.TryGetValue(samplerId, out state);

    private int ResolveNativeFramebuffer(int framebufferId) =>
        framebufferId == PassDeclaration.DefaultFramebuffer
            ? (_defaultRedirect > 0 ? _defaultRedirect : _defaultFramebuffer)
            : framebufferId;

    /// <summary>
    /// World/UI separation: the target <see cref="PassDeclaration.DefaultFramebuffer" /> stands for
    /// while the platform's UI scope is open - its UI image - or 0 for the window image itself. Every
    /// draw, clear, format query and readback that names Default resolves through
    /// <see cref="ResolveNativeFramebuffer" />, so each render system that has always drawn "onto the
    /// window" draws into the UI image instead without knowing it, and only the compose, which closes
    /// the scope first, writes the window image (VulkanClientPlatform.UiSeparation.cs).
    /// </summary>
    internal void RedirectDefaultFramebuffer(int framebufferId) => _defaultRedirect = framebufferId;

    /// <summary>The target Default currently resolves to instead of the window image; 0 for none.</summary>
    internal int DefaultFramebufferRedirect => _defaultRedirect;

    private int _defaultRedirect;

    /// <summary>
    /// The pipeline for a program and a piece of fixed state, created through the pipeline
    /// cache on first request and returned from this device's table afterwards. Null with a
    /// reason when the program is not linked, was linked as something else, or the request
    /// names no target formats.
    /// </summary>
    internal NativePipeline? RequestNativePipeline(NativePipelineDescription description, out string error)
    {
        error = "";
        if (!_programs.TryGetValue(description.ProgramId, out ShaderProgramResources? program))
        {
            error = "program " + description.ProgramId + " is not linked";
            return null;
        }
        if (description.PassName != null &&
            (!_programNames.TryGetValue(description.ProgramId, out string? name) ||
             !string.Equals(name, description.PassName, StringComparison.Ordinal)))
        {
            error = "program " + description.ProgramId + " is not '" + description.PassName + "'";
            return null;
        }
        if (description.VariantKey != null &&
            !string.Equals(NativeVariantOf(description.ProgramId), description.VariantKey, StringComparison.Ordinal))
        {
            error = "program '" + description.PassName + "' was linked for variant '" +
                NativeVariantOf(description.ProgramId) + "', not '" + description.VariantKey + "'";
            return null;
        }
        if (description.Targets == null)
        {
            error = "the request names no target formats";
            return null;
        }
        if (description.SamplesBoundDepth && description.DepthWrite)
        {
            error = "a pipeline that samples the bound depth attachment cannot also write depth";
            return null;
        }
        if (description.VertexLayoutId < 0 || description.VertexLayoutId >= _meshes.LayoutCount)
        {
            error = "vertex layout " + description.VertexLayoutId + " does not exist";
            return null;
        }

        ColorWriteTier tier = _context.Capabilities.ColorWriteTier;
        bool dynamicBlend = tier == ColorWriteTier.DynamicMask && _context.Capabilities.DynamicColorBlend;
        int count = description.Targets.ColorFormats.Length;

        // The blend set as the pipeline bakes it under the colour write tier.
        var baked = new AttachmentBlend[Math.Max(count, 1)];
        for (int i = 0; i < baked.Length; i++)
        {
            AttachmentBlend blend = AttachmentBlend.Default;
            if (i < description.Blend.Length) blend = description.Blend[i];
            else blend.WriteMask = 0;

            switch (tier)
            {
            case ColorWriteTier.DynamicMask:
                if (dynamicBlend) blend = AttachmentBlend.Default;
                blend.WriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
                break;
            }
            baked[i] = blend;
        }

        int rawBlendId = _nativeBlends.Intern(new BlendSignature(NativeBlendSpan(description, count)));
        int bakedBlendId = _nativeBlends.Intern(new BlendSignature(baked.AsSpan(0, Math.Max(count, 0))));
        int formatsId = _nativeFormats.Intern(description.Targets);

        // Keyed on the blend set as described, not as baked: the draw emits its dynamic blend and
        // write masks from the cached description, so two descriptions that bake to one Vulkan
        // pipeline under a dynamic tier are still two entries here (they share the PipelineKey below).
        var cacheKey = new NativePipelineCacheKey(description.ProgramId, formatsId, rawBlendId,
            description.DepthTest, description.DepthWrite, description.DepthCompare,
            description.Cull, description.Topology, description.VertexLayoutId, description.PolygonMode,
            description.FrontFace, description.LineWidth, description.SamplesBoundDepth);
        if (_nativePipelines.TryGetValue(cacheKey, out NativePipeline? cached) &&
            ReferenceEquals(cached.Program, program))
        {
            return cached;
        }

        // The system's own vertex layout - the reserved empty one for a pass that generates its
        // vertices, the mesh's interned layout for a mesh draw - plus the constant attribute
        // defaults GL promises for anything the program declares and the layout does not carry.
        VertexLayoutDescription vertexLayout = _meshes.LayoutOf(description.VertexLayoutId)
            .WithDefaultsFor(program.Interface.VertexInputs);

        // The blend id is negative, a range of its own in the pipeline cache's key space. The
        // vertex layout is in the key, so a mesh pipeline and a fullscreen pipeline of the same
        // program are never the same entry.
        var key = new PipelineKey(
            ProgramId: description.ProgramId,
            VertexLayoutId: description.VertexLayoutId,
            TargetFormatsId: formatsId,
            BlendId: -(bakedBlendId + 1),
            PolygonMode: description.PolygonMode,
            TopologyClass: GlEnums.TopologyClassOf(description.Topology));

        var request = new GraphicsPipelineCache.PipelineRequest
        {
            Program = program,
            VertexLayout = vertexLayout,
            Targets = description.Targets,
            Blend = baked,
            PolygonMode = description.PolygonMode,
            Topology = description.Topology,
        };

        var pipeline = new NativePipeline(program, description, key, request, -(rawBlendId + 1));
        _nativePipelines[cacheKey] = pipeline;

        // Created here rather than at the first draw where it can be; an async cache queues
        // the compile and the first draws are skipped until it is published.
        _pipelines.Prepare(key, request);
        return pipeline;
    }

    private static ReadOnlySpan<AttachmentBlend> NativeBlendSpan(NativePipelineDescription description, int count)
    {
        if (description.Blend.Length >= count) return description.Blend.AsSpan(0, Math.Max(count, 0));
        var padded = new AttachmentBlend[Math.Max(count, 0)];
        for (int i = 0; i < padded.Length; i++)
        {
            padded[i] = i < description.Blend.Length ? description.Blend[i] : AttachmentBlend.Default;
        }
        return padded;
    }

    /// <summary>Whether a pipeline's program is still the linked one of that id (a shader reload replaces it).</summary>
    internal bool IsNativePipelineLive(NativePipeline pipeline) =>
        _programs.TryGetValue(pipeline.ProgramId, out ShaderProgramResources? program) &&
        ReferenceEquals(program, pipeline.Program);

    private void ForgetNativePipelines(int programId)
    {
        if (_nativePipelines.Count == 0) return;

        var stale = new List<NativePipelineCacheKey>();
        foreach (KeyValuePair<NativePipelineCacheKey, NativePipeline> entry in _nativePipelines)
        {
            if (entry.Key.ProgramId == programId) stale.Add(entry.Key);
        }
        foreach (NativePipelineCacheKey key in stale) _nativePipelines.Remove(key);
    }

    // ---------------------------------------------------------------------- passes

    /// <summary>
    /// Opens a native pass on an explicit target: its colour slots, the textures it samples
    /// and the viewport its draws use. The draw-buffer mask is not consulted.
    /// </summary>
    internal bool BeginNativePass(NativePassDescription pass)
    {
        EndNativePass();
        if (!_frameActive) return false;

        int id = ResolveNativeFramebuffer(pass.FramebufferId);
        VulkanFramebuffer? target = id > 0 ? _targets.Get(id) : null;
        if (target == null)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native pass '" + pass.Name + "' skipped: no such target " + id);
            return false;
        }

        CommandBuffer commandBuffer = Commands;
        _targets.DeclarePass(commandBuffer, new PassDeclaration
        {
            Name = pass.Name,
            FramebufferId = id,
            ColorSlots = pass.ColorSlots,
            Reads = pass.Reads,
            TransientSlots = pass.TransientSlots,
            Flags = pass.Flags,
        }, id);
        if (!ReferenceEquals(_targets.Bound, target)) _targets.Bind(commandBuffer, id);

        for (int slot = 0; slot < RenderLimits.MaxColorAttachments && pass.ClearSlots != 0; slot++)
        {
            if (((pass.ClearSlots >> slot) & 1) == 0) continue;
            _targets.ClearPassAttachment(commandBuffer, slot,
                pass.ClearValue[0], pass.ClearValue[1], pass.ClearValue[2], pass.ClearValue[3]);
        }

        _nativePass = pass;
        _nativeTarget = target;
        if (pass.Generic) _genericPasses++;
        else _nativePasses++;
        VulkanStats.NoteNativePass();
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native pass '" + pass.Name + "' target=" + id + " slots=" + pass.ColorSlots +
                " reads=" + string.Join(",", pass.Reads));
        }
        return true;
    }

    /// <summary>Closes the native pass and its scope.</summary>
    internal void EndNativePass() => EndNativePass(keepScope: false);

    /// <summary>
    /// Closes the native pass. <paramref name="keepScope" /> leaves the rendering scope and the
    /// pass declaration exactly as they were, for a native draw recorded inside a pass the
    /// surrounding stage has already declared - the entity loop, which records one native draw
    /// per entity into the Opaque stage's own pass and would otherwise end and restart the
    /// rendering scope once per entity. It is only correct when the pass description named that
    /// same declaration, so <see cref="BeginNativePass" /> coalesced into it rather than opening
    /// one of its own; a pass with its own name, slots or clears must be closed the normal way.
    ///
    /// The native-pass bookkeeping is cleared either way, so what a render system does between
    /// its draws (its uniforms by name) happens outside a native pass.
    /// </summary>
    internal void EndNativePass(bool keepScope)
    {
        if (_nativePass == null) return;

        _nativePass = null;
        _nativeTarget = null;
        if (!_frameActive || keepScope) return;

        CommandBuffer commandBuffer = Commands;
        _targets.EndPass(commandBuffer);
        _targets.EndRendering(commandBuffer);
    }

    // --------------------------------------------------------------------- uniforms

    /// <summary>Writes a uniform of a native pipeline's program at its resolved placement.</summary>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, ReadOnlySpan<byte> data)
    {
        switch (uniform.Block)
        {
        case NativeUniformBlock.Record:
            pipeline.Program.SetUniform(uniform.Offset, data);
            break;
        case NativeUniformBlock.Push:
            pipeline.Program.SetPushUniform(ShaderProgramResources.PushLocationBase + uniform.Offset, data);
            break;
        case NativeUniformBlock.Frame:
            WriteFrameGlobal(uniform.Offset, data);
            break;
        }
    }

    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float value) =>
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(&value, sizeof(float)));

    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, int value) =>
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(&value, sizeof(int)));

    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float x, float y)
    {
        float* values = stackalloc float[2] { x, y };
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(values, 2 * sizeof(float)));
    }

    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float x, float y, float z)
    {
        float* values = stackalloc float[3] { x, y, z };
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(values, 3 * sizeof(float)));
    }

    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float x, float y, float z, float w)
    {
        float* values = stackalloc float[4] { x, y, z, w };
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(values, 4 * sizeof(float)));
    }

    /// <summary>
    /// A float run at a placement: a matrix, a vector array, a kernel. The model-view matrix a
    /// world system writes before each of its draws goes through here, which is what makes the
    /// write draw-frequency - a record member is snapshotted into this frame's uniform ring when
    /// the draw binds set 2, a push member is pushed with the draw's push block.
    /// </summary>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, ReadOnlySpan<float> values)
    {
        if (values.IsEmpty) return;
        fixed (float* first = values)
        {
            WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(first, values.Length * sizeof(float)));
        }
    }

    // ------------------------------------------------------------------------ draws

    /// <summary>
    /// Records the fullscreen triangle of a native pass: the pass's reads are made
    /// shader-readable, the sampled textures resolve to bindless slots straight from their
    /// handles and sampler state, and the pipeline's fixed state is what the draw runs with.
    ///
    /// The mesh-drawing siblings are in VulkanDevice.NativeMesh.cs; all of them share
    /// <see cref="BeginNativeDraw" />, which is this method's old body.
    /// </summary>
    internal bool DrawNativeFullscreen(NativePipeline pipeline, ReadOnlySpan<NativeTexture> textures)
    {
        if (!BeginNativeDraw(pipeline, textures, 0, out CommandBuffer commandBuffer, out VulkanFramebuffer? target))
        {
            return false;
        }

        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.Fullscreen, pipeline.ProgramId, target!.Id, 0));
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native fullscreen program=" + pipeline.ProgramId + " pass='" + _nativePass!.Name +
                "' target=" + target.Id);
        }
        _context.Api.CmdDraw(commandBuffer, 3, 1, 0, 0);
        NoteNativeDraw(NativeDrawKind.Fullscreen);
        return true;
    }

    /// <summary>
    /// Everything a native draw needs before its draw command: the pass and pipeline are
    /// checked, the textures the draw samples are put into the layout a shader read needs,
    /// the rendering scope is opened, the pipeline is bound, the sampled textures resolve to
    /// bindless slots straight from their handles and sampler state, the program's sets are
    /// bound (with <paramref name="meshId" />, so a chunk's storage-buffer vertex fetch and an
    /// entity's animation block resolve to this draw's mesh) and the dynamic state is emitted.
    ///
    /// Shared by the fullscreen draw and every mesh draw. None of it reads the GL state
    /// tracker, a texture unit or a draw-buffer mask.
    /// </summary>
    private bool BeginNativeDraw(NativePipeline pipeline, ReadOnlySpan<NativeTexture> textures, int meshId,
        out CommandBuffer commandBuffer, out VulkanFramebuffer? target)
    {
        commandBuffer = default;
        target = null;

        if (!_frameActive || _nativePass == null || _nativeTarget == null)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native draw skipped: no open native pass");
            return false;
        }

        NativePassDescription pass = _nativePass;
        VulkanFramebuffer bound = _nativeTarget;
        if (!ReferenceEquals(_targets.Bound, bound))
        {
            AddDiagnostic("native pass '" + pass.Name + "' lost its target before its draw");
            return false;
        }

        ShaderProgramResources program = pipeline.Program;
        if (!IsNativePipelineLive(pipeline))
        {
            AddDiagnostic("native pass '" + pass.Name + "' draws with program " + pipeline.ProgramId +
                ", which has been relinked or deleted");
            return false;
        }

        commandBuffer = Commands;
        ReleaseReadSelfCopies();
        EnsureBindlessPlaceholdersReadable(commandBuffer);

        // The pass's reads, put into the layout a shader read needs. A colour attachment of
        // its own target is sampled through a pooled ReadSelf copy taken before the scope
        // opens - the atlas compositions (BlendedTextureManager, RenderTextureIntoFrameBuffer)
        // copy one region of an atlas into another region of the same atlas, and the copy is
        // what the draw samples (SnapshotColorAttachment). The bound
        // depth attachment with depth writes off is sampled in place, which the pipeline
        // declares (NativePipelineDescription.SamplesBoundDepth) and the scope then holds
        // read-only.
        bool depthReadOnly = false;
        for (int i = 0; i < textures.Length; i++)
        {
            VulkanTexture? texture = _textures.Get(textures[i].TextureId);
            if (texture == null) continue;
            if (_targets.IsBoundDepth(textures[i].TextureId))
            {
                if (!pipeline.Description.SamplesBoundDepth)
                {
                    AddDiagnostic("native pass '" + pass.Name + "' samples texture " + textures[i].TextureId +
                        ", the depth attachment of its own target, through a pipeline that does not declare it");
                    return false;
                }
                depthReadOnly = true;
                continue;
            }
            if (_targets.IsAttachmentOfBound(textures[i].TextureId))
            {
                if (texture.Aspect != ImageAspectFlags.ColorBit)
                {
                    AddDiagnostic("native pass '" + pass.Name + "' samples texture " + textures[i].TextureId +
                        ", a non-colour attachment of its own target");
                    return false;
                }
                SnapshotColorAttachment(commandBuffer, textures[i].TextureId, texture);
                continue;
            }
            _targets.FlushPendingClears(commandBuffer, texture);
            if (texture.Layout == ImageLayout.ShaderReadOnlyOptimal)
            {
                _uploads.NoteUse(commandBuffer, texture);
                continue;
            }
            _targets.EndRendering(commandBuffer);
            _textures.Require(_barriers, commandBuffer, texture, ResourceUsage.SampleFragment);
        }
        PrepareUnnamedFrameTextures(commandBuffer, program, textures);
        _barriers.Flush(commandBuffer);

        // Decided before the scope opens, since it decides the depth attachment's layout.
        _targets.SetDepthReadOnly(depthReadOnly);
        _targets.EnsureRendering(commandBuffer);

        RenderTargetFormats scope = _targets.ScopeFormats(bound);
        if (!scope.Equals(pipeline.Description.Targets))
        {
            AddDiagnostic("native pass '" + pass.Name + "' has target formats its pipeline was not built for");
            return false;
        }

        if (!_pipelines.TryGet(pipeline.Key, pipeline.Request, out Pipeline handle))
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("native draw skipped: pipeline for program " + pipeline.ProgramId + " still compiling");
            }
            return false;
        }

        Vk api = _context.Api;
        api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, handle);

        VertexLayoutDescription vertexLayout = pipeline.Request.VertexLayout;
        if (vertexLayout.Bindings.Length > 0 &&
            vertexLayout.Bindings[^1].Binding == VertexLayoutDescription.DefaultAttributeBinding &&
            _defaultAttributes != null)
        {
            Silk.NET.Vulkan.Buffer defaults = _defaultAttributes.Handle;
            ulong offset = 0;
            api.CmdBindVertexBuffers(commandBuffer,
                VertexLayoutDescription.DefaultAttributeBinding, 1, &defaults, &offset);
        }

        // The program's push block, then this draw's slots over it.
        int pushSize = program.Interface.PushConstantSize;
        if (program.PushShadow != null) program.PushShadow.CopyTo(_pushShadow, 0);
        else if (pushSize > 0) _pushShadow.AsSpan(0, pushSize).Clear();

        for (int i = 0; i < textures.Length; i++)
        {
            NativeTexture sampled = textures[i];
            NativeSamplerSlot sampler = sampled.Sampler;
            if (!sampler.IsPresent) continue;

            // A ReadSelf copy taken above stands in for the attachment it copies.
            VulkanTexture? texture = _textures.Get(
                _sampledTextureOverrides.TryGetValue(sampled.TextureId, out int readSelfCopy)
                    ? readSelfCopy
                    : sampled.TextureId);
            if (texture != null && !BindlessKinds.Suits(TextureShape.Of(texture), sampler.Kind))
            {
                if (RenderTrace.Enabled)
                {
                    RenderTrace.Write("native sampler " + sampler.Index + " on program " + program.ProgramId +
                        " has texture " + sampled.TextureId + " of format " + texture.Format +
                        " bound, which it cannot sample; using a placeholder");
                }
                texture = null;
            }

            SamplerState sampling = SamplerState.Default;
            if (texture != null)
            {
                // MAX_LEVEL belongs to the texture, even when the caller overrides the filters.
                sampling = sampled.Sampling is { } state
                    ? state with { MaxLevel = texture.State.MaxLevel }
                    : texture.State;
            }

            // The bound depth attachment is sampled in the read-only depth layout, the one the
            // scope holds it in, exactly as the emulated resolve keys it.
            ImageLayout layout = depthReadOnly && _targets.IsBoundDepth(sampled.TextureId)
                ? ImageLayout.DepthReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;

            if (sampler.FrameBinding >= 0)
            {
                SamplerBindingValue value = texture == null
                    ? default
                    : new SamplerBindingValue((uint)sampler.FrameBinding, texture.View,
                        _textures.Samplers.Get(BindlessKinds.EffectiveState(sampling, sampler.Kind)), texture.Id, layout);
                if (texture == null) VulkanStats.NoteSamplerPlaceholder();
                int frameIndex = FrameTextureIndex(sampler.FrameBinding);
                lock (_frameTextureLock)
                {
                    _frameTextureValues[frameIndex] = value;
                    _frameTextureIds[frameIndex] = texture == null ? 0 : sampled.TextureId;
                }
                continue;
            }

            uint slot = _bindless!.Resolve(texture, sampler.Kind, sampling, layout);
            VulkanStats.NoteBindlessSlotResolution();
            BitConverter.TryWriteBytes(_pushShadow.AsSpan(sampler.PushOffset, ProgramInterfaceLayout.SlotBytes), slot);
        }

        BindProgramSets(commandBuffer, program, meshId);
        EmitNativeDynamicState(commandBuffer, bound, pass, pipeline);

        target = bound;
        return true;
    }

    /// <summary>
    /// A frame texture the program reads (set 0) that this draw does not name keeps the value the
    /// last draw that named it left - GL's "whatever the unit still holds". That texture may have
    /// been written since (the liquid depth pass renders into liquidDepth's image before the sky
    /// dome, whose route names only sky and glow): it is put back into the read layout here, and
    /// replaced by the placeholder when it is an attachment of this draw's own target, which a
    /// shader cannot read.
    /// </summary>
    private void PrepareUnnamedFrameTextures(CommandBuffer commandBuffer, ShaderProgramResources program,
        ReadOnlySpan<NativeTexture> textures)
    {
        if (!program.Interface.UsesFrameTextures) return;
        foreach (SamplerBinding declared in program.Interface.Samplers)
        {
            if (!declared.IsFrameTexture) continue;
            bool named = false;
            for (int i = 0; i < textures.Length && !named; i++)
            {
                named = textures[i].Sampler.IsPresent && textures[i].Sampler.FrameBinding == declared.FrameBinding;
            }
            if (named) continue;

            int index = FrameTextureIndex(declared.FrameBinding);
            SamplerBindingValue stale;
            int textureId;
            lock (_frameTextureLock)
            {
                stale = _frameTextureValues[index];
                textureId = _frameTextureIds[index];
            }
            if (stale.View.Handle == 0) continue;

            // Gone, recreated, a ReadSelf copy of an attachment, or an attachment of this draw's own
            // target: nothing the shader may read any more, so the placeholder stands in.
            VulkanTexture? texture = _textures.Get(textureId);
            if (texture == null || texture.Id != stale.Resource || _targets.IsAttachmentOfBound(textureId))
            {
                lock (_frameTextureLock)
                {
                    _frameTextureValues[index] = default;
                    _frameTextureIds[index] = 0;
                }
                continue;
            }
            if (stale.Layout != ImageLayout.ShaderReadOnlyOptimal)
            {
                lock (_frameTextureLock) _frameTextureValues[index] = stale with { Layout = ImageLayout.ShaderReadOnlyOptimal };
            }
            _targets.FlushPendingClears(commandBuffer, texture);
            if (texture.Layout == ImageLayout.ShaderReadOnlyOptimal)
            {
                _uploads.NoteUse(commandBuffer, texture);
                continue;
            }
            _targets.EndRendering(commandBuffer);
            _textures.Require(_barriers, commandBuffer, texture, ResourceUsage.SampleFragment);
        }
    }

    /// <summary>The dynamic state of a native draw: the pipeline's fixed state and the pass's viewport, never the tracker's.</summary>
    private void EmitNativeDynamicState(CommandBuffer commandBuffer, VulkanFramebuffer target,
        NativePassDescription pass, NativePipeline pipeline)
    {
        ColorWriteTier tier = _context.Capabilities.ColorWriteTier;
        bool dynamicBlend = tier == ColorWriteTier.DynamicMask && _context.Capabilities.DynamicColorBlend;
        int colorStates = (int)Math.Min(_context.Capabilities.MaxColorAttachments, (uint)RenderLimits.MaxColorAttachments);
        NativePipelineDescription description = pipeline.Description;

        uint colorWrite = 0;
        for (int i = 0; i < colorStates && tier != ColorWriteTier.PipelineKey; i++)
        {
            ColorComponentFlags mask = i < description.Blend.Length ? description.Blend[i].WriteMask : 0;
            // An output the program never writes keeps the attachment's contents, as it does on GL.
            if (!pipeline.Program.Interface.WrittenFragmentOutputs.Contains(i)) mask = 0;
            if (tier == ColorWriteTier.DynamicEnable)
            {
                if (mask != 0) colorWrite |= 1u << i;
            }
            else
            {
                colorWrite |= (uint)mask << (i * 4);
            }
        }

        int width = pass.ViewportWidth >= 0 ? pass.ViewportWidth : (int)target.Width;
        int height = pass.ViewportHeight >= 0 ? pass.ViewportHeight : (int)target.Height;
        var values = new DynamicStateValues
        {
            Viewport = new Viewport(pass.ViewportX, pass.ViewportY, width, height, 0f, 1f),
            Scissor = pass.Scissor ?? new Rect2D(new Offset2D(0, 0), new Extent2D(target.Width, target.Height)),
            CullMode = description.Cull,
            FrontFace = description.FrontFace,
            Topology = description.Topology,
            DepthTest = description.DepthTest,
            DepthWrite = description.DepthWrite,
            DepthCompare = description.DepthCompare,
            StencilTest = false,
            StencilFail = StencilOp.Keep,
            StencilPass = StencilOp.Keep,
            StencilDepthFail = StencilOp.Keep,
            StencilCompare = CompareOp.Always,
            StencilCompareMask = 0xFF,
            StencilWriteMask = 0xFF,
            StencilReference = 0,
            // Clamped through the same device range as the emulated path's, so a native line
            // draw and the seam's neutral body rasterize identically.
            LineWidth = _context.Capabilities.ClampLineWidth(description.LineWidth),
            ColorWrite = colorWrite,
            BlendStateId = dynamicBlend ? pipeline.DynamicBlendId : 0,
        };

        FrameSlot slot = _frames.Current;
        ulong serial = slot.CommandBuffer.Handle == commandBuffer.Handle ? slot.RecordingSerial : 0;

        Span<AttachmentBlend> blendStates = stackalloc AttachmentBlend[dynamicBlend ? colorStates : 0];
        for (int i = 0; i < blendStates.Length; i++)
        {
            blendStates[i] = i < description.Blend.Length ? description.Blend[i] : AttachmentBlend.Default;
        }
        EmitDynamicState(commandBuffer, values, serial, tier, dynamicBlend, colorStates, blendStates);
    }
}
