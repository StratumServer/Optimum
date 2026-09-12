namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One frame's latency breakdown, in microseconds: the eight intervals every
/// vendor tool reports (NV's <c>VkLatencyTimingsFrameReportNV</c> is the widest
/// of them, and this is its shape).
///
/// Backends with a driver report (NV) fill every field from
/// <c>vkGetLatencyTimingsNV</c>. Backends without one (None, Native, AMD) fill
/// the CPU-observable intervals from their own marker timestamps through
/// <see cref="FromCpuTimestamps" /> and leave <see cref="DriverUs" />,
/// <see cref="OsRenderQueueUs" /> and <see cref="GpuUs" /> at zero; the stats
/// line (seam S7) prints what is there.
/// </summary>
/// <param name="FrameId">The latency frame id allocated at the sleep (seam S2).</param>
/// <param name="PresentId">The present id chained as <c>VkPresentIdKHR</c>, or 0 when the frame never presented.</param>
/// <param name="InputUs">Input sample to simulation start.</param>
/// <param name="SimulationUs">Simulation start to simulation end.</param>
/// <param name="RenderSubmitUs">Render submit start to render submit end.</param>
/// <param name="PresentUs">Present start to present end (the <c>vkQueuePresentKHR</c> call itself).</param>
/// <param name="DriverUs">Driver start to driver end; 0 without a driver report.</param>
/// <param name="OsRenderQueueUs">OS render queue start to end; 0 without a driver report.</param>
/// <param name="GpuUs">GPU render start to end; 0 without a driver report.</param>
/// <param name="TotalUs">Input sample to present end: the whole frame as the player feels it.</param>
internal readonly record struct LatencyFrameReport(
    ulong FrameId,
    ulong PresentId,
    ulong InputUs,
    ulong SimulationUs,
    ulong RenderSubmitUs,
    ulong PresentUs,
    ulong DriverUs,
    ulong OsRenderQueueUs,
    ulong GpuUs,
    ulong TotalUs)
{
    /// <summary>
    /// Builds a report from the CPU timestamps a backend without a driver report
    /// collected, all on one monotonic clock in microseconds (see
    /// <see cref="LatencyClock" />). A missing marker is passed as 0 and makes the
    /// intervals that need it 0; intervals never go negative.
    /// </summary>
    public static LatencyFrameReport FromCpuTimestamps(
        ulong frameId,
        ulong presentId,
        long inputSampleUs,
        long simulationStartUs,
        long simulationEndUs,
        long renderSubmitStartUs,
        long renderSubmitEndUs,
        long presentStartUs,
        long presentEndUs)
    {
        long start = inputSampleUs != 0 ? inputSampleUs : simulationStartUs;
        return new LatencyFrameReport(
            frameId,
            presentId,
            Span(inputSampleUs, simulationStartUs),
            Span(simulationStartUs, simulationEndUs),
            Span(renderSubmitStartUs, renderSubmitEndUs),
            Span(presentStartUs, presentEndUs),
            0,
            0,
            0,
            Span(start, presentEndUs));
    }

    /// <summary>0 when either end is missing or the pair is out of order; the difference otherwise.</summary>
    private static ulong Span(long fromUs, long toUs) =>
        fromUs <= 0 || toUs <= 0 || toUs <= fromUs ? 0UL : (ulong)(toUs - fromUs);
}
