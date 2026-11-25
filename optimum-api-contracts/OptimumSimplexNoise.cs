using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vintagestory.API.Config;

/// <summary>
/// High-throughput, bit-exact 3D BCC simplex noise evaluation backing
/// <see cref="Vintagestory.API.MathTools.NewSimplexNoiseLayer"/> and
/// <see cref="OptimumColumnNoiseBuffer"/>.
/// </summary>
public static class OptimumSimplexNoise
{
    private const long PrimeX = 0x5205402B9270C86FL;
    private const long PrimeY = 0x598CD327003817B5L;
    private const long PrimeZ = 0x5BCC226E9FA0BACBL;
    private const long HashMultiplier = 0x53A3F72DEEC546F5L;
    private const long SeedFlip3D = -0x52D547B2E96ED629L;

    internal const double Root3Over3 = 0.577350269189626;
    private const double FallbackRotate3 = 2.0 / 3.0;
    internal const double Rotate3Orthogonalizer = -0.211324865405187;

    private const int NGrads3DExponent = 8;
    private const int NGrads3D = 1 << NGrads3DExponent;
    private const double Normalizer3D = 0.2781926117527186;

    private static readonly float[] Gradients3D = CreateGradients3D();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EvaluateImprovedXZ(long seed, double x, double y, double z)
    {
        double xz = x + z;
        double s2 = xz * Rotate3Orthogonalizer;
        double yy = y * Root3Over3;
        double xr = x + s2 + yy;
        double zr = z + s2 + yy;
        double yr = xz * -Root3Over3 + yy;

        return Noise3UnrotatedBase(seed, xr, yr, zr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EvaluatePreRotated(long seed, double xrBase, double yrBase, double zrBase, double y)
    {
        double yy = y * Root3Over3;
        return Noise3UnrotatedBase(seed, xrBase + yy, yrBase + yy, zrBase + yy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EvaluateFallbackOrientation(long seed, double x, double y, double z)
    {
        double r = FallbackRotate3 * (x + y + z);
        return Noise3UnrotatedBase(seed, x - r, y - r, z - r);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Noise3UnrotatedBase(long seed, double xr, double yr, double zr)
    {
        // Hoist the base reference once so all up to 8 Grad calls index without repeated array bounds checks.
        ref float gradients = ref MemoryMarshal.GetArrayDataReference(Gradients3D);

        int xrb = (int)Math.Floor(xr), yrb = (int)Math.Floor(yr), zrb = (int)Math.Floor(zr);
        float xi = (float)(xr - xrb), yi = (float)(yr - yrb), zi = (float)(zr - zrb);

        long xrbp = xrb * PrimeX, yrbp = yrb * PrimeY, zrbp = zrb * PrimeZ;
        long seed2 = seed ^ SeedFlip3D;

        int xNMask = (int)(-0.5f - xi), yNMask = (int)(-0.5f - yi), zNMask = (int)(-0.5f - zi);

        float x0 = xi + xNMask;
        float y0 = yi + yNMask;
        float z0 = zi + zNMask;
        float a0 = 0.75f - x0 * x0 - y0 * y0 - z0 * z0;
        long hash0 = HashPrimes(seed, xrbp + (xNMask & PrimeX), yrbp + (yNMask & PrimeY), zrbp + (zNMask & PrimeZ));
        float value = (a0 * a0) * (a0 * a0) * Grad(ref gradients, hash0, x0, y0, z0);

        float x1 = xi - 0.5f;
        float y1 = yi - 0.5f;
        float z1 = zi - 0.5f;
        float a1 = 0.75f - x1 * x1 - y1 * y1 - z1 * z1;
        value += (a1 * a1) * (a1 * a1) * Grad(
            ref gradients,
            HashPrimes(seed2, xrbp + PrimeX, yrbp + PrimeY, zrbp + PrimeZ),
            x1, y1, z1);

        float xAFlipMask0 = ((xNMask | 1) << 1) * x1;
        float yAFlipMask0 = ((yNMask | 1) << 1) * y1;
        float zAFlipMask0 = ((zNMask | 1) << 1) * z1;
        float xAFlipMask1 = (-2 - (xNMask << 2)) * x1 - 1.0f;
        float yAFlipMask1 = (-2 - (yNMask << 2)) * y1 - 1.0f;
        float zAFlipMask1 = (-2 - (zNMask << 2)) * z1 - 1.0f;

        bool skip5 = false;
        float a2 = xAFlipMask0 + a0;
        if (a2 > 0)
        {
            value += (a2 * a2) * (a2 * a2) * Grad(
                ref gradients,
                HashPrimes(seed, xrbp + (~xNMask & PrimeX), yrbp + (yNMask & PrimeY), zrbp + (zNMask & PrimeZ)),
                x0 - (xNMask | 1),
                y0,
                z0);
        }
        else
        {
            float a3 = yAFlipMask0 + zAFlipMask0 + a0;
            if (a3 > 0)
            {
                value += (a3 * a3) * (a3 * a3) * Grad(
                    ref gradients,
                    HashPrimes(seed, xrbp + (xNMask & PrimeX), yrbp + (~yNMask & PrimeY), zrbp + (~zNMask & PrimeZ)),
                    x0,
                    y0 - (yNMask | 1),
                    z0 - (zNMask | 1));
            }

            float a4 = xAFlipMask1 + a1;
            if (a4 > 0)
            {
                value += (a4 * a4) * (a4 * a4) * Grad(
                    ref gradients,
                    HashPrimes(seed2, xrbp + (xNMask & unchecked(PrimeX * 2)), yrbp + PrimeY, zrbp + PrimeZ),
                    (xNMask | 1) + x1,
                    y1,
                    z1);
                skip5 = true;
            }
        }

        bool skip9 = false;
        float a6 = yAFlipMask0 + a0;
        if (a6 > 0)
        {
            value += (a6 * a6) * (a6 * a6) * Grad(
                ref gradients,
                HashPrimes(seed, xrbp + (xNMask & PrimeX), yrbp + (~yNMask & PrimeY), zrbp + (zNMask & PrimeZ)),
                x0,
                y0 - (yNMask | 1),
                z0);
        }
        else
        {
            float a7 = xAFlipMask0 + zAFlipMask0 + a0;
            if (a7 > 0)
            {
                value += (a7 * a7) * (a7 * a7) * Grad(
                    ref gradients,
                    HashPrimes(seed, xrbp + (~xNMask & PrimeX), yrbp + (yNMask & PrimeY), zrbp + (~zNMask & PrimeZ)),
                    x0 - (xNMask | 1),
                    y0,
                    z0 - (zNMask | 1));
            }

            float a8 = yAFlipMask1 + a1;
            if (a8 > 0)
            {
                value += (a8 * a8) * (a8 * a8) * Grad(
                    ref gradients,
                    HashPrimes(seed2, xrbp + PrimeX, yrbp + (yNMask & (PrimeY << 1)), zrbp + PrimeZ),
                    x1,
                    (yNMask | 1) + y1,
                    z1);
                skip9 = true;
            }
        }

        bool skipD = false;
        float aA = zAFlipMask0 + a0;
        if (aA > 0)
        {
            value += (aA * aA) * (aA * aA) * Grad(
                ref gradients,
                HashPrimes(seed, xrbp + (xNMask & PrimeX), yrbp + (yNMask & PrimeY), zrbp + (~zNMask & PrimeZ)),
                x0,
                y0,
                z0 - (zNMask | 1));
        }
        else
        {
            float aB = xAFlipMask0 + yAFlipMask0 + a0;
            if (aB > 0)
            {
                value += (aB * aB) * (aB * aB) * Grad(
                    ref gradients,
                    HashPrimes(seed, xrbp + (~xNMask & PrimeX), yrbp + (~yNMask & PrimeY), zrbp + (zNMask & PrimeZ)),
                    x0 - (xNMask | 1),
                    y0 - (yNMask | 1),
                    z0);
            }

            float aC = zAFlipMask1 + a1;
            if (aC > 0)
            {
                value += (aC * aC) * (aC * aC) * Grad(
                    ref gradients,
                    HashPrimes(seed2, xrbp + PrimeX, yrbp + PrimeY, zrbp + (zNMask & (PrimeZ << 1))),
                    x1,
                    y1,
                    (zNMask | 1) + z1);
                skipD = true;
            }
        }

        if (!skip5)
        {
            float a5 = yAFlipMask1 + zAFlipMask1 + a1;
            if (a5 > 0)
            {
                value += (a5 * a5) * (a5 * a5) * Grad(
                    ref gradients,
                    HashPrimes(seed2, xrbp + PrimeX, yrbp + (yNMask & (PrimeY << 1)), zrbp + (zNMask & (PrimeZ << 1))),
                    x1,
                    (yNMask | 1) + y1,
                    (zNMask | 1) + z1);
            }
        }

        if (!skip9)
        {
            float a9 = xAFlipMask1 + zAFlipMask1 + a1;
            if (a9 > 0)
            {
                value += (a9 * a9) * (a9 * a9) * Grad(
                    ref gradients,
                    HashPrimes(seed2, xrbp + (xNMask & unchecked(PrimeX * 2)), yrbp + PrimeY, zrbp + (zNMask & (PrimeZ << 1))),
                    (xNMask | 1) + x1,
                    y1,
                    (zNMask | 1) + z1);
            }
        }

        if (!skipD)
        {
            float aD = xAFlipMask1 + yAFlipMask1 + a1;
            if (aD > 0)
            {
                value += (aD * aD) * (aD * aD) * Grad(
                    ref gradients,
                    HashPrimes(seed2, xrbp + (xNMask & (PrimeX << 1)), yrbp + (yNMask & (PrimeY << 1)), zrbp + PrimeZ),
                    (xNMask | 1) + x1,
                    (yNMask | 1) + y1,
                    z1);
            }
        }

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long HashPrimes(long seed, long xsvp, long ysvp, long zsvp)
    {
        return seed ^ xsvp ^ ysvp ^ zsvp;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grad(ref float gradients, long hash, float dx, float dy, float dz)
    {
        hash *= HashMultiplier;
        hash ^= hash >> (64 - NGrads3DExponent + 2);
        // gi is masked to a 4-float aligned slot in [0, 1020], so gi + 2 <= 1022 < Gradients3D.Length (1024).
        int gi = (int)hash & ((NGrads3D - 1) << 2);
        return Unsafe.Add(ref gradients, gi) * dx
            + Unsafe.Add(ref gradients, gi + 1) * dy
            + Unsafe.Add(ref gradients, gi + 2) * dz;
    }

    private static float[] CreateGradients3D()
    {
        float[] gradients = new float[NGrads3D * 4];
        float[] grad3 =
        {
             2.22474487139f,       2.22474487139f,      -1.0f,                 0.0f,
             2.22474487139f,       2.22474487139f,       1.0f,                 0.0f,
             3.0862664687972017f,  1.1721513422464978f,  0.0f,                 0.0f,
             1.1721513422464978f,  3.0862664687972017f,  0.0f,                 0.0f,
            -2.22474487139f,       2.22474487139f,      -1.0f,                 0.0f,
            -2.22474487139f,       2.22474487139f,       1.0f,                 0.0f,
            -1.1721513422464978f,  3.0862664687972017f,  0.0f,                 0.0f,
            -3.0862664687972017f,  1.1721513422464978f,  0.0f,                 0.0f,
            -1.0f,                -2.22474487139f,      -2.22474487139f,       0.0f,
             1.0f,                -2.22474487139f,      -2.22474487139f,       0.0f,
             0.0f,                -3.0862664687972017f, -1.1721513422464978f,  0.0f,
             0.0f,                -1.1721513422464978f, -3.0862664687972017f,  0.0f,
            -1.0f,                -2.22474487139f,       2.22474487139f,       0.0f,
             1.0f,                -2.22474487139f,       2.22474487139f,       0.0f,
             0.0f,                -1.1721513422464978f,  3.0862664687972017f,  0.0f,
             0.0f,                -3.0862664687972017f,  1.1721513422464978f,  0.0f,
            //--------------------------------------------------------------------//
            -2.22474487139f,      -2.22474487139f,      -1.0f,                 0.0f,
            -2.22474487139f,      -2.22474487139f,       1.0f,                 0.0f,
            -3.0862664687972017f, -1.1721513422464978f,  0.0f,                 0.0f,
            -1.1721513422464978f, -3.0862664687972017f,  0.0f,                 0.0f,
            -2.22474487139f,      -1.0f,                -2.22474487139f,       0.0f,
            -2.22474487139f,       1.0f,                -2.22474487139f,       0.0f,
            -1.1721513422464978f,  0.0f,                -3.0862664687972017f,  0.0f,
            -3.0862664687972017f,  0.0f,                -1.1721513422464978f,  0.0f,
            -2.22474487139f,      -1.0f,                 2.22474487139f,       0.0f,
            -2.22474487139f,       1.0f,                 2.22474487139f,       0.0f,
            -3.0862664687972017f,  0.0f,                 1.1721513422464978f,  0.0f,
            -1.1721513422464978f,  0.0f,                 3.0862664687972017f,  0.0f,
            -1.0f,                 2.22474487139f,      -2.22474487139f,       0.0f,
             1.0f,                 2.22474487139f,      -2.22474487139f,       0.0f,
             0.0f,                 1.1721513422464978f, -3.0862664687972017f,  0.0f,
             0.0f,                 3.0862664687972017f, -1.1721513422464978f,  0.0f,
            -1.0f,                 2.22474487139f,       2.22474487139f,       0.0f,
             1.0f,                 2.22474487139f,       2.22474487139f,       0.0f,
             0.0f,                 3.0862664687972017f,  1.1721513422464978f,  0.0f,
             0.0f,                 1.1721513422464978f,  3.0862664687972017f,  0.0f,
             2.22474487139f,      -2.22474487139f,      -1.0f,                 0.0f,
             2.22474487139f,      -2.22474487139f,       1.0f,                 0.0f,
             1.1721513422464978f, -3.0862664687972017f,  0.0f,                 0.0f,
             3.0862664687972017f, -1.1721513422464978f,  0.0f,                 0.0f,
             2.22474487139f,      -1.0f,                -2.22474487139f,       0.0f,
             2.22474487139f,       1.0f,                -2.22474487139f,       0.0f,
             3.0862664687972017f,  0.0f,                -1.1721513422464978f,  0.0f,
             1.1721513422464978f,  0.0f,                -3.0862664687972017f,  0.0f,
             2.22474487139f,      -1.0f,                 2.22474487139f,       0.0f,
             2.22474487139f,       1.0f,                 2.22474487139f,       0.0f,
             1.1721513422464978f,  0.0f,                 3.0862664687972017f,  0.0f,
             3.0862664687972017f,  0.0f,                 1.1721513422464978f,  0.0f,
        };
        for (int i = 0; i < grad3.Length; i++)
        {
            grad3[i] = (float)(grad3[i] / Normalizer3D);
        }
        for (int i = 0, j = 0; i < gradients.Length; i++, j++)
        {
            if (j == grad3.Length) j = 0;
            gradients[i] = grad3[j];
        }
        return gradients;
    }
}
