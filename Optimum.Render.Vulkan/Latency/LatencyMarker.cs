namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The frame phases every vendor latency tool knows about.
///
/// The values are the values of <c>VkLatencyMarkerNV</c> (Silk.NET's
/// <c>LatencyMarkerNV</c>), so the NV backend can cast this straight into
/// <c>vkSetLatencyMarkerNV</c> without a translation table; a unit test pins
/// every one of them against Silk.NET. The other backends use the same set:
/// AMD's anti-lag has only INPUT and PRESENT, and the None backend records all
/// of them as CPU timestamps.
///
/// Where each one is stamped in an Optimum frame (plan section "Latency seams",
/// seams S3 and S4; the renderer owns all of them, never double-stamped):
/// <list type="bullet">
/// <item><description><see cref="InputSample" /> and <see cref="SimulationStart" />:
/// in <c>VulkanClientPlatform.LatencySleep</c>, right after the sleep returns and
/// immediately before the client gathers the mouse delta.</description></item>
/// <item><description><see cref="SimulationEnd" /> and <see cref="RenderSubmitStart" />:
/// on the first <c>BeginRenderStage(Before)</c> of the frame.</description></item>
/// <item><description><see cref="RenderSubmitEnd" />: after Submit A in
/// <c>VulkanDevice.Present</c>.</description></item>
/// <item><description><see cref="PresentStart" /> / <see cref="PresentEnd" />:
/// around <c>Swapchain.Present</c>.</description></item>
/// <item><description>The OutOfBand markers: reserved for submissions outside the
/// frame loop (standalone uploads, async present paths). Nothing stamps them yet.</description></item>
/// </list>
/// </summary>
internal enum LatencyMarker
{
    /// <summary>The client's simulation tick starts (after the sleep and the input sample).</summary>
    SimulationStart = 0,

    /// <summary>The simulation tick ends; rendering begins.</summary>
    SimulationEnd = 1,

    /// <summary>The renderer starts recording and submitting the frame's work.</summary>
    RenderSubmitStart = 2,

    /// <summary>The frame's last work submission has been queued (Submit A).</summary>
    RenderSubmitEnd = 3,

    /// <summary>Immediately before <c>vkQueuePresentKHR</c>.</summary>
    PresentStart = 4,

    /// <summary>Immediately after <c>vkQueuePresentKHR</c> returns.</summary>
    PresentEnd = 5,

    /// <summary>The frame's input is sampled; this is the point latency is measured from.</summary>
    InputSample = 6,

    /// <summary>A latency-measurement flash was triggered (tooling only).</summary>
    TriggerFlash = 7,

    /// <summary>A submission outside the frame loop starts.</summary>
    OutOfBandRenderSubmitStart = 8,

    /// <summary>A submission outside the frame loop has been queued.</summary>
    OutOfBandRenderSubmitEnd = 9,

    /// <summary>A present outside the frame loop starts.</summary>
    OutOfBandPresentStart = 10,

    /// <summary>A present outside the frame loop has returned.</summary>
    OutOfBandPresentEnd = 11,
}
