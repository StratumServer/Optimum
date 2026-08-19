using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Identifies one worldgen column in one dimension.
/// </summary>
public readonly struct OptimumWorldgenFootprintKey : IEquatable<OptimumWorldgenFootprintKey>, IComparable<OptimumWorldgenFootprintKey>
{
    public OptimumWorldgenFootprintKey(int dimension, int chunkX, int chunkZ)
    {
        Dimension = dimension;
        ChunkX = chunkX;
        ChunkZ = chunkZ;
    }

    public int Dimension { get; }
    public int ChunkX { get; }
    public int ChunkZ { get; }

    public int CompareTo(OptimumWorldgenFootprintKey other)
    {
        int dimension = Dimension.CompareTo(other.Dimension);
        if (dimension != 0) return dimension;

        int chunkX = ChunkX.CompareTo(other.ChunkX);
        return chunkX != 0 ? chunkX : ChunkZ.CompareTo(other.ChunkZ);
    }

    public bool Equals(OptimumWorldgenFootprintKey other)
    {
        return Dimension == other.Dimension && ChunkX == other.ChunkX && ChunkZ == other.ChunkZ;
    }

    public override bool Equals(object? obj)
    {
        return obj is OptimumWorldgenFootprintKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Dimension, ChunkX, ChunkZ);
    }
}

/// <summary>
/// Reserves a set of worldgen columns with deterministic lock ordering.
/// </summary>
public sealed class OptimumWorldgenFootprintRegistry
{
    private readonly ConcurrentDictionary<OptimumWorldgenFootprintKey, object> claims = new();

    public bool TryAcquire(IEnumerable<OptimumWorldgenFootprintKey> keys, out OptimumWorldgenFootprintLease? lease)
    {
        if (keys == null) throw new ArgumentNullException(nameof(keys));

        var ordered = new List<OptimumWorldgenFootprintKey>();
        var unique = new HashSet<OptimumWorldgenFootprintKey>();
        foreach (OptimumWorldgenFootprintKey key in keys)
        {
            if (unique.Add(key)) ordered.Add(key);
        }
        ordered.Sort();

        var token = new object();
        int acquired = 0;
        for (; acquired < ordered.Count; acquired++)
        {
            if (claims.TryAdd(ordered[acquired], token)) continue;

            for (int i = 0; i < acquired; i++) claims.TryRemove(ordered[i], out _);
            lease = null;
            return false;
        }

        lease = new OptimumWorldgenFootprintLease(this, ordered, token);
        return true;
    }

    internal void Release(IReadOnlyList<OptimumWorldgenFootprintKey> keys, object token)
    {
        var entries = (ICollection<KeyValuePair<OptimumWorldgenFootprintKey, object>>)claims;
        for (int i = 0; i < keys.Count; i++) entries.Remove(new KeyValuePair<OptimumWorldgenFootprintKey, object>(keys[i], token));
    }
}

/// <summary>
/// Releases a worldgen footprint reservation once.
/// </summary>
public sealed class OptimumWorldgenFootprintLease : IDisposable
{
    private readonly OptimumWorldgenFootprintRegistry registry;
    private readonly IReadOnlyList<OptimumWorldgenFootprintKey> keys;
    private readonly object token;
    private int disposed;

    internal OptimumWorldgenFootprintLease(
        OptimumWorldgenFootprintRegistry registry,
        IReadOnlyList<OptimumWorldgenFootprintKey> keys,
        object token)
    {
        this.registry = registry;
        this.keys = keys;
        this.token = token;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            registry.Release(keys, token);
        }
    }
}
