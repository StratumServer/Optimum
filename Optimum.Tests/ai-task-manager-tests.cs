using System;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public sealed class AiTaskManagerTests
{
    [Fact]
    public void AiTaskManagerPatch_EliminatesGetInvocationListInSteadyState()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSEssentials/Entity/AI/AiTaskManager.cs.patch");
        Assert.True(File.Exists(patchPath), "AiTaskManager.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("_onShouldExecuteTaskCached", patched);
        Assert.Contains("_onExecuteTaskCached", patched);
        Assert.DoesNotContain("OnShouldExecuteTask.GetInvocationList()", patched);
        Assert.DoesNotContain("OnExecuteTask.GetInvocationList().Cast", patched);
    }

    [Fact]
    public void AiTaskManager_ShouldExecuteTask_ShortCircuitsAndHandlesSubscribers()
    {
        var manager = new AiTaskManager(null!);
        int firstCalled = 0;
        int secondCalled = 0;

        ActionBoolReturn<IAiTask> handler1 = task => { firstCalled++; return false; };
        ActionBoolReturn<IAiTask> handler2 = task => { secondCalled++; return true; };

        manager.OnShouldExecuteTask += handler1;
        manager.OnShouldExecuteTask += handler2;

        // Calling private method via reflection or testing ExecuteTask
        var shouldExecute = typeof(AiTaskManager).GetMethod("ShouldExecuteTask",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(shouldExecute);

        bool result = (bool)shouldExecute.Invoke(manager, new object?[] { null })!;
        Assert.False(result);
        Assert.Equal(1, firstCalled);
        Assert.Equal(0, secondCalled); // Short-circuited!

        // Remove handler1
        manager.OnShouldExecuteTask -= handler1;
        result = (bool)shouldExecute.Invoke(manager, new object?[] { null })!;
        Assert.True(result);
        Assert.Equal(1, firstCalled); // Not called again
        Assert.Equal(1, secondCalled); // Now called and returned true
    }
}
