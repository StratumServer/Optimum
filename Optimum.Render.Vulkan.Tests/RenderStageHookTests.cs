using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Vulkan-native plan, Phase 2 (contract C3): the donor ClientMain.TriggerRenderStage brackets
/// each stage with the platform's BeginRenderStage/EndRenderStage, and VulkanClientPlatform
/// records the stage and forwards the bracket to its listener. Headless: the real
/// TriggerRenderStage runs on an uninitialised ClientMain (no event manager, so no renderers)
/// against a platform with no device; neither touches GL or Vulkan.
/// </summary>
public class RenderStageHookTests
{
    private sealed class RecordingListener : IRenderStageListener
    {
        public readonly List<string> Calls = new();
        public VulkanClientPlatform? Platform;
        public readonly List<string> Faults = new();

        public void OnBeginRenderStage(EnumRenderStage stage)
        {
            Calls.Add("begin " + stage);
            if (Platform != null && (!Platform.InRenderStage || Platform.CurrentRenderStage != stage))
                Faults.Add("begin " + stage + " saw stage " + Platform.CurrentRenderStage + " active=" + Platform.InRenderStage);
        }

        public void OnEndRenderStage(EnumRenderStage stage)
        {
            Calls.Add("end " + stage);
            if (Platform != null && (Platform.InRenderStage || Platform.CurrentRenderStage != stage))
                Faults.Add("end " + stage + " saw stage " + Platform.CurrentRenderStage + " active=" + Platform.InRenderStage);
        }
    }

    /// <summary>Every stage once, in declaration order (the order MainRenderLoop broadly follows).</summary>
    private static readonly EnumRenderStage[] FrameStages = (EnumRenderStage[])Enum.GetValues(typeof(EnumRenderStage));

    private static ClientMain HeadlessGame(ClientPlatformAbstract platform)
    {
        // TriggerRenderStage marks the profiler first; a disabled one returns immediately.
        ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static (string _) => { });
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.Platform = platform;
        return game;
    }

    [Fact]
    public void AListenerSeesBeginAndEndForEachStageInOrder()
    {
        var platform = new VulkanClientPlatform(null!);
        var listener = new RecordingListener { Platform = platform };
        platform.RenderStageListener = listener;
        ClientMain game = HeadlessGame(platform);

        for (int frame = 0; frame < 2; frame++)
        {
            foreach (EnumRenderStage stage in FrameStages)
            {
                game.TriggerRenderStage(stage, 0.016f);
                Assert.False(platform.InRenderStage);
                Assert.Equal(stage, platform.CurrentRenderStage);
            }
        }

        var expected = new List<string>();
        for (int frame = 0; frame < 2; frame++)
        {
            foreach (EnumRenderStage stage in FrameStages)
            {
                expected.Add("begin " + stage);
                expected.Add("end " + stage);
            }
        }
        Assert.Equal(expected, listener.Calls);
        Assert.Empty(listener.Faults);
        Assert.True(FrameStages.Length >= 10);
    }

    [Fact]
    public void WithoutAListenerTheBracketOnlyTracksTheStage()
    {
        var platform = new VulkanClientPlatform(null!);
        Assert.Null(platform.RenderStageListener);
        ClientMain game = HeadlessGame(platform);

        game.TriggerRenderStage(EnumRenderStage.Opaque, 0.016f);
        Assert.Equal(EnumRenderStage.Opaque, platform.CurrentRenderStage);
        Assert.False(platform.InRenderStage);

        platform.BeginRenderStage(EnumRenderStage.OIT);
        Assert.True(platform.InRenderStage);
        Assert.Equal(EnumRenderStage.OIT, platform.CurrentRenderStage);
        platform.EndRenderStage(EnumRenderStage.OIT);
        Assert.False(platform.InRenderStage);
    }

    [Fact]
    public void TheOpenGlPlatformKeepsTheNeutralBodies()
    {
        var platform = new ClientPlatformWindows(null!);
        ClientMain game = HeadlessGame(platform);

        foreach (EnumRenderStage stage in FrameStages)
            game.TriggerRenderStage(stage, 0.016f);

        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformAbstract.BeginRenderStage))!.DeclaringType);
        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformAbstract.EndRenderStage))!.DeclaringType);
    }
}
