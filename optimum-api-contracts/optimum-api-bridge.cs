using System;
using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Vintagestory.API.Config;

public static class OptimumApiBridge
{
    private sealed class ChiselLodMarker;

    private static readonly ConditionalWeakTable<ModelDataPoolLocation, ChiselLodMarker> ChiselLodLocations = new();

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerPos")]
    internal static extern ref BlockPos GetPlayerPos(FrustumCulling culler);

    private static int inventoryDirty = 1;

    internal static readonly SearchValues<char> CodePartDelimiter = SearchValues.Create("-");
    internal static readonly SearchValues<char> WildcardStar = SearchValues.Create("*");
    internal static readonly SearchValues<char> AssetColonDelimiter = SearchValues.Create(":");
    internal static readonly SearchValues<char> WildcardTriggerChars = SearchValues.Create("*@");
    internal static readonly SearchValues<char> AssetPathDelimiters = SearchValues.Create(":/");

    /// <summary>
    /// Reinterprets an array of unmanaged primitives (floats, ints, shorts, etc.) directly as a read-only byte span
    /// using MemoryMarshal.AsBytes, eliminating intermediate byte array allocations and memcpy overhead for GL buffer uploads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> AsBytes<T>(T[]? array) where T : struct
    {
        return array == null ? ReadOnlySpan<byte>.Empty : MemoryMarshal.AsBytes(array.AsSpan());
    }

    /// <summary>
    /// Reinterprets a slice of an array of unmanaged primitives directly as a read-only byte span
    /// using MemoryMarshal.AsBytes, eliminating intermediate byte array allocations and memcpy overhead for GL buffer uploads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> AsBytes<T>(T[]? array, int start, int length) where T : struct
    {
        return array == null ? ReadOnlySpan<byte>.Empty : MemoryMarshal.AsBytes(array.AsSpan(start, length));
    }

    /// <summary>
    /// Reinterprets a span of unmanaged primitives directly as a read-only byte span
    /// using MemoryMarshal.AsBytes, eliminating intermediate byte array allocations and memcpy overhead for GL buffer uploads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> AsBytes<T>(ReadOnlySpan<T> span) where T : struct
    {
        return MemoryMarshal.AsBytes(span);
    }

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

    public static bool InFrustum(FrustumCulling culler, Sphere sphere) =>
        OptimumFrustumCullSimd.InFrustum(culler, sphere);

    public static bool InFrustumShadowPass(FrustumCulling culler, Sphere sphere) =>
        OptimumFrustumCullSimd.InFrustumShadowPass(culler, sphere);

    public static bool SphereInFrustum(FrustumCulling culler, double x, double y, double z, double radius) =>
        OptimumFrustumCullSimd.SphereInFrustum(culler, x, y, z, radius);

    public static bool InFrustumAndRange(
        FrustumCulling culler,
        Sphere sphere,
        bool nowVisible,
        int lodLevel) =>
        OptimumFrustumCullSimd.InFrustumAndRange(culler, sphere, nowVisible, lodLevel, null!);

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
        BlockPos playerPos = GetPlayerPos(culler);
        return playerPos.HorDistanceSqTo(sphere.x, sphere.z);
    }

    // GUI elements that generate raw GL texture ids call these from their patched Dispose.
    public static void DeleteGuiTextures(System.Collections.Generic.Dictionary<string, int> textureIds, ICoreClientAPI api)
    {
        foreach (int textureId in textureIds.Values) api.Render.GLDeleteTexture(textureId);
        textureIds.Clear();
    }

    public static void DeleteGuiTexture(ref int textureId, ICoreClientAPI api)
    {
        if (textureId > 0) api.Render.GLDeleteTexture(textureId);
        textureId = 0;
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
        ReadOnlySpan<char> pathSpan = path.AsSpan();
        if (posFromRight == 0 && pathSpan.IndexOfAny(CodePartDelimiter) < 0) return path;
        if (posFromRight < 0)
        {
            _ = path.Length;
            throw new IndexOutOfRangeException();
        }

        int end = path.Length;
        for (int skip = 0; skip < posFromRight; skip++)
        {
            int dash = pathSpan.Slice(0, end).LastIndexOfAny(CodePartDelimiter);
            if (dash < 0) return null;
            end = dash;
        }
        int start = pathSpan.Slice(0, end).LastIndexOfAny(CodePartDelimiter) + 1;
        return path.Substring(start, end - start);
    }

    public static string? RegistryObjectFirstCodePart(RegistryObject? obj, int posFromLeft = 0)
    {
        if (obj?.Code == null) return null;
        string path = obj.Code.Path;
        ReadOnlySpan<char> pathSpan = path.AsSpan();
        if (posFromLeft == 0 && pathSpan.IndexOfAny(CodePartDelimiter) < 0) return path;
        if (posFromLeft < 0)
        {
            _ = path.Length;
            throw new IndexOutOfRangeException();
        }

        int start = 0;
        int part = 0;
        ReadOnlySpan<char> remaining = pathSpan;
        while (true)
        {
            int dashIdx = remaining.IndexOfAny(CodePartDelimiter);
            if (dashIdx < 0)
            {
                return part == posFromLeft ? path.Substring(start) : null;
            }
            if (part == posFromLeft)
            {
                return path.Substring(start, dashIdx);
            }
            part++;
            start += dashIdx + 1;
            remaining = remaining.Slice(dashIdx + 1);
        }
    }

    public static AssetLocation? RegistryObjectFillPlaceHolder(
        AssetLocation? input,
        OrderedDictionary<string, string> searchReplace)
    {
        if (searchReplace.Count == 0)
        {
            return input;
        }

        input!.Path = RegistryObjectFillPlaceHolder(input.Path, searchReplace)!;
        return input;
    }

    public static string RegistryObjectFillPlaceHolder(
        string input,
        OrderedDictionary<string, string> searchReplace)
    {
        if (searchReplace.Count == 0)
        {
            return input;
        }

        ArgumentNullException.ThrowIfNull(input);

        int firstOpen = input.IndexOf('{');
        if (firstOpen < 0)
        {
            return input;
        }

        ReadOnlySpan<char> span = input.AsSpan();
        char[]? rented = null;
        Span<char> buffer = span.Length <= 128
            ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(span.Length * 2));

        int pos = 0;
        bool changed = false;
        bool replacementHasBrace = false;

        try
        {
            if (firstOpen > 0)
            {
                EnsureCharBufferCapacity(ref buffer, ref rented, pos, firstOpen);
                span.Slice(0, firstOpen).CopyTo(buffer.Slice(pos));
                pos += firstOpen;
                span = span.Slice(firstOpen);
            }

            while (!span.IsEmpty)
            {
                int openIdx = span.IndexOf('{');
                if (openIdx < 0)
                {
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, span.Length);
                    span.CopyTo(buffer.Slice(pos));
                    pos += span.Length;
                    break;
                }

                if (openIdx > 0)
                {
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, openIdx);
                    span.Slice(0, openIdx).CopyTo(buffer.Slice(pos));
                    pos += openIdx;
                    span = span.Slice(openIdx);
                }

                int closeIdx = span.Slice(1).IndexOf('}');
                if (closeIdx < 0)
                {
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, span.Length);
                    span.CopyTo(buffer.Slice(pos));
                    pos += span.Length;
                    break;
                }
                closeIdx += 1;

                ReadOnlySpan<char> inner = span.Slice(1, closeIdx - 1);
                int nestedOpen = inner.LastIndexOf('{');
                if (nestedOpen >= 0)
                {
                    int literalLen = nestedOpen + 1;
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, literalLen);
                    span.Slice(0, literalLen).CopyTo(buffer.Slice(pos));
                    pos += literalLen;
                    span = span.Slice(nestedOpen + 1);
                    continue;
                }

                string? replacement = MatchPlaceholder(inner, searchReplace);
                if (replacement != null)
                {
                    changed = true;
                    if (replacement.Contains('{'))
                    {
                        replacementHasBrace = true;
                    }
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, replacement.Length);
                    replacement.AsSpan().CopyTo(buffer.Slice(pos));
                    pos += replacement.Length;
                }
                else
                {
                    int rawLen = closeIdx + 1;
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, rawLen);
                    span.Slice(0, rawLen).CopyTo(buffer.Slice(pos));
                    pos += rawLen;
                }

                span = span.Slice(closeIdx + 1);
            }

            if (!changed)
            {
                return input;
            }

            if (replacementHasBrace && searchReplace.Count > 1)
            {
                string current = input;
                foreach (var val in searchReplace)
                {
                    current = RegistryObjectFillPlaceHolder(current, val.Key, val.Value);
                }
                return current;
            }

            return new string(buffer.Slice(0, pos));
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    public static string RegistryObjectFillPlaceHolder(
        string input,
        string search,
        string replace)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(replace);

        int firstOpen = input.IndexOf('{');
        if (firstOpen < 0)
        {
            return input;
        }

        ReadOnlySpan<char> searchSpan = search.AsSpan();
        ReadOnlySpan<char> span = input.AsSpan();
        char[]? rented = null;
        Span<char> buffer = span.Length <= 128
            ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(span.Length * 2));

        int pos = 0;
        bool changed = false;

        try
        {
            if (firstOpen > 0)
            {
                EnsureCharBufferCapacity(ref buffer, ref rented, pos, firstOpen);
                span.Slice(0, firstOpen).CopyTo(buffer.Slice(pos));
                pos += firstOpen;
                span = span.Slice(firstOpen);
            }

            while (!span.IsEmpty)
            {
                int openIdx = span.IndexOf('{');
                if (openIdx < 0)
                {
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, span.Length);
                    span.CopyTo(buffer.Slice(pos));
                    pos += span.Length;
                    break;
                }

                if (openIdx > 0)
                {
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, openIdx);
                    span.Slice(0, openIdx).CopyTo(buffer.Slice(pos));
                    pos += openIdx;
                    span = span.Slice(openIdx);
                }

                int closeIdx = span.Slice(1).IndexOf('}');
                if (closeIdx < 0)
                {
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, span.Length);
                    span.CopyTo(buffer.Slice(pos));
                    pos += span.Length;
                    break;
                }
                closeIdx += 1;

                ReadOnlySpan<char> inner = span.Slice(1, closeIdx - 1);
                int nestedOpen = inner.LastIndexOf('{');
                if (nestedOpen >= 0)
                {
                    int literalLen = nestedOpen + 1;
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, literalLen);
                    span.Slice(0, literalLen).CopyTo(buffer.Slice(pos));
                    pos += literalLen;
                    span = span.Slice(nestedOpen + 1);
                    continue;
                }

                bool matches = inner.IndexOf('|') < 0
                    ? inner.SequenceEqual(searchSpan)
                    : ContainsPipeSegment(inner, searchSpan);

                if (matches)
                {
                    changed = true;
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, replace.Length);
                    replace.AsSpan().CopyTo(buffer.Slice(pos));
                    pos += replace.Length;
                }
                else
                {
                    int rawLen = closeIdx + 1;
                    EnsureCharBufferCapacity(ref buffer, ref rented, pos, rawLen);
                    span.Slice(0, rawLen).CopyTo(buffer.Slice(pos));
                    pos += rawLen;
                }

                span = span.Slice(closeIdx + 1);
            }

            return changed ? new string(buffer.Slice(0, pos)) : input;
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static string? MatchPlaceholder(
        ReadOnlySpan<char> inner,
        OrderedDictionary<string, string> searchReplace)
    {
        if (inner.IndexOf('|') < 0)
        {
            foreach (var val in searchReplace)
            {
                if (val.Key != null && inner.SequenceEqual(val.Key.AsSpan()))
                {
                    return val.Value ?? string.Empty;
                }
            }
            return null;
        }

        foreach (var val in searchReplace)
        {
            if (val.Key != null && ContainsPipeSegment(inner, val.Key.AsSpan()))
            {
                return val.Value ?? string.Empty;
            }
        }
        return null;
    }

    private static bool ContainsPipeSegment(ReadOnlySpan<char> inner, ReadOnlySpan<char> key)
    {
        while (true)
        {
            int pipe = inner.IndexOf('|');
            ReadOnlySpan<char> segment = pipe < 0 ? inner : inner.Slice(0, pipe);
            if (segment.SequenceEqual(key))
            {
                return true;
            }
            if (pipe < 0)
            {
                return false;
            }
            inner = inner.Slice(pipe + 1);
        }
    }

    private static void EnsureCharBufferCapacity(
        ref Span<char> buffer,
        ref char[]? rented,
        int pos,
        int needed)
    {
        if (pos + needed <= buffer.Length)
        {
            return;
        }

        int newSize = Math.Max(buffer.Length * 2, pos + needed);
        char[] next = ArrayPool<char>.Shared.Rent(newSize);
        buffer.Slice(0, pos).CopyTo(next);
        if (rented != null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
        rented = next;
        buffer = next;
    }

    public static bool WildcardUtilMatch(AssetLocation? needle, AssetLocation? haystack)
    {
        if (needle == null || haystack == null) return false;
        if (needle.Domain != "*" && needle.Domain != haystack.Domain) return false;
        string? needlePath = needle.Path;
        string? haystackPath = haystack.Path;
        if (needlePath == null || haystackPath == null) return false;
        return WildcardUtil.Match(needlePath, haystackPath);
    }

    public static bool WildcardUtilMatch(AssetLocation? wildCard, AssetLocation? inCode, string[]? allowedVariants)
    {
        if (wildCard == null || inCode == null) return false;
        if (wildCard.Domain == "*" && wildCard.Path == "*") return true;
        if (wildCard.Equals(inCode)) return true;
        if (wildCard.Domain != "*" && !wildCard.Domain.Equals(inCode.Domain)) return false;

        string? needle = wildCard.Path;
        string? haystack = inCode.Path;
        if (needle == null || haystack == null) return false;

        ReadOnlySpan<char> needleSpan = needle.AsSpan();
        if (needleSpan.IndexOfAny(WildcardTriggerChars) < 0)
            return needle.Length == haystack.Length && needle.Equals(haystack, StringComparison.OrdinalIgnoreCase);

        if (!WildcardUtil.Match(needle, haystack)) return false;

        if (allowedVariants != null && allowedVariants.Length > 0 && needleSpan.IndexOfAny(WildcardStar) >= 0)
        {
            if (!WildcardUtil.MatchesVariants(wildCard, inCode, allowedVariants)) return false;
        }

        return true;
    }

    public static AssetLocation? WildcardUtilWildCardReplace(AssetLocation? code, AssetLocation? search, AssetLocation? replace)
    {
        if (search == code) return search;
        if (code == null || search == null || replace == null || code.Path == null || search.Path == null || replace.Path == null) return null;
        if (search.Domain != "*" && search.Domain != code.Domain) return null;

        string searchPathStr = search.Path;
        string codePathStr = code.Path;
        string replacePathStr = replace.Path;

        ReadOnlySpan<char> searchPath = searchPathStr.AsSpan();
        ReadOnlySpan<char> codePath = codePathStr.AsSpan();
        ReadOnlySpan<char> replacePath = replacePathStr.AsSpan();

        int starCount = 0;
        ReadOnlySpan<char> searchScan = searchPath;
        while (true)
        {
            int idx = searchScan.IndexOfAny(WildcardStar);
            if (idx < 0) break;
            starCount++;
            searchScan = searchScan.Slice(idx + 1);
        }

        if (starCount == 0)
        {
            if (codePath.SequenceEqual(searchPath))
            {
                return new AssetLocation(code.Domain, replacePathStr);
            }
            return null;
        }

        Span<Range> captures = stackalloc Range[Math.Min(starCount, 16)];
        if (!MatchWildcardGreedy(codePath, searchPath, captures, 0, 0))
        {
            return null;
        }

        int replaceStars = 0;
        ReadOnlySpan<char> replaceScan = replacePath;
        while (true)
        {
            int idx = replaceScan.IndexOfAny(WildcardStar);
            if (idx < 0) break;
            replaceStars++;
            replaceScan = replaceScan.Slice(idx + 1);
        }

        int toFill = Math.Min(replaceStars, starCount);
        int finalLength = replacePath.Length - toFill;
        for (int i = 0; i < toFill; i++)
        {
            finalLength += captures[i].GetOffsetAndLength(codePath.Length).Length;
        }

        Range[] capturedRanges = captures[0..toFill].ToArray();
        string outCode = string.Create(finalLength, (codePathStr, replacePathStr, capturedRanges, toFill), static (dest, state) =>
        {
            ReadOnlySpan<char> cSpan = state.codePathStr.AsSpan();
            ReadOnlySpan<char> rSpan = state.replacePathStr.AsSpan();
            Range[] caps = state.capturedRanges;
            int fillCount = state.toFill;

            int destIdx = 0;
            int repIdx = 0;
            int capIdx = 0;

            while (repIdx < rSpan.Length)
            {
                int nextStar = rSpan.Slice(repIdx).IndexOfAny(WildcardStar);
                if (nextStar < 0 || capIdx >= fillCount)
                {
                    rSpan.Slice(repIdx).CopyTo(dest.Slice(destIdx));
                    break;
                }

                rSpan.Slice(repIdx, nextStar).CopyTo(dest.Slice(destIdx));
                destIdx += nextStar;
                repIdx += nextStar + 1;

                var (offset, length) = caps[capIdx].GetOffsetAndLength(cSpan.Length);
                cSpan.Slice(offset, length).CopyTo(dest.Slice(destIdx));
                destIdx += length;
                capIdx++;
            }
        });

        return new AssetLocation(code.Domain, outCode);
    }

    private static bool MatchWildcardGreedy(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern, Span<Range> captures, int captureIndex, int textOffset)
    {
        int starIdx = pattern.IndexOfAny(WildcardStar);
        if (starIdx < 0)
        {
            return text.SequenceEqual(pattern);
        }

        ReadOnlySpan<char> prefix = pattern.Slice(0, starIdx);
        if (!text.StartsWith(prefix))
        {
            return false;
        }

        ReadOnlySpan<char> restPattern = pattern.Slice(starIdx + 1);
        int restStar = restPattern.IndexOfAny(WildcardStar);
        if (restStar < 0)
        {
            if (!text.Slice(starIdx).EndsWith(restPattern))
            {
                return false;
            }

            int capLen = text.Length - starIdx - restPattern.Length;
            if (captureIndex < captures.Length)
            {
                captures[captureIndex] = new Range(textOffset + starIdx, textOffset + starIdx + capLen);
            }
            return true;
        }

        ReadOnlySpan<char> literal = restPattern.Slice(0, restStar);
        ReadOnlySpan<char> searchSlice = text.Slice(starIdx);
        int searchSliceOffset = textOffset + starIdx;

        int remainingSearchLength = searchSlice.Length - restPattern.Slice(restStar).Length + 1;
        while (remainingSearchLength >= literal.Length)
        {
            int foundIdx = searchSlice.Slice(0, remainingSearchLength).LastIndexOf(literal);
            if (foundIdx < 0) break;

            int nextTextOffset = searchSliceOffset + foundIdx + literal.Length;
            ReadOnlySpan<char> nextText = searchSlice.Slice(foundIdx + literal.Length);
            ReadOnlySpan<char> nextPattern = restPattern.Slice(restStar);

            if (MatchWildcardGreedy(nextText, nextPattern, captures, captureIndex + 1, nextTextOffset))
            {
                if (captureIndex < captures.Length)
                {
                    captures[captureIndex] = new Range(searchSliceOffset, searchSliceOffset + foundIdx);
                }
                return true;
            }

            remainingSearchLength = foundIdx;
        }

        return false;
    }

    public static bool EvolvingNatFloatEquals(EvolvingNatFloat left, EvolvingNatFloat right)
    {
        return left.Transform == right.Transform && left.Factor == right.Factor;
    }

    public static void ClientAnimatorInitMatrices(ClientAnimator animator, int maxJointId)
    {
        var identMat = ClientAnimator.identMat;
        var defaultPoseMatrices = new float[16 * maxJointId];
        for (int i = 0; i < defaultPoseMatrices.Length; i += 16)
        {
            Buffer.BlockCopy(identMat, 0, defaultPoseMatrices, i * sizeof(float), 16 * sizeof(float));
        }
        animator.TransformationMatricesDefaultPose = defaultPoseMatrices;
        animator.TransformationMatrices = new float[defaultPoseMatrices.Length];
    }

    public static void AssetLocationResolveToDomainAndPath(string? domainAndPath, out string? domain, out string? path)
    {
        if (domainAndPath == null)
        {
            domain = null;
            path = null;
            return;
        }

        ReadOnlySpan<char> span = domainAndPath.AsSpan();
        int colonIndex = span.IndexOfAny(AssetColonDelimiter);

        if (colonIndex == -1)
        {
            domain = null;
            path = LowercaseAndIntern(span, domainAndPath);
            return;
        }

        ReadOnlySpan<char> domainSpan = span.Slice(0, colonIndex);
        ReadOnlySpan<char> pathSpan = span.Slice(colonIndex + 1);

        if (domainSpan.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            domain = "game";
        }
        else if (domainSpan.Equals("survival", StringComparison.OrdinalIgnoreCase))
        {
            domain = "survival";
        }
        else if (domainSpan.Equals("creative", StringComparison.OrdinalIgnoreCase))
        {
            domain = "creative";
        }
        else
        {
            domain = LowercaseAndIntern(domainSpan, null);
        }

        path = LowercaseAndIntern(pathSpan, null);
    }

    private static string LowercaseAndIntern(ReadOnlySpan<char> span, string? original)
    {
        if (span.IsEmpty) return string.Empty;

        bool hasUpper = false;
        for (int i = 0; i < span.Length; i++)
        {
            if (char.IsUpper(span[i]))
            {
                hasUpper = true;
                break;
            }
        }

        if (!hasUpper)
        {
            if (original != null && original.Length == span.Length)
            {
                return string.Intern(original);
            }
            if (span.Length <= 256)
            {
                Span<char> buf = stackalloc char[span.Length];
                span.CopyTo(buf);
                return string.Intern(new string(buf));
            }
            return string.Intern(span.ToString());
        }

        if (span.Length <= 256)
        {
            Span<char> buf = stackalloc char[span.Length];
            span.ToLowerInvariant(buf);
            return string.Intern(new string(buf));
        }

        return string.Intern(span.ToString().ToLowerInvariant());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetIncandescenceColorFloats(int temperature, out float r, out float g, out float b)
    {
        if (temperature < 500)
        {
            r = g = b = 0f;
            return;
        }

        r = Math.Max(0f, Math.Min(1f, (temperature - 500) / 400f));
        g = Math.Max(0f, Math.Min(1f, (temperature - 900) / 200f));
        b = Math.Max(0f, Math.Min(1f, (temperature - 1100) / 200f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetIncandescenceColorFloats(int temperature, out float r, out float g, out float b, out float a)
    {
        if (temperature < 500)
        {
            r = g = b = a = 0f;
            return;
        }

        r = Math.Max(0f, Math.Min(1f, (temperature - 500) / 400f));
        g = Math.Max(0f, Math.Min(1f, (temperature - 900) / 200f));
        b = Math.Max(0f, Math.Min(1f, (temperature - 1100) / 200f));
        a = Math.Max(0f, Math.Min(1f, (temperature - 525) / 2f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToRGBAFloats(int color, out float r, out float g, out float b, out float a)
    {
        r = ((color >> 16) & 0xff) / 255f;
        g = ((color >> 8) & 0xff) / 255f;
        b = (color & 0xff) / 255f;
        a = ((color >> 24) & 0xff) / 255f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float[] GetIncandescenceColorAsColor4f(int temperature)
    {
        GetIncandescenceColorFloats(temperature, out float r, out float g, out float b, out float a);
        return new float[] { r, g, b, a };
    }
}
