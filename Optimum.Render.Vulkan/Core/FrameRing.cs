using System;
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
/// frame that last used it (<see cref="FrameRing.BeginFrame" /> waits for that).
/// </summary>
internal sealed unsafe class FrameSlot : IDisposable
{
    private readonly VulkanContext _context;
    private readonly FrameTimeline _timeline;
    private readonly ulong _alignment;
    private readonly ulong _regionStart;
    private readonly ulong _regionSize;
    private readonly VulkanBuffer _uniformRing;
    private ulong _cursor;
    private bool _disposed;

    public CommandPool CommandPool { get; }
    public CommandBuffer CommandBuffer { get; private set; }

    /// <summary>The Frame timeline value this slot's current frame signals when submitted.</summary>
    public ulong FrameValue { get; private set; }

    public FrameSlot(VulkanContext context, FrameTimeline timeline, VulkanBuffer uniformRing,
        ulong regionStart, ulong regionSize)
    {
        _context = context;
        _timeline = timeline;
        _uniformRing = uniformRing;
        _regionStart = regionStart;
        _regionSize = regionSize;
        _alignment = Math.Max(1, context.Capabilities.MinUniformBufferOffsetAlignment);

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
    /// Recycles the slot for frame <paramref name="frameValue" />. The caller has
    /// already waited for the frame that last used it, so resetting the pool is legal.
    /// </summary>
    public void Begin(ulong frameValue)
    {
        Vk api = _context.Api;
        FrameValue = frameValue;

        api.ResetCommandPool(_context.Device, CommandPool, 0);
        _cursor = 0;

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer commandBuffer;
        api.AllocateCommandBuffers(_context.Device, &allocateInfo, &commandBuffer);
        CommandBuffer = commandBuffer;

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        api.BeginCommandBuffer(commandBuffer, &begin);
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
        Vk api = _context.Api;
        CommandBuffer commandBuffer = CommandBuffer;
        api.EndCommandBuffer(commandBuffer);
        VulkanStats.NoteUniformRingUse(_cursor, _regionSize);

        Semaphore wait = waitSemaphore;
        ulong waitValue = 0;
        PipelineStageFlags stage = waitStage;
        uint waitCount = wait.Handle == 0 ? 0u : 1u;

        // Binary present semaphore first (its value is ignored), then the timeline.
        Semaphore* signals = stackalloc Semaphore[2];
        ulong* signalValues = stackalloc ulong[2];
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
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            WaitSemaphoreCount = waitCount,
            PWaitSemaphores = waitCount == 0 ? null : &wait,
            PWaitDstStageMask = waitCount == 0 ? null : &stage,
            SignalSemaphoreCount = signalCount,
            PSignalSemaphores = signals,
        };

        // Shares the queue with off-thread setup submissions; see QueueLock. A
        // worker's synchronous upload holds that lock through its fence wait, so
        // this is a CPU wait on the GPU like any other and is counted as one.
        long submitStart = VulkanStats.WaitStart();
        lock (_context.QueueLock)
        {
            VulkanResult.Check(api.QueueSubmit(_context.GraphicsQueue, 1, &submit, default(Fence)),
                "vkQueueSubmit for a frame");
        }
        VulkanStats.NoteWait(WaitSite.QueueSubmit, submitStart);
        _timeline.NoteFrameSubmitted(FrameValue);
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
/// Frame <c>n</c> uses slot <c>(n - 1) % FramesInFlight</c> and, before it
/// starts, waits for Frame value <c>n - FramesInFlight</c>: the frame that last
/// used that slot. That wait is the only CPU wait the ring makes in steady state.
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
    private int _index = -1;
    private bool _disposed;

    public FrameRing(VulkanContext context, int framesInFlight = 2, ulong uniformRingSize = 32 * 1024 * 1024)
    {
        _timeline = new FrameTimeline(context);
        _retired = new RetireQueue(_timeline);
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
            _slots[i] = new FrameSlot(context, _timeline, _uniformRing, regionSize * (ulong)i, regionSize);
        }
    }

    public int FramesInFlight => _slots.Length;

    /// <summary>The Frame and Transfer timelines every submission signals.</summary>
    public FrameTimeline Timeline => _timeline;

    /// <summary>The buffer every uniform descriptor points at.</summary>
    public Buffer UniformBuffer => _uniformRing.Handle;

    public FrameSlot Current => _index < 0
        ? throw new InvalidOperationException("BeginFrame has not been called yet")
        : _slots[_index];

    /// <summary>
    /// Starts the next frame: reserves its Frame value, waits for the frame that
    /// last used its slot, destroys whatever the timelines say is no longer
    /// referenced, and recycles the slot.
    /// </summary>
    /// <param name="site">
    /// Which wait the timeline wait counts as: frame pacing at a real frame start,
    /// <see cref="WaitSite.FlushFrame" /> when a mid-frame flush continues the frame.
    /// </param>
    public FrameSlot BeginFrame(WaitSite site = WaitSite.FramePacing)
    {
        ulong frameValue = _timeline.ReserveFrame();
        _timeline.WaitForFrame(FrameTimeline.PacingTarget(frameValue, _slots.Length), site);
        _retired.Collect();

        _index = (int)((frameValue - 1) % (ulong)_slots.Length);
        FrameSlot slot = _slots[_index];
        slot.Begin(frameValue);
        return slot;
    }

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
        _retired.DisposeAll();
        foreach (FrameSlot slot in _slots) slot.Dispose();
        _uniformRing.Dispose();
        _timeline.Dispose();
    }
}
