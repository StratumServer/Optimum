using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

public static class OptimumApiBridge
{
    private sealed class ChiselLodMarker;

    private static readonly ConditionalWeakTable<ModelDataPoolLocation, ChiselLodMarker> ChiselLodLocations = new();
    private static readonly FieldInfo FrustumPlayerPos =
        typeof(FrustumCulling).GetField("playerPos", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(FrustumCulling).FullName, "playerPos");
    private static int inventoryDirty = 1;

    public static void RgbToHsvInts(int red, int green, int blue, int[] destination)
    {
        float k = 0f;
        if (green < blue)
        {
            (green, blue) = (blue, green);
            k = -1f;
        }
        if (red < green)
        {
            (red, green) = (green, red);
            k = -2f / 6f - k;
        }

        float chroma = red - Math.Min(green, blue);
        destination[0] = (int)(255 * Math.Abs(k + (green - blue) / (6.0 * chroma + 1.0e-20)));
        destination[1] = (int)(255 * chroma / (red + 1.0e-20));
        destination[2] = red;
    }

    public static void HsvToRgbInts(int hue, int saturation, int value, int[] destination)
    {
        if (saturation == 0 || value == 0)
        {
            destination[0] = value;
            destination[1] = value;
            destination[2] = value;
            return;
        }

        int region = hue / 43;
        int remainder = (hue - region * 43) * 6;
        int p = value * (255 - saturation) >> 8;
        int q = value * (255 - (saturation * remainder >> 8)) >> 8;
        int t = value * (255 - (saturation * (255 - remainder) >> 8)) >> 8;

        (destination[0], destination[1], destination[2]) = region switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
    }

    public static void GetViewVector(float pitch, float yaw, Vec3f destination)
    {
        float cosPitch = GameMath.Cos(pitch);
        float sinPitch = GameMath.Sin(pitch);
        float cosYaw = GameMath.Cos(yaw + GameMath.PI / 2);
        float sinYaw = GameMath.Sin(yaw + GameMath.PI / 2);
        destination.Set(-cosPitch * sinYaw, sinPitch, -cosPitch * cosYaw);
    }

    public static void SetChiselLodDistance(ModelDataPoolLocation location, bool enabled)
    {
        if (enabled)
        {
            ChiselLodLocations.GetValue(location, static _ => new ChiselLodMarker());
        }
        else
        {
            ChiselLodLocations.Remove(location);
        }
    }

    public static bool InFrustumAndRange(
        FrustumCulling culler,
        Sphere sphere,
        bool nowVisible,
        int lodLevel,
        ModelDataPoolLocation location)
    {
        // Mirrors the source-tree FrustumCulling.InFrustumAndRange chisel branch:
        // LOD 2 (real carved mesh) renders inside ChiselLodDistanceSq, LOD 3 (cube proxy)
        // renders outside it. Both LOD levels of a chisel chunk part are flagged, so this
        // MUST branch on lodLevel - returning one shared boolean makes them both visible
        // up close (z-fighting) and both invisible far away (block disappears).
        if (OptimumConfig.ChiselLodEnabled
            && (lodLevel == 2 || lodLevel == 3)
            && ChiselLodLocations.TryGetValue(location, out _))
        {
            // lodLevel 1 == frustum planes + "distance < ViewDistanceSq", i.e. exactly the
            // base bound the source-tree chisel branch keeps. The vanilla lod2Bias split is
            // replaced by the chisel distance split below.
            if (!culler.InFrustumAndRange(sphere, nowVisible, 1))
            {
                return false;
            }

            double chiselDistanceSq = OptimumConfig.ChiselLodDistanceSq;
            double chiselDistance = ChiselDistanceSqTo(culler, sphere);
            return lodLevel == 2
                ? chiselDistance <= chiselDistanceSq
                : chiselDistance > chiselDistanceSq;
        }

        return culler.InFrustumAndRange(sphere, nowVisible, lodLevel);
    }

    /// <summary>
    /// Shadow-pass counterpart of <see cref="InFrustumAndRange"/>. Vanilla's
    /// CullInstantShadowPassNear/Far cases have no distance awareness at all, so the LOD 3
    /// cube proxy always made it into the depth map - producing a full-block shadow for a
    /// carved block at any distance. Applies the same LOD 2 / LOD 3 distance split.
    /// </summary>
    public static bool InFrustumShadowPass(bool baseResult, FrustumCulling culler, ModelDataPoolLocation location)
    {
        if (!baseResult || !OptimumConfig.ChiselLodEnabled)
        {
            return baseResult;
        }

        int lodLevel = location.LodLevel;
        if (lodLevel != 2 && lodLevel != 3)
        {
            return baseResult;
        }
        if (!ChiselLodLocations.TryGetValue(location, out _))
        {
            return baseResult;
        }

        double chiselDistanceSq = OptimumConfig.ChiselLodDistanceSq;
        double chiselDistance = ChiselDistanceSqTo(culler, location.FrustumCullSphere);
        return lodLevel == 2
            ? chiselDistance <= chiselDistanceSq
            : chiselDistance > chiselDistanceSq;
    }

    private static double ChiselDistanceSqTo(FrustumCulling culler, Sphere sphere)
    {
        var playerPos = (BlockPos)FrustumPlayerPos.GetValue(culler)!;
        return playerPos.HorDistanceSqTo(sphere.x, sphere.z);
    }

    public static void MarkInventoryDirty()
    {
        Volatile.Write(ref inventoryDirty, 1);
    }

    public static bool ConsumeAnySlotDirty()
    {
        return Interlocked.Exchange(ref inventoryDirty, 0) != 0;
    }

    public static void ForceAdaptiveCap(AdaptiveWorkerController controller, int cap)
    {
        controller.ForceCapForTesting(cap);
    }
}
