#nullable enable
using System;
using OpenTK.Graphics.OpenGL;

namespace Vintagestory.GameContent;

/// <summary>
/// Non-blocking ring of GL timestamp queries (one per marker) and opt-in PrimitivesGenerated /
/// SamplesPassed windows. Each slot remembers the frame it was issued for, so results harvested
/// a few frames later are attributed to the right frame. Emits KHR_debug groups per segment so
/// RenderDoc captures show labeled passes.
/// </summary>
public sealed class OptimumGpuTimerRing : IDisposable
{
    public const int RingSize = 4;
    private const int Markers = ProfilerSegments.MarkerCount;
    private const int Windows = ProfilerGlCounters.WindowCount;
    private const double NsToMs = 1.0 / 1_000_000.0;

    private readonly int[] stampQueries = new int[RingSize * Markers];
    private readonly int[] counterQueries = new int[RingSize * Windows * 2];
    private readonly long[] slotFrame = new long[RingSize];
    private readonly int[] slotMarkerMask = new int[RingSize];
    private readonly int[] slotWindowMask = new int[RingSize];
    private readonly bool[] slotPending = new bool[RingSize];
    private readonly long[] stamps = new long[Markers];

    private int current;
    private int openWindow = -1;
    private bool debugGroupOpen;
    private bool initialized;
    private bool timestampSupported;
    private bool countersCreated;
    private bool debugGroupsSupported;
    private bool disposed;

    public bool CountersEnabled { get; set; }
    public string GlVendor { get; private set; } = "";
    public string GlRenderer { get; private set; } = "";
    public string GlVersion { get; private set; } = "";

    private void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }
        initialized = true;
        GlVendor = GL.GetString(StringName.Vendor) ?? "";
        GlRenderer = GL.GetString(StringName.Renderer) ?? "";
        GlVersion = GL.GetString(StringName.Version) ?? "";
        GL.GetInteger(GetPName.MajorVersion, out int major);
        GL.GetInteger(GetPName.MinorVersion, out int minor);
        timestampSupported = major > 3 || (major == 3 && minor >= 3);
        debugGroupsSupported = major > 4 || (major == 4 && minor >= 3);
        if (timestampSupported)
        {
            GL.GenQueries(stampQueries.Length, stampQueries);
        }
    }

    public void BeginFrame(long frameIndex)
    {
        if (disposed)
        {
            return;
        }
        EnsureInitialized();
        if (CountersEnabled && !countersCreated && timestampSupported)
        {
            GL.GenQueries(counterQueries.Length, counterQueries);
            countersCreated = true;
        }
        // A slot still pending here is four frames old and unread; reusing it drops that frame's GPU data.
        slotFrame[current] = frameIndex;
        slotMarkerMask[current] = 0;
        slotWindowMask[current] = 0;
        slotPending[current] = false;
    }

    public void Mark(ProfilerMarker marker)
    {
        if (disposed || !timestampSupported)
        {
            return;
        }
        int m = (int)marker;
        if (debugGroupsSupported)
        {
            if (debugGroupOpen)
            {
                GL.PopDebugGroup();
                debugGroupOpen = false;
            }
            if (marker != ProfilerMarker.DoneEnd)
            {
                string label = ProfilerSegments.Labels[ProfilerSegments.SegmentOf(marker)];
                GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 100 + m, label.Length, label);
                debugGroupOpen = true;
            }
        }
        GL.QueryCounter(stampQueries[current * Markers + m], QueryCounterTarget.Timestamp);
        slotMarkerMask[current] |= 1 << m;
    }

    public void BeginWindow(int window)
    {
        if (disposed || !CountersEnabled || !countersCreated || openWindow >= 0)
        {
            return;
        }
        int q = (current * Windows + window) * 2;
        GL.BeginQuery(QueryTarget.PrimitivesGenerated, counterQueries[q]);
        GL.BeginQuery(QueryTarget.SamplesPassed, counterQueries[q + 1]);
        openWindow = window;
    }

    public void EndWindow(int window)
    {
        if (openWindow != window)
        {
            return;
        }
        GL.EndQuery(QueryTarget.SamplesPassed);
        GL.EndQuery(QueryTarget.PrimitivesGenerated);
        slotWindowMask[current] |= 1 << window;
        openWindow = -1;
    }

    public void EndFrame()
    {
        if (disposed)
        {
            return;
        }
        if (openWindow >= 0)
        {
            EndWindow(openWindow);
        }
        if (debugGroupOpen)
        {
            GL.PopDebugGroup();
            debugGroupOpen = false;
        }
        slotPending[current] = slotMarkerMask[current] != 0;
        current = (current + 1) % RingSize;
    }

    /// <summary>
    /// Reads the oldest completed slot without blocking. Returns false when nothing is ready.
    /// </summary>
    public bool TryHarvest(out long frameIndex, Span<double> segmentsMs, out double spanMs, out bool hasCounters, Span<long> primitives, Span<long> samplesPassed)
    {
        frameIndex = -1;
        spanMs = 0.0;
        hasCounters = false;
        if (disposed || !timestampSupported)
        {
            return false;
        }

        for (int offset = 0; offset < RingSize; offset++)
        {
            int slot = (current + offset) % RingSize;
            if (!slotPending[slot])
            {
                continue;
            }

            int mask = slotMarkerMask[slot];
            int last = 31 - int.LeadingZeroCount(mask);
            GL.GetQueryObject(stampQueries[slot * Markers + last], GetQueryObjectParam.QueryResultAvailable, out int available);
            if (available == 0)
            {
                // The GPU completes queries in order, so newer slots are not ready either.
                return false;
            }

            for (int m = 0; m < Markers; m++)
            {
                if ((mask & (1 << m)) != 0)
                {
                    GL.GetQueryObject(stampQueries[slot * Markers + m], GetQueryObjectParam.QueryResult, out stamps[m]);
                }
            }
            spanMs = ProfilerSegments.Compute(stamps, mask, NsToMs, segmentsMs);

            primitives.Clear();
            samplesPassed.Clear();
            int windows = slotWindowMask[slot];
            hasCounters = windows != 0;
            for (int w = 0; w < Windows; w++)
            {
                if ((windows & (1 << w)) == 0)
                {
                    continue;
                }
                int q = (slot * Windows + w) * 2;
                GL.GetQueryObject(counterQueries[q], GetQueryObjectParam.QueryResult, out long prims);
                GL.GetQueryObject(counterQueries[q + 1], GetQueryObjectParam.QueryResult, out long samples);
                int c = ProfilerGlCounters.CounterOfWindow[w];
                primitives[c] += prims;
                samplesPassed[c] += samples;
            }

            frameIndex = slotFrame[slot];
            slotPending[slot] = false;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (timestampSupported)
        {
            GL.DeleteQueries(stampQueries.Length, stampQueries);
        }
        if (countersCreated)
        {
            GL.DeleteQueries(counterQueries.Length, counterQueries);
        }
    }
}
