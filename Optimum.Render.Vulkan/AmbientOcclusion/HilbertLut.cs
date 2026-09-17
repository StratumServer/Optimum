namespace Optimum.Render.Vulkan.AmbientOcclusion;

/// <summary>
/// The 64x64 Hilbert index table the AO noise starts from (docs/research/ambient-occlusion.md
/// C.7): generated at startup, never shipped. Neighbouring texels get neighbouring indices,
/// so the R2 sequence the index drives is low-discrepancy across the 3x3 denoise
/// footprint. XeGTAO's <c>HilbertIndex</c> (vaGTAO.hlsl, MIT, Intel), level 6.
/// </summary>
internal static class HilbertLut
{
    public const int Width = 64;

    /// <summary>The curve index of texel (<paramref name="x" />, <paramref name="y" />), 0..4095.</summary>
    public static uint Index(uint x, uint y)
    {
        uint index = 0;
        for (uint level = Width / 2; level > 0; level /= 2)
        {
            uint regionX = (x & level) > 0 ? 1u : 0u;
            uint regionY = (y & level) > 0 ? 1u : 0u;
            index += level * level * ((3u * regionX) ^ regionY);
            if (regionY == 0)
            {
                if (regionX == 1)
                {
                    x = Width - 1 - x;
                    y = Width - 1 - y;
                }
                (x, y) = (y, x);
            }
        }
        return index;
    }

    /// <summary>The table in row order as floats, for an R32F texture: every index is exact in a float.</summary>
    public static float[] Build()
    {
        var table = new float[Width * Width];
        for (uint y = 0; y < Width; y++)
        for (uint x = 0; x < Width; x++)
            table[y * Width + x] = Index(x, y);
        return table;
    }
}
