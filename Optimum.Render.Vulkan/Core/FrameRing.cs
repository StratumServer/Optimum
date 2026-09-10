using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Core;

/// <summary>Where a uniform upload landed in the ring buffer.</summary>
internal readonly record struct RingAllocation(Buffer Buffer, uint Offset, IntPtr Pointer);

/// <summary>
/// One frame's worth of transient GPU state.
///
/// Everything here is reset wholesale rather than freed piecemeal: the command
/// pool, and the bump cursor into this slot's slice of the shared uniform ring. A
/// slot is only reused once its fence says the GPU has finished with it, which is
/// also what makes deferred deletion safe.
/// </summary>
internal sealed unsafe class FrameSlot : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ulong _alignment;
    private readonly ulong _regionStart;
    private readonly ulong _regionSize;
    private readonly VulkanBuffer _uniformRing;
    private ulong _cursor;
    private bool _disposed;

    public CommandPool CommandPool { get; }
    public CommandBuffer CommandBuffer { get; private set; }
    public Fence Fence { get; }

    /// <summary>
    /// Resources the GPU may still be reading. They are destroyed when this
    /// slot's fence signals, never at the moment the game asks.
    /// </summary>
    private readonly List<IDisposable> _pendingDeletions = new();

    public FrameSlot(VulkanContext context, VulkanBuffer uniformRing, ulong regionStart, ulong regionSize)
    {
        _context = context;
        _uniformRing = uniformRing;
        _regionStart = regionStart;
        _regionSize = regionSize;
        _alignment = Math.Max(1, context.Capabilities.MinUniformBufferOffsetAlignment);

        Vk api = context.Api;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = context.GraphicsQueueFamily,
            Flags = CommandPoolCreateFlags.TransientBit,
        };
        api.CreateCommandPool(context.Device, &poolInfo, null, out CommandPool commandPool);
        CommandPool = commandPool;

        // Created signalled so the first frame does not wait on a fence that
        // will never be submitted.
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };
        api.CreateFence(context.Device, &fenceInfo, null, out Fence fence);
        Fence = fence;
    }

    /// <summary>
    /// Waits for the GPU to finish with this slot, then recycles it. This is the
    /// only point where deferred deletions actually happen.
    /// </summary>
    public void BeginFrame(ConcurrentQueue<IDisposable> incomingDeletions)
    {
        Vk api = _context.Api;
        Fence fence = Fence;

        VulkanResult.Check(api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue),
            "vkWaitForFences at the start of a frame");
        VulkanResult.Check(api.ResetFences(_context.Device, 1, &fence),
            "vkResetFences at the start of a frame");

        foreach (IDisposable pending in _pendingDeletions) pending.Dispose();
        _pendingDeletions.Clear();

        // Deletions queued from a finalizer thread join this slot, so they too
        // wait a full frame cycle before the resource is destroyed.
        while (incomingDeletions.TryDequeue(out IDisposable? deletion))
        {
            _pendingDeletions.Add(deletion);
        }

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
    /// Closes the command buffer and submits it against this slot's fence.
    ///
    /// Every frame that begins must end here. <see cref="BeginFrame" /> resets the
    /// fence, so a slot that is begun and never submitted would leave the fence
    /// unsignalled and deadlock the next time the ring came round to it.
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

        Semaphore wait = waitSemaphore;
        Semaphore signal = signalSemaphore;
        PipelineStageFlags stage = waitStage;

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            WaitSemaphoreCount = wait.Handle == 0 ? 0u : 1u,
            PWaitSemaphores = wait.Handle == 0 ? null : &wait,
            PWaitDstStageMask = wait.Handle == 0 ? null : &stage,
            SignalSemaphoreCount = signal.Handle == 0 ? 0u : 1u,
            PSignalSemaphores = signal.Handle == 0 ? null : &signal,
        };

        // Shares the queue with off-thread setup submissions; see QueueLock.
        lock (_context.QueueLock)
        {
            VulkanResult.Check(api.QueueSubmit(_context.GraphicsQueue, 1, &submit, Fence),
                "vkQueueSubmit for a frame");
        }
    }

    public void DeferDeletion(IDisposable resource) => _pendingDeletions.Add(resource);

    public ulong UniformBytesUsed => _cursor;
    public ulong UniformCapacity => _regionSize;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (IDisposable pending in _pendingDeletions) pending.Dispose();
        _pendingDeletions.Clear();

        Vk api = _context.Api;
        api.DestroyFence(_context.Device, Fence, null);
        api.DestroyCommandPool(_context.Device, CommandPool, null);
    }
}

/// <summary>
/// Rotates through a small number of frame slots.
///
/// Two in flight is the default: enough to keep the GPU fed while the CPU records
/// the next frame, few enough that input latency stays close to what the OpenGL
/// path had. Frame generation will want a third later.
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
    private readonly ConcurrentQueue<IDisposable> _incomingDeletions = new();
    private int _index = -1;
    private bool _disposed;

    public FrameRing(VulkanContext context, int framesInFlight = 2, ulong uniformRingSize = 32 * 1024 * 1024)
    {
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
            _slots[i] = new FrameSlot(context, _uniformRing, regionSize * (ulong)i, regionSize);
        }
    }

    public int FramesInFlight => _slots.Length;

    /// <summary>The buffer every uniform descriptor points at.</summary>
    public Buffer UniformBuffer => _uniformRing.Handle;

    public FrameSlot Current => _index < 0
        ? throw new InvalidOperationException("BeginFrame has not been called yet")
        : _slots[_index];

    public FrameSlot BeginFrame()
    {
        _index = (_index + 1) % _slots.Length;
        FrameSlot slot = _slots[_index];
        slot.BeginFrame(_incomingDeletions);
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
    /// the queue is drained at the next BeginFrame, which is.
    ///
    /// The resource is adopted by the slot that drains the queue and destroyed
    /// when that slot next comes round, so it survives up to two full ring cycles
    /// rather than one. That is deliberately conservative: the alternative is to
    /// know which frames referenced it, which the GL-shaped API this backend sits
    /// behind never tells us.
    /// </summary>
    public void DeferDeletion(IDisposable resource) => _incomingDeletions.Enqueue(resource);

    public int PendingDeletionCount => _incomingDeletions.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_incomingDeletions.TryDequeue(out IDisposable? deletion)) deletion.Dispose();
        foreach (FrameSlot slot in _slots) slot.Dispose();
        _uniformRing.Dispose();
    }
}
