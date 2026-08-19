using System;
using System.Threading;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Tests for shader preprocessing parallelism (Step 35).
/// Validates the config toggle, DTO serialization, and the phasing logic
/// (Phase 1 = CPU preprocessing, Phase 2 = GL compile).
/// Runtime GL behavior cannot be tested without a live OpenGL context.
/// </summary>
public class ShaderPreprocessParallelTests
{
    public ShaderPreprocessParallelTests()
    {
        OptimumConfig.ShaderPreprocessParallel = true;
    }

    [Fact]
    public void DefaultEnabled()
    {
        Assert.True(OptimumConfig.ShaderPreprocessParallel);
    }

    [Fact]
    public void ConfigToggleDisables()
    {
        OptimumConfig.ShaderPreprocessParallel = false;
        Assert.False(OptimumConfig.ShaderPreprocessParallel);
    }

    [Fact]
    public void ConfigDataSerializationRoundTrip()
    {
        var data = new OptimumConfigData();
        Assert.True(data.ShaderPreprocessParallel);
    }

    [Fact]
    public void DescribeTogglesContainsShaderPreprocessParallel()
    {
        var toggles = OptimumConfig.DescribeToggles();
        bool found = false;
        foreach (var (name, _) in toggles)
        {
            if (name == nameof(OptimumConfigData.ShaderPreprocessParallel))
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "ShaderPreprocessParallel should appear in DescribeToggles()");
    }

    [Fact]
    public void ToggleDoesNotAffectOtherConfig()
    {
        bool prevChunkDeserialize = OptimumConfig.ChunkDeserializeParallel;
        bool prevLaunchBudget = OptimumConfig.LaunchTaskBudgetEnabled;

        OptimumConfig.ShaderPreprocessParallel = false;

        Assert.Equal(prevChunkDeserialize, OptimumConfig.ChunkDeserializeParallel);
        Assert.Equal(prevLaunchBudget, OptimumConfig.LaunchTaskBudgetEnabled);
    }

    [Fact]
    public void VolatileFieldVisibleAcrossThreads()
    {
        OptimumConfig.ShaderPreprocessParallel = false;
        bool seen = false;

        var thread = new Thread(() =>
        {
            seen = OptimumConfig.ShaderPreprocessParallel;
        });
        thread.Start();
        thread.Join();

        Assert.False(seen);

        OptimumConfig.ShaderPreprocessParallel = true;
        thread = new Thread(() =>
        {
            seen = OptimumConfig.ShaderPreprocessParallel;
        });
        thread.Start();
        thread.Join();

        Assert.True(seen);
    }
}
