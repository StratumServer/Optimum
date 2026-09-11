using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>How a declared pass treats sampling and scope splits.</summary>
[Flags]
public enum PassFlags
{
    None = 0,

    /// <summary>
    /// Mod-hosted stages sample what they like: at pass entry every render-target
    /// texture outside the pass's attachments that is not already shader-readable
    /// moves to SHADER_READ_ONLY_OPTIMAL, so an undeclared read does not split.
    /// </summary>
    OpenSampling = 1,

    /// <summary>
    /// A split (a second vkCmdBeginRendering inside the pass) is expected here and
    /// is not traced as a declaration violation. It is still counted.
    /// </summary>
    AllowSplit = 2,
}

/// <summary>
/// One pass as the platform declares it: the target, which of its colour slots
/// take part (the rest are null attachments, so they can be sampled), what it
/// reads, which slots it overwrites completely every frame (transient for the
/// plan), and its sampling policy. Depth follows the target: the scope holds the
/// bound depth attachment, writable or read-only as the draws need.
/// </summary>
public sealed class PassDeclaration
{
    /// <summary>The bound framebuffer; <see cref="DefaultFramebuffer" /> for the default target.</summary>
    public const int BoundFramebuffer = 0;

    public const int DefaultFramebuffer = -1;

    public string Name = "";

    /// <summary>Render target id; <see cref="BoundFramebuffer" /> declares on whatever is bound.</summary>
    public int FramebufferId = BoundFramebuffer;

    /// <summary>Bit i: colour slot i is an attachment of the pass. Default: every bound slot.</summary>
    public uint ColorSlots = uint.MaxValue;

    /// <summary>Texture ids the pass samples (not its own attachments), pre-transitioned at pass entry.</summary>
    public int[] Reads = Array.Empty<int>();

    /// <summary>
    /// Bit i: colour slot i is plainly written over its whole extent by the pass
    /// and never needed from a previous frame, so an exactly matching plan may
    /// load it DONT_CARE.
    /// </summary>
    public uint TransientSlots;

    public PassFlags Flags;
}

/// <summary>A clear issued with no pass open, waiting for the next use of its image.</summary>
internal readonly record struct PendingClear(VulkanTexture Texture, uint Layer, bool Depth, float R, float G, float B, float A);

/// <summary>
/// The streaming frame graph (Vulkan-native plan, Phase 2 step 2). The client's
/// frame is imperative, so passes are declared and recorded in frame order as they
/// happen: <see cref="RenderTargetManager" /> asks for a pass index when a pass
/// opens its one rendering scope (<see cref="OpenPass" />), and the load ops come
/// from the <see cref="FramePlan" /> solved from the previous frame, applied only
/// while every pass so far matches it exactly. Store ops stay STORE: a streaming
/// recorder cannot know that the rest of the frame will still match (see
/// <see cref="FramePlan" />).
///
/// It also holds the clears issued with no pass open (clear promotion): they become
/// LOAD_OP_CLEAR on the next scope that attaches the image, or a standalone clear
/// command when something else touches the image first.
///
/// <c>OPTIMUM_VULKAN_FRAMEGRAPH=0</c> turns all of it off and keeps the scope
/// inference path exactly as it was. Render thread only.
/// </summary>
internal sealed class FrameGraph
{
    public const string Variable = "OPTIMUM_VULKAN_FRAMEGRAPH";

    public static bool EnabledByEnvironment => Environment.GetEnvironmentVariable(Variable) != "0";

    /// <summary>Change only between frames.</summary>
    public bool Enabled { get; set; } = EnabledByEnvironment;

    private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
    private readonly List<PassSignature> _frame = new();
    private readonly List<PendingClear> _pending = new();
    private FramePlan? _plan;
    private bool _prefixMatches = true;

    // Totals for tests; VulkanStats carries the interval counters.
    public long Passes { get; private set; }
    public long DeclaredPasses { get; private set; }
    public long Splits { get; private set; }
    public long UndeclaredSplits { get; private set; }
    public long PlanHits { get; private set; }
    public long PlanMisses { get; private set; }
    public long InPassClears { get; private set; }
    public long PromotedClears { get; private set; }
    public long StandaloneClears { get; private set; }
    public long PlannedDontCareLoads { get; private set; }

    /// <summary>Passes opened in the frame being recorded.</summary>
    public int PassesThisFrame => _frame.Count;

    /// <summary>The plan the current frame is matched against, null before the first frame ended.</summary>
    public FramePlan? Plan => _plan;

    public int NameId(string name)
    {
        if (!_names.TryGetValue(name, out int id))
        {
            id = _names.Count;
            _names.Add(name, id);
        }
        return id;
    }

    /// <summary>
    /// A pass opens its scope: records its signature and returns its index in the
    /// frame. <paramref name="declared" /> is false for a scope no declaration
    /// covered (the inference path inside a graph frame).
    /// </summary>
    public int OpenPass(PassSignature signature, bool declared)
    {
        int index = _frame.Count;
        _prefixMatches = _prefixMatches && _plan != null && !_plan.IsConservative && _plan.MatchesPass(index, signature);
        _frame.Add(signature);
        Passes++;
        if (declared) DeclaredPasses++;
        VulkanStats.NotePass();
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("pass " + index + " name=" + signature.NameId + " attachments=" + signature.Attachments.Length +
                " reads=" + signature.Reads.Length + " " + signature.Width + "x" + signature.Height +
                " plan=" + (_prefixMatches ? "match" : "conservative"));
        }
        return index;
    }

    /// <summary>
    /// The load op for attachment <paramref name="attachment" /> of pass
    /// <paramref name="pass" /> when no clear was promoted into it: the plan's op
    /// while the frame so far matches the plan, LOAD otherwise.
    /// </summary>
    public AttachmentLoadOp PlannedLoad(int pass, int attachment)
    {
        if (!_prefixMatches || _plan == null || pass < 0 || pass >= _plan.PassCount) return AttachmentLoadOp.Load;
        AttachmentLoadOp op = _plan.LoadOp(pass, attachment);
        if (op == AttachmentLoadOp.DontCare) PlannedDontCareLoads++;
        return op;
    }

    /// <summary>A scope reopened inside a pass that had already opened one.</summary>
    public void NoteSplit(bool allowed)
    {
        Splits++;
        if (!allowed) UndeclaredSplits++;
        VulkanStats.NotePassSplit();
    }

    public void NoteInPassClear()
    {
        InPassClears++;
        VulkanStats.NoteInPassClear();
    }

    /// <summary>
    /// Ends the frame: a hit when every pass matched the plan the frame was recorded
    /// against, then the plan for the next frame is solved from this one.
    /// </summary>
    public void EndFrame()
    {
        if (_frame.Count > 0)
        {
            if (_plan != null && !_plan.IsConservative && _plan.Matches(_frame))
            {
                PlanHits++;
                VulkanStats.NotePlanHit();
            }
            else
            {
                PlanMisses++;
                VulkanStats.NotePlanMiss();
            }
            _plan = FramePlan.Build(_frame);
        }
        _frame.Clear();
        _prefixMatches = true;
    }

    // ------------------------------------------------------------ clear promotion

    public bool HasPendingClears => _pending.Count > 0;

    public void PromoteColorClear(VulkanTexture texture, uint layer, float r, float g, float b, float a)
    {
        Replace(new PendingClear(texture, layer, false, r, g, b, a));
    }

    public void PromoteDepthClear(VulkanTexture texture, float depth)
    {
        Replace(new PendingClear(texture, 0, true, depth, 0, 0, 0));
    }

    private void Replace(PendingClear clear)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            PendingClear existing = _pending[i];
            if (ReferenceEquals(existing.Texture, clear.Texture) && existing.Layer == clear.Layer &&
                existing.Depth == clear.Depth)
            {
                _pending[i] = clear;
                return;
            }
        }
        _pending.Add(clear);
    }

    public bool HasPendingClear(VulkanTexture texture)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            if (ReferenceEquals(_pending[i].Texture, texture)) return true;
        }
        return false;
    }

    /// <summary>Takes the clear of one attachment view into a load op, if one is pending.</summary>
    public bool TakeForLoad(VulkanTexture texture, uint layer, bool depth, out PendingClear clear)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            PendingClear candidate = _pending[i];
            if (!ReferenceEquals(candidate.Texture, texture) || candidate.Depth != depth) continue;
            if (!depth && candidate.Layer != layer) continue;
            _pending.RemoveAt(i);
            clear = candidate;
            PromotedClears++;
            VulkanStats.NotePromotedClear();
            return true;
        }
        clear = default;
        return false;
    }

    /// <summary>Takes every clear pending on <paramref name="texture" /> (null: on every texture) for standalone commands.</summary>
    public void TakeStandalone(VulkanTexture? texture, List<PendingClear> output)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (texture != null && !ReferenceEquals(_pending[i].Texture, texture)) continue;
            output.Add(_pending[i]);
            _pending.RemoveAt(i);
        }
        // Oldest first, so two clears never reorder.
        output.Reverse();
    }

    public void NoteStandaloneClear()
    {
        StandaloneClears++;
        VulkanStats.NoteStandaloneClear();
    }

    /// <summary>A deleted texture's clears are moot.</summary>
    public void Drop(VulkanTexture texture)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_pending[i].Texture, texture)) _pending.RemoveAt(i);
        }
    }
}
