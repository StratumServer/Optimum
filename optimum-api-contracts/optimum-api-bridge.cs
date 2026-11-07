using System;
using System.Diagnostics;
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

    internal static bool IsChiselTracked(ModelDataPoolLocation location) =>
        location != null && ChiselLodLocations.TryGetValue(location, out _);

    public static bool InFrustumAndRange(
        FrustumCulling culler,
        Sphere sphere,
        bool nowVisible,
        int lodLevel,
        ModelDataPoolLocation location)
    {
        if (OptimumConfig.EffectiveSimdCulling)
        {
            return OptimumFrustumCullSimd.InFrustumAndRange(culler, sphere, nowVisible, lodLevel, location);
        }

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
            // Hysteresis: entities that were visible stay visible until they cross the outer threshold.
            double innerThreshold = chiselDistanceSq;
            double outerThreshold = chiselDistanceSq * OptimumConfig.HysteresisFactorSq;
            return lodLevel == 2
                ? chiselDistance <= (nowVisible ? outerThreshold : innerThreshold)
                : chiselDistance > (nowVisible ? innerThreshold : outerThreshold);
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

        // Timed from here down: TryGetValue plus the reflection-based playerPos read in
        // ChiselDistanceSqTo are the actual per-call cost of this hook (see
        // docs/benchmarking.md / ChiselLodShadowBenchmark for the isolated measurement).
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (!ChiselLodLocations.TryGetValue(location, out _))
            {
                return baseResult;
            }

            double chiselDistanceSq = OptimumConfig.ChiselLodDistanceSq;
            double chiselDistance = ChiselDistanceSqTo(culler, location.FrustumCullSphere);
            // Hysteresis: entities that were visible stay visible until they cross the outer threshold.
            double innerThreshold = chiselDistanceSq;
            double outerThreshold = chiselDistanceSq * OptimumConfig.HysteresisFactorSq;
            return lodLevel == 2
                ? chiselDistance <= (baseResult ? outerThreshold : innerThreshold)
                : chiselDistance > (baseResult ? innerThreshold : outerThreshold);
        }
        finally
        {
            if (OptimumDiagnostics.StutterWatchEnabled)
            {
                OptimumDiagnostics.RecordChiselShadowCull(Stopwatch.GetTimestamp() - start);
            }
        }
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

    private const char Ascii85FirstCharacter = '!';
    private const char Ascii85LastCharacter = 'u';

    private static readonly uint[] Ascii85Powers =
    [
        85u * 85u * 85u * 85u,
        85u * 85u * 85u,
        85u * 85u,
        85u,
        1u,
    ];

    public static string Ascii85Encode(byte[] bytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        int exactLength = GetAscii85EncodedLength(bytes);
        return string.Create(exactLength, bytes, static (chars, state) =>
        {
            int writePos = 0;
            int i = 0;
            Span<char> scratch = stackalloc char[5];

            while (i <= state.Length - 4)
            {
                uint val = ((uint)state[i] << 24) | ((uint)state[i + 1] << 16) |
                           ((uint)state[i + 2] << 8) | state[i + 3];
                if (val == 0)
                {
                    chars[writePos++] = 'z';
                }
                else
                {
                    uint v = val;
                    for (int k = 4; k >= 0; k--)
                    {
                        scratch[k] = (char)((v % 85) + Ascii85FirstCharacter);
                        v /= 85;
                    }
                    scratch.CopyTo(chars.Slice(writePos, 5));
                    writePos += 5;
                }
                i += 4;
            }

            int rem = state.Length - i;
            if (rem > 0)
            {
                uint val = 0;
                for (int j = 0; j < rem; j++)
                {
                    val |= (uint)state[i + j] << (24 - 8 * j);
                }

                int charsToWrite = rem + 1;
                uint v = val;
                for (int k = 4; k >= 0; k--)
                {
                    scratch[k] = (char)((v % 85) + Ascii85FirstCharacter);
                    v /= 85;
                }
                scratch.Slice(0, charsToWrite).CopyTo(chars.Slice(writePos, charsToWrite));
            }
        });
    }

    public static byte[] Ascii85Decode(string encoded)
    {
        if (encoded == null)
        {
            throw new ArgumentNullException(nameof(encoded));
        }

        int decodedLength = GetAscii85DecodedLength(encoded);
        byte[] result = new byte[decodedLength];
        int writePos = 0;
        int count = 0;
        uint value = 0;

        for (int i = 0; i < encoded.Length; i++)
        {
            char ch = encoded[i];
            if (ch == 'z' && count == 0)
            {
                result[writePos++] = 0;
                result[writePos++] = 0;
                result[writePos++] = 0;
                result[writePos++] = 0;
            }
            else if (ch < Ascii85FirstCharacter || ch > Ascii85LastCharacter)
            {
                throw new FormatException($"Invalid character '{ch}' in Ascii85 block.");
            }
            else
            {
                try
                {
                    uint digit = (uint)(ch - Ascii85FirstCharacter);
                    uint add = checked(Ascii85Powers[count] * digit);
                    value = checked(value + add);
                }
                catch (OverflowException ex)
                {
                    throw new FormatException("The current group of characters decodes to a value greater than UInt32.MaxValue.", ex);
                }

                count++;

                if (count == 5)
                {
                    result[writePos++] = (byte)(value >> 24);
                    result[writePos++] = (byte)(value >> 16);
                    result[writePos++] = (byte)(value >> 8);
                    result[writePos++] = (byte)value;
                    count = 0;
                    value = 0;
                }
            }
        }

        if (count == 1)
        {
            throw new FormatException("The final Ascii85 block must contain more than one character.");
        }
        else if (count > 1)
        {
            for (int padding = count; padding < 5; padding++)
            {
                try
                {
                    value = checked(value + 84u * Ascii85Powers[padding]);
                }
                catch (OverflowException ex)
                {
                    throw new FormatException("The current group of characters decodes to a value greater than UInt32.MaxValue.", ex);
                }
            }
            result[writePos++] = (byte)(value >> 24);
            if (count > 2) result[writePos++] = (byte)(value >> 16);
            if (count > 3) result[writePos++] = (byte)(value >> 8);
        }

        return result;
    }

    private static int GetAscii85EncodedLength(byte[] bytes)
    {
        int length = 0;
        int i = 0;
        while (i <= bytes.Length - 4)
        {
            if ((bytes[i] | bytes[i + 1] | bytes[i + 2] | bytes[i + 3]) == 0)
            {
                length += 1;
            }
            else
            {
                length += 5;
            }
            i += 4;
        }

        int rem = bytes.Length - i;
        if (rem > 0)
        {
            length += rem + 1;
        }
        return length;
    }

    private static int GetAscii85DecodedLength(string encoded)
    {
        int length = 0;
        int count = 0;
        for (int i = 0; i < encoded.Length; i++)
        {
            char ch = encoded[i];
            if (ch == 'z' && count == 0)
            {
                length += 4;
            }
            else if (ch >= Ascii85FirstCharacter && ch <= Ascii85LastCharacter)
            {
                count++;
                if (count == 5)
                {
                    length += 4;
                    count = 0;
                }
            }
            else
            {
                throw new FormatException($"Invalid character '{ch}' in Ascii85 block.");
            }
        }
        if (count == 1)
        {
            throw new FormatException("The final Ascii85 block must contain more than one character.");
        }
        if (count > 1)
        {
            length += count - 1;
        }
        return length;
    }

    public static string? RegistryObjectLastCodePart(RegistryObject? obj, int posFromRight = 0)
    {
        if (obj?.Code == null) return null;
        string path = obj.Code.Path;
        if (posFromRight == 0 && !path.Contains('-')) return path;
        if (posFromRight < 0)
        {
            _ = path.Length;
            throw new IndexOutOfRangeException();
        }

        int end = path.Length;
        for (int skip = 0; skip < posFromRight; skip++)
        {
            int dash = path.LastIndexOf('-', end - 1);
            if (dash < 0) return null;
            end = dash;
        }
        int start = path.LastIndexOf('-', end - 1) + 1;
        return path.Substring(start, end - start);
    }

    public static string? RegistryObjectFirstCodePart(RegistryObject? obj, int posFromLeft = 0)
    {
        if (obj?.Code == null) return null;
        string path = obj.Code.Path;
        if (posFromLeft == 0 && !path.Contains('-')) return path;
        if (posFromLeft < 0)
        {
            _ = path.Length;
            throw new IndexOutOfRangeException();
        }

        int start = 0;
        int part = 0;
        int pathLength = path.Length;
        for (int i = 0; i < pathLength; i++)
        {
            if (path[i] != '-') continue;
            if (part == posFromLeft) return path.Substring(start, i - start);
            part++;
            start = i + 1;
        }

        return part == posFromLeft ? path.Substring(start) : null;
    }
}
