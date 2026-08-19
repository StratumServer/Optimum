using System;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class OptimumDispatchClaimTests
{
    [Fact]
    public void ConcurrentClaimHasOneOwnerAndReleaseRestoresAvailability()
    {
        var claim = new OptimumDispatchClaim();
        int winners = 0;

        Parallel.For(0, 256, _ =>
        {
            if (claim.TryClaim())
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
        claim.Release();
        Assert.True(claim.TryClaim());
        claim.Release();
    }

    [Fact]
    public void ClaimReleaseRunsThroughFinallyAfterFailure()
    {
        var claim = new OptimumDispatchClaim();

        try
        {
            Assert.True(claim.TryClaim());
            throw new InvalidOperationException("fixture failure");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            claim.Release();
        }

        Assert.True(claim.TryClaim());
        claim.Release();
    }

    [Fact]
    public void ForeignWorldgenAssembliesAreOutsideTheAuditedSet()
    {
        Assert.False(OptimumWorldgenSafetyGate.IsKnownSafeAssembly("Optimum.Tests"));
        Assert.True(OptimumWorldgenSafetyGate.IsKnownSafeAssembly("VSEssentials"));
        Assert.Contains("Optimum.Tests", OptimumWorldgenSafetyGate.FindForeignAssemblies(new[] { "VSEssentials", "Optimum.Tests" }));
    }
}
