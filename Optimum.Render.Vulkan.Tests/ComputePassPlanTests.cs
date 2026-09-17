using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The compute pass kind without a device: the compute usages' layouts, stages and
/// accesses; the barriers a storage write, a sampled read and a mip chain derive from
/// them; declaration validation; group counts from an image; the signature the frame
/// plan sees; and the storage format fallback.
/// </summary>
public class ComputePassPlanTests
{
    private static List<ImageTransition> Require(ResourceStateTracker tracker, uint baseMip, uint mipCount,
        ResourceUsage usage)
    {
        var output = new List<ImageTransition>();
        int count = tracker.Require(baseMip, mipCount, 0, tracker.Layers, usage, false, output);
        Assert.Equal(output.Count, count);
        return output;
    }

    private static List<ImageTransition> Require(ResourceStateTracker tracker, ResourceUsage usage) =>
        Require(tracker, 0, tracker.MipLevels, usage);

    [Theory]
    [InlineData(ResourceUsage.SampleCompute, ImageLayout.ShaderReadOnlyOptimal, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.StorageReadCompute, ImageLayout.General, AccessFlags2.ShaderStorageReadBit)]
    [InlineData(ResourceUsage.StorageWrite, ImageLayout.General, AccessFlags2.ShaderStorageWriteBit)]
    [InlineData(ResourceUsage.StorageReadWrite, ImageLayout.General, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit)]
    public void ComputeUsagesAreGeneralForStorageAndShaderReadOnlyForSampling(ResourceUsage usage, ImageLayout layout,
        AccessFlags2 access)
    {
        Assert.Equal(new UsageState(layout, PipelineStageFlags2.ComputeShaderBit, access), UsageState.For(usage, depth: false));
    }

    [Theory]
    [InlineData(ComputeAccess.Sampled, ResourceUsage.SampleCompute)]
    [InlineData(ComputeAccess.StorageRead, ResourceUsage.StorageReadCompute)]
    [InlineData(ComputeAccess.StorageWrite, ResourceUsage.StorageWrite)]
    [InlineData(ComputeAccess.StorageReadWrite, ResourceUsage.StorageReadWrite)]
    public void EveryAccessHasItsUsage(ComputeAccess access, ResourceUsage usage) =>
        Assert.Equal(usage, ComputePassPlanner.UsageOf(access));

    [Fact]
    public void AStorageWriteThenASampledReadMakesTheWriteAvailableToTheFragmentShader()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        ImageTransition first = Assert.Single(Require(tracker, ResourceUsage.StorageWrite));
        Assert.Equal(ImageLayout.Undefined, first.Sides.OldLayout);
        Assert.Equal(ImageLayout.General, first.Sides.NewLayout);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, first.Sides.DstAccess);

        // The next raster pass samples it: GENERAL -> SHADER_READ_ONLY naming the dispatch's write.
        ImageTransition read = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal(ImageLayout.General, read.Sides.OldLayout);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, read.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.ComputeShaderBit, read.Sides.SrcStage);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, read.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, read.Sides.DstStage);

        // The frame after writes it again: back to GENERAL, naming the fragment read.
        ImageTransition again = Assert.Single(Require(tracker, ResourceUsage.StorageWrite));
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, again.Sides.OldLayout);
        Assert.Equal(ImageLayout.General, again.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, again.Sides.SrcStage);
        Assert.Equal(AccessFlags2.ShaderSampledReadBit, again.Sides.SrcAccess);
    }

    [Fact]
    public void RepeatedStorageWritesAndStorageReadsInGeneralNeedTheBarriersTheOrderingRulesAsk()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.StorageWrite);
        // Write after write at the same stage: none.
        Assert.Empty(Require(tracker, ResourceUsage.StorageWrite));
        // Read after write at the stage the write is visible to: none.
        Assert.Empty(Require(tracker, ResourceUsage.StorageReadCompute));
        // A second compute read: none.
        Assert.Empty(Require(tracker, ResourceUsage.StorageReadCompute));
        // A compute-only write is not visible to the fragment stage: one GENERAL -> GENERAL barrier.
        Require(tracker, ResourceUsage.StorageReadWrite);
        ImageTransition fragment = Assert.Single(Require(tracker, ResourceUsage.StorageRead));
        Assert.Equal(ImageLayout.General, fragment.Sides.OldLayout);
        Assert.Equal(ImageLayout.General, fragment.Sides.NewLayout);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit, fragment.Sides.SrcAccess);
    }

    [Fact]
    public void ASampledComputeReadAfterAnUploadNeedsNoBarrierOnceTheImageIsShaderReadable()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.TransferDst);
        Require(tracker, ResourceUsage.SampleFragment);
        // The barrier into SHADER_READ_ONLY already made the upload available: read after read.
        Assert.Empty(Require(tracker, ResourceUsage.SampleCompute));
        Assert.Empty(Require(tracker, ResourceUsage.SampleCompute));
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            tracker.StateOf(0, 0).ReadStages);
    }

    [Fact]
    public void AMipChainReadsLevelNAndWritesLevelNPlusOneWithOneBarrierPerLevel()
    {
        const uint levels = 4;
        var tracker = new ResourceStateTracker(levels, 1, depth: false);

        // Level 0 is stored first (the prefilter's input copy).
        ImageTransition level0 = Assert.Single(Require(tracker, 0, 1, ResourceUsage.StorageWrite));
        Assert.Equal((0u, 1u), (level0.BaseMip, level0.MipCount));
        Assert.True(tracker.IsSplit);

        for (uint n = 0; n + 1 < levels; n++)
        {
            ImageTransition read = Assert.Single(Require(tracker, n, 1, ResourceUsage.SampleCompute));
            Assert.Equal((n, 1u), (read.BaseMip, read.MipCount));
            Assert.Equal(ImageLayout.General, read.Sides.OldLayout);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, read.Sides.NewLayout);
            Assert.Equal(AccessFlags2.ShaderStorageWriteBit, read.Sides.SrcAccess);

            ImageTransition write = Assert.Single(Require(tracker, n + 1, 1, ResourceUsage.StorageWrite));
            Assert.Equal((n + 1, 1u), (write.BaseMip, write.MipCount));
            Assert.Equal(ImageLayout.Undefined, write.Sides.OldLayout);
            Assert.Equal(ImageLayout.General, write.Sides.NewLayout);

            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.StateOf(n, 0).Layout);
            Assert.Equal(ImageLayout.General, tracker.StateOf(n + 1, 0).Layout);
        }

        // The consumer samples the whole chain: only the last level still needs a barrier.
        ImageTransition last = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal((levels - 1, 1u), (last.BaseMip, last.MipCount));
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, last.Sides.SrcAccess);
        for (uint n = 0; n < levels; n++) Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.StateOf(n, 0).Layout);

        // The next frame's chain starts over: every level leaves SHADER_READ_ONLY in one rectangle.
        ImageTransition next = Assert.Single(Require(tracker, 0, 1, ResourceUsage.StorageWrite));
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, next.Sides.OldLayout);
    }

    private static Func<int, ComputeImageInfo?> Images(params (int Id, ComputeImageInfo Info)[] images)
    {
        var map = new Dictionary<int, ComputeImageInfo>();
        foreach ((int id, ComputeImageInfo info) in images) map[id] = info;
        return id => map.TryGetValue(id, out ComputeImageInfo info) ? info : null;
    }

    private static ComputePassDeclaration Pass(params ComputeBinding[] bindings) => new()
    {
        Name = "test",
        ProgramId = 1,
        Bindings = bindings,
        Dispatches = new[] { ComputeDispatch.Covering(0) },
    };

    [Fact]
    public void AMipChainPassValidatesAndAConflictingUseOfOneLevelDoesNot()
    {
        var images = Images((5, new ComputeImageInfo(64, 32, 5, 1)), (6, new ComputeImageInfo(8, 8, 1, 4)));

        Assert.Null(ComputePassPlanner.Validate(Pass(
            new ComputeBinding(1, 5, ComputeAccess.StorageWrite, BaseMip: 1),
            new ComputeBinding(0, 5, ComputeAccess.Sampled, BaseMip: 0)), images));

        Assert.Contains("two ways", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 1),
            new ComputeBinding(1, 5, ComputeAccess.Sampled, BaseMip: 0, MipCount: 2)), images));
        Assert.Contains("two ways", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 2),
            new ComputeBinding(1, 5, ComputeAccess.StorageWrite, BaseMip: 2)), images));
        // Two sampled reads of one level are one layout: allowed.
        Assert.Null(ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.Sampled),
            new ComputeBinding(1, 5, ComputeAccess.Sampled)), images));

        Assert.Contains("exactly one level", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, MipCount: 2)), images));
        Assert.Contains("of a 5-level texture", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.Sampled, BaseMip: 4, MipCount: 2)), images));
        Assert.Contains("bound twice", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.Sampled),
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 1)), images));
        Assert.Contains("no texture", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 99, ComputeAccess.StorageWrite)), images));
        Assert.Contains("layered", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 6, ComputeAccess.StorageWrite)), images));
        Assert.Contains("no dispatch", ComputePassPlanner.Validate(new ComputePassDeclaration
        {
            Bindings = new[] { new ComputeBinding(0, 5, ComputeAccess.StorageWrite) },
        }, images));
        Assert.Contains("binding index", ComputePassPlanner.Validate(new ComputePassDeclaration
        {
            Bindings = new[] { new ComputeBinding(0, 5, ComputeAccess.StorageWrite) },
            Dispatches = new[] { ComputeDispatch.Covering(3) },
        }, images));
    }

    [Theory]
    [InlineData(64u, 8u, 8u)]
    [InlineData(65u, 8u, 9u)]
    [InlineData(1u, 16u, 1u)]
    [InlineData(1920u, 16u, 120u)]
    [InlineData(1081u, 16u, 68u)]
    public void GroupCountsCoverTheImage(uint extent, uint local, uint groups) =>
        Assert.Equal(groups, ComputePassPlanner.GroupsCovering(extent, local));

    [Fact]
    public void ADispatchSizedFromABindingCoversThatLevel()
    {
        var images = Images((5, new ComputeImageInfo(100, 30, 5, 1)));
        ComputePassDeclaration pass = Pass(new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 2));
        // Level 2 is 25x7: 4x1 groups of 8x8.
        Assert.Equal((4u, 1u, 1u), ComputePassPlanner.Groups(pass.Dispatches[0], pass, images, 8, 8));
        Assert.Equal((3u, 2u, 1u), ComputePassPlanner.Groups(ComputeDispatch.Explicit(3, 2), pass, images, 8, 8));
        Assert.Equal(1u, ComputePassPlanner.LevelExtent(3, 7));
    }

    [Fact]
    public void TheSignatureNamesWritesAsPersistentUsesAndReadsAsReads()
    {
        var images = Images((5, new ComputeImageInfo(64, 32, 5, 1)), (7, new ComputeImageInfo(64, 32, 1, 1)),
            (8, new ComputeImageInfo(64, 32, 1, 1)));
        PassSignature signature = ComputePassPlanner.Signature(3, Pass(
            new ComputeBinding(0, 7, ComputeAccess.Sampled),
            new ComputeBinding(1, 5, ComputeAccess.Sampled, BaseMip: 0),
            new ComputeBinding(2, 5, ComputeAccess.StorageWrite, BaseMip: 1),
            new ComputeBinding(3, 8, ComputeAccess.StorageReadWrite)), images);

        Assert.Equal(3, signature.NameId);
        Assert.Equal(new[] { 7, 5, 8 }, signature.Reads);
        Assert.Equal(new[]
        {
            new AttachmentUse(5, ResourceUsage.StorageWrite, false),
            new AttachmentUse(8, ResourceUsage.StorageReadWrite, false),
        }, signature.Attachments);
        Assert.Equal((32, 16), (signature.Width, signature.Height));

        // A raster pass that attaches the dispatch's output later must load it, never DONT_CARE.
        var raster = new PassSignature
        {
            NameId = 4, Width = 64, Height = 32, FormatsId = 1,
            Attachments = new[] { new AttachmentUse(8, ResourceUsage.ColorWrite, true) },
        };
        FramePlan plan = FramePlan.Build(new[] { signature, raster });
        Assert.Equal(AttachmentLoadOp.Load, plan.LoadOp(1, 0));
        Assert.Equal(-1, plan.AliasSlot(8));
    }

    [SkippableFact]
    public void TheWorkGroupSizeIsReadFromTheModule()
    {
        Shaders.ShaderCompiler compiler;
        try
        {
            compiler = new Shaders.ShaderCompiler();
        }
        catch (Exception error) when (error is DllNotFoundException or InvalidOperationException)
        {
            throw new SkipException("shaderc unavailable: " + error.Message);
        }
        using (compiler)
        {
            Shaders.ShaderCompileResult literal = compiler.CompileCompute("""
                #version 450
                layout(local_size_x = 16, local_size_y = 4, local_size_z = 2) in;
                void main() {}
                """, "literal.comp");
            Assert.True(literal.Success, literal.Error);
            Assert.True(SpirvLocalSize.TryRead(literal.Spirv, out uint x, out uint y, out uint z));
            Assert.Equal((16u, 4u, 2u), (x, y, z));

            // A specialization-constant size is not a literal: the description's size applies.
            Shaders.ShaderCompileResult specialized = compiler.CompileCompute("""
                #version 450
                layout(local_size_x_id = 0, local_size_y_id = 1) in;
                void main() {}
                """, "specialized.comp");
            Assert.True(specialized.Success, specialized.Error);
            Assert.False(SpirvLocalSize.TryRead(specialized.Spirv, out _, out _, out _));
        }
        Assert.False(SpirvLocalSize.TryRead(new byte[16], out _, out _, out _));
    }

    [Fact]
    public void AStorageFormatFallsBackToAWiderFormatOfItsKindAndFinallyRgba8()
    {
        const FormatFeatureFlags storage = StorageFormats.Required;
        Func<Format, FormatFeatureFlags> only(params Format[] supported) =>
            format => Array.IndexOf(supported, format) >= 0 ? storage : FormatFeatureFlags.SampledImageBit;

        Assert.Equal(Format.R8Unorm, StorageFormats.Choose(Format.R8Unorm, only(Format.R8Unorm, Format.R8G8B8A8Unorm)));
        Assert.Equal(Format.R8G8B8A8Unorm, StorageFormats.Choose(Format.R8Unorm, only(Format.R8G8B8A8Unorm)));
        Assert.Equal(Format.R32Sfloat, StorageFormats.Choose(Format.R16Sfloat, only(Format.R32Sfloat)));
        Assert.Equal(Format.R32G32B32A32Sfloat, StorageFormats.Choose(Format.R32Sfloat, only(Format.R32G32B32A32Sfloat)));
        Assert.Equal(Format.R8G8B8A8Unorm, StorageFormats.Choose(Format.R32Sfloat, only()));
        // Storage alone is not enough: the next candidate that also samples wins.
        Assert.Equal(Format.R8G8Unorm, StorageFormats.Choose(Format.R8Unorm,
            format => format == Format.R8Unorm ? FormatFeatureFlags.StorageImageBit : storage));
    }
}
