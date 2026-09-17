using System.Collections.Generic;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// How OptimumConfig reads the launcher's shader compatibility report: schema 2's <c>rewriterPrograms</c>, the
/// programs a mod's GLSL replaced, which the Vulkan renderer links through the rewriter instead of the native
/// SPIR-V (docs/vulkan-native-shaders.md section 8). Anything that cannot say which programs are safe counts as
/// "all", the scanner's own conservative rule.
///
/// The pure parse is tested rather than <c>SetDataPath</c>: loading a report sets the static scan state every
/// other OptimumConfig test reads in parallel (a failed scan disables the shader features).
/// </summary>
public sealed class ShaderCompatibilityConfigTests
{
    private static IReadOnlyCollection<string> Parse(string? json) => OptimumConfig.ParseShaderRewriterPrograms(json);

    private static bool Overridden(IReadOnlyCollection<string> programs, string name) =>
        OptimumConfig.IsShaderProgramOverriddenBy(programs, name);

    [Fact]
    public void NamedProgramsAreOverriddenAndOthersAreNot()
    {
        var programs = Parse("""{ "schemaVersion": 2, "scanFailed": false, "rewriterPrograms": ["blit", " chunkopaque ", ""] }""");
        Assert.Equal(new[] { "blit", "chunkopaque" }, programs);
        Assert.True(Overridden(programs, "blit"));
        Assert.True(Overridden(programs, "ChunkOpaque"));
        Assert.False(Overridden(programs, "final"));
        Assert.False(Overridden(programs, OptimumConfig.AllShaderPrograms));
        Assert.False(Overridden(programs, ""));
    }

    [Fact]
    public void AnEmptyListLeavesEveryProgramNative()
    {
        var programs = Parse("""{ "schemaVersion": 2, "scanFailed": false, "rewriterPrograms": [] }""");
        Assert.Empty(programs);
        Assert.False(Overridden(programs, "final"));
        Assert.False(Overridden(programs, OptimumConfig.AllShaderPrograms));
    }

    [Fact]
    public void TheAllEntryOverridesEveryProgram() =>
        AssertAll(Parse("""{ "schemaVersion": 2, "scanFailed": false, "rewriterPrograms": ["all"] }"""));

    [Fact]
    public void AReportWithoutTheFieldCountsAsAll() =>
        AssertAll(Parse("""{ "schemaVersion": 2, "scanFailed": false, "disabledFeatures": [] }"""));

    [Fact]
    public void AVersionOneReportCountsAsAll()
    {
        AssertAll(Parse("""{ "schemaVersion": 1, "scanFailed": false, "rewriterPrograms": [] }"""));
        AssertAll(Parse("""{ "scanFailed": false, "disabledFeatures": [] }"""));
    }

    [Fact]
    public void AFailedScanCountsAsAll() =>
        AssertAll(Parse("""{ "schemaVersion": 2, "scanFailed": true, "rewriterPrograms": [] }"""));

    [Fact]
    public void AMissingOrUnreadableReportCountsAsAll()
    {
        AssertAll(Parse(null));
        AssertAll(Parse(""));
        AssertAll(Parse("{ not json"));
        AssertAll(Parse("null"));
    }

    private static void AssertAll(IReadOnlyCollection<string> programs)
    {
        Assert.Equal(new[] { OptimumConfig.AllShaderPrograms }, programs);
        Assert.True(Overridden(programs, "final"));
        Assert.True(Overridden(programs, "blit"));
    }
}
