using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Core;

/// <summary>Where a uniform upload landed in the ring buffer.</summary>
internal readonly record struct RingAllocation(Buffer Buffer, uint Offset, IntPtr Pointer);

/// <summary>
/// One frame's worth of transient GPU state.
///
/// Everything here is reset wholesale rather than freed piecemeal: the command
/// pool, and the bump cursor into this slot's slice of the shared uniform ring. A
/// slot is only reused once the Frame timeline says the GPU has finished the
/// last submission that used it (<see cref="FrameRing.BeginFrame" /> waits for that).
///
/// The slot's frame command buffer is submitted together with the open upload
/// batch (<see cref="UploadManager" />), batch first, in one SubmitInfo that
/// signals both timelines: uploads recorded since the last submission run before
/// the frame that uses them, and nothing waits for them.
///
/// A frame may be submitted in parts (<see cref="SubmitPartial" />, for a
/// readback that has to see the frame's work so far). Every command buffer
/// carries its own Frame timeline value, and all of them stay in this slot: the
/// uniform cursor keeps counting, so snapshots taken before a partial submit stay
/// valid after it.
/// </summary>
internal sealed unsafe class FrameSlot : IDisposable
{
    private readonly VulkanContext _context;
    private readonly FrameTimeline _timeline;
    private readonly UploadManager _uploads;
    private readonly ulong _alignment;
    private readonly ulong _regionStart;
    private readonly ulong _regionSize;
    private readonly VulkanBuffer _uniformRing;
    // Allocated once and recycled: resetting the pool returns every one of them
    // to the initial state, where it can be begun again.
    private readonly List<CommandBuffer> _commandBuffers = new();
    private int _commandBuffersUsed;
    private ulong _cursor;
    private bool _disposed;

    /// <summary>The slot's position in the ring.</summary>
    public int Index { get; }

    public CommandPool CommandPool { get; }
    public CommandBuffer CommandBuffer { get; private set; }

    /// <summary>The Frame timeline value the command buffer being recorded signals when submitted.</summary>
    public ulong FrameValue { get; private set; }

    /// <summary>
    /// The value of this slot's newest accepted submission, 0 before the first.
    /// The next frame to use the slot waits for it.
    /// </summary>
    public ulong LastSignalledValue { get; private set; }

    /// <summary>Partial submissions in the current frame.</summary>
    public int PartialSubmits { get; private set; }

    public FrameSlot(VulkanContext context, FrameTimeline timeline, UploadManager uploads, VulkanBuffer uniformRing,
        ulong regionStart, ulong regionSize, int index = 0)
    {
        _context = context;
        _timeline = timeline;
        _uploads = uploads;
        _uniformRing = uniformRing;
        _regionStart = regionStart;
        _regionSize = regionSize;
        _alignment = Math.Max(1, context.Capabilities.MinUniformBufferOffsetAlignment);
        Index = index;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = context.GraphicsQueueFamily,
            Flags = CommandPoolCreateFlags.TransientBit,
        };
        context.Api.CreateCommandPool(context.Device, &poolInfo, null, out CommandPool commandPool);
        CommandPool = commandPool;
    }

    /// <summary>
    /// Recycles the slot for a frame whose first command buffer signals
    /// <paramref name="frameValue" />. The caller has already waited for the last
    /// submission that used the slot, so resetting the pool is legal.
    /// </summary>
    public void Begin(ulong frameValue)
    {
        _context.Api.ResetCommandPool(_context.Device, CommandPool, 0);
        _cursor = 0;
        _commandBuffersUsed = 0;
        PartialSubmits = 0;
        FrameValue = frameValue;
        StartCommandBuffer();
    }

    private void StartCommandBuffer()
    {
        Vk api = _context.Api;
        CommandBuffer commandBuffer;
        if (_commandBuffersUsed < _commandBuffers.Count)
        {
            commandBuffer = _commandBuffers[_commandBuffersUsed];
        }
        else
        {
            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = CommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VulkanResult.Check(api.AllocateCommandBuffers(_context.Device, &allocateInfo, &commandBuffer),
                "vkAllocateCommandBuffers for a frame slot");
            _commandBuffers.Add(commandBuffer);
        }
        _commandBuffersUsed++;

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanResult.Check(api.BeginCommandBuffer(commandBuffer, &begin),
            "vkBeginCommandBuffer for a frame slot");
        CommandBuffer = commandBuffer;
        _uploads.OnFrameCommandsStarted(commandBuffer);
    }

    /// <summary>
    /// Bump-allocates uniform space in this slot's region of the shared ring.
    /// Returns false when the region is exhausted, which the caller reports
    /// rather than crashing on.
    /// </summary>
    public bool TryAllocateUniforms(int size, out RingAllocation allocation)
    {
        ulong aligned = (_cursor + _alignment - 1) / _alignment * _alignment;
        if (aligned + (ulong)size > _regionSize)
        {
            allocation = default;
            return false;
        }

        ulong absolute = _regionStart + aligned;
        allocation = new RingAllocation(
            _uniformRing.Handle, (uint)absolute, _uniformRing.Mapped + (int)absolute);
        _cursor = aligned + (ulong)size;
        return true;
    }

    /// <summary>
    /// Submits what the frame has recorded so far and continues in a new command
    /// buffer of this slot, under a newly reserved Frame value. Nothing waits and
    /// nothing is reset. The caller closes any open rendering scope first (and
    /// must not have an occlusion query open). Returns the value just signalled.
    /// </summary>
    public ulong SubmitPartial()
    {
        ulong submitted = FrameValue;
        Submit(default, default, PipelineStageFlags.AllCommandsBit);
        PartialSubmits++;
        FrameValue = _timeline.ReserveFrame();
        StartCommandBuffer();
        return submitted;
    }

    /// <summary>
    /// Closes the command buffer and submits it, signalling the Frame timeline to
    /// <see cref="FrameValue" /> (and the binary present semaphore when given).
    ///
    /// Every frame that begins must end here: a reserved Frame value that is never
    /// signalled holds back every deferred destruction recorded at or after it.
    /// </summary>
    public void EndFrameAndSubmit(
        Semaphore waitSemaphore = default,
        Semaphore signalSemaphore = default,
        // The swapchain image's first use in the frame is the present blit, a
        // transfer, which a COLOR_ATTACHMENT_OUTPUT wait does not order: the
        // blit could overwrite an image the presentation engine still owns and
        // the display would show a stale or torn frame. Wait at every stage.
        PipelineStageFlags waitStage = PipelineStageFlags.AllCommandsBit)
    {
        Submit(waitSemaphore, signalSemaphore, waitStage);
        VulkanStats.NoteUniformRingUse(_cursor, _regionSize);
    }

    private void Submit(Semaphore waitSemaphore, Semaphore signalSemaphore, PipelineStageFlags waitStage)
    {
        Vk api = _context.Api;
        CommandBuffer commandBuffer = CommandBuffer;
        api.EndCommandBuffer(commandBuffer);

        Semaphore wait = waitSemaphore;
        ulong waitValue = 0;
        PipelineStageFlags stage = waitStage;
        uint waitCount = wait.Handle == 0 ? 0u : 1u;

        // Binary present semaphore first (its value is ignored), then the Frame
        // timeline, then the Transfer timeline when an upload batch rides along.
        Semaphore* signals = stackalloc Semaphore[3];
        ulong* signalValues = stackalloc ulong[3];
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];

        // The queue is shared with the swapchain's present and between-frames
        // upload submissions; see QueueLock. Counted as a wait like any other.
        long submitStart = VulkanStats.WaitStart();
        // The upload lock is held from taking the batch to the submit, so no
        // upload can land in a batch that is already closed, and Transfer values
        // reach the queue in the order they were reserved.
        _uploads.EnterSubmit();
        try
        {
            uint signalCount = 0;
            if (signalSemaphore.Handle != 0)
            {
                signals[signalCount] = signalSemaphore;
                signalValues[signalCount] = 0;
                signalCount++;
            }
            signals[signalCount] = _timeline.Frame;
            signalValues[signalCount] = FrameValue;
            signalCount++;

            uint commandBufferCount = 0;
            bool uploads = _uploads.TakeOpenBatchLocked(out CommandBuffer uploadCommands, out ulong transferValue);
            if (uploads)
            {
                // First: it runs before the frame command buffer that samples what it wrote.
                commandBuffers[commandBufferCount++] = uploadCommands;
                signals[signalCount] = _timeline.Transfer;
                signalValues[signalCount] = transferValue;
                signalCount++;
            }
            commandBuffers[commandBufferCount++] = commandBuffer;

            var timelineInfo = new TimelineSemaphoreSubmitInfo
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = waitCount,
                PWaitSemaphoreValues = waitCount == 0 ? null : &waitValue,
                SignalSemaphoreValueCount = signalCount,
                PSignalSemaphoreValues = signalValues,
            };

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                CommandBufferCount = commandBufferCount,
                PCommandBuffers = commandBuffers,
                WaitSemaphoreCount = waitCount,
                PWaitSemaphores = waitCount == 0 ? null : &wait,
                PWaitDstStageMask = waitCount == 0 ? null : &stage,
                SignalSemaphoreCount = signalCount,
                PSignalSemaphores = signals,
            };

            lock (_context.QueueLock)
            {
                VulkanResult.Check(api.QueueSubmit(_context.GraphicsQueue, 1, &submit, default(Fence)),
                    "vkQueueSubmit for a frame");
            }
            if (uploads) _timeline.NoteTransferSubmitted(transferValue);
            _uploads.OnFrameCommandsSubmittedLocked();
        }
        finally
        {
            _uploads.ExitSubmit();
        }
        VulkanStats.NoteWait(WaitSite.QueueSubmit, submitStart);
        _timeline.NoteFrameSubmitted(FrameValue);
        LastSignalledValue = FrameValue;
    }

    public ulong UniformBytesUsed => _cursor;
    public ulong UniformCapacity => _regionSize;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Api.DestroyCommandPool(_context.Device, CommandPool, null);
    }
}

/// <summary>
/// Rotates through a small number of frame slots, paced by the Frame timeline.
///
/// Two in flight is the default: enough to keep the GPU fed while the CPU records
/// the next frame, few enough that input latency stays close to what the OpenGL
/// path had. Frame generation will want a third later.
///
/// Frames use the slots in turn. Before a frame starts, it waits for the newest
/// Frame value its slot signalled: the end of the frame that last used it (or of
/// that frame's last partial submission). Without partial submissions that is
/// frame <c>n - FramesInFlight</c>. That wait is the only CPU wait the ring makes
/// in steady state.
///
/// The uniform ring is one buffer for the whole ring rather than one per slot,
/// with each slot bump-allocating inside its own slice. That is what lets
/// descriptor sets be written once and reused forever: the set names the buffer,
/// and the per-draw offset travels as a dynamic offset instead. A buffer per slot
/// would mean rewriting every set every frame, which is the cost this design
/// exists to avoid.
/// </summary>
internal sealed class FrameRing : IDisposable
{
    private readonly FrameSlot[] _slots;
    private readonly VulkanBuffer _uniformRing;
    private readonly FrameTimeline _timeline;
    private readonly RetireQueue _retired;
    private readonly UploadManager _uploads;
    private int _index = -1;
    private bool _disposed;

    public FrameRing(VulkanContext context, int framesInFlight = 2, ulong uniformRingSize = 32 * 1024 * 1024,
        ulong stagingPerSlot = UploadManager.DefaultStagingPerSlot)
    {
        _timeline = new FrameTimeline(context);
        _retired = new RetireQueue(_timeline);
        _uploads = new UploadManager(context, _timeline, _retired, framesInFlight, stagingPerSlot);
        _uniformRing = new VulkanBuffer(context, uniformRingSize,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        // Each region must start on a uniform-offset boundary, otherwise every
        // dynamic offset handed out from slot 1 onwards inherits the misalignment.
        ulong alignment = Math.Max(1UL, context.Capabilities.MinUniformBufferOffsetAlignment);
        ulong regionSize = uniformRingSize / (ulong)framesInFlight / alignment * alignment;
        _slots = new FrameSlot[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _slots[i] = new FrameSlot(context, _timeline, _uploads, _uniformRing, regionSize * (ulong)i, regionSize, i);
        }
    }

    public int FramesInFlight => _slots.Length;

    /// <summary>The Frame and Transfer timelines every submission signals.</summary>
    public FrameTimeline Timeline => _timeline;

    /// <summary>The upload batches every submission of this ring carries first.</summary>
    public UploadManager Uploads => _uploads;

    /// <summary>The buffer every uniform descriptor points at.</summary>
    public Buffer UniformBuffer => _uniformRing.Handle;

    public FrameSlot Current => _index < 0
        ? throw new InvalidOperationException("BeginFrame has not been called yet")
        : _slots[_index];

    /// <summary>
    /// Starts the next frame: reserves its first Frame value, waits for the last
    /// submission of the frame that used its slot before, destroys whatever the
    /// timelines say is no longer referenced, and recycles the slot.
    /// </summary>
    public FrameSlot BeginFrame()
    {
        int index = (_index + 1) % _slots.Length;
        FrameSlot slot = _slots[index];

        ulong frameValue = _timeline.ReserveFrame();
        _timeline.WaitForFrame(slot.LastSignalledValue, WaitSite.FramePacing);
        _retired.Collect();

        _index = index;
        slot.Begin(frameValue);
        return slot;
    }

    /// <summary>
    /// Submits the current frame's work so far and keeps recording it in the same
    /// slot; see <see cref="FrameSlot.SubmitPartial" />.
    /// </summary>
    public ulong SubmitPartial() => Current.SubmitPartial();

    /// <summary>Ends and submits the current frame. Pairs with every BeginFrame.</summary>
    public void EndFrame(
        Semaphore waitSemaphore = default,
        Semaphore signalSemaphore = default) =>
        Current.EndFrameAndSubmit(waitSemaphore, signalSemaphore);

    /// <summary>
    /// Queues a resource for destruction once the GPU is done with it.
    ///
    /// Safe from any thread. The game's VAO and UBO finalizers call Dispose from
    /// the finalizer thread, so this cannot assume it is on the render thread;
    /// destruction happens at a later BeginFrame, which is.
    ///
    /// The resource is keyed on the newest Frame and Transfer values reserved so
    /// far (every command that could still name it carries one of them or an
    /// older one) and destroyed at the first frame start after both completed.
    /// </summary>
    public void DeferDeletion(IDisposable resource) => _retired.Retire(resource);

    public int PendingDeletionCount => _retired.PendingCount;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Callers normally wait for the device to go idle first (VulkanDevice.Dispose
        // does); this covers the ones that did not, such as a test unwinding from a
        // failed assert. The last submission may still name everything below.
        _timeline.WaitForSignalledFramesAtTeardown();
        _uploads.Dispose();
        _retired.DisposeAll();
        foreach (FrameSlot slot in _slots) slot.Dispose();
        _uniformRing.Dispose();
        _timeline.Dispose();
    }
}
