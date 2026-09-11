using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 2 (contract C3): ClientMain.TriggerRenderStage brackets the stage's
/// renderers with ClientPlatformAbstract.BeginRenderStage/EndRenderStage, the method and both
/// virtuals reach the shipped DLL through Cecil, the OpenGL path keeps the neutral bodies and
/// VulkanClientPlatform overrides them (self-checked) and forwards to IRenderStageListener.
/// </summary>
public class RenderStageHooksCoverageTests
{
    private const string ClientMainPath = "Vintagestory.Client.NoObf/ClientMain.cs";
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string TriggerSignature = "public void TriggerRenderStage(EnumRenderStage stage, float dt)";

    [Fact]
    public void TriggerRenderStageBracketsTheEvent()
    {
        string body = Body(ReadLib(ClientMainPath), TriggerSignature);

        int begin = body.IndexOf("stagePlatform.BeginRenderStage(stage);", StringComparison.Ordinal);
        int trigger = body.IndexOf("eventManager?.TriggerRenderStage(stage, dt);", StringComparison.Ordinal);
        int end = body.IndexOf("stagePlatform.EndRenderStage(stage);", StringComparison.Ordinal);
        int glCheck = body.IndexOf("Platform.CheckGlError(", StringComparison.Ordinal);
        Assert.True(begin >= 0, "BeginRenderStage missing:\n" + body);
        Assert.True(trigger > begin, "the event does not follow BeginRenderStage:\n" + body);
        Assert.True(end > trigger, "EndRenderStage does not follow the event:\n" + body);
        Assert.True(glCheck > end, "the GL error check moved inside the bracket:\n" + body);
        Assert.Single(Regex.Matches(body, @"BeginRenderStage\("));
        Assert.Single(Regex.Matches(body, @"EndRenderStage\("));
    }

    [Fact]
    public void TriggerRenderStageIsCecilSafe()
    {
        string body = StripComments(Body(ReadLib(ClientMainPath), TriggerSignature));
        Assert.DoesNotContain("=>", body);
        Assert.DoesNotContain("delegate", body);
        Assert.DoesNotContain("static ", body);
        Assert.False(Regex.IsMatch(body, @"\.(All|Any|Where|Select|First|Count)\s*\("), "LINQ in a transplanted method:\n" + body);
    }

    [Fact]
    public void ThePatcherShipsTheMethodAndTheVirtuals()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientMain\", \"TriggerRenderStage\", 2),", patcher);

        string injected = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},");
        Assert.Contains("\"BeginRenderStage\",", injected);
        Assert.Contains("\"EndRenderStage\",", injected);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresNeutralVirtualsAndOpenGlDoesNotOverrideThem()
    {
        string platform = ReadLib(AbstractPath);
        foreach (string signature in new[]
        {
            "public virtual void BeginRenderStage(EnumRenderStage stage)",
            "public virtual void EndRenderStage(EnumRenderStage stage)",
        })
        {
            Assert.Equal("{ }", Regex.Replace(Body(platform, signature), @"\s+", " ").Trim());
        }

        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.DoesNotContain("BeginRenderStage", windows);
        Assert.DoesNotContain("EndRenderStage", windows);
    }

    [Fact]
    public void TheVulkanPlatformOverridesSelfChecksAndForwards()
    {
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");
        Assert.Contains("new(true, \"BeginRenderStage\", new[] { \"EnumRenderStage\" }),", selfCheck);
        Assert.Contains("new(true, \"EndRenderStage\", new[] { \"EnumRenderStage\" }),", selfCheck);

        string stages = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Stages.cs");
        string begin = Body(stages, "public override void BeginRenderStage(EnumRenderStage stage)");
        Assert.Contains("CurrentRenderStage = stage;", begin);
        Assert.Contains("RenderStageListener?.OnBeginRenderStage(stage);", begin);
        Assert.Contains("RenderStageListener?.OnEndRenderStage(stage);",
            Body(stages, "public override void EndRenderStage(EnumRenderStage stage)"));
        Assert.Contains("internal IRenderStageListener? RenderStageListener;", stages);

        string listener = Read("Optimum.Render.Vulkan/Graph/IRenderStageListener.cs");
        Assert.Contains("namespace Optimum.Render.Vulkan.Graph;", listener);
        Assert.Contains("internal interface IRenderStageListener", listener);
        Assert.Contains("void OnBeginRenderStage(EnumRenderStage stage);", listener);
        Assert.Contains("void OnEndRenderStage(EnumRenderStage stage);", listener);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string ReadLib(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/" + relativePath));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }
}
