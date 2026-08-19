using System.Collections.Generic;

namespace Vintagestory.API.Config;

/// <summary>
/// Tracks managed thread ids that run client tessellation work.
/// </summary>
public sealed class OptimumTesselationWorkerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _slotsByThreadId = new();

    /// <summary>
    /// Add a tessellation worker thread id. Repeated registration has no effect.
    /// </summary>
    public int Register(int threadId)
    {
        lock (_gate)
        {
            if (_slotsByThreadId.TryGetValue(threadId, out int slot))
            {
                return slot;
            }

            slot = _slotsByThreadId.Count;
            _slotsByThreadId[threadId] = slot;
            OptimumDiagnostics.RecordTessWorkerRegistered(threadId);
            return slot;
        }
    }

    /// <summary>
    /// Check whether a managed thread id belongs to a registered worker.
    /// </summary>
    public bool Contains(int threadId)
    {
        lock (_gate)
        {
            return _slotsByThreadId.ContainsKey(threadId);
        }
    }

    /// <summary>
    /// Returns the stable worker slot for a registered thread.
    /// </summary>
    public int GetSlot(int threadId)
    {
        lock (_gate)
        {
            return _slotsByThreadId.TryGetValue(threadId, out int slot) ? slot : 0;
        }
    }
}
