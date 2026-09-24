namespace Komet.Runtime;

/// <summary>
/// Test-only stand-in for Komet's public runtime switch. Komet 1.2.0
/// exposes this exact field and Optimum reads it by reflection.
/// </summary>
public static class InflowBrake
{
    public static bool Enabled;
}
