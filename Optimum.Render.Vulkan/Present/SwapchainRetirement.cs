using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

// The Present/ folder follows the plan's layout; the namespace stays Core until
// the renderer is reorganised, like Frame/.
namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Swapchain slots that were replaced but may still be referenced by work the GPU
/// has not finished.
///
/// A slot (its swapchain handle, images, views, acquire and present semaphores)
/// is retired as one unit, keyed on the Frame timeline value of the last present
/// submission that used one of its images. It is destroyed at the first
/// <see cref="Collect" /> that sees the Frame counter at or past that value: the
/// batch that blitted into its image and signalled its present semaphore has
/// completed, and vkQueuePresentKHR, which is synchronous on the CPU, returned
/// before the slot could be replaced. No vkDeviceWaitIdle is involved.
///
/// Render thread only: slots are created, presented and retired there.
/// </summary>
internal sealed class SwapchainRetirement
{
    private readonly record struct Entry(IDisposable Slot, ulong LastPresentValue);

    private readonly ITimelineClock _clock;
    private readonly List<Entry> _entries = new();

    public SwapchainRetirement(ITimelineClock clock) => _clock = clock;

    public int PendingCount => _entries.Count;

    /// <summary>Queues <paramref name="slot" /> until Frame value <paramref name="lastPresentValue" /> completed (0: never presented).</summary>
    public void Retire(IDisposable slot, ulong lastPresentValue) =>
        _entries.Add(new Entry(slot, lastPresentValue));

    /// <summary>Destroys, oldest first, every slot whose last present submission completed. Returns how many.</summary>
    public int Collect()
    {
        if (_entries.Count == 0) return 0;

        ulong completed = _clock.FrameCompleted;
        int destroyed = 0;
        int kept = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (entry.LastPresentValue <= completed)
            {
                entry.Slot.Dispose();
                destroyed++;
            }
            else
            {
                _entries[kept++] = entry;
            }
        }
        _entries.RemoveRange(kept, _entries.Count - kept);
        return destroyed;
    }

    /// <summary>Teardown only, after the GPU finished every submission.</summary>
    public void DisposeAll()
    {
        foreach (Entry entry in _entries) entry.Slot.Dispose();
        _entries.Clear();
    }
}

/// <summary>What the present path does with the result of vkAcquireNextImageKHR.</summary>
internal enum AcquireAction
{
    /// <summary>The image is good; present it.</summary>
    Present,
    /// <summary>SUBOPTIMAL: the image is acquired and usable; rebuild before the next acquire.</summary>
    PresentThenRebuild,
    /// <summary>OUT_OF_DATE on the first attempt: rebuild now and acquire once more.</summary>
    RebuildAndRetry,
    /// <summary>OUT_OF_DATE again after the rebuild: skip presenting this frame, rebuild next frame.</summary>
    SkipFrame,
    /// <summary>Anything else (a lost device above all): reported, never a silent skip.</summary>
    Fail,
}

/// <summary>
/// The swapchain's decisions, as pure functions so they are tested without a
/// window (SwapchainRetirementTests).
/// </summary>
internal static class SwapchainPolicy
{
    /// <summary>
    /// SUBOPTIMAL rebuilds before the next acquire; OUT_OF_DATE rebuilds and
    /// re-acquires once; a second OUT_OF_DATE gives up on this frame.
    /// </summary>
    public static AcquireAction OnAcquire(Result result, int attempt)
    {
        if (result == Result.Success) return AcquireAction.Present;
        if (result == Result.SuboptimalKhr) return AcquireAction.PresentThenRebuild;
        if (result == Result.ErrorOutOfDateKhr) return attempt == 0 ? AcquireAction.RebuildAndRetry : AcquireAction.SkipFrame;
        return AcquireAction.Fail;
    }

    /// <summary>
    /// <c>max(caps.min + 1, mailbox ? 3 : 2)</c>, clamped to the surface maximum
    /// (0 means unbounded). Mailbox wants a third image so a finished frame can
    /// replace the queued one while another is on screen.
    /// </summary>
    public static uint ChooseImageCount(uint capabilitiesMin, uint capabilitiesMax, PresentModeKHR mode)
    {
        uint wanted = Math.Max(capabilitiesMin + 1, mode == PresentModeKHR.MailboxKhr ? 3u : 2u);
        if (capabilitiesMax > 0 && wanted > capabilitiesMax) wanted = capabilitiesMax;
        return wanted;
    }

    /// <summary>A minimised window reports a zero extent; presentation parks until it grows again.</summary>
    public static bool IsParked(Extent2D extent) => extent.Width == 0 || extent.Height == 0;

    /// <summary>
    /// With vsync: FIFO, promoted to FIFO_RELAXED once sustained missed vsyncs
    /// were seen and the surface offers it (a late frame tears instead of waiting
    /// a whole interval). Without vsync: MAILBOX (drops frames, never tears), then
    /// IMMEDIATE, then FIFO, the only mode every driver must have.
    /// </summary>
    public static PresentModeKHR ChoosePresentMode(bool vsync, bool relaxedPromoted, IReadOnlyList<PresentModeKHR> supported)
    {
        if (vsync)
        {
            return relaxedPromoted && Contains(supported, PresentModeKHR.FifoRelaxedKhr)
                ? PresentModeKHR.FifoRelaxedKhr
                : PresentModeKHR.FifoKhr;
        }
        if (Contains(supported, PresentModeKHR.MailboxKhr)) return PresentModeKHR.MailboxKhr;
        if (Contains(supported, PresentModeKHR.ImmediateKhr)) return PresentModeKHR.ImmediateKhr;
        return PresentModeKHR.FifoKhr;
    }

    private static bool Contains(IReadOnlyList<PresentModeKHR> modes, PresentModeKHR mode)
    {
        for (int i = 0; i < modes.Count; i++)
        {
            if (modes[i] == mode) return true;
        }
        return false;
    }
}

/// <summary>
/// A slot's acquire semaphores: <c>imageCount + 1</c> binary semaphores, taken for
/// each vkAcquireNextImageKHR.
///
/// A semaphore whose acquire failed is untouched and returns at once
/// (<see cref="Return" />). One whose signal a present submission waits on stays
/// in use until that submission completed on the GPU: a binary semaphore with an
/// uncompleted wait must not be handed to another acquire
/// (VUID-vkAcquireNextImageKHR-semaphore-01779), so it is parked against the
/// submission's Frame value (<see cref="ReturnAfter" />) and reclaimed by a later
/// <see cref="Take" /> once the Frame counter passed it. A semaphore whose acquire
/// succeeded but was never submitted is never returned; it dies with the slot.
///
/// The ring paces on frame n - FramesInFlight, so at most FramesInFlight - 1 present
/// submissions are uncompleted when an acquire starts; imageCount + 1 covers that.
/// </summary>
internal sealed class AcquireSemaphoreFreeList
{
    private readonly Stack<ulong> _free = new();
    private readonly List<(ulong Handle, ulong FrameValue)> _pending = new();

    public AcquireSemaphoreFreeList(IReadOnlyList<ulong> handles)
    {
        for (int i = handles.Count - 1; i >= 0; i--) _free.Push(handles[i]);
        Capacity = handles.Count;
    }

    public int Capacity { get; }
    public int FreeCount => _free.Count;
    public int PendingCount => _pending.Count;

    public static int CapacityFor(uint imageCount) => (int)imageCount + 1;

    /// <summary>A free semaphore; parked ones whose submission completed (Frame counter <paramref name="frameCompleted" />) are reclaimed first when none is free.</summary>
    public ulong Take(ulong frameCompleted)
    {
        if (_free.Count == 0) Reclaim(frameCompleted);
        if (_free.Count == 0)
        {
            throw new InvalidOperationException(
                "every acquire semaphore of this swapchain is waited on by an uncompleted present submission " +
                "or was signalled by an acquire that was never presented");
        }
        return _free.Pop();
    }

    /// <summary>The acquire failed; the semaphore was never signalled.</summary>
    public void Return(ulong handle)
    {
        CheckRoom();
        _free.Push(handle);
    }

    /// <summary>A submission carrying Frame value <paramref name="frameValue" /> waits on the semaphore.</summary>
    public void ReturnAfter(ulong handle, ulong frameValue)
    {
        CheckRoom();
        _pending.Add((handle, frameValue));
    }

    private void CheckRoom()
    {
        if (_free.Count + _pending.Count >= Capacity) throw new InvalidOperationException("acquire semaphore returned twice");
    }

    private void Reclaim(ulong frameCompleted)
    {
        int kept = 0;
        for (int i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].FrameValue <= frameCompleted) _free.Push(_pending[i].Handle);
            else _pending[kept++] = _pending[i];
        }
        _pending.RemoveRange(kept, _pending.Count - kept);
    }
}

/// <summary>
/// Decides when FIFO should be promoted to FIFO_RELAXED: once enough frames in a
/// window missed their vsync. The refresh interval is estimated as the shortest
/// frame interval seen in the window (under FIFO nothing presents faster than the
/// display), and a frame counts as a miss when it took more than 1.5 of those.
/// A game that is consistently slower than the display never looks like it missed
/// anything, which is intended: relaxed FIFO only helps occasional late frames.
/// </summary>
internal sealed class MissedVsyncDetector
{
    public const int Window = 120;
    public const int MissesToPromote = 12;
    /// <summary>Intervals below this are not display refreshes (a burst after a stall).</summary>
    public const double ShortestPlausibleIntervalMs = 4.0;

    private readonly double[] _intervals = new double[Window];
    private int _count;
    private int _next;

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>Adds one present-to-present interval; true once promotion is warranted (then resets).</summary>
    public bool NoteInterval(double milliseconds)
    {
        if (!(milliseconds > 0) || double.IsInfinity(milliseconds)) return false;

        _intervals[_next] = milliseconds;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
        if (_count < Window) return false;

        double period = double.MaxValue;
        for (int i = 0; i < Window; i++)
        {
            if (_intervals[i] >= ShortestPlausibleIntervalMs && _intervals[i] < period) period = _intervals[i];
        }
        if (period == double.MaxValue) return false;

        int misses = 0;
        for (int i = 0; i < Window; i++)
        {
            if (_intervals[i] > period * 1.5) misses++;
        }
        if (misses < MissesToPromote) return false;

        Reset();
        return true;
    }
}
