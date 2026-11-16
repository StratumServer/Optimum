using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public class SelectionBoxRayBoundsTests
{
    private static readonly MethodInfo IsBeyondRayLengthMethod = typeof(EntityBehaviorSelectionBoxes)
        .Assembly
        .GetType("Vintagestory.GameContent.SelectionBoxRayBounds")!
        .GetMethod("IsBeyondRayLength", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo IsEntityBeyondRayRangeMethod = typeof(EntityBehaviorSelectionBoxes)
        .Assembly
        .GetType("Vintagestory.GameContent.SelectionBoxRayBounds")!
        .GetMethod("IsEntityBeyondRayRange", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void RejectsDistantEntityButKeepsTheSelectionBoxAndMarginBoundary()
    {
        Cuboidf selectionBox = new(-0.5f, 0, -0.5f, 0.5f, 2, 0.5f);

        Assert.False(IsEntityBeyondRayRange(9.5, 0, 0, 0, 0, 0, selectionBox, 4.5));
        Assert.True(IsEntityBeyondRayRange(10, 0, 0, 0, 0, 0, selectionBox, 4.5));
    }

    [Fact]
    public void KeepsOversizedAndOffsetSelectionBoxesWithinReach()
    {
        Cuboidf oversized = new(0, 0, 0, 20, 1, 1);

        Assert.False(IsEntityBeyondRayRange(27, 0, 0, 0, 0, 0, oversized, 4.5));
        Assert.True(IsEntityBeyondRayRange(30, 0, 0, 0, 0, 0, oversized, 4.5));
    }

    [Fact]
    public void FailsOpenForInvalidEntityBoundsAndPositions()
    {
        Cuboidf selectionBox = new(-0.5f, 0, -0.5f, 0.5f, 2, 0.5f);

        Assert.False(IsEntityBeyondRayRange(double.NaN, 0, 0, 0, 0, 0, selectionBox, 4.5));
        Assert.False(IsEntityBeyondRayRange(100, 0, 0, 0, 0, 0, selectionBox, double.PositiveInfinity));
        selectionBox.X1 = float.NaN;
        Assert.False(IsEntityBeyondRayRange(100, 0, 0, 0, 0, 0, selectionBox, 4.5));
    }

    [Fact]
    public void RejectsDistantBoxButKeepsAnOversizedBoxThatReachesTheRay()
    {
        Vec3d origin = new(0, 0, 0);
        Assert.True(IsBeyondRayLength(Transform(20, 0, 0, 1, 1, 1), origin, 100));

        Matrixf oversized = Transform(-100, 0, 0, 200, 1, 1);
        Assert.False(IsBeyondRayLength(oversized, origin, 100));
    }

    [Fact]
    public void KeepsExactReachBoundaryAndReadsTheCurrentTransformOnEachCall()
    {
        Matrixf transform = Transform(10, 0, 0, 1, 1, 1);
        Vec3d origin = new(0, 0, 0);

        Assert.False(IsBeyondRayLength(transform, origin, 100));

        transform.Values[12] = 12;
        Assert.True(IsBeyondRayLength(transform, origin, 100));
    }

    [Fact]
    public void DoesNotRejectAnySampledPointWithinRayLengthAcrossAffineTransforms()
    {
        Random random = new(575);
        Vec3d origin = new(3.25, -4.5, 7.75);

        for (int transformIndex = 0; transformIndex < 100; transformIndex++)
        {
            Matrixf transform = RandomAffineTransform(random);
            for (int pointIndex = 0; pointIndex < 100; pointIndex++)
            {
                Vec4d point = transform.TransformVector(new Vec4d(random.NextDouble(), random.NextDouble(), random.NextDouble(), 1));
                double dx = point.X - origin.X;
                double dy = point.Y - origin.Y;
                double dz = point.Z - origin.Z;
                double distanceSq = dx * dx + dy * dy + dz * dz;

                Assert.False(IsBeyondRayLength(transform, origin, distanceSq));
            }
        }
    }

    [Fact]
    public void FailsOpenForNonAffineNonFiniteAndInvalidRayInputs()
    {
        Matrixf nonAffine = Transform(20, 0, 0, 1, 1, 1);
        nonAffine.Values[3] = 0.01f;
        Assert.False(IsBeyondRayLength(nonAffine, new Vec3d(), 1));

        Matrixf nonFinite = Transform(20, 0, 0, 1, 1, 1);
        nonFinite.Values[0] = float.NaN;
        Assert.False(IsBeyondRayLength(nonFinite, new Vec3d(), 1));

        Matrixf distant = Transform(20, 0, 0, 1, 1, 1);
        Assert.False(IsBeyondRayLength(distant, new Vec3d(), double.PositiveInfinity));
        Assert.False(IsBeyondRayLength(distant, new Vec3d(double.NaN, 0, 0), 1));
    }

    private static Matrixf Transform(float x, float y, float z, float scaleX, float scaleY, float scaleZ)
    {
        return new Matrixf(new float[]
        {
            scaleX, 0, 0, 0,
            0, scaleY, 0, 0,
            0, 0, scaleZ, 0,
            x, y, z, 1
        });
    }

    private static bool IsBeyondRayLength(Matrixf transform, Vec3d origin, double rayLengthSq)
    {
        return (bool)IsBeyondRayLengthMethod.Invoke(null, new object[] { transform, origin, rayLengthSq })!;
    }

    private static bool IsEntityBeyondRayRange(
        double rayOriginX, double rayOriginY, double rayOriginZ,
        double entityX, double entityY, double entityZ,
        Cuboidf selectionBox, double rayLength)
    {
        return (bool)IsEntityBeyondRayRangeMethod.Invoke(null, new object[]
        {
            rayOriginX, rayOriginY, rayOriginZ, entityX, entityY, entityZ, selectionBox, rayLength
        })!;
    }

    private static Matrixf RandomAffineTransform(Random random)
    {
        float Next() => (float)(random.NextDouble() * 40 - 20);
        float[] values =
        {
            Next(), Next(), Next(), 0,
            Next(), Next(), Next(), 0,
            Next(), Next(), Next(), 0,
            Next(), Next(), Next(), 1
        };
        return new Matrixf(values);
    }
}
