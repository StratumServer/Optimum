using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    private static readonly ConditionalWeakTable<ITesselatorManager, MeshData> UnknownBlockModelDataByTesselator = new();
    private static int chiselLodTrackedCount;

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
            if (ChiselLodLocations.TryAdd(location, new ChiselLodMarker()))
            {
                Interlocked.Increment(ref chiselLodTrackedCount);
            }
        }
        else if (Volatile.Read(ref chiselLodTrackedCount) > 0 && ChiselLodLocations.Remove(location))
        {
            if (Interlocked.Decrement(ref chiselLodTrackedCount) < 0)
            {
                Interlocked.Exchange(ref chiselLodTrackedCount, 0);
            }
        }
    }

    internal static bool IsChiselTracked(ModelDataPoolLocation location) =>
        Volatile.Read(ref chiselLodTrackedCount) > 0 && location != null && ChiselLodLocations.TryGetValue(location, out _);

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
            && IsChiselTracked(location))
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
            if (!IsChiselTracked(location))
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

    [ThreadStatic]
    private static DummySlot? s_searchDummySlot;

    [ThreadStatic]
    private static WeightedSlotEntry[]? s_weightedSlotScratch;

    private readonly struct WeightedSlotEntry(int key, ItemSlot slot, float weight, int order)
    {
        public readonly int Key = key;
        public readonly ItemSlot Slot = slot;
        public readonly float Weight = weight;
        public readonly int Order = order;
    }

    private sealed class WeightedSlotEntryComparer : System.Collections.Generic.IComparer<WeightedSlotEntry>
    {
        public static readonly WeightedSlotEntryComparer Instance = new();

        public int Compare(WeightedSlotEntry x, WeightedSlotEntry y)
        {
            int weightComparison = x.Weight.CompareTo(y.Weight);
            return weightComparison != 0 ? weightComparison : x.Order.CompareTo(y.Order);
        }
    }

    public static bool ItemStackMatchesSearchText(ItemStack stack, IWorldAccessor? world, string? searchText)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return true;
        }

        string name = stack.GetName();
        if (name != null && name.CaseInsensitiveContains(searchText))
        {
            return true;
        }

        // Steal the thread-local DummySlot across GetDescription so a nested
        // MatchesSearchText inside a modded GetHeldItemInfo cannot clobber it.
        DummySlot? slot = s_searchDummySlot;
        if (slot != null)
        {
            s_searchDummySlot = null;
        }
        else
        {
            slot = new DummySlot();
        }

        slot.Set(stack);
        try
        {
            string? description = stack.GetDescription(world, slot, false);
            return description != null && description.CaseInsensitiveContains(searchText);
        }
        finally
        {
            slot.Set(null);
            slot.BackgroundIcon = null;
            slot.HexBackgroundColor = null;
            slot.DrawUnavailable = false;
            s_searchDummySlot ??= slot;
        }
    }

    public static void FilterSlotGridItems(
        ref string? searchTextField,
        OrderedDictionary<int, ItemSlot> availableSlots,
        OrderedDictionary<int, ItemSlot> renderedSlots,
        IInventory inventory,
        ICoreClientAPI? api,
        int cols,
        ref int rows,
        string? text,
        System.Collections.Generic.Dictionary<int, string>? searchCache,
        System.Collections.Generic.Dictionary<int, string>? searchCacheNames)
    {
        string normalized = string.IsNullOrEmpty(text)
            ? string.Empty
            : text.ToSearchFriendly().ToLowerInvariant();
        searchTextField = normalized;
        renderedSlots.Clear();

        int slotCount = availableSlots.Count;
        if (normalized.Length == 0)
        {
            for (int i = 0; i < slotCount; i++)
            {
                int key = availableSlots.GetKeyAtIndex(i);
                ItemSlot slot = inventory[key];
                if (slot.Itemstack == null) continue;
                renderedSlots.Add(key, slot);
            }

            rows = (int)Math.Ceiling(1f * renderedSlots.Count / cols);
            return;
        }

        if (searchCacheNames == null)
        {
            rows = (int)Math.Ceiling(1f * renderedSlots.Count / cols);
            return;
        }

        WeightedSlotEntry[]? scratch = s_weightedSlotScratch;
        s_weightedSlotScratch = null;
        int weightedCount = 0;
        try
        {
            IWorldAccessor? world = api?.World;
            for (int i = 0; i < slotCount; i++)
            {
                int key = availableSlots.GetKeyAtIndex(i);
                ItemSlot slot = inventory[key];
                ItemStack? stack = slot.Itemstack;
                if (stack == null) continue;

                if (searchCache != null && searchCache.TryGetValue(key, out string? cachedText))
                {
                    string? name = searchCacheNames[key];
                    float weight = ComputeSlotSearchWeight(name, cachedText, normalized);
                    if (weight >= 0f)
                    {
                        if (scratch == null)
                        {
                            scratch = new WeightedSlotEntry[Math.Clamp(slotCount, 16, 256)];
                        }
                        else if (weightedCount == scratch.Length)
                        {
                            Array.Resize(ref scratch, scratch.Length * 2);
                        }

                        scratch[weightedCount] = new WeightedSlotEntry(key, slot, weight, weightedCount);
                        weightedCount++;
                    }
                }
                else
                {
                    // Preserve vanilla evaluation order for searchCacheNames[key].
                    _ = searchCacheNames[key];
                    if (ItemStackMatchesSearchText(stack, world, normalized))
                    {
                        renderedSlots.Add(key, slot);
                    }
                }
            }

            if (weightedCount > 0)
            {
                Array.Sort(scratch!, 0, weightedCount, WeightedSlotEntryComparer.Instance);
                for (int i = 0; i < weightedCount; i++)
                {
                    renderedSlots.Add(scratch![i].Key, scratch[i].Slot);
                }
            }

            rows = (int)Math.Ceiling(1f * renderedSlots.Count / cols);
        }
        finally
        {
            if (scratch != null)
            {
                if (weightedCount > 0)
                {
                    Array.Clear(scratch, 0, weightedCount);
                }

                if (s_weightedSlotScratch == null || scratch.Length > s_weightedSlotScratch.Length)
                {
                    s_weightedSlotScratch = scratch;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeSlotSearchWeight(string? name, string? cachedText, string normalizedSearch)
    {
        if (System.Text.Ascii.IsValid(normalizedSearch))
        {
            return ComputeSlotSearchWeightAsciiFast(name, cachedText, normalizedSearch);
        }

        int index = name != null
            ? name.IndexOf(normalizedSearch, StringComparison.InvariantCultureIgnoreCase)
            : -1;

        if (index == 0 && name!.Length == normalizedSearch.Length) return 0f;
        if (index == 0 && name!.Length > normalizedSearch.Length && name[normalizedSearch.Length] == ' ') return 0.125f;
        if (index > 0 && name![index - 1] == ' ' && index + normalizedSearch.Length == name.Length) return 0.25f;
        if (index > 0 && name![index - 1] == ' ') return 0.5f;
        if (index == 0) return 0.75f;
        if (index > 0) return 1f;
        if (cachedText != null && cachedText.StartsWith(normalizedSearch, StringComparison.InvariantCultureIgnoreCase)) return 2f;
        if (cachedText != null && cachedText.CaseInsensitiveContains(normalizedSearch, StringComparison.InvariantCultureIgnoreCase)) return 3f;
        return -1f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeSlotSearchWeightAsciiFast(string? name, string? cachedText, string normalizedSearch)
    {
        StringComparison nameComparison = (name == null || System.Text.Ascii.IsValid(name))
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.InvariantCultureIgnoreCase;
        int index = name != null
            ? name.IndexOf(normalizedSearch, nameComparison)
            : -1;

        if (index == 0 && name!.Length == normalizedSearch.Length) return 0f;
        if (index == 0 && name!.Length > normalizedSearch.Length && name[normalizedSearch.Length] == ' ') return 0.125f;
        if (index > 0 && name![index - 1] == ' ' && index + normalizedSearch.Length == name.Length) return 0.25f;
        if (index > 0 && name![index - 1] == ' ') return 0.5f;
        if (index == 0) return 0.75f;
        if (index > 0) return 1f;

        if (cachedText != null)
        {
            StringComparison textComparison = System.Text.Ascii.IsValid(cachedText)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.InvariantCultureIgnoreCase;
            if (cachedText.StartsWith(normalizedSearch, textComparison)) return 2f;
            if (cachedText.IndexOf(normalizedSearch, textComparison) >= 0) return 3f;
        }
        return -1f;
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

    private const int LargeKeyFrameElementThreshold = 16;

    /// <summary>
    /// Computes the deepest element-pose nesting level without accumulating depth across siblings.
    /// </summary>
    public static int ClientAnimatorGetMaxDepth(System.Collections.Generic.List<ElementPose>? poses, int depth)
    {
        if (poses == null)
        {
            return depth;
        }

        int maxDepth = depth;
        for (int i = 0; i < poses.Count; i++)
        {
            ElementPose pose = poses[i];
            if (pose.ChildElementPoses != null)
            {
                int childDepth = ClientAnimatorGetMaxDepth(pose.ChildElementPoses, depth + 1);
                if (childDepth > maxDepth)
                {
                    maxDepth = childDepth;
                }
            }
        }

        return maxDepth;
    }

    /// <summary>
    /// Uses a pre-sized hash Dictionary once keyframe element count exceeds small-array scan size.
    /// </summary>
    public static System.Collections.Generic.IDictionary<ShapeElement, AnimationKeyFrameElement> CreateKeyFrameElementDictionary(int capacity)
    {
        return capacity > LargeKeyFrameElementThreshold
            ? new System.Collections.Generic.Dictionary<ShapeElement, AnimationKeyFrameElement>(capacity)
            : new FastSmallDictionary<ShapeElement, AnimationKeyFrameElement>(capacity);
    }

    /// <summary>
    /// Precomputes normalized keyframe interpolation factors once per active animation
    /// before recursive bone matrix traversal in ClientAnimator.calculateMatrices.
    /// </summary>
    public static void ComputeClientAnimatorFrameLerps(
        RunningAnimation[] curAnims,
        int animCount,
        int[] prevFrames,
        int[] nextFrames,
        ref float[]? frameLerps)
    {
        int count = Math.Min(animCount, Math.Min(curAnims.Length, Math.Min(prevFrames.Length, nextFrames.Length)));
        float[]? lerps = frameLerps;
        if (lerps == null || lerps.Length < count)
        {
            lerps = new float[Math.Max(count, ClientAnimator.MaxConcurrentAnimations)];
            frameLerps = lerps;
        }

        for (int i = 0; i < count; i++)
        {
            RunningAnimation anim = curAnims[i];
            int pf = prevFrames[i];
            int nf = nextFrames[i];
            int quantityFrames = anim.Animation.QuantityFrames;
            float keyFrameDist = nf > pf ? (nf - pf) : (quantityFrames - pf + nf);
            float curFrameDist = anim.CurrentFrame >= pf ? (anim.CurrentFrame - pf) : (quantityFrames - pf + anim.CurrentFrame);
            lerps[i] = keyFrameDist > 0f ? (curFrameDist / keyFrameDist) : 0f;
        }
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

    public static void DisposeLoadedTexture(
        object texture,
        ref bool disposed,
        ref int textureId,
        ICoreClientAPI? capi)
    {
        disposed = true;
        int id = textureId;
        if (id != 0)
        {
            textureId = 0;
            capi?.Gui.DeleteTexture(id);
        }
        GC.SuppressFinalize(texture);
    }

    public static void FinalizeLoadedTexture(
        ref bool disposed,
        ref int textureId,
        bool ignoreUndisposed,
        string? trace,
        ICoreClientAPI? capi)
    {
        int id = textureId;
        if (ignoreUndisposed || id == 0 || disposed || capi == null || OptimumGlReclaimQueue.IsGlShuttingDown)
        {
            return;
        }

        disposed = true;
        textureId = 0;

        if (OptimumConfig.GlFinalizerReclaimActive && !OptimumGlReclaimQueue.LoadedTextureFinalizerPatchedByMod)
        {
            OptimumGlReclaimQueue.EnqueueTexture(id);
        }

        if (OptimumGlReclaimQueue.ShouldLogTextureLeak(trace != null))
        {
            if (trace == null)
            {
                capi.Logger?.Debug("Texture with texture id {0} is leaking memory, missing call to Dispose. Set env var TEXTURE_DEBUG_DISPOSE to get allocation trace.", id);
            }
            else
            {
                capi.Logger?.Debug("Texture with texture id {0} is leaking memory, missing call to Dispose. Allocated at {1}.", id, trace);
            }
        }
    }

    private static readonly JsonLoadSettings IgnoreJsonLineInfoSettings = new()
    {
        LineInfoHandling = LineInfoHandling.Ignore,
    };

    public static JToken ParseJsonToken(string json)
    {
        return OptimumConfig.EffectiveIgnoreJsonLineInfo
            ? JToken.Parse(json, IgnoreJsonLineInfoSettings)
            : JToken.Parse(json);
    }

    public static JObject ParseJsonObject(string json)
    {
        return OptimumConfig.EffectiveIgnoreJsonLineInfo
            ? JObject.Parse(json, IgnoreJsonLineInfoSettings)
            : JObject.Parse(json);
    }

    public static JToken ReadJsonToken(JsonReader reader)
    {
        return OptimumConfig.EffectiveIgnoreJsonLineInfo
            ? JToken.ReadFrom(reader, IgnoreJsonLineInfoSettings)
            : JToken.ReadFrom(reader);
    }

    public static object? TryLoadJsonNoLineInfo(string text, Type targetType, JsonSerializerSettings? settings)
    {
        if (settings != null || !OptimumConfig.EffectiveIgnoreJsonLineInfo)
        {
            return null;
        }
        if (targetType == typeof(JObject))
        {
            using var sr = new StringReader(text);
            using var jr = new JsonTextReader(sr);
            return JObject.Load(jr, IgnoreJsonLineInfoSettings);
        }
        if (targetType == typeof(JToken))
        {
            using var sr = new StringReader(text);
            using var jr = new JsonTextReader(sr);
            return JToken.Load(jr, IgnoreJsonLineInfoSettings);
        }
        return null;
    }

    public delegate void InventoryMeshWindowPreparerDelegate(
        ICoreClientAPI api,
        Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot> renderedSlots,
        int visibleStartIndex,
        int visibleCount,
        int lookaheadStartIndex,
        int lookaheadCount);

    private static readonly object InventoryMeshPreparerGate = new();
    private static readonly ConditionalWeakTable<ICoreClientAPI, InventoryMeshWindowPreparerDelegate> InventoryMeshPreparers = new();
    private static ICoreClientAPI? lastInventoryMeshApi;
    private static InventoryMeshWindowPreparerDelegate? lastInventoryMeshPreparer;

    public static void RegisterInventoryMeshPreparer(
        ICoreClientAPI? api,
        InventoryMeshWindowPreparerDelegate? preparer)
    {
        if (api == null)
        {
            return;
        }

        lock (InventoryMeshPreparerGate)
        {
            if (preparer == null)
            {
                InventoryMeshPreparers.Remove(api);
                if (ReferenceEquals(lastInventoryMeshApi, api))
                {
                    lastInventoryMeshApi = null;
                    lastInventoryMeshPreparer = null;
                }
                return;
            }

            InventoryMeshPreparers.Remove(api);
            InventoryMeshPreparers.Add(api, preparer);
            lastInventoryMeshApi = api;
            lastInventoryMeshPreparer = preparer;
        }
    }

    public static void UnregisterInventoryMeshPreparer(ICoreClientAPI? api)
    {
        RegisterInventoryMeshPreparer(api, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static InventoryMeshWindowPreparerDelegate? GetInventoryMeshPreparer(ICoreClientAPI api)
    {
        if (ReferenceEquals(api, Volatile.Read(ref lastInventoryMeshApi)))
        {
            return Volatile.Read(ref lastInventoryMeshPreparer);
        }

        lock (InventoryMeshPreparerGate)
        {
            if (InventoryMeshPreparers.TryGetValue(api, out InventoryMeshWindowPreparerDelegate? preparer))
            {
                lastInventoryMeshApi = api;
                lastInventoryMeshPreparer = preparer;
                return preparer;
            }
            return null;
        }
    }

    public static void PrepareSlotGridInventoryMeshes(
        ICoreClientAPI? api,
        Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot>? renderedSlots,
        ElementBounds? elementBounds,
        ElementBounds[]? slotBounds)
    {
        if (!OptimumConfig.LazyInventoryMeshesEnabled || !OptimumConfig.LazyInventoryMeshPrepEnabled)
        {
            return;
        }

        if (api == null || renderedSlots == null || renderedSlots.Count == 0)
        {
            return;
        }

        InventoryMeshWindowPreparerDelegate? preparer = GetInventoryMeshPreparer(api);
        if (preparer == null)
        {
            return;
        }

        ElementBounds? parentBounds = elementBounds?.ParentBounds;
        int slotCount = renderedSlots.Count;
        if (parentBounds == null || slotBounds == null || slotBounds.Length == 0)
        {
            int visible = Math.Min(slotCount, 135);
            int lookahead = Math.Min(135, slotCount - visible);
            preparer(api, renderedSlots, 0, visible, visible, lookahead);
            return;
        }

        int count = Math.Min(slotCount, slotBounds.Length);
        double parentTop = parentBounds.absY;
        int startIndex = 0;
        if (count > 32 && slotBounds[0] != null && slotBounds[count - 1] != null && slotBounds[0].absY <= slotBounds[count - 1].absY)
        {
            int lo = 0;
            int hi = count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                ElementBounds? midBounds = slotBounds[mid];
                if (midBounds != null && midBounds.absY + midBounds.absInnerHeight < parentTop)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            if (lo > 0 && slotBounds[lo - 1] != null && slotBounds[lo - 1].absY + slotBounds[lo - 1].absInnerHeight >= parentTop)
            {
                lo = 0;
            }
            startIndex = lo;
        }

        int firstVisible = -1;
        int lastVisibleExclusive = startIndex;
        for (int i = startIndex; i < count; i++)
        {
            ElementBounds? slotBound = slotBounds[i];
            if (slotBound == null)
            {
                continue;
            }

            if (slotBound.absY + slotBound.absInnerHeight < parentTop)
            {
                continue;
            }

            if (slotBound.PartiallyInside(parentBounds))
            {
                if (firstVisible < 0)
                {
                    firstVisible = i;
                }
                lastVisibleExclusive = i + 1;
            }
            else if (firstVisible >= 0)
            {
                break;
            }
        }

        if (firstVisible < 0)
        {
            return;
        }

        int visibleCount = lastVisibleExclusive - firstVisible;
        int lookaheadStart = lastVisibleExclusive;
        int lookaheadCount = Math.Min(135, count - lookaheadStart);
        preparer(api, renderedSlots, firstVisible, visibleCount, lookaheadStart, lookaheadCount);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "api")]
    private static extern ref ICoreClientAPI GetGuiElementApi(GuiElement element);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cols")]
    private static extern ref int GetSlotGridCols(GuiElementItemSlotGridBase grid);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "inventory")]
    private static extern ref IInventory GetSlotGridInventory(GuiElementItemSlotGridBase grid);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "scissorBounds")]
    private static extern ref ElementBounds[]? GetSlotGridScissorBounds(GuiElementItemSlotGridBase grid);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ComposeSlotOverlays")]
    private static extern bool InvokeComposeSlotOverlays(GuiElementItemSlotGridBase grid, ItemSlot slot, int slotId, int slotIndex);

    /// <summary>
    /// Fast re-compose path after FilterItemsBySearchText: when static slot textures and font
    /// metrics already match the current GUI scale, rebuilds bounds and durability overlays ONLY
    /// for renderedSlots instead of disposing and reallocating textures for all availableSlots.
    /// </summary>
    public static bool TryComposeFilteredSlotGrid(GuiElementItemSlotGridBase? grid)
    {
        if (grid == null || grid.Bounds == null)
        {
            return false;
        }

        ICoreClientAPI api = GetGuiElementApi(grid);
        int cols = GetSlotGridCols(grid);
        IInventory inventory = GetSlotGridInventory(grid);
        if (api == null || cols <= 0 || inventory == null)
        {
            return false;
        }

        double absSlotSize = GuiElement.scaled(GuiElementPassiveItemSlot.unscaledSlotSize);
        int expectedPx = (int)absSlotSize;
        LoadedTexture? slotTex = grid.slotTexture;
        LoadedTexture? hiTex = grid.highlightSlotTexture;
        LoadedTexture? crossTex = grid.crossedOutTexture;
        LoadedTexture[]? oldQuantityTextures = grid.slotQuantityTextures;
        if (slotTex == null || slotTex.Disposed || slotTex.TextureId == 0 ||
            slotTex.Width != expectedPx || slotTex.Height != expectedPx ||
            hiTex == null || hiTex.Disposed || hiTex.TextureId == 0 ||
            crossTex == null || crossTex.Disposed || crossTex.TextureId == 0 ||
            grid.textComposer == null || oldQuantityTextures == null)
        {
            return false;
        }

        int count = grid.renderedSlots.Count;
        ElementBounds[] newSlotBounds = count == 0 ? Array.Empty<ElementBounds>() : new ElementBounds[count];
        ElementBounds[] newScissorBounds = count == 0 ? Array.Empty<ElementBounds>() : new ElementBounds[count];
        LoadedTexture[] newQuantityTextures = count == 0 ? Array.Empty<LoadedTexture>() : new LoadedTexture[count];

        int reusableLimit = Math.Min(count, oldQuantityTextures.Length);
        for (int i = 0; i < reusableLimit; i++)
        {
            LoadedTexture? existing = oldQuantityTextures[i];
            if (existing != null && !existing.Disposed && existing.TextureId == 0)
            {
                newQuantityTextures[i] = existing;
            }
            else
            {
                existing?.Dispose();
                newQuantityTextures[i] = new LoadedTexture(api);
            }
        }
        for (int i = reusableLimit; i < oldQuantityTextures.Length; i++)
        {
            oldQuantityTextures[i]?.Dispose();
        }
        for (int i = reusableLimit; i < count; i++)
        {
            newQuantityTextures[i] = new LoadedTexture(api);
        }

        grid.SlotBounds = newSlotBounds;
        GetSlotGridScissorBounds(grid) = newScissorBounds;
        grid.slotQuantityTextures = newQuantityTextures;

        grid.Bounds.CalcWorldBounds();

        double unscaledSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        double unscaledPadding = GuiElementItemSlotGridBase.unscaledSlotPadding;
        for (int idx = 0; idx < count; idx++)
        {
            int col = idx % cols;
            int row = idx / cols;
            double x = col * (unscaledSize + unscaledPadding);
            double y = row * (unscaledSize + unscaledPadding);

            ElementBounds slotBound = ElementBounds.Fixed(x, y, unscaledSize, unscaledSize).WithParent(grid.Bounds);
            slotBound.CalcWorldBounds();
            newSlotBounds[idx] = slotBound;

            ElementBounds scissorBound = ElementBounds.Fixed(x + 1.75, y + 1.5, unscaledSize - 4.0, unscaledSize - 4.0).WithParent(grid.Bounds);
            scissorBound.CalcWorldBounds();
            newScissorBounds[idx] = scissorBound;

            int key = grid.renderedSlots.GetKeyAtIndex(idx);
            ItemSlot? slot = inventory[key];
            if (slot?.Itemstack != null && slot.Itemstack.Collectible.ShouldDisplayItemDamage(slot.Itemstack))
            {
                InvokeComposeSlotOverlays(grid, slot, key, idx);
            }
        }

        return true;
    }

    private static bool TryGetCommonNatFloat(
        float avg,
        float var,
        EnumDistribution dist,
        float offset,
        out NatFloat? shared)
    {
        if (offset == 0f && dist == EnumDistribution.UNIFORM && avg == 1f)
        {
            if (var == 0f)
            {
                shared = SoundAttributes.NatFloatOne;
                return true;
            }
            if (var == 0.25f)
            {
                shared = SoundAttributes.RandomPitch;
                return true;
            }
        }

        shared = null;
        return false;
    }

    public static NatFloat ReuseCommonNatFloat(float avg, float var, EnumDistribution dist, float offset = 0f)
    {
        if (OptimumConfig.ReuseCommonNatFloatEnabled && TryGetCommonNatFloat(avg, var, dist, offset, out NatFloat? shared))
        {
            OptimumDiagnostics.RecordCommonNatFloatReuse();
            return shared!;
        }

        OptimumDiagnostics.RecordCommonNatFloatAlloc();
        NatFloat result = new NatFloat(avg, var, dist);
        if (offset != 0f)
        {
            result.offset = offset;
        }
        return result;
    }

    public static NatFloat ReuseCommonNatFloat(NatFloat value)
    {
        if (value == null)
        {
            return null!;
        }

        if (OptimumConfig.ReuseCommonNatFloatEnabled && TryGetCommonNatFloat(value.avg, value.var, value.dist, value.offset, out NatFloat? shared))
        {
            OptimumDiagnostics.RecordCommonNatFloatReuse();
            return shared!;
        }

        OptimumDiagnostics.RecordCommonNatFloatAlloc();
        return value;
    }

    public static NatFloat CloneOrReuseNatFloat(NatFloat? value)
    {
        if (value == null)
        {
            return null!;
        }

        if (OptimumConfig.ReuseCommonNatFloatEnabled && TryGetCommonNatFloat(value.avg, value.var, value.dist, value.offset, out NatFloat? shared))
        {
            OptimumDiagnostics.RecordCommonNatFloatReuse();
            return shared!;
        }

        OptimumDiagnostics.RecordCommonNatFloatAlloc();
        return value.Clone();
    }

    public static NatFloat ReadCommonNatFloatFromBytes(BinaryReader reader)
    {
        float offset = reader.ReadSingle();
        float avg = reader.ReadSingle();
        float var = reader.ReadSingle();
        EnumDistribution dist = (EnumDistribution)reader.ReadByte();
        return ReuseCommonNatFloat(avg, var, dist, offset);
    }

    public static SoundAttributes? ReadSoundAttributesJson(
        SoundAttributeConverter converter,
        JsonReader reader,
        JsonSerializer serializer)
    {
        if (reader.ValueType == typeof(string))
        {
            AssetLocationJsonParser? parser = null;
            var converters = serializer.Converters;
            for (int i = 0; i < converters.Count; i++)
            {
                if (converters[i] is AssetLocationJsonParser found)
                {
                    parser = found;
                    break;
                }
            }
            parser ??= new AssetLocationJsonParser(GlobalConstants.DefaultDomain);
            AssetLocation? location = parser.ReadJson(reader, typeof(AssetLocation), null, false, serializer);
            ArgumentNullException.ThrowIfNull(location);
            if (converter.AddSoundsPrefix)
            {
                location.WithPathPrefixOnce("sounds/");
            }

            return new SoundAttributes(location, converter.RandomPitch) { Range = converter.DefaultRange };
        }

        SoundAttributes sound = new SoundAttributes();
        sound.Range = converter.DefaultRange;

        // Clear defaults before Populate so Newtonsoft allocates only fields present in JSON.
        sound.Pitch = null!;
        sound.Volume = null!;

        try
        {
            object boxed = sound;
            serializer.Populate(reader, boxed);
            sound = (SoundAttributes)boxed;
            if (converter.AddSoundsPrefix)
            {
                sound.Location?.WithPathPrefixOnce("sounds/");
            }
        }
        catch (Exception e)
        {
            throw new Exception("Error while parsing " + sound, e);
        }

        if (sound.Pitch == null)
        {
            sound.Pitch = converter.RandomPitch ? SoundAttributes.RandomPitch : SoundAttributes.NatFloatOne;
        }
        else if (OptimumConfig.ReuseCommonNatFloatEnabled)
        {
            sound.Pitch = ReuseCommonNatFloat(sound.Pitch);
        }

        if (sound.Volume == null)
        {
            sound.Volume = SoundAttributes.NatFloatOne;
        }
        else if (OptimumConfig.ReuseCommonNatFloatEnabled)
        {
            sound.Volume = ReuseCommonNatFloat(sound.Volume);
        }

        return sound.Location == null ? null : sound;
    }

    /// <summary>
    /// Deduplicates a single shape or animation bone/element string against the session-scoped pool.
    /// </summary>
    public static string? DeduplicateShapeString(string? value)
    {
        return OptimumShapeStringPool.Shared.Deduplicate(value);
    }

    /// <summary>
    /// Concatenates a step-parenting prefix with a shape/keyframe name and returns a canonical
    /// session-scoped string instance without intermediate string allocation on cache hit.
    /// </summary>
    public static string ConcatAndDeduplicateShapeString(string? prefix, string? value)
    {
        return OptimumShapeStringPool.Shared.ConcatAndDeduplicate(prefix, value);
    }

    /// <summary>
    /// Deduplicates animation codes, names, and keyframe element dictionary keys on a loaded shape.
    /// Skips dictionary rebuild when every key already points to its canonical pooled instance.
    /// </summary>
    public static void DeduplicateShapeAnimations(Shape? shape)
    {
        Animation[]? animations = shape?.Animations;
        if (animations == null)
        {
            return;
        }

        for (int i = 0; i < animations.Length; i++)
        {
            Animation anim = animations[i];
            if (anim == null)
            {
                continue;
            }

            anim.Code = DeduplicateShapeString(anim.Code);
            anim.Name = DeduplicateShapeString(anim.Name);
            DeduplicateKeyFrames(anim.KeyFrames);
        }
    }

    /// <summary>
    /// Deduplicates element names, step-parent names, attachment point codes, and animation
    /// keyframe dictionary keys on a shape assembled or modified at runtime.
    /// </summary>
    public static void DeduplicateShapeStrings(Shape? shape)
    {
        if (shape == null)
        {
            return;
        }

        DeduplicateShapeElementsRecursive(shape.Elements);
        DeduplicateShapeAnimations(shape);
    }

    /// <summary>
    /// Releases all pooled shape and animation strings on asset reload or world exit.
    /// </summary>
    public static void ClearShapeStringPool()
    {
        OptimumShapeStringPool.Shared.Clear();
    }

    private static void DeduplicateShapeElementsRecursive(ShapeElement[]? elements)
    {
        if (elements == null)
        {
            return;
        }

        for (int i = 0; i < elements.Length; i++)
        {
            ShapeElement elem = elements[i];
            if (elem == null)
            {
                continue;
            }

            elem.Name = DeduplicateShapeString(elem.Name);
            elem.StepParentName = DeduplicateShapeString(elem.StepParentName);

            AttachmentPoint[]? attachmentPoints = elem.AttachmentPoints;
            if (attachmentPoints != null)
            {
                for (int j = 0; j < attachmentPoints.Length; j++)
                {
                    AttachmentPoint ap = attachmentPoints[j];
                    if (ap != null)
                    {
                        ap.Code = DeduplicateShapeString(ap.Code);
                    }
                }
            }

            if (elem.Children != null)
            {
                DeduplicateShapeElementsRecursive(elem.Children);
            }
        }
    }

    private static void DeduplicateKeyFrames(AnimationKeyFrame[]? keyFrames)
    {
        if (keyFrames == null)
        {
            return;
        }

        for (int i = 0; i < keyFrames.Length; i++)
        {
            AnimationKeyFrame kf = keyFrames[i];
            System.Collections.Generic.Dictionary<string, AnimationKeyFrameElement>? elements = kf?.Elements;
            if (elements == null || elements.Count == 0)
            {
                continue;
            }

            bool needsRebuild = false;
            foreach (System.Collections.Generic.KeyValuePair<string, AnimationKeyFrameElement> entry in elements)
            {
                string? canonical = DeduplicateShapeString(entry.Key);
                if (!object.ReferenceEquals(canonical, entry.Key))
                {
                    needsRebuild = true;
                    break;
                }
            }

            if (!needsRebuild)
            {
                continue;
            }

            var deduplicated = new System.Collections.Generic.Dictionary<string, AnimationKeyFrameElement>(elements.Count, elements.Comparer);
            foreach (System.Collections.Generic.KeyValuePair<string, AnimationKeyFrameElement> entry in elements)
            {
                deduplicated[DeduplicateShapeString(entry.Key)!] = entry.Value;
            }
            kf!.Elements = deduplicated;
        }
    }

    // Issue #642: TreeAttribute / BlockEntity span serialization bridge methods.
    public static void OnTreeAttributeRegistered(int attrId, Type type)
    {
        OptimumTreeAttributeCodec.OnAttributeRegistered(attrId, type);
    }

    public static IAttribute CreateTreeAttribute(byte attrId)
    {
        return OptimumTreeAttributeCodec.CreateBuiltInOrMappedAttribute(attrId);
    }

    public static TreeAttribute CreateTreeFromBytes(byte[] blockEntityData)
    {
        if (!OptimumConfig.SpanBlockEntitySerializationEnabled || blockEntityData == null)
        {
            TreeAttribute fallback = new TreeAttribute();
            using (MemoryStream ms = new MemoryStream(blockEntityData!))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                fallback.FromBytes(reader);
            }
            return fallback;
        }
        return OptimumTreeAttributeCodec.CreateTreeFromBytes(blockEntityData);
    }

    public static void ReadTreeFromBytes(TreeAttribute tree, byte[] data)
    {
        if (!OptimumConfig.SpanBlockEntitySerializationEnabled || data == null)
        {
            using (MemoryStream ms = new MemoryStream(data!))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                tree.FromBytes(reader);
            }
            return;
        }
        OptimumTreeAttributeCodec.ReadTreeFromBytes(tree, data);
    }

    public static byte[] TreeToBytes(TreeAttribute tree)
    {
        if (!OptimumConfig.SpanBlockEntitySerializationEnabled)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    tree.ToBytes(writer);
                }
                return ms.ToArray();
            }
        }
        return OptimumTreeAttributeCodec.TreeToBytes(tree);
    }

    public static void RegisterUnknownBlockModelData(ITesselatorManager? tesselatorManager, MeshData? unknownBlockModelData)
    {
        if (tesselatorManager == null || unknownBlockModelData == null)
        {
            return;
        }

        UnknownBlockModelDataByTesselator.AddOrUpdate(tesselatorManager, unknownBlockModelData);
    }

    public static void UnregisterUnknownBlockModelData(ITesselatorManager? tesselatorManager)
    {
        if (tesselatorManager == null)
        {
            return;
        }

        UnknownBlockModelDataByTesselator.Remove(tesselatorManager);
    }

    public static bool IsEligibleChiselLodBlock(Block? block)
    {
        if (block == null || block.IsMissing || block.Id <= 0 || block.BlockId <= 0)
        {
            return false;
        }

        if (block.Code == null || string.Equals(block.Code.Path, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (block.DrawType == EnumDrawType.Empty ||
            block.BlockMaterial == EnumBlockMaterial.Air ||
            block.BlockMaterial == EnumBlockMaterial.Meta)
        {
            return false;
        }

        return block.RenderPass == EnumChunkRenderPass.Opaque &&
               (block.VertexFlags == null || block.VertexFlags.GlowLevel == 0);
    }

    public static bool IsPlaceholderChiselLodMesh(ICoreClientAPI? capi, MeshData? mesh)
    {
        if (mesh == null ||
            mesh.VerticesCount <= 0 ||
            mesh.IndicesCount <= 0 ||
            mesh.XyzFaces == null ||
            mesh.XyzFacesCount <= 0 ||
            mesh.Uv == null ||
            mesh.TextureIds == null ||
            mesh.TextureIds.Length == 0)
        {
            return true;
        }

        ITesselatorManager? tesselatorManager = capi?.TesselatorManager;
        if (tesselatorManager != null &&
            UnknownBlockModelDataByTesselator.TryGetValue(tesselatorManager, out MeshData? unknownMesh) &&
            object.ReferenceEquals(mesh, unknownMesh))
        {
            return true;
        }

        TextureAtlasPosition? unknownTex = capi?.BlockTextureAtlas?.UnknownTexturePosition;
        if (unknownTex != null && MeshUsesAtlasPosition(mesh, unknownTex))
        {
            return true;
        }

        return false;
    }

    private static bool MeshUsesAtlasPosition(MeshData mesh, TextureAtlasPosition pos)
    {
        int[]? texIds = mesh.TextureIds;
        float[]? uv = mesh.Uv;
        if (texIds == null || texIds.Length == 0 || uv == null)
        {
            return false;
        }

        float minU = Math.Min(pos.x1, pos.x2);
        float maxU = Math.Max(pos.x1, pos.x2);
        float minV = Math.Min(pos.y1, pos.y2);
        float maxV = Math.Max(pos.y1, pos.y2);
        if (maxU <= minU || maxV <= minV)
        {
            return false;
        }

        int targetAtlasId = pos.atlasTextureId;
        bool hasTargetAtlas = false;
        for (int i = 0; i < texIds.Length; i++)
        {
            if (texIds[i] == targetAtlasId)
            {
                hasTargetAtlas = true;
                break;
            }
        }

        if (!hasTargetAtlas)
        {
            return false;
        }

        int vpf = mesh.VerticesPerFace;
        if (vpf <= 0)
        {
            return false;
        }

        int faceCount = Math.Min(mesh.XyzFacesCount, mesh.VerticesCount / vpf);
        faceCount = Math.Min(faceCount, uv.Length / (vpf * 2));
        byte[]? texIndices = mesh.TextureIndices;
        int texIndicesLimit = texIndices != null ? Math.Min(mesh.TextureIndicesCount, texIndices.Length) : 0;

        for (int f = 0; f < faceCount; f++)
        {
            byte texIdx = (texIndices != null && f < texIndicesLimit) ? texIndices[f] : (byte)0;
            if ((uint)texIdx >= (uint)texIds.Length || texIds[texIdx] != targetAtlasId)
            {
                continue;
            }

            int baseUv = f * vpf * 2;
            bool allVerticesInUnknownBox = true;
            for (int v = 0; v < vpf; v++)
            {
                float u = uv[baseUv + v * 2];
                float w = uv[baseUv + v * 2 + 1];
                if (u < minU || u > maxU || w < minV || w > maxV)
                {
                    allVerticesInUnknownBox = false;
                    break;
                }
            }

            if (allVerticesInUnknownBox)
            {
                return true;
            }
        }

        return false;
    }
}
