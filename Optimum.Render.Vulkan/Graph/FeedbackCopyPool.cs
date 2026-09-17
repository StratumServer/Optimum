using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>The shape of a ReadSelf copy: copies with equal descriptions are interchangeable.</summary>
internal readonly record struct FeedbackCopyDesc(uint Width, uint Height, Format Format, uint MipLevels, uint Layers, bool Cube);

/// <summary>
/// Pooled ReadSelf copies: the snapshot a draw samples when it reads a colour attachment
/// it is also writing (atlas composition). Replaces the permanent copy per source texture.
///
/// A pass takes a copy (<see cref="Acquire" />) and gives it back when it ends
/// (<see cref="Release" />). Within the frame being recorded a released copy is reused at
/// once: the commands that sampled it come earlier in the same command stream. A copy
/// released in an earlier frame is reused only after the Frame timeline completed the value
/// recorded when that frame ended (<see cref="EndFrame" />, <see cref="Collect" />), and a
/// copy that stays free for <see cref="IdleFrames" /> frames is destroyed (the device
/// retires it on the timeline).
/// </summary>
internal sealed class FeedbackCopyPool
{
    private sealed class Copy
    {
        public int TextureId;
        public FeedbackCopyDesc Desc;
        public ulong RetiredAt;
        public long FreeSince;
    }

    private readonly ITimelineClock _clock;
    private readonly Func<FeedbackCopyDesc, int> _create;
    private readonly Action<int> _destroy;
    private readonly Dictionary<int, Copy> _inUse = new();
    private readonly List<Copy> _releasedThisFrame = new();
    private readonly List<Copy> _retiring = new();
    private readonly List<Copy> _free = new();
    private long _frame;

    public FeedbackCopyPool(ITimelineClock clock, Func<FeedbackCopyDesc, int> create, Action<int> destroy,
        int idleFrames = 120)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
        IdleFrames = idleFrames;
    }

    public int IdleFrames { get; }

    /// <summary>Copies taken and not yet released.</summary>
    public int InUse => _inUse.Count;

    /// <summary>Copies released in earlier frames whose timeline value has not completed.</summary>
    public int Retiring => _retiring.Count;

    /// <summary>Copies ready for any frame.</summary>
    public int Free => _free.Count + _releasedThisFrame.Count;

    /// <summary>Every copy the pool holds.</summary>
    public int Live => _inUse.Count + _releasedThisFrame.Count + _retiring.Count + _free.Count;

    /// <summary>Copies created so far.</summary>
    public long Created { get; private set; }

    /// <summary>Copies destroyed so far.</summary>
    public long Destroyed { get; private set; }

    /// <summary>A copy of <paramref name="desc" /> for one pass; its texture id.</summary>
    public int Acquire(FeedbackCopyDesc desc)
    {
        Copy? copy = Take(_releasedThisFrame, desc) ?? Take(_free, desc);
        if (copy == null)
        {
            copy = new Copy { TextureId = _create(desc), Desc = desc };
            Created++;
        }
        _inUse.Add(copy.TextureId, copy);
        return copy.TextureId;
    }

    /// <summary>Gives a copy back at the end of its pass. Unknown ids are ignored.</summary>
    public void Release(int copyId)
    {
        if (_inUse.Remove(copyId, out Copy? copy)) _releasedThisFrame.Add(copy);
    }

    /// <summary>
    /// Ends the recorded frame: its released copies wait for the Frame value recorded now.
    /// Call before the next frame reserves its value.
    /// </summary>
    public void EndFrame()
    {
        if (_releasedThisFrame.Count == 0) return;
        ulong recorded = _clock.FrameRecorded;
        foreach (Copy copy in _releasedThisFrame)
        {
            copy.RetiredAt = recorded;
            _retiring.Add(copy);
        }
        _releasedThisFrame.Clear();
    }

    /// <summary>
    /// Frees the copies whose timeline value completed and destroys copies free for more
    /// than <see cref="IdleFrames" /> frames. Call once per frame after <see cref="EndFrame" />.
    /// </summary>
    public void Collect()
    {
        _frame++;
        ulong completed = _clock.FrameCompleted;
        int kept = 0;
        for (int i = 0; i < _retiring.Count; i++)
        {
            Copy copy = _retiring[i];
            if (copy.RetiredAt <= completed)
            {
                copy.FreeSince = _frame;
                _free.Add(copy);
            }
            else
            {
                _retiring[kept++] = copy;
            }
        }
        _retiring.RemoveRange(kept, _retiring.Count - kept);

        kept = 0;
        for (int i = 0; i < _free.Count; i++)
        {
            Copy copy = _free[i];
            if (_frame - copy.FreeSince > IdleFrames)
            {
                _destroy(copy.TextureId);
                Destroyed++;
            }
            else
            {
                _free[kept++] = copy;
            }
        }
        _free.RemoveRange(kept, _free.Count - kept);
    }

    private static Copy? Take(List<Copy> list, FeedbackCopyDesc desc)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Desc != desc) continue;
            Copy copy = list[i];
            list.RemoveAt(i);
            return copy;
        }
        return null;
    }
}
