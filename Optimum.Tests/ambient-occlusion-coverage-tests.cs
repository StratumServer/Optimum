using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the Vulkan ambient occlusion (docs/research/ambient-occlusion.md section C
/// and the section E decisions): the three-way setting and its backend rule, the post-chain seam
/// that lets the platform replace vanilla SSAO and composes before the TAA resolve, the shader
/// prefix and the class-channel writes that compile in only under it, the opt-in debug outputs,
/// the patcher listings and the licence notices of the ported compute shaders.
///
/// Text assertions prove the wiring exists; Optimum.Render.Vulkan.Tests/AmbientOcclusionTests
/// and SceneSsaoTests prove the numbers.
/// </summary>
public class AmbientOcclusionCoverageTests
{
    // ------------------------------------------------------------------ the setting

    [Fact]
    public void TheSettingDefaultsToAutoMediumAndPersistsThroughOptimumJson()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("public static string AmbientOcclusion = \"auto\";", config);
        Assert.Contains("public static string AmbientOcclusionPreset = \"medium\";", config);
        Assert.Contains("public string AmbientOcclusion { get; set; } = \"auto\";", config);
        Assert.Contains("public string AmbientOcclusionPreset { get; set; } = \"medium\";", config);
        Assert.Contains("AmbientOcclusion = AmbientOcclusion,", config);
        Assert.Contains("AmbientOcclusionPreset = AmbientOcclusionPreset,", config);
        Assert.Contains("(nameof(OptimumConfigData.AmbientOcclusion), AmbientOcclusion)", config);
        Assert.Contains("(nameof(OptimumConfigData.AmbientOcclusionPreset), AmbientOcclusionPreset)", config);
        // An unrecognised value degrades to the default instead of failing the file.
        Assert.Contains("AmbientOcclusion = requestedAo is \"vanilla\" or \"gtao\" ? requestedAo : \"auto\";", config);
        Assert.Contains("AmbientOcclusionPreset = requestedAoPreset is \"low\" or \"high\" or \"ultra\" ? requestedAoPreset : \"medium\";", config);
    }

    [Fact]
    public void AutoIsGtaoOnVulkanWithTaaVanillaOtherwiseAndOpenGlIgnoresTheSetting()
    {
        string saved = OptimumConfig.AmbientOcclusion;
        try
        {
            OptimumConfig.AmbientOcclusion = "auto";
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: false));

            OptimumConfig.AmbientOcclusion = "vanilla";
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: true));

            OptimumConfig.AmbientOcclusion = "gtao";
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: true));
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
            // OpenGL stays vanilla SSAO whatever is asked for.
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: true));
            Assert.False(OptimumConfig.GtaoSelected(vulkanBackend: false, taaActive: false));

            OptimumConfig.AmbientOcclusion = "GTAO";
            Assert.True(OptimumConfig.GtaoSelected(vulkanBackend: true, taaActive: false));
        }
        finally
        {
            OptimumConfig.AmbientOcclusion = saved;
        }

        // The effective form reads the backend that actually started and the TAA actually in effect.
        Assert.Contains("public static bool EffectiveGtao => GtaoSelected(OptimumRender.IsVulkan, EffectiveTaa);",
            Read("VintagestoryApi/Config/OptimumConfig.cs"));
    }

    // ------------------------------------------------------------------ the post chain

    [Fact]
    public void ThePlatformReplacesVanillaSsaoAndComposesBeforeTheResolve()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        // Phase 3b: the post body is one virtual per pass; the AO step is this one, and the
        // chain that calls it before the resolve is RenderPostprocessingEffects.
        string post = Between(platform, "public virtual void OptimumPostAmbientOcclusion",
            "public virtual int OptimumPostSceneTexture");
        string chain = Between(platform, "public override void RenderPostprocessingEffects",
            "public virtual void OptimumPostAmbientOcclusion");

        int reset = post.IndexOf("optimumAmbientOcclusionTexture = 0;", StringComparison.Ordinal);
        int ask = post.IndexOf("optimumAmbientOcclusionTexture = RenderOptimumAmbientOcclusion(projectMatrix);", StringComparison.Ordinal);
        int vanillaGuard = post.IndexOf("if (optimumAmbientOcclusionTexture == 0 && RenderSSAO && projectMatrix != null)", StringComparison.Ordinal);
        int ssao = post.IndexOf("ssao.Use();", StringComparison.Ordinal);
        int gtaoGuard = post.IndexOf("if (optimumAmbientOcclusionTexture != 0)", StringComparison.Ordinal);
        int compose = post.IndexOf("ApplyOptimumSceneSsao();", gtaoGuard, StringComparison.Ordinal);
        int aoStep = chain.IndexOf("OptimumPostAmbientOcclusion(projectMatrix);", StringComparison.Ordinal);
        int resolve = chain.IndexOf("RenderOptimumTaaResolve();", StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < ask, "the texture is cleared before the platform is asked");
        Assert.True(ask < vanillaGuard && vanillaGuard < ssao, "vanilla SSAO runs only when the platform returned nothing");
        Assert.True(ssao < gtaoGuard && gtaoGuard < compose,
            "the platform's AO is composed after the vanilla block");
        Assert.True(aoStep >= 0 && aoStep < resolve, "the AO step runs before the resolve");
        Assert.Contains("if (RenderSSAO && projectMatrix != null)\n\t\t{\n\t\t\toptimumAmbientOcclusionTexture = RenderOptimumAmbientOcclusion(projectMatrix);",
            post.Replace("\r\n", "\n"));

        // The neutral body is the OpenGL path: 0, vanilla SSAO. ClientPlatformWindows does not override it.
        string abstractPlatform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");
        Assert.Contains("public virtual int RenderOptimumAmbientOcclusion(float[] projectMatrix)\n\t{\n\t\treturn 0;\n\t}",
            abstractPlatform.Replace("\r\n", "\n"));
        Assert.Contains("public virtual int OptimumAmbientOcclusionDebugTexture(int index)\n\t{\n\t\treturn 0;\n\t}",
            abstractPlatform.Replace("\r\n", "\n"));
        Assert.DoesNotContain("override int RenderOptimumAmbientOcclusion", platform);

        // The compose binds the platform's texture, and the attenuation inputs only for the OPTIMUMAO shaders.
        string apply = Between(platform, "private void ApplyOptimumSceneSsao()", "public override void RenderFinalComposition");
        Assert.Contains("composite.BindTexture2D(\"ssaoScene\", optimumAmbientOcclusionTexture, 0);", apply);
        Assert.Contains("composite.BindTexture2D(\"ssaoScene\", frameBuffers[14].ColorTextureIds[0], 0);", apply);
        Assert.Contains("if (OptimumConfig.AmbientOcclusionShadersUseGtao)", apply);
        Assert.Contains("composite.BindTexture2D(\"gPositionScene\", frameBuffers[0].ColorTextureIds[3], 1);", apply);
        Assert.Contains("composite.BindTexture2D(\"revealageScene\", frameBuffers[1].ColorTextureIds[1], 2);", apply);
        Assert.Contains("optimumSsaoInScene = true;", apply);

        // Never on glow: the pass keeps colour 0 alone and reads the AO and attenuation inputs.
        string ssaoPass = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSsao.cs");
        string declaration = Between(ssaoPass, "private void NativeSceneSsaoPass()", "reads.ToArray(), clearWhite: false))");
        Assert.Contains("NativePostPipeline(nativeSceneSsao, composite, primary.FboId, 1u,", declaration);
        Assert.Contains("var reads = new List<int> { aoTexture };", declaration);
        Assert.Contains("int gPosition = gtao ? primary.ColorTextureIds[3] : 0;", declaration);
        Assert.Contains("int revealage = gtao ? transparent.ColorTextureIds[1] : 0;", declaration);

        // The Vulkan platform runs GTAO only for shaders built with it, never a half-res min hack.
        string vulkan = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.AmbientOcclusion.cs");
        Assert.Contains("public override int RenderOptimumAmbientOcclusion(float[] projectMatrix)", vulkan);
        Assert.Contains("if (!OptimumConfig.AmbientOcclusionShadersUseGtao) return 0;", vulkan);
        Assert.Contains("OptimumTemporal.Frame.FrameIndex", vulkan);
        Assert.Contains("bool temporal = OptimumConfig.EffectiveTaa && TaaTargetsReady;", vulkan);
    }

    [Fact]
    public void TheShaderPrefixStampsOptimumAoFromTheBackendAndTheGBuffer()
    {
        string registry = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        string prefixes = Between(registry, "private static void registerDefaultShaderCodePrefixes", "private static string HandleIncludes");
        Assert.Contains("bool optimumAo = OptimumConfig.EffectiveGtao && ClientSettings.SSAOQuality > 0;", prefixes);
        Assert.Contains("OptimumConfig.AmbientOcclusionShadersUseGtao = optimumAo;", prefixes);
        Assert.Contains("taaDefines = taaDefines + \"#define OPTIMUMAO \" + (optimumAo ? 1 : 0) + \"\\r\\n\";", prefixes);
        Assert.True(prefixes.IndexOf("#define OPTIMUMAO", StringComparison.Ordinal) <
                    prefixes.IndexOf("taaFrag.PrefixCode = taaFrag.PrefixCode + taaDefines;", StringComparison.Ordinal),
            "the define rides on the TAA defines both stages receive");
    }

    // ------------------------------------------------------------------ the class channel

    [Theory]
    [InlineData("sources/shaders/standard.fsh", "outGNormal.w = -1.0;", 1, new[] { "ALLOWDEPTHOFFSET > 0", "SSAOLEVEL > 0", "OPTIMUMAO > 0" })]
    [InlineData("sources/shaders/entityanimated.fsh", "outGNormal.w = -1.0;", 1, new[] { "ALLOWDEPTHOFFSET > 0", "USEOIT==0 && SSAOLEVEL > 0", "OPTIMUMAO > 0" })]
    public void ClassChannelWritesCompileInOnlyUnderTheirGuards(string path, string write, int expected, string[] guards)
    {
        string shader = Read(path);
        int found = 0;
        var stack = new List<string>();
        foreach (string raw in shader.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#if", StringComparison.Ordinal)) stack.Add(line);
            else if (line.StartsWith("#endif", StringComparison.Ordinal)) stack.RemoveAt(stack.Count - 1);
            else if (line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal))
                stack[^1] = "#else of " + stack[^1];
            else if (line.Contains(write, StringComparison.Ordinal))
            {
                found++;
                foreach (string guard in guards)
                {
                    Assert.True(stack.Any(s => s.StartsWith("#if", StringComparison.Ordinal) && s.Contains(guard, StringComparison.Ordinal)),
                        path + ": '" + write + "' is not inside #if " + guard + " (open: " + string.Join(" | ", stack) + ")");
                }
            }
        }
        Assert.Equal(expected, found);
        // Every OPTIMUMAO block is additive: nothing vanilla sits in an #else of it.
        Assert.DoesNotContain("#else", Regex.Matches(shader, @"#if OPTIMUMAO > 0[\s\S]*?#endif").Select(m => m.Value).FirstOrDefault() ?? "");
    }

    /// <summary>
    /// The thin class (C.5) is the vertex stage's wind flag, which vanilla writes into gnormal.w
    /// (grass, plants and leaves wave; blocks and snow layers do not). The fragment stage no longer
    /// forces it for a whole pool: the blend-no-cull pool holds solid blocks too - snow layers - so
    /// keying the class off haxyFade made 59 % of the visible pixels in a snow-covered world, 158 of
    /// 168 flat faces, 0.05-block occluders (2026-09-17).
    /// </summary>
    [Fact]
    public void TheThinClassIsTheVertexWindFlagAndTheComposeDropsTheRowMin()
    {
        string vertex = Read("sources/shaders/chunkopaque.vsh");
        Assert.Contains("bool isLeaves = ((renderFlags & WindModeBitMask) > 0);", vertex);
        Assert.Contains("gnormal.w = isLeaves ? 1 : 0;", vertex);
        foreach (string path in new[] { "sources/shaders/chunkopaque.fsh", "sources/shaders-vk/chunkopaque.frag" })
        {
            string chunk = Read(path);
            Assert.DoesNotContain("outGNormal.w = 1.0;", chunk);
            Assert.DoesNotContain("optimumThinClass", chunk);
            Assert.Contains("the thin class is the", chunk);
            Assert.Contains("vertex stage's wind flag", chunk);
        }
        Assert.DoesNotContain("optimumThinClass", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs"));
        Assert.DoesNotContain("optimumThinClass", Read("sources/shaders-vk/chunkopaque.interface.glsl"));

        string compose = Read("sources/shaders/scene-ssao.fsh").Replace("\r\n", "\n");
        Assert.Contains("#if OPTIMUMAO > 0\n    if (optimumAoMode == 1)", compose);
        Assert.Contains("ao = texelFetch(ssaoScene, texel, 0).r;", compose);
        Assert.Contains("max(0.0, 1.0 - texelFetch(revealageScene, texel, 0).r) * 0.75", compose);
        Assert.Contains("ao = 1.0 - (1.0 - ao) * (1.0 - attenuate);", compose);
        // The albedo hook exists, compiled out in the first version.
        Assert.Contains("#if OPTIMUMAO_MULTIBOUNCE > 0", compose);
        Assert.Contains("uniform sampler2D aoAlbedo;", compose);
        Assert.DoesNotContain("OPTIMUMAO_MULTIBOUNCE", Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs"));
    }

    // ------------------------------------------------------------------ outputs, patcher, licences

    [Fact]
    public void TheDebugOutputsAreOptInInTheParityDumpAndTheHeadlessHarness()
    {
        string device = Read("VintagestoryApi/Client/optimum-render-device.cs");
        Assert.Contains("public static readonly bool AmbientOcclusionOutputs = ResolveAmbientOcclusionOutputs();", device);
        Assert.Contains("Environment.GetEnvironmentVariable(\"OPTIMUM_AO_OUTPUTS\")", device);

        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string dump = Between(platform, "private void OptimumRunParityDump()", "private string OptimumParitySlotName(int slot)");
        Assert.Contains("if (OptimumParityDump.AmbientOcclusionOutputs)", dump);
        Assert.Contains("OptimumAmbientOcclusionDebugTexture(aoIndex)", dump);
        string names = Between(platform, "private string OptimumParitySlotName(int slot)", "private int OptimumParityDumpAttachment");
        foreach (string name in new[] { "OptimumAoWorking", "OptimumAoEdges", "OptimumAoDepthMip0", "OptimumAoOutput" })
            Assert.Contains("return \"" + name + "\";", names);
        string capture = Between(platform, "private void OptimumHeadlessCaptureFrame(long worldFrame)", "private void OptimumRunParityDump()");
        Assert.Contains("OptimumHeadlessWriteAmbientOcclusion(worldFrame);", capture);
        // Through the dump's single readback and writer call site, into the frame directory.
        Assert.Contains("OptimumParityDumpAttachment(OptimumHeadless.FrameDirectory, aoSlot, OptimumParitySlotName(aoSlot), attachment, aoTexture);", capture);

        string vulkan = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.AmbientOcclusion.cs");
        string debug = Between(vulkan, "public override int OptimumAmbientOcclusionDebugTexture(int index)", "private GtaoSettings AmbientOcclusionSettings");
        Assert.True(debug.IndexOf("WorkingTermTexture", StringComparison.Ordinal) < debug.IndexOf("EdgesTexture", StringComparison.Ordinal) &&
                    debug.IndexOf("EdgesTexture", StringComparison.Ordinal) < debug.IndexOf("WorkingDepthTexture", StringComparison.Ordinal) &&
                    debug.IndexOf("WorkingDepthTexture", StringComparison.Ordinal) < debug.IndexOf("OutputTexture", StringComparison.Ordinal),
            "the Vulkan override answers in the slot order the dump names");
    }

    [Fact]
    public void EveryChangedMemberIsListedForTheCecilTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
        {
            "RenderOptimumAmbientOcclusion", "OptimumAmbientOcclusionDebugTexture", "optimumAmbientOcclusionTexture",
            "OptimumAoWorkingSlot", "OptimumAoEdgesSlot", "OptimumAoDepthSlot", "OptimumAoOutputSlot", "OptimumAoOutputCount",
            "OptimumHeadlessWriteAmbientOcclusion",
            // Already transplanted members whose bodies changed.
            "ApplyOptimumSceneSsao", "OptimumRunParityDump", "OptimumParitySlotName", "OptimumHeadlessCaptureFrame",
        })
        {
            Assert.Contains("\"" + member + "\"", patcher);
        }
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderPostprocessingEffects\", 1)", patcher);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ShaderRegistry\", \"registerDefaultShaderCodePrefixes\", 2)", patcher);

        string expected = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        Assert.Contains("new(true, \"RenderOptimumAmbientOcclusion\", new[] { \"Single[]\" }),", expected);
        Assert.Contains("new(true, \"OptimumAmbientOcclusionDebugTexture\", new[] { \"Int32\" }),", expected);
    }

    [Fact]
    public void TheComputeShadersCarryTheirLicenceNoticesAndShipInsideTheRenderer()
    {
        const string permission = "Permission is hereby granted, free of charge, to any person obtaining a copy";
        foreach (string file in new[] { "common.glsl", "prefilter.comp", "main.comp", "denoise.comp" })
        {
            string source = Read("sources/shaders-vk/gtao/" + file);
            Assert.Contains("Copyright (C) 2016-2021, Intel Corporation", source);
            Assert.Contains("https://github.com/GameTechDev/XeGTAO", source);
            Assert.Contains(permission, source);
        }
        string main = Read("sources/shaders-vk/gtao/main.comp");
        Assert.Contains("https://github.com/bevyengine/bevy", main);
        Assert.Contains("Copyright (c) 2016, Intel Corporation", main); // ASSAO's normal-based edges, via Godot
        Assert.Contains("clayjohn: convert to Vulkan and Godot", main);

        string project = Read("Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj");
        Assert.Contains("<EmbeddedResource Include=\"..\\sources\\shaders-vk\\gtao\\*.comp;..\\sources\\shaders-vk\\gtao\\*.glsl\">", project);
        Assert.Contains("<LogicalName>shaders-vk/gtao/%(Filename)%(Extension)</LogicalName>", project);
        // No shipped noise texture in the first version (E.2.2): the Hilbert table is generated.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Root(), "sources", "shaders-vk", "gtao"))
            .Where(f => !f.EndsWith(".comp", StringComparison.Ordinal) && !f.EndsWith(".glsl", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ the master switch

    [Fact]
    public void TheMasterSwitchDefaultsToOnAndPersistsThroughOptimumJson()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("public static bool AmbientOcclusionEnabled = true;", config);
        Assert.Contains("public bool AmbientOcclusionEnabled { get; set; } = true;", config);
        Assert.Contains("AmbientOcclusionEnabled = data.AmbientOcclusionEnabled;", config);
        Assert.Contains("AmbientOcclusionEnabled = AmbientOcclusionEnabled,", config);
        Assert.Contains("(nameof(OptimumConfigData.AmbientOcclusionEnabled), AmbientOcclusionEnabled.ToString())", config);
    }

    [Fact]
    public void TheMasterSwitchGatesBothAoPathsThroughRenderSsao()
    {
        // Vanilla SSAO and RenderOptimumAmbientOcclusion hang off the same condition, so gating
        // RenderSSAO switches off AO on both backends and in both modes.
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains(
            "RenderSSAO = ClientSettings.SSAOQuality > 0 && base.DoPostProcessingEffects && OptimumConfig.AmbientOcclusionEnabled;",
            platform);
        // SetupSSAO is deliberately not gated: the G-buffer and its frame buffers stay, which is
        // what lets the switch flip without a rebuild.
        Assert.Contains("SetupSSAO = ClientSettings.SSAOQuality > 0;", platform);
        Assert.DoesNotContain("SetupSSAO = ClientSettings.SSAOQuality > 0 && OptimumConfig.AmbientOcclusionEnabled", platform);
    }

    [Fact]
    public void TheAoChoiceIsOneDropDownWiredToTheConfig()
    {
        string gui = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");
        Assert.Contains("Lang.Get(\"optimum-ao\")", gui);
        Assert.Contains("Lang.Get(\"optimum-ao-tooltip\")", gui);
        // One control for the whole choice: off, the game's own pass, ours, or auto.
        Assert.Contains("AddDropDown(new string[] { \"off\", \"auto\", \"vanilla\", \"gtao\" }", gui);
        Assert.Contains("onOptimumAmbientOcclusionChanged", gui);
        Assert.Contains("\"optAoMode\")", gui);
        Assert.Contains("composer.GetDropDown(\"optAoMode\").SetSelectedIndex(", gui);
        foreach (string key in new[] { "optimum-ao-off", "optimum-ao-auto", "optimum-ao-vanilla", "optimum-ao-gtao" })
        {
            Assert.Contains("Lang.Get(\"" + key + "\")", gui);
        }

        string handler = Between(gui, "private void onOptimumAmbientOcclusionChanged(string code, bool selected)", "\n\t}");
        Assert.Contains("OptimumConfig.AmbientOcclusionEnabled = false;", handler);
        Assert.Contains("OptimumConfig.AmbientOcclusion = code;", handler);
        Assert.Contains("OptimumConfig.Save();", handler);
        // Off gates the passes only, so it must not reload; changing WHICH AO runs changes the
        // OPTIMUMAO the shaders carry, so exactly that case reloads them.
        Assert.Contains("bool wasGtao = Vintagestory.API.Config.OptimumConfig.EffectiveGtao;", handler);
        Assert.Contains("if (Vintagestory.API.Config.OptimumConfig.EffectiveGtao != wasGtao)", handler);
        Assert.Contains("handler.ReloadShaders();", handler);
        Assert.DoesNotContain("RebuildFrameBuffers", handler);
        Assert.DoesNotContain("RequestReset", handler);
    }

    [Fact]
    public void TheMasterSwitchHasItsLangEntriesAndItsPatcherListing()
    {
        string lang = Read("sources/lang/en.json");
        foreach (string key in new[]
        {
            "optimum-ao", "optimum-ao-tooltip",
            "optimum-ao-off", "optimum-ao-auto", "optimum-ao-vanilla", "optimum-ao-gtao",
        })
        {
            Assert.Contains("\"" + key + "\":", lang);
        }
        Assert.Contains("\"onOptimumAmbientOcclusionChanged\"", Read("Optimum.Patcher/Program.cs"));
    }

    // ------------------------------------------------------------------ the debug view

    [Fact]
    public void SwitchingAoOffAlsoStopsFinalFromMultiplyingByTheStaleSsaoTarget()
    {
        // The regression this pins: the scene shaders are still compiled with SSAOLEVEL > 0, so
        // final.fsh multiplies by ssaoScene unless optimumSsaoInScene says otherwise. With AO off
        // nothing renders into that target, so an unguarded multiply darkens the whole image.
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains(
            "final.Uniform(\"optimumSsaoInScene\", (optimumSsaoInScene || !RenderSSAO) ? 1 : 0);",
            platform);
    }

    [Fact]
    public void TheDebugViewShowsWhicheverAoRanAndOnlyWhileAoIsOn()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains("bool optimumAoDebugView = OptimumConfig.AmbientOcclusionDebugView && RenderSSAO;", platform);
        // GTAO's own visibility texture when it produced one, the vanilla blurred target otherwise.
        Assert.Contains("optimumAoDebugView && optimumAmbientOcclusionTexture != 0", platform);
        Assert.Contains("final.Uniform(\"optimumAoDebug\", optimumAoDebugView ? 1 : 0);", platform);

        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("public static bool AmbientOcclusionDebugView = false;", config);
        Assert.Contains("public bool AmbientOcclusionDebugView { get; set; } = false;", config);
        Assert.Contains("AmbientOcclusionDebugView = data.AmbientOcclusionDebugView;", config);
        Assert.Contains("AmbientOcclusionDebugView = AmbientOcclusionDebugView,", config);
    }

    [Fact]
    public void TheDebugBranchIsTheSameInBothShaderTwins()
    {
        // The native program and the GLSL 330 override must agree on the uniform set (the parity
        // harness) and on what the branch does, or the two backends show different pictures.
        string gl = Read("sources/shaders/final.fsh");
        Assert.Contains("uniform int optimumAoDebug;", gl);
        Assert.Contains("float aoDebugTerm = texture(ssaoScene, texCoord).r;", gl);
        Assert.Contains("outColor = vec4(vec3(aoDebugTerm), 1.0);", gl);

        string vk = Read("sources/shaders-vk/final.frag");
        Assert.Contains("float aoDebugTerm = texture(optimumTextures2D[ssaoScene], texCoord).r;", vk);
        Assert.Contains("outColor = vec4(vec3(aoDebugTerm), 1.0);", vk);
        Assert.Contains("int optimumAoDebug;", Read("sources/shaders-vk/final.interface.glsl"));

        // Before colour grading and vignetting in both: the view is the AO term, nothing else.
        Assert.True(gl.IndexOf("optimumAoDebug != 0", StringComparison.Ordinal)
            < gl.IndexOf("vec4 gradedColor = ColorGrade(color);", StringComparison.Ordinal));
        Assert.True(vk.IndexOf("optimumAoDebug != 0", StringComparison.Ordinal)
            < vk.IndexOf("vec4 gradedColor = ColorGrade(color);", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDebugViewIsAnOptimumTabSwitchWithItsLangEntriesAndPatcherListing()
    {
        string gui = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");
        Assert.Contains("Lang.Get(\"optimum-aodebug\")", gui);
        Assert.Contains("AddSwitch(onOptimumAmbientOcclusionDebugChanged", gui);
        Assert.Contains("composer.GetSwitch(\"optAoDebug\").SetValue(Vintagestory.API.Config.OptimumConfig.AmbientOcclusionDebugView);", gui);

        string handler = Between(gui, "private void onOptimumAmbientOcclusionDebugChanged(bool on)", "\n\t}");
        Assert.Contains("OptimumConfig.AmbientOcclusionDebugView = on;", handler);
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("RebuildFrameBuffers", handler);

        string lang = Read("sources/lang/en.json");
        Assert.Contains("\"optimum-aodebug\":", lang);
        Assert.Contains("\"optimum-aodebug-tooltip\":", lang);
        Assert.Contains("\"onOptimumAmbientOcclusionDebugChanged\"", Read("Optimum.Patcher/Program.cs"));
    }

    // ------------------------------------- the AO step drawn natively (Phase 3b stage 1c)

    /// <summary>
    /// The Vulkan platform draws the AO step through the native device API: a pipeline with
    /// stated fixed state per pass, a pass that names its target, its written colour slot and its
    /// reads, uniforms written by resolved placement, and textures resolved straight to bindless
    /// slots. The chain step routes to it, with the lib virtual as the old route.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformDrawsTheAoStepNatively()
    {
        string chain = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostChain.cs");
        Assert.Contains("if (UseNativePostChain && NativeAmbientOcclusionReady())", chain);
        Assert.Contains("NativeAmbientOcclusion(projectMatrix);", chain);
        Assert.Contains("OptimumPostAmbientOcclusion(projectMatrix);", chain);

        string native = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSsao.cs");

        // One NativeFullscreenPass per program, with the uniform and sampler names the passes
        // resolve once instead of looking up per draw.
        Assert.Contains("nativeSsao = new(\"ssao\",", native);
        Assert.Contains("nativeBilateralBlur = new(\"bilateralblur\",", native);
        Assert.Contains("nativeSceneSsao = new(\"scene-ssao\",", native);
        Assert.Contains("\"screenSize\", \"projection\", \"samples\", \"temporalFrameIndex\"", native);
        Assert.Contains("\"gPosition\", \"gNormal\", \"texNoise\", \"revealage\"", native);
        Assert.Contains("\"frameSize\", \"isVertical\"", native);
        Assert.Contains("\"inputTexture\", \"depthTexture\"", native);
        Assert.Contains("\"invRenderHeight\", \"optimumAoMode\"", native);
        Assert.Contains("\"ssaoScene\", \"gPositionScene\", \"revealageScene\"", native);

        // Every pass states its fixed state and writes colour slot 0 alone.
        Assert.Equal(3, Count(native, "depthTest: false, depthWrite: false, CompareOp.Less)"));
        Assert.Contains("ColorSlots = 1u,", native);
        Assert.Contains("device.DrawNativeFullscreen(pipeline,", native);
        Assert.Contains("device.EndNativePass();", native);
    }

    /// <summary>
    /// The values are the OpenGL body's, expression for expression: the half-resolution
    /// screenSize, the raw unjittered projection, the 64-sample kernel, the temporal dither index
    /// under exactly the condition that compiles the uniform in, the blur's shared frameSize and
    /// its iteration count, and the composite's inverse render height.
    /// </summary>
    [Fact]
    public void TheNativeAoStepUsesTheOpenGlBodysValues()
    {
        string native = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSsao.cs");

        Assert.Contains("float half = ssaa == 1f ? 0.5f : 1f;", native);
        Assert.Contains("ssaa * client.Width * half, ssaa * client.Height * half", native);
        Assert.Contains("WriteNativeFloats(pipeline, nativeSsao.Uniforms[1], projectMatrix);", native);
        Assert.Contains("WriteNativeFloats(pipeline, nativeSsao.Uniforms[2], OptimumSsaoKernel);", native);
        Assert.Contains("if (OptimumConfig.EffectiveTaa)", native);
        Assert.Contains("(float)(OptimumTemporal.Frame.FrameIndex & 1023L)", native);

        Assert.Contains("int iterations = ClientSettings.SSAOQuality == 1 ? 1 : 3;", native);
        Assert.Contains("buffers[i == 0 ? NativeSsaoTargetIndex : NativeSsaoBlurVerticalIndex].ColorTextureIds[0]", native);
        // The blur's frameSize is frameBuffers[15]'s size on both halves, reproduced not fixed.
        Assert.Contains("(float)frameSizeSource.Width, (float)frameSizeSource.Height", native);

        Assert.Contains("device.WriteNative(pipeline, nativeSceneSsao.Uniforms[0], 1f / primary.Height);", native);
        Assert.Contains("OptimumPostAmbientOcclusionTexture != 0 ? 1 : 0", native);
    }

    /// <summary>
    /// Both AO modes reach the composite, and the step records that AO is in the scene so the
    /// final composition never applies it twice. The GTAO branch binds the attenuation inputs and
    /// sets optimumAoMode exactly where the body does - under AmbientOcclusionShadersUseGtao,
    /// which is what stamps the OPTIMUMAO variant.
    /// </summary>
    [Fact]
    public void TheNativeAoStepCarriesBothModesAndTheirFlags()
    {
        string native = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSsao.cs");

        Assert.Contains("OptimumPostAmbientOcclusionTexture = RenderOptimumAmbientOcclusion(projectMatrix);", native);
        Assert.Contains("if (OptimumPostAmbientOcclusionTexture == 0 && OptimumRenderSsao && projectMatrix != null)", native);
        Assert.Contains("if (OptimumTaaRequested && TaaTargetsReady)", native);
        Assert.Contains("bool gtao = OptimumConfig.AmbientOcclusionShadersUseGtao;", native);
        Assert.Contains("aoTexture = blurred.ColorTextureIds[0];", native);
        Assert.Contains("OptimumPostSsaoInScene = false;", native);
        Assert.Contains("OptimumPostSsaoInScene = true;", native);

        // The Multiply blend is the pipeline's, not a tracked GL state.
        string multiply = Between(native, "private static AttachmentBlend[] NativeMultiplySlotZeroBlend()", "\n    }");
        Assert.Contains("blend.SrcColor = BlendFactor.Zero;", multiply);
        Assert.Contains("blend.DstColor = BlendFactor.OneMinusSrcAlpha;", multiply);
        Assert.Contains("blend.SrcAlpha = BlendFactor.One;", multiply);
        Assert.Contains("blend.DstAlpha = BlendFactor.OneMinusSrcAlpha;", multiply);
        Assert.Contains("blend.ColorOp = BlendOp.Add;", multiply);
        Assert.Contains("blend.AlphaOp = BlendOp.Add;", multiply);
    }

    /// <summary>
    /// The two pieces of AO frame state and the SSAA factor are lib accessors, so the native step
    /// sets and reads exactly what the OpenGL body's step does, and all three are listed for Cecil
    /// and in the vanilla-regions test.
    /// </summary>
    [Fact]
    public void TheAoStepsFrameStateIsALibSeamListedForCecil()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains("public int OptimumPostAmbientOcclusionTexture", platform);
        Assert.Contains("public bool OptimumPostSsaoInScene", platform);
        Assert.Contains("public float OptimumPostSsaaLevel", platform);
        // The body keeps writing the fields directly: "OFF is vanilla".
        Assert.Contains("optimumSsaoInScene = false;", platform);
        Assert.Contains("optimumAmbientOcclusionTexture = 0;", platform);

        string patcher = Read("Optimum.Patcher/Program.cs");
        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");
        foreach (string member in new[]
                 {
                     "OptimumPostAmbientOcclusionTexture", "OptimumPostSsaoInScene", "OptimumPostSsaaLevel",
                 })
        {
            Assert.Contains("\"" + member + "\"", patcher);
            Assert.Contains("\"" + member + "\"", regions);
        }
    }

    /// <summary>
    /// The white clear the SSAO target starts from is the pass's own load, declared with the pass
    /// - not a glClearBuffer against a draw-buffer mask.
    /// </summary>
    [Fact]
    public void TheSsaoTargetsWhiteClearIsThePassesOwnLoad()
    {
        string native = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSsao.cs");
        Assert.Contains("ClearSlots = clearWhite ? 1u : 0u,", native);
        Assert.Contains("ClearValue = new[] { 1f, 1f, 1f, 1f },", native);
        Assert.Contains("clearWhite: true", native);

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.Native.cs");
        Assert.Contains("public uint ClearSlots;", device);
        Assert.Contains("_targets.ClearPassAttachment(commandBuffer, slot,", device);

        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        Assert.Contains("public void ClearPassAttachment(CommandBuffer commandBuffer, int attachment,", targets);
        Assert.Contains("_graph.PromoteColorClear(texture, _bound.Color[attachment].Layer, r, g, b, a);", targets);
    }

    // ------------------------------------------------------------------ helpers

    private static int Count(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string full = Path.Combine(Root(), patchPath);
        return File.Exists(full) ? PatchReader.ReadPatchedContent(full) : Read(sourcePath);
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing: " + start);
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "missing after " + start + ": " + end);
        return text[from..to];
    }

    private static string Root()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return root;
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root(), path));
}
