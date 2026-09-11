using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// Load/store ops and transient alias slots for one frame, solved from a recorded frame's
/// ordered pass signatures and applied to a later frame only when that frame's signatures
/// match exactly (<see cref="Matches"/>). A mismatch costs one conservative frame
/// (<see cref="Conservative"/>: LOAD/STORE everywhere, no aliasing).
///
/// Rules, per pass p and attachment a on resource r:
/// <list type="bullet">
/// <item>r is <b>transient</b> for the frame when it is an attachment somewhere, every
/// attachment use of it is marked transient, and its first reference in the frame is a plain
/// write (<see cref="ResourceUsage.ColorWrite"/> or <see cref="ResourceUsage.DepthWrite"/>)
/// with no other use of r in that pass (no read, no blend, no read-only depth). A transient
/// that is read, blended into or depth-tested before it is written this frame depends on older
/// contents and is treated as persistent.</item>
/// <item><see cref="LoadOp"/> is DONT_CARE for a plain write in r's first pass when r is
/// transient, LOAD otherwise. CLEAR is never returned: the recorder decides clears and
/// overrides the plan's op.</item>
/// <item><see cref="StoreOp"/> is DONT_CARE in r's last pass when r is transient, STORE
/// otherwise. Any later reference (a read or an attachment, which always loads) keeps
/// STORE.</item>
/// <item><see cref="AliasSlot"/> is the <see cref="TransientPlacement"/> slot of a transient
/// resource, -1 for everything else. Whether slots are actually shared is the recorder's
/// decision (<c>OPTIMUM_VULKAN_ALIAS</c>, default off).</item>
/// </list>
///
/// Store DONT_CARE is only sound for the whole matched frame: a streaming recorder that
/// applies the plan pass by pass must not apply a DONT_CARE store before it knows the rest of
/// the frame still matches (<see cref="MatchesPass"/> checks one pass).
/// </summary>
internal sealed class FramePlan
{
    private readonly PassSignature[] _passes;
    private readonly AttachmentLoadOp[][] _load;
    private readonly AttachmentStoreOp[][] _store;
    private readonly Dictionary<int, int> _aliasSlots;

    private FramePlan(PassSignature[] passes, AttachmentLoadOp[][] load, AttachmentStoreOp[][] store,
        Dictionary<int, int> aliasSlots, bool conservative, int aliasSlotCount)
    {
        _passes = passes;
        _load = load;
        _store = store;
        _aliasSlots = aliasSlots;
        IsConservative = conservative;
        AliasSlotCount = aliasSlotCount;
    }

    /// <summary>True for a plan that loads and stores everything and aliases nothing.</summary>
    public bool IsConservative { get; }

    public int PassCount => _passes.Length;

    /// <summary>Number of distinct alias slots the transients were placed into.</summary>
    public int AliasSlotCount { get; }

    /// <summary>Solves load/store ops and alias slots for <paramref name="frame"/>. The
    /// signatures are copied; later mutation by the caller does not change the plan.</summary>
    public static FramePlan Build(IReadOnlyList<PassSignature> frame)
    {
        PassSignature[] passes = Snapshot(frame);
        var resources = new Dictionary<int, ResourceInfo>();
        var order = new List<int>();

        for (int p = 0; p < passes.Length; p++)
        {
            PassSignature pass = passes[p];
            AttachmentUse[] attachments = pass.Attachments;

            // First touch in this pass: remember whether the pass only plainly writes it.
            for (int a = 0; a < attachments.Length; a++)
            {
                AttachmentUse use = attachments[a];
                ResourceInfo info = Touch(resources, order, use.ResourceId, p, pass, a);
                info.Attached = true;
                if (!use.Transient) info.AllTransient = false;
                if (info.FirstPass == p && !IsPlainWrite(use.Usage)) info.FirstIsPlainWrite = false;
            }

            int[] reads = pass.Reads;
            for (int i = 0; i < reads.Length; i++)
            {
                ResourceInfo info = Touch(resources, order, reads[i], p, pass, -1);
                if (info.FirstPass == p) info.FirstIsPlainWrite = false;
            }
        }

        var intervals = new List<TransientInterval>();
        for (int i = 0; i < order.Count; i++)
        {
            ResourceInfo info = resources[order[i]];
            if (info.IsTransient)
                intervals.Add(new TransientInterval(info.ResourceId, info.Bucket, info.FirstPass, info.LastPass));
        }

        int[] slots = TransientPlacement.Place(intervals);
        var aliasSlots = new Dictionary<int, int>(intervals.Count);
        int slotCount = 0;
        for (int i = 0; i < intervals.Count; i++)
        {
            aliasSlots[intervals[i].ResourceId] = slots[i];
            if (slots[i] + 1 > slotCount) slotCount = slots[i] + 1;
        }

        var load = new AttachmentLoadOp[passes.Length][];
        var store = new AttachmentStoreOp[passes.Length][];
        for (int p = 0; p < passes.Length; p++)
        {
            AttachmentUse[] attachments = passes[p].Attachments;
            load[p] = new AttachmentLoadOp[attachments.Length];
            store[p] = new AttachmentStoreOp[attachments.Length];
            for (int a = 0; a < attachments.Length; a++)
            {
                AttachmentUse use = attachments[a];
                ResourceInfo info = resources[use.ResourceId];
                bool transient = info.IsTransient;
                load[p][a] = transient && info.FirstPass == p && IsPlainWrite(use.Usage)
                    ? AttachmentLoadOp.DontCare
                    : AttachmentLoadOp.Load;
                store[p][a] = transient && info.LastPass == p
                    ? AttachmentStoreOp.DontCare
                    : AttachmentStoreOp.Store;
            }
        }

        return new FramePlan(passes, load, store, aliasSlots, conservative: false, slotCount);
    }

    /// <summary>The plan for a frame whose signature did not match: LOAD and STORE on every
    /// attachment, no aliasing.</summary>
    public static FramePlan Conservative(IReadOnlyList<PassSignature> frame)
    {
        PassSignature[] passes = Snapshot(frame);
        var load = new AttachmentLoadOp[passes.Length][];
        var store = new AttachmentStoreOp[passes.Length][];
        for (int p = 0; p < passes.Length; p++)
        {
            int count = passes[p].Attachments.Length;
            load[p] = new AttachmentLoadOp[count];
            store[p] = new AttachmentStoreOp[count];
            for (int a = 0; a < count; a++)
            {
                load[p][a] = AttachmentLoadOp.Load;
                store[p][a] = AttachmentStoreOp.Store;
            }
        }

        return new FramePlan(passes, load, store, new Dictionary<int, int>(), conservative: true, 0);
    }

    /// <summary>The plan to apply to <paramref name="frame"/>: <paramref name="previous"/> when
    /// it was built from an identical frame, otherwise a conservative plan.</summary>
    public static FramePlan Select(FramePlan? previous, IReadOnlyList<PassSignature> frame)
    {
        if (previous != null && previous.Matches(frame)) return previous;
        return Conservative(frame);
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

    public AttachmentStoreOp StoreOp(int pass, int attachment)
    {
        CheckIndex(pass, attachment);
        return _store[pass][attachment];
    }

    /// <summary>The alias slot of a transient resource, or -1 when the resource is persistent,
    /// unknown, or the plan is conservative.</summary>
    public int AliasSlot(int resourceId) => _aliasSlots.TryGetValue(resourceId, out int slot) ? slot : -1;

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

    private static ResourceInfo Touch(Dictionary<int, ResourceInfo> resources, List<int> order, int resourceId,
        int pass, PassSignature signature, int attachmentIndex)
    {
        if (!resources.TryGetValue(resourceId, out ResourceInfo? info))
        {
            info = new ResourceInfo(resourceId, pass);
            resources.Add(resourceId, info);
            order.Add(resourceId);
        }

        if (info.FirstPass == pass && attachmentIndex >= 0 && !info.HasBucket)
        {
            info.Bucket = new SizeBucket(signature.Width, signature.Height, signature.FormatsId, attachmentIndex);
            info.HasBucket = true;
        }

        info.LastPass = pass;
        return info;
    }

    private sealed class ResourceInfo
    {
        public ResourceInfo(int resourceId, int firstPass)
        {
            ResourceId = resourceId;
            FirstPass = firstPass;
            LastPass = firstPass;
        }

        public readonly int ResourceId;
        public readonly int FirstPass;
        public int LastPass;
        public bool Attached;
        public bool AllTransient = true;
        public bool FirstIsPlainWrite = true;
        public SizeBucket Bucket;
        public bool HasBucket;

        // A resource first touched only by a read has no bucket, but then FirstIsPlainWrite is false too.
        public bool IsTransient => Attached && AllTransient && FirstIsPlainWrite && HasBucket;
    }
}
