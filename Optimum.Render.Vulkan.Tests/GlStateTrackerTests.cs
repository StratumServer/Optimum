using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Covers the emulated GL state machine and the pipeline key it resolves into.
///
/// State bugs are the quiet kind: a wrong blend factor or a key that collides
/// does not crash, it just renders subtly wrong somewhere deep in a scene. These
/// pin the translations against the vanilla behaviour they have to reproduce.
/// </summary>
public class GlStateTrackerTests
{
    // ------------------------------------------------------------ blend modes

    /// <summary>
    /// The factor pairs come straight from ClientPlatformWindows.GlToggleBlend.
    /// Every one of the game's named modes has to land on the same pair it had
    /// under GL, or transparency and glow render differently.
    /// </summary>
    [Theory]
    [InlineData(EnumBlendMode.Standard, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha)]
    [InlineData(EnumBlendMode.Brighten, BlendFactor.DstColor, BlendFactor.One)]
    [InlineData(EnumBlendMode.Multiply, BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha)]
    [InlineData(EnumBlendMode.PremultipliedAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha)]
    [InlineData(EnumBlendMode.Glow, BlendFactor.SrcAlpha, BlendFactor.One)]
    [InlineData(EnumBlendMode.Overlay, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha)]
    public void NamedBlendModesMatchTheVanillaFactorPairs(
        EnumBlendMode mode, BlendFactor expectedSrc, BlendFactor expectedDst)
    {
        var tracker = new GlStateTracker();
        tracker.SetBlend(true, mode);

        AttachmentBlend blend = tracker.BlendFor(0);
        Assert.True(blend.Enabled);
        Assert.Equal(expectedSrc, blend.SrcColor);
        Assert.Equal(expectedDst, blend.DstColor);
    }

    /// <summary>
    /// Glow and Overlay use BlendFuncSeparate in vanilla, so their alpha factors
    /// differ from their colour ones.
    /// </summary>
    [Fact]
    public void SeparateAlphaBlendModesKeepTheirDistinctAlphaFactors()
    {
        var tracker = new GlStateTracker();

        tracker.SetBlend(true, EnumBlendMode.Glow);
        Assert.Equal(BlendFactor.One, tracker.BlendFor(0).SrcAlpha);
        Assert.Equal(BlendFactor.Zero, tracker.BlendFor(0).DstAlpha);

        tracker.SetBlend(true, EnumBlendMode.Overlay);
        Assert.Equal(BlendFactor.One, tracker.BlendFor(0).SrcAlpha);
        Assert.Equal(BlendFactor.One, tracker.BlendFor(0).DstAlpha);

        tracker.SetBlend(true, EnumBlendMode.Multiply);
        Assert.Equal(BlendFactor.One, tracker.BlendFor(0).SrcAlpha);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, tracker.BlendFor(0).DstAlpha);
    }

    /// <summary>
    /// GL's colour mask is global and Vulkan's is per attachment, so setting it
    /// has to reach every one of them.
    /// </summary>
    [Fact]
    public void ColorMaskAppliesToEveryAttachment()
    {
        var tracker = new GlStateTracker();
        tracker.SetColorMask(true, false, true, false);

        for (int attachment = 0; attachment < GlStateTracker.MaxColorAttachments; attachment++)
        {
            ColorComponentFlags mask = tracker.BlendFor(attachment).WriteMask;
            Assert.Equal(ColorComponentFlags.RBit | ColorComponentFlags.BBit, mask);
        }
    }

    /// <summary>
    /// SystemRenderOITLayers sets blend per attachment. Touching one must not
    /// disturb its neighbours.
    /// </summary>
    [Fact]
    public void PerAttachmentBlendLeavesOtherAttachmentsAlone()
    {
        var tracker = new GlStateTracker();
        tracker.SetBlend(true, EnumBlendMode.Standard);

        // GL_ONE, GL_ONE on attachment 3, as the OIT accumulation pass sets.
        tracker.SetAttachmentBlendFunc(3, 1, 1, 1, 1);

        Assert.Equal(BlendFactor.One, tracker.BlendFor(3).SrcColor);
        Assert.Equal(BlendFactor.One, tracker.BlendFor(3).DstColor);
        Assert.Equal(BlendFactor.SrcAlpha, tracker.BlendFor(0).SrcColor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, tracker.BlendFor(0).DstColor);
    }

    // -------------------------------------------------------------- interning

    [Fact]
    public void IdenticalBlendStateInternsToTheSameId()
    {
        var tracker = new GlStateTracker();

        tracker.SetBlend(true, EnumBlendMode.Standard);
        int first = tracker.BlendId(2);

        tracker.SetBlend(false, EnumBlendMode.Standard);
        tracker.SetBlend(true, EnumBlendMode.Standard);
        int second = tracker.BlendId(2);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ChangingBlendStateProducesADifferentId()
    {
        var tracker = new GlStateTracker();

        tracker.SetBlend(true, EnumBlendMode.Standard);
        int standard = tracker.BlendId(2);

        tracker.SetBlend(true, EnumBlendMode.Glow);
        int glow = tracker.BlendId(2);

        Assert.NotEqual(standard, glow);
    }

    /// <summary>
    /// The id is cached between changes so a run of draws sharing state pays
    /// nothing, but a change has to invalidate it. Getting this wrong would pin
    /// the wrong pipeline for every subsequent draw.
    /// </summary>
    [Fact]
    public void EveryMutatorInvalidatesTheCachedBlendId()
    {
        var mutations = new (string Name, Action<GlStateTracker> Apply)[]
        {
            ("SetBlend", t => t.SetBlend(true, EnumBlendMode.Glow)),
            ("SetColorMask", t => t.SetColorMask(true, true, false, true)),
            ("SetAttachmentBlendFunc", t => t.SetAttachmentBlendFunc(0, 1, 1, 1, 1)),
            ("SetAttachmentBlendEquation", t => t.SetAttachmentBlendEquation(0, 0x800A)),
        };

        foreach ((string name, Action<GlStateTracker> apply) in mutations)
        {
            var tracker = new GlStateTracker();
            int before = tracker.BlendId(4);
            apply(tracker);
            Assert.True(before != tracker.BlendId(4), $"{name} did not invalidate the cached blend id");
        }
    }

    /// <summary>
    /// The packing squeezes eight fields into 32 bits. A collision there would
    /// silently merge two different blend states onto one pipeline.
    /// </summary>
    [Fact]
    public void AttachmentBlendPackingIsCollisionFreeAcrossTheUsedRange()
    {
        var seen = new Dictionary<uint, AttachmentBlend>();
        var factors = new[]
        {
            BlendFactor.Zero, BlendFactor.One, BlendFactor.SrcAlpha,
            BlendFactor.OneMinusSrcAlpha, BlendFactor.DstColor, BlendFactor.SrcAlphaSaturate,
        };
        var ops = new[] { BlendOp.Add, BlendOp.Subtract, BlendOp.ReverseSubtract, BlendOp.Min, BlendOp.Max };

        foreach (bool enabled in new[] { false, true })
        foreach (BlendFactor srcColor in factors)
        foreach (BlendFactor dstColor in factors)
        foreach (BlendOp colorOp in ops)
        foreach (BlendFactor srcAlpha in factors)
        {
            var blend = new AttachmentBlend
            {
                Enabled = enabled,
                SrcColor = srcColor,
                DstColor = dstColor,
                ColorOp = colorOp,
                SrcAlpha = srcAlpha,
                DstAlpha = BlendFactor.One,
                AlphaOp = BlendOp.Add,
                WriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };

            uint packed = blend.Pack();
            if (seen.TryGetValue(packed, out AttachmentBlend existing))
            {
                Assert.True(existing.Equals(blend), $"packing collision at 0x{packed:X8}");
            }
            seen[packed] = blend;
        }

        Assert.True(seen.Count > 1000, "the sweep should have covered a wide range");
    }

    // ------------------------------------------------------------ pipeline key

    [Fact]
    public void PipelineKeysCompareByValue()
    {
        var a = new PipelineKey(1, 2, 3, 4, PolygonMode.Fill, 2);
        var b = new PipelineKey(1, 2, 3, 4, PolygonMode.Fill, 2);
        var c = new PipelineKey(1, 2, 3, 4, PolygonMode.Line, 2);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    /// <summary>
    /// Dynamic state must not reach the key: if it did, every viewport or depth
    /// change would compile a new pipeline.
    /// </summary>
    [Fact]
    public void DynamicStateDoesNotChangeThePipelineKey()
    {
        var tracker = new GlStateTracker();
        int target = tracker.InternTargetFormats(
            new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.D32Sfloat));

        PipelineKey before = tracker.BuildKey(vertexLayoutId: 7, target, attachmentCount: 1);

        tracker.SetViewport(0, 0, 1920, 1080);
        tracker.SetScissor(10, 10, 100, 100);
        tracker.SetScissorEnabled(true);
        tracker.SetDepthTest(true);
        tracker.SetDepthWrite(false);
        tracker.SetDepthFunc(0x0203);
        tracker.SetCullEnabled(true);
        tracker.SetCullBack(false);
        tracker.SetStencilTest(true);
        tracker.SetStencilFunc(0x0202, 1, 0xFF);
        tracker.SetStencilOp(0x1E00, 0x1E00, 0x1E01);
        tracker.SetLineWidth(2.5f);

        Assert.Equal(before, tracker.BuildKey(vertexLayoutId: 7, target, attachmentCount: 1));
    }

    [Fact]
    public void PipelineStateDoesChangeThePipelineKey()
    {
        var tracker = new GlStateTracker();
        int target = tracker.InternTargetFormats(
            new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.D32Sfloat));

        PipelineKey before = tracker.BuildKey(vertexLayoutId: 7, target, attachmentCount: 1);

        tracker.SetWireframe(true);
        Assert.NotEqual(before, tracker.BuildKey(vertexLayoutId: 7, target, attachmentCount: 1));

        tracker.SetWireframe(false);
        tracker.SetProgram(42);
        Assert.NotEqual(before, tracker.BuildKey(vertexLayoutId: 7, target, attachmentCount: 1));
    }

    /// <summary>
    /// Topology is dynamic within a class but not across one, so lines and
    /// triangles need separate pipelines while line list and line strip share.
    /// </summary>
    [Fact]
    public void TopologyClassSeparatesLinesFromTrianglesButNotLineStrips()
    {
        var tracker = new GlStateTracker();
        int target = tracker.InternTargetFormats(
            new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.D32Sfloat));

        tracker.SetTopology(EnumDrawMode.Triangles);
        PipelineKey triangles = tracker.BuildKey(0, target, 1);

        tracker.SetTopology(EnumDrawMode.Lines);
        PipelineKey lines = tracker.BuildKey(0, target, 1);

        tracker.SetTopology(EnumDrawMode.LineStrip);
        PipelineKey lineStrip = tracker.BuildKey(0, target, 1);

        Assert.NotEqual(triangles, lines);
        Assert.Equal(lines, lineStrip);
    }

    [Fact]
    public void RenderTargetFormatsInternByValue()
    {
        var tracker = new GlStateTracker();

        int first = tracker.InternTargetFormats(
            new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm, Format.R16G16B16A16Sfloat }, Format.D32Sfloat));
        int same = tracker.InternTargetFormats(
            new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm, Format.R16G16B16A16Sfloat }, Format.D32Sfloat));
        int different = tracker.InternTargetFormats(
            new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.D32Sfloat));

        Assert.Equal(first, same);
        Assert.NotEqual(first, different);
    }

    // ------------------------------------------------------------- translations

    [Theory]
    [InlineData(0x0200, CompareOp.Never)]
    [InlineData(0x0201, CompareOp.Less)]
    [InlineData(0x0202, CompareOp.Equal)]
    [InlineData(0x0203, CompareOp.LessOrEqual)]
    [InlineData(0x0204, CompareOp.Greater)]
    [InlineData(0x0205, CompareOp.NotEqual)]
    [InlineData(0x0206, CompareOp.GreaterOrEqual)]
    [InlineData(0x0207, CompareOp.Always)]
    public void GlComparisonConstantsMapToVulkanCompareOps(int glFunc, CompareOp expected)
    {
        Assert.Equal(expected, GlEnums.CompareOpFrom(glFunc));
    }

    /// <summary>
    /// GL folds the mipmap mode into the minification filter constant; Vulkan
    /// splits them. LINEAR_MIPMAP_LINEAR is trilinear, which the block atlas
    /// relies on.
    /// </summary>
    [Theory]
    [InlineData(0x2600, Filter.Nearest, SamplerMipmapMode.Nearest)]
    [InlineData(0x2601, Filter.Linear, SamplerMipmapMode.Nearest)]
    [InlineData(0x2703, Filter.Linear, SamplerMipmapMode.Linear)]
    [InlineData(0x2702, Filter.Nearest, SamplerMipmapMode.Linear)]
    public void GlMinificationFiltersSplitIntoFilterAndMipmapMode(
        int glFilter, Filter expectedFilter, SamplerMipmapMode expectedMode)
    {
        (Filter filter, SamplerMipmapMode mode) = GlEnums.MinFilterFrom(glFilter);
        Assert.Equal(expectedFilter, filter);
        Assert.Equal(expectedMode, mode);
    }

    /// <summary>
    /// RGB has no guaranteed colour-attachment support in Vulkan, so the vanilla
    /// RGB8 revealage target is promoted to RGBA8 rather than failing.
    /// </summary>
    [Fact]
    public void ThreeChannelFormatsArePromotedToFourChannels()
    {
        Assert.Equal(Format.R8G8B8A8Unorm, GlEnums.TextureFormatFromGl(0x8051));
        Assert.Equal(Format.R8G8B8A8Unorm, GlEnums.TextureFormatFromGl(0x1907));
    }

    [Fact]
    public void DepthAndFloatFormatsMapExactly()
    {
        Assert.Equal(Format.D32Sfloat, GlEnums.TextureFormatFromGl(0x8DAB));
        Assert.Equal(Format.R16G16B16A16Sfloat, GlEnums.TextureFormatFromGl(0x881A));
        Assert.Equal(Format.R16Sfloat, GlEnums.TextureFormatFromGl(0x822D));
        Assert.Equal(Format.R32G32B32A32Sfloat, GlEnums.TextureFormatFromGl(0x8814));
    }

    /// <summary>
    /// Not a preference: GL's counter-clockwise front face, read in a Vulkan
    /// framebuffer that was never flipped, is clockwise. The game never calls
    /// glFrontFace, so this is a constant and flipping it would invert culling
    /// everywhere.
    /// </summary>
    [Fact]
    public void FrontFaceIsClockwiseToMatchUnflippedGlWinding()
    {
        Assert.Equal(FrontFace.Clockwise, GlStateTracker.FrontFace);
    }

    /// <summary>
    /// The game scissors dialogs that run off the top of the screen, which gives
    /// glScissor a negative y. GL clips such a rectangle and keeps the visible
    /// part; Vulkan rejects the negative offset and drops the draw, so the same
    /// region has to be expressed without one.
    /// </summary>
    [Fact]
    public void ANegativeScissorOriginIsClippedToTheSameVisibleRegion()
    {
        var tracker = new GlStateTracker();

        tracker.SetScissor(-20, -72, 300, 200);

        Assert.Equal(0, tracker.Scissor.Offset.X);
        Assert.Equal(0, tracker.Scissor.Offset.Y);

        // The rectangle still ends where it did: -20 + 300 and -72 + 200.
        Assert.Equal(280u, tracker.Scissor.Extent.Width);
        Assert.Equal(128u, tracker.Scissor.Extent.Height);
    }

    [Fact]
    public void AScissorEntirelyOffscreenBecomesEmptyRatherThanNegative()
    {
        var tracker = new GlStateTracker();

        tracker.SetScissor(-50, -50, 20, 20);

        Assert.Equal(0, tracker.Scissor.Offset.X);
        Assert.Equal(0, tracker.Scissor.Offset.Y);
        Assert.Equal(0u, tracker.Scissor.Extent.Width);
        Assert.Equal(0u, tracker.Scissor.Extent.Height);
    }

    [Fact]
    public void ResetRestoresTheDefaultsAFreshContextWouldHave()
    {
        var tracker = new GlStateTracker();
        tracker.SetDepthTest(true);
        tracker.SetWireframe(true);
        tracker.SetBlend(true, EnumBlendMode.Glow);
        tracker.SetProgram(9);

        tracker.Reset();

        Assert.False(tracker.DepthTest);
        Assert.True(tracker.DepthWrite);
        Assert.Equal(PolygonMode.Fill, tracker.PolygonMode);
        Assert.Equal(CompareOp.Less, tracker.DepthCompare);
        Assert.Equal(0, tracker.CurrentProgram);
        Assert.False(tracker.BlendFor(0).Enabled);
    }
}
