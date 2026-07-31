using Vintagestory.API.Common;
using Xunit;

namespace Optimum.Tests;

public sealed class PerceptionEffectRestorationTests
{
    [Fact(Skip = "Requires fork patches: animVersion")]
    public void ShapeElementAppliesDrunkRotationOffsets()
    {
        ShapeElement element = new()
        {
            From = [0, 0, 0],
            RotationOrigin = [0, 0, 0]
        };

        float[] offsetMatrix = element.GetLocalTransformMatrix(animVersion: 0,
            tf: new ElementPose { degOffX = 12, degOffY = -7, degOffZ = 4 });
        float[] regularMatrix = element.GetLocalTransformMatrix(animVersion: 0,
            tf: new ElementPose { degX = 12, degY = -7, degZ = 4 });

        Assert.Equal(regularMatrix, offsetMatrix);
    }
}
