using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class NetworkAllocationAuditCoverageTests
{
    [Fact]
    public void AnimationSpeedBuffersCrossVirtualOwnershipBoundaries()
    {
        string process = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemNetworkProcess.cs");
        string entity = Read("VintagestoryApi/Common/Entity/Entity.cs");
        string manager = Read("VintagestoryApi/Common/Model/Animation/AnimationManager.cs");

        Assert.Contains("float[] array = new float[packet.activeAnimationSpeedsCount]", process);
        Assert.Contains("float[] array = new float[animationPacket.activeAnimationSpeedsCount]", process);
        Assert.Contains("public virtual void OnReceivedServerAnimations", entity);
        Assert.Contains("public virtual void OnReceivedServerAnimations", manager);
        Assert.DoesNotContain("ArrayPool<float>", process);
    }

    [Fact]
    public void GuiFrameCallbacksUseReusableScratchLists()
    {
        string gui = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/GuiManager.cs");

        Assert.Contains("_scratchFinalizeFrame ??= new List<GuiDialog>()", gui);
        Assert.Contains("_scratchKeyDownOpened ??= new List<GuiDialog>()", gui);
        Assert.Contains("_scratchMouseMove ??= new List<GuiDialog>()", gui);
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
