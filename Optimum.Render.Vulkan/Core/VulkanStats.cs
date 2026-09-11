using System;
using System.Diagnostics;
using System.Threading;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Per-second counters for the work the backend does, so a slow phase can be
/// attributed rather than guessed at.
///
/// A frame time on its own says the renderer is slow; it does not say whether
/// the cost is device allocations, blocking uploads waiting on the GPU, or
/// descriptor churn. These count each of those and report them together.
///
/// Off unless OPTIMUM_VULKAN_STATS names a file. The counters themselves are
/// always live - they are interlocked increments on paths that already cost
/// microseconds apiece, so they do not need gating, and having them unconditional
/// means a report can be turned on for a session that is already misbehaving.
/// </summary>
internal static class VulkanStats
{
    private static long _allocations;
    private static long _uploads;
    private static long _uploadWaitTicks;
    private static long _texturesCreated;
    private static long _texturesDeleted;
    private static long _frames;
    private static long _droppedMeshWrites;
    private static long _uniformOverflows;

    /// <summary>A mesh write that could not land; see MeshManager.Write.</summary>
    public static void NoteDroppedMeshWrite() => Interlocked.Increment(ref _droppedMeshWrites);

    public static long DroppedMeshWrites => Interlocked.Read(ref _droppedMeshWrites);

    /// <summary>A named uniform block that did not fit the frame ring and took a transient buffer instead.</summary>
    public static void NoteUniformOverflow() => Interlocked.Increment(ref _uniformOverflows);

    public static long UniformOverflows => Interlocked.Read(ref _uniformOverflows);

    public static void NoteAllocation() => Interlocked.Increment(ref _allocations);
    public static void NoteTextureCreated() => Interlocked.Increment(ref _texturesCreated);
    public static void NoteTextureDeleted() => Interlocked.Increment(ref _texturesDeleted);
    public static void NoteFrame() => Interlocked.Increment(ref _frames);

    /// <summary>Textures deleted since the last <see cref="SampleIfDue" />.</summary>
    public static long TexturesDeleted => Interlocked.Read(ref _texturesDeleted);

    public static void NoteUpload(long elapsedTicks)
    {
        Interlocked.Increment(ref _uploads);
        Interlocked.Add(ref _uploadWaitTicks, elapsedTicks);
    }

    /// <summary>
    /// Takes and clears the counters, formatted as one line, or null when the
    /// interval has not elapsed. Called once per frame by the render thread.
    /// </summary>
    public static string? SampleIfDue(TimeSpan interval)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastSample);
        if (last == 0)
        {
            Interlocked.CompareExchange(ref _lastSample, now, 0);
            return null;
        }

        double elapsed = (now - last) / (double)Stopwatch.Frequency;
        if (elapsed < interval.TotalSeconds) return null;
        if (Interlocked.CompareExchange(ref _lastSample, now, last) != last) return null;

        long frames = Interlocked.Exchange(ref _frames, 0);
        long allocations = Interlocked.Exchange(ref _allocations, 0);
        long uploads = Interlocked.Exchange(ref _uploads, 0);
        long uploadTicks = Interlocked.Exchange(ref _uploadWaitTicks, 0);
        long created = Interlocked.Exchange(ref _texturesCreated, 0);
        long deleted = Interlocked.Exchange(ref _texturesDeleted, 0);
        long dropped = Interlocked.Exchange(ref _droppedMeshWrites, 0);
        long overflows = Interlocked.Exchange(ref _uniformOverflows, 0);

        double uploadMs = uploadTicks * 1000.0 / Stopwatch.Frequency;
        double frameMs = frames > 0 ? elapsed * 1000.0 / frames : 0;

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "stats {0:F1}s: {1} frames ({2:F1} ms/frame), {3} allocations ({4} live), " +
            "{5} blocking uploads costing {6:F0} ms ({7:F0}% of the interval), " +
            "textures +{8}/-{9}, mesh writes dropped {10}, uniform overflows {11}",
            elapsed, frames, frameMs, allocations, VulkanMemory.LiveAllocations,
            uploads, uploadMs, uploadMs / (elapsed * 1000.0) * 100.0, created, deleted, dropped, overflows);
    }

    private static long _lastSample;
}
