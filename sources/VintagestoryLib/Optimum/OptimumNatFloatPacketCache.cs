using Vintagestory.API.Config;

namespace Optimum;

/// <summary>
/// Pre-sized immutable packet instances for the two common sound <c>NatFloat</c>
/// defaults so <see cref="Vintagestory.Common.CollectibleNet.ToPacket(Vintagestory.API.MathTools.NatFloat)"/>
/// avoids repeated allocations during server asset synchronization.
/// </summary>
internal static class OptimumNatFloatPacketCache
{
    internal static readonly Packet_NatFloat NatFloatOnePacket;
    internal static readonly Packet_NatFloat RandomPitchPacket;

    static OptimumNatFloatPacketCache()
    {
        NatFloatOnePacket = new Packet_NatFloat
        {
            Avg = 1024,
            Var = 0,
            Dist = 0
        };
        Packet_NatFloatSerializer.GetSize(NatFloatOnePacket);

        RandomPitchPacket = new Packet_NatFloat
        {
            Avg = 1024,
            Var = 256,
            Dist = 0
        };
        Packet_NatFloatSerializer.GetSize(RandomPitchPacket);
    }

    internal static bool TryGetPacket(int avg, int var, int dist, out Packet_NatFloat cached)
    {
        if (OptimumConfig.ReuseCommonNatFloatEnabled && dist == 0 && avg == 1024)
        {
            if (var == 0)
            {
                OptimumDiagnostics.RecordPacketNatFloatReuse();
                cached = NatFloatOnePacket;
                return true;
            }
            if (var == 256)
            {
                OptimumDiagnostics.RecordPacketNatFloatReuse();
                cached = RandomPitchPacket;
                return true;
            }
        }

        cached = null;
        return false;
    }
}
