using System;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Tracks reservations for a fixed producer-to-render handoff.
/// </summary>
public sealed class OptimumBoundedHandoff
{
    private readonly int _capacity;
    private readonly int _priorityReserve;
    private int _reserved;

    public OptimumBoundedHandoff(int capacity, int priorityReserve = 0)
    {
        _capacity = Math.Max(1, capacity);
        _priorityReserve = Math.Clamp(priorityReserve, 0, _capacity - 1);
        OptimumDiagnostics.RecordTessHandoffCapacity(_capacity);
    }

    public int Capacity => _capacity;

    public int Reserved => Volatile.Read(ref _reserved);

    public bool TryReserve(bool priority)
    {
        int limit = priority ? _capacity : _capacity - _priorityReserve;
        while (true)
        {
            int current = Volatile.Read(ref _reserved);
            if (current >= limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reserved, current + 1, current) == current)
            {
                OptimumDiagnostics.RecordTessHandoffReserved(current + 1);
                return true;
            }
        }
    }

    public void Release()
    {
        while (true)
        {
            int current = Volatile.Read(ref _reserved);
            if (current <= 0)
            {
                throw new InvalidOperationException("Cannot release an empty handoff.");
            }

            if (Interlocked.CompareExchange(ref _reserved, current - 1, current) == current)
            {
                return;
            }
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _reserved, 0);
    }
}
