using System;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Pins <c>OptimumConfig.LatencyMode</c> for the length of a test and restores it.
///
/// The setting ships on since 2026-09-12, so the "off is off" tests - the ones that
/// assert a device paces nothing and owns no frame cap - have to ask for off rather
/// than lean on the default. The device reads the setting once, at Initialize, so the
/// scope has to be entered before the device is created. Test parallelisation is off
/// for this assembly (AssemblyInfo), so a static is safe to pin here.
/// </summary>
internal sealed class LatencyModeScope : IDisposable
{
    private readonly string _previous;

    private LatencyModeScope(string mode)
    {
        _previous = OptimumConfig.LatencyMode;
        OptimumConfig.LatencyMode = mode;
    }

    /// <summary>The shipped default, asserted where a test depends on it.</summary>
    public const string ShippedDefault = "on";

    public static LatencyModeScope Off() => new("off");

    public static LatencyModeScope Of(string mode) => new(mode);

    public void Dispose() => OptimumConfig.LatencyMode = _previous;
}
