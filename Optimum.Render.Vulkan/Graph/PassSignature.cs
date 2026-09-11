using System;
using System.Collections.Generic;

namespace Optimum.Render.Vulkan.Graph;

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
