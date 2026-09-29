using Vintagestory.API.Common;

namespace Vintagestory.Client.NoObf;

/// <summary>
/// Carries the GTAO thin-occluder class through the chunk mesh's existing
/// colour-map integer. Bit 15 is a season offset for wind-mode geometry;
/// non-wind geometry does not read it for colour mapping. Clear any inherited
/// foliage offset on non-wind vertices before assigning the AO class. The chunk shader
/// reads it only for non-wind vertices while Optimum AO is active.
/// </summary>
internal static class OptimumAoClass
{
    private const int ThinBit = 1 << 15;

    /// <summary>
    /// Blocks with non-wind JSON or cube geometry can opt into the thin class
    /// with an <c>optimumAoThin</c> boolean block attribute. Wind geometry is
    /// already classified by its vertex flags and keeps the season-offset bit.
    /// </summary>
    internal static int Pack(int colorMapData, Block block)
    {
        if ((block.VertexFlags.All & VertexFlags.WindModeBitsMask) != 0) return colorMapData;
        int cleanColorMapData = colorMapData & ~ThinBit;
        return block.Attributes?["optimumAoThin"]?.AsBool(false) == true
            ? cleanColorMapData | ThinBit : cleanColorMapData;
    }

    /// <summary>Cross quads are thin even in a block that also draws a solid snow layer.</summary>
    internal static int PackCross(int colorMapData, Block block)
    {
        return (block.VertexFlags.All & VertexFlags.WindModeBitsMask) == 0
            ? colorMapData | ThinBit : colorMapData;
    }
}
