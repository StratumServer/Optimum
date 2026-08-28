using System;
using System.Runtime.InteropServices;

namespace Optimum.Installer.Views;

/// <summary>
/// Reads the operating system "reduce / turn off animations" preference so the
/// wizard can skip its step transition. Windows exposes it through
/// <c>SPI_GETCLIENTAREAANIMATION</c>; other platforms report motion allowed.
/// </summary>
public static class MotionSettings
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref int value, uint update);

    public static bool ReduceMotion { get; } = Detect();

    public static bool AllowMotion => !ReduceMotion;

    private static bool Detect()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            int enabled = 1;
            if (SystemParametersInfoW(SpiGetClientAreaAnimation, 0, ref enabled, 0))
                return enabled == 0;
        }
        catch (Exception)
        {
            // Best effort: assume motion is fine if the query fails.
        }
        return false;
    }
}
