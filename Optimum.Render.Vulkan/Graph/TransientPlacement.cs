using System;
using System.Collections.Generic;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// Images that may share one allocation: same extent and the same format. The format is not
/// part of a pass signature per attachment, so it is identified by the pass's interned format
/// list plus the attachment index, taken from the resource's first pass. That key can refuse
/// aliasing two images that would have been compatible; it can never alias two that are not.
/// </summary>
internal readonly record struct SizeBucket(int Width, int Height, int FormatsId, int AttachmentIndex);

/// <summary>A transient resource's lifetime within one frame, both ends inclusive.</summary>
internal readonly record struct TransientInterval(int ResourceId, SizeBucket Bucket, int FirstPass, int LastPass);

/// <summary>
/// Assigns alias slots to transient resources. Greedy first-fit in order of first pass (then
/// resource id, so the result is deterministic): a resource takes the lowest-numbered slot of
/// its own bucket whose previous occupant's last pass is strictly before this resource's first
/// pass, otherwise it opens a new slot. Two intervals in one slot therefore never overlap, a
/// slot only ever holds one bucket, and because intervals are taken by start time the slot
/// count per bucket equals the largest number of that bucket's lifetimes alive at one pass.
/// </summary>
internal static class TransientPlacement
{
    /// <summary>Returns one slot per input interval, in input order. Slots are dense from 0.</summary>
    public static int[] Place(IReadOnlyList<TransientInterval> intervals)
    {
        if (intervals == null) throw new ArgumentNullException(nameof(intervals));

        int count = intervals.Count;
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            TransientInterval interval = intervals[i];
            if (interval.FirstPass < 0 || interval.LastPass < interval.FirstPass)
                throw new ArgumentException($"Interval for resource {interval.ResourceId} is [{interval.FirstPass},{interval.LastPass}].", nameof(intervals));
            order[i] = i;
        }

        Array.Sort(order, new StartOrder(intervals));

        int[] slots = new int[count];
        var slotBucket = new List<SizeBucket>();
        var slotLastPass = new List<int>();
        for (int k = 0; k < count; k++)
        {
            int index = order[k];
            TransientInterval interval = intervals[index];
            int chosen = -1;
            for (int s = 0; s < slotBucket.Count; s++)
            {
                if (slotBucket[s] == interval.Bucket && slotLastPass[s] < interval.FirstPass)
                {
                    chosen = s;
                    break;
                }
            }

            if (chosen < 0)
            {
                chosen = slotBucket.Count;
                slotBucket.Add(interval.Bucket);
                slotLastPass.Add(interval.LastPass);
            }
            else
            {
                slotLastPass[chosen] = interval.LastPass;
            }

            slots[index] = chosen;
        }

        return slots;
    }

    private sealed class StartOrder : IComparer<int>
    {
        private readonly IReadOnlyList<TransientInterval> _intervals;

        public StartOrder(IReadOnlyList<TransientInterval> intervals) => _intervals = intervals;

        public int Compare(int x, int y)
        {
            TransientInterval a = _intervals[x];
            TransientInterval b = _intervals[y];
            int c = a.FirstPass.CompareTo(b.FirstPass);
            if (c != 0) return c;
            c = a.ResourceId.CompareTo(b.ResourceId);
            return c != 0 ? c : x.CompareTo(y);
        }
    }
}
