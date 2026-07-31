using System;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

/// <summary>
/// Bridge for Optimum-patched methods. When fork patches are not applied,
/// these methods provide stub/fallback implementations.
/// </summary>
public static class OptimumApiBridge
{
    /// <summary>
    /// Converts RGB to HSV. Falls back to manual calculation if patched method unavailable.
    /// </summary>
    public static void RgbToHsvInts(int red, int green, int blue, int[] destination)
    {
        // Fallback: manual RGB to HSV conversion
        float r = red / 255f;
        float g = green / 255f;
        float b = blue / 255f;
        
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;
        
        destination[2] = (int)(max * 255);
        if (max > 0)
            destination[1] = (int)((delta / max) * 255);
        else
            destination[1] = 0;
        
        if (delta == 0)
            destination[0] = 0;
        else if (max == r)
            destination[0] = (int)(60 * (((g - b) / delta) % 6));
        else if (max == g)
            destination[0] = (int)(60 * (((b - r) / delta) + 2));
        else
            destination[0] = (int)(60 * (((r - g) / delta) + 4));
        
        if (destination[0] < 0) destination[0] += 360;
    }

    public static void HsvToRgbInts(int hue, int saturation, int value, int[] destination)
    {
        float h = hue;
        float s = saturation / 255f;
        float v = value / 255f;
        float c = v * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float m = v - c;
        float r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        destination[0] = (int)((r + m) * 255);
        destination[1] = (int)((g + m) * 255);
        destination[2] = (int)((b + m) * 255);
    }

    public static T SortableQueueItemAt<T>(SortableQueue<T> queue, int index) where T : IComparable<T>
    {
        throw new NotSupportedException("SortableQueue.ItemAt requires Optimum patches.");
    }

    public static void GetViewVector(float pitch, float yaw, Vec3f destination)
    {
        float cosPitch = (float)Math.Cos(pitch);
        destination.X = (float)(-Math.Sin(yaw) * cosPitch);
        destination.Y = (float)Math.Sin(pitch);
        destination.Z = (float)(-Math.Cos(yaw) * cosPitch);
    }

    public static void SetChiselLodDistance(ModelDataPoolLocation location, bool enabled) { }
    public static bool ConsumeAnySlotDirty() { return false; }
    public static void ForceAdaptiveCap(AdaptiveWorkerController controller, int cap)
    {
        controller.ForceCapForTesting(cap);
    }
}
