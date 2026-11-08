using System;
using System.Collections.Generic;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class OptimumCircuitBreakerTests
{
    [Fact]
    public void OptimumCircuitBreaker_StartsClosed()
    {
        var breaker = default(OptimumCircuitBreaker);

        Assert.True(breaker.IsClosed);
        Assert.False(breaker.IsOpen);
        Assert.False(breaker.IsHalfOpen);
        Assert.Equal(OptimumCircuitBreakerState.Closed, breaker.State);
        Assert.Equal(0, breaker.TripCount);
        Assert.Equal("closed", breaker.GetStatus());
    }

    [Fact]
    public void OptimumCircuitBreaker_Trip_TransitionsToOpen_AndIncrementsCount()
    {
        var breaker = default(OptimumCircuitBreaker);
        var logged = new List<string>();

        bool firstLogged = breaker.Trip(new InvalidOperationException("boom"), "test-subsystem", msg => logged.Add(msg));

        Assert.True(firstLogged);
        Assert.False(breaker.IsClosed);
        Assert.True(breaker.IsOpen);
        Assert.False(breaker.IsHalfOpen);
        Assert.Equal(OptimumCircuitBreakerState.Open, breaker.State);
        Assert.Equal(1, breaker.TripCount);
        Assert.Single(logged);
        Assert.Contains("test-subsystem", logged[0]);
        Assert.Contains("boom", logged[0]);
        Assert.Equal("open (tripped, count=1)", breaker.GetStatus());

        // Second trip should increment count but suppress redundant logging
        bool secondLogged = breaker.Trip(new InvalidOperationException("second boom"), "test-subsystem", msg => logged.Add(msg));

        Assert.False(secondLogged);
        Assert.Equal(2, breaker.TripCount);
        Assert.Single(logged);
        Assert.Equal("open (tripped, count=2)", breaker.GetStatus());
    }

    [Fact]
    public void OptimumCircuitBreaker_Reset_ReturnsToClosed_AndReEnablesLogging()
    {
        var breaker = default(OptimumCircuitBreaker);
        breaker.Trip(new Exception("fail"), "test-subsystem");
        Assert.True(breaker.IsOpen);

        breaker.Reset();

        Assert.True(breaker.IsClosed);
        Assert.False(breaker.IsOpen);
        Assert.Equal(OptimumCircuitBreakerState.Closed, breaker.State);
        Assert.Equal("closed", breaker.GetStatus());

        // After reset, a subsequent trip logs again
        var logged = new List<string>();
        bool loggedAfterReset = breaker.Trip(new Exception("fail again"), "test-subsystem", msg => logged.Add(msg));
        Assert.True(loggedAfterReset);
        Assert.Single(logged);
    }

    [Fact]
    public void OptimumCircuitBreaker_SetHalfOpen_TransitionsState()
    {
        var breaker = default(OptimumCircuitBreaker);
        breaker.Trip(new Exception("fail"), "test-subsystem");

        breaker.SetHalfOpen();

        Assert.False(breaker.IsClosed);
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.IsHalfOpen);
        Assert.Equal(OptimumCircuitBreakerState.HalfOpen, breaker.State);
        Assert.Equal("half-open", breaker.GetStatus());
    }

    [Fact]
    public void OptimumCircuitBreakerRegistry_Register_ResetAll_AndSummary()
    {
        var b1 = default(OptimumCircuitBreaker);
        var b2 = default(OptimumCircuitBreaker);

        b1.Trip(new Exception("err1"), "unit-test-1");
        b2.Trip(new Exception("err2"), "unit-test-2");

        OptimumCircuitBreakerRegistry.Register("unit-test-1", () => b1.GetStatus(), () => b1.Reset());
        OptimumCircuitBreakerRegistry.Register("unit-test-2", () => b2.GetStatus(), () => b2.Reset());

        string summary = OptimumCircuitBreakerRegistry.GetBreakersSummary();
        Assert.Contains("unit-test-1=open (tripped, count=1)", summary);
        Assert.Contains("unit-test-2=open (tripped, count=1)", summary);

        OptimumCircuitBreakerRegistry.ResetAll();

        Assert.True(b1.IsClosed);
        Assert.True(b2.IsClosed);

        OptimumCircuitBreakerRegistry.Unregister("unit-test-1");
        OptimumCircuitBreakerRegistry.Unregister("unit-test-2");
    }
}
