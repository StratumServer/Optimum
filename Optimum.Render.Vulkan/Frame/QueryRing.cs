using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

// The Frame/ folder follows the plan's layout; the namespace stays Core until the
// renderer is reorganised.
namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Occlusion queries that never make the CPU wait.
///
/// Each frame slot owns its own occlusion query pools, 32 queries apiece, reset
/// wholesale when the slot starts a frame (the first commands of its command
/// buffer, outside any rendering scope). A GL query object is a small record
/// pointing at the slot, index and Frame timeline value of its most recently
/// ended query. Results are read with <c>vkGetQueryPoolResults</c> without the
/// wait bit, with availability, into the slot's host buffer, and only once the
/// timeline says the command buffer carrying the query has finished: either when
/// the game polls after that point, or at the latest when the slot is recycled,
/// just before its reset. The result therefore appears a frame or two after the
/// query, like GL's availability polling, and nothing ever blocks on it.
///
/// The plan named <c>vkCmdCopyQueryPoolResults</c> for the copy. That command is
/// not part of Vulkan 1.3 core and needs an extension the hardware floor does not
/// include; a host read gated on the timeline gives the same no-wait guarantee.
/// </summary>
internal sealed unsafe class QueryRing : IDisposable
{
    public const uint QueriesPerPool = 32;

    private const QueryResultFlags ReadFlags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit;
    private const int Stride = 2 * sizeof(ulong);

    private readonly VulkanContext _context;
    private readonly ITimelineClock _clock;
    private readonly SlotQueries[] _slots;
    private readonly Dictionary<int, QueryObject> _objects = new();
    private int _nextId = 1;
    private int _currentSlot = -1;
    private bool _disposed;

    public QueryRing(VulkanContext context, ITimelineClock clock, int framesInFlight)
    {
        _context = context;
        _clock = clock;
        _slots = new SlotQueries[framesInFlight];
        for (int i = 0; i < framesInFlight; i++) _slots[i] = new SlotQueries();
    }

    private sealed class SlotQueries
    {
        public readonly List<QueryPool> Pools = new();
        /// <summary>Queries handed out in the current generation.</summary>
        public uint Used;
        /// <summary>Bumped at every frame start of the slot; a record of an older generation was harvested.</summary>
        public ulong Generation;
        /// <summary>Records of the current generation not yet harvested.</summary>
        public readonly List<QueryRecord> Pending = new();
        /// <summary>Value and availability per query, two ulongs each, in pool order.</summary>
        public ulong[] Host = Array.Empty<ulong>();
    }

    private sealed class QueryRecord
    {
        public readonly int Slot;
        public readonly ulong Generation;
        public readonly uint Index;
        public bool Ended;
        public ulong FrameValue;
        public bool Resolved;
        public ulong Samples;

        public QueryRecord(int slot, ulong generation, uint index)
        {
            Slot = slot;
            Generation = generation;
            Index = index;
        }
    }

    private sealed class QueryObject
    {
        public QueryRecord? Active;
        public QueryRecord? Latest;
        /// <summary>The result of an earlier query, returned while the latest one is still in flight.</summary>
        public int PreviousResult = int.MaxValue;
    }

    /// <summary>Pools created so far across every slot. Tests only.</summary>
    internal int PoolCount
    {
        get
        {
            int count = 0;
            foreach (SlotQueries slot in _slots) count += slot.Pools.Count;
            return count;
        }
    }

    public int Create()
    {
        int id = _nextId++;
        _objects[id] = new QueryObject();
        return id;
    }

    /// <summary>
    /// Forgets the query object. Its records stay with their slot until that slot
    /// is recycled; the pools are shared, so there is nothing to destroy.
    /// </summary>
    public void Delete(int id) => _objects.Remove(id);

    /// <summary>
    /// The slot is starting a frame and the timeline has passed its previous one:
    /// reads every result that frame produced into the host buffer, then records
    /// the pool resets at the top of the new command buffer.
    /// </summary>
    public void BeginSlot(int slotIndex, CommandBuffer commandBuffer)
    {
        SlotQueries slot = _slots[slotIndex];
        Harvest(slot);

        uint poolsUsed = (slot.Used + QueriesPerPool - 1) / QueriesPerPool;
        for (int i = 0; i < poolsUsed; i++)
        {
            _context.Api.CmdResetQueryPool(commandBuffer, slot.Pools[i], 0, QueriesPerPool);
        }

        slot.Used = 0;
        slot.Generation++;
        _currentSlot = slotIndex;
    }

    /// <summary>
    /// Hands out the next query of the current slot for <paramref name="id" />.
    /// <paramref name="freshPool" /> is true when the pool was created just now:
    /// the caller has to reset it before beginning the query, outside a rendering
    /// scope. Every later frame resets it with the others.
    /// </summary>
    public bool TryBegin(int id, out QueryPool pool, out uint index, out bool freshPool)
    {
        pool = default;
        index = 0;
        freshPool = false;
        if (_currentSlot < 0 || !_objects.TryGetValue(id, out QueryObject? query)) return false;

        SlotQueries slot = _slots[_currentSlot];
        int poolIndex = (int)(slot.Used / QueriesPerPool);
        if (poolIndex == slot.Pools.Count)
        {
            var createInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Occlusion,
                QueryCount = QueriesPerPool,
            };
            QueryPool created;
            VulkanResult.Check(_context.Api.CreateQueryPool(_context.Device, &createInfo, null, &created),
                "vkCreateQueryPool for the occlusion query ring");
            slot.Pools.Add(created);

            var host = new ulong[slot.Pools.Count * QueriesPerPool * 2];
            Array.Copy(slot.Host, host, slot.Host.Length);
            slot.Host = host;
            freshPool = true;
        }

        pool = slot.Pools[poolIndex];
        index = slot.Used % QueriesPerPool;

        var record = new QueryRecord(_currentSlot, slot.Generation, slot.Used);
        slot.Used++;
        slot.Pending.Add(record);
        query.Active = record;
        return true;
    }

    /// <summary>
    /// Ends the query begun for <paramref name="id" /> in this frame, recorded in
    /// the command buffer that will signal <paramref name="frameValue" />. It
    /// becomes the query whose result the object reports.
    /// </summary>
    public bool TryEnd(int id, ulong frameValue, out QueryPool pool, out uint index)
    {
        pool = default;
        index = 0;
        if (!_objects.TryGetValue(id, out QueryObject? query) || query.Active == null) return false;

        QueryRecord record = query.Active;
        query.Active = null;
        SlotQueries slot = _slots[record.Slot];
        // A query begun in an earlier frame cannot be ended in this one; Vulkan
        // requires both in the same command buffer. Harvest reports it as lost.
        if (record.Slot != _currentSlot || record.Generation != slot.Generation) return false;

        if (query.Latest != null)
        {
            Refresh(query.Latest);
            if (query.Latest.Resolved) query.PreviousResult = Clamp(query.Latest.Samples);
        }

        record.Ended = true;
        record.FrameValue = frameValue;
        query.Latest = record;

        pool = slot.Pools[(int)(record.Index / QueriesPerPool)];
        index = record.Index % QueriesPerPool;
        return true;
    }

    /// <summary>GL_QUERY_RESULT_AVAILABLE: the latest ended query's command buffer has finished.</summary>
    public bool IsResultAvailable(int id)
    {
        if (!_objects.TryGetValue(id, out QueryObject? query) || query.Latest == null) return false;
        Refresh(query.Latest);
        return query.Latest.Resolved;
    }

    /// <summary>
    /// The latest query's sample count once available; before that, the previous
    /// query's, and "every sample passed" when there never was one - for a query
    /// that gates culling or glare, visible is the failure that costs little.
    /// </summary>
    public int GetResult(int id)
    {
        if (!_objects.TryGetValue(id, out QueryObject? query)) return 0;
        if (query.Latest != null)
        {
            Refresh(query.Latest);
            if (query.Latest.Resolved) return Clamp(query.Latest.Samples);
        }
        return query.PreviousResult;
    }

    private static int Clamp(ulong samples) => (int)Math.Min(samples, int.MaxValue);

    /// <summary>Reads one result early, once the timeline has passed the command buffer that carried it.</summary>
    private void Refresh(QueryRecord record)
    {
        if (record.Resolved || !record.Ended) return;
        SlotQueries slot = _slots[record.Slot];
        // Harvest resolves every record of a generation before bumping it.
        if (record.Generation != slot.Generation) return;
        if (_clock.FrameCompleted < record.FrameValue) return;

        uint base2 = record.Index * 2;
        fixed (ulong* host = slot.Host)
        {
            Result status = _context.Api.GetQueryPoolResults(_context.Device,
                slot.Pools[(int)(record.Index / QueriesPerPool)], record.Index % QueriesPerPool, 1,
                (nuint)Stride, host + base2, (ulong)Stride, ReadFlags);
            if (status != Result.Success && status != Result.NotReady)
            {
                VulkanResult.Check(status, "vkGetQueryPoolResults for an occlusion query");
            }
        }

        if (slot.Host[base2 + 1] != 0)
        {
            record.Resolved = true;
            record.Samples = slot.Host[base2];
        }
    }

    private void Harvest(SlotQueries slot)
    {
        if (slot.Pending.Count == 0) return;

        uint remaining = slot.Used;
        fixed (ulong* host = slot.Host)
        {
            for (int p = 0; remaining > 0 && p < slot.Pools.Count; p++)
            {
                uint count = Math.Min(remaining, QueriesPerPool);
                Result status = _context.Api.GetQueryPoolResults(_context.Device, slot.Pools[p], 0, count,
                    (nuint)(count * Stride), host + (ulong)p * QueriesPerPool * 2, (ulong)Stride, ReadFlags);
                // NOT_READY only means some query was never ended; the availability
                // word says which, and the rest are written.
                if (status != Result.Success && status != Result.NotReady)
                {
                    VulkanResult.Check(status, "vkGetQueryPoolResults for the occlusion query ring");
                }
                remaining -= count;
            }
        }

        foreach (QueryRecord record in slot.Pending)
        {
            if (record.Resolved) continue;
            uint base2 = record.Index * 2;
            record.Resolved = true;
            record.Samples = record.Ended && slot.Host[base2 + 1] != 0 ? slot.Host[base2] : ulong.MaxValue;
        }
        slot.Pending.Clear();
    }

    /// <summary>The caller has waited for every signalled frame first.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (SlotQueries slot in _slots)
        {
            foreach (QueryPool pool in slot.Pools) _context.Api.DestroyQueryPool(_context.Device, pool, null);
            slot.Pools.Clear();
        }
        _objects.Clear();
    }
}
