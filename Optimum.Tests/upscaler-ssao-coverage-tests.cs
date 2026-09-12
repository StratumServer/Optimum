using System.IO;
using Xunit;

namespace Optimum.Tests;

public class UpscalerSsaoCoverageTests
{
    [Fact]
    public void JitteredAoIsComposedBeforeTheUpscalerAndIsNotAppliedTwice()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        int start = platform.IndexOf("public override void RenderPostprocessingEffects");
        string post = platform[start..platform.IndexOf("public override void ClearSsaoTarget", start)];
        Assert.True(post.IndexOf("ssao.Use();") < post.IndexOf("ApplyOptimumUpscaleSsao();"));
        Assert.True(post.IndexOf("ApplyOptimumUpscaleSsao();") < post.IndexOf("RenderOptimumUpscale();"));
        Assert.Contains("OptimumUpscalerActive && RenderSSAO && projectMatrix != null", post);
        Assert.Contains("optimumUpscaleSsaoApplied = false;", post);
        Assert.Contains("final.Uniform(\"optimumSsaoInScene\", optimumUpscaleSsaoApplied ? 1 : 0)", platform);
        Assert.Contains("if (optimumSsaoInScene == 0)", Read("sources/shaders/final.fsh"));
        Assert.Contains("\"optimumUpscaleSsaoApplied\"", Read("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"ApplyOptimumUpscaleSsao\"", Read("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"UpscaleSsao\"", Read("Optimum.Patcher/Program.cs"));
        Assert.Contains("RegisterOptimumShaderProgram(\"upscale-ssao\"", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs"));
        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        Assert.Contains("Name = \"UpscaleSsao/0\"", graph);
        Assert.Contains("ColorSlots = 1u", graph);
    }

    private static string Read(string path)
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return File.ReadAllText(Path.Combine(root, path));
    }
}
