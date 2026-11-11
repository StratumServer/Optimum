using System;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Chooses how long the render thread may spend uploading finished chunk meshes in one frame.
/// The budget is the slack between a 60 Hz frame and the time the rest of the frame needs, so an
/// upload pass never pushes a frame that was fine past the smoothness threshold. A frame that is
/// already over it still gets <see cref="MinBudgetMs"/>, and a long queue gets at least
/// <see cref="BacklogFloorMs"/>, so the queue always drains. Before a chunk is uploaded its cost is
/// estimated from the nanoseconds per vertex measured so far, and a chunk that does not fit in the
/// remaining time waits for the next frame, except the first chunk of a frame.
/// </summary>
public sealed class OptimumUploadBudget
{
    /// <summary>Frame time in milliseconds that uploads must not push a frame past, 60 Hz.</summary>
    public const double TargetFrameMs = 1000.0 / 60.0;

    /// <summary>Least time in milliseconds a frame may spend uploading, however slow the frame.</summary>
    public const double MinBudgetMs = 2.0;

    /// <summary>Most time in milliseconds a frame may spend uploading.</summary>
    public const double MaxBudgetMs = 8.0;

    /// <summary>Least budget in milliseconds while at least half of the upload queue is full.</summary>
    public const double BacklogFloorMs = 4.0;

    /// <summary>Queue fullness at which the backlog floor applies.</summary>
    public const double BacklogFraction = 0.5;

    private const double SmoothingAlpha = 0.2;

    public static OptimumUploadBudget Shared { get; } = new();

    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private double frameMs;
    private double uploadMs;
    private double ticksPerVertex;
    private double averageVertices;
    private long lastBudgetTicks;

    /// <summary>Budget chosen for the current frame, in milliseconds.</summary>
    public double LastBudgetMs => Volatile.Read(ref lastBudgetTicks) / TicksPerMs;

    public double SmoothedFrameMs => frameMs;

    public double SmoothedUploadMs => uploadMs;

    public double TicksPerVertex => ticksPerVertex;

    /// <summary>
    /// Mean vertex count of recent uploads. The normal upload queue cannot be peeked, so this stands in
    /// for the size of the next mesh.
    /// </summary>
    public int AverageVertices => (int)averageVertices;

    /// <summary>
    /// Starts a frame. Returns the upload budget in stopwatch ticks from the frame time of the
    /// previous frame, the upload time that frame used and the queue backlog.
    /// </summary>
    public long BeginFrame(float frameDeltaMs, int backlog, int capacity)
    {
        if (frameDeltaMs > 0f)
        {
            frameMs = frameMs == 0.0 ? frameDeltaMs : SmoothingAlpha * frameDeltaMs + (1.0 - SmoothingAlpha) * frameMs;
        }

        double otherWorkMs = Math.Max(0.0, frameMs - uploadMs);
        double budgetMs = Math.Clamp(TargetFrameMs - otherWorkMs, MinBudgetMs, MaxBudgetMs);
        if (capacity > 0 && backlog >= capacity * BacklogFraction)
        {
            budgetMs = Math.Max(budgetMs, BacklogFloorMs);
        }

        long ticks = (long)(budgetMs * TicksPerMs);
        Volatile.Write(ref lastBudgetTicks, ticks);
        return ticks;
    }

    /// <summary>Estimated ticks to upload a mesh with the given vertex count.</summary>
    public long EstimateTicks(int vertices) => (long)(vertices * ticksPerVertex);

    /// <summary>
    /// True when the next mesh may be uploaded now. The first upload of a frame always fits, so a
    /// frame with a huge chunk still makes progress.
    /// </summary>
    public bool Fits(long elapsedTicks, long budgetTicks, int nextVertices, bool anyUploaded)
    {
        return !anyUploaded || elapsedTicks + EstimateTicks(nextVertices) <= budgetTicks;
    }

    /// <summary>
    /// Decides whether the next mesh may be uploaded. With the deadline budget off this is the
    /// legacy rule, upload until the fixed time limit has passed.
    /// </summary>
    public bool AllowsUpload(bool deadline, long elapsedTicks, long budgetTicks, int nextVertices, bool anyUploaded)
    {
        return deadline ? Fits(elapsedTicks, budgetTicks, nextVertices, anyUploaded) : elapsedTicks < budgetTicks;
    }

    /// <summary>Learns the upload cost per vertex from one finished upload.</summary>
    public void RecordChunk(int vertices, long ticks)
    {
        if (vertices <= 0 || ticks <= 0)
        {
            return;
        }

        double sample = (double)ticks / vertices;
        ticksPerVertex = ticksPerVertex == 0.0 ? sample : SmoothingAlpha * sample + (1.0 - SmoothingAlpha) * ticksPerVertex;
        averageVertices = averageVertices == 0.0 ? vertices : SmoothingAlpha * vertices + (1.0 - SmoothingAlpha) * averageVertices;
    }

    /// <summary>Records the time one frame's upload pass used, once per frame that uploaded.</summary>
    public void RecordFrameUpload(long ticks)
    {
        double ms = ticks / TicksPerMs;
        uploadMs = SmoothingAlpha * ms + (1.0 - SmoothingAlpha) * uploadMs;
    }

    public void Reset()
    {
        frameMs = 0.0;
        uploadMs = 0.0;
        ticksPerVertex = 0.0;
        averageVertices = 0.0;
        Volatile.Write(ref lastBudgetTicks, 0);
    }
}
