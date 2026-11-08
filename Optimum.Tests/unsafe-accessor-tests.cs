using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public sealed class UnsafeAccessorTests
{
    [Fact]
    public void GetPlayerPos_AccessesInternalPlayerPosFieldDirectly()
    {
        var culler = new FrustumCulling();
        var expectedPos = new BlockPos(100, 50, -200);

        // Assign via ref returned by the UnsafeAccessor
        ref BlockPos actualPos = ref OptimumApiBridge.GetPlayerPos(culler);
        actualPos = expectedPos;

        // Verify the ref returns the assigned position
        Assert.NotNull(actualPos);
        Assert.Equal(expectedPos.X, actualPos.X);
        Assert.Equal(expectedPos.Y, actualPos.Y);
        Assert.Equal(expectedPos.Z, actualPos.Z);

        // Verify that reflection reads the exact value written via the UnsafeAccessor
        var field = typeof(FrustumCulling).GetField("playerPos", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var readViaReflection = (BlockPos)field.GetValue(culler)!;
        Assert.Equal(expectedPos.X, readViaReflection.X);
        Assert.Equal(expectedPos.Y, readViaReflection.Y);
        Assert.Equal(expectedPos.Z, readViaReflection.Z);

        // Mutate through ref and verify reflection sees the mutation
        var mutatedPos = new BlockPos(300, 75, 450);
        actualPos = mutatedPos;
        var readMutated = (BlockPos)field.GetValue(culler)!;
        Assert.Equal(mutatedPos.X, readMutated.X);
        Assert.Equal(mutatedPos.Y, readMutated.Y);
        Assert.Equal(mutatedPos.Z, readMutated.Z);
    }
}
