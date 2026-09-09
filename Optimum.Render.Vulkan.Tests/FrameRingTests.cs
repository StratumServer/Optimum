using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Covers the frame ring and the descriptor cache against a real device.
///
/// Both exist to make the per-frame cost of a GL-shaped renderer bearable, and
/// both have failure modes that only appear under motion: a slot reused before
/// the GPU finished with it, a resource freed while still referenced, a
/// descriptor set that silently keeps stale contents.
/// </summary>
public class FrameRingTests
{
    private readonly ITestOutputHelper _output;

    public FrameRingTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(ITestOutputHelper output, out VulkanContext? context)
    {
        var options = new VulkanContextOptions { Headless = true, EnableValidation = true };
        bool created = VulkanContext.TryCreate(options, out context, out string? failureReason);
        if (!created) output.WriteLine("Vulkan unavailable: " + failureReason);
        return created;
    }

    private sealed class TrackedResource : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [SkippableFact]
    public void FrameSlotsRotateAndCanBeCycledRepeatedly()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20);

            var seen = new List<FrameSlot>();
            for (int frame = 0; frame < 6; frame++)
            {
                FrameSlot slot = ring.BeginFrame();
                seen.Add(slot);
                ring.EndFrame();
            }

            // Two slots, alternating, and reused rather than reallocated.
            Assert.Same(seen[0], seen[2]);
            Assert.Same(seen[1], seen[3]);
            Assert.NotSame(seen[0], seen[1]);

            context!.Api.DeviceWaitIdle(context.Device);
        }
    }

    /// <summary>
    /// A resource handed to the ring must survive until the GPU is demonstrably
    /// done with the frame that referenced it. Freeing at the moment the game
    /// asks is the classic use-after-free in a Vulkan port of a GL renderer,
    /// because GL let the driver worry about it.
    /// </summary>
    [SkippableFact]
    public void DeferredDeletionsOutliveTheFrameThatQueuedThem()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20);
            var resource = new TrackedResource();

            ring.BeginFrame();
            ring.DeferDeletion(resource);
            ring.EndFrame();
            Assert.False(resource.Disposed, "must not be freed during the frame that queued it");

            // The next frame drains the queue and adopts the resource.
            ring.BeginFrame();
            ring.EndFrame();
            Assert.False(resource.Disposed, "must not be freed while the adopting slot is in flight");

            ring.BeginFrame();
            ring.EndFrame();
            Assert.False(resource.Disposed, "the adopting slot has not come round yet");

            // Back to the adopting slot: its fence has signalled, so the GPU is
            // demonstrably finished with everything that frame referenced.
            ring.BeginFrame();
            ring.EndFrame();
            Assert.True(resource.Disposed, "should be freed once the adopting slot's fence signalled");

            context!.Api.DeviceWaitIdle(context.Device);
        }
    }

    /// <summary>
    /// VAO and UBO finalizers call Dispose from the finalizer thread, so the
    /// deletion queue has to accept work from threads that are not the render
    /// thread while still doing the destruction on it.
    /// </summary>
    [SkippableFact]
    public void DeletionsQueuedFromOtherThreadsAreAccepted()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20);

            var resources = new List<TrackedResource>();
            for (int i = 0; i < 64; i++) resources.Add(new TrackedResource());

            Parallel.ForEach(resources, resource => ring.DeferDeletion(resource));
            Assert.Equal(64, ring.PendingDeletionCount);

            for (int frame = 0; frame < 4; frame++)
            {
                ring.BeginFrame();
                ring.EndFrame();
            }

            Assert.All(resources, resource => Assert.True(resource.Disposed));
            context!.Api.DeviceWaitIdle(context.Device);
        }
    }

    [SkippableFact]
    public void UniformAllocationsRespectTheDeviceAlignmentAndTheRegionBound()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            ulong alignment = context!.Capabilities.MinUniformBufferOffsetAlignment;
            using var ring = new FrameRing(context, framesInFlight: 2, uniformRingSize: 64 * 1024);

            FrameSlot slot = ring.BeginFrame();

            for (int i = 0; i < 16; i++)
            {
                Assert.True(slot.TryAllocateUniforms(100, out RingAllocation allocation));
                Assert.True(allocation.Offset % alignment == 0,
                    $"offset {allocation.Offset} is not aligned to {alignment}");
                Assert.NotEqual(IntPtr.Zero, allocation.Pointer);
            }

            // Exhausting the region reports rather than overruns.
            Assert.False(slot.TryAllocateUniforms((int)slot.UniformCapacity + 1, out _));

            _output.WriteLine($"alignment {alignment}, used {slot.UniformBytesUsed} of {slot.UniformCapacity}");

            ring.EndFrame();
            context.Api.DeviceWaitIdle(context.Device);
        }
    }

    /// <summary>
    /// Each slot bump-allocates inside its own slice of one shared buffer. The
    /// shared buffer is what lets descriptor sets be written once and reused,
    /// since the set names the buffer and the offset travels dynamically.
    /// </summary>
    [SkippableFact]
    public void SlotsAllocateFromDisjointRegionsOfOneSharedBuffer()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 64 * 1024);

            FrameSlot first = ring.BeginFrame();
            Assert.True(first.TryAllocateUniforms(256, out RingAllocation a));
            ring.EndFrame();

            FrameSlot second = ring.BeginFrame();
            Assert.True(second.TryAllocateUniforms(256, out RingAllocation b));
            ring.EndFrame();

            Assert.Equal(a.Buffer.Handle, b.Buffer.Handle);
            Assert.Equal(ring.UniformBuffer.Handle, a.Buffer.Handle);
            Assert.NotEqual(a.Offset, b.Offset);

            context!.Api.DeviceWaitIdle(context.Device);
        }
    }

    // -------------------------------------------------------- descriptor cache

    /// <summary>
    /// The set and binding numbers are decided by the shader rewriter and
    /// duplicated as constants in the descriptor layer so it does not depend on
    /// the translation types. If the two ever drift, samplers get written into
    /// the wrong set and nothing renders.
    /// </summary>
    [Fact]
    public void DescriptorBindingConstantsAgreeWithTheShaderRewriter()
    {
        Assert.Equal(ProgramInterfaceLayout.DefaultBlockSet, ProgramInterfaceLayoutBindings.DefaultBlockSet);
        Assert.Equal(ProgramInterfaceLayout.DefaultBlockBinding, ProgramInterfaceLayoutBindings.DefaultBlockBinding);
        Assert.Equal(ProgramInterfaceLayout.SamplerSet, ProgramInterfaceLayoutBindings.SamplerSet);
        Assert.Equal(ProgramInterfaceLayout.StorageSet, ProgramInterfaceLayoutBindings.StorageSet);
    }

    [Fact]
    public void DescriptorContentsCompareByValue()
    {
        var view = new ImageView(0x1234);
        var sampler = new Sampler(0x5678);

        var a = new DescriptorSetContents(1, 1,
            new[] { new SamplerBindingValue(0, view, sampler) }, Array.Empty<BufferBindingValue>());
        var b = new DescriptorSetContents(1, 1,
            new[] { new SamplerBindingValue(0, view, sampler) }, Array.Empty<BufferBindingValue>());
        var differentTexture = new DescriptorSetContents(1, 1,
            new[] { new SamplerBindingValue(0, new ImageView(0x9999), sampler) },
            Array.Empty<BufferBindingValue>());
        var differentProgram = new DescriptorSetContents(2, 1,
            new[] { new SamplerBindingValue(0, view, sampler) }, Array.Empty<BufferBindingValue>());

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, differentTexture);
        Assert.NotEqual(a, differentProgram);
    }

    /// <summary>
    /// The point of the cache: binding the same atlas over and over, which is
    /// what chunk rendering does thousands of times a frame, must cost a
    /// dictionary lookup rather than an allocation and a write.
    /// </summary>
    [SkippableFact]
    public unsafe void RepeatedIdenticalBindingsReuseOneDescriptorSet()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var compiler = new ShaderCompiler();
            using var program = LoadProgram(context!, compiler, "blit", programId: 1);
            using var cache = new DescriptorCache(context!);
            using var image = new VulkanImage(context!, 16, 16, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit);

            Sampler sampler = CreateSampler(context!);
            DescriptorSetLayout layout = program.SetLayouts[ProgramInterfaceLayout.SamplerSet];

            DescriptorSetContents Contents() => new(1, ProgramInterfaceLayout.SamplerSet,
                new[] { new SamplerBindingValue(0, image.View, sampler) },
                Array.Empty<BufferBindingValue>());

            DescriptorSet first = cache.Get(Contents(), layout);
            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(first.Handle, cache.Get(Contents(), layout).Handle);
            }

            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.Misses);
            Assert.Equal(1000, cache.Hits);
            _output.WriteLine($"sets: {cache.Count}, hits: {cache.Hits}, misses: {cache.Misses}");

            context!.Api.DeviceWaitIdle(context.Device);
            context.Api.DestroySampler(context.Device, sampler, null);
        }
    }

    /// <summary>
    /// Growing past one pool must keep working. A cache that silently failed to
    /// allocate would look like missing textures, not like an error.
    /// </summary>
    [SkippableFact]
    public unsafe void TheCacheGrowsBeyondASinglePool()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var compiler = new ShaderCompiler();
            using var program = LoadProgram(context!, compiler, "blit", programId: 1);
            using var cache = new DescriptorCache(context!);
            using var image = new VulkanImage(context!, 4, 4, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit);

            Sampler sampler = CreateSampler(context!);
            DescriptorSetLayout layout = program.SetLayouts[ProgramInterfaceLayout.SamplerSet];

            // Distinct views over one image: cheap, and enough to make each set's
            // contents unique without one device allocation per entry.
            var views = new List<ImageView>();
            try
            {
                const int count = 700;
                for (int i = 0; i < count; i++)
                {
                    var viewInfo = new ImageViewCreateInfo
                    {
                        SType = StructureType.ImageViewCreateInfo,
                        Image = image.Handle,
                        ViewType = ImageViewType.Type2D,
                        Format = Format.R8G8B8A8Unorm,
                        SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                    };
                    context!.Api.CreateImageView(context.Device, &viewInfo, null, out ImageView view);
                    views.Add(view);

                    cache.Get(new DescriptorSetContents(1, ProgramInterfaceLayout.SamplerSet,
                        new[] { new SamplerBindingValue(0, view, sampler) },
                        Array.Empty<BufferBindingValue>()), layout);
                }

                Assert.Equal(count, cache.Count);
                Assert.Equal(count, cache.Misses);
                _output.WriteLine($"grew to {cache.Count} sets across multiple pools");
            }
            finally
            {
                context!.Api.DeviceWaitIdle(context.Device);
                foreach (ImageView view in views) context.Api.DestroyImageView(context.Device, view, null);
                context.Api.DestroySampler(context.Device, sampler, null);
            }
        }
    }

    private static ShaderProgramResources LoadProgram(
        VulkanContext context, ShaderCompiler compiler, string programName, int programId)
    {
        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        ShaderCorpus.ShaderVariant variant = System.Linq.Enumerable.First(
            ShaderCorpus.Variants(), v => v.Name == "everything-on");

        TranslatedProgram translated = ShaderTranslator.Translate(
            ShaderCorpus.BuildProgram(programName, files, includes, variant), compiler);
        Assert.True(translated.Success, string.Join("; ", translated.Errors));

        return new ShaderProgramResources(context, programId, translated);
    }

    /// <summary>
    /// A combined image sampler descriptor needs a real sampler; writing a null
    /// handle into one crashes the driver rather than reporting an error.
    /// </summary>
    private static unsafe Sampler CreateSampler(VulkanContext context)
    {
        var createInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 1.0f,
        };
        context.Api.CreateSampler(context.Device, &createInfo, null, out Sampler sampler);
        return sampler;
    }
}
