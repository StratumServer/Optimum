using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 2 contract C4 without a device: tier selection and its env override,
/// the effective write mask, how each tier keys pipelines, and the dirty bits the
/// dynamic tiers add.
/// </summary>
public class ColorWriteTierTests
{
    private const ColorComponentFlags Rgba =
        ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    // Tiers travel as their env tokens: the enum is internal to the backend.
    [Theory]
    [InlineData(true, true, null, "enable")]
    [InlineData(false, true, null, "mask")]
    [InlineData(false, false, null, "pipeline")]
    [InlineData(true, true, "mask", "mask")]
    [InlineData(true, true, "pipeline", "pipeline")]
    [InlineData(true, false, "mask", "pipeline")]
    [InlineData(false, true, "enable", "mask")]
    public void TheBestSupportedTierAtOrBelowTheForcedOneIsSelected(
        bool enable, bool mask, string? forced, string expected)
    {
        ColorWriteTier selected = DeviceCaps.SelectColorWriteTier(enable, mask, DeviceCaps.ParseColorWriteTier(forced));
        Assert.Equal(expected, DeviceCaps.Token(selected));
    }

    [Theory]
    [InlineData("enable", "enable")]
    [InlineData(" MASK ", "mask")]
    [InlineData("pipeline", "pipeline")]
    [InlineData("", null)]
    [InlineData("bogus", null)]
    [InlineData(null, null)]
    public void TheOverrideParsesItsThreeTokens(string? value, string? expected)
    {
        ColorWriteTier? parsed = DeviceCaps.ParseColorWriteTier(value);
        Assert.Equal(expected, parsed == null ? null : DeviceCaps.Token(parsed.Value));
    }

    [Fact]
    public void ColorWriteAndBlendChangesAreTheirOwnDirtyBits()
    {
        var cache = new DynamicStateCache();
        var values = new DynamicStateValues { ColorWrite = 0b011, BlendStateId = 4, LineWidth = 1f };

        Assert.Equal(DynamicStateDirty.Everything, cache.Update(7, values));
        Assert.Equal(DynamicStateDirty.None, cache.Update(7, values));

        values.ColorWrite = 0b111;
        Assert.Equal(DynamicStateDirty.ColorWrite, cache.Update(7, values));

        values.BlendStateId = 5;
        Assert.Equal(DynamicStateDirty.ColorBlend, cache.Update(7, values));

        // The core set is unchanged: the per-draw constant still counts only it.
        Assert.Equal(VulkanStats.DynamicStateCommandsPerDraw, DynamicStateCache.CommandCount(DynamicStateDirty.All));
        Assert.Equal(DynamicStateDirty.None, DynamicStateDirty.All & (DynamicStateDirty.ColorWrite | DynamicStateDirty.ColorBlend));
    }
}
