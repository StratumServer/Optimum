using Optimum.Render.Vulkan.Shaders;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The pipeline masks off colour attachments whose fragment output is never
/// stored to (GL keeps their contents; Vulkan would write undefined values).
/// The scan has to see every real store and no declaration.
/// </summary>
public class FragmentOutputAssignmentTests
{
    [Theory]
    [InlineData("out vec4 outColor;\nvoid main(){ outColor = vec4(1.0); }", "outColor", true)]
    [InlineData("out vec4 outGlow;\nvoid main(){ outGlow.rgb = vec3(0.0); }", "outGlow", true)]
    [InlineData("out vec4 outGlow;\nvoid main(){ outGlow.a += 0.5; }", "outGlow", true)]
    [InlineData("out vec4 arr[2];\nvoid main(){ arr[1] = vec4(0.0); }", "arr", true)]
    [InlineData("layout(location = 3) out vec4 outGPosition;\nvoid main(){ }", "outGPosition", false)]
    [InlineData("out vec4 outGNormal;\nvoid main(){ if (outGNormal == vec4(0.0)) discard; }", "outGNormal", false)]
    [InlineData("out vec4 outColor;\nvoid OIT(vec4 c){ outColor = c; }\nvoid main(){ OIT(vec4(1.0)); }", "outColor", true)]
    [InlineData("out vec4 outColor;\nout vec4 outColorHi;\nvoid main(){ outColorHi = vec4(1.0); }", "outColor", false)]
    public void DetectsStoresAndIgnoresDeclarationsAndReads(string source, string name, bool expected)
    {
        Assert.Equal(expected, ProgramInterfaceLayout.FragmentOutputIsAssigned(source, name));
    }
}
