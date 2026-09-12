using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The passthrough upscaler: the second entry in the upscaler slot, which plans
/// exactly like DLSS for the chosen preset and whose evaluate is a magnifying blit
/// from the render-resolution scene colour to the display-resolution target.
///
/// It is two things at once, and both are asserted here. As a diagnostic it
/// separates our own rendering from the vendor's reconstruction, because it takes
/// the identical path and differs only in the evaluate. As a feature it is the
/// upscaler for a GPU with no vendor path, because it touches no vendor runtime at
/// all - which is why its availability is answered per dropdown entry rather than
/// by the slot-wide "can DLSS run here".
///
/// The behaviour on the GPU - a display-resolution image, the render size following
/// the preset, no NGX call, and dlss -> passthrough -> off leaking no feature - is
/// <c>PassthroughUpscalerTests</c> in the Vulkan suite. What is asserted here is the
/// wiring that GPU test cannot see: the setting, the tab entry, the patcher's
/// manifest and the renderer's self-check.
/// </summary>
public class PassthroughUpscalerCoverageTests
{
    private const string GuiPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch";
    private const string GuiSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs";
    private const string AbstractPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch";
    private const string AbstractSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";

    // ---- (a) the setting ---------------------------------------------------

    /// <summary>
    /// The slot, its filter and the jitter switch, declared and persisted in both
    /// copies of the config with the usual defensive load: an unrecognised value
    /// degrades rather than failing the parse.
    /// </summary>
    [Fact]
    public void TheSlotTheFilterAndTheJitterSwitchAreDeclaredAndPersisted()
    {
        foreach (string config in Configs())
        {
            // The slot the parser and the dropdown both read.
            Assert.Contains(
                "public static readonly string[] UpscalerNames = { \"off\", \"dlss\", \"passthrough\" };",
                config);
            Assert.Contains(
                "string.Equals(requestedUpscaler, \"passthrough\", StringComparison.OrdinalIgnoreCase) ? \"passthrough\" :",
                config);
            Assert.Contains("public static bool EffectiveUpscalerIsPassthrough =>", config);
            Assert.Contains(
                "string.Equals(EffectiveUpscaler, \"passthrough\", StringComparison.OrdinalIgnoreCase);",
                config);

            // The filter: persisted, defensively parsed, and read as one question.
            Assert.Contains("public static string UpscalerPassthroughFilter = \"linear\";", config);
            Assert.Contains("public string UpscalerPassthroughFilter { get; set; } = \"linear\";", config);
            Assert.Contains(
                "(nameof(OptimumConfigData.UpscalerPassthroughFilter), UpscalerPassthroughFilter),", config);
            Assert.Contains("UpscalerPassthroughFilter = UpscalerPassthroughFilter,", config);
            Assert.Contains(
                "string requestedPassthroughFilter = data.UpscalerPassthroughFilter?.Trim() ?? \"\";", config);
            Assert.Contains("public static bool UpscalerPassthroughIsNearest =>", config);

            // The jitter switch, on by default: the frame is only ever unjittered
            // because someone asked for it.
            Assert.Contains("public static bool UpscalerJitter = true;", config);
            Assert.Contains("public bool UpscalerJitter { get; set; } = true;", config);
        }
    }

    /// <summary>
    /// The two rules that make the passthrough slot behave like the DLSS one rather
    /// than like a half-enabled setting: it owns the temporal resolve (so the
    /// in-house TAA resolve, the sharpen pass and the FSR blit stand down), and the
    /// jitter window is the upscaler's to decide once one owns the resolve.
    /// </summary>
    [Fact]
    public void ThePassthroughSlotOwnsTheResolveAndTheJitterWindow()
    {
        foreach (string config in Configs())
        {
            Assert.Contains(
                "public static bool UpscalerReplacesTaa => EffectiveUpscalerIsDlss || EffectiveUpscalerIsPassthrough;",
                config);
            Assert.Contains(
                "public static bool JitterWindowOpen =>\n" +
                "        TaaJitterDev || (UpscalerReplacesTaa ? UpscalerJitter : EffectiveTaa);",
                config);
            // The temporal inputs stay on either way: the motion writers still run,
            // they are just writing for an unjittered frame.
            Assert.Contains(
                "public static bool EffectiveTemporalPipeline => EffectiveTaa || UpscalerReplacesTaa;", config);
        }
    }

    /// <summary>
    /// The size-for-size claim, in the only place it can be made without a driver:
    /// the nominal ratios are the SDK's own, not round numbers. Balanced is
    /// 1/1.724 because that is what NGX's optimal-settings query answers
    /// (2560x1490 -> 1485x864); 1/1.7 answered 1506x876 and the comparison would
    /// have been between two different render sizes.
    /// <c>PassthroughUpscalerTests.PassthroughPlanMatchesTheVendorPlanSizeForSize</c>
    /// holds the same claim against the real driver.
    /// </summary>
    [Fact]
    public void TheNominalRatiosAreTheVendorsOwn()
    {
        foreach (string config in Configs())
        {
            Assert.Contains(
                "{ 1.0f, 1.0f / 1.5f, 1.0f / 1.724f, 0.5f, 1.0f / 3.0f };", config);
            Assert.Contains("public static float NominalUpscalerRenderScale(string quality)", config);
        }

        string passthrough = Read("Optimum.Render.Vulkan/Upscale/PassthroughUpscaler.cs");
        Assert.Contains("float scale = OptimumConfig.NominalUpscalerRenderScale(preset);", passthrough);
        // Rounded the way the SDK's query rounds, and never above the display size.
        Assert.Contains("private static int Round(double value) => (int)Math.Floor(value + 0.5);", passthrough);
        Assert.Contains("if (renderWidth > displayWidth) renderWidth = displayWidth;", passthrough);

        // And nothing in it reaches a vendor runtime.
        Assert.DoesNotContain("Ngx", passthrough.Replace("no NGX", "").Replace("NGX's", "")
            .Replace("NGX,", "").Replace("NGX at all", "").Replace("NgxPerfQuality", ""));
    }

    // ---- (b) the tab entry -------------------------------------------------

    /// <summary>
    /// The dropdown offers it, the filter row is beside it, and both carry a label
    /// and a hover text like every other row on the page.
    /// </summary>
    [Fact]
    public void TheTabOffersThePassthroughSlotAndItsFilter()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        Assert.Contains("AddDropDown(new string[] { \"off\", \"dlss\", \"passthrough\" }", gui);
        Assert.Contains("Lang.Get(\"optimum-upscaler-passthrough\")", gui);

        Assert.Contains("Lang.Get(\"optimum-upscalerpassthroughfilter\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscalerpassthroughfilter-tooltip\")", gui);
        Assert.Contains("AddDropDown(new string[] { \"linear\", \"nearest\" }", gui);
        Assert.Contains("onOptimumUpscalerPassthroughFilterChanged", gui);
        Assert.Contains("\"optUpscalerPassthroughFilter\")", gui);

        Assert.Contains("Lang.Get(\"optimum-upscalerjitter\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscalerjitter-tooltip\")", gui);
        Assert.Contains("onOptimumUpscalerJitterChanged", gui);
        Assert.Contains("\"optUpscalerJitter\")", gui);
    }

    /// <summary>
    /// The two handlers do exactly as much as their change costs. The filter is a
    /// plain setting write - no target changes size, nothing temporal is invalidated,
    /// and the next frame's blit reads it. The jitter switch changes the projection
    /// every pass is rendered with, so the history accumulated on the other side of
    /// it is dropped; still no rebuild and no shader reload, because
    /// <c>EffectiveTemporalPipeline</c> has not moved.
    /// </summary>
    [Fact]
    public void TheFilterIsAPlainWriteAndTheJitterSwitchDropsTheHistory()
    {
        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);

        string filter = Between(gui, "private void onOptimumUpscalerPassthroughFilterChanged(", "\n\t}\n");
        Assert.Contains("OptimumConfig.UpscalerPassthroughFilter = code;", filter);
        Assert.Contains("OptimumConfig.Save();", filter);
        Assert.DoesNotContain("ApplyOptimumUpscalerSettings", filter);
        Assert.DoesNotContain("ReloadShaders", filter);
        Assert.DoesNotContain("OptimumTemporal.RequestReset", filter);

        string jitter = Between(gui, "private void onOptimumUpscalerJitterChanged(", "\n\t}\n");
        Assert.Contains(
            "OptimumConfig.UpscalerJitter = string.Equals(code, \"on\", StringComparison.OrdinalIgnoreCase);",
            jitter);
        Assert.Contains("OptimumTemporal.RequestReset(EnumTemporalResetReason.Toggle);", jitter);
        Assert.DoesNotContain("ApplyOptimumUpscalerSettings", jitter);
        Assert.DoesNotContain("ReloadShaders", jitter);
    }

    /// <summary>
    /// The rows say what the frame is doing: the filter is live only while the
    /// passthrough upscaler is the one selected, and the jitter switch while any
    /// upscaler owns the resolve - the comparison is run against both of them.
    /// </summary>
    [Fact]
    public void TheRowsAreDeadWhenTheyMeanNothing()
    {
        string refresh = Between(
            ReadPatchedOrSource(GuiPatch, GuiSource), "private void optimumUpdateUpscalerRows()", "\n\t}\n");

        Assert.Contains(
            "passthroughFilter.Enabled = Vintagestory.API.Config.OptimumConfig.EffectiveUpscalerIsPassthrough;",
            refresh);
        Assert.Contains(
            "jitter.Enabled = Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa;",
            refresh);
    }

    /// <summary>
    /// Availability is asked per dropdown entry, not once for the slot. This is the
    /// whole reason the passthrough upscaler is reachable at all on a machine with
    /// no NGX - which is both the machine the comparison is worth running on and
    /// every GPU that has no vendor upscaler.
    /// </summary>
    [Fact]
    public void AvailabilityIsAskedPerEntrySoTheFallbackSurvivesAMissingNgx()
    {
        string platform = ReadPatchedOrSource(AbstractPatch, AbstractSource);
        // The neutral body forwards, which is the right answer for a platform with
        // no upscaler at all - the OpenGL one included.
        Assert.Contains("public virtual string OptimumUpscalerUnavailableFor(string upscaler)", platform);
        Assert.Contains("return OptimumUpscalerUnavailable();", platform);

        string gui = ReadPatchedOrSource(GuiPatch, GuiSource);
        string handler = Between(gui, "private void onOptimumUpscalerChanged(", "\n\t}\n");
        Assert.Contains("ScreenManager.Platform.OptimumUpscalerUnavailableFor(code)", handler);

        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");
        string answer = Between(upscale, "public override string OptimumUpscalerUnavailableFor(string upscaler)", "\n    }");
        Assert.Contains(
            "if (string.Equals(upscaler, \"passthrough\", StringComparison.OrdinalIgnoreCase)) return null;", answer);

        // The patcher carries the new virtual, and the renderer's self-check expects it.
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumUpscalerUnavailableFor\",", patcher);
        Assert.Contains("\"onOptimumUpscalerPassthroughFilterChanged\",", patcher);
        Assert.Contains("\"onOptimumUpscalerJitterChanged\",", patcher);
        Assert.Contains(
            "new(true, \"OptimumUpscalerUnavailableFor\", new[] { \"String\" }),",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs"));
    }

    // ---- (c) the frame -----------------------------------------------------

    /// <summary>
    /// The placement: the passthrough slot is planned before any host is asked
    /// anything, takes the whole of the DLSS frame apart from the reconstruction,
    /// and publishes the plan the targets were really allocated for so the LOD bias
    /// and the jitter sequence length follow it exactly as they follow a DLSS plan.
    /// </summary>
    [Fact]
    public void ThePassthroughFrameSharesEverythingButTheReconstruction()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");

        // Planned first, and from the setting alone.
        string planning = Between(upscale, "public override bool OptimumTryPlanUpscaleRenderSize(", "\n    }");
        int passthrough = planning.IndexOf("PassthroughUpscaler.TryPlanForFrame(", StringComparison.Ordinal);
        int dlss = planning.IndexOf("DlssUpscaler.TryPlanForFrame(", StringComparison.Ordinal);
        Assert.True(passthrough > 0 && dlss > passthrough,
            "the passthrough plan must be answered before any host is asked");

        // The evaluate: one blit, the filter from the setting, and the plan published.
        string blit = Between(
            upscale, "private bool RenderPassthroughUpscale(FrameBufferRef primary, FrameBufferRef target)", "\n    }");
        Assert.Contains("PassthroughUpscaler.Publish(plan);", blit);
        Assert.Contains("ShaderRegistry.ApplyOptimumLodBias();", blit);
        Assert.Contains("device.BlitColorScaled(", blit);
        Assert.Contains("!OptimumConfig.UpscalerPassthroughIsNearest", blit);
        Assert.Contains("DisableOptimumUpscaler(", blit);
        // And it asks no host anything: there is no vendor runtime on this path.
        Assert.DoesNotContain("upscaler.", blit);

        // And the rest of the frame - the overlays' depth upscale above all - is the
        // shared body, so only the reconstruction differs between the two.
        string frame = Between(upscale, "public override bool RenderOptimumUpscale()", "\n    }");
        Assert.Contains("if (PassthroughUpscaler.Requested)", frame);
        Assert.Contains("else if (!RenderDlssUpscale(primary, target))", frame);
        Assert.Contains("device.UpscaleDepthNearest(primary.DepthTextureId, target.DepthTextureId)", frame);

        // The device's whole cost: one blit, no pipeline and no shader of ours, so
        // the comparison cannot accidentally measure our own blit shader.
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.Passthrough.cs");
        Assert.Contains("internal bool BlitColorScaled(int sourceTexture, int destinationTexture, bool linear)", device);
        Assert.Contains("CmdBlitImage", device);
        Assert.Contains("filterLinear ? Filter.Linear : Filter.Nearest", device);
        // A linear blit needs a format feature the specification does not guarantee,
        // so it is asked for and nearest is the fallback rather than a refusal.
        Assert.Contains("SampledImageFilterLinearBit", device);
    }

    /// <summary>
    /// The lifecycle across a slot change, which is where a vendor feature leaks if
    /// anywhere: the live feature is retired before the rebuild, the passthrough
    /// plan behind the tab's readout is retired by hand because it has no feature to
    /// retire, and teardown clears the published plan whichever upscaler made it.
    /// </summary>
    [Fact]
    public void SwitchingSlotsRetiresBothKindsOfPlan()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");

        string apply = Between(upscale, "public override void ApplyOptimumUpscalerSettings()", "\n    }");
        int retire = apply.IndexOf("upscaler.RetireFeature();", StringComparison.Ordinal);
        int cleared = apply.IndexOf("passthroughPlan = default;", StringComparison.Ordinal);
        int rebuild = apply.IndexOf("base.ApplyOptimumUpscalerSettings();", StringComparison.Ordinal);
        Assert.True(retire >= 0 && cleared > retire && rebuild > cleared,
            "both plans must be retired before the rebuild re-plans the render size");
        Assert.Contains("OptimumConfig.ClearUpscalerPlan();", apply);

        // Teardown: a passthrough session has no host to shut down but does publish a
        // plan, so "no host" may not return before the plan is retired.
        string shutdown = Between(upscale, "internal void ShutDownUpscaler()", "\n    }");
        Assert.Contains("bool published = upscaler != null || passthroughPlan.IsValid;", shutdown);
        Assert.Contains("passthroughPlan = default;", shutdown);
        int published = shutdown.IndexOf("if (!published) return;", StringComparison.Ordinal);
        int clear = shutdown.IndexOf("OptimumConfig.ClearUpscalerPlan();", StringComparison.Ordinal);
        Assert.True(published > 0 && clear > published);

        // And the DLSS host plans only for its own slot: with "passthrough" selected
        // it must answer no, or a vendor feature would be created for a frame that is
        // never going to evaluate it.
        string dlss = Read("Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs");
        string gate = Between(dlss, "public static bool TryPlanForFrame(", "\n    }");
        Assert.Contains("if (!Requested) return false;", gate);
        Assert.DoesNotContain("if (!OptimumConfig.UpscalerReplacesTaa) return false;", gate);
    }

    // ---- (d) language ------------------------------------------------------

    [Fact]
    public void EveryPassthroughLabelAndTooltipHasAnEnglishString()
    {
        string lang = Read("sources/lang/en.json");
        foreach (string key in new[]
        {
            "optimum-upscaler-passthrough",
            "optimum-upscalerpassthroughfilter", "optimum-upscalerpassthroughfilter-tooltip",
            "optimum-upscalerpassthroughfilter-linear", "optimum-upscalerpassthroughfilter-nearest",
            "optimum-upscalerjitter", "optimum-upscalerjitter-tooltip",
            "optimum-upscalerjitter-on", "optimum-upscalerjitter-off",
        })
        {
            Assert.Contains("\"" + key + "\":", lang);
        }

        // The slot is the fallback upscaler as well as the diagnostic, so its entry
        // is not labelled as a debug switch. The jitter switch still is.
        Assert.DoesNotContain("\"optimum-upscaler-passthrough\": \"Debug:", lang);
        Assert.DoesNotContain("\"optimum-upscalerpassthroughfilter\": \"Debug:", lang);
        Assert.Contains("\"optimum-upscalerjitter\": \"Debug:", lang);
    }

    // ---- helpers -----------------------------------------------------------

    private static string[] Configs() => new[]
    {
        Read("VintagestoryApi/Config/OptimumConfig.cs"),
        Read("sources/VintagestoryApi/Config/OptimumConfig.cs"),
    };

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "not found: " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, "not found after " + start + ": " + end);
        return text[from..to];
    }

    /// <summary>
    /// The patched file as it will be, read from the generated patch where one
    /// exists and from the working tree otherwise.
    ///
    /// A patch is flattened back into the file it produces first - context and
    /// added lines with their diff column removed, removed lines and hunk headers
    /// dropped - so a body can be sliced out of it with the same markers the source
    /// file uses. Reading the raw diff instead makes every assertion depend on
    /// whether a line happened to land in a hunk as context or as an addition,
    /// which is not something this test has an opinion about.
    /// </summary>
    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string patch = PatchReader.FindRepositoryFile(patchPath);
        return File.Exists(patch) ? Flatten(File.ReadAllText(patch)) : Read(sourcePath);
    }

    private static string Flatten(string patch)
    {
        var builder = new System.Text.StringBuilder(patch.Length);
        foreach (string line in patch.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal) ||
                line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("--- ", StringComparison.Ordinal) ||
                line.StartsWith("+++ ", StringComparison.Ordinal) ||
                line.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }
            builder.Append(line.Length > 0 ? line[1..] : line).Append('\n');
        }
        return builder.ToString();
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
