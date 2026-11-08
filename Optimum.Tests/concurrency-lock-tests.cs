using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class ConcurrencyLockTests
{
    [Fact]
    public void OptimumTesselationWorkerRegistry_HandlesConcurrentRegistrationsWithoutDeadlock()
    {
        var registry = new OptimumTesselationWorkerRegistry();
        const int threadCount = 32;
        const int iterationsPerThread = 500;
        var assignedSlots = new ConcurrentBag<int>();

        Parallel.For(0, threadCount, threadIndex =>
        {
            for (int i = 0; i < iterationsPerThread; i++)
            {
                int pseudoThreadId = 1000 + (threadIndex * 10) + (i % 10);
                int slot = registry.Register(pseudoThreadId);
                Assert.True(slot >= 0);
                assignedSlots.Add(slot);

                Assert.True(registry.Contains(pseudoThreadId));
                Assert.Equal(slot, registry.GetSlot(pseudoThreadId));
            }
        });

        Assert.NotEmpty(assignedSlots);
    }
}
