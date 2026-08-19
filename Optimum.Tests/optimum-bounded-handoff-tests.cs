using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

// Shares OptimumDiagnostics' static tessellation counters with
// OptimumDiagnosticsCountersTests's worker-registry test - both must run in the same
// xUnit collection (serialized) or ResetTessellation() calls from one interleave with
// the other's assertions under xUnit's default cross-class parallelism.
[Collection("TessellationDiagnostics")]
public sealed class OptimumBoundedHandoffTests
{
    [Fact]
    public void CapacityLeavesRoomForPriorityWork()
    {
        var handoff = new OptimumBoundedHandoff(4, priorityReserve: 1);

        Assert.True(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: false));
        Assert.False(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: true));
        Assert.Equal(4, handoff.Reserved);

        handoff.Release();
        Assert.True(handoff.TryReserve(priority: true));
        Assert.Equal(4, handoff.Reserved);
    }

    [Fact]
    public void ConcurrentReservationsNeverExceedCapacity()
    {
        var handoff = new OptimumBoundedHandoff(32, priorityReserve: 8);
        int accepted = 0;

        Parallel.For(0, 512, _ =>
        {
            if (handoff.TryReserve(priority: false))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.Equal(24, accepted);
        Assert.Equal(24, handoff.Reserved);

        for (int i = 0; i < accepted; i++)
        {
            handoff.Release();
        }

        Assert.Equal(0, handoff.Reserved);
    }

    // Step 6 of the worker-pool wiring plan: the handoff must publish its capacity and a
    // running reservation peak to OptimumDiagnostics, so `.optimum status` can prove the
    // worker pool's reserve/release path is actually live without a debugger.
    [Fact]
    public void ConstructorAndTryReservePublishCapacityAndPeakToDiagnostics()
    {
        OptimumDiagnostics.ResetTessellation();

        var handoff = new OptimumBoundedHandoff(6, priorityReserve: 1);
        Assert.True(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: false));
        handoff.Release();
        Assert.True(handoff.TryReserve(priority: false));

        string summary = OptimumDiagnostics.GetTessellationSummary();
        // Peak must reflect the high-water mark (2), not the final reserved count (1).
        Assert.Contains("handoffPeak=2/6", summary);

        OptimumDiagnostics.ResetTessellation();
        Assert.Contains("handoffPeak=0/6", OptimumDiagnostics.GetTessellationSummary());
    }
}
