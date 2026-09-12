using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One latency implementation behind the seams of the frame (plan section
/// "Latency seams", S2-S8). Exactly one instance is live on a device, reachable
/// as <c>VulkanDevice.Latency</c>; the default is <see cref="NoneLatencyBackend" />.
///
/// The frame, with the call sites of every member:
/// <code>
/// window_RenderFrame                 (lib)
///   if (!LatencyOwnsFrameCap) the client's FPS cap runs   &lt;- OwnsFrameCap
///   LatencySleep()                                        &lt;- Sleep(frameId)
///                                                            Marker(InputSample), Marker(SimulationStart)
///   UpdateMousePosition(); OnNewFrame(dt)
///     first BeginRenderStage(Before)                      &lt;- Marker(SimulationEnd), Marker(RenderSubmitStart)
///   EndFrame() -&gt; VulkanDevice.Present
///     FrameSlot.Submit (A, B, partial)                    &lt;- TagSubmit
///     after Submit A                                      &lt;- Marker(RenderSubmitEnd)
///     around Swapchain.Present                            &lt;- Marker(PresentStart/PresentEnd), OnPresent
///   Swapchain.Build                                       &lt;- OnSwapchainCreated
///   VulkanStats sample                                    &lt;- TakeReports
/// </code>
///
/// Implementations are used from the one client thread for everything except
/// <see cref="TagSubmit" />, which the upload path can reach under the ring's
/// submit lock, and <see cref="TakeReports" />, which the stats sample calls.
/// </summary>
internal interface ILatencyBackend : IDisposable
{
    /// <summary>Which implementation this is; goes into the "device up" log line and the stats.</summary>
    LatencyBackendKind Kind { get; }

    /// <summary>The settings last handed to <see cref="Apply" />.</summary>
    LatencySettings Settings { get; }

    /// <summary>
    /// Sets mode and frame cap. Called once when the device comes up, again
    /// whenever the client's setting changes, and again from
    /// <see cref="OnSwapchainCreated" />, because a new swapchain drops the
    /// driver's sleep mode.
    /// </summary>
    void Apply(in LatencySettings settings);

    /// <summary>
    /// True when this backend paces the frame itself, so the client's own FPS
    /// limiter must stand down (lib seam S3: the cap block in
    /// <c>window_RenderFrame</c> runs only when this is false). False for
    /// <see cref="NoneLatencyBackend" /> and for any backend whose mode is Off.
    /// </summary>
    bool OwnsFrameCap { get; }

    /// <summary>
    /// The frame's one sleep, called immediately before input is sampled, exactly
    /// once per frame. <paramref name="frameId" /> is the latency frame id
    /// allocated for this frame (seam S2).
    /// </summary>
    /// <returns>Microseconds actually waited; 0 when the backend did not sleep.</returns>
    ulong Sleep(ulong frameId);

    /// <summary>
    /// Stamps one phase marker of the frame. Called from the sites listed on this
    /// interface and nowhere else: every marker has exactly one owner, so no
    /// phase is ever stamped twice.
    /// </summary>
    void Marker(ulong frameId, LatencyMarker marker);

    /// <summary>
    /// A new swapchain exists (resize, vsync toggle, OUT_OF_DATE, present-mode
    /// promotion). Called from <c>Swapchain.Build</c> after the slot is live, so
    /// the backend can re-apply its sleep mode to the new handle.
    /// </summary>
    void OnSwapchainCreated(SwapchainKHR swapchain);

    /// <summary>
    /// The swapchain the backend was last told about has been retired (a rebuild
    /// passed it as oldSwapchain) or destroyed, and nothing must be called
    /// against that handle any more. Called from <c>Swapchain.Build</c> the
    /// moment the old slot is handed to the retirement queue - which happens even
    /// when the creation that replaces it fails, so the frames that keep running
    /// on a chain that could not be rebuilt make no vendor call at all - and from
    /// <c>Swapchain.Dispose</c>.
    ///
    /// A backend with no per-swapchain state does nothing; NV drops the handle,
    /// so its sleep, its markers and its timing query all stand down until the
    /// next <see cref="OnSwapchainCreated" />.
    /// </summary>
    void OnSwapchainRetired();

    /// <summary>
    /// Offers a pNext struct for one <c>vkQueueSubmit</c> of the frame (NV's
    /// <c>VkLatencySubmissionPresentIdNV</c> at extension revision 3 and up,
    /// where tagging is all-or-nothing across a frame's submits).
    ///
    /// Called from the shared <c>FrameSlot.Submit</c>, which Submit A, Submit B
    /// and SubmitPartial all pass through, with the chain the caller has already
    /// built; the return value becomes <c>SubmitInfo.PNext</c>. A backend that
    /// has nothing to add returns <paramref name="pNext" /> unchanged - which is
    /// why the caller needs to know nothing about the backend.
    ///
    /// Whatever is returned must stay valid until that submit has been made; a
    /// backend storing the struct keeps it in stable native memory, one slot per
    /// frame in flight.
    /// </summary>
    unsafe void* TagSubmit(ulong frameId, void* pNext);

    /// <summary>
    /// The frame has been presented: <paramref name="presentId" /> is the value
    /// chained as <c>VkPresentIdKHR</c> (0 when present ids are off). Called
    /// right after <c>vkQueuePresentKHR</c> returns, after the PresentEnd marker,
    /// and is where a CPU-timestamp backend closes the frame's report.
    /// </summary>
    void OnPresent(ulong frameId, ulong presentId);

    /// <summary>
    /// The reports finished since the last call, oldest first, and clears them.
    /// Called by the stats sample (seam S7); an empty array when nothing closed.
    /// </summary>
    LatencyFrameReport[] TakeReports();
}
