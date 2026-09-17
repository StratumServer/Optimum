using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The descriptor cache key must tell two resources apart even when the driver
/// has given them the same handle value, which it does once the first is
/// destroyed. This is the pure half of the loading-screen crash; the live half
/// is in <see cref="VulkanDeviceIntegrationTests" />.
/// </summary>
public class DescriptorCacheLifetimeTests
{
    [Fact]
    public void TwoResourcesWithTheSameHandleAreDifferentKeys()
    {
        var view = new ImageView(0x1234);
        var sampler = new Sampler(0x99);

        var first = new DescriptorSetContents(1, 1,
            new[] { new SamplerBindingValue(0, view, sampler, Resource: 10) }, Array.Empty<BufferBindingValue>());
        var successor = new DescriptorSetContents(1, 1,
            new[] { new SamplerBindingValue(0, view, sampler, Resource: 11) }, Array.Empty<BufferBindingValue>());
        var same = new DescriptorSetContents(1, 1,
            new[] { new SamplerBindingValue(0, view, sampler, Resource: 10) }, Array.Empty<BufferBindingValue>());

        Assert.NotEqual(first, successor);
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
    }

    [Fact]
    public void BuffersAreToldApartByLifetimeIdToo()
    {
        var buffer = new Silk.NET.Vulkan.Buffer(0x5555);

        var first = new DescriptorSetContents(1, 0, Array.Empty<SamplerBindingValue>(),
            new[] { new BufferBindingValue(1, buffer, 0, 256, Resource: 20) });
        var successor = new DescriptorSetContents(1, 0, Array.Empty<SamplerBindingValue>(),
            new[] { new BufferBindingValue(1, buffer, 0, 256, Resource: 21) });

        Assert.NotEqual(first, successor);
    }

    [Fact]
    public void ResourceIdsNeverRepeat()
    {
        ulong a = ResourceIds.Next();
        ulong b = ResourceIds.Next();

        Assert.NotEqual(0UL, a);
        Assert.True(b > a);
    }
}
