using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class OptimumWorldgenFootprintLeaseTests
{
    [Fact]
    public async Task ConflictingFootprintsHaveOneOwnerUntilRelease()
    {
        var registry = new OptimumWorldgenFootprintRegistry();
        var keys = new[] { new OptimumWorldgenFootprintKey(0, 10, 20) };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task<bool> owner = Task.Run(() =>
        {
            if (!registry.TryAcquire(keys, out OptimumWorldgenFootprintLease? lease)) return false;
            entered.Set();
            release.Wait();
            lease!.Dispose();
            return true;
        });

        Assert.True(entered.Wait(5000));
        Assert.False(registry.TryAcquire(keys, out OptimumWorldgenFootprintLease? blocked));
        Assert.Null(blocked);

        release.Set();
        Assert.True(await owner);
        Assert.True(registry.TryAcquire(keys, out OptimumWorldgenFootprintLease? available));
        available!.Dispose();
    }

    [Fact]
    public void FailedAcquireRollsBackEveryPartialReservation()
    {
        var registry = new OptimumWorldgenFootprintRegistry();
        var heldKey = new OptimumWorldgenFootprintKey(0, 1, 1);
        var freeKey = new OptimumWorldgenFootprintKey(0, 2, 2);
        var overlap = new[] { heldKey, freeKey };

        Assert.True(registry.TryAcquire(new[] { heldKey }, out OptimumWorldgenFootprintLease? held));
        Assert.False(registry.TryAcquire(overlap, out OptimumWorldgenFootprintLease? failed));
        Assert.Null(failed);

        held!.Dispose();
        Assert.True(registry.TryAcquire(overlap, out OptimumWorldgenFootprintLease? recovered));
        recovered!.Dispose();
    }

    [Fact]
    public void DuplicateKeysProduceOneReservation()
    {
        var registry = new OptimumWorldgenFootprintRegistry();
        var key = new OptimumWorldgenFootprintKey(-1, -4, 9);
        var duplicateKeys = new List<OptimumWorldgenFootprintKey> { key, key, key };

        Assert.True(registry.TryAcquire(duplicateKeys, out OptimumWorldgenFootprintLease? lease));
        Assert.False(registry.TryAcquire(new[] { key }, out OptimumWorldgenFootprintLease? blocked));
        lease!.Dispose();
        Assert.Null(blocked);
    }
}
