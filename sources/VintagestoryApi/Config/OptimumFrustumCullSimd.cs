using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #75 Tier 2: SIMD-vectorized frustum culling on CPU.
/// Evaluates bounding plane equations in parallel via Vector256 / Vector128 (AVX2 / ARM NEON)
/// with single-instruction mask extraction, eliminating scalar plane loop branch overhead.
/// </summary>
public static class OptimumFrustumCullSimd
{
    private const float InvSqrt3 = 1f / 1.7320508f;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustum")]
    internal static extern ref Plane[] GetFrustum(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerPos")]
    internal static extern ref BlockPos GetPlayerPos(FrustumCulling culler);

    internal sealed class Context
    {
        public Vector256<float> Nx;
        public Vector256<float> Ny;
        public Vector256<float> Nz;
        public Vector256<float> D;
        public Vector256<float> AbsNx;
        public Vector256<float> AbsNy;
        public Vector256<float> AbsNz;

        public Vector128<float> Nx0, Nx1;
        public Vector128<float> Ny0, Ny1;
        public Vector128<float> Nz0, Nz1;
        public Vector128<float> D0, D1;
        public Vector128<float> AbsNx0, AbsNx1;
        public Vector128<float> AbsNy0, AbsNy1;
        public Vector128<float> AbsNz0, AbsNz1;

        private readonly double[] _planeValues = new double[24];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsUpToDate(Plane[] planes)
        {
            for (int i = 0; i < 6; i++)
            {
                int offset = i * 4;
                if (_planeValues[offset] != planes[i].normalX
                    || _planeValues[offset + 1] != planes[i].normalY
                    || _planeValues[offset + 2] != planes[i].normalZ
                    || _planeValues[offset + 3] != planes[i].D)
                {
                    return false;
                }
            }

            return true;
        }

        public void Update(Plane[] planes)
        {
            for (int i = 0; i < 6; i++)
            {
                int offset = i * 4;
                _planeValues[offset] = planes[i].normalX;
                _planeValues[offset + 1] = planes[i].normalY;
                _planeValues[offset + 2] = planes[i].normalZ;
                _planeValues[offset + 3] = planes[i].D;
            }

            if (Vector256.IsHardwareAccelerated)
            {
                Span<float> nx = stackalloc float[8];
                Span<float> ny = stackalloc float[8];
                Span<float> nz = stackalloc float[8];
                Span<float> d  = stackalloc float[8];
                Span<float> anx = stackalloc float[8];
                Span<float> any = stackalloc float[8];
                Span<float> anz = stackalloc float[8];

                for (int i = 0; i < 6; i++)
                {
                    nx[i] = (float)planes[i].normalX;
                    ny[i] = (float)planes[i].normalY;
                    nz[i] = (float)planes[i].normalZ;
                    d[i]  = (float)planes[i].D;
                    anx[i] = MathF.Abs(nx[i]);
                    any[i] = MathF.Abs(ny[i]);
                    anz[i] = MathF.Abs(nz[i]);
                }
                d[6] = 1e9f;
                d[7] = 1e9f;

                Nx = Vector256.Create(nx);
                Ny = Vector256.Create(ny);
                Nz = Vector256.Create(nz);
                D  = Vector256.Create(d);
                AbsNx = Vector256.Create(anx);
                AbsNy = Vector256.Create(any);
                AbsNz = Vector256.Create(anz);
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                Span<float> nx0 = stackalloc float[4], nx1 = stackalloc float[4];
                Span<float> ny0 = stackalloc float[4], ny1 = stackalloc float[4];
                Span<float> nz0 = stackalloc float[4], nz1 = stackalloc float[4];
                Span<float> d0  = stackalloc float[4], d1  = stackalloc float[4];
                Span<float> anx0 = stackalloc float[4], anx1 = stackalloc float[4];
                Span<float> any0 = stackalloc float[4], any1 = stackalloc float[4];
                Span<float> anz0 = stackalloc float[4], anz1 = stackalloc float[4];

                for (int i = 0; i < 4; i++)
                {
                    nx0[i] = (float)planes[i].normalX;
                    ny0[i] = (float)planes[i].normalY;
                    nz0[i] = (float)planes[i].normalZ;
                    d0[i]  = (float)planes[i].D;
                    anx0[i] = MathF.Abs(nx0[i]);
                    any0[i] = MathF.Abs(ny0[i]);
                    anz0[i] = MathF.Abs(nz0[i]);
                }
                for (int i = 0; i < 2; i++)
                {
                    nx1[i] = (float)planes[i + 4].normalX;
                    ny1[i] = (float)planes[i + 4].normalY;
                    nz1[i] = (float)planes[i + 4].normalZ;
                    d1[i]  = (float)planes[i + 4].D;
                    anx1[i] = MathF.Abs(nx1[i]);
                    any1[i] = MathF.Abs(ny1[i]);
                    anz1[i] = MathF.Abs(nz1[i]);
                }
                d1[2] = 1e9f;
                d1[3] = 1e9f;

                Nx0 = Vector128.Create(nx0); Nx1 = Vector128.Create(nx1);
                Ny0 = Vector128.Create(ny0); Ny1 = Vector128.Create(ny1);
                Nz0 = Vector128.Create(nz0); Nz1 = Vector128.Create(nz1);
                D0  = Vector128.Create(d0);  D1  = Vector128.Create(d1);
                AbsNx0 = Vector128.Create(anx0); AbsNx1 = Vector128.Create(anx1);
                AbsNy0 = Vector128.Create(any0); AbsNy1 = Vector128.Create(any1);
                AbsNz0 = Vector128.Create(anz0); AbsNz1 = Vector128.Create(anz1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsOutside5(float sx, float sy, float sz, float rx, float ry, float rz)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                var vSx = Vector256.Create(sx);
                var vSy = Vector256.Create(sy);
                var vSz = Vector256.Create(sz);
                var vRx = Vector256.Create(rx * InvSqrt3);
                var vRy = Vector256.Create(ry * InvSqrt3);
                var vRz = Vector256.Create(rz * InvSqrt3);

                Vector256<float> acc = D;
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx, acc) : (vSx * Nx + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny, acc) : (vSy * Ny + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz, acc) : (vSz * Nz + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vRx, AbsNx, acc) : (vRx * AbsNx + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vRy, AbsNy, acc) : (vRy * AbsNy + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vRz, AbsNz, acc) : (vRz * AbsNz + acc);

                Vector256<float> cmp = Vector256.LessThan(acc, Vector256<float>.Zero);
                return (cmp.ExtractMostSignificantBits() & 0x1Fu) != 0;
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var vSx = Vector128.Create(sx);
                var vSy = Vector128.Create(sy);
                var vSz = Vector128.Create(sz);
                var vRx = Vector128.Create(rx * InvSqrt3);
                var vRy = Vector128.Create(ry * InvSqrt3);
                var vRz = Vector128.Create(rz * InvSqrt3);

                Vector128<float> acc0 = D0;
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx0, acc0) : (vSx * Nx0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny0, acc0) : (vSy * Ny0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz0, acc0) : (vSz * Nz0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vRx, AbsNx0, acc0) : (vRx * AbsNx0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vRy, AbsNy0, acc0) : (vRy * AbsNy0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vRz, AbsNz0, acc0) : (vRz * AbsNz0 + acc0);

                Vector128<float> cmp0 = Vector128.LessThan(acc0, Vector128<float>.Zero);
                if ((cmp0.ExtractMostSignificantBits() & 0x0Fu) != 0) return true;

                Vector128<float> acc1 = D1;
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx1, acc1) : (vSx * Nx1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny1, acc1) : (vSy * Ny1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz1, acc1) : (vSz * Nz1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vRx, AbsNx1, acc1) : (vRx * AbsNx1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vRy, AbsNy1, acc1) : (vRy * AbsNy1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vRz, AbsNz1, acc1) : (vRz * AbsNz1 + acc1);

                Vector128<float> cmp1 = Vector128.LessThan(acc1, Vector128<float>.Zero);
                return (cmp1.ExtractMostSignificantBits() & 0x01u) != 0;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsOutside6(float sx, float sy, float sz, float rx, float ry, float rz)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                var vSx = Vector256.Create(sx);
                var vSy = Vector256.Create(sy);
                var vSz = Vector256.Create(sz);
                var vRx = Vector256.Create(rx * InvSqrt3);
                var vRy = Vector256.Create(ry * InvSqrt3);
                var vRz = Vector256.Create(rz * InvSqrt3);

                Vector256<float> acc = D;
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx, acc) : (vSx * Nx + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny, acc) : (vSy * Ny + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz, acc) : (vSz * Nz + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vRx, AbsNx, acc) : (vRx * AbsNx + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vRy, AbsNy, acc) : (vRy * AbsNy + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vRz, AbsNz, acc) : (vRz * AbsNz + acc);

                Vector256<float> cmp = Vector256.LessThan(acc, Vector256<float>.Zero);
                return (cmp.ExtractMostSignificantBits() & 0x3Fu) != 0;
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var vSx = Vector128.Create(sx);
                var vSy = Vector128.Create(sy);
                var vSz = Vector128.Create(sz);
                var vRx = Vector128.Create(rx * InvSqrt3);
                var vRy = Vector128.Create(ry * InvSqrt3);
                var vRz = Vector128.Create(rz * InvSqrt3);

                Vector128<float> acc0 = D0;
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx0, acc0) : (vSx * Nx0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny0, acc0) : (vSy * Ny0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz0, acc0) : (vSz * Nz0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vRx, AbsNx0, acc0) : (vRx * AbsNx0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vRy, AbsNy0, acc0) : (vRy * AbsNy0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vRz, AbsNz0, acc0) : (vRz * AbsNz0 + acc0);

                Vector128<float> cmp0 = Vector128.LessThan(acc0, Vector128<float>.Zero);
                if ((cmp0.ExtractMostSignificantBits() & 0x0Fu) != 0) return true;

                Vector128<float> acc1 = D1;
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx1, acc1) : (vSx * Nx1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny1, acc1) : (vSy * Ny1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz1, acc1) : (vSz * Nz1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vRx, AbsNx1, acc1) : (vRx * AbsNx1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vRy, AbsNy1, acc1) : (vRy * AbsNy1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vRz, AbsNz1, acc1) : (vRz * AbsNz1 + acc1);

                Vector128<float> cmp1 = Vector128.LessThan(acc1, Vector128<float>.Zero);
                return (cmp1.ExtractMostSignificantBits() & 0x03u) != 0;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSphereOutside6(float sx, float sy, float sz, float radius)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                var vSx = Vector256.Create(sx);
                var vSy = Vector256.Create(sy);
                var vSz = Vector256.Create(sz);
                var vRad = Vector256.Create(radius);

                Vector256<float> acc = D;
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx, acc) : (vSx * Nx + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny, acc) : (vSy * Ny + acc);
                acc = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz, acc) : (vSz * Nz + acc);
                acc += vRad;

                Vector256<float> cmp = Vector256.LessThanOrEqual(acc, Vector256<float>.Zero);
                return (cmp.ExtractMostSignificantBits() & 0x3Fu) != 0;
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var vSx = Vector128.Create(sx);
                var vSy = Vector128.Create(sy);
                var vSz = Vector128.Create(sz);
                var vRad = Vector128.Create(radius);

                Vector128<float> acc0 = D0;
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx0, acc0) : (vSx * Nx0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny0, acc0) : (vSy * Ny0 + acc0);
                acc0 = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz0, acc0) : (vSz * Nz0 + acc0);
                acc0 += vRad;

                Vector128<float> cmp0 = Vector128.LessThanOrEqual(acc0, Vector128<float>.Zero);
                if ((cmp0.ExtractMostSignificantBits() & 0x0Fu) != 0) return true;

                Vector128<float> acc1 = D1;
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSx, Nx1, acc1) : (vSx * Nx1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSy, Ny1, acc1) : (vSy * Ny1 + acc1);
                acc1 = Fma.IsSupported ? Fma.MultiplyAdd(vSz, Nz1, acc1) : (vSz * Nz1 + acc1);
                acc1 += vRad;

                Vector128<float> cmp1 = Vector128.LessThanOrEqual(acc1, Vector128<float>.Zero);
                return (cmp1.ExtractMostSignificantBits() & 0x03u) != 0;
            }

            return false;
        }
    }

    [ThreadStatic]
    private static Context? t_context;
    [ThreadStatic]
    private static FrustumCulling? t_lastCuller;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Context GetOrCreateContext(FrustumCulling culler, Plane[] planes)
    {
        var ctx = t_context;
        if (ctx == null || !ReferenceEquals(culler, t_lastCuller))
        {
            ctx = new Context();
            ctx.Update(planes);
            t_context = ctx;
            t_lastCuller = culler;
            return ctx;
        }

        if (!ctx.IsUpToDate(planes))
        {
            ctx.Update(planes);
        }
        return ctx;
    }

    public static bool InFrustumAndRange(
        FrustumCulling culler,
        Sphere sphere,
        bool nowVisible,
        int lodLevel,
        ModelDataPoolLocation location)
    {
        if (OptimumConfig.ChiselLodEnabled
            && (lodLevel == 2 || lodLevel == 3)
            && OptimumApiBridge.IsChiselTracked(location))
        {
            var pPos = GetPlayerPos(culler);
            if (pPos == null) return false;

            double chiselDistance = pPos.HorDistanceSqTo(sphere.x, sphere.z);
            if (chiselDistance >= culler.ViewDistanceSq)
            {
                return false;
            }

            double chiselDistanceSq = OptimumConfig.ChiselLodDistanceSq;
            double innerThreshold = chiselDistanceSq;
            double outerThreshold = chiselDistanceSq * OptimumConfig.HysteresisFactorSq;
            bool chiselVisible = lodLevel == 2
                ? chiselDistance <= (nowVisible ? outerThreshold : innerThreshold)
                : chiselDistance > (nowVisible ? innerThreshold : outerThreshold);
            if (!chiselVisible) return false;
        }
        else
        {
            var playerPos = GetPlayerPos(culler);
            if (playerPos == null) return false;

            double distance = playerPos.HorDistanceSqTo(sphere.x, sphere.z);
            bool rangeVisible = lodLevel switch
            {
                0 => culler.lod0BiasSq > 0 && distance < culler.lod0BiasSq + 32 * 32,
                1 => distance < culler.ViewDistanceSq,
                2 => distance <= culler.lod2BiasSq,
                3 => distance > culler.lod2BiasSq && distance < culler.ViewDistanceSq,
                _ => false,
            };
            if (!rangeVisible) return false;
        }

        Plane[] planes = GetFrustum(culler);
        if (planes == null || planes.Length < 6)
        {
            return false;
        }

        if (OptimumConfig.EffectiveSimdCulling)
        {
            var ctx = GetOrCreateContext(culler, planes);
            if (ctx.IsOutside5(sphere.x, sphere.y, sphere.z, sphere.radius, sphere.radiusY, sphere.radiusZ))
            {
                OptimumDiagnostics.RecordSimdFrustumTest(culled: true);
                return false;
            }

            OptimumDiagnostics.RecordSimdFrustumTest(culled: false);
            return true;
        }

        for (int i = 0; i < 5; i++)
        {
            if (planes[i].AABBisOutside(sphere)) return false;
        }
        return true;
    }

    public static bool InFrustum(FrustumCulling culler, Sphere sphere)
    {
        return InFrustumCore(culler, sphere, recordDiagnostics: true);
    }

    /// <summary>Tests upload-visibility telemetry bounds without counting them as renderer culls.</summary>
    internal static bool InFrustumForTelemetry(FrustumCulling culler, Sphere sphere)
    {
        return InFrustumCore(culler, sphere, recordDiagnostics: false);
    }

    private static bool InFrustumCore(FrustumCulling culler, Sphere sphere, bool recordDiagnostics)
    {
        Plane[] planes = GetFrustum(culler);
        if (planes == null || planes.Length < 6)
        {
            return false;
        }

        if (OptimumConfig.EffectiveSimdCulling)
        {
            var ctx = GetOrCreateContext(culler, planes);
            bool outside = ctx.IsOutside6(sphere.x, sphere.y, sphere.z, sphere.radius, sphere.radiusY, sphere.radiusZ);
            if (recordDiagnostics)
            {
                OptimumDiagnostics.RecordSimdFrustumTest(culled: outside);
            }
            return !outside;
        }

        for (int i = 0; i < 6; i++)
        {
            if (planes[i].AABBisOutside(sphere)) return false;
        }
        return true;
    }

    public static bool InFrustumShadowPass(FrustumCulling culler, Sphere sphere)
    {
        var playerPos = GetPlayerPos(culler);
        if (playerPos != null)
        {
            double distX = Math.Abs(playerPos.X - sphere.x);
            if (distX >= culler.shadowRangeX) return false;
            double distZ = Math.Abs(playerPos.Z - sphere.z);
            if (distZ >= culler.shadowRangeZ) return false;
        }

        Plane[] planes = GetFrustum(culler);
        if (planes == null || planes.Length < 6)
        {
            return false;
        }

        if (OptimumConfig.EffectiveSimdCulling)
        {
            var ctx = GetOrCreateContext(culler, planes);
            bool outside = ctx.IsOutside6(sphere.x, sphere.y, sphere.z, sphere.radius, sphere.radiusY, sphere.radiusZ);
            OptimumDiagnostics.RecordSimdFrustumTest(culled: outside);
            return !outside;
        }

        for (int i = 0; i < 6; i++)
        {
            if (planes[i].AABBisOutside(sphere)) return false;
        }
        return true;
    }

    public static bool SphereInFrustum(FrustumCulling culler, double x, double y, double z, double radius)
    {
        Plane[] planes = GetFrustum(culler);
        if (planes == null || planes.Length < 6)
        {
            return false;
        }

        if (OptimumConfig.EffectiveSimdCulling)
        {
            var ctx = GetOrCreateContext(culler, planes);
            bool outside = ctx.IsSphereOutside6((float)x, (float)y, (float)z, (float)radius);
            OptimumDiagnostics.RecordSimdFrustumTest(culled: outside);
            return !outside;
        }

        for (int i = 0; i < 6; i++)
        {
            if (planes[i].distanceOfPoint(x, y, z) <= -radius) return false;
        }
        return true;
    }
}
