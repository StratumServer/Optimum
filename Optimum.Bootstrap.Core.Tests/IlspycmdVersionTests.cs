using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>
/// Ports the ilspycmd version cases from
/// <c>scripts/tests/install-linux-prerequisites.sh</c>. These are the exact
/// accept and reject values that script pins.
/// </summary>
public class IlspycmdVersionTests
{
    private static readonly IlspycmdCompatibility Range = IlspycmdCompatibility.Fallback;

    [Theory]
    [InlineData("10.1.0.8386")]
    [InlineData("10.1.0.8387")]
    [InlineData("10.1.1.0")]
    [InlineData("10.1.1.8387")]
    [InlineData("10.1.1.8388")]
    public void AcceptsVersionsInsideTheRange(string version)
    {
        Assert.True(Range.Supports(version));
    }

    [Theory]
    [InlineData("10.1.0.8385")]
    [InlineData("10.1.1.8389")]
    [InlineData("10.1.2.9000")]
    [InlineData("10.0.1.8346")]
    [InlineData("10.2.0.1")]
    [InlineData("10.0.0.8323-preview3")]
    [InlineData("10.1.1.8388-rc1")]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("10.1.1")]
    public void RejectsEverythingElse(string version)
    {
        Assert.False(Range.Supports(version));
    }

    [Fact]
    public void ReadsTheRangeAndPinFromConfigFiles()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/repo/.config/ilspycmd-compat.json",
            """{ "minimumVersion": "10.1.0.8386", "maximumVersion": "10.1.1.8388" }""");
        probe.AddFile("/repo/.config/dotnet-tools.json",
            """{ "version": 1, "tools": { "ilspycmd": { "version": "10.1.1.8388" } } }""");

        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(probe, "/repo");

        Assert.Equal("10.1.1.8388", compat.Pin);
        Assert.Equal(new IlspycmdVersion(10, 1, 0, 8386), compat.Minimum);
        Assert.Equal(new IlspycmdVersion(10, 1, 1, 8388), compat.Maximum);
    }

    [Fact]
    public void FallsBackWhenConfigFilesAreAbsent()
    {
        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(new FakeSystemProbe(), "/repo");
        Assert.Equal(IlspycmdCompatibility.Fallback, compat);
    }
}
