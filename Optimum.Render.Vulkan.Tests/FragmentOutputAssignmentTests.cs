using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
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

    private static ProgramInterfaceLayout LayoutOf(string fragmentSource)
        => ProgramInterfaceLayout.Build(new List<(EnumShaderType, ParsedShader)>
        {
            (EnumShaderType.VertexShader, GlslParser.Parse("#version 330 core\nvoid main(){ }")),
            (EnumShaderType.FragmentShader, GlslParser.Parse(fragmentSource)),
        });

    /// <summary>
    /// An output array with only a constant element stored to marks just that
    /// element's location written. Marking the whole span would leave colour
    /// writes on for an attachment the shader never touches - GL keeps such an
    /// attachment, Vulkan fills it with undefined data.
    /// </summary>
    [Fact]
    public void AConstantArrayIndexMarksOnlyThatElement()
    {
        ProgramInterfaceLayout layout = LayoutOf(
            "#version 330 core\nlayout(location = 0) out vec4 motion[2];\n" +
            "void main(){ motion[1] = vec4(1.0); }");

        Assert.Equal(new[] { 1 }, layout.WrittenFragmentOutputs.OrderBy(i => i).ToArray());
    }

    /// <summary>Several constant indices each mark their own location, and no others.</summary>
    [Fact]
    public void SeveralConstantArrayIndicesMarkEachElement()
    {
        ProgramInterfaceLayout layout = LayoutOf(
            "#version 330 core\nlayout(location = 0) out vec4 motion[3];\n" +
            "void main(){ motion[0] = vec4(1.0); motion[2].rgb = vec3(0.0); }");

        Assert.Equal(new[] { 0, 2 }, layout.WrittenFragmentOutputs.OrderBy(i => i).ToArray());
    }

    /// <summary>
    /// A dynamic index could hit any element, so the whole span stays written -
    /// masking a written attachment off would be the worse failure.
    /// </summary>
    [Fact]
    public void ADynamicArrayIndexKeepsTheWholeSpan()
    {
        ProgramInterfaceLayout layout = LayoutOf(
            "#version 330 core\nlayout(location = 0) out vec4 motion[2];\nuniform int slot;\n" +
            "void main(){ motion[slot] = vec4(1.0); }");

        Assert.Equal(new[] { 0, 1 }, layout.WrittenFragmentOutputs.OrderBy(i => i).ToArray());
    }

    /// <summary>A store to the array as a whole writes every element.</summary>
    [Fact]
    public void AWholeArrayStoreKeepsTheWholeSpan()
    {
        ProgramInterfaceLayout layout = LayoutOf(
            "#version 330 core\nlayout(location = 0) out vec4 motion[2];\nuniform vec4 src[2];\n" +
            "void main(){ motion = src; }");

        Assert.Equal(new[] { 0, 1 }, layout.WrittenFragmentOutputs.OrderBy(i => i).ToArray());
    }

    /// <summary>
    /// Non-array outputs are untouched by the element tracking, including the
    /// component-indexed store, where the index selects a channel rather than
    /// an attachment.
    /// </summary>
    [Fact]
    public void NonArrayOutputsAreUnchanged()
    {
        ProgramInterfaceLayout layout = LayoutOf(
            "#version 330 core\nlayout(location = 0) out vec4 outColor;\n" +
            "layout(location = 1) out vec4 outGlow;\nlayout(location = 2) out vec4 outUntouched;\n" +
            "void main(){ outColor = vec4(1.0); outGlow[2] = 0.5; }");

        Assert.Equal(new[] { 0, 1 }, layout.WrittenFragmentOutputs.OrderBy(i => i).ToArray());
    }
}
