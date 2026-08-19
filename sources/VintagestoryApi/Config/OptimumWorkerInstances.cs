using System;

namespace Vintagestory.API.Config;

/// <summary>
/// Pre-built pool of N instances of <typeparamref name="T"/>, indexed by worker
/// slot. Workers get a full object copy rather than ThreadLocal field-by-field
/// conversion, which preserves reflection-visible field names and avoids the
/// "value fetched once, used after a thread hop" bug class.
/// </summary>
internal sealed class OptimumWorkerInstances<T> where T : class
{
    private readonly T[] _instances;

    /// <summary>Number of pre-allocated slots.</summary>
    public int SlotCount => _instances.Length;

    /// <summary>
    /// Create the pool with <paramref name="slotCount"/> pre-built instances.
    /// </summary>
    public OptimumWorkerInstances(int slotCount)
    {
        if (slotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Must be at least 1.");

        _instances = new T[slotCount];
        for (int i = 0; i < slotCount; i++)
            _instances[i] = Activator.CreateInstance<T>() ?? throw new InvalidOperationException($"Could not create worker instance for slot {i}.");
    }

    /// <summary>
    /// Create the pool with <paramref name="slotCount"/> instances built by
    /// <paramref name="factory"/>. Use when T needs constructor arguments.
    /// </summary>
    public OptimumWorkerInstances(int slotCount, Func<int, T> factory)
    {
        if (slotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Must be at least 1.");
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));

        _instances = new T[slotCount];
        for (int i = 0; i < slotCount; i++)
            _instances[i] = factory(i) ?? throw new InvalidOperationException($"Factory returned null for slot {i}.");
    }

    /// <summary>
    /// Get the instance for the given worker slot. Same slot always returns
    /// the same reference. Different slots never share an instance.
    /// </summary>
    public T Get(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)_instances.Length)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), $"Slot {slotIndex} out of range [0, {_instances.Length}).");

        return _instances[slotIndex];
    }
}
