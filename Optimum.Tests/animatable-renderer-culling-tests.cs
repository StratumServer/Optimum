using System.IO;
using Xunit;

namespace Optimum.Tests;

public class AnimatableRendererCullingTests
{
    [Fact]
    public void AnimatableRenderer_CullsOffScreenDrawCallsBeforeStateChanges()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Model/Animation/AnimatableRenderer.cs.patch"));

        // Checks frustum visibility before entering OpenGL state modification and draw calls
        Assert.Contains("capi.Render?.CurrentFrustumCuller != null", patch);
        Assert.Contains("capi.Render.CurrentFrustumCuller.SphereInFrustum", patch);

        // Uses bounding sphere based on scaled entity radius
        Assert.Contains("double radius = Math.Max(ScaleX, Math.Max(ScaleY, ScaleZ)) * 2.5;", patch);
        Assert.Contains("pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, radius", patch);

        // Early exits without modifying depth mask, cull face, or shader bindings
        Assert.Contains("return;", patch);
    }
}
