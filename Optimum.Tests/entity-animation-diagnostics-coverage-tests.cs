using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("EntityAnimationDiagnostics")]
public class EntityAnimationDiagnosticsCoverageTests
{
    [Fact]
    public void AnimationPathsExposeDistanceContextAndMeasurements()
    {
        string manager = Read("VintagestoryApi/Common/Model/Animation/AnimationManager.cs");
        string animator = Read("VintagestoryApi/Common/Model/Animation/ClientAnimator.cs");
        string renderer = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");

        Assert.Contains("RecordEntityAnimationManagerCall", manager);
        Assert.Contains("RecordEntityAnimationPoseSkip", manager);
        Assert.Contains("RecordEntityAnimationMatrixBuild", animator);
        Assert.Contains("RecordEntityAnimationMatrixSkip", animator);
        Assert.Contains("BeginEntityAnimationContext", renderer);
        Assert.Contains("EndEntityAnimationContext", renderer);
    }

    [Fact]
    public void SummarySeparatesPlayerNearMidFarAndUnknownWork()
    {
        bool previous = OptimumDiagnostics.StutterWatchEnabled;
        try
        {
            OptimumDiagnostics.ResetEntityAnimation();
            OptimumDiagnostics.StutterWatchEnabled = true;

            OptimumDiagnostics.BeginEntityAnimationContext(isPlayer: true, distanceSq: 0);
            OptimumDiagnostics.RecordEntityAnimationManagerCall();
            OptimumDiagnostics.RecordEntityAnimationHeadUpdate();
            OptimumDiagnostics.RecordEntityAnimationPoseUpdate();
            OptimumDiagnostics.RecordEntityAnimationMatrixBuild();
            OptimumDiagnostics.RecordEntityAnimationMatrixTicks(1);
            OptimumDiagnostics.EndEntityAnimationContext();

            OptimumDiagnostics.BeginEntityAnimationContext(isPlayer: false, distanceSq: 30 * 30);
            OptimumDiagnostics.RecordEntityAnimationManagerCall();
            OptimumDiagnostics.RecordEntityAnimationPoseSkip();
            OptimumDiagnostics.RecordEntityAnimationMatrixSkip();
            OptimumDiagnostics.EndEntityAnimationContext();

            OptimumDiagnostics.RecordEntityAnimationManagerCall();

            string summary = OptimumDiagnostics.GetEntityAnimationSummary();
            Assert.Contains("managerCalls=3", summary);
            Assert.Contains("headUpdates=1", summary);
            Assert.Contains("poseUpdates=1", summary);
            Assert.Contains("poseSkips=1", summary);
            Assert.Contains("matrixBuilds=1", summary);
            Assert.Contains("matrixSkips=1", summary);
            Assert.Contains("player:1/1/0/1/0", summary);
            Assert.Contains("mid:1/0/1/0/1", summary);
            Assert.Contains("unknown:1/0/0/0/0", summary);
        }
        finally
        {
            OptimumDiagnostics.ResetEntityAnimation();
            OptimumDiagnostics.StutterWatchEnabled = previous;
        }
    }

    private static string Read(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
