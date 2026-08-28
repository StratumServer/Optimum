using Optimum.Bootstrap.Core;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class EngineProtocolTests
{
    [Fact]
    public void EngineProgressCeilingIs99()
    {
        Assert.Equal(99, BootstrapProgress.MaxEnginePercent);
    }

    [Theory]
    [InlineData(FailureReason.BadInput, "bad-input")]
    [InlineData(FailureReason.PatchConflict, "patch-conflict")]
    [InlineData(FailureReason.Cancelled, "cancelled")]
    [InlineData(FailureReason.EngineInternal, "engine-internal")]
    public void FailureReasonWireTokensAreKebabCase(FailureReason reason, string expected)
    {
        Assert.Equal(expected, reason.Wire());
    }

    [Fact]
    public void EveryFailureReasonHasAWireToken()
    {
        foreach (FailureReason reason in Enum.GetValues<FailureReason>())
        {
            string wire = reason.Wire();
            Assert.False(string.IsNullOrWhiteSpace(wire));
            Assert.Equal(wire.ToLowerInvariant(), wire);
        }
    }

    [Fact]
    public void CoreVersionIsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(CoreInfo.Version));
    }
}
