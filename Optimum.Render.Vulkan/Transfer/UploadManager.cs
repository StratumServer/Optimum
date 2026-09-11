using System;
using System.Collections.Generic;
using System.Threading;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

// The Transfer/ folder follows the plan's layout; the namespace stays Core until
// the renderer is reorganised.
namespace Optimum.Render.Vulkan.Core;

/// <summary>Where staged bytes landed: the buffer, the offset inside it, and its host mapping.</summary>
internal readonly record struct StagingSlice(Buffer Buffer, ulong Offset, IntPtr Pointer);

/// <summary>
/// Uploads that never wait (transfer backend A of the Vulkan-native plan).
///
/// Uploads, mip chains and buffer copies are recorded into an open upload batch:
/// a command buffer from the batch's own graphics-family pool plus a bump
/// allocated slice of the staging ring. Any thread may record, under this
/// manager's lock. The next frame submission (a frame end or a partial submit)
/// closes the batch and submits it first, in the same vkQueueSubmit and the same
/// SubmitInfo as the frame's command buffer, so the upload executes before the
/// frame that samples it and nothing waits for it.
///
/// Each batch reserves a Transfer timeline value when it opens and that
/// submission signals it, alongside the Frame value. Every resource retired
/// while a batch is open is keyed on that Transfer value by the
/// <see cref="RetireQueue" />, so a worker's upload can never name a texture or
/// staging buffer that was already destroyed. Every reserved value is signalled
/// by exactly one submission: the frame's, or <see cref="SubmitStandalone" />
/// between frames.
///
/// GL executes calls in order. The batch runs before the whole frame command
/// buffer, so an upload to a texture or buffer the frame command buffer already
/// used since its last submission would travel back in time past those uses.
/// Those uploads (render thread only; the frame command buffer is the render
/// thread's) are recorded inline into the frame command buffer instead, outside
/// any rendering scope: still no wait, and GL's order holds.
///
/// Staging: one persistently mapped ring of FramesInFlight x
/// <see cref="DefaultStagingPerSlot" />, one region per batch. A batch is reused
/// once the Transfer timeline passed its value. An upload larger than the region's
/// free space, or any upload of a batch beyond the ring's (created when more
/// batches are in flight than the ring has regions), takes a dedicated staging
/// buffer retired on the timelines and counted.
/// </summary>
internal sealed unsafe class UploadManager : IDisposable
{
    public const ulong DefaultStagingPerSlot = 32UL << 20;

    // Buffer-to-image copies of depth need offsets that are multiples of 4, and
    // wider texels want their own size; 16 covers every format the client uploads.
    private const ulong StagingAlignment = 16;

    private sealed class Batch
    {
        public CommandPool Pool;
        public CommandBuffer CommandBuffer;
        public ulong RegionStart;
        public ulong RegionSize;
        public ulong Cursor;
        public ulong TransferValue;
        public bool Submitted;
        public bool EverOpened;
    }

    private readonly VulkanContext _context;
    private readonly FrameTimeline _timeline;
    private readonly RetireQueue _retired;
    private readonly int _ringBatches;
    private readonly ulong _stagingPerSlot;
    private readonly object _lock = new();
    private readonly List<Batch> _batches = new();
    private VulkanBuffer? _stagingRing;
    private Batch? _open;
    private bool _disposed;

    // The frame command buffer being recorded, set by the frame slot: the handle
    // (0 between submissions), the thread recording it, and a generation that
    // changes with every new frame command buffer. Written by the render thread,
    // read by any.
    private long _frameCommandsHandle;
    private int _frameThreadId = -1;
    private long _frameGeneration;

    /// <summary>
    /// Closes the device's open rendering scope on the frame command buffer before
    /// an inline upload records transfer commands into it. Null outside a device.
    /// </summary>
    public Action<CommandBuffer>? CloseRenderingScope { get; set; }

    public UploadManager(VulkanContext context, FrameTimeline timeline, RetireQueue retired,
        int framesInFlight, ulong stagingPerSlot = DefaultStagingPerSlot)
    {
        _context = context;
        _timeline = timeline;
        _retired = retired;
        _ringBatches = Math.Max(1, framesInFlight);
        _stagingPerSlot = Math.Max(StagingAlignment, stagingPerSlot / StagingAlignment * StagingAlignment);
    }

    /// <summary>Upload batches created so far (the ring's plus any beyond it). Tests only.</summary>
    internal int BatchCount
    {
        get { lock (_lock) return _batches.Count; }
    }

    /// <summary>Whether a batch is open and will ride the next submission. Tests only.</summary>
    internal bool HasOpenBatch
    {
        get { lock (_lock) return _open != null; }
    }

    // ------------------------------------------------------------ frame hooks

    /// <summary>
    /// The frame slot began a new command buffer on the calling thread. Uses
    /// noted against the previous one stop counting.
    /// </summary>
    public void OnFrameCommandsStarted(CommandBuffer commandBuffer)
    {
        Volatile.Write(ref _frameThreadId, Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _frameGeneration);
        Volatile.Write(ref _frameCommandsHandle, (long)commandBuffer.Handle);
    }

    /// <summary>Whether the calling thread is recording a frame command buffer right now.</summary>
    public bool IsFrameRecordingThread =>
        Volatile.Read(ref _frameCommandsHandle) != 0 &&
        Volatile.Read(ref _frameThreadId) == Environment.CurrentManagedThreadId;

    /// <summary>Records that <paramref name="commandBuffer" />, if it is the frame's, uses the texture.</summary>
    public void NoteUse(CommandBuffer commandBuffer, VulkanTexture texture)
    {
        if (IsFrameCommands(commandBuffer)) texture.FrameUse = Volatile.Read(ref _frameGeneration);
    }

    /// <summary>Records that <paramref name="commandBuffer" />, if it is the frame's, uses the buffer.</summary>
    public void NoteUse(CommandBuffer commandBuffer, VulkanBuffer buffer)
    {
        if (IsFrameCommands(commandBuffer)) buffer.FrameUse = Volatile.Read(ref _frameGeneration);
    }

    private bool IsFrameCommands(CommandBuffer commandBuffer) =>
        commandBuffer.Handle != 0 && (long)commandBuffer.Handle == Volatile.Read(ref _frameCommandsHandle);

    /// <summary>
    /// Whether a resource whose last noted use is <paramref name="frameUse" /> is
    /// used by the frame command buffer the calling thread is recording, which
    /// makes an upload to it an inline one.
    /// </summary>
    public bool UsedByPendingFrame(long frameUse) =>
        frameUse != 0 && frameUse == Volatile.Read(ref _frameGeneration) && IsFrameRecordingThread;

    // --------------------------------------------------------------- recording

    /// <summary>
    /// Takes the lock and returns the command buffer to record an upload into:
    /// the frame command buffer (scope closed) when <paramref name="inlineInFrame" />
    /// holds and the calling thread records the frame, the open batch's otherwise.
    /// Pair with <see cref="EndRecording" /> in a finally. Reentrant.
    /// </summary>
    public CommandBuffer BeginRecording(bool inlineInFrame)
    {
        Monitor.Enter(_lock);
        try
        {
            if (inlineInFrame && IsFrameRecordingThread)
            {
                var frameCommands = new CommandBuffer((nint)Volatile.Read(ref _frameCommandsHandle));
                CloseRenderingScope?.Invoke(frameCommands);
                VulkanStats.NoteInlineUpload();
                return frameCommands;
            }
            return EnsureOpenLocked().CommandBuffer;
        }
        catch
        {
            Monitor.Exit(_lock);
            throw;
        }
    }

    public void EndRecording() => Monitor.Exit(_lock);

    /// <summary>
    /// Bump-allocates <paramref name="size" /> staging bytes for the upload being
    /// recorded (inside <see cref="BeginRecording" />). Valid until the batch's
    /// submission, which carries every command recorded in the meantime, completed.
    /// </summary>
    public StagingSlice Stage(ulong size)
    {
        if (!Monitor.IsEntered(_lock)) throw new InvalidOperationException("Stage outside BeginRecording");

        Batch batch = EnsureOpenLocked();
        if (batch.RegionSize > 0)
        {
            ulong aligned = (batch.Cursor + StagingAlignment - 1) / StagingAlignment * StagingAlignment;
            if (aligned + size <= batch.RegionSize)
            {
                VulkanBuffer ring = StagingRing();
                batch.Cursor = aligned + size;
                ulong absolute = batch.RegionStart + aligned;
                return new StagingSlice(ring.Handle, absolute, ring.Mapped + (nint)absolute);
            }
        }

        // Oversized, overflowing, or a batch beyond the ring: its own buffer. The
        // open batch's Transfer value (and the newest Frame value, for an inline
        // copy) is what the retire entry is keyed on, so it outlives the copy.
        var dedicated = new VulkanBuffer(_context, Math.Max(size, 1), BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);
        _retired.Retire(dedicated);
        VulkanStats.NoteStagingOverflow();
        return new StagingSlice(dedicated.Handle, 0, dedicated.Mapped);
    }

    /// <summary>
    /// Copies bytes into a buffer the host cannot map. Inline when the frame
    /// command buffer being recorded already uses the buffer, batched otherwise.
    /// </summary>
    public void UploadToBuffer(VulkanBuffer destination, ulong offset, IntPtr source, ulong size)
    {
        if (source == IntPtr.Zero || size == 0) return;

        VulkanStats.NoteUploadRequest();
        CommandBuffer commandBuffer = BeginRecording(UsedByPendingFrame(destination.FrameUse));
        try
        {
            StagingSlice staging = Stage(size);
            System.Buffer.MemoryCopy((void*)source, (void*)staging.Pointer, (long)size, (long)size);

            // Buffers have no layouts, so nothing else orders this copy against the
            // draws around it: a frame that read the buffer before, and the frame
            // command buffer (a later one in the same submission) that reads it
            // after. Synchronization validation reports both as hazards without an
            // explicit buffer barrier on each side (2026-09-11, the staged index
            // buffer of AsyncTransferTests).
            BufferBarrier(commandBuffer, destination,
                PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit);
            var copy = new BufferCopy { SrcOffset = staging.Offset, DstOffset = offset, Size = size };
            _context.Api.CmdCopyBuffer(commandBuffer, staging.Buffer, destination.Handle, 1, &copy);
            BufferBarrier(commandBuffer, destination,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
            NoteUse(commandBuffer, destination);
        }
        finally
        {
            EndRecording();
        }
    }

    /// <summary>A synchronization2 barrier on a whole buffer; no queue family change.</summary>
    private void BufferBarrier(CommandBuffer commandBuffer, VulkanBuffer buffer,
        PipelineStageFlags2 sourceStage, AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage, AccessFlags2 destinationAccess)
    {
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer.Handle,
            Offset = 0,
            Size = Vk.WholeSize,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private VulkanBuffer StagingRing() =>
        // Allocated on first use: most frame rings in tests never stage anything.
        _stagingRing ??= new VulkanBuffer(_context, _stagingPerSlot * (ulong)_ringBatches,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);

    private Batch EnsureOpenLocked()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UploadManager));
        if (_open != null) return _open;

        Batch? free = null;
        ulong completed = 0;
        bool completedRead = false;
        for (int i = 0; i < _batches.Count; i++)
        {
            Batch candidate = _batches[i];
            if (!candidate.Submitted)
            {
                free = candidate;
                break;
            }
            if (!completedRead)
            {
                completed = _timeline.TransferCompleted;
                completedRead = true;
            }
            if (completed >= candidate.TransferValue)
            {
                free = candidate;
                break;
            }
        }

        free ??= CreateBatch();

        Vk api = _context.Api;
        if (free.EverOpened)
        {
            // The Transfer timeline passed the batch's submission: every command
            // buffer of the pool has completed, so resetting it is legal.
            VulkanResult.Check(api.ResetCommandPool(_context.Device, free.Pool, 0),
                "vkResetCommandPool for an upload batch");
        }

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanResult.Check(api.BeginCommandBuffer(free.CommandBuffer, &begin),
            "vkBeginCommandBuffer for an upload batch");

        free.Cursor = 0;
        free.Submitted = false;
        free.EverOpened = true;
        free.TransferValue = _timeline.ReserveTransfer();
        _open = free;
        return free;
    }

    private Batch CreateBatch()
    {
        Vk api = _context.Api;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _context.GraphicsQueueFamily,
            Flags = CommandPoolCreateFlags.TransientBit,
        };
        VulkanResult.Check(api.CreateCommandPool(_context.Device, &poolInfo, null, out CommandPool pool),
            "vkCreateCommandPool for an upload batch");

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer commandBuffer;
        VulkanResult.Check(api.AllocateCommandBuffers(_context.Device, &allocateInfo, &commandBuffer),
            "vkAllocateCommandBuffers for an upload batch");

        int index = _batches.Count;
        var batch = new Batch
        {
            Pool = pool,
            CommandBuffer = commandBuffer,
            RegionStart = index < _ringBatches ? _stagingPerSlot * (ulong)index : 0,
            RegionSize = index < _ringBatches ? _stagingPerSlot : 0,
        };
        _batches.Add(batch);
        if (index >= _ringBatches) VulkanStats.NoteUploadBatchGrowth();
        return batch;
    }

    // -------------------------------------------------------------- submission

    /// <summary>
    /// Takes the recording lock without recording, for owners whose table changes
    /// must be ordered against uploads: a texture deleted under it either was
    /// recorded into the open batch first (so its retirement is keyed on that
    /// batch's Transfer value) or is not found by an upload that looks it up after.
    /// </summary>
    public void EnterLock() => Monitor.Enter(_lock);

    public void ExitLock() => Monitor.Exit(_lock);

    /// <summary>Holds the lock across a frame submission; see <see cref="TakeOpenBatchLocked" />.</summary>
    public void EnterSubmit() => Monitor.Enter(_lock);

    public void ExitSubmit() => Monitor.Exit(_lock);

    /// <summary>
    /// Inside <see cref="EnterSubmit" />: closes the open batch, if any, for the
    /// submission being built, which must put its command buffer first and signal
    /// the Transfer timeline to <paramref name="transferValue" />.
    /// </summary>
    public bool TakeOpenBatchLocked(out CommandBuffer commandBuffer, out ulong transferValue)
    {
        Batch? batch = _open;
        if (batch == null)
        {
            commandBuffer = default;
            transferValue = 0;
            return false;
        }

        VulkanResult.Check(_context.Api.EndCommandBuffer(batch.CommandBuffer),
            "vkEndCommandBuffer for an upload batch");
        batch.Submitted = true;
        _open = null;
        commandBuffer = batch.CommandBuffer;
        transferValue = batch.TransferValue;
        return true;
    }

    /// <summary>
    /// Inside <see cref="EnterSubmit" />, after the queue accepted the submission:
    /// the frame command buffer is closed, so nothing can be recorded inline until
    /// the slot starts the next one.
    /// </summary>
    public void OnFrameCommandsSubmittedLocked() => Volatile.Write(ref _frameCommandsHandle, 0);

    /// <summary>
    /// Between frames: submits the open batch on its own (opening an empty one if
    /// none is open, so a caller always gets a value to wait on) and returns its
    /// Transfer value. Never waits; a readback that needs the bytes waits on the
    /// returned value. Refused while a frame is recording: its submission carries
    /// the batch, and the batch may hold staging an inline copy reads.
    /// </summary>
    public ulong SubmitStandalone()
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _frameCommandsHandle) != 0)
            {
                throw new InvalidOperationException(
                    "a frame is being recorded; its submission carries the open upload batch");
            }

            EnsureOpenLocked();
            TakeOpenBatchLocked(out CommandBuffer commandBuffer, out ulong transferValue);

            Semaphore transfer = _timeline.Transfer;
            ulong value = transferValue;
            var timelineInfo = new TimelineSemaphoreSubmitInfo
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                SignalSemaphoreValueCount = 1,
                PSignalSemaphoreValues = &value,
            };
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &transfer,
            };

            // Timed like a frame submission: the lock is shared with the swapchain.
            long submitStart = VulkanStats.WaitStart();
            lock (_context.QueueLock)
            {
                VulkanResult.Check(_context.Api.QueueSubmit(_context.GraphicsQueue, 1, &submit, default(Fence)),
                    "vkQueueSubmit for an upload batch");
            }
            VulkanStats.NoteWait(WaitSite.QueueSubmit, submitStart);
            _timeline.NoteTransferSubmitted(transferValue);
            return transferValue;
        }
    }

    /// <summary>
    /// Waits for every signalled Transfer value (teardown only, never throws),
    /// then destroys the pools and the staging ring. Retired dedicated staging
    /// buffers belong to the retire queue.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _timeline.WaitForSignalledTransfersAtTeardown();
            foreach (Batch batch in _batches)
            {
                _context.Api.DestroyCommandPool(_context.Device, batch.Pool, null);
            }
            _batches.Clear();
            _open = null;
            _stagingRing?.Dispose();
            _stagingRing = null;
        }
    }
}
