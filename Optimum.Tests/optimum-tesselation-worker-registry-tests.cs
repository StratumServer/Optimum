using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class OptimumTesselationWorkerRegistryTests
{
    [Fact]
    public void RegisteredThreadIsRecognizedAndUnknownThreadIsRejected()
    {
        var registry = new OptimumTesselationWorkerRegistry();

        int firstSlot = registry.Register(17);
        int secondSlot = registry.Register(17);

        Assert.True(registry.Contains(17));
        Assert.False(registry.Contains(18));
        Assert.Equal(firstSlot, secondSlot);
        Assert.Equal(firstSlot, registry.GetSlot(17));
        Assert.Equal(0, registry.GetSlot(18));
    }

    [Fact]
    public void ConcurrentRegistrationKeepsAllWorkersVisible()
    {
        var registry = new OptimumTesselationWorkerRegistry();

        Parallel.For(1, 65, threadId => registry.Register(threadId));

        var slots = new HashSet<int>();
        for (int threadId = 1; threadId < 65; threadId++)
        {
            Assert.True(registry.Contains(threadId));
            Assert.True(slots.Add(registry.GetSlot(threadId)));
        }
        Assert.Equal(64, slots.Count);
        Assert.Contains(0, slots);
        Assert.Contains(63, slots);
    }
}
