using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Plan decision 9's device floor: one pipeline layout with a bindless set of
/// partially bound, update-after-bind combined-image-sampler arrays. The floor is
/// judged without a device; the selected device is then shown to enable it, with a
/// layout of the decided shape passing validation.
/// </summary>
public class BindlessCapabilityTests
{
    private readonly ITestOutputHelper _output;

    public BindlessCapabilityTests(ITestOutputHelper output) => _output = output;

    private static DescriptorIndexingSupport AtFloor() => new(
        RuntimeDescriptorArray: true,
        DescriptorBindingPartiallyBound: true,
        DescriptorBindingSampledImageUpdateAfterBind: true,
        ShaderSampledImageArrayDynamicIndexing: true,
        MaxPerStageDescriptorUpdateAfterBindSampledImages: DescriptorIndexingFloor.RequiredSampledImages,
        MaxPerStageDescriptorUpdateAfterBindSamplers: DescriptorIndexingFloor.RequiredSampledImages,
        MaxDescriptorSetUpdateAfterBindSampledImages: DescriptorIndexingFloor.RequiredSampledImages,
        MaxDescriptorSetUpdateAfterBindSamplers: DescriptorIndexingFloor.RequiredSampledImages,
        MaxDescriptorSetUpdateAfterBindUniformBuffersDynamic: DescriptorIndexingFloor.RequiredDynamicUniformBuffers,
        MaxPushConstantsSize: DescriptorIndexingFloor.RequiredPushConstantBytes);

    [Fact]
    public void TheSampledImageRequirementIsTheResearchedSetOneTablePlusTheFrameTextures()
    {
        Assert.Equal(19456u, DescriptorIndexingFloor.BindlessSampledImages);
        Assert.Equal(19472u, DescriptorIndexingFloor.RequiredSampledImages);
        Assert.Equal(128u, DescriptorIndexingFloor.RequiredPushConstantBytes);
    }

    [Fact]
    public void ADeviceExactlyAtTheFloorMeetsIt()
    {
        Assert.Empty(DescriptorIndexingFloor.Missing(AtFloor()));
    }

    [Fact]
    public void EachMissingFeatureIsNamedOnItsOwn()
    {
        Assert.Equal(new[] { "runtimeDescriptorArray" },
            DescriptorIndexingFloor.Missing(AtFloor() with { RuntimeDescriptorArray = false }));
        Assert.Equal(new[] { "descriptorBindingPartiallyBound" },
            DescriptorIndexingFloor.Missing(AtFloor() with { DescriptorBindingPartiallyBound = false }));
        Assert.Equal(new[] { "descriptorBindingSampledImageUpdateAfterBind" },
            DescriptorIndexingFloor.Missing(AtFloor() with { DescriptorBindingSampledImageUpdateAfterBind = false }));
        Assert.Equal(new[] { "shaderSampledImageArrayDynamicIndexing" },
            DescriptorIndexingFloor.Missing(AtFloor() with { ShaderSampledImageArrayDynamicIndexing = false }));
    }

    [Fact]
    public void EachLimitBelowTheFloorIsNamedWithTheReportedAndRequiredValue()
    {
        uint below = DescriptorIndexingFloor.RequiredSampledImages - 1;
        string required = DescriptorIndexingFloor.RequiredSampledImages.ToString();

        AssertSingle(AtFloor() with { MaxPerStageDescriptorUpdateAfterBindSampledImages = below },
            "maxPerStageDescriptorUpdateAfterBindSampledImages", below, required);
        AssertSingle(AtFloor() with { MaxPerStageDescriptorUpdateAfterBindSamplers = below },
            "maxPerStageDescriptorUpdateAfterBindSamplers", below, required);
        AssertSingle(AtFloor() with { MaxDescriptorSetUpdateAfterBindSampledImages = below },
            "maxDescriptorSetUpdateAfterBindSampledImages", below, required);
        AssertSingle(AtFloor() with { MaxDescriptorSetUpdateAfterBindSamplers = below },
            "maxDescriptorSetUpdateAfterBindSamplers", below, required);
        uint dynamicBelow = DescriptorIndexingFloor.RequiredDynamicUniformBuffers - 1;
        AssertSingle(AtFloor() with { MaxDescriptorSetUpdateAfterBindUniformBuffersDynamic = dynamicBelow },
            "maxDescriptorSetUpdateAfterBindUniformBuffersDynamic", dynamicBelow,
            DescriptorIndexingFloor.RequiredDynamicUniformBuffers.ToString());
        AssertSingle(AtFloor() with { MaxPushConstantsSize = 64 }, "maxPushConstantsSize", 64, "128");
    }

    /// <summary>
    /// The per-stage update-after-bind sampled-image limits recorded for every target
    /// family in docs/research/vulkan-bindless.md (NVIDIA, AMD Windows, Intel Windows,
    /// RADV, ANV 12.5+, ANV pre-12.5, llvmpipe, SwiftShader): the floor excludes none.
    /// </summary>
    [Theory]
    [InlineData(1_048_576u)]
    [InlineData(4_294_967_295u)]
    [InlineData(33_554_432u)]
    [InlineData(8_388_606u)]
    [InlineData(201_326_592u)]
    [InlineData(1_000_000u)]
    [InlineData(500_000u)]
    public void NoResearchedTargetFallsBelowTheSampledImageFloor(uint reported)
    {
        Assert.Empty(DescriptorIndexingFloor.Missing(AtFloor() with
        {
            MaxPerStageDescriptorUpdateAfterBindSampledImages = reported,
            MaxPerStageDescriptorUpdateAfterBindSamplers = reported,
            MaxDescriptorSetUpdateAfterBindSampledImages = reported,
            MaxDescriptorSetUpdateAfterBindSamplers = reported,
        }));
    }

    /// <summary>
    /// The selected device meets the floor, and a pipeline layout of decision 9's
    /// shape - set 0 with a dynamic frame UBO and frame textures, set 1 the bindless
    /// array flagged PARTIALLY_BOUND | UPDATE_AFTER_BIND from an update-after-bind pool,
    /// a 128-byte push-constant range - is created and allocated with no validation
    /// message. The layer reports the binding flags and the pool flag as errors unless
    /// the device enabled the matching features, so this is also the proof they are on.
    /// </summary>
    [SkippableFact]
    public unsafe void TheSelectedDeviceEnablesTheBindlessSetAndADecisionNineLayoutValidates()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            Vk api = context!.Api;
            Device device = context.Device;
            DescriptorIndexingSupport support = context.Capabilities.DescriptorIndexing;
            _output.WriteLine($"device: {context.Capabilities.DeviceName}");
            _output.WriteLine($"support: {support}");
            Assert.Empty(DescriptorIndexingFloor.Missing(support));

            const ShaderStageFlags stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

            DescriptorBindingFlags bindlessFlags = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit;
            var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &bindlessFlags,
            };
            var textures = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = DescriptorIndexingFloor.BindlessSampledImages,
                StageFlags = stages,
            };
            var bindlessInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &bindingFlags,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
                BindingCount = 1,
                PBindings = &textures,
            };
            DescriptorSetLayout bindless;
            Assert.Equal(Result.Success, api.CreateDescriptorSetLayout(device, &bindlessInfo, null, &bindless));

            DescriptorSetLayoutBinding* frameBindings = stackalloc DescriptorSetLayoutBinding[2];
            frameBindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.UniformBufferDynamic,
                DescriptorCount = 1,
                StageFlags = stages,
            };
            frameBindings[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = DescriptorIndexingFloor.FrameTextures,
                StageFlags = stages,
            };
            var frameInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = frameBindings,
            };
            DescriptorSetLayout frame;
            Assert.Equal(Result.Success, api.CreateDescriptorSetLayout(device, &frameInfo, null, &frame));

            DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = frame;
            setLayouts[1] = bindless;
            var pushConstants = new PushConstantRange
            {
                StageFlags = stages,
                Offset = 0,
                Size = DescriptorIndexingFloor.RequiredPushConstantBytes,
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstants,
            };
            PipelineLayout pipelineLayout;
            Assert.Equal(Result.Success, api.CreatePipelineLayout(device, &layoutInfo, null, &pipelineLayout));

            var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, DescriptorIndexingFloor.BindlessSampledImages);
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            DescriptorPool pool;
            Assert.Equal(Result.Success, api.CreateDescriptorPool(device, &poolInfo, null, &pool));

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &bindless,
            };
            DescriptorSet set;
            Assert.Equal(Result.Success, api.AllocateDescriptorSets(device, &allocateInfo, &set));
            Assert.NotEqual(0ul, set.Handle);

            api.DestroyDescriptorPool(device, pool, null);
            api.DestroyPipelineLayout(device, pipelineLayout, null);
            api.DestroyDescriptorSetLayout(device, frame, null);
            api.DestroyDescriptorSetLayout(device, bindless, null);
        }

        foreach (string message in ValidationAssert.Snapshot(messages)) _output.WriteLine("[validation] " + message);
        ValidationAssert.NoErrors(messages);
        ValidationAssert.NoSyncHazards(messages);
    }

    private static void AssertSingle(DescriptorIndexingSupport support, string name, uint reported, string required)
    {
        Assert.Equal(new[] { $"{name} {reported} < {required}" }, DescriptorIndexingFloor.Missing(support));
    }
}
