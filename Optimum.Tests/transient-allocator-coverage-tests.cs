using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Phase 2 step 4 (transient allocator) at source level: the post-chain slots opt in through
/// the Transient pool class, aliasing is env-gated and default off, an aliased lease discards,
/// ReadSelf copies are pooled instead of kept per texture, and the stats line exists.
/// The pixels are proven by Optimum.Render.Vulkan.Tests/TransientAllocatorTests.cs.
/// </summary>
public class TransientAllocatorCoverageTests
{
    [Fact]
    public void ThePostChainSlotsOptIn()
    {
        string platform = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("device.CreateTransientTexture2DRaw(ssaoWidth, ssaoHeight, 6407, 13);", platform);
        foreach (int slot in new[] { 14, 15 })
            Assert.Contains("list[" + slot + "] = CreateOptimumColorTarget(ssaoWidth, ssaoHeight, EnumTextureInternalFormat.Rgba8, " + slot + ");", platform);
        foreach (int slot in new[] { 2, 3, 8, 9, 4, 7, 10 })
            Assert.Contains("list[" + slot + "] = CreateOptimumColorTarget(", platform);
        Assert.Contains("EnumTextureInternalFormat.Rgba16f, OptimumTaaSharpenIndex);", platform);
        Assert.Contains("EnumTextureInternalFormat.Rgba8, OptimumFsrFramebufferIndex);", platform);
        Assert.Contains("target.ColorTextureIds[0] = device.CreateTransientTexture2D(width, height, format, slot);", platform);

        string allocator = Read("Optimum.Render.Vulkan/Graph/TransientAllocator.cs");
        Assert.Contains("PostChainSlots = { 2, 3, 4, 7, 8, 9, 10, 13, 14, 15, 18, 21 };", allocator);

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains("poolClass: MemoryPoolClass.Transient);", device);
        Assert.Contains("_transients.OptIn(id, framebufferSlot);", device);
    }

    [Fact]
    public void AliasingIsEnvGatedAndDiscardsOnFirstUse()
    {
        string allocator = Read("Optimum.Render.Vulkan/Graph/TransientAllocator.cs");
        Assert.Contains("public const string AliasVariable = \"OPTIMUM_VULKAN_ALIAS\";", allocator);
        Assert.Contains("Environment.GetEnvironmentVariable(AliasVariable) == \"1\"", allocator);
        Assert.Contains("int[] slots = TransientPlacement.Place(_intervals);", allocator);
        Assert.Contains("if (_aliasing) _backing.Discard(image.TextureId);", allocator);

        string backing = Read("Optimum.Render.Vulkan/Graph/TextureTransientBacking.cs");
        Assert.Contains("poolClass: MemoryPoolClass.Transient", backing);

        string tracker = Read("Optimum.Render.Vulkan/Graph/ResourceStateTracker.cs");
        Assert.Contains("public void Discard()", tracker);

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains("TransientAliasingOverride ?? Graph.TransientAllocator.AliasingFromEnvironment()", device);
        Assert.Contains("_transients.BeginFrame();", device);
    }

    [Fact]
    public void ReadSelfCopiesArePooledOnTheTimeline()
    {
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.DoesNotContain("_feedbackCopies", device);
        Assert.Contains("int copyId = _readSelfCopies.Acquire(new Graph.FeedbackCopyDesc(", device);
        Assert.Contains("_readSelfCopies.EndFrame();", device);
        Assert.Contains("_readSelfCopies.Collect();", device);
        Assert.Contains("new Graph.FeedbackCopyPool(_frames.Timeline, CreateReadSelfCopy, ReleaseTexture)", device);

        string pool = Read("Optimum.Render.Vulkan/Graph/FeedbackCopyPool.cs");
        Assert.Contains("if (copy.RetiredAt <= completed)", pool);
    }

    [Fact]
    public void StatsReportTransientAliasedAndHeapPeak()
    {
        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("\"stats.transients transient_mib={0:F1} aliased_mib={1:F1} heap_peak_mib={2:F1} leases={3} \"", stats);
        Assert.Contains("HeapPeakBytes: memory?.TakeTransientHeapPeak() ?? 0,", stats);

        string doc = Read("docs/taa-acceptance.md");
        Assert.Contains("stats.transients", doc);
    }

    private static string Read(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, relativePath));
    }
}
