using System;
using System.Threading;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class OptimumThreadGuardTests
{
    public OptimumThreadGuardTests()
    {
        // Ensure clean state per test
        OptimumThreadGuard.Enable();
        OptimumThreadGuard.ResetViolations();
    }

    [Fact]
    public void SameThreadPassesVerify()
    {
        var guard = new OptimumThreadGuard();
        guard.Mark();
        Assert.True(guard.Verify());
        Assert.Equal(0, OptimumThreadGuard.ViolationCount);
    }

    [Fact]
    public void DifferentThreadFailsVerify()
    {
        var guard = new OptimumThreadGuard();
        guard.Mark(); // Marks on current (xUnit) thread

        bool passed = true;
        var t = new Thread(() =>
        {
            passed = guard.Verify(throwOnViolation: false);
        });
        t.Start();
        t.Join();

        Assert.False(passed);
        Assert.Equal(1, OptimumThreadGuard.ViolationCount);
    }

    [Fact]
    public void DifferentThreadThrowsWhenRequested()
    {
        var guard = new OptimumThreadGuard();
        guard.Mark();

        Exception caught = null;
        var t = new Thread(() =>
        {
            try { guard.Verify(throwOnViolation: true); }
            catch (Exception ex) { caught = ex; }
        });
        t.Start();
        t.Join();

        Assert.NotNull(caught);
        Assert.IsType<System.InvalidOperationException>(caught);
    }

    [Fact]
    public void DisabledGuardDoesNothing()
    {
        OptimumThreadGuard.Disable();

        var guard = new OptimumThreadGuard();
        guard.Mark();

        // Even on different thread, no violation when disabled
        bool passed = false;
        var t = new Thread(() => { passed = guard.Verify(); });
        t.Start();
        t.Join();

        Assert.True(passed);
        Assert.Equal(0, OptimumThreadGuard.ViolationCount);

        OptimumThreadGuard.Enable(); // Restore for other tests
    }

    [Fact]
    public void UnmarkedGuardSkipsVerify()
    {
        var guard = new OptimumThreadGuard();
        // Never call Mark()
        Assert.True(guard.Verify());
        Assert.Equal(0, OptimumThreadGuard.ViolationCount);
    }
}
