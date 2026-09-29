using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The bookkeeping of the per-slot indirect-command ring, with no Vulkan in it.
///
/// Each frame slot owns one indirect buffer. Multi-draws of a frame bump-allocate
/// from the slot's buffer, and the cursor returns to zero only when the slot
/// begins its next frame, which is after the Frame timeline says the GPU has
/// finished the previous frame that used it. The ring never wraps inside a frame:
/// the wrapping ring this replaces could hand a new draw the region a frame still
/// in flight was reading.
///
/// A frame that asks for more than its slot holds is told so (<see cref="TryAllocate" />
/// returns false) and the caller takes an overflow buffer for the rest of that
/// frame. The slot's buffer grows only at a frame boundary, to fit the busiest
/// frame any slot has seen.
/// </summary>
internal sealed class IndirectRing
{
    /// <summary>Smallest buffer a slot is given: 13107 indexed indirect commands.</summary>
    public const ulong DefaultMinimumCapacity = 256UL * 1024;

    private const ulong Granularity = 64UL * 1024;

    private struct Slot
    {
        public ulong Capacity;
        public ulong Cursor;
        public ulong Usage;
        public int Overflows;
    }

    private readonly Slot[] _slots;
    private int _current = -1;

    public IndirectRing(int slots, ulong minimumCapacity = DefaultMinimumCapacity)
    {
        if (slots <= 0) throw new ArgumentOutOfRangeException(nameof(slots));
        _slots = new Slot[slots];
        MinimumCapacity = Math.Max(1UL, minimumCapacity);
    }

    public ulong MinimumCapacity { get; }

    /// <summary>The slot recording now, -1 before the first frame.</summary>
    public int Current => _current;

    /// <summary>Bytes the busiest frame so far asked for, overflow included. Never shrinks.</summary>
    public ulong PeakFrameUsage { get; private set; }

    public ulong CapacityOf(int slot) => _slots[slot].Capacity;
    public ulong CursorOf(int slot) => _slots[slot].Cursor;
    public ulong FrameUsageOf(int slot) => _slots[slot].Usage;

    /// <summary>Allocations of the slot's current frame that did not fit its buffer.</summary>
    public int OverflowsOf(int slot) => _slots[slot].Overflows;

    /// <summary>
    /// A buffer size that holds <paramref name="demand" /> bytes with half again as
    /// much headroom, rounded to 64 KiB, and never below the minimum.
    /// </summary>
    public ulong CapacityFor(ulong demand)
    {
        // A ring whose minimum is below the granularity (tests) rounds to its minimum.
        ulong granularity = Math.Min(Granularity, MinimumCapacity);
        ulong wanted = demand + demand / 2;
        ulong rounded = (wanted + granularity - 1) / granularity * granularity;
        return Math.Max(MinimumCapacity, rounded);
    }

    /// <summary>
    /// Starts <paramref name="slot" />'s next frame: folds every slot's last frame
    /// usage into the peak, resets this slot's cursor, and reports whether its
    /// existing buffer is too small for the peak. When it is, the caller retires
    /// the old buffer, creates one of <paramref name="capacity" /> bytes and calls
    /// <see cref="Attach" />. A slot without a buffer is not grown here; it gets
    /// one at its first allocation.
    /// </summary>
    public bool BeginFrame(int slot, out ulong capacity)
    {
        foreach (Slot previous in _slots) PeakFrameUsage = Math.Max(PeakFrameUsage, previous.Usage);

        _current = slot;
        ref Slot current = ref _slots[slot];
        current.Cursor = 0;
        current.Usage = 0;
        current.Overflows = 0;

        capacity = CapacityFor(PeakFrameUsage);
        return current.Capacity != 0 && current.Capacity < PeakFrameUsage;
    }

    /// <summary>
    /// Whether the current slot has no buffer yet, and the size to create when it
    /// has none. Creating one is safe at any time: nothing recorded names it.
    /// </summary>
    public bool NeedsBuffer(ulong bytes, out ulong capacity)
    {
        RequireFrame();
        capacity = CapacityFor(Math.Max(PeakFrameUsage, _slots[_current].Usage + bytes));
        return _slots[_current].Capacity == 0;
    }

    /// <summary>Records that the current slot's buffer now holds <paramref name="capacity" /> bytes.</summary>
    public void Attach(ulong capacity)
    {
        RequireFrame();
        if (capacity < _slots[_current].Cursor) throw new InvalidOperationException("a slot buffer cannot shrink under its cursor");
        _slots[_current].Capacity = capacity;
    }

    /// <summary>
    /// Bump-allocates <paramref name="bytes" /> in the current slot's buffer. Never
    /// wraps: when the rest of the buffer is too small, returns false and leaves the
    /// cursor where it is. Either way the bytes count toward this frame's usage.
    /// </summary>
    public bool TryAllocate(ulong bytes, out ulong offset)
    {
        RequireFrame();
        ref Slot current = ref _slots[_current];
        current.Usage += bytes;

        if (current.Cursor + bytes > current.Capacity)
        {
            current.Overflows++;
            offset = 0;
            return false;
        }

        offset = current.Cursor;
        current.Cursor += bytes;
        return true;
    }

    private void RequireFrame()
    {
        if (_current < 0) throw new InvalidOperationException("BeginFrame has not been called yet");
    }
}
