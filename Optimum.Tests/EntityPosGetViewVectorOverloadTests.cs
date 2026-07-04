using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public class EntityPosGetViewVectorOverloadTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 1.2f)]
    [InlineData(-1.1f, 3.4f)]
    [InlineData(3.14159f, -2.5f)]
    public void IntoVectorOverloadMatchesAllocatingOverload(float pitch, float yaw)
    {
        Vec3f expected = EntityPos.GetViewVector(pitch, yaw);

        Vec3f into = new Vec3f();
        EntityPos.GetViewVector(pitch, yaw, into);

        Assert.Equal(expected.X, into.X, 5);
        Assert.Equal(expected.Y, into.Y, 5);
        Assert.Equal(expected.Z, into.Z, 5);
    }

    [Fact]
    public void IntoVectorOverloadReusesTheSameInstance()
    {
        Vec3f vector = new Vec3f();

        EntityPos.GetViewVector(0.1f, 0.2f, vector);
        Vec3f sameInstance = vector;
        EntityPos.GetViewVector(0.3f, 0.4f, vector);

        Assert.Same(sameInstance, vector);
    }
}
