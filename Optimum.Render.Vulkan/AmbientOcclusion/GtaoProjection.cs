using System;

namespace Optimum.Render.Vulkan.AmbientOcclusion;

/// <summary>
/// The reconstruction constants of the AO passes from a GL projection
/// (docs/research/xegtao-integration.md, integration plan step 1): view depth from the
/// [0, 1] depth buffer and view XY from the texel's UV, in the working frame x right,
/// y up, z forward (GL view space mirrored in z). The texel rows run bottom-up (GL order;
/// the device never flips), so unlike XeGTAO's D3D constants the Y terms keep their sign.
///
/// The jitter columns of the projection (P[2][0], P[2][1]) are folded into the offset,
/// so a jittered G-buffer reconstructs exactly rather than within a sub-pixel.
/// </summary>
internal readonly record struct GtaoProjection(
    float DepthUnpackMul, float DepthUnpackAdd,
    float NdcToViewMulX, float NdcToViewMulY,
    float NdcToViewAddX, float NdcToViewAddY)
{
    /// <summary>
    /// From a column-major GL perspective matrix (<c>m[10] = A</c>, <c>m[14] = B</c>,
    /// <c>m[11] = -1</c>); null for anything that is not a perspective projection.
    /// </summary>
    public static GtaoProjection? From(float[]? m)
    {
        if (m == null || m.Length < 16) return null;
        if (MathF.Abs(m[11] + 1f) > 1e-4f || MathF.Abs(m[15]) > 1e-4f) return null;
        if (m[0] == 0f || m[5] == 0f || m[14] == 0f) return null;

        float a = m[10];
        float b = m[14];
        float tanX = 1f / m[0];
        float tanY = 1f / m[5];
        return new GtaoProjection(
            -b / 2f, (1f - a) / 2f,
            2f * tanX, 2f * tanY,
            (m[8] - 1f) * tanX, (m[9] - 1f) * tanY);
    }

    /// <summary>View depth (positive, forward) of a [0, 1] depth value; the shader's <c>gtaoViewDepth</c>.</summary>
    public float ViewDepth(float screenDepth) => DepthUnpackMul / (DepthUnpackAdd - screenDepth);

    /// <summary>The working-frame position of a texel UV (row 0 at the bottom) at a view depth; <c>gtaoViewPosition</c>.</summary>
    public (float X, float Y, float Z) ViewPosition(float u, float v, float viewDepth) =>
        ((NdcToViewMulX * u + NdcToViewAddX) * viewDepth, (NdcToViewMulY * v + NdcToViewAddY) * viewDepth, viewDepth);
}
