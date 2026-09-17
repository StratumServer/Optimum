using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Which format a storage image is created in. A compute pass names the format it
/// wants (the AO working term wants R8_UNORM, the prefiltered depth R32F); a device
/// that cannot use that format as a storage image and sample it gets the first
/// wider format of the same kind that it can, ending in RGBA8 for unsigned
/// normalised formats and RGBA32F for float ones (the formats Vulkan guarantees
/// storage support for). A shader writing the first channel of the wider format
/// reads the same value back, so a fallback costs memory, never correctness.
///
/// Pure: the feature lookup is passed in, so the choice is testable without a device.
/// </summary>
internal static class StorageFormats
{
    /// <summary>What a storage image must support: storage writes and sampling.</summary>
    public const FormatFeatureFlags Required = FormatFeatureFlags.StorageImageBit | FormatFeatureFlags.SampledImageBit;

    /// <summary>The candidates for <paramref name="requested" />, the requested format first.</summary>
    public static IReadOnlyList<Format> CandidatesFor(Format requested) => requested switch
    {
        Format.R8Unorm => new[] { Format.R8Unorm, Format.R8G8Unorm, Format.R8G8B8A8Unorm },
        Format.R8G8Unorm => new[] { Format.R8G8Unorm, Format.R8G8B8A8Unorm },
        Format.R16Unorm => new[] { Format.R16Unorm, Format.R16G16B16A16Unorm, Format.R8G8B8A8Unorm },
        Format.R16Sfloat => new[] { Format.R16Sfloat, Format.R32Sfloat, Format.R16G16B16A16Sfloat, Format.R32G32B32A32Sfloat },
        Format.R32Sfloat => new[] { Format.R32Sfloat, Format.R32G32B32A32Sfloat },
        Format.R16G16Sfloat => new[] { Format.R16G16Sfloat, Format.R16G16B16A16Sfloat, Format.R32G32B32A32Sfloat },
        Format.R16G16B16A16Sfloat => new[] { Format.R16G16B16A16Sfloat, Format.R32G32B32A32Sfloat },
        Format.R8G8B8A8Unorm => new[] { Format.R8G8B8A8Unorm },
        _ => new[] { requested, Format.R8G8B8A8Unorm },
    };

    /// <summary>
    /// The first candidate whose optimal-tiling features include <see cref="Required" />;
    /// RGBA8 when none does (every Vulkan device supports it as a storage image).
    /// </summary>
    public static Format Choose(Format requested, Func<Format, FormatFeatureFlags> optimalFeatures)
    {
        foreach (Format candidate in CandidatesFor(requested))
        {
            if ((optimalFeatures(candidate) & Required) == Required) return candidate;
        }
        return Format.R8G8B8A8Unorm;
    }

    /// <summary>Whether a colour attachment usage may be added: the format must support it.</summary>
    public static bool SupportsColorAttachment(FormatFeatureFlags features) =>
        (features & FormatFeatureFlags.ColorAttachmentBit) != 0;
}
