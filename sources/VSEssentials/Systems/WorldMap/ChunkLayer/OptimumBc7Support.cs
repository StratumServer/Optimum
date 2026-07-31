using System;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Vintagestory.GameContent;

/// <summary>
/// BC7 (BPTC) texture compression for map pages. Checks GPU capability at
/// startup and provides encode/upload paths when supported. Falls back to
/// uncompressed RGBA8 upload when the extension is absent.
///
/// BC7 cuts VRAM by 4x (256x256 RGBA8 = 256KB per page; BC7 = 64KB) and
/// proportionally reduces upload bandwidth.
///
/// The encoder runs on a background thread. The upload path uses
/// GL.CompressedTexSubImage3D instead of GL.TexSubImage3D.
/// </summary>
public static class OptimumBc7Support
{
    private const int GL_COMPRESSED_RGBA_BPTC_UNORM = 36492;
    private const int GL_TEXTURE_2D_ARRAY = 35866;
    private const int GL_NUM_EXTENSIONS = 33309;

    /// <summary>
    /// Detect GL_ARB_texture_compression_bptc at startup. Call once from
    /// the render thread after GL context creation.
    /// </summary>
    public static void DetectSupport()
    {
        bool supported = false;
        try
        {
            int extCount = GL.GetInteger((GetPName)GL_NUM_EXTENSIONS);
            for (int i = 0; i < extCount; i++)
            {
                string ext = GL.GetString((StringNameIndexed)7939, i); // GL_EXTENSIONS indexed
                if (ext == "GL_ARB_texture_compression_bptc")
                {
                    supported = true;
                    break;
                }
            }
        }
        catch
        {
            // GetStringi not available on ancient drivers: assume no support
            supported = false;
        }

        OptimumConfig.MapPageCacheBc7Supported = supported;
    }

    /// <summary>
    /// Encode RGBA8 pixels (int[65536]) to BC7 compressed block data.
    /// Returns the compressed byte array (64KB for a 256x256 page).
    ///
    /// BC7 compressed size for a 256x256 texture: (256/4)*(256/4)*16 = 65536 bytes.
    /// Each 4x4 block compresses to 16 bytes.
    /// </summary>
    public static byte[]? Encode(int[] rgbaPixels, int width, int height)
    {
        if (!OptimumConfig.MapPageCacheBc7 || !OptimumConfig.MapPageCacheBc7Supported)
            return null;

        if (rgbaPixels == null || rgbaPixels.Length != width * height)
            return null;

        // BC7 block dimensions
        int blocksX = width / 4;
        int blocksY = height / 4;
        int compressedSize = blocksX * blocksY * 16; // 16 bytes per block
        byte[] compressed = new byte[compressedSize];

        // Encode each 4x4 block
        byte[] blockPixels = new byte[64]; // 4x4 pixels * 4 bytes
        int outputOffset = 0;

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                // Extract 4x4 block pixels
                for (int row = 0; row < 4; row++)
                {
                    int srcY = by * 4 + row;
                    for (int col = 0; col < 4; col++)
                    {
                        int srcX = bx * 4 + col;
                        int pixel = rgbaPixels[srcY * width + srcX];
                        int pixelOffset = (row * 4 + col) * 4;
                        blockPixels[pixelOffset + 0] = (byte)(pixel & 0xFF);         // R
                        blockPixels[pixelOffset + 1] = (byte)((pixel >> 8) & 0xFF);  // G
                        blockPixels[pixelOffset + 2] = (byte)((pixel >> 16) & 0xFF); // B
                        blockPixels[pixelOffset + 3] = (byte)((pixel >> 24) & 0xFF); // A
                    }
                }

                // Encode the block using BC7 Mode 6 (simple, fast, good for map data)
                EncodeBlockMode6(blockPixels, compressed, outputOffset);
                outputOffset += 16;
            }
        }

        return compressed;
    }

    /// <summary>
    /// Upload BC7-compressed data to a specific layer of a texture array.
    /// Call from the render thread.
    /// </summary>
    public static void UploadCompressedLayer(int textureId, int layer, byte[] compressedData, int width, int height)
    {
        GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, textureId);
        unsafe
        {
            fixed (byte* ptr = compressedData)
            {
                GL.CompressedTexSubImage3D(
                    (TextureTarget)GL_TEXTURE_2D_ARRAY,
                    0,          // mip level
                    0, 0,       // x, y offset
                    layer,      // z offset = layer
                    width,
                    height,
                    1,          // depth = 1 layer
                    (InternalFormat)GL_COMPRESSED_RGBA_BPTC_UNORM,
                    compressedData.Length,
                    (IntPtr)ptr
                );
            }
        }
    }

    /// <summary>
    /// Encode a 4x4 block using BC7 Mode 6. This mode uses 1 subset with
    /// 7-bit per-component endpoints and 4-bit indices (16 values per pixel).
    ///
    /// Mode 6: 1 bit mode, 0 partition bits, 0 rotation, 7+7 color bits (endpoints),
    /// 7+7 alpha bits, 1 p-bit per endpoint, 4 index bits per texel.
    /// Total: 1 + 0 + 0 + 14*4 + 2 + 64 = 128 bits = 16 bytes.
    ///
    /// The encoder uses a variance-weighted principal axis for index assignment:
    /// channels with more spread in the block dominate the projection, which
    /// produces better color fidelity than uniform weighting on smooth map terrain.
    /// </summary>
    private static void EncodeBlockMode6(byte[] blockPixels, byte[] output, int outputOffset)
    {
        // Compute per-channel mean
        int sumR = 0, sumG = 0, sumB = 0, sumA = 0;
        for (int i = 0; i < 64; i += 4)
        {
            sumR += blockPixels[i];
            sumG += blockPixels[i + 1];
            sumB += blockPixels[i + 2];
            sumA += blockPixels[i + 3];
        }
        float meanR = sumR / 16f;
        float meanG = sumG / 16f;
        float meanB = sumB / 16f;
        float meanA = sumA / 16f;

        // Compute per-channel variance and covariance with the dominant axis
        float varR = 0, varG = 0, varB = 0, varA = 0;
        for (int i = 0; i < 64; i += 4)
        {
            float dr = blockPixels[i] - meanR;
            float dg = blockPixels[i + 1] - meanG;
            float db = blockPixels[i + 2] - meanB;
            float da = blockPixels[i + 3] - meanA;
            varR += dr * dr;
            varG += dg * dg;
            varB += db * db;
            varA += da * da;
        }

        // Build the projection axis from variance (pseudo-PCA: use variance
        // as weights for the direction vector). This gives better results than
        // uniform weighting when one channel varies more than others.
        float totalVar = varR + varG + varB + varA;
        float wR, wG, wB, wA;
        if (totalVar < 1f)
        {
            // Constant block: all indices will be 0
            wR = wG = wB = wA = 0.25f;
        }
        else
        {
            wR = varR / totalVar;
            wG = varG / totalVar;
            wB = varB / totalVar;
            wA = varA / totalVar;
        }

        // Find min/max projection along the weighted axis
        float minProj = float.MaxValue, maxProj = float.MinValue;
        int minIdx = 0, maxIdx = 0;
        for (int i = 0; i < 16; i++)
        {
            int off = i * 4;
            float proj = blockPixels[off] * wR + blockPixels[off + 1] * wG
                       + blockPixels[off + 2] * wB + blockPixels[off + 3] * wA;
            if (proj < minProj) { minProj = proj; minIdx = i; }
            if (proj > maxProj) { maxProj = proj; maxIdx = i; }
        }

        // Use the actual pixel values at min/max projection as endpoints
        int e0r = blockPixels[minIdx * 4];
        int e0g = blockPixels[minIdx * 4 + 1];
        int e0b = blockPixels[minIdx * 4 + 2];
        int e0a = blockPixels[minIdx * 4 + 3];
        int e1r = blockPixels[maxIdx * 4];
        int e1g = blockPixels[maxIdx * 4 + 1];
        int e1b = blockPixels[maxIdx * 4 + 2];
        int e1a = blockPixels[maxIdx * 4 + 3];

        // Quantize endpoints to 7 bits (Mode 6 precision)
        int q0r = (e0r * 127 + 127) / 255;
        int q0g = (e0g * 127 + 127) / 255;
        int q0b = (e0b * 127 + 127) / 255;
        int q0a = (e0a * 127 + 127) / 255;
        int q1r = (e1r * 127 + 127) / 255;
        int q1g = (e1g * 127 + 127) / 255;
        int q1b = (e1b * 127 + 127) / 255;
        int q1a = (e1a * 127 + 127) / 255;

        // Compute indices by projecting each pixel along the endpoint line
        float projRange = maxProj - minProj;
        Span<byte> indices = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int off = i * 4;
            float proj = blockPixels[off] * wR + blockPixels[off + 1] * wG
                       + blockPixels[off + 2] * wB + blockPixels[off + 3] * wA;

            if (projRange < 0.001f)
            {
                indices[i] = 0;
            }
            else
            {
                float t = (proj - minProj) / projRange;
                indices[i] = (byte)Math.Clamp((int)(t * 15f + 0.5f), 0, 15);
            }
        }

        // BC7 anchor constraint: index[0]'s MSB is implicitly 0 (only 3 bits stored).
        // If index[0] > 7, swap endpoints and invert all indices so the anchor
        // projects to the low end of the interpolation range.
        if (indices[0] > 7)
        {
            // Swap endpoints
            (q0r, q1r) = (q1r, q0r);
            (q0g, q1g) = (q1g, q0g);
            (q0b, q1b) = (q1b, q0b);
            (q0a, q1a) = (q1a, q0a);

            // Invert all indices
            for (int i = 0; i < 16; i++)
            {
                indices[i] = (byte)(15 - indices[i]);
            }
        }

        // Pack Mode 6 block (128 bits)
        Array.Clear(output, outputOffset, 16);

        // Mode 6: 7 zero bits then a 1 bit = 0x40
        output[outputOffset] = 0x40;

        // Pack endpoints (7 bits each, RGBA interleaved by color then endpoint)
        PackBits(output, outputOffset, 7, 7, q0r);
        PackBits(output, outputOffset, 14, 7, q1r);
        PackBits(output, outputOffset, 21, 7, q0g);
        PackBits(output, outputOffset, 28, 7, q1g);
        PackBits(output, outputOffset, 35, 7, q0b);
        PackBits(output, outputOffset, 42, 7, q1b);
        PackBits(output, outputOffset, 49, 7, q0a);
        PackBits(output, outputOffset, 56, 7, q1a);
        // p-bits at 63, 64: leave as zero

        // Indices: 4 bits each starting at bit 65 (anchor index is 3 bits)
        for (int i = 0; i < 16; i++)
        {
            if (i == 0)
            {
                // Anchor index: 3 bits (MSB implied 0)
                PackBits(output, outputOffset, 65, 3, indices[i] & 0x7);
            }
            else
            {
                PackBits(output, outputOffset, 65 + 3 + (i - 1) * 4, 4, indices[i]);
            }
        }
    }

    private static void PackBits(byte[] data, int baseOffset, int bitOffset, int numBits, int value)
    {
        for (int i = 0; i < numBits; i++)
        {
            if ((value & (1 << i)) != 0)
            {
                int bitPos = bitOffset + i;
                data[baseOffset + bitPos / 8] |= (byte)(1 << (bitPos % 8));
            }
        }
    }
}
