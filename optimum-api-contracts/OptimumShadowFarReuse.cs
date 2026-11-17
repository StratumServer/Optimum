using System;
using System.Collections.Generic;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #608: Tracks invalidation state and camera-delta reprojection for reusing
/// the far shadow cascade (<c>EnumRenderStage.ShadowFar</c>) across stationary and
/// bounded-motion frames when <c>ShadowMapQuality &gt; 1</c>.
/// </summary>
public sealed class OptimumShadowFarReuse
{
    public const double GuardMarginBlocks = 8.0;
    public const double MaxLightTexelShift = 1.0;
    public const double MaxZExtendDrift = 0.5;
    public const double EntityMoveThreshold = 0.25;
    public const double EntityMoveThresholdSq = EntityMoveThreshold * EntityMoveThreshold;
    public const double ChunkRangePadding = 64.0;
    public const double LargeEntityMinRadius = 3.5;

    private static readonly double[] OffsetMatrix = CreateOffsetMatrix();

    private struct EntitySnapshot
    {
        public double X;
        public double Y;
        public double Z;
    }

    private bool valid;
    private double cachedCamX;
    private double cachedCamY;
    private double cachedCamZ;
    private int cachedDimension;
    private int cachedShadowMapQuality;
    private int cachedViewportWidth;
    private int cachedViewportHeight;
    private int cachedFboId;
    private int cachedDepthTexId;
    private int cachedFboWidth;
    private int cachedFboHeight;
    private int cachedFieldOfView;
    private double cachedZNear;
    private float cachedShadowRangeFar;
    private double cachedShadowDistance;
    private double cachedMinBoxSpan;
    private double cachedShadowBoxZExtend;
    private double cachedLightDirX;
    private double cachedLightDirY;
    private double cachedLightDirZ;
    private bool cachedUsingMoon;
    private bool cachedVegSkip;
    private bool cachedChiselLod;
    private int cachedChiselDist;
    private bool cachedEntityCull;
    private int cachedEntityCullDist;
    private int cachedTerrainGen;

    private readonly double[] cachedBasePv = new double[16];
    private readonly double[] scratchReprojectedPv = new double[16];
    private readonly double[] scratchToShadow = new double[16];
    private readonly Dictionary<long, EntitySnapshot> cachedEntities = new();
    private int entityMatchedCount;
    private bool entityCheckInvalidated;

    public bool IsValid => valid;
    public float CachedShadowRangeFar => cachedShadowRangeFar;

    public void Invalidate()
    {
        valid = false;
        cachedEntities.Clear();
    }

    public bool CanReuseMetadata(
        double camX, double camY, double camZ,
        int dimension,
        int shadowMapQuality,
        int viewportWidth, int viewportHeight,
        int fboId, int depthTexId, int fboWidth, int fboHeight,
        int fieldOfView, double zNear,
        double lightPosX, double lightPosY, double lightPosZ,
        bool usingMoon, double shadowBoxZExtend,
        bool vegSkip, bool chiselLod, int chiselDist, bool entityCull, int entityCullDist,
        int terrainGen,
        int miniDimensionCount)
    {
        if (!valid) return false;
        if (!OptimumConfig.ShadowFarReuseEnabled || shadowMapQuality <= 1) return false;
        if (miniDimensionCount > 0) return false;
        if (dimension != cachedDimension || shadowMapQuality != cachedShadowMapQuality) return false;
        if (viewportWidth != cachedViewportWidth || viewportHeight != cachedViewportHeight) return false;
        if (fboId == 0 || depthTexId == 0 || fboId != cachedFboId || depthTexId != cachedDepthTexId ||
            fboWidth != cachedFboWidth || fboHeight != cachedFboHeight) return false;
        if (fieldOfView != cachedFieldOfView || zNear != cachedZNear) return false;
        if (vegSkip != cachedVegSkip || chiselLod != cachedChiselLod || chiselDist != cachedChiselDist ||
            entityCull != cachedEntityCull || entityCullDist != cachedEntityCullDist) return false;
        if (terrainGen != cachedTerrainGen) return false;
        if (usingMoon != cachedUsingMoon || Math.Abs(shadowBoxZExtend - cachedShadowBoxZExtend) > MaxZExtendDrift) return false;

        double dx = camX - cachedCamX;
        double dy = camY - cachedCamY;
        double dz = camZ - cachedCamZ;
        if (!IsCameraWithinGuardMargin(dx, dy, dz, GuardMarginBlocks)) return false;

        int shadowMapResolution = Math.Max(4, shadowMapQuality + 2) * 1024;
        double texelShift = ComputeLightTexelShift(
            cachedLightDirX, cachedLightDirY, cachedLightDirZ,
            lightPosX, lightPosY, lightPosZ,
            cachedShadowDistance, cachedMinBoxSpan, shadowMapResolution);
        return texelShift <= MaxLightTexelShift;
    }

    public void BeginEntityCheck()
    {
        entityMatchedCount = 0;
        entityCheckInvalidated = false;
    }

    public bool CheckEntity(long entityId, double x, double y, double z)
    {
        if (entityCheckInvalidated) return false;
        if (!cachedEntities.TryGetValue(entityId, out EntitySnapshot snapshot))
        {
            entityCheckInvalidated = true;
            return false;
        }
        double dx = x - snapshot.X;
        double dy = y - snapshot.Y;
        double dz = z - snapshot.Z;
        if (dx * dx + dy * dy + dz * dz > EntityMoveThresholdSq)
        {
            entityCheckInvalidated = true;
            return false;
        }
        entityMatchedCount++;
        return true;
    }

    public bool FinishEntityCheck()
    {
        return !entityCheckInvalidated && entityMatchedCount == cachedEntities.Count;
    }

    public void Reproject(double camX, double camY, double camZ, float[] destMatrix)
    {
        double dx = camX - cachedCamX;
        double dy = camY - cachedCamY;
        double dz = camZ - cachedCamZ;
        ReprojectToShadowMapSpaceMatrix(cachedBasePv, OffsetMatrix, dx, dy, dz, scratchReprojectedPv, scratchToShadow, destMatrix);
    }

    public void BeginSnapshotRecording()
    {
        cachedEntities.Clear();
    }

    public void RecordEntity(long entityId, double x, double y, double z)
    {
        cachedEntities[entityId] = new EntitySnapshot { X = x, Y = y, Z = z };
    }

    public void CommitFreshRender(
        double[] projectionViewMatrix,
        double camX, double camY, double camZ,
        int dimension,
        int shadowMapQuality,
        int viewportWidth, int viewportHeight,
        int fboId, int depthTexId, int fboWidth, int fboHeight,
        int fieldOfView, double zNear,
        float shadowRangeFar, double shadowDistance,
        double boxWidth, double boxHeight,
        double lightPosX, double lightPosY, double lightPosZ,
        bool usingMoon, double shadowBoxZExtend,
        bool vegSkip, bool chiselLod, int chiselDist, bool entityCull, int entityCullDist,
        int terrainGen)
    {
        if (projectionViewMatrix == null || projectionViewMatrix.Length < 16 || fboId == 0 || depthTexId == 0)
        {
            Invalidate();
            return;
        }
        if (!TryNormalize(lightPosX, lightPosY, lightPosZ, out double nx, out double ny, out double nz))
        {
            Invalidate();
            return;
        }

        Buffer.BlockCopy(projectionViewMatrix, 0, cachedBasePv, 0, 16 * sizeof(double));
        cachedCamX = camX;
        cachedCamY = camY;
        cachedCamZ = camZ;
        cachedDimension = dimension;
        cachedShadowMapQuality = shadowMapQuality;
        cachedViewportWidth = viewportWidth;
        cachedViewportHeight = viewportHeight;
        cachedFboId = fboId;
        cachedDepthTexId = depthTexId;
        cachedFboWidth = fboWidth;
        cachedFboHeight = fboHeight;
        cachedFieldOfView = fieldOfView;
        cachedZNear = zNear;
        cachedShadowRangeFar = shadowRangeFar;
        cachedShadowDistance = shadowDistance;
        cachedMinBoxSpan = Math.Min(boxWidth, boxHeight);
        cachedShadowBoxZExtend = shadowBoxZExtend;
        cachedLightDirX = nx;
        cachedLightDirY = ny;
        cachedLightDirZ = nz;
        cachedUsingMoon = usingMoon;
        cachedVegSkip = vegSkip;
        cachedChiselLod = chiselLod;
        cachedChiselDist = chiselDist;
        cachedEntityCull = entityCull;
        cachedEntityCullDist = entityCullDist;
        cachedTerrainGen = terrainGen;
        valid = true;
    }

    public static bool IsCameraWithinGuardMargin(double deltaX, double deltaY, double deltaZ, double guardMargin = GuardMarginBlocks)
    {
        return deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ <= guardMargin * guardMargin;
    }

    public static bool TryNormalize(double x, double y, double z, out double nx, out double ny, out double nz)
    {
        double lenSq = x * x + y * y + z * z;
        if (lenSq <= 1e-18 || double.IsNaN(lenSq) || double.IsInfinity(lenSq))
        {
            nx = 0.0;
            ny = 0.0;
            nz = 0.0;
            return false;
        }
        double inv = 1.0 / Math.Sqrt(lenSq);
        nx = x * inv;
        ny = y * inv;
        nz = z * inv;
        return true;
    }

    public static double ComputeLightTexelShift(
        double normCachedX, double normCachedY, double normCachedZ,
        double lightPosX, double lightPosY, double lightPosZ,
        double shadowDistance, double minBoxSpan, int shadowMapResolution)
    {
        if (!TryNormalize(lightPosX, lightPosY, lightPosZ, out double nx, out double ny, out double nz))
        {
            return double.PositiveInfinity;
        }
        double dx = nx - normCachedX;
        double dy = ny - normCachedY;
        double dz = nz - normCachedZ;
        double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (shadowDistance * chord * shadowMapResolution) / Math.Max(1.0, minBoxSpan);
    }

    public static bool IsChunkWithinShadowFarRange(
        double camX, double camZ, int playerDimension,
        double chunkCenterX, double chunkCenterZ, int chunkDimension,
        float shadowRangeFar, int miniDimensionsId)
    {
        if (chunkDimension == miniDimensionsId) return true;
        if (chunkDimension != playerDimension) return false;
        double maxDist = Math.Max(60.0, shadowRangeFar) + ChunkRangePadding;
        double dx = chunkCenterX - camX;
        double dz = chunkCenterZ - camZ;
        return dx * dx + dz * dz <= maxDist * maxDist;
    }

    public static void ReprojectToShadowMapSpaceMatrix(
        double[] basePv,
        double[] offsetMatrix,
        double deltaX, double deltaY, double deltaZ,
        double[] scratchTranslatedPv,
        double[] scratchToShadow,
        float[] destMatrix)
    {
        // Column-major post-translation: M * T(delta) adjusts only column 3.
        Array.Copy(basePv, scratchTranslatedPv, 12);
        scratchTranslatedPv[12] = basePv[0] * deltaX + basePv[4] * deltaY + basePv[8] * deltaZ + basePv[12];
        scratchTranslatedPv[13] = basePv[1] * deltaX + basePv[5] * deltaY + basePv[9] * deltaZ + basePv[13];
        scratchTranslatedPv[14] = basePv[2] * deltaX + basePv[6] * deltaY + basePv[10] * deltaZ + basePv[14];
        scratchTranslatedPv[15] = basePv[3] * deltaX + basePv[7] * deltaY + basePv[11] * deltaZ + basePv[15];

        for (int col = 0; col < 4; col++)
        {
            int colBase = col * 4;
            double b0 = scratchTranslatedPv[colBase];
            double b1 = scratchTranslatedPv[colBase + 1];
            double b2 = scratchTranslatedPv[colBase + 2];
            double b3 = scratchTranslatedPv[colBase + 3];
            for (int row = 0; row < 4; row++)
            {
                scratchToShadow[colBase + row] =
                    offsetMatrix[row] * b0 +
                    offsetMatrix[4 + row] * b1 +
                    offsetMatrix[8 + row] * b2 +
                    offsetMatrix[12 + row] * b3;
            }
        }

        for (int i = 0; i < 16; i++)
        {
            destMatrix[i] = (float)scratchToShadow[i];
        }
    }

    public static double[] CreateOffsetMatrix()
    {
        double[] m = new double[16];
        m[0] = 0.5;
        m[5] = 0.5;
        m[10] = 0.5;
        m[12] = 0.5;
        m[13] = 0.5;
        m[14] = 0.5;
        m[15] = 1.0;
        return m;
    }
}
