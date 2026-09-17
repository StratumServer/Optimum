using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// World/UI separation (VulkanClientPlatform.UiSeparation.cs): where each piece sits. The GPU
/// behaviour is UiSeparationTests; these pin the placements a refactor could move across the call
/// they belong to - a compose after the Done stage records the HUD-less image in every screenshot,
/// a bind before the blit's last route leaves the GUI on the window, a scope nobody closes blends
/// the next world pass under the UI factors.
/// </summary>
public class UiSeparationCoverageTests
{
    private const string PlatformPath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.UiSeparation.cs";
    private const string GraphPath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs";

    [Fact]
    public void ClientMainComposesAfterTheOrthoStageAndBeforeDone()
    {
        string body = StripComments(Body(ReadLib("Vintagestory.Client.NoObf/ClientMain.cs"),
            "public void RenderToDefaultFramebuffer(float dt)"));
        int ortho = body.IndexOf("rendOrthoDone", StringComparison.Ordinal);
        int compose = body.IndexOf("Platform.OptimumComposeUiTarget();", StringComparison.Ordinal);
        int done = body.IndexOf("TriggerRenderStage(EnumRenderStage.Done, dt);", StringComparison.Ordinal);
        Assert.True(ortho >= 0 && compose > ortho && done > compose,
            "the compose must sit between the Ortho stage and the Done stage:\n" + body);
        Assert.Single(Regex.Matches(body, @"OptimumComposeUiTarget\(\)"));

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientMain\", \"RenderToDefaultFramebuffer\", 1),", patcher);
    }

    [Fact]
    public void TheMenuScreensComposeInScreenManager()
    {
        string body = StripComments(Body(ReadLib("Vintagestory.Client/ScreenManager.cs"), "internal void Render(float dt)"));
        int blit = body.IndexOf("Platform.BlitPrimaryToDefault();", StringComparison.Ordinal);
        int screen = body.IndexOf("CurrentScreen.RenderToDefaultFramebuffer(dt);", StringComparison.Ordinal);
        int compose = body.IndexOf("Platform.OptimumComposeUiTarget();", StringComparison.Ordinal);
        Assert.True(blit >= 0 && screen > blit && compose > screen,
            "the menu compose must follow the screen's own drawing:\n" + body);
        Assert.Contains("new(\"Vintagestory.Client.ScreenManager\", \"Render\", 1),", Read("Optimum.Patcher/Program.cs"));
    }

    [Fact]
    public void TheComposeIsANeutralVirtualThatOnlyVulkanOverrides()
    {
        string platform = ReadLib("Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");
        Assert.Equal("{ }", Regex.Replace(Body(platform, "public virtual void OptimumComposeUiTarget()"), @"\s+", " ").Trim());
        Assert.DoesNotContain("OptimumComposeUiTarget", ReadLib("Vintagestory.Client.NoObf/ClientPlatformWindows.cs"));

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumComposeUiTarget\",", patcher);
        Assert.Contains("new(true, \"OptimumComposeUiTarget\", Array.Empty<string>()),",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs"));
        Assert.Contains("public override void OptimumComposeUiTarget()", Read(PlatformPath));
    }

    [Fact]
    public void TheComposeProgramIsRegisteredAndShipsBothTwins()
    {
        Assert.Contains("public static ShaderProgram UiCompose;", ReadLib("Vintagestory.Client.NoObf/ShaderPrograms.cs"));
        string registry = ReadLib("Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains("RegisterOptimumShaderProgram(\"ui-compose\", ShaderPrograms.UiCompose = new ShaderProgram());", registry);
        Assert.Contains("shaderProgram == ShaderPrograms.UiCompose)", registry);
        Assert.Contains("\"UiCompose\",", Read("Optimum.Patcher/Program.cs"));

        // A pass-through: blit.fsh's forced alpha of 1 would cover the world with the UI image.
        foreach (string file in new[] { "sources/shaders/ui-compose.fsh", "sources/shaders-vk/ui-compose.frag" })
        {
            string fragment = StripComments(Read(file));
            Assert.DoesNotContain(".a = 1", fragment);
            Assert.Matches(new Regex(@"outColor = texture\((optimumTextures2D\[uiTex\]|uiTex), texCoord\);"), fragment);
        }
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/shaders/ui-compose.vsh")));
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/shaders-vk/ui-compose.vert")));
        Assert.True(File.Exists(PatchReader.FindRepositoryFile("sources/shaders-vk/ui-compose.interface.glsl")));
    }

    [Fact]
    public void TheScopeOpensAtTheBlitsEndAndTheSnapshotAtTheCompositionsEnd()
    {
        string graph = Read(GraphPath);

        string blit = Body(graph, "    public override void BlitPrimaryToDefault()");
        int native = blit.IndexOf("RenderNativeBlit();", StringComparison.Ordinal);
        int glRoute = blit.IndexOf("base.BlitPrimaryToDefault();", StringComparison.Ordinal);
        int open = blit.IndexOf("OpenUiScope();", StringComparison.Ordinal);
        Assert.True(native >= 0 && glRoute > native && open > glRoute, "the scope must open after both routes:\n" + blit);
        Assert.DoesNotContain("return;", blit);

        string composition = Body(graph, "    public override void RenderFinalComposition()");
        int legacy = composition.IndexOf("LegacyFinalComposition();", StringComparison.Ordinal);
        int capture = composition.IndexOf("CaptureSceneNoHud();", StringComparison.Ordinal);
        Assert.True(legacy >= 0 && capture > legacy, "the snapshot must follow both routes:\n" + composition);
        Assert.DoesNotContain("return;", composition);
    }

    [Fact]
    public void EveryScopeIsClosedBeforeTheWorldDrawsAgain()
    {
        string frame = Body(Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs"),
            "    public override void BeginFrame()");
        int close = frame.IndexOf("CloseUiScope();", StringComparison.Ordinal);
        int begin = frame.IndexOf("device.BeginFrame();", StringComparison.Ordinal);
        Assert.True(close >= 0 && begin > close, "BeginFrame must close the scope first:\n" + frame);

        string framebuffers = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("CloseUiScope();", Body(framebuffers, "    public override void DisposeFrameBuffers("));
        Assert.Contains("AllocateUiSeparationTargets(list, width, height);",
            Body(framebuffers, "    public override List<FrameBufferRef> SetupDefaultFrameBuffers()"));

        // The compose closes the scope before anything can return or draw.
        string compose = Body(Read(PlatformPath), "    public override void OptimumComposeUiTarget()");
        int closed = compose.IndexOf("CloseUiScope();", StringComparison.Ordinal);
        int firstReturn = compose.IndexOf("return;", compose.IndexOf("UiScopeOpen", StringComparison.Ordinal) + 20,
            StringComparison.Ordinal);
        int draw = compose.IndexOf("DrawNativeFullscreen", StringComparison.Ordinal);
        Assert.True(closed >= 0 && firstReturn > closed && draw > closed, compose);
    }

    [Fact]
    public void DefaultResolvesToTheUiImageOnlyThroughTheDevicesOneResolver()
    {
        string native = Read("Optimum.Render.Vulkan/VulkanDevice.Native.cs");
        // Expression-bodied, so read up to the member's semicolon.
        int start = native.IndexOf("private int ResolveNativeFramebuffer(int framebufferId) =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "the resolver is gone");
        string resolver = native.Substring(start, native.IndexOf(';', start) - start);
        Assert.Contains("_defaultRedirect > 0 ? _defaultRedirect : _defaultFramebuffer", resolver);
        Assert.Single(Regex.Matches(native, @"_defaultRedirect = "));

        string stated = Read("Optimum.Render.Vulkan/Platform/StatedRenderState.cs");
        Assert.Contains("if (IsUiImage(framebufferId)) blend = blend.ForUiImage();", stated);
        Assert.Contains("stated.IsUiImage(framebufferId)", Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeGui.cs"));
        Assert.Contains("stated.IsUiImage(", Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeWorld.cs"));
    }

    [Fact]
    public void TheParityDumpNamesBothSlots()
    {
        string windows = ReadLib("Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains("private const int OptimumSceneNoHudIndex = 23;", windows);
        Assert.Contains("private const int OptimumUiTargetIndex = 24;", windows);
        Assert.Contains("return \"OptimumSceneNoHud\";", windows);
        Assert.Contains("return \"OptimumUiTarget\";", windows);
        string platform = Read(PlatformPath);
        Assert.Contains("internal const int OptimumSceneNoHudIndex = 23;", platform);
        Assert.Contains("internal const int OptimumUiTargetIndex = 24;", platform);
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
}
