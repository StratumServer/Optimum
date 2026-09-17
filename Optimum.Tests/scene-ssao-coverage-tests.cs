using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// AO is derived from the jittered G-buffer. Applied after the TAA resolve (vanilla's
/// place, in Final) it never reaches the history and moves the whole frame by the raw
/// camera jitter. With TAA on it is multiplied into the scene before the resolve, and
/// Final skips its own multiply so the AO is applied exactly once.
/// </summary>
public class SceneSsaoCoverageTests
{
    [Fact]
    public void JitteredAoIsComposedBeforeTheResolveAndIsNotAppliedTwice()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        // Phase 3b: the AO step is its own virtual, and the chain calls it before the resolve.
        int start = platform.IndexOf("public virtual void OptimumPostAmbientOcclusion", StringComparison.Ordinal);
        Assert.True(start > 0);
        string post = platform[start..platform.IndexOf("public virtual int OptimumPostSceneTexture", start, StringComparison.Ordinal)];
        int chainStart = platform.IndexOf("public override void RenderPostprocessingEffects", StringComparison.Ordinal);
        string chain = platform[chainStart..start];

        int reset = post.IndexOf("optimumSsaoInScene = false;", StringComparison.Ordinal);
        int ssao = post.IndexOf("ssao.Use();", StringComparison.Ordinal);
        int apply = post.IndexOf("ApplyOptimumSceneSsao();", StringComparison.Ordinal);
        int aoStep = chain.IndexOf("OptimumPostAmbientOcclusion(projectMatrix);", StringComparison.Ordinal);
        int resolve = chain.IndexOf("RenderOptimumTaaResolve();", StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < ssao, "the flag is cleared before the SSAO pass");
        Assert.True(ssao < apply, "the AO is computed before it is composed");
        Assert.True(aoStep >= 0 && aoStep < resolve, "the AO is composed before the resolve");
        Assert.Equal(1, Count(post, "ssao.Use();"));
        Assert.Contains("if (OptimumTaaRequested && TaaTargetsReady)", post);

        // The flag means "AO is not Final's to apply": set when the AO was already multiplied
        // into the scene before the resolve, and also when AO is switched off entirely, where
        // nothing rendered into the SSAO target and multiplying by it would darken the frame.
        Assert.Contains("final.Uniform(\"optimumSsaoInScene\", (optimumSsaoInScene || !RenderSSAO) ? 1 : 0);", platform);
        Assert.Contains("optimumSsaoInScene = true;", platform);
        Assert.Contains("if (optimumSsaoInScene == 0)", Read("sources/shaders/final.fsh"));
        Assert.Contains("uniform int optimumSsaoInScene;", Read("sources/shaders/final.fsh"));

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"optimumSsaoInScene\"", patcher);
        Assert.Contains("\"ApplyOptimumSceneSsao\"", patcher);
        Assert.Contains("\"SceneSsao\"", patcher);
        string registry = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains("RegisterOptimumShaderProgram(\"scene-ssao\"", registry);
        Assert.Contains("shaderProgram == ShaderPrograms.SceneSsao", registry);

        string ssaoPass = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSsao.cs");
        Assert.Contains("BeginNativeAoPass(\"SceneSsao/\" + primary.FboId, primary.FboId,", ssaoPass);
        Assert.Contains("NativePostPipeline(nativeSceneSsao, composite, primary.FboId, 1u,", ssaoPass);
    }

    [Fact]
    public void TheSceneSsaoShaderShipsInTheWindowsPackage()
    {
        string package = Read("scripts/package.ps1");
        Assert.Contains("'assets/game/shaders/scene-ssao.vsh'", package);
        Assert.Contains("'assets/game/shaders/scene-ssao.fsh'", package);
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string Read(string path)
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return File.ReadAllText(Path.Combine(root, path));
    }
}
