using System;
using System.Collections.Generic;
using System.Threading;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Resources waiting for the GPU to stop referencing them.
///
/// Each entry records the newest Frame and Transfer values that existed when it
/// was retired: any command that could name the resource carries one of those
/// values or an older one. The entry is destroyed at the first
/// <see cref="Collect" /> that sees both counters at or past its values, never
/// earlier, and ready entries are destroyed in the order they were retired.
///
/// <see cref="Retire" /> is safe from any thread (the game's VAO and UBO
/// finalizers release from the finalizer thread). <see cref="Collect" /> runs on
/// the render thread at the start of a frame, and disposes outside the lock so a
/// resource whose Dispose retires something else cannot deadlock.
/// </summary>
internal sealed class RetireQueue
{
    private readonly record struct Entry(IDisposable Resource, ulong Frame, ulong Transfer);

    private readonly ITimelineClock _clock;
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    private int _count;

    public RetireQueue(ITimelineClock clock) => _clock = clock;

    /// <summary>Entries not yet destroyed.</summary>
    public int PendingCount => Volatile.Read(ref _count);

    /// <summary>Queues <paramref name="resource" /> against the timeline values recorded right now.</summary>
    public void Retire(IDisposable resource)
    {
        lock (_lock)
        {
            _entries.Add(new Entry(resource, _clock.FrameRecorded, _clock.TransferRecorded));
            Volatile.Write(ref _count, _entries.Count);
        }
    }

    /// <summary>
    /// Destroys every entry whose Frame and Transfer values have both completed,
    /// oldest first. An entry that has not passed stays queued without holding back
    /// later entries that have. Returns how many were destroyed.
    /// </summary>
    public int Collect()
    {
        // Completion only moves forward, so counters read before taking the lock
        // are still true for every entry inside it.
        return CollectThrough(_clock.FrameCompleted, _clock.TransferCompleted);
    }

    /// <summary>
    /// <see cref="Collect" /> against explicit ceilings, for the one caller that
    /// knows a Frame value is safe without the GPU having reached it: a frame that
    /// was reserved and then abandoned never signals its value, so nothing the GPU
    /// runs can ever name the resources retired inside it, and waiting for
    /// <c>FrameCompleted</c> to pass that value would wait for ever.
    /// </summary>
    public int CollectThrough(ulong frameCompleted, ulong transferCompleted)
    {
        if (PendingCount == 0) return 0;

        List<IDisposable>? ready = null;
        lock (_lock)
        {
            int kept = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Frame <= frameCompleted && entry.Transfer <= transferCompleted)
                {
                    ready ??= new List<IDisposable>();
                    ready.Add(entry.Resource);
                }
                else
                {
                    _entries[kept++] = entry;
                }
            }
            _entries.RemoveRange(kept, _entries.Count - kept);
            Volatile.Write(ref _count, _entries.Count);
        }

        if (ready == null) return 0;
        foreach (IDisposable resource in ready) resource.Dispose();
        return ready.Count;
    }

    /// <summary>
    /// Destroys everything regardless of the timelines, oldest first. Teardown
    /// only, after the GPU has finished all submitted work.
    /// </summary>
    public void DisposeAll()
    {
        while (true)
        {
            Entry[] all;
            lock (_lock)
            {
                if (_entries.Count == 0) return;
                all = _entries.ToArray();
                _entries.Clear();
                Volatile.Write(ref _count, 0);
            }
            foreach (Entry entry in all) entry.Resource.Dispose();
        }
    }
}
