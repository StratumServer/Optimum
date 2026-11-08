using System;
using System.Collections.Generic;

namespace Vintagestory.API.Config;

/// <summary>
/// Operating state of an Optimum runtime circuit breaker.
/// </summary>
public enum OptimumCircuitBreakerState : byte
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2
}

/// <summary>
/// Allocation-free circuit breaker value type for catching and suppressing
/// rendering exceptions without permanent fallback latches.
/// </summary>
public struct OptimumCircuitBreaker
{
    private byte _state;
    private byte _failureLogged;
    private int _tripCount;

    public readonly bool IsClosed => _state == (byte)OptimumCircuitBreakerState.Closed;
    public readonly bool IsOpen => _state == (byte)OptimumCircuitBreakerState.Open;
    public readonly bool IsHalfOpen => _state == (byte)OptimumCircuitBreakerState.HalfOpen;

    public readonly OptimumCircuitBreakerState State => (OptimumCircuitBreakerState)_state;
    public readonly int TripCount => _tripCount;

    public bool Trip(Exception? exception, string name, Action<string>? logger = null)
    {
        _state = (byte)OptimumCircuitBreakerState.Open;
        _tripCount++;

        if (_failureLogged == 0)
        {
            _failureLogged = 1;
            logger?.Invoke($"[Optimum] Circuit breaker tripped for {name}: {exception?.Message}");
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _state = (byte)OptimumCircuitBreakerState.Closed;
        _failureLogged = 0;
    }

    public void SetHalfOpen()
    {
        _state = (byte)OptimumCircuitBreakerState.HalfOpen;
    }

    public readonly string GetStatus()
    {
        return State switch
        {
            OptimumCircuitBreakerState.Closed => "closed",
            OptimumCircuitBreakerState.Open => $"open (tripped, count={_tripCount})",
            OptimumCircuitBreakerState.HalfOpen => "half-open",
            _ => "unknown"
        };
    }
}

/// <summary>
/// Global registry for observing and resetting tripped circuit breakers across subsystems.
/// </summary>
public static class OptimumCircuitBreakerRegistry
{
    private static readonly Dictionary<string, (Func<string> GetStatus, Action Reset)> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    public static void Register(string name, Func<string> getStatus, Action reset)
    {
        lock (Lock)
        {
            Entries[name] = (getStatus, reset);
        }
    }

    public static void Unregister(string name)
    {
        lock (Lock)
        {
            Entries.Remove(name);
        }
    }

    public static void ResetAll()
    {
        lock (Lock)
        {
            foreach (var entry in Entries.Values)
            {
                try
                {
                    entry.Reset();
                }
                catch
                {
                }
            }
        }
    }

    public static string GetBreakersSummary()
    {
        lock (Lock)
        {
            if (Entries.Count == 0)
            {
                return "  circuit-breakers: none registered";
            }

            var parts = new List<string>(Entries.Count);
            foreach (var kvp in Entries)
            {
                parts.Add($"{kvp.Key}={kvp.Value.GetStatus()}");
            }
            return "  circuit-breakers: " + string.Join(", ", parts);
        }
    }
}
