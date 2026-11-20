namespace Komet.Runtime
{
    /// <summary>
    /// Test-only stand-in for Komet 1.x/2.x's public runtime switch.
    /// </summary>
    public static class InflowBrake
    {
        public static bool Enabled;
    }
}

namespace Komet.Chunks
{
    /// <summary>
    /// Test-only stand-in for Komet 3.x's public join-pacer switch (Komet.Chunks.JoinPacer.Enabled).
    /// </summary>
    public static class JoinPacer
    {
        public static bool Enabled;
    }
}
