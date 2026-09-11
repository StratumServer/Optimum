using System;
using System.Threading;
using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

// The Frame/ folder follows the plan's layout; the namespace stays Core until the
// renderer is reorganised, so every existing consumer keeps its one using.
namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The four numbers a <see cref="RetireQueue" /> needs from the timelines. An
/// interface so the lifetime rules can be tested without a device.
/// </summary>
internal interface ITimelineClock
{
    /// <summary>
    /// The newest Frame value any command recorded or submitted so far carries:
    /// a resource released now can only be referenced by work at or below it.
    /// </summary>
    ulong FrameRecorded { get; }

    /// <summary>The same for the Transfer timeline.</summary>
    ulong TransferRecorded { get; }

    /// <summary>The Frame value the GPU has finished (the semaphore's counter).</summary>
    ulong FrameCompleted { get; }

    /// <summary>The Transfer value the GPU has finished.</summary>
    ulong TransferCompleted { get; }
}

/// <summary>
/// The renderer's clock: two timeline semaphores.
///
/// Every graphics submission of frame <c>n</c> signals <see cref="Frame" /> to
/// <c>n</c>; transfer work signals <see cref="Transfer" />. Pacing waits on the
/// Frame timeline, and deferred destruction compares recorded values against the
/// counters, so nothing in steady state needs a fence.
///
/// Values are reserved before recording (<see cref="ReserveFrame" />) and noted as
/// signalled once the submit that carries them was accepted
/// (<see cref="NoteFrameSubmitted" />). Waits are clamped to the signalled value:
/// waiting for a value no submission will ever signal would hang forever.
///
/// Presentation cannot wait on a timeline, so the per-image present semaphores
/// stay binary and live in the swapchain.
/// </summary>
internal sealed unsafe class FrameTimeline : ITimelineClock, IDisposable
{
    private readonly VulkanContext _context;
    private long _frameReserved;
    private long _frameSignalled;
    private long _transferReserved;
    private long _transferSignalled;
    private bool _disposed;

    public Semaphore Frame { get; }
    public Semaphore Transfer { get; }

    public FrameTimeline(VulkanContext context)
    {
        _context = context;
        Frame = CreateTimeline(context, "the Frame timeline semaphore");
        Transfer = CreateTimeline(context, "the Transfer timeline semaphore");
    }

    private static Semaphore CreateTimeline(VulkanContext context, string what)
    {
        var type = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var info = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &type,
        };
        Semaphore semaphore;
        VulkanResult.Check(context.Api.CreateSemaphore(context.Device, &info, null, &semaphore),
            "vkCreateSemaphore for " + what);
        return semaphore;
    }

    // ------------------------------------------------------------------ Frame

    /// <summary>The value the next frame's submission will signal. Render thread.</summary>
    public ulong ReserveFrame() => (ulong)Interlocked.Increment(ref _frameReserved);

    /// <summary>The submission carrying <paramref name="value" /> was accepted by the queue.</summary>
    public void NoteFrameSubmitted(ulong value) => RaiseTo(ref _frameSignalled, value);

    public ulong FrameRecorded => (ulong)Interlocked.Read(ref _frameReserved);

    /// <summary>The newest Frame value handed to an accepted submission.</summary>
    public ulong FrameSignalled => (ulong)Interlocked.Read(ref _frameSignalled);

    public ulong FrameCompleted => Counter(Frame, "the Frame timeline");

    /// <summary>
    /// Blocks until the GPU finished Frame value <paramref name="value" /> (clamped
    /// to what was signalled) and counts one wait at <paramref name="site" />, even
    /// when the value has already passed: the count is the number of pacing points,
    /// which is what the stats gate compares.
    /// </summary>
    public void WaitForFrame(ulong value, WaitSite site) =>
        Wait(Frame, WaitTarget(value, FrameSignalled), site, "the Frame timeline");

    /// <summary>
    /// Teardown only: waits for every signalled frame before the caller destroys
    /// what those frames name, and never throws (a lost device or an exception
    /// unwinding past the caller's own idle wait must not turn into a driver crash
    /// on destroying objects a queued command buffer still uses).
    /// </summary>
    public void WaitForSignalledFramesAtTeardown()
    {
        Semaphore handle = Frame;
        ulong target = FrameSignalled;
        var info = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &handle,
            PValues = &target,
        };
        long waitStart = VulkanStats.WaitStart();
        _context.Api.WaitSemaphores(_context.Device, &info, 5UL * 1000 * 1000 * 1000);
        VulkanStats.NoteWait(WaitSite.DeviceWaitIdle, waitStart);
    }

    // --------------------------------------------------------------- Transfer

    /// <summary>The value the next transfer submission will signal.</summary>
    public ulong ReserveTransfer() => (ulong)Interlocked.Increment(ref _transferReserved);

    public void NoteTransferSubmitted(ulong value) => RaiseTo(ref _transferSignalled, value);

    public ulong TransferRecorded => (ulong)Interlocked.Read(ref _transferReserved);

    public ulong TransferSignalled => (ulong)Interlocked.Read(ref _transferSignalled);

    public ulong TransferCompleted => Counter(Transfer, "the Transfer timeline");

    public void WaitForTransfer(ulong value, WaitSite site) =>
        Wait(Transfer, WaitTarget(value, TransferSignalled), site, "the Transfer timeline");

    // ------------------------------------------------------------------ rules

    /// <summary>
    /// The Frame value frame <paramref name="frameValue" /> waits for before it
    /// starts: <c>n - framesInFlight</c>, the frame that last used the same slot,
    /// saturating at zero for the first frames.
    /// </summary>
    public static ulong PacingTarget(ulong frameValue, int framesInFlight)
    {
        ulong inFlight = (ulong)Math.Max(1, framesInFlight);
        return frameValue > inFlight ? frameValue - inFlight : 0;
    }

    /// <summary>Never wait past the newest signalled value; nothing would ever wake the wait.</summary>
    public static ulong WaitTarget(ulong requested, ulong signalled) => Math.Min(requested, signalled);

    // ---------------------------------------------------------------- helpers

    private ulong Counter(Semaphore semaphore, string what)
    {
        ulong value;
        VulkanResult.Check(_context.Api.GetSemaphoreCounterValue(_context.Device, semaphore, &value),
            "vkGetSemaphoreCounterValue on " + what);
        return value;
    }

    private void Wait(Semaphore semaphore, ulong value, WaitSite site, string what)
    {
        Semaphore handle = semaphore;
        ulong target = value;
        var info = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &handle,
            PValues = &target,
        };

        long waitStart = VulkanStats.WaitStart();
        Result result = _context.Api.WaitSemaphores(_context.Device, &info, ulong.MaxValue);
        VulkanStats.NoteWait(site, waitStart);
        VulkanResult.Check(result, "vkWaitSemaphores on " + what);
    }

    private static void RaiseTo(ref long field, ulong value)
    {
        long wanted = (long)Math.Min(value, long.MaxValue);
        long seen = Interlocked.Read(ref field);
        while (wanted > seen)
        {
            long previous = Interlocked.CompareExchange(ref field, wanted, seen);
            if (previous == seen) break;
            seen = previous;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Api.DestroySemaphore(_context.Device, Frame, null);
        _context.Api.DestroySemaphore(_context.Device, Transfer, null);
    }
}
