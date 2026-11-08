using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class TimerResolutionTests
{
    [Fact]
    public void ClientPlatformWindowsPatch_ImplementsTimeBeginPeriodAndProcessExitCleanup()
    {
        string patchPath = PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch");
        string patch = File.ReadAllText(patchPath);

        // Verify winmm P/Invoke definitions
        Assert.Contains("[DllImport(\"winmm.dll\", EntryPoint = \"timeBeginPeriod\")]", patch);
        Assert.Contains("private static extern uint OptimumTimeBeginPeriod(uint uPeriod);", patch);
        Assert.Contains("[DllImport(\"winmm.dll\", EntryPoint = \"timeEndPeriod\")]", patch);
        Assert.Contains("private static extern uint OptimumTimeEndPeriod(uint uPeriod);", patch);

        // Verify 1ms resolution request and Windows OS guard
        Assert.Contains("if (!OperatingSystem.IsWindows()) return;", patch);
        Assert.Contains("OptimumTimeBeginPeriod(1);", patch);

        // Verify clean process exit cleanup pairing
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit += exitHandler;", patch);
        Assert.Contains("OptimumTimeEndPeriod(1);", patch);

        // Verify initialization is invoked during defaults setup
        Assert.Contains("EnsureOptimumTimerResolution();", patch);
    }
}
