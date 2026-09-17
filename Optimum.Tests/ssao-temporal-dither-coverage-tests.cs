using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// GTAO roadmap step 2: vanilla's SSAO rotates its sample kernel with a Bayer-128
/// dither locked to the pixel grid, so under a jittered camera every surface point
/// draws a different kernel every frame and no temporal accumulator can average it.
/// Optimum's override advances that dither by the golden ratio per frame, using the
/// pipeline's own frame index - and only while a temporal consumer owns the frame,
/// because a per-frame-varying dither with nothing accumulating behind it is
/// strictly worse than the fixed one.
/// </summary>
public class SsaoTemporalDitherCoverageTests
{
    [Fact]
    public void TheOverrideExistsAndOnlyAddsTheTemporalDither()
    {
        string shader = Read("sources/shaders/ssao.fsh");

        // The vanilla dither is still the base: the temporal term is added to it,
        // it does not replace it.
        Assert.Contains("float dither = bayer128(texcoord * screenSize);", shader);
        Assert.Contains("dither = fract(dither + fract(temporalFrameIndex * (PHI - 1.0)));", shader);
        Assert.Contains("uniform float temporalFrameIndex;", shader);
    }

    /// <summary>
    /// Both the uniform and the frame-varying term sit inside <c>#if TAAMOTION == 1</c>,
    /// which ShaderRegistry stamps from OptimumConfig.EffectiveTaa. With
    /// TAAMOTION 0 the file preprocesses back to vanilla.
    /// </summary>
    [Fact]
    public void TheFrameVaryingTermIsGatedOnTheTemporalPipeline()
    {
        string shader = Read("sources/shaders/ssao.fsh");
        foreach (string guarded in new[]
        {
            "uniform float temporalFrameIndex;",
            "dither = fract(dither + fract(temporalFrameIndex * (PHI - 1.0)));"
        })
        {
            int at = shader.IndexOf(guarded, StringComparison.Ordinal);
            Assert.True(at > 0, guarded + " missing");
            int opened = shader.LastIndexOf("#if TAAMOTION == 1", at, StringComparison.Ordinal);
            int closed = shader.LastIndexOf("#endif", at, StringComparison.Ordinal);
            Assert.True(opened > 0 && opened > closed, guarded + " is not inside #if TAAMOTION == 1");
        }

        Assert.Contains(
            "#define TAAMOTION \" + (taaMotion ? 1 : 0)",
            Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs"));
        Assert.Contains(
            "bool taaMotion = OptimumConfig.EffectiveTaa;",
            Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs"));
    }

    /// <summary>
    /// Every line the shader changes has to preprocess away when TAAMOTION is 0: the
    /// override must be the vanilla file plus guarded blocks, nothing removed and
    /// nothing rewritten, so a game update stays a small re-apply.
    /// </summary>
    [Fact]
    public void WithoutATemporalConsumerTheOverrideIsTheVanillaShader()
    {
        // Read the pristine shader from the client archive, never from the deployed
        // client directory: `make deploy` copies our own overrides in there, so a
        // deployed checkout compared the override against itself - and against a
        // copy that already carried the TAAMOTION blocks, which fails for the wrong
        // reason. The vanilla shaders are proprietary and never committed, so a
        // checkout without the archive has nothing to compare against.
        string? vanilla = VanillaShaderArchive.TryRead("assets/game/shaders/ssao.fsh");
        if (vanilla == null) return;

        string preprocessed = StripTaaMotionBlocks(Read("sources/shaders/ssao.fsh"));
        Assert.Equal(vanilla.Replace("\r\n", "\n"), preprocessed.Replace("\r\n", "\n"));
    }

    /// <summary>Drops every <c>#if TAAMOTION == 1</c> ... <c>#endif</c> block, lines included.</summary>
    private static string StripTaaMotionBlocks(string shader)
    {
        var kept = new System.Text.StringBuilder();
        bool skipping = false;
        foreach (string line in shader.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r').Trim();
            if (!skipping && trimmed == "#if TAAMOTION == 1") { skipping = true; continue; }
            if (skipping)
            {
                if (trimmed == "#endif") skipping = false;
                continue;
            }
            kept.Append(line).Append('\n');
        }
        Assert.False(skipping, "unterminated #if TAAMOTION block");
        return kept.ToString().TrimEnd('\n');
    }

    [Fact]
    public void ThePassSetsTheFrameIndexUnderTheSameCondition()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        int start = platform.IndexOf("public override void RenderPostprocessingEffects", StringComparison.Ordinal);
        Assert.True(start > 0);
        string post = platform[start..platform.IndexOf("public override void ClearSsaoTarget", start, StringComparison.Ordinal)];

        // Same clock as the jitter and the resolve: OptimumTemporal's frame index,
        // wrapped only so it stays exact in a float.
        Assert.Contains("if (OptimumConfig.EffectiveTaa)", post);
        Assert.Contains(
            "ssao.Uniform(\"temporalFrameIndex\", (float)(OptimumTemporal.Frame.FrameIndex & 1023L));",
            post);
        // Set on the bound SSAO program, before the draw that reads it.
        int set = post.IndexOf("ssao.Uniform(\"temporalFrameIndex\"", StringComparison.Ordinal);
        Assert.True(post.IndexOf("ssao.Use();", StringComparison.Ordinal) < set);
        Assert.True(set < post.IndexOf("RenderFullscreenTriangle(screenQuad);", set, StringComparison.Ordinal));
        Assert.True(set < post.IndexOf("ssao.Stop();", StringComparison.Ordinal));

        // The method is a Cecil transplant; an edited body only ships if it is listed.
        Assert.Contains(
            "new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderPostprocessingEffects\", 1)",
            Read("Optimum.Patcher/Program.cs"));
    }

    /// <summary>
    /// The override only does anything if it reaches the install. The deploy and the
    /// Linux/macOS packagers copy sources/shaders wholesale and then verify every file
    /// arrived; the Windows packager names its files one by one, so ssao.fsh has to be
    /// in that list or a release silently runs vanilla's shader.
    /// </summary>
    [Fact]
    public void TheShaderShipsInTheDeployAndPackagingLists()
    {
        string makefile = Read("Makefile");
        Assert.Contains("for f in sources/shaders/*;", makefile);
        Assert.Contains("did not reach", makefile);
        Assert.Contains("'assets/game/shaders/ssao.fsh'", Read("scripts/package.ps1"));
        Assert.Contains("shader source file(s) never reached the staged assets", Read("scripts/package-linux.sh"));
        Assert.Contains("shader source file(s) never reached the staged assets", Read("scripts/package-macos.sh"));
    }

    private static string Root()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return root;
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root(), path));
}
