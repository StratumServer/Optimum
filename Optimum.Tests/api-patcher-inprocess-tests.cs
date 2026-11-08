using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class InProcessPatcherInvocationTests
{
    [Fact]
    public void ProgramRunReturnsErrorCodeOnMissingArguments()
    {
        int exitCode = Program.Run([]);
        Assert.Equal(1, exitCode);
    }
}
