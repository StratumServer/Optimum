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
    public void TheEffectiveMaskIsTheColorMaskOnlyWhereTheDrawBufferIsOnAndTheOutputWritten()
    {
        var tracker = new GlStateTracker();
        tracker.SetColorMask(true, true, false, true);
        uint written = GlStateTracker.OutputBits(new HashSet<int> { 0, 1, 2 });
        Assert.Equal(0b111u, written);

        ColorComponentFlags rgA = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.ABit;
        Assert.Equal(rgA, tracker.EffectiveWriteMask(0, 0b101, written));
        Assert.Equal((ColorComponentFlags)0, tracker.EffectiveWriteMask(1, 0b101, written)); // draw buffer off
        Assert.Equal(rgA, tracker.EffectiveWriteMask(2, 0b101, written));
        Assert.Equal((ColorComponentFlags)0, tracker.EffectiveWriteMask(3, 0b1111, written)); // not written
    }

    /// <summary>The key tier with every draw buffer selected keys exactly as before C4.</summary>
    [Fact]
    public void TheKeyTierWithAllDrawBuffersKeysLikeTheLegacyBlendId()
    {
        var tracker = new GlStateTracker();
        tracker.SetBlend(true, EnumBlendMode.Standard);
        tracker.SetAttachmentBlendFunc(1, 1, 1, 1, 1);

        Assert.Equal(tracker.BlendId(3), tracker.PipelineBlendId(3, uint.MaxValue));
        Assert.Equal(tracker.BuildKey(1, 2, 3), tracker.BuildKey(1, 2, 3, 0b111));
        Assert.NotEqual(tracker.BuildKey(1, 2, 3), tracker.BuildKey(1, 2, 3, 0b011));
        Assert.Equal((ColorComponentFlags)0, tracker.PipelineBlendFor(2, 0b011).WriteMask);
        Assert.Equal(Rgba, tracker.PipelineBlendFor(1, 0b011).WriteMask);
    }

    [Fact]
    public void TheEnableTierKeepsTheKeyAcrossDrawBufferChanges()
    {
        var tracker = new GlStateTracker { ColorWriteTier = ColorWriteTier.DynamicEnable };
        tracker.SetBlend(true, EnumBlendMode.Standard);

        PipelineKey all = tracker.BuildKey(1, 2, 3, 0b111);
        Assert.Equal(all, tracker.BuildKey(1, 2, 3, 0b011));
        Assert.Equal(all, tracker.BuildKey(1, 2, 3, 0b100));
        Assert.Equal(Rgba, tracker.PipelineBlendFor(2, 0b011).WriteMask);

        // glColorMask stays baked in this tier.
        tracker.SetColorMask(true, true, true, false);
        Assert.NotEqual(all, tracker.BuildKey(1, 2, 3, 0b111));
    }

    [Fact]
    public void TheMaskTierKeepsTheKeyAcrossDrawBufferAndColorMaskChanges()
    {
        var tracker = new GlStateTracker { ColorWriteTier = ColorWriteTier.DynamicMask };
        tracker.SetBlend(true, EnumBlendMode.Standard);

        PipelineKey all = tracker.BuildKey(1, 2, 3, 0b111);
        Assert.Equal(all, tracker.BuildKey(1, 2, 3, 0b001));
        tracker.SetColorMask(false, false, false, false);
        Assert.Equal(all, tracker.BuildKey(1, 2, 3, 0b001));

        // Blend factors stay in the key unless blend is dynamic too.
        tracker.SetAttachmentBlendFunc(2, 1, 1, 1, 1);
        PipelineKey additive = tracker.BuildKey(1, 2, 3, 0b111);
        Assert.NotEqual(all, additive);

        tracker.DynamicBlend = true;
        PipelineKey dynamicBlend = tracker.BuildKey(1, 2, 3, 0b111);
        tracker.SetBlend(false, EnumBlendMode.Glow);
        tracker.SetAttachmentBlendEquation(0, 0x800A);
        Assert.Equal(dynamicBlend, tracker.BuildKey(1, 2, 3, 0b010));
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
