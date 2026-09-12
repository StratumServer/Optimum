using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// P5 settings: the three TAA rows in the Optimum settings tab, the scanner
/// rules that veto TAA when a mod owns one of its shaders, and the packaging
/// that has to carry every one of those shaders into a release.
/// </summary>
public class TaaSettingsCoverageTests
{
    // ---- (a) settings rows -------------------------------------------------

    [Fact]
    public void TheOptimumTabHasATaaToggleAndBothTaaSliders()
    {
        string gui = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        Assert.Contains("Lang.Get(\"optimum-taa\")", gui);
        Assert.Contains("AddSwitch(onOptimumTaaChanged", gui);
        Assert.Contains("\"optTaa\")", gui);

        Assert.Contains("Lang.Get(\"optimum-taasharpness\")", gui);
        Assert.Contains("AddSlider(onOptimumTaaSharpnessChanged", gui);
        Assert.Contains("\"optTaaSharpness\")", gui);

        Assert.Contains("Lang.Get(\"optimum-taamipbias\")", gui);
        Assert.Contains("AddSlider(onOptimumTaaMipBiasChanged", gui);
        Assert.Contains("\"optTaaMipBias\")", gui);

        // Every row in this tab carries a hover text; a row without one reads as
        // an unexplained switch in a list of explained ones.
        Assert.Contains("Lang.Get(\"optimum-taa-tooltip\")", gui);
        Assert.Contains("Lang.Get(\"optimum-taasharpness-tooltip\")", gui);
        Assert.Contains("Lang.Get(\"optimum-taamipbias-tooltip\")", gui);
    }

    [Fact]
    public void TheRowsAreBackedByTheCurrentConfigurationWhenTheTabOpens()
    {
        string gui = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        // EffectiveTaa, not Taa: a launcher verdict or a runtime fallback has
        // already turned TAA off, and the switch must show what is running.
        Assert.Contains(
            "composer.GetSwitch(\"optTaa\").SetValue(Vintagestory.API.Config.OptimumConfig.EffectiveTaa);",
            gui);

        // The sliders are integers; the config fields are floats in 0..1 and
        // -1..0, so the rows carry hundredths.
        Assert.Contains("GetSlider(\"optTaaSharpness\").SetValues(", gui);
        Assert.Contains("OptimumConfig.TaaSharpness * 100f", gui);
        Assert.Contains(", 0, 100, 5, \"%\");", gui);
        Assert.Contains("GetSlider(\"optTaaMipBias\").SetValues(", gui);
        Assert.Contains("OptimumConfig.TaaMipBias * 100f", gui);
        Assert.Contains(", -100, 0, 5, \"/100\");", gui);
    }

    [Fact]
    public void TheToggleRebuildsTargetsReloadsShadersAndDropsTheHistory()
    {
        string gui = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        string handler = Between(gui, "private void onOptimumTaaChanged(bool on)", "\n\t}");

        // A missing launcher scan must not veto TAA: IsFeatureExplicitlyDisabled,
        // like OptimumConfig.EffectiveTaa, never IsShaderFeatureDisabled (which
        // reports everything disabled when no scan exists).
        Assert.Contains("IsFeatureExplicitlyDisabled(\"Taa\")", handler);
        Assert.DoesNotContain("IsShaderFeatureDisabled(\"Taa\")", handler);

        Assert.Contains("OptimumConfig.Taa = on;", handler);
        Assert.Contains("OptimumConfig.Save();", handler);
        // The history targets and the motion attachment only exist while TAA is
        // on, final.fsh compiles its FXAA branch against EffectiveTaa, and a
        // history captured under the other configuration must never be
        // reprojected into a frame that did not produce it.
        Assert.Contains("ScreenManager.Platform.RebuildFrameBuffers();", handler);
        Assert.Contains("handler.ReloadShaders();", handler);
        Assert.Contains("OptimumTemporal.RequestReset(EnumTemporalResetReason.Toggle);", handler);
    }

    /// <summary>
    /// The GUI switch has already flipped by the time the handler runs, so the
    /// "TAA is explicitly disabled" bail-out has to put it back. Without the
    /// reset the row shows TAA on for the rest of the session while
    /// EffectiveTaa stays false.
    /// </summary>
    [Fact]
    public void TheToggleResetsTheSwitchWhenTaaIsExplicitlyDisabled()
    {
        string gui = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        string handler = Between(gui, "private void onOptimumTaaChanged(bool on)", "\n\t}");

        // The reset targets the same switch key the row is built with, and
        // takes its value from EffectiveTaa - the only truth the rest of the
        // chain reads.
        Assert.Contains("AddSwitch(onOptimumTaaChanged", gui);
        Assert.Contains("\"optTaa\")", gui);
        Assert.Contains(
            "composer?.GetSwitch(\"optTaa\")?.SetValue(Vintagestory.API.Config.OptimumConfig.EffectiveTaa);",
            handler);

        // And it happens before the bail-out, not after it.
        int reset = handler.IndexOf("GetSwitch(\"optTaa\")", StringComparison.Ordinal);
        int giveUp = handler.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(reset >= 0, "the explicit-disable bail-out never resets the switch");
        Assert.True(reset < giveUp, "the switch has to be reset before the handler returns");
    }

    [Fact]
    public void BothSlidersApplyLiveAndNeverRebuildOrResetAnything()
    {
        string gui = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        foreach ((string name, string field, string clamp) in new[]
        {
            ("onOptimumTaaSharpnessChanged", "TaaSharpness", "GameMath.Clamp(val / 100f, 0f, 1f)"),
            ("onOptimumTaaMipBiasChanged", "TaaMipBias", "GameMath.Clamp(val / 100f, -1f, 0f)"),
        })
        {
            string handler = Between(gui, "private bool " + name + "(int val)", "\n\t}");
            Assert.Contains("OptimumConfig." + field + " = " + clamp + ";", handler);
            Assert.Contains("OptimumConfig.Save();", handler);
            // Sharpen strength and LOD bias are read where they are used, so a
            // frame buffer rebuild, a shader reload or a temporal reset on every
            // drag step would be a stutter with no effect on the image.
            Assert.DoesNotContain("RebuildFrameBuffers", handler);
            Assert.DoesNotContain("ReloadShaders", handler);
            Assert.DoesNotContain("RequestReset", handler);
        }
    }

    [Fact]
    public void TheRowsFitTheFixedMainMenuDialog()
    {
        string gui = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        // ComposerHeader lays the main-menu dialog out at a fixed 740px, and the
        // tab starts at y0 = 87. Since the upscaler, quality, sharpness and latency
        // rows joined it (DLSS plan, Phase 6) the stock build ends at row 25 (TAA
        // mip bias) and the feature-flag build at row 29, so the row interval
        // shrinks in both or the last rows fall off the dialog.
        Assert.Contains("double rowH = 24.0;", gui);
        Assert.Contains("double rowH = 21.0;", gui);

        Assert.Contains("rowH * 23", gui); // TAA toggle
        Assert.Contains("rowH * 24", gui); // sharpness
        Assert.Contains("rowH * 25", gui); // mip bias
        Assert.Contains("rowH * 29", gui); // greedy far distance, shifted down

        // The row itself is 30px tall, so the last one has to end inside the dialog.
        Assert.True(87.0 + 24.0 * 25 + 30.0 <= 740.0);
        Assert.True(87.0 + 21.0 * 29 + 30.0 <= 740.0);
    }

    [Fact]
    public void EveryNewRowHasItsTranslationStrings()
    {
        string lang = Read("sources/lang/en.json");
        foreach (string key in new[]
        {
            "optimum-taa", "optimum-taa-tooltip",
            "optimum-taasharpness", "optimum-taasharpness-tooltip",
            "optimum-taamipbias", "optimum-taamipbias-tooltip",
        })
        {
            Assert.Contains("\"" + key + "\":", lang);
        }
    }

    [Fact]
    public void TheHandlersAreListedForTheCecilTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"onOptimumTaaChanged\"", patcher);
        Assert.Contains("\"onOptimumTaaSharpnessChanged\"", patcher);
        Assert.Contains("\"onOptimumTaaMipBiasChanged\"", patcher);
    }

    [Fact]
    public void TheSettingsPersistThroughOptimumJson()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");

        // Written, read back, clamped on load and carried by the snapshot clone.
        Assert.Contains("(nameof(OptimumConfigData.Taa), Taa.ToString())", config);
        Assert.Contains("(nameof(OptimumConfigData.TaaSharpness), TaaSharpness.ToString(\"F2\"))", config);
        Assert.Contains("(nameof(OptimumConfigData.TaaMipBias), TaaMipBias.ToString(\"F2\"))", config);
        Assert.Contains("Taa = data.Taa;", config);
        Assert.Contains("TaaSharpness = Math.Clamp(data.TaaSharpness, 0f, 1f);", config);
        Assert.Contains("TaaMipBias = Math.Clamp(data.TaaMipBias, -2f, 1f);", config);
        Assert.Contains("public bool Taa { get; set; }", config);
        Assert.Contains("public float TaaSharpness { get; set; }", config);
        Assert.Contains("public float TaaMipBias { get; set; }", config);
    }

    // ---- (b) scanner rules -------------------------------------------------

    [Fact]
    public void TheScannerVetoesTaaForEveryShaderStageTaaOwns()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");
        string decision = Between(scanner, "bool externalMotionShader =", "AddFeatureDecision(report, \"Taa\"");

        foreach (string shader in new[]
        {
            // The resolve, its debug views, the sky-motion pass and the sharpen.
            "taa-resolve.vsh", "taa-resolve.fsh",
            "taa-debug.vsh", "taa-debug.fsh",
            "taa-skymotion.vsh", "taa-skymotion.fsh",
            "taa-sharpen.vsh", "taa-sharpen.fsh",
            // The liquid velocity pass and the FSR pair the sharpen shares its
            // vertex stage and lobe maths with.
            "chunkliquidmotion.vsh", "chunkliquidmotion.fsh",
            "fsr-easu.vsh", "fsr-easu.fsh",
            "fsr-rcas.vsh", "fsr-rcas.fsh",
            // The motion-vector writers themselves.
            "chunkopaque.vsh", "chunkopaque.fsh",
            "chunktopsoil.vsh", "chunktopsoil.fsh",
            "entityanimated.vsh", "entityanimated.fsh",
            "standard.vsh", "standard.fsh",
            "instanced.vsh", "instanced.fsh",
            "chunkliquid.vsh",
            "particlescube.vsh", "particlescube.fsh",
            "transparentcompose.fsh",
            "decals.vsh", "decals.fsh",
            "vertexwarp.vsh",
        })
        {
            Assert.Contains("HasExternalShader(report, \"" + shader + "\")", decision);
        }

        // A stage added after this test was written is covered by the prefix,
        // and the whole shaderincludes directory by the include rule - one is a
        // list that goes stale, the other two do not.
        Assert.Contains("HasExternalShaderPrefix(report, \"taa-\")", decision);
        Assert.Contains("HasExternalShaderInclude(report)", decision);

        // "Taa" stays out of ShaderFeatures: a scanner failure must not veto a
        // renderer feature the user asked for, only an explicit verdict does.
        Assert.DoesNotContain("\"Taa\",", Between(scanner, "ShaderFeatures", "];"));
    }

    [Fact]
    public void EveryTaaShaderTheRepositoryShipsHasAScannerRule()
    {
        // The real guard against the list going stale: enumerate what
        // sources/shaders actually contains rather than trusting the names above.
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");
        string decision = Between(scanner, "bool externalMotionShader =", "AddFeatureDecision(report, \"Taa\"");
        string shaderDir = Path.GetDirectoryName(
            PatchReader.FindRepositoryFile("sources/shaders/taa-resolve.fsh"))!;

        var checkedShaders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(shaderDir))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith("taa-", StringComparison.Ordinal) &&
                !name.StartsWith("fsr-", StringComparison.Ordinal) &&
                !name.StartsWith("chunkliquidmotion", StringComparison.Ordinal))
            {
                continue;
            }

            checkedShaders.Add(name);
            Assert.True(
                decision.Contains("HasExternalShader(report, \"" + name + "\")", StringComparison.Ordinal),
                "no scanner rule disables Taa when a mod ships " + name);
        }

        Assert.True(checkedShaders.Count >= 10, "the shader enumeration found nothing to check");
    }

    // ---- (c) packaging -----------------------------------------------------

    [Fact]
    public void EveryPackagerShipsTheWholeShaderAndIncludeDirectoryAndProvesIt()
    {
        string scriptsDirectory = Path.GetDirectoryName(
            PatchReader.FindRepositoryFile("scripts/package-linux.sh"))!;

        var packagers = new List<string>();
        foreach (string path in Directory.EnumerateFiles(scriptsDirectory, "package*"))
        {
            string text = File.ReadAllText(path);
            if (!text.Contains("sources/shaders", StringComparison.Ordinal)) continue;
            string name = Path.GetFileName(path);
            packagers.Add(name);

            Assert.True(text.Contains("sources/shaderincludes", StringComparison.Ordinal),
                name + " overlays sources/shaders but not sources/shaderincludes");
            // The overlay is a whole-directory copy, so a new stage ships without
            // a packager edit - what needs proving is that the copy landed.
            Assert.True(text.Contains("never reached the staged assets", StringComparison.Ordinal),
                name + " does not verify that every source shader reached the stage");
        }

        foreach (string expected in new[]
        {
            "package-linux.sh", "package-macos.sh", "package-linux.ps1",
            "package-macos.ps1", "package.ps1",
        })
        {
            Assert.Contains(expected, packagers);
        }
    }

    [Fact]
    public void TheWindowsPackagerAssertsEveryTaaShaderInItsStagedTree()
    {
        string packager = Read("scripts/package.ps1");
        foreach (string staged in new[]
        {
            "assets/game/shaderincludes/vertexwarp.vsh",
            "assets/game/shaders/taa-resolve.vsh",
            "assets/game/shaders/taa-resolve.fsh",
            "assets/game/shaders/taa-debug.vsh",
            "assets/game/shaders/taa-debug.fsh",
            "assets/game/shaders/taa-skymotion.vsh",
            "assets/game/shaders/taa-skymotion.fsh",
            // P5's post-resolve sharpen: a separate branch added the shader, this
            // list came from another, and the wildcard overlay would have shipped
            // it silently either way - the reviewer list is the only place the
            // release states it is supposed to be there.
            "assets/game/shaders/taa-sharpen.vsh",
            "assets/game/shaders/taa-sharpen.fsh",
            "assets/game/shaders/chunkliquidmotion.vsh",
            "assets/game/shaders/chunkliquidmotion.fsh",
            "assets/game/shaders/fsr-easu.vsh",
            "assets/game/shaders/fsr-easu.fsh",
            "assets/game/shaders/fsr-rcas.vsh",
            "assets/game/shaders/fsr-rcas.fsh",
        })
        {
            Assert.Contains("'" + staged + "'", packager);
        }

        // Everything the assertion names has to exist in the repository, or the
        // list is a package failure waiting for the next release rather than a
        // guard.
        foreach (string staged in new[]
        {
            "taa-resolve.vsh", "taa-resolve.fsh", "taa-debug.vsh", "taa-debug.fsh",
            "taa-skymotion.vsh", "taa-skymotion.fsh",
            "taa-sharpen.vsh", "taa-sharpen.fsh",
            "chunkliquidmotion.vsh", "chunkliquidmotion.fsh",
            "fsr-easu.vsh", "fsr-easu.fsh", "fsr-rcas.vsh", "fsr-rcas.fsh",
        })
        {
            PatchReader.FindRepositoryFile("sources/shaders/" + staged);
        }
    }

    [Fact]
    public void MakeDeployCopiesEveryShaderAndFailsWhenOneDoesNotArrive()
    {
        string makefile = Read("Makefile");

        // Not "*.fsh plus *.vsh": both deploy paths copy the whole directory, and
        // a stage that ships on only one of the two paths is exactly the bug the
        // completeness check catches. The copy is a per-file loop whose cp failure
        // aborts the target, so a copy error cannot be swallowed the way
        // "find -exec cp" swallowed it.
        Assert.Equal(2, Occurrences(makefile, "for f in sources/shaders/*;"));
        Assert.Equal(2, Occurrences(makefile, "for f in sources/shaderincludes/*;"));
        Assert.Contains("cp -f \"$$f\" \"$(VANILLA_DIR)/assets/game/shaders/$$(basename $$f)\" || exit 1", makefile);
        Assert.Contains("cp -f \"$$f\" \"$(INSTALL_DIR)/assets/game/shaders/$$(basename $$f)\" || exit 1", makefile);
        Assert.DoesNotContain("cp sources/shaders/*.fsh sources/shaders/*.vsh", makefile);
        Assert.DoesNotContain("find sources/shaders", makefile);
        Assert.Contains("mkdir -p $(VANILLA_DIR)/assets/game/shaderincludes", makefile);
        Assert.Contains("mkdir -p $(INSTALL_DIR)/assets/game/shaderincludes", makefile);

        // The completeness check compares CONTENT, not mere existence: a vanilla
        // file of the same name that was never overwritten used to satisfy
        // [ -f "$$d" ] and pass.
        Assert.Equal(2, Occurrences(makefile, "did not reach"));
        Assert.Equal(2, Occurrences(makefile, "cmp -s \"$$f\" \"$$d\""));
        Assert.DoesNotContain("[ -f \"$$d\" ] ||", makefile);
    }

    // ---- helpers -----------------------------------------------------------

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "not found: " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, "not found after " + start + ": " + end);
        return text[from..to];
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
