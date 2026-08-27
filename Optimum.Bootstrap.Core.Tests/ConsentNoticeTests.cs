using Optimum.Bootstrap.Core.Licensing;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class ConsentNoticeTests
{
    [Fact]
    public void TheNoticeLoadsAndNamesTheDecompilation()
    {
        Assert.Contains("decompil", ConsentNotice.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNoticeMatchesTheLicenseAudit()
    {
        // LICENSE-SCOPE.md grants MIT to a listed path set; the whole-project
        // "GPLv3 with the Commons Clause" claim in install-windows.ps1 is wrong
        // and must not survive into Core.
        Assert.Contains("MIT", ConsentNotice.Text);
        Assert.Contains("LICENSE-SCOPE.md", ConsentNotice.Text);
        Assert.DoesNotContain("Commons Clause restriction", ConsentNotice.Text);
    }

    [Fact]
    public void TheNoticeStatesOptimumDoesNotRedistributeGameCode()
    {
        Assert.Contains("redistribute", ConsentNotice.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAcknowledgeFlagIsTheOneTheCliRequires()
    {
        Assert.Equal("--acknowledge-decompile", ConsentNotice.AcknowledgeFlag);
    }
}
