using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 6: the upscaler, quality-preset and latency rows in the
/// Optimum settings tab.
///
/// Before this, the only way to switch DLSS on, change its preset or compare it
/// against TAA was to edit ModConfig/optimum.json and restart, which made a
/// quality comparison impossible. The rows are asserted here the way the rest of
/// the tab is: the page declares them with their handlers and hover texts, the
/// handlers persist and then re-plan through one injected virtual on
/// <c>ClientPlatformAbstract</c> (the page never reaches into the renderer), the
/// patcher carries every new member, the renderer's self-check expects the
/// virtuals, and every label has a translation.
///
/// The honest-state rule has its own cases: a machine with no upscaler must be
/// told so rather than offered a choice the frame would not honour, and the
/// preset row must be dead while nothing is upscaling.
/// </summary>
public class UpscalerSettingsUiCoverageTests
{
    private const string GuiPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch";
    private const string GuiSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs";
    private const string AbstractPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch";
    private const string AbstractSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";

    // ---- (a) the rows ------------------------------------------------------

    [Fact]
    public void TheTabHasAnUpscalerAQualityAndALatencyRow()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        Assert.Contains("Lang.Get(\"optimum-upscaler\")", gui);
        Assert.Contains("onOptimumUpscalerChanged", gui);
        Assert.Contains("\"optUpscaler\")", gui);

        Assert.Contains("Lang.Get(\"optimum-upscalerquality\")", gui);
        Assert.Contains("onOptimumUpscalerQualityChanged", gui);
        Assert.Contains("\"optUpscalerQuality\")", gui);

        Assert.Contains("Lang.Get(\"optimum-latency\")", gui);
        Assert.Contains("onOptimumLatencyChanged", gui);
        Assert.Contains("\"optLatency\")", gui);

        // A switch cannot express three upscaler presets, let alone five, so all
        // three rows are dropdowns - the shape the render-scale row already uses.
        Assert.Contains("AddDropDown(new string[] { \"off\", \"dlss\" }", gui);
        Assert.Contains(
            "AddDropDown(new string[] { \"dlaa\", \"quality\", \"balanced\", \"performance\", \"ultraperformance\" }",
            gui);
        Assert.Contains("AddDropDown(new string[] { \"off\", \"on\", \"boost\" }", gui);

        // Every row in this tab carries a hover text.
        Assert.Contains("Lang.Get(\"optimum-upscaler-tooltip\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscalerquality-tooltip\")", gui);
        Assert.Contains("Lang.Get(\"optimum-latency-tooltip\")", gui);
    }

    /// <summary>
    /// The preset values the row offers are exactly the ones the config declares,
    /// DLAA included, so a preset can never be selected that the parser degrades.
    /// </summary>
    [Fact]
    public void TheQualityRowOffersEveryPresetTheConfigDeclares()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        string declared = Between(config, "UpscalerQualityNames =", ";");

        foreach (string preset in new[] { "dlaa", "quality", "balanced", "performance", "ultraperformance" })
        {
            Assert.Contains("\"" + preset + "\"", declared);
            Assert.Contains("Lang.Get(\"optimum-upscalerquality-" + preset + "\")", gui);
        }

        // And the upscaler row offers exactly the slots the config knows about.
        Assert.Contains("\"off\", \"dlss\"", Between(config, "UpscalerNames = {", "}"));
    }

    /// <summary>
    /// The rows come up showing what the frame is really doing: the effective
    /// upscaler (a renderer stand-down has already made this session's answer
    /// "off"), not the persisted string.
    /// </summary>
    [Fact]
    public void TheRowsAreBackedByTheEffectiveStateWhenTheTabOpens()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        Assert.Contains(
            "composer.GetDropDown(\"optUpscaler\").SetSelectedValue(Vintagestory.API.Config.OptimumConfig.EffectiveUpscaler);",
            gui);
        Assert.Contains(
            "composer.GetDropDown(\"optUpscalerQuality\").SetSelectedValue(Vintagestory.API.Config.OptimumConfig.UpscalerQuality);",
            gui);
        Assert.Contains(
            "composer.GetDropDown(\"optLatency\").SetSelectedValue(Vintagestory.API.Config.OptimumConfig.LatencyMode);",
            gui);
        Assert.Contains("optimumUpdateUpscalerRows();", gui);
    }

    // ---- (a2) the tab the rows live on -------------------------------------

    /// <summary>
    /// PR #3, "needs its own settings tab": upscaling is a page beside the Extra
    /// one, registered exactly the way every other tab in this dialog is - a
    /// toggle button in both <c>ComposerHeader</c> branches (main menu and
    /// in-game), its own bounds measured in <c>updateButtonBounds</c>, its toggle
    /// key set from the current tab, and a page method that composes through
    /// <c>ComposerHeader</c> with that key.
    /// </summary>
    [Fact]
    public void UpscalingIsItsOwnTabRegisteredLikeEveryOtherTab()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        // The bounds, beside the Extra tab's own.
        Assert.Contains("private ElementBounds uButtonBounds = ElementBounds.Fixed(0.0, 0.0, 0.0, 40.0)", gui);
        Assert.Contains("uButtonBounds.ParentBounds = elementBounds;", gui);   // main menu
        Assert.Contains("uButtonBounds.ParentBounds = elementBounds3;", gui);  // in game

        // The button, in both branches, with the same toggle key the page composes with.
        Assert.Contains(
            "AddToggleButton(Lang.Get(\"optimum-upscaling-tab-header\"), font, OnOptimumUpscalingOptions, uButtonBounds, \"optimumupscaling\")",
            gui);
        Assert.Equal(2, Occurrences(gui,
            "AddToggleButton(Lang.Get(\"optimum-upscaling-tab-header\"), font, OnOptimumUpscalingOptions, uButtonBounds, \"optimumupscaling\")"));
        Assert.Contains("result.GetToggleButton(\"optimumupscaling\")?.SetValue(currentTab == \"optimumupscaling\");", gui);

        // The tab-width measurement that sizes the button row, and the Back button
        // that now follows the new tab rather than the Extra one.
        Assert.Contains("cairoFont.GetTextExtents(Lang.Get(\"optimum-upscaling-tab-header\"));", gui);
        Assert.Contains("uButtonBounds.WithFixedWidth(width10).FixedRightOf(oButtonBounds, 15.0);", gui);
        Assert.Contains("backButtonBounds.WithFixedWidth(width8).FixedRightOf(uButtonBounds, 25.0);", gui);

        // The page itself.
        Assert.Contains("private void OnOptimumUpscalingOptions(bool on)", gui);
        Assert.Contains("ComposerHeader(\"gamesettings-optimumupscalingoptions\", \"optimumupscaling\")", gui);
        Assert.Contains("composer.GetToggleButton(\"optimumupscaling\")?.SetValue(true);", gui);
    }

    /// <summary>
    /// The Cecil path adds its tab buttons through one injected hook helper
    /// (<c>_AddOptimumTab</c>, called after <c>EndIf</c> in vanilla's
    /// <c>ComposerHeader</c>), so the new tab has to be added there too or it
    /// exists only in the developer build. The Back button and the dialog-width
    /// walk measure from the last button in the row, which is now the new one -
    /// the walk itself is what keeps the Cairo surface from being overrun.
    /// </summary>
    [Fact]
    public void TheCecilHookAddsBothTabButtons()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string hook = Between(gui, "private Vintagestory.API.Client.GuiComposer _AddOptimumTab(", "\n\t}");

        Assert.Contains("Lang.Get(\"optimum-upscaling-tab-header\")", hook);
        Assert.Contains("OnOptimumUpscalingOptions, uButtonBounds, \"optimumupscaling\")", hook);
        Assert.Contains("uButtonBounds.FixedRightOf(oButtonBounds, 10.0);", hook);
        Assert.Contains("backButtonBounds.FixedRightOf(uButtonBounds, 15.0);", hook);
        Assert.Contains("? uButtonBounds.fixedX + uButtonBounds.fixedWidth", hook);
    }

    /// <summary>
    /// The readout the tab buys with the space: the plan the frame is really
    /// running, asked of the platform that owns the upscaler and re-read once per
    /// frame, never predicted from the preset.
    /// </summary>
    [Fact]
    public void TheTabShowsTheLivePlan()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string platform = ReadPatchedOrSource(AbstractPatch, AbstractSource);

        Assert.Contains("public virtual string OptimumUpscalerPlan()", platform);
        Assert.Contains(
            "AddDynamicText(optimumUpscalePlanText(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y0 + rowH * 6, 650, 60), \"optUpscalePlan\")",
            gui);

        string text = Between(gui, "private string optimumUpscalePlanText()", "\n\t}");
        Assert.Contains("ScreenManager.Platform.OptimumUpscalerPlan()", text);
        Assert.Contains("Lang.Get(\"optimum-upscaleplan-none\")", text);

        // Live: the readout follows the frame, because the plan only becomes the new
        // one on the frame that creates the feature.
        string refresh = Between(gui, "internal void Refresh()", "\n\t}");
        Assert.Contains("optimumUpdateUpscalePlanReadout();", refresh);
        string readout = Between(gui, "private void optimumUpdateUpscalePlanReadout()", "\n\t}");
        Assert.Contains("composer.GetDynamicText(\"optUpscalePlan\")", readout);
        Assert.Contains("readout.SetNewText(optimumUpscalePlanText());", readout);
    }

    /// <summary>
    /// The Vulkan platform answers the readout from the live feature's own plan,
    /// and null while none is serving one - the tab then says so rather than
    /// printing numbers no frame used.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformAnswersThePlanFromTheLiveFeature()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");
        string plan = Between(upscale, "public override string OptimumUpscalerPlan()", "\n    }");

        Assert.Contains("if (upscaler == null) return null;", plan);
        Assert.Contains("UpscalePlan plan = upscaler.Plan;", plan);
        Assert.Contains("return plan.IsValid ? plan.ToString() : null;", plan);
    }

    // ---- (b) honest state --------------------------------------------------

    [Fact]
    public void AnUnavailableUpscalerIsShownRatherThanOffered()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        // The page asks the platform - the renderer already knows every way DLSS
        // can be absent and produces the sentence.
        Assert.Contains("ScreenManager.Platform.OptimumUpscalerUnavailable()", gui);
        // The entry says so, and the reason goes into the row's hover text.
        Assert.Contains("Lang.Get(\"optimum-upscaler-dlss-unavailable\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscaler-unavailable\") + \" \" + upscalerUnavailable", gui);

        // And selecting it anyway is refused, with the row put back to what is
        // running rather than left claiming an upscale.
        string handler = Between(gui, "private void onOptimumUpscalerChanged(", "\n\t}\n");
        Assert.Contains("OptimumUpscalerUnavailable()", handler);
        Assert.Contains(
            "if (unavailable != null && !string.Equals(code, \"off\", StringComparison.OrdinalIgnoreCase))",
            handler);
        // The refusal returns before anything is persisted or rebuilt, having put
        // the row back to what is running.
        int refusal = handler.IndexOf("if (unavailable != null", StringComparison.Ordinal);
        int returned = handler.IndexOf("return;", refusal, StringComparison.Ordinal);
        int persisted = handler.IndexOf("OptimumConfig.Upscaler = code;", StringComparison.Ordinal);
        Assert.InRange(returned, refusal, persisted);
        Assert.InRange(
            handler.IndexOf("optimumUpdateUpscalerRows();", refusal, StringComparison.Ordinal), refusal, returned);
    }

    [Fact]
    public void TheQualityRowIsDeadWhileNothingIsUpscaling()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string refresh = Between(gui, "private void optimumUpdateUpscalerRows()", "\n\t}\n");

        Assert.Contains(
            "quality.Enabled = Vintagestory.API.Config.OptimumConfig.EffectiveUpscalerIsDlss;",
            refresh);
        // And the selection follows the effective upscaler, not the setting.
        Assert.Contains(
            "upscaler.SetSelectedValue(Vintagestory.API.Config.OptimumConfig.EffectiveUpscaler);",
            refresh);
    }

    /// <summary>
    /// While an upscaler owns the temporal resolve the TAA switch does nothing,
    /// and the page says so instead of implying two resolves.
    /// </summary>
    [Fact]
    public void TheTaaRowSaysWhenAnUpscalerOwnsTheResolve()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        Assert.Contains("if (Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa)", gui);
        Assert.Contains("Lang.Get(\"optimum-taa-upscaler-owns-resolve\")", gui);
        Assert.Contains(".AddHoverText(taaTooltip,", gui);
    }

    // ---- (c) live apply ----------------------------------------------------

    [Fact]
    public void TheUpscalerHandlerPersistsReplansReloadsAndDropsTheHistory()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumUpscalerChanged(", "\n\t}\n");

        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Upscaler = code;", handler);
        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Save();", handler);
        // Through the virtual, never into the renderer: the settings page must not
        // know that Optimum.Render.Vulkan exists.
        Assert.Contains("ScreenManager.Platform.ApplyOptimumUpscalerSettings();", handler);
        Assert.DoesNotContain("Optimum.Render", handler);
        // The motion writers are compiled against "does anything temporal want this
        // frame jittered", which changes when the upscaler is the only consumer.
        Assert.Contains("handler.ReloadShaders();", handler);
        Assert.Contains("OptimumTemporal.RequestReset(EnumTemporalResetReason.Toggle);", handler);
    }

    [Fact]
    public void ThePresetHandlerReplansWithoutReloadingShaders()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumUpscalerQualityChanged(", "\n\t}\n");

        Assert.Contains("Vintagestory.API.Config.OptimumConfig.UpscalerQuality = code;", handler);
        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Save();", handler);
        Assert.Contains("ScreenManager.Platform.ApplyOptimumUpscalerSettings();", handler);
        Assert.Contains("OptimumTemporal.RequestReset(EnumTemporalResetReason.RenderScale);", handler);
        // Only the render size changes: every define, motion writer and uniform is
        // the same on both sides of a preset change.
        Assert.DoesNotContain("ReloadShaders", handler);
    }

    [Fact]
    public void TheLatencyHandlerOnlyReAppliesTheMode()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumLatencyChanged(", "\n\t}\n");

        Assert.Contains("Vintagestory.API.Config.OptimumConfig.LatencyMode = code;", handler);
        Assert.Contains("Vintagestory.API.Config.OptimumConfig.Save();", handler);
        Assert.Contains("ScreenManager.Platform.ApplyOptimumLatencySettings();", handler);
        // No target changes size and nothing temporal is invalidated.
        Assert.DoesNotContain("RebuildFrameBuffers", handler);
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("RequestReset", handler);
    }

    /// <summary>
    /// The seam itself: three virtuals on the abstract platform with neutral
    /// bodies. The rebuild is the whole of what the OpenGL path needs - it
    /// re-plans the render size and reallocates every target, which is what
    /// restores the pre-upscaler chain when the slot goes back to off - and the
    /// OpenGL path has no latency backend at all.
    /// </summary>
    [Fact]
    public void TheAbstractPlatformDeclaresTheThreeNeutralVirtuals()
    {
        string platform = ReadPatchedOrSource(AbstractPatch, AbstractSource);

        Assert.Contains("public virtual string OptimumUpscalerUnavailable()", platform);
        Assert.Contains("return Vintagestory.API.Config.Lang.Get(\"optimum-upscaler-unavailable-renderer\");", platform);

        string apply = Between(platform, "public virtual void ApplyOptimumUpscalerSettings()", "\n\t}");
        Assert.Contains("RebuildFrameBuffers();", apply);

        string latency = Between(platform, "public virtual void ApplyOptimumLatencySettings()", "\n\t}");
        Assert.DoesNotContain("RebuildFrameBuffers", latency);
    }

    // ---- (d) transplant and self-check -------------------------------------

    [Fact]
    public void EveryNewMemberIsListedForTheCecilTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
        {
            // The injected virtuals on ClientPlatformAbstract.
            "\"OptimumUpscalerUnavailable\"",
            "\"ApplyOptimumUpscalerSettings\"",
            "\"ApplyOptimumLatencySettings\"",
            // The settings page's new handlers and its row refresh.
            "\"onOptimumUpscalerChanged\"",
            "\"onOptimumUpscalerQualityChanged\"",
            "\"onOptimumLatencyChanged\"",
            "\"optimumUpdateUpscalerRows\"",
            // The page method that builds the rows was already a target; it has to
            // stay one, or the new rows never reach the shipped client.
            "\"OnOptimumOptions\"",
            // PR #3 follow-up: the upscaling tab, its bounds, its readout and the
            // per-frame refresh that feeds it.
            "\"uButtonBounds\"",
            "\"OnOptimumUpscalingOptions\"",
            "\"optimumUpscalePlanText\"",
            "\"optimumUpdateUpscalePlanReadout\"",
            "\"OptimumUpscalerPlan\"",
        })
        {
            Assert.Contains(member, patcher);
        }

        // Refresh is vanilla, so member injection would skip it ("MEMBER EXISTS")
        // and the readout would never get its frame: its body is transplanted
        // through the method-target list instead.
        Assert.Contains(
            "new(\"Vintagestory.Client.NoObf.GuiCompositeSettings\", \"Refresh\", 0)",
            patcher);
    }

    [Fact]
    public void TheRendererSelfCheckExpectsTheThreeVirtuals()
    {
        string platform = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        string expected = Between(platform, "ExpectedVirtuals =", "};");

        Assert.Contains("new(true, \"OptimumUpscalerUnavailable\", Array.Empty<string>())", expected);
        Assert.Contains("new(true, \"ApplyOptimumUpscalerSettings\", Array.Empty<string>())", expected);
        Assert.Contains("new(true, \"ApplyOptimumLatencySettings\", Array.Empty<string>())", expected);
    }

    /// <summary>
    /// The Vulkan side of the same three: the reason comes from the host that
    /// produced it, a slot or preset change retires the live feature before the
    /// rebuild (NGX sizes its buffers at creation, so a plan change is a new
    /// feature), and the frame stops upscaling the moment the setting says off.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformOverridesAllThree()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");

        Assert.Contains("public override string OptimumUpscalerUnavailable()", upscale);
        Assert.Contains("return upscaler.Active ? null : upscaler.Unavailable;", upscale);

        string apply = Between(upscale, "public override void ApplyOptimumUpscalerSettings()", "\n    }");
        Assert.Contains("upscaler.RetireFeature();", apply);
        Assert.Contains("base.ApplyOptimumUpscalerSettings();", apply);

        Assert.Contains("public override void ApplyOptimumLatencySettings()", upscale);
        Assert.Contains("device?.ReapplyLatencySettings();", upscale);

        // The setting, not just the host's liveness, decides whether the frame
        // upscales: the tab can turn it off while the host is still up.
        Assert.Contains(
            "UpscalerActive && OptimumConfig.UpscalerReplacesTaa && MotionAttachmentIndex >= 0",
            upscale);
    }

    // ---- (e) language ------------------------------------------------------

    [Fact]
    public void EveryNewLabelAndTooltipHasAnEnglishString()
    {
        string lang = Read("sources/lang/en.json");
        foreach (string key in new[]
        {
            "optimum-upscaler", "optimum-upscaler-tooltip",
            "optimum-upscaler-off", "optimum-upscaler-dlss", "optimum-upscaler-dlss-unavailable",
            "optimum-upscaler-unavailable", "optimum-upscaler-unavailable-renderer",
            "optimum-upscalerquality", "optimum-upscalerquality-tooltip",
            "optimum-upscalerquality-dlaa", "optimum-upscalerquality-quality",
            "optimum-upscalerquality-balanced", "optimum-upscalerquality-performance",
            "optimum-upscalerquality-ultraperformance",
            "optimum-latency", "optimum-latency-tooltip",
            "optimum-latency-off", "optimum-latency-on", "optimum-latency-boost",
            "optimum-taa-upscaler-owns-resolve",
            // PR #3 follow-up: the tab and its plan readout.
            "optimum-upscaling-tab-header",
            "optimum-upscaleplan", "optimum-upscaleplan-tooltip", "optimum-upscaleplan-none",
        })
        {
            Assert.Contains("\"" + key + "\":", lang);
        }
    }

    /// <summary>
    /// The other direction, so a row cannot reference a key nobody wrote: every
    /// <c>optimum-upscaler*</c> and <c>optimum-latency*</c> key the settings page
    /// asks for really exists in en.json.
    /// </summary>
    [Fact]
    public void ThePageNeverAsksForAKeyEnJsonDoesNotHave()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string lang = Read("sources/lang/en.json");

        var asked = new List<string>();
        int at = 0;
        while (true)
        {
            int from = gui.IndexOf("Lang.Get(\"", at, StringComparison.Ordinal);
            if (from < 0) break;
            from += "Lang.Get(\"".Length;
            int to = gui.IndexOf('"', from);
            if (to < 0) break;
            at = to;
            string key = gui[from..to];
            // "optimum-upscal" covers optimum-upscaler*, optimum-upscaling-tab-header
            // and the optimum-upscaleplan* readout keys in one prefix.
            if (key.StartsWith("optimum-upscal", StringComparison.Ordinal) ||
                key.StartsWith("optimum-latency", StringComparison.Ordinal) ||
                key.StartsWith("optimum-taa-upscaler", StringComparison.Ordinal))
            {
                asked.Add(key);
            }
        }

        Assert.NotEmpty(asked);
        foreach (string key in asked)
        {
            Assert.Contains("\"" + key + "\":", lang);
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while (true)
        {
            int found = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0) return count;
            count++;
            at = found + needle.Length;
        }
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
    }
}
