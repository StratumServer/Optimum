#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL;

namespace Vintagestory.GameContent;

/// <summary>
/// Non-blocking 4-slot ring buffer for OpenGL GPU timestamp queries and opt-in
/// PrimitivesGenerated / SamplesPassed hardware counters. Emits KHR_debug groups
/// when active so RenderDoc captures display labeled stages automatically.
/// </summary>
public sealed class OptimumGpuTimerRing : IDisposable
{
    public const int RingSize = 4;
    public const int StageCount = 10;
    public const int QueriesPerSlot = StageCount * 2;
    public const int GlCounterCount = 4;

    private static readonly string[] DebugGroupNames =
    {
        "Optimum:Before",
        "Optimum:ShadowNear",
        "Optimum:Opaque",
        "Optimum:OIT",
        "Optimum:AfterOIT",
        "Optimum:AfterPostProcessing",
        "Optimum:AfterFinalComposition",
        "Optimum:AfterBlit",
        "Optimum:Ortho",
        "Optimum:Done",
    };

    private readonly int[,] queryRing = new int[RingSize, QueriesPerSlot];
    private readonly int[] slotVisitedMask = new int[RingSize];
    private readonly bool[] slotPending = new bool[RingSize];

    private readonly int[,] primRing = new int[RingSize, GlCounterCount];
    private readonly int[,] sampleRing = new int[RingSize, GlCounterCount];
    private readonly int[] glCounterMask = new int[RingSize];
    private readonly bool[] glCounterPending = new bool[RingSize];
    private readonly bool[] primQueryOpen = new bool[GlCounterCount];
    private readonly bool[] sampleQueryOpen = new bool[GlCounterCount];

    private int currentSlot;
    private int currentGlCounterSlot;
    private bool initialized;
    private bool timestampSupported;
    private bool glCounterQueriesCreated;
    private bool debugGroupsSupported;
    private bool disposed;

    public bool GlCountersEnabled { get; set; }
    public string GlVendor { get; private set; } = "";
    public string GlRenderer { get; private set; } = "";
    public string GlVersion { get; private set; } = "";

    public void EnsureInitialized()
    {
        if (initialized || disposed)
        {
            return;
        }
        initialized = true;

        try
        {
            GlVendor = GL.GetString(StringName.Vendor) ?? "";
            GlRenderer = GL.GetString(StringName.Renderer) ?? "";
            GlVersion = GL.GetString(StringName.Version) ?? "";
            GL.GetInteger(GetPName.MajorVersion, out int major);
            GL.GetInteger(GetPName.MinorVersion, out int minor);

            if (major > 3 || (major == 3 && minor >= 3))
            {
                int[] ids = new int[RingSize * QueriesPerSlot];
                GL.GenQueries(ids.Length, ids);
                int idx = 0;
                for (int s = 0; s < RingSize; s++)
                {
                    for (int q = 0; q < QueriesPerSlot; q++)
                    {
                        queryRing[s, q] = ids[idx++];
                    }
                }
                timestampSupported = true;
            }

            if (major > 4 || (major == 4 && minor >= 3))
            {
                debugGroupsSupported = true;
            }
        }
        catch
        {
            timestampSupported = false;
            debugGroupsSupported = false;
        }
    }

    private void EnsureGlCountersCreated()
    {
        if (glCounterQueriesCreated || disposed || !timestampSupported)
        {
            return;
        }
        try
        {
            int[] pids = new int[RingSize * GlCounterCount];
            int[] sids = new int[RingSize * GlCounterCount];
            GL.GenQueries(pids.Length, pids);
            GL.GenQueries(sids.Length, sids);
            int idx = 0;
            for (int s = 0; s < RingSize; s++)
            {
                for (int c = 0; c < GlCounterCount; c++)
                {
                    primRing[s, c] = pids[idx];
                    sampleRing[s, c] = sids[idx];
                    idx++;
                }
            }
            glCounterQueriesCreated = true;
        }
        catch
        {
            GlCountersEnabled = false;
        }
    }

    public void BeginFrame()
    {
        EnsureInitialized();
        slotVisitedMask[currentSlot] = 0;
        slotPending[currentSlot] = false;
        glCounterMask[currentGlCounterSlot] = 0;
        glCounterPending[currentGlCounterSlot] = false;
        if (GlCountersEnabled && !glCounterQueriesCreated)
        {
            EnsureGlCountersCreated();
        }
    }

    public void MarkStage(ProfilerStageId stage, bool isStart)
    {
        if (disposed || !initialized)
        {
            return;
        }

        int s = (int)stage;
        if (isStart)
        {
            slotVisitedMask[currentSlot] |= 1 << s;
            if (debugGroupsSupported)
            {
                try { GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, s + 100, DebugGroupNames[s].Length, DebugGroupNames[s]); }
                catch { debugGroupsSupported = false; }
            }
            if (timestampSupported)
            {
                try { GL.QueryCounter(queryRing[currentSlot, s * 2], QueryCounterTarget.Timestamp); }
                catch { timestampSupported = false; }
            }
        }
        else
        {
            if (timestampSupported)
            {
                try { GL.QueryCounter(queryRing[currentSlot, s * 2 + 1], QueryCounterTarget.Timestamp); }
                catch { timestampSupported = false; }
            }
            if (debugGroupsSupported)
            {
                try { GL.PopDebugGroup(); }
                catch { debugGroupsSupported = false; }
            }
        }
    }

    public void BracketGlCounter(GlCounterStageId stage, bool isStart)
    {
        if (disposed || !GlCountersEnabled || !glCounterQueriesCreated)
        {
            return;
        }

        int idx = (int)stage;
        try
        {
            if (isStart)
            {
                glCounterMask[currentGlCounterSlot] |= 1 << idx;
                GL.BeginQuery(QueryTarget.PrimitivesGenerated, primRing[currentGlCounterSlot, idx]);
                primQueryOpen[idx] = true;
                GL.BeginQuery(QueryTarget.SamplesPassed, sampleRing[currentGlCounterSlot, idx]);
                sampleQueryOpen[idx] = true;
            }
            else
            {
                if (sampleQueryOpen[idx])
                {
                    GL.EndQuery(QueryTarget.SamplesPassed);
                    sampleQueryOpen[idx] = false;
                }
                if (primQueryOpen[idx])
                {
                    GL.EndQuery(QueryTarget.PrimitivesGenerated);
                    primQueryOpen[idx] = false;
                }
            }
        }
        catch
        {
            GlCountersEnabled = false;
        }
    }

    public void EndFrameAndHarvest(
        double[] completedGpuStagesMs,
        double[] completedGpuGapsMs,
        out double gpuSpanMs,
        long[] completedPrimitives,
        long[] completedSamplesPassed)
    {
        gpuSpanMs = 0.0;
        Array.Clear(completedGpuStagesMs, 0, completedGpuStagesMs.Length);
        Array.Clear(completedGpuGapsMs, 0, completedGpuGapsMs.Length);
        Array.Clear(completedPrimitives, 0, completedPrimitives.Length);
        Array.Clear(completedSamplesPassed, 0, completedSamplesPassed.Length);

        if (disposed)
        {
            return;
        }

        if (timestampSupported && slotVisitedMask[currentSlot] != 0)
        {
            slotPending[currentSlot] = true;
        }
        currentSlot = (currentSlot + 1) % RingSize;

        if (GlCountersEnabled && glCounterQueriesCreated && glCounterMask[currentGlCounterSlot] != 0)
        {
            glCounterPending[currentGlCounterSlot] = true;
        }
        currentGlCounterSlot = (currentGlCounterSlot + 1) % RingSize;

        HarvestTimestamps(completedGpuStagesMs, completedGpuGapsMs, ref gpuSpanMs);
        HarvestGlCounters(completedPrimitives, completedSamplesPassed);
    }

    private void HarvestTimestamps(double[] stagesMs, double[] gapsMs, ref double gpuSpanMs)
    {
        if (!timestampSupported)
        {
            return;
        }

        for (int offset = 0; offset < RingSize; offset++)
        {
            int slot = (currentSlot + offset) % RingSize;
            if (!slotPending[slot])
            {
                continue;
            }

            int mask = slotVisitedMask[slot];
            int lastStage = -1;
            for (int s = StageCount - 1; s >= 0; s--)
            {
                if ((mask & (1 << s)) != 0)
                {
                    lastStage = s;
                    break;
                }
            }
            if (lastStage < 0)
            {
                slotPending[slot] = false;
                continue;
            }

            GL.GetQueryObject(queryRing[slot, lastStage * 2 + 1], GetQueryObjectParam.QueryResultAvailable, out int avail);
            if (avail == 0)
            {
                continue;
            }

            long[] tStart = new long[StageCount];
            long[] tEnd = new long[StageCount];
            for (int s = 0; s < StageCount; s++)
            {
                if ((mask & (1 << s)) == 0)
                {
                    continue;
                }
                GL.GetQueryObject(queryRing[slot, s * 2], GetQueryObjectParam.QueryResult, out tStart[s]);
                GL.GetQueryObject(queryRing[slot, s * 2 + 1], GetQueryObjectParam.QueryResult, out tEnd[s]);
                stagesMs[s] = Math.Max(0.0, (tEnd[s] - tStart[s]) / 1_000_000.0);
            }

            // Inter-stage gaps: index 0 = Before->ShadowNear (ShadowFar), 1 = Opaque->OIT, 2 = OIT->AfterOIT,
            // 3 = AfterOIT->AfterPostProcessing, 4 = AfterPostProcessing->AfterFinalComposition, 5 = AfterFinalComposition->AfterBlit
            ComputeGap(mask, tEnd, tStart, 0, 1, gapsMs, 0);
            ComputeGap(mask, tEnd, tStart, 2, 3, gapsMs, 1);
            ComputeGap(mask, tEnd, tStart, 3, 4, gapsMs, 2);
            ComputeGap(mask, tEnd, tStart, 4, 5, gapsMs, 3);
            ComputeGap(mask, tEnd, tStart, 5, 6, gapsMs, 4);
            ComputeGap(mask, tEnd, tStart, 6, 7, gapsMs, 5);

            if ((mask & 1) != 0)
            {
                gpuSpanMs = Math.Max(0.0, (tEnd[lastStage] - tStart[0]) / 1_000_000.0);
            }

            slotPending[slot] = false;
            break;
        }
    }

    private static void ComputeGap(int mask, long[] tEnd, long[] tStart, int a, int b, double[] gapsMs, int outIdx)
    {
        if ((mask & (1 << a)) != 0 && (mask & (1 << b)) != 0)
        {
            gapsMs[outIdx] = Math.Max(0.0, (tStart[b] - tEnd[a]) / 1_000_000.0);
        }
    }

    private void HarvestGlCounters(long[] primitives, long[] samplesPassed)
    {
        if (!glCounterQueriesCreated)
        {
            return;
        }

        for (int offset = 0; offset < RingSize; offset++)
        {
            int slot = (currentGlCounterSlot + offset) % RingSize;
            if (!glCounterPending[slot])
            {
                continue;
            }

            int mask = glCounterMask[slot];
            int lastIdx = -1;
            for (int c = GlCounterCount - 1; c >= 0; c--)
            {
                if ((mask & (1 << c)) != 0)
                {
                    lastIdx = c;
                    break;
                }
            }
            if (lastIdx < 0)
            {
                glCounterPending[slot] = false;
                continue;
            }

            GL.GetQueryObject(sampleRing[slot, lastIdx], GetQueryObjectParam.QueryResultAvailable, out int avail);
            if (avail == 0)
            {
                continue;
            }

            for (int c = 0; c < GlCounterCount; c++)
            {
                if ((mask & (1 << c)) == 0)
                {
                    continue;
                }
                GL.GetQueryObject(primRing[slot, c], GetQueryObjectParam.QueryResult, out long prims);
                GL.GetQueryObject(sampleRing[slot, c], GetQueryObjectParam.QueryResult, out long samples);
                primitives[c] = prims;
                samplesPassed[c] = samples;
            }

            glCounterPending[slot] = false;
            break;
        }
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
            try
            {
                int[] ids = new int[RingSize * QueriesPerSlot];
                int idx = 0;
                for (int s = 0; s < RingSize; s++)
                    for (int q = 0; q < QueriesPerSlot; q++)
                        ids[idx++] = queryRing[s, q];
                GL.DeleteQueries(ids.Length, ids);
            }
            catch
            {
            }
            timestampSupported = false;
        }

        if (glCounterQueriesCreated)
        {
            try
            {
                int[] ids = new int[RingSize * GlCounterCount * 2];
                int idx = 0;
                for (int s = 0; s < RingSize; s++)
                {
                    for (int c = 0; c < GlCounterCount; c++)
                    {
                        ids[idx++] = primRing[s, c];
                        ids[idx++] = sampleRing[s, c];
                    }
                }
                GL.DeleteQueries(ids.Length, ids);
            }
            catch
            {
            }
            glCounterQueriesCreated = false;
        }
    }
}
