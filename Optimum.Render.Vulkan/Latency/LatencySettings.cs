namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// How hard the active latency backend works, mirroring the persisted
/// <c>LatencyMode</c> setting (off|on|boost) in <c>OptimumConfig</c>.
/// </summary>
internal enum LatencyMode
{
    /// <summary>No sleeping and no markers beyond the CPU timestamps the reports need.</summary>
    Off = 0,

    /// <summary>The backend paces the frame (NV: lowLatencyMode; AMD: anti-lag on; Native: completion pacing).</summary>
    On = 1,

    /// <summary>
    /// As <see cref="On" />, plus the vendor's clock boost while the CPU is the
    /// bottleneck (NV: lowLatencyBoost). Backends without a boost treat it as
    /// <see cref="On" />.
    /// </summary>
    Boost = 2,
}

/// <summary>
/// What the client asks of the latency backend: one mode and one frame cap.
///
/// The cap is named after <c>VkLatencySleepModeInfoNV.minimumIntervalUs</c> - the
/// minimum interval between two frame starts - because every backend expresses
/// the same thing: NV passes it through, AMD converts it to <c>maxFPS</c>, the
/// Native backend paces release to release, and XeLL later takes it in
/// <c>xellSetSleepMode</c>. One cap for every backend, so the client's own FPS
/// limiter can stand down whenever <c>ILatencyBackend.OwnsFrameCap</c> is true.
/// </summary>
/// <param name="Mode">Off, On or Boost.</param>
/// <param name="MinimumIntervalUs">
/// Minimum microseconds between two frame starts; 0 is uncapped. 60 fps is 16666.
/// </param>
internal readonly record struct LatencySettings(LatencyMode Mode, ulong MinimumIntervalUs)
{
    /// <summary>The default: no latency work at all.</summary>
    public static LatencySettings Disabled => new(LatencyMode.Off, 0);

    /// <summary>True when the backend should sleep and stamp markers.</summary>
    public bool Enabled => Mode != LatencyMode.Off;

    /// <summary>True when the vendor's clock boost is asked for.</summary>
    public bool Boost => Mode == LatencyMode.Boost;

    /// <summary>0 when uncapped, else the cap expressed as frames per second (rounded down).</summary>
    public uint MaxFps => MinimumIntervalUs == 0 ? 0u : (uint)(1_000_000UL / MinimumIntervalUs);

    /// <summary>The cap that gives at most <paramref name="fps" /> frames a second; 0 is uncapped.</summary>
    public static ulong IntervalUsForFps(double fps) =>
        fps <= 0 ? 0UL : (ulong)(1_000_000.0 / fps);
}
