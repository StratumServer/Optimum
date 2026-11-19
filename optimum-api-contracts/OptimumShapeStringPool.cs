using System;

namespace Vintagestory.API.Config;

/// <summary>
/// Bounded, thread-safe, session-scoped ordinal string deduplication pool for shape and animation
/// bone/element names, replacing permanent CLR global interning (<see cref="string.Intern"/>) so
/// shape loads share repeated names while asset reloads and world exits can release all entries.
/// </summary>
internal sealed class OptimumShapeStringPool
{
    public static readonly OptimumShapeStringPool Shared = new();

    private const int ShardCount = 16;
    private const int ShardMask = ShardCount - 1;
    private const int InitialShardCapacity = 64;
    private const int MaxShardCapacity = 8192;
    private const int MaxShardEntries = 6144; // 16 * 6,144 = 98,304 distinct strings max
    private const int MaxStackConcatChars = 256;

    private sealed class Shard
    {
        public string?[]? Slots;
        public int Count;
    }

    private readonly Shard[] shards;

    public OptimumShapeStringPool()
    {
        shards = new Shard[ShardCount];
        for (int i = 0; i < ShardCount; i++)
        {
            shards[i] = new Shard();
        }
    }

    public string? Deduplicate(string? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = value.AsSpan();
        int hash = string.GetHashCode(span, StringComparison.Ordinal);
        return FindOrInsert(span, value, hash);
    }

    public string ConcatAndDeduplicate(string? prefix, string? value)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return Deduplicate(value ?? string.Empty)!;
        }

        if (string.IsNullOrEmpty(value))
        {
            return Deduplicate(prefix)!;
        }

        int totalLen = prefix.Length + value.Length;
        if (totalLen > MaxStackConcatChars)
        {
            return Deduplicate(string.Concat(prefix, value))!;
        }

        Span<char> combined = stackalloc char[totalLen];
        prefix.AsSpan().CopyTo(combined);
        value.AsSpan().CopyTo(combined.Slice(prefix.Length));

        int hash = string.GetHashCode(combined, StringComparison.Ordinal);
        return FindOrInsert(combined, null, hash);
    }

    public void Clear()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            Shard shard = shards[i];
            lock (shard)
            {
                shard.Slots = null;
                shard.Count = 0;
            }
        }
    }

    internal (int EntryCount, int AllocatedSlots) GetStats()
    {
        int entries = 0;
        int slots = 0;
        for (int i = 0; i < ShardCount; i++)
        {
            Shard shard = shards[i];
            lock (shard)
            {
                entries += shard.Count;
                slots += shard.Slots?.Length ?? 0;
            }
        }
        return (entries, slots);
    }

    private string FindOrInsert(ReadOnlySpan<char> chars, string? materialized, int hash)
    {
        Shard shard = shards[((uint)hash >> 24) & ShardMask];
        lock (shard)
        {
            string?[]? slots = shard.Slots;
            if (slots == null)
            {
                slots = new string?[InitialShardCapacity];
                shard.Slots = slots;
            }

            int mask = slots.Length - 1;
            int idx = hash & mask;

            while (true)
            {
                string? existing = slots[idx];
                if (existing == null)
                {
                    if (shard.Count >= MaxShardEntries)
                    {
                        return materialized ?? new string(chars);
                    }

                    if ((shard.Count + 1) * 4 >= slots.Length * 3 && slots.Length < MaxShardCapacity)
                    {
                        slots = GrowLocked(shard);
                        mask = slots.Length - 1;
                        idx = hash & mask;
                        while (slots[idx] != null)
                        {
                            idx = (idx + 1) & mask;
                        }
                    }

                    string canonical = materialized ?? new string(chars);
                    slots[idx] = canonical;
                    shard.Count++;
                    return canonical;
                }

                if (existing.AsSpan().SequenceEqual(chars))
                {
                    return existing;
                }

                idx = (idx + 1) & mask;
            }
        }
    }

    private static string?[] GrowLocked(Shard shard)
    {
        string?[] oldSlots = shard.Slots!;
        int newCapacity = oldSlots.Length * 2;
        string?[] newSlots = new string?[newCapacity];
        int mask = newCapacity - 1;

        for (int i = 0; i < oldSlots.Length; i++)
        {
            string? entry = oldSlots[i];
            if (entry == null)
            {
                continue;
            }

            int hash = string.GetHashCode(entry.AsSpan(), StringComparison.Ordinal);
            int idx = hash & mask;
            while (newSlots[idx] != null)
            {
                idx = (idx + 1) & mask;
            }
            newSlots[idx] = entry;
        }

        shard.Slots = newSlots;
        return newSlots;
    }
}
