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
/// pointing at the slot, indices and Frame timeline value of its most recently
/// ended query. Results are read with <c>vkGetQueryPoolResults</c> without the
/// wait bit, with availability, into the slot's host buffer, and only once the
/// timeline says the command buffer carrying the query has finished: either when
/// the game polls after that point, or at the latest when the slot is recycled,
/// just before its reset. The result therefore appears a frame or two after the
/// query, like GL's availability polling, and nothing ever blocks on it.
///
/// A GL query counts every sample between begin and end, across framebuffer
/// binds; a Vulkan query has to begin and end inside one rendering scope and one
/// command buffer. So a running query is suspended when its scope closes (a
/// target change, a layout transition, a readback or upload that submits the
/// frame partially, present) and resumed on a fresh index when the next scope
/// opens. Its result is the sum of those segments. The pool a resumed segment
/// needs is created and reset between the two scopes.
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
    // At most one occlusion query is active at a time, as in GL: either running
    // inside the open scope, or suspended until the next scope opens.
    private QueryRecord? _running;
    private QueryRecord? _suspended;
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
        /// <summary>Query indices handed out in the current generation.</summary>
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
        /// <summary>One index per segment: a new one each time the query resumed in a new scope.</summary>
        public readonly List<uint> Indices = new(1);
        public bool Ended;
        /// <summary>A segment could not be resumed; the result reports every sample passed.</summary>
        public bool Lost;
        /// <summary>The query object was deleted while the query was running.</summary>
        public bool Abandoned;
        public ulong FrameValue;
        public bool Resolved;
        public ulong Samples;

        public QueryRecord(int slot, ulong generation)
        {
            Slot = slot;
            Generation = generation;
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
    /// is recycled; the pools are shared, so there is nothing to destroy. A query
    /// still running ends with its scope and is not resumed.
    /// </summary>
    public void Delete(int id)
    {
        if (!_objects.Remove(id, out QueryObject? query) || query.Active == null) return;
        if (ReferenceEquals(query.Active, _suspended)) _suspended = null;
        else if (ReferenceEquals(query.Active, _running)) query.Active.Abandoned = true;
    }

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
        // Present closed the last scope, so nothing is running; a query the
        // previous frame never ended is reported lost by its own slot's harvest.
        _running = null;
        _suspended = null;
    }

    /// <summary>Whether <paramref name="id" /> can begin now: known, in a frame, and no other query active.</summary>
    public bool CanBegin(int id) =>
        _currentSlot >= 0 && _running == null && _suspended == null && _objects.ContainsKey(id);

    /// <summary>The next query index lives in a pool that does not exist yet.</summary>
    public bool NextNeedsPool => _currentSlot >= 0 && NeedsPool(_slots[_currentSlot]);

    private static bool NeedsPool(SlotQueries slot) => slot.Used / QueriesPerPool >= (uint)slot.Pools.Count;

    /// <summary>
    /// Creates the pool the next index needs and records its reset. The caller is
    /// outside any rendering scope. Every later frame of the slot resets it with
    /// the others.
    /// </summary>
    public void AddPool(CommandBuffer commandBuffer)
    {
        SlotQueries slot = _slots[_currentSlot];
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

        _context.Api.CmdResetQueryPool(commandBuffer, created, 0, QueriesPerPool);
    }

    /// <summary>
    /// Begins a query for <paramref name="id" />. Inside an open scope it starts
    /// at once; outside one it starts when the next scope opens. The caller
    /// checked <see cref="CanBegin" /> and added a pool if <see cref="NextNeedsPool" />.
    /// </summary>
    public void Begin(int id, CommandBuffer commandBuffer, bool scopeOpen)
    {
        if (!CanBegin(id) || NextNeedsPool) return;

        SlotQueries slot = _slots[_currentSlot];
        var record = new QueryRecord(_currentSlot, slot.Generation);
        slot.Pending.Add(record);
        _objects[id].Active = record;

        if (scopeOpen) Start(record, commandBuffer);
        else _suspended = record;
    }

    /// <summary>
    /// Ends the query begun for <paramref name="id" /> in this frame, whose last
    /// segment is recorded in the command buffer that will signal
    /// <paramref name="frameValue" />. It becomes the query whose result the object reports.
    /// </summary>
    public void End(int id, ulong frameValue, CommandBuffer commandBuffer)
    {
        if (!_objects.TryGetValue(id, out QueryObject? query) || query.Active == null) return;

        QueryRecord record = query.Active;
        query.Active = null;
        if (ReferenceEquals(record, _running))
        {
            _running = null;
            Stop(record, commandBuffer);
        }
        else if (ReferenceEquals(record, _suspended))
        {
            _suspended = null;
        }

        // A query begun in an earlier frame cannot be ended in this one; Vulkan
        // requires both in the same command buffer. Harvest reports it as lost.
        if (record.Slot != _currentSlot || record.Generation != _slots[record.Slot].Generation) return;

        if (query.Latest != null)
        {
            Refresh(query.Latest);
            if (query.Latest.Resolved) query.PreviousResult = Clamp(query.Latest.Samples);
        }

        record.Ended = true;
        record.FrameValue = frameValue;
        query.Latest = record;
    }

    /// <summary>Scope hook, before <c>vkCmdEndRendering</c>: a running query ends its segment and waits for the next scope.</summary>
    public void OnScopeClosing(CommandBuffer commandBuffer)
    {
        QueryRecord? record = _running;
        if (record == null) return;
        _running = null;
        Stop(record, commandBuffer);
        if (!record.Abandoned) _suspended = record;
    }

    /// <summary>Scope hook, after <c>vkCmdEndRendering</c>: the pool a resumed segment will need is reset now, outside the scope.</summary>
    public void OnScopeClosed(CommandBuffer commandBuffer)
    {
        if (_suspended != null && NextNeedsPool) AddPool(commandBuffer);
    }

    /// <summary>Scope hook, after <c>vkCmdBeginRendering</c>: a suspended query resumes on a fresh index.</summary>
    public void OnScopeOpened(CommandBuffer commandBuffer)
    {
        QueryRecord? record = _suspended;
        if (record == null) return;
        _suspended = null;
        if (record.Slot != _currentSlot || record.Generation != _slots[record.Slot].Generation) return;

        // Unreachable when every scope end ran OnScopeClosed; a query that cannot
        // resume reports every sample passed rather than a partial count.
        if (NextNeedsPool)
        {
            record.Lost = true;
            return;
        }
        Start(record, commandBuffer);
    }

    private void Start(QueryRecord record, CommandBuffer commandBuffer)
    {
        SlotQueries slot = _slots[record.Slot];
        uint index = slot.Used++;
        record.Indices.Add(index);
        // GL_SAMPLES_PASSED is an exact count (sun glare divides it by 1500).
        _context.Api.CmdBeginQuery(commandBuffer, slot.Pools[(int)(index / QueriesPerPool)], index % QueriesPerPool,
            _context.Capabilities.OcclusionQueryPrecise ? QueryControlFlags.PreciseBit : default(QueryControlFlags));
        _running = record;
    }

    private void Stop(QueryRecord record, CommandBuffer commandBuffer)
    {
        SlotQueries slot = _slots[record.Slot];
        uint index = record.Indices[record.Indices.Count - 1];
        _context.Api.CmdEndQuery(commandBuffer, slot.Pools[(int)(index / QueriesPerPool)], index % QueriesPerPool);
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

        if (record.Lost || record.Indices.Count == 0)
        {
            record.Resolved = true;
            record.Samples = ulong.MaxValue;
            return;
        }

        ulong samples = 0;
        fixed (ulong* host = slot.Host)
        {
            foreach (uint index in record.Indices)
            {
                uint base2 = index * 2;
                Result status = _context.Api.GetQueryPoolResults(_context.Device,
                    slot.Pools[(int)(index / QueriesPerPool)], index % QueriesPerPool, 1,
                    (nuint)Stride, host + base2, (ulong)Stride, ReadFlags);
                if (status != Result.Success && status != Result.NotReady)
                {
                    VulkanResult.Check(status, "vkGetQueryPoolResults for an occlusion query");
                }
                if (host[base2 + 1] == 0) return;
                samples += host[base2];
            }
        }

        record.Resolved = true;
        record.Samples = samples;
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
            record.Resolved = true;
            record.Samples = SumOfHarvested(slot, record);
        }
        slot.Pending.Clear();
    }

    private static ulong SumOfHarvested(SlotQueries slot, QueryRecord record)
    {
        if (!record.Ended || record.Lost || record.Indices.Count == 0) return ulong.MaxValue;
        ulong samples = 0;
        foreach (uint index in record.Indices)
        {
            uint base2 = index * 2;
            if (slot.Host[base2 + 1] == 0) return ulong.MaxValue;
            samples += slot.Host[base2];
        }
        return samples;
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
