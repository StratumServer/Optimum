using System.IO;
using Xunit;

namespace Optimum.Tests;

public class AnimationUtilRestingTests
{
    [Fact]
    public void AnimationUtil_ParksRendererWhenEnteringRestState()
    {
        string patch = File.ReadAllText(
            PatchReader.FindRepositoryFile("patches/VintagestoryApi/Common/Model/Animation/AnimationUtil.cs.patch"));

        // Tracks renderer registration state
        Assert.Contains("private bool _isRendererRegistered;", patch);
        Assert.Contains("public bool IsParked => !_isRendererRegistered;", patch);

        // Registers on startup if client API is present
        Assert.Contains("capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, \"beanimutil\");", patch);
        Assert.Contains("_isRendererRegistered = true;", patch);

        // Unregisters from render loop when all animations finish and resting
        Assert.Contains("capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);", patch);
        Assert.Contains("_isRendererRegistered = false;", patch);

        // Re-awakens and re-registers immediately when StartAnimation is invoked
        Assert.Contains("if (!_isRendererRegistered && capi != null)", patch);
    }
}
