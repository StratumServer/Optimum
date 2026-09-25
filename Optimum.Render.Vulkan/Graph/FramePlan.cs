using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// Cached attachment load operations for a recorded frame. The streaming graph applies
/// them only while the current pass prefix matches. Stores always preserve contents;
/// physical image reuse belongs to TransientAllocator and its explicit lifetimes.
/// </summary>
internal sealed class FramePlan
{
    private readonly PassSignature[] _passes;
    private readonly AttachmentLoadOp[][] _load;

    private FramePlan(PassSignature[] passes, AttachmentLoadOp[][] load)
    {
        _passes = passes;
        _load = load;
    }

    public int PassCount => _passes.Length;

    /// <summary>Snapshot the frame and identify attachments whose initial contents
    /// are disposable. Every attachment use must opt in, and the first reference
    /// must be a plain write with no read of that resource in the same pass.</summary>
    public static FramePlan Build(IReadOnlyList<PassSignature> frame)
    {
        PassSignature[] passes = Snapshot(frame);
        var resources = new Dictionary<int, ResourceInfo>();
        for (int p = 0; p < passes.Length; p++)
        {
            foreach (AttachmentUse use in passes[p].Attachments)
            {
                ResourceInfo info = Touch(resources, use.ResourceId, p);
                if (!use.Transient || (info.FirstPass == p && !IsPlainWrite(use.Usage)))
                    info.DiscardInitialContents = false;
            }
            foreach (int read in passes[p].Reads)
            {
                ResourceInfo info = Touch(resources, read, p);
                if (info.FirstPass == p) info.DiscardInitialContents = false;
            }
        }

        var load = new AttachmentLoadOp[passes.Length][];
        for (int p = 0; p < passes.Length; p++)
        {
            AttachmentUse[] attachments = passes[p].Attachments;
            load[p] = new AttachmentLoadOp[attachments.Length];
            for (int a = 0; a < attachments.Length; a++)
            {
                AttachmentUse use = attachments[a];
                ResourceInfo info = resources[use.ResourceId];
                load[p][a] = info.DiscardInitialContents && info.FirstPass == p && IsPlainWrite(use.Usage)
                    ? AttachmentLoadOp.DontCare
                    : AttachmentLoadOp.Load;
            }
        }
        return new FramePlan(passes, load);
    }

    /// <summary>Exact match on pass count and, per pass, name, attachments (resource, usage,
    /// transient flag, order), reads (order significant), extent and formats.</summary>
    public bool Matches(IReadOnlyList<PassSignature> frame)
    {
        if (frame == null || frame.Count != _passes.Length) return false;
        for (int p = 0; p < _passes.Length; p++)
        {
            if (!_passes[p].SameAs(frame[p])) return false;
        }
        return true;
    }

    /// <summary>Whether pass <paramref name="pass"/> of this plan is identical to
    /// <paramref name="signature"/>. False for an index past the plan's end.</summary>
    public bool MatchesPass(int pass, PassSignature signature)
    {
        if (pass < 0 || pass >= _passes.Length) return false;
        return _passes[pass].SameAs(signature);
    }

    public AttachmentLoadOp LoadOp(int pass, int attachment)
    {
        CheckIndex(pass, attachment);
        return _load[pass][attachment];
    }

    private void CheckIndex(int pass, int attachment)
    {
        if ((uint)pass >= (uint)_passes.Length)
            throw new ArgumentOutOfRangeException(nameof(pass), pass, $"Plan has {_passes.Length} passes.");
        if ((uint)attachment >= (uint)_load[pass].Length)
            throw new ArgumentOutOfRangeException(nameof(attachment), attachment, $"Pass {pass} has {_load[pass].Length} attachments.");
    }

    private static bool IsPlainWrite(ResourceUsage usage) =>
        usage == ResourceUsage.ColorWrite || usage == ResourceUsage.DepthWrite;

    private static PassSignature[] Snapshot(IReadOnlyList<PassSignature> frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        var passes = new PassSignature[frame.Count];
        for (int p = 0; p < passes.Length; p++)
        {
            PassSignature source = frame[p] ?? throw new ArgumentException($"Pass {p} is null.", nameof(frame));
            passes[p] = source.Clone();
        }
        return passes;
    }

    private static ResourceInfo Touch(Dictionary<int, ResourceInfo> resources, int resourceId, int pass)
    {
        if (!resources.TryGetValue(resourceId, out ResourceInfo? info))
        {
            info = new ResourceInfo(pass);
            resources.Add(resourceId, info);
        }
        return info;
    }

    private sealed class ResourceInfo
    {
        public ResourceInfo(int firstPass) => FirstPass = firstPass;
        public readonly int FirstPass;
        public bool DiscardInitialContents = true;
    }
}

/// <summary>One attachment of a pass: which resource, how it is used, and whether its
/// contents are allowed to die with the frame.</summary>
/// <param name="Transient">The contents are never read in a later frame. A resource is
/// treated as transient only when every attachment use of it in the frame says so and its
/// first reference is a plain write (see <see cref="FramePlan"/>).</param>
internal readonly record struct AttachmentUse(int ResourceId, ResourceUsage Usage, bool Transient);

/// <summary>
/// What a pass looked like when it was recorded: the unit a <see cref="FramePlan"/> is
/// built from and matched against. Resource ids are frame-graph ids, stable across frames
/// for the same logical image.
/// </summary>
internal sealed class PassSignature
{
    public int NameId;
    public AttachmentUse[] Attachments = Array.Empty<AttachmentUse>();
    /// <summary>Resources sampled or otherwise read (not as attachments), in declaration order.</summary>
    public int[] Reads = Array.Empty<int>();
    public int Width;
    public int Height;
    /// <summary>Interned id of the ordered attachment format list.</summary>
    public int FormatsId;

    public PassSignature Clone() => new()
    {
        NameId = NameId,
        Attachments = Attachments == null ? Array.Empty<AttachmentUse>() : (AttachmentUse[])Attachments.Clone(),
        Reads = Reads == null ? Array.Empty<int>() : (int[])Reads.Clone(),
        Width = Width,
        Height = Height,
        FormatsId = FormatsId,
    };

    /// <summary>Exact equality on every field the plan depends on. Null arrays equal empty ones;
    /// read order is significant.</summary>
    public bool SameAs(PassSignature other)
    {
        if (other == null) return false;
        if (NameId != other.NameId || Width != other.Width || Height != other.Height || FormatsId != other.FormatsId)
            return false;
        return SameSequence(Attachments, other.Attachments) && SameSequence(Reads, other.Reads);
    }

    private static bool SameSequence<T>(T[]? a, T[]? b) where T : IEquatable<T>
    {
        int la = a?.Length ?? 0;
        int lb = b?.Length ?? 0;
        if (la != lb) return false;
        for (int i = 0; i < la; i++)
        {
            if (!a![i].Equals(b![i])) return false;
        }
        return true;
    }
}
