using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The values poison mode (OPTIMUM_VULKAN_POISON=1) writes into fresh resources.
///
/// OpenGL and Vulkan both leave new storage undefined, but in practice GL
/// drivers hand out zeroed memory and Vulkan allocators hand out whatever the
/// previous tenant left. A read of never-written content therefore "works" on
/// one backend and flickers on the other. Poison makes such a read loud and
/// identical every frame: NaN for float formats, magenta (alpha 1) for
/// normalised and sRGB colour, 0xDEADBEEF for integer formats and host memory,
/// 0.5 for depth.
/// </summary>
internal static unsafe class VulkanPoison
{
    public const uint Word = 0xDEADBEEF;
    public const float Depth = 0.5f;

    public static bool IsCompressed(Format format) =>
        format.ToString().Contains("Block", StringComparison.Ordinal);

    public static bool IsFloat(Format format)
    {
        string name = format.ToString();
        return name.Contains("Sfloat", StringComparison.Ordinal) || name.Contains("Ufloat", StringComparison.Ordinal);
    }

    public static bool IsInteger(Format format)
    {
        string name = format.ToString();
        return name.Contains("Uint", StringComparison.Ordinal) || name.Contains("Sint", StringComparison.Ordinal);
    }

    public static ClearColorValue ColorFor(Format format)
    {
        var value = new ClearColorValue();
        if (IsFloat(format))
        {
            value.Float32_0 = float.NaN;
            value.Float32_1 = float.NaN;
            value.Float32_2 = float.NaN;
            value.Float32_3 = float.NaN;
        }
        else if (IsInteger(format))
        {
            // Uint and Sint clears read the same union bits.
            value.Uint32_0 = Word;
            value.Uint32_1 = Word;
            value.Uint32_2 = Word;
            value.Uint32_3 = Word;
        }
        else
        {
            value.Float32_0 = 1f;
            value.Float32_1 = 0f;
            value.Float32_2 = 1f;
            value.Float32_3 = 1f;
        }
        return value;
    }

    /// <summary>Writes 0xDEADBEEF as little-endian words over the whole range, a partial word at the tail.</summary>
    public static void FillHostMemory(IntPtr memory, ulong size)
    {
        byte* bytes = (byte*)memory;
        ulong words = size / 4;
        uint* wordPointer = (uint*)bytes;
        for (ulong i = 0; i < words; i++) wordPointer[i] = Word;
        for (ulong i = words * 4; i < size; i++) bytes[i] = (byte)(Word >> (int)(8 * (i % 4)));
    }
}
